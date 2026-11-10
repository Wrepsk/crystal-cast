using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text;

namespace CrystalCast.Sync;

public sealed class ScreenStateIpc : IDisposable
{
    public const int ApiVersion = 10;

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
    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<string> snapshotProvider;
    private readonly ICallGateProvider<string, bool> applyStateProvider;
    private readonly ICallGateProvider<string, string> applyStateDetailedProvider;
    private readonly ICallGateProvider<string, bool> removeProvider;
    private readonly ICallGateProvider<string, object> localStateChangedProvider;
    private readonly ICallGateProvider<string, string> createScreenProvider;
    private readonly ICallGateProvider<string, string> updateScreenProvider;
    private readonly ICallGateProvider<string, string> updateSourceProvider;
    private readonly ICallGateProvider<string, string> sourceLockProvider;
    private readonly ICallGateProvider<string, string> sourceStateProvider;
    private readonly ICallGateProvider<string> capabilitiesProvider;
    private readonly ICallGateProvider<object> readyProvider;
    private readonly ICallGateProvider<object> disposedProvider;
    private readonly ICallGateProvider<string, string> playlistGetProvider;
    private readonly ICallGateProvider<string, string> playlistAddProvider;
    private readonly ICallGateProvider<string, string> playlistRemoveProvider;
    private readonly ICallGateProvider<string, string> playlistClearProvider;
    private readonly ICallGateProvider<string, string> playlistSkipProvider;
    private readonly ICallGateProvider<string, string> playlistMoveProvider;
    private readonly ICallGateProvider<string, string> playlistSetOptionsProvider;
    private readonly ScreenPlaylistIpcService playlistService;
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

        apiVersionProvider = services.PluginInterface.GetIpcProvider<int>("CrystalCast.ApiVersion");
        snapshotProvider = services.PluginInterface.GetIpcProvider<string>("CrystalCast.Screen.GetSnapshot");
        applyStateProvider = services.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.ApplyState");
        applyStateDetailedProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.ApplyStateDetailed");
        removeProvider = services.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.Remove");
        localStateChangedProvider = services.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.LocalStateChanged");
        var screenChangedProvider = services.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.Changed");
        changePublisher = new ScreenChangePublisher(configuration, screenChangedProvider, ownerSessionId);
        mutationService = new ScreenIpcMutationService(configuration, renderer, stateBuilder, changePublisher, PublishLocalState, services.Log, hiddenScreens);
        createScreenProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Create");
        updateScreenProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Update");
        updateSourceProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.UpdateSource");
        sourceLockProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.SetSourceLock");
        sourceStateProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.GetSourceState");
        capabilitiesProvider = services.PluginInterface.GetIpcProvider<string>("CrystalCast.GetCapabilities");
        readyProvider = services.PluginInterface.GetIpcProvider<object>("CrystalCast.Ready");
        disposedProvider = services.PluginInterface.GetIpcProvider<object>("CrystalCast.Disposed");
        playlistGetProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Get");
        playlistAddProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Add");
        playlistRemoveProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Remove");
        playlistClearProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Clear");
        playlistSkipProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Skip");
        playlistMoveProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.Move");
        playlistSetOptionsProvider = services.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Playlist.SetOptions");
        playlistService = new ScreenPlaylistIpcService(configuration, playlistAdvancer, PublishPlaylistAdvance, PublishPlaylistChange, services.Log);
        dispatcher = new IpcMainThreadDispatcher(services.Framework);

        UpdateRegistration();
        services.Framework.Update += OnFrameworkUpdate;
    }

    public IReadOnlyCollection<ScreenStateEnvelope> RemoteScreens => remoteScreens.GetSnapshot();

    public bool Enabled => configuration.IpcEnabled;

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
            SendUnavailableForKnownLocalScreens();
            RemoveIpcCreatedScreens();
            remoteScreens.Clear();
            changePublisher.Clear();
        }

        UpdateRegistration();
        configuration.Save();
    }

    public void UpdateRegistration()
    {
        if (configuration.IpcEnabled)
        {
            Register();
            return;
        }

        Unregister();
    }

    private void Register()
    {
        if (registered)
            return;

        // Every handler runs on the game's main thread; see IpcMainThreadDispatcher.
        apiVersionProvider.RegisterFunc(() => ApiVersion);
        snapshotProvider.RegisterFunc(() => dispatcher.Invoke(GetSnapshotJson, MutationError));
        applyStateProvider.RegisterFunc(json => dispatcher.Invoke(() => ApplyStateJson(json), _ => false));
        applyStateDetailedProvider.RegisterFunc(json => dispatcher.Invoke(() => ApplyStateDetailedJson(json), ApplyStateError));
        removeProvider.RegisterFunc(screenId => dispatcher.Invoke(() => Remove(screenId), _ => false));
        createScreenProvider.RegisterFunc(json => Mutate(json, mutationService.CreateScreenJson));
        updateScreenProvider.RegisterFunc(json => Mutate(json, mutationService.UpdateScreenJson));
        updateSourceProvider.RegisterFunc(json => Mutate(json, mutationService.UpdateSourceJson));
        sourceLockProvider.RegisterFunc(json => Mutate(json, mutationService.SetSourceLockJson));
        sourceStateProvider.RegisterFunc(screenId => dispatcher.Invoke(() => GetSourceStateJson(screenId), SourceStateError));
        capabilitiesProvider.RegisterFunc(() => dispatcher.Invoke(GetCapabilitiesJson, MutationError));
        playlistGetProvider.RegisterFunc(screenId => dispatcher.Invoke(() => playlistService.GetJson(screenId), PlaylistError));
        playlistAddProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.AddJson));
        playlistRemoveProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.RemoveJson));
        playlistClearProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.ClearJson));
        playlistSkipProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.SkipJson));
        playlistMoveProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.MoveJson));
        playlistSetOptionsProvider.RegisterFunc(json => MutatePlaylist(json, playlistService.SetOptionsJson));
        registered = true;
        readyProvider.SendMessage();
    }

    /// <summary>Size check, then the handler on the main thread.</summary>
    private string Mutate(string json, Func<string, string> handler)
    {
        if (!IpcPayloadLimit.TryCheck(json, out var error))
            return IpcJsonService.SerializeMutationError(error);

        return dispatcher.Invoke(() => handler(json), MutationError);
    }

    private string GetSourceStateJson(string screenId)
    {
        if (!IpcPayloadLimit.TryCheckScreenId(screenId, out var error))
            return SourceStateError(error);

        return mutationService.GetSourceStateJson(screenId);
    }

    private string MutatePlaylist(string json, Func<string, string> handler)
    {
        if (!IpcPayloadLimit.TryCheck(json, out var error))
            return PlaylistError(error);

        return dispatcher.Invoke(() => handler(json), PlaylistError);
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

        apiVersionProvider.UnregisterFunc();
        snapshotProvider.UnregisterFunc();
        applyStateProvider.UnregisterFunc();
        applyStateDetailedProvider.UnregisterFunc();
        removeProvider.UnregisterFunc();
        createScreenProvider.UnregisterFunc();
        updateScreenProvider.UnregisterFunc();
        updateSourceProvider.UnregisterFunc();
        sourceLockProvider.UnregisterFunc();
        sourceStateProvider.UnregisterFunc();
        capabilitiesProvider.UnregisterFunc();
        playlistGetProvider.UnregisterFunc();
        playlistAddProvider.UnregisterFunc();
        playlistRemoveProvider.UnregisterFunc();
        playlistClearProvider.UnregisterFunc();
        playlistSkipProvider.UnregisterFunc();
        playlistMoveProvider.UnregisterFunc();
        playlistSetOptionsProvider.UnregisterFunc();
        registered = false;
        playbackWatcher.Clear();
    }

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

    private void SendUnavailableForKnownLocalScreens()
    {
        changePublisher.SendUnavailableEventsForMissingLocalScreens([]);
    }

    private void RemoveIpcCreatedScreens()
    {
        configuration.Normalize();
        var removed = configuration.BrowserScreens.RemoveAll(screen => screen.CreatedByIpc);
        if (removed <= 0)
            return;

        configuration.Normalize();
    }

    public string PublishLocalState()
    {
        return PublishLocalState(null, null);
    }

    /// <summary>
    /// A screen moved to another playlist video, by ending, Skip or Play now. Reloads the player when the link
    /// is the same (the browser only reloads on a new link) and announces the change.
    /// </summary>
    internal void PublishPlaylistAdvance(PlaylistAdvance advance)
    {
        if (advance.SameSource && FindBrowserScreen(advance.ScreenId) is { } screen)
            renderer.TryRestartDynamicSource(screen);

        PublishLocalState(advance.ScreenId, [ScreenIpcChangeKind.Source, ScreenIpcChangeKind.Playlist]);
    }

    internal void PublishPlaylistChange(string screenId)
    {
        PublishLocalState(screenId, [ScreenIpcChangeKind.Playlist]);
    }

    private string PublishLocalState(string? changedScreenId, IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        configuration.Normalize();
        UpdateRegistration();
        if (!configuration.IpcEnabled)
        {
            configuration.Save();
            return string.Empty;
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

        string? firstJson = null;
        foreach (var state in states)
        {
            if (!changePublisher.MaybeSendScreenChanged(state, changedScreenId, forcedChanges))
                continue;

            var json = IpcJsonService.Serialize(state);
            firstJson ??= json;
            localStateChangedProvider.SendMessage(json);
        }

        changePublisher.SendUnavailableEventsForMissingLocalScreens(publishedScreenIds);

        if (forcedChanges is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(changedScreenId)
            && states.All(state => !string.Equals(state.ScreenId, changedScreenId, StringComparison.Ordinal))
            && FindBrowserScreen(changedScreenId) is { } forcedScreen
            && forcedScreen.Enabled
            && stateBuilder.TryBuildBrowserScreenState(forcedScreen, out var forcedState))
        {
            changePublisher.MaybeSendScreenChanged(forcedState, changedScreenId, forcedChanges);
        }

        return firstJson ?? string.Empty;
    }

    public ScreenStateEnvelope BuildLocalState()
    {
        return stateBuilder.BuildLocalState();
    }

    public IEnumerable<ScreenStateEnvelope> BuildLocalStates()
    {
        return stateBuilder.BuildLocalStates();
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        Unregister();
        dispatcher.Dispose();
        remoteScreens.Clear();
        changePublisher.Clear();
    }

    private string GetSnapshotJson()
    {
        var localStates = BuildLocalStates().ToArray();
        changePublisher.RememberKnownLocalScreens(localStates.Select(state => state.ScreenId));
        var snapshot = new
        {
            schemaVersion = 1,
            apiVersion = ApiVersion,
            local = localStates.FirstOrDefault(),
            localScreens = localStates,
            remote = remoteScreens.GetSnapshot()
                .OrderBy(screen => screen.OwnerSessionId)
                .ThenBy(screen => screen.ScreenId)
                .ToArray(),
        };
        return IpcJsonService.Serialize(snapshot);
    }

    private bool ApplyStateJson(string json)
    {
        return ApplyState(json).Success;
    }

    private string ApplyStateDetailedJson(string json)
    {
        return IpcJsonService.Serialize(ApplyState(json));
    }

    private ScreenIpcApplyStateResponse ApplyState(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || !IpcPayloadLimit.TryCheck(json, out _))
            {
                return new ScreenIpcApplyStateResponse
                {
                    Success = false,
                    Result = ScreenIpcApplyStateResult.RejectedInvalid,
                    Error = $"State payload must be at most {MaxPayloadBytes} UTF-8 bytes.",
                };
            }

            var state = IpcJsonService.Deserialize<ScreenStateEnvelope>(json);
            var result = remoteScreens.Apply(state, ownerSessionId, out var error);
            return new ScreenIpcApplyStateResponse
            {
                Success = result is RemoteScreenApplyResult.Applied
                    or RemoteScreenApplyResult.IgnoredSelf
                    or RemoteScreenApplyResult.IgnoredDuplicate
                    or RemoteScreenApplyResult.IgnoredStale,
                Result = result switch
                {
                    RemoteScreenApplyResult.Applied => ScreenIpcApplyStateResult.Applied,
                    RemoteScreenApplyResult.IgnoredSelf => ScreenIpcApplyStateResult.IgnoredSelf,
                    RemoteScreenApplyResult.IgnoredDuplicate => ScreenIpcApplyStateResult.IgnoredDuplicate,
                    RemoteScreenApplyResult.IgnoredStale => ScreenIpcApplyStateResult.IgnoredStale,
                    RemoteScreenApplyResult.RejectedCapacity => ScreenIpcApplyStateResult.RejectedCapacity,
                    _ => ScreenIpcApplyStateResult.RejectedInvalid,
                },
                Error = error,
                ScreenId = state?.ScreenId ?? string.Empty,
                OwnerSessionId = state?.OwnerSessionId ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            services.Log.Warning(ex, "Failed to apply CrystalCast screen state IPC payload.");
            return new ScreenIpcApplyStateResponse
            {
                Success = false,
                Result = ScreenIpcApplyStateResult.RejectedInvalid,
                Error = "State payload is not valid JSON.",
            };
        }
    }

    private bool Remove(string screenId)
    {
        screenId = IpcJsonService.NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId) || !IpcPayloadLimit.TryCheckScreenId(screenId, out _))
            return false;

        if (mutationService.Remove(screenId))
            return true;

        return remoteScreens.RemoveByScreenId(screenId);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }
}
