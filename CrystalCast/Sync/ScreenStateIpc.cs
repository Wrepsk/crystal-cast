using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace CrystalCast.Sync;

/// <summary>
/// Provides CrystalCast's IPC: registers the channels in <see cref="IpcChannels"/> while IPC is switched on,
/// hands each call to the service that answers it, and announces screen changes.
/// </summary>
public sealed class ScreenStateIpc : IDisposable
{
    public const int ApiVersion = 11;

    /// <summary>Largest JSON argument any call accepts, in UTF-8 bytes.</summary>
    internal const int MaxPayloadBytes = 64 * 1024;

    /// <summary>How often the player's own state is checked for <c>CrystalCast.Screen.Changed</c>.</summary>
    internal const int PlaybackWatchIntervalMs = 250;

    private readonly Configuration configuration;
    private readonly CrystalCastServices services;
    private readonly string ownerSessionId;
    private readonly ScreenStateBuilder stateBuilder;
    private readonly ScreenChangePublisher changePublisher;
    private readonly ScreenIpcMutationService mutationService;
    private readonly ScreenPlaylistIpcService playlistService;
    private readonly ICallGateProvider<object> readyProvider;
    private readonly ICallGateProvider<object> disposedProvider;
    private readonly List<IpcFunction> functions = [];
    private readonly WorldScreenManager renderer;
    private readonly RemoteScreenStateStore remoteScreens = new();
    private readonly IpcMainThreadDispatcher dispatcher;
    private readonly ScreenPlaybackWatcher playbackWatcher = new();
    private readonly HiddenIpcScreens hiddenScreens = new();
    private long lastPlaybackWatchTick;
    private bool registered;

    internal ScreenStateIpc(
        Configuration configuration,
        WorldScreenManager renderer,
        CrystalCastServices services,
        ScreenPlaylistAdvancer playlistAdvancer)
    {
        this.configuration = configuration;
        this.services = services;
        this.renderer = renderer;
        ownerSessionId = Guid.NewGuid().ToString("N");
        stateBuilder = new ScreenStateBuilder(
            configuration,
            renderer,
            ownerSessionId,
            services.ClientState,
            services.PlacementResolver);

        var pluginInterface = services.PluginInterface;
        changePublisher = new ScreenChangePublisher(
            configuration,
            pluginInterface.GetIpcProvider<string, object>(IpcChannels.ScreenChanged),
            ownerSessionId);
        mutationService = new ScreenIpcMutationService(configuration, renderer, stateBuilder, changePublisher, PublishLocalState, services.Log, hiddenScreens);
        playlistService = new ScreenPlaylistIpcService(configuration, playlistAdvancer, PublishPlaylistAdvance, PublishPlaylistChange, services.Log);
        readyProvider = pluginInterface.GetIpcProvider<object>(IpcChannels.Ready);
        disposedProvider = pluginInterface.GetIpcProvider<object>(IpcChannels.Disposed);
        dispatcher = new IpcMainThreadDispatcher(services.Framework);

        // Every handler runs on the game's main thread; see IpcMainThreadDispatcher.
        AddFunction(IpcChannels.ApiVersion, () => ApiVersion);
        AddFunction(IpcChannels.GetCapabilities, () => dispatcher.Invoke(GetCapabilitiesJson, MutationError));
        AddFunction(IpcChannels.GetSnapshot, () => dispatcher.Invoke(GetSnapshotJson, MutationError));
        AddFunction(IpcChannels.ApplyState, json => dispatcher.Invoke(() => ApplyStateJson(json), ApplyStateError));
        AddFunction(IpcChannels.CreateScreen, json => Mutate(json, mutationService.CreateScreenJson));
        AddFunction(IpcChannels.UpdateScreen, json => Mutate(json, mutationService.UpdateScreenJson));
        AddFunction(IpcChannels.UpdateSource, json => Mutate(json, mutationService.UpdateSourceJson));
        AddFunction(IpcChannels.SetSourceLock, json => Mutate(json, mutationService.SetSourceLockJson));
        AddFunction(IpcChannels.GetSourceState, screenId => dispatcher.Invoke(() => GetSourceStateJson(screenId), SourceStateError));
        AddFunction(IpcChannels.RemoveScreen, screenId => dispatcher.Invoke(() => RemoveJson(screenId), MutationError));
        AddFunction(IpcChannels.PlaylistGet, screenId => dispatcher.Invoke(() => playlistService.GetJson(screenId), PlaylistError));
        AddFunction(IpcChannels.PlaylistAdd, json => MutatePlaylist(json, playlistService.AddJson));
        AddFunction(IpcChannels.PlaylistRemove, json => MutatePlaylist(json, playlistService.RemoveJson));
        AddFunction(IpcChannels.PlaylistClear, json => MutatePlaylist(json, playlistService.ClearJson));
        AddFunction(IpcChannels.PlaylistSkip, json => MutatePlaylist(json, playlistService.SkipJson));
        AddFunction(IpcChannels.PlaylistMove, json => MutatePlaylist(json, playlistService.MoveJson));
        AddFunction(IpcChannels.PlaylistSetOptions, json => MutatePlaylist(json, playlistService.SetOptionsJson));

        UpdateRegistration();
        services.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>How many IPC screens the user deleted and keeps hidden this session.</summary>
    public int HiddenScreenCount => hiddenScreens.Count;

    /// <summary>Lets plugins create the hidden screens again; they come back on their next create or update.</summary>
    public void ShowHiddenScreens() => hiddenScreens.Clear();

    /// <summary>Tells other plugins the user deleted one of their screens, and whether to keep it hidden.</summary>
    public void OnUserDeletedScreen(BrowserScreenProfile screen, bool hide) => mutationService.RemovedByUser(screen, hide);

    public void SetEnabled(bool enabled)
    {
        if (configuration.IpcEnabled == enabled)
            return;

        configuration.IpcEnabled = enabled;
        if (!enabled)
        {
            // Every screen announced so far becomes unavailable.
            changePublisher.SendUnavailableEventsForMissingLocalScreens([]);
            RemoveIpcCreatedScreens();
            remoteScreens.Clear();
            changePublisher.Clear();
        }

        UpdateRegistration();
        configuration.Save();
    }

    /// <summary>Announces the user's or the plugin's own changes to every screen that differs from what was last sent.</summary>
    public void PublishLocalState()
    {
        PublishLocalState(null, null);
    }

    /// <summary>
    /// A screen moved to another playlist video, by ending, Skip or Play now. Reloads the player when the link
    /// is the same (the browser only reloads on a new link) and announces the change.
    /// </summary>
    internal void PublishPlaylistAdvance(PlaylistAdvance advance)
    {
        if (advance.SameSource && configuration.FindBrowserScreen(advance.ScreenId) is { } screen)
            renderer.TryRestartDynamicSource(screen);

        PublishLocalState(advance.ScreenId, [ScreenIpcChangeKind.Source, ScreenIpcChangeKind.Playlist]);
    }

    internal void PublishPlaylistChange(string screenId)
    {
        PublishLocalState(screenId, [ScreenIpcChangeKind.Playlist]);
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        Unregister();
        dispatcher.Dispose();
        remoteScreens.Clear();
        changePublisher.Clear();
    }

    private void AddFunction<TResult>(string channel, Func<TResult> handler)
    {
        var provider = services.PluginInterface.GetIpcProvider<TResult>(channel);
        functions.Add(new IpcFunction(() => provider.RegisterFunc(handler), provider.UnregisterFunc));
    }

    private void AddFunction(string channel, Func<string, string> handler)
    {
        var provider = services.PluginInterface.GetIpcProvider<string, string>(channel);
        functions.Add(new IpcFunction(() => provider.RegisterFunc(handler), provider.UnregisterFunc));
    }

    private void UpdateRegistration()
    {
        if (configuration.IpcEnabled)
            Register();
        else
            Unregister();
    }

    private void Register()
    {
        if (registered)
            return;

        foreach (var function in functions)
            function.Register();

        registered = true;
        readyProvider.SendMessage();
    }

    private void Unregister()
    {
        if (!registered)
            return;

        try
        {
            disposedProvider.SendMessage();
        }
        catch (Exception ex)
        {
            services.Log.Warning(ex, "A CrystalCast.Disposed subscriber failed.");
        }

        foreach (var function in functions)
            function.Unregister();

        registered = false;
        playbackWatcher.Clear();
    }

    /// <summary>Size check, then the handler on the main thread.</summary>
    private string Mutate(string json, Func<string, string> handler)
    {
        if (!IpcPayloadLimit.TryCheck(json, out var error))
            return MutationError(error);

        return dispatcher.Invoke(() => handler(json), MutationError);
    }

    private string MutatePlaylist(string json, Func<string, string> handler)
    {
        if (!IpcPayloadLimit.TryCheck(json, out var error))
            return PlaylistError(error);

        return dispatcher.Invoke(() => handler(json), PlaylistError);
    }

    private string GetSourceStateJson(string screenId)
    {
        if (!IpcPayloadLimit.TryCheckScreenId(screenId, out var error))
            return SourceStateError(error);

        return mutationService.GetSourceStateJson(screenId);
    }

    private static string PlaylistError(string error) =>
        IpcJsonService.Serialize(new ScreenIpcPlaylistResponse { Success = false, Error = error });

    private static string MutationError(string error) => IpcJsonService.SerializeMutationError(error);

    private static string SourceStateError(string error) =>
        IpcJsonService.Serialize(new ScreenIpcSourceStateResponse { Success = false, Error = error });

    private static string ApplyStateError(string error) =>
        IpcJsonService.Serialize(new ScreenIpcApplyStateResponse
        {
            Success = false,
            Result = ScreenIpcApplyStateResult.RejectedInvalid,
            Error = error,
        });

    /// <summary>
    /// Sends <c>CrystalCast.Screen.Changed</c> for changes made inside the player itself, which no settings
    /// save or IPC call announces: pausing in browser controls, a video ending, the next playlist item, a
    /// title loading, or a seek.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (!registered || now - lastPlaybackWatchTick < PlaybackWatchIntervalMs)
            return;

        lastPlaybackWatchTick = now;
        try
        {
            WatchPlayback();
        }
        catch (Exception ex)
        {
            services.Log.Debug(ex, "Checking CrystalCast screens for player changes failed.");
        }
    }

    private void WatchPlayback()
    {
        if (!configuration.Enabled)
        {
            playbackWatcher.Clear();
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in ScreenLimitPolicy.GetAllowedScreens(configuration.BrowserScreens).Where(screen => screen.Enabled))
        {
            if (!stateBuilder.TryBuildBrowserScreenState(screen, out var state))
                continue;

            seen.Add(screen.ScreenId);
            var changes = playbackWatcher.Observe(state, nowUnixMs);
            if (changes.Count == 0)
                continue;

            screen.LocalSequence++;
            state.Sequence = screen.LocalSequence;
            changePublisher.SendObservedChanges(state, changes);
        }

        playbackWatcher.Retain(seen);
    }

    private string GetCapabilitiesJson()
    {
        var webView2Available = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var webView2Version, out var webView2Error);
        var engine = configuration.YouTubeBrowserEngine;
        var capabilities = new ScreenIpcCapabilities
        {
            ApiVersion = ApiVersion,
            PluginVersion = typeof(ScreenStateIpc).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Enabled = configuration.Enabled,
            Providers = BrowserSourceIpcAdapters.All.Select(adapter => adapter.ProviderKind.ToString()).ToArray(),
            Features = ["createIfMissing", "removalReason"],
            Limits = new ScreenIpcLimits
            {
                MaxUserScreens = Configuration.MaxBrowserScreens,
                MaxIpcScreens = Configuration.MaxIpcBrowserScreens,
                MaxScreens = Configuration.MaxRenderableBrowserScreens,
                MaxActiveScreens = Configuration.MaxActiveBrowserScreens,
                MaxRemoteScreens = RemoteScreenStateStore.MaxRemoteScreens,
                MaxPayloadBytes = MaxPayloadBytes,
                IpcScreensInUse = ScreenLimitPolicy.CountIpcScreens(configuration.BrowserScreens),
                MaxPlaylistEntries = ScreenPlaylistPolicy.MaxEntries,
            },
            Browser = new ScreenIpcBrowserCapabilities
            {
                IsWine = WineEnvironment.IsWine,
                Backend = engine.ToString(),
                CaptureMode = BrowserPlatformPolicy.ResolveCaptureMode(engine, WineEnvironment.IsWine).ToString(),
                NativeLinuxBrowserForProviders = BrowserPlatformPolicy.UsesWebKitGtk(engine, WineEnvironment.IsWine, BrowserSourceProviderKind.YouTube),
                WebView2Available = webView2Available,
                WebView2Version = webView2Available ? webView2Version : string.Empty,
                WebView2Error = webView2Available ? string.Empty : webView2Error,
            },
        };
        return IpcJsonService.Serialize(capabilities);
    }

    private void RemoveIpcCreatedScreens()
    {
        configuration.Normalize();
        var removed = configuration.BrowserScreens.RemoveAll(screen => screen.CreatedByIpc);
        if (removed <= 0)
            return;

        configuration.Normalize();
    }

    private void PublishLocalState(string? changedScreenId, IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        configuration.Normalize();
        UpdateRegistration();
        if (!configuration.IpcEnabled)
        {
            configuration.Save();
            return;
        }

        var states = new List<ScreenStateEnvelope>();
        if (configuration.Enabled)
        {
            var screensToPublish = ScreenLimitPolicy.GetAllowedScreens(configuration.BrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            foreach (var screen in screensToPublish)
            {
                if (!stateBuilder.TryResolveForIpc(screen.Placement, out var resolved))
                    continue;

                screen.LocalSequence++;
                states.Add(stateBuilder.BuildBrowserScreenState(screen, resolved));
            }
        }

        configuration.Save();

        var publishedScreenIds = states.Select(state => state.ScreenId).ToHashSet(StringComparer.Ordinal);
        changePublisher.RememberKnownLocalScreens(publishedScreenIds);

        foreach (var state in states)
            changePublisher.MaybeSendScreenChanged(state, changedScreenId, forcedChanges);

        changePublisher.SendUnavailableEventsForMissingLocalScreens(publishedScreenIds);

        // A forced change still has to go out for a screen that could not be placed just now.
        if (forcedChanges is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(changedScreenId)
            && states.All(state => !string.Equals(state.ScreenId, changedScreenId, StringComparison.Ordinal))
            && configuration.FindBrowserScreen(changedScreenId) is { } forcedScreen
            && forcedScreen.Enabled
            && stateBuilder.TryBuildBrowserScreenState(forcedScreen, out var forcedState))
        {
            changePublisher.MaybeSendScreenChanged(forcedState, changedScreenId, forcedChanges);
        }
    }

    private string GetSnapshotJson()
    {
        var localStates = stateBuilder.BuildLocalStates().ToArray();
        changePublisher.RememberKnownLocalScreens(localStates.Select(state => state.ScreenId));
        return IpcJsonService.Serialize(new ScreenIpcSnapshot
        {
            ApiVersion = ApiVersion,
            LocalScreens = localStates,
            RemoteScreens = remoteScreens.GetSnapshot()
                .OrderBy(screen => screen.OwnerSessionId)
                .ThenBy(screen => screen.ScreenId)
                .ToArray(),
        });
    }

    private string ApplyStateJson(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || !IpcPayloadLimit.TryCheck(json, out _))
                return ApplyStateError($"State payload must be at most {MaxPayloadBytes} UTF-8 bytes.");

            var state = IpcJsonService.Deserialize<ScreenStateEnvelope>(json);
            var result = remoteScreens.Apply(state, ownerSessionId, out var error);
            return IpcJsonService.Serialize(new ScreenIpcApplyStateResponse
            {
                Success = result is ScreenIpcApplyStateResult.Applied
                    or ScreenIpcApplyStateResult.IgnoredSelf
                    or ScreenIpcApplyStateResult.IgnoredDuplicate
                    or ScreenIpcApplyStateResult.IgnoredStale,
                Result = result,
                Error = error,
                ScreenId = state?.ScreenId ?? string.Empty,
                OwnerSessionId = state?.OwnerSessionId ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            services.Log.Warning(ex, "Failed to apply CrystalCast screen state IPC payload.");
            return ApplyStateError("State payload is not valid JSON.");
        }
    }

    /// <summary>
    /// Removes a screen created through IPC, or the remote states stored under this screen ID. The user's own
    /// screens cannot be removed through IPC.
    /// </summary>
    private string RemoveJson(string screenId)
    {
        screenId = IpcJsonService.NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId))
            return MutationError("Screen ID is required.");

        if (!IpcPayloadLimit.TryCheckScreenId(screenId, out var error))
            return MutationError(error);

        if (mutationService.Remove(screenId) || remoteScreens.RemoveByScreenId(screenId))
            return IpcJsonService.Serialize(new ScreenIpcMutationResponse { Success = true, ScreenId = screenId });

        return IpcJsonService.SerializeMutationError(
            configuration.FindBrowserScreen(screenId) != null
                ? $"Screen '{screenId}' was not created through IPC; only the user can delete it."
                : $"Screen '{screenId}' was not found.",
            screenId);
    }

    private readonly record struct IpcFunction(Action Register, Action Unregister);
}
