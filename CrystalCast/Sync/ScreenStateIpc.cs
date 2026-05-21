using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Plugin.Ipc;

namespace CrystalCast.Sync;

public sealed class ScreenStateIpc : IDisposable
{
    public const int ApiVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;
    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<string> snapshotProvider;
    private readonly ICallGateProvider<string, bool> applyStateProvider;
    private readonly ICallGateProvider<string, bool> removeProvider;
    private readonly ICallGateProvider<string, object> localStateChangedProvider;
    private readonly ICallGateProvider<string, object> screenChangedProvider;
    private readonly ICallGateProvider<string, string> createScreenProvider;
    private readonly ICallGateProvider<string, string> updateScreenProvider;
    private readonly ICallGateProvider<string, string> updateSourceProvider;
    private readonly ICallGateProvider<string, string> sourceLockProvider;
    private readonly ICallGateProvider<string, string> sourceStateProvider;
    private readonly Dictionary<string, ScreenStateEnvelope> remoteScreens = new();
    private readonly Dictionary<string, ScreenChangeFingerprint> localScreenFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownLocalScreenIds = new(StringComparer.Ordinal);

    public ScreenStateIpc(Configuration configuration, WorldScreenManager renderer)
    {
        this.configuration = configuration;
        this.renderer = renderer;

        apiVersionProvider = Plugin.PluginInterface.GetIpcProvider<int>("CrystalCast.ApiVersion");
        snapshotProvider = Plugin.PluginInterface.GetIpcProvider<string>("CrystalCast.Screen.GetSnapshot");
        applyStateProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.ApplyState");
        removeProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.Remove");
        localStateChangedProvider = Plugin.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.LocalStateChanged");
        screenChangedProvider = Plugin.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.Changed");
        createScreenProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Create");
        updateScreenProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Update");
        updateSourceProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.UpdateSource");
        sourceLockProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.SetSourceLock");
        sourceStateProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.GetSourceState");

        apiVersionProvider.RegisterFunc(() => ApiVersion);
        snapshotProvider.RegisterFunc(GetSnapshotJson);
        applyStateProvider.RegisterFunc(ApplyStateJson);
        removeProvider.RegisterFunc(Remove);
        createScreenProvider.RegisterFunc(CreateScreenJson);
        updateScreenProvider.RegisterFunc(UpdateScreenJson);
        updateSourceProvider.RegisterFunc(UpdateSourceJson);
        sourceLockProvider.RegisterFunc(SetSourceLockJson);
        sourceStateProvider.RegisterFunc(GetSourceStateJson);
    }

    public IReadOnlyCollection<ScreenStateEnvelope> RemoteScreens => remoteScreens.Values;

    public string PublishLocalState()
    {
        return PublishLocalState(null, null);
    }

    private string PublishLocalState(string? changedScreenId, IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        configuration.Normalize();
        var states = new List<ScreenStateEnvelope>();
        if (configuration.Enabled && configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var screensToPublish = configuration.BrowserScreens
                .Take(Configuration.MaxRenderableBrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            foreach (var screen in screensToPublish)
            {
                if (!TryResolveForIpc(screen.Placement, out var resolved))
                    continue;

                screen.LocalSequence++;
                states.Add(BuildBrowserScreenState(screen, resolved));
            }
        }
        else if (configuration.Enabled)
        {
            var placement = configuration.GetLocalVideoPlacement();
            if (TryResolveForIpc(placement, out var resolved))
            {
                configuration.LocalSequence++;
                states.Add(BuildLocalVideoState(placement, resolved));
            }
        }

        configuration.Save();
        var publishedScreenIds = states.Select(state => state.ScreenId).ToHashSet(StringComparer.Ordinal);
        RememberKnownLocalScreens(publishedScreenIds);

        string? firstJson = null;
        foreach (var state in states)
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            firstJson ??= json;
            localStateChangedProvider.SendMessage(json);
            MaybeSendScreenChanged(state, changedScreenId, forcedChanges);
        }

        SendUnavailableEventsForMissingLocalScreens(publishedScreenIds);

        if (forcedChanges is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(changedScreenId)
            && states.All(state => !string.Equals(state.ScreenId, changedScreenId, StringComparison.Ordinal))
            && FindBrowserScreen(changedScreenId) is { } forcedScreen
            && forcedScreen.Enabled
            && TryBuildBrowserScreenState(forcedScreen, out var forcedState))
        {
            MaybeSendScreenChanged(forcedState, changedScreenId, forcedChanges);
        }

        return firstJson ?? string.Empty;
    }

    public ScreenStateEnvelope BuildLocalState()
    {
        return BuildLocalStates().FirstOrDefault() ?? new ScreenStateEnvelope
        {
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
        };
    }

    public IEnumerable<ScreenStateEnvelope> BuildLocalStates()
    {
        configuration.Normalize();
        if (!configuration.Enabled)
            return [];

        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var enabledScreens = configuration.BrowserScreens
                .Take(Configuration.MaxRenderableBrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            var states = new List<ScreenStateEnvelope>();
            foreach (var screen in enabledScreens)
            {
                if (TryBuildBrowserScreenState(screen, out var state))
                    states.Add(state);
            }

            return states;
        }

        return TryBuildLocalVideoState(out var localState) ? [localState] : [];
    }

    private bool TryBuildLocalVideoState(out ScreenStateEnvelope state)
    {
        var placement = configuration.GetLocalVideoPlacement();
        if (!TryResolveForIpc(placement, out var resolved))
        {
            state = null!;
            return false;
        }

        state = BuildLocalVideoState(placement, resolved);
        return true;
    }

    private ScreenStateEnvelope BuildLocalVideoState(ScreenPlacementSettings placement, ResolvedScreenPlacement resolved)
    {
        var source = BuildLocalVideoSourceState();
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(
            resolved.YawRadians,
            resolved.PitchRadians,
            resolved.RollRadians);

        return new ScreenStateEnvelope
        {
            SchemaVersion = 1,
            ScreenId = configuration.ScreenId,
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
            Position = Vector3Dto.FromVector3(resolved.Position),
            Rotation = QuaternionDto.FromQuaternion(System.Numerics.Quaternion.Normalize(rotation)),
            SizeMeters = new Vector2Dto(placement.WidthMeters, placement.HeightMeters),
            Source = source,
            Playback = BuildLocalPlaybackState(),
            Visual = new ScreenVisualState
            {
                OccludedAlpha = placement.OccludedAlpha,
                OcclusionTolerance = placement.OcclusionTolerance,
                ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
                DistanceFadeEnabled = placement.EnableDistanceFade,
                FadeStartMeters = placement.FadeStartMeters,
                FadeStopMeters = placement.FadeStopMeters,
            },
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = configuration.LocalSequence,
        };
    }

    private bool TryBuildBrowserScreenState(BrowserScreenProfile screen, out ScreenStateEnvelope state)
    {
        if (!TryResolveForIpc(screen.Placement, out var resolved))
        {
            state = null!;
            return false;
        }

        state = BuildBrowserScreenState(screen, resolved);
        return true;
    }

    private ScreenStateEnvelope BuildBrowserScreenState(BrowserScreenProfile screen, ResolvedScreenPlacement resolved)
    {
        var placement = screen.Placement;
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(
            resolved.YawRadians,
            resolved.PitchRadians,
            resolved.RollRadians);

        return new ScreenStateEnvelope
        {
            SchemaVersion = 1,
            ScreenId = screen.ScreenId,
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
            Position = Vector3Dto.FromVector3(resolved.Position),
            Rotation = QuaternionDto.FromQuaternion(System.Numerics.Quaternion.Normalize(rotation)),
            SizeMeters = new Vector2Dto(placement.WidthMeters, placement.HeightMeters),
            Source = BuildBrowserSourceState(screen),
            Playback = BuildBrowserPlaybackState(screen),
            Visual = new ScreenVisualState
            {
                OccludedAlpha = placement.OccludedAlpha,
                OcclusionTolerance = placement.OcclusionTolerance,
                ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
                DistanceFadeEnabled = placement.EnableDistanceFade,
                FadeStartMeters = placement.FadeStartMeters,
                FadeStopMeters = placement.FadeStopMeters,
            },
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = screen.LocalSequence,
        };
    }

    private static bool TryResolveForIpc(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        if (ScreenPlacementResolver.TryResolve(placement, out resolved))
            return true;

        resolved = default;
        return false;
    }

    public void Dispose()
    {
        apiVersionProvider.UnregisterFunc();
        snapshotProvider.UnregisterFunc();
        applyStateProvider.UnregisterFunc();
        removeProvider.UnregisterFunc();
        createScreenProvider.UnregisterFunc();
        updateScreenProvider.UnregisterFunc();
        updateSourceProvider.UnregisterFunc();
        sourceLockProvider.UnregisterFunc();
        sourceStateProvider.UnregisterFunc();
        remoteScreens.Clear();
        localScreenFingerprints.Clear();
        knownLocalScreenIds.Clear();
    }

    private string GetSnapshotJson()
    {
        var localStates = BuildLocalStates().ToArray();
        RememberKnownLocalScreens(localStates.Select(state => state.ScreenId));
        var snapshot = new
        {
            schemaVersion = 1,
            apiVersion = ApiVersion,
            local = localStates.FirstOrDefault(),
            localScreens = localStates,
            remote = remoteScreens.Values.OrderBy(screen => screen.ScreenId).ToArray(),
        };
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private bool ApplyStateJson(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<ScreenStateEnvelope>(json, JsonOptions);
            if (state == null || state.SchemaVersion != 1 || string.IsNullOrWhiteSpace(state.ScreenId))
                return false;

            if (state.OwnerSessionId == configuration.OwnerSessionId)
                return true;

            if (remoteScreens.TryGetValue(state.ScreenId, out var existing) && existing.Sequence >= state.Sequence)
                return true;

            remoteScreens[state.ScreenId] = state;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to apply CrystalCast screen state IPC payload.");
            return false;
        }
    }

    private bool Remove(string screenId)
    {
        screenId = NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId))
            return false;

        if (remoteScreens.Remove(screenId))
            return true;

        configuration.Normalize();
        var screen = FindBrowserScreen(screenId);
        if (screen is not { CreatedByIpc: true })
            return false;

        configuration.BrowserScreens.Remove(screen);
        configuration.Normalize();
        configuration.Save();
        localScreenFingerprints.Remove(screenId);
        knownLocalScreenIds.Remove(screenId);
        return true;
    }

    private string CreateScreenJson(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ScreenIpcMutationRequest>(json, JsonOptions);
            if (request == null)
                return SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            if (configuration.BrowserScreens.Count >= Configuration.MaxRenderableBrowserScreens)
                return SerializeMutationError($"CrystalCast can render at most {Configuration.MaxRenderableBrowserScreens} browser screens.");

            var requestedScreenId = NormalizeText(request.ScreenId);
            if (!string.IsNullOrWhiteSpace(requestedScreenId) && FindBrowserScreen(requestedScreenId) != null)
                return SerializeMutationError($"Screen '{requestedScreenId}' already exists.", requestedScreenId);

            if (request.Provider is { } provider && provider != BrowserSourceProviderKind.YouTube)
                return SerializeMutationError($"Unsupported browser source provider '{provider}'.");

            var name = NormalizeText(request.Name);
            if (string.IsNullOrWhiteSpace(name))
                name = GetNextIpcScreenName();

            var screen = configuration.CreateDefaultBrowserScreen(name);
            if (!string.IsNullOrWhiteSpace(requestedScreenId))
                screen.ScreenId = requestedScreenId;

            screen.CreatedByIpc = true;
            screen.IpcOwnerId = NormalizeText(request.OwnerId);
            screen.SourceControlsOwnerId = NormalizeText(request.SourceControlsOwnerId);
            if (request.SourceControlsLocked == true && string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId))
                screen.SourceControlsOwnerId = screen.IpcOwnerId;

            if (!ApplyScreenMutation(screen, request, out var error))
                return SerializeMutationError(error, screen.ScreenId);

            configuration.BrowserScreens.Add(screen);
            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            PublishLocalState(screen.ScreenId, GetCreateChangeKinds());
            return SerializeMutationSuccess(screen, created: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to create CrystalCast screen through IPC.");
            return SerializeMutationError($"Failed to create screen: {ex.Message}");
        }
    }

    private string UpdateScreenJson(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ScreenIpcMutationRequest>(json, JsonOptions);
            if (request == null)
                return SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            if (!ApplyScreenMutation(screen, request, out var error))
                return SerializeMutationError(error, screen.ScreenId);

            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyYouTubeRuntimeControls(screen, request.YouTube);
            PublishLocalState(screen.ScreenId, GetMutationChangeKinds(request));
            return SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast screen through IPC.");
            return SerializeMutationError($"Failed to update screen: {ex.Message}");
        }
    }

    private string SetSourceLockJson(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ScreenIpcSourceLockRequest>(json, JsonOptions);
            if (request == null)
                return SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            screen.SourceControlsLocked = request.Locked;
            screen.SourceControlsOwnerId = request.Locked
                ? FirstNonEmpty(request.OwnerId, screen.SourceControlsOwnerId, screen.IpcOwnerId)
                : string.Empty;

            configuration.Save();
            PublishLocalState(screen.ScreenId, [ScreenIpcChangeKind.SourceLock]);
            return SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast source lock through IPC.");
            return SerializeMutationError($"Failed to update source lock: {ex.Message}");
        }
    }

    private string UpdateSourceJson(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ScreenIpcSourceUpdateRequest>(json, JsonOptions);
            if (request == null)
                return SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            if (request.Provider is { } provider && provider != BrowserSourceProviderKind.YouTube)
                return SerializeMutationError($"Unsupported browser source provider '{provider}'.", screen.ScreenId);

            screen.ProviderKind = BrowserSourceProviderKind.YouTube;
            if (!ApplyYouTubePatch(screen, request.YouTube, out var error))
                return SerializeMutationError(error, screen.ScreenId);

            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyYouTubeRuntimeControls(screen, request.YouTube);
            PublishLocalState(screen.ScreenId, GetSourceUpdateChangeKinds(request));
            return SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast source through IPC.");
            return SerializeMutationError($"Failed to update source: {ex.Message}");
        }
    }

    private string GetSourceStateJson(string screenId)
    {
        try
        {
            configuration.Normalize();
            var screen = string.IsNullOrWhiteSpace(screenId)
                ? configuration.GetActiveBrowserScreen()
                : FindBrowserScreen(screenId);
            if (screen == null)
            {
                return JsonSerializer.Serialize(new ScreenIpcSourceStateResponse
                {
                    Success = false,
                    Error = $"Screen '{screenId}' was not found.",
                    ScreenId = NormalizeText(screenId),
                }, JsonOptions);
            }

            var source = BuildBrowserSourceState(screen);
            var playback = BuildBrowserPlaybackState(screen);
            return JsonSerializer.Serialize(new ScreenIpcSourceStateResponse
            {
                Success = true,
                ScreenId = screen.ScreenId,
                Name = screen.Name,
                Enabled = screen.Enabled,
                CreatedByIpc = screen.CreatedByIpc,
                OwnerId = screen.IpcOwnerId,
                SourceControlsLocked = screen.SourceControlsLocked,
                SourceControlsOwnerId = screen.SourceControlsOwnerId,
                SourceKind = ScreenSourceKind.YouTubeBrowser,
                Provider = screen.ProviderKind.ToString(),
                SourceName = renderer.GetSourceName(screen),
                SourceStatus = renderer.GetSourceStatus(screen),
                YouTube = new YouTubeSourceStateDto
                {
                    Url = screen.YouTubeUrl,
                    CanonicalUrl = source.Url,
                    VideoId = source.VideoId,
                    Title = source.Title,
                    State = playback.State,
                    PositionMs = playback.PositionMs,
                    DurationMs = playback.DurationMs,
                    Rate = playback.Rate,
                    HostTimestampUnixMs = playback.HostTimestampUnixMs,
                },
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read CrystalCast source state through IPC.");
            return JsonSerializer.Serialize(new ScreenIpcSourceStateResponse
            {
                Success = false,
                Error = $"Failed to get source state: {ex.Message}",
                ScreenId = NormalizeText(screenId),
            }, JsonOptions);
        }
    }

    private bool ApplyScreenMutation(
        BrowserScreenProfile screen,
        ScreenIpcMutationRequest request,
        out string error)
    {
        error = string.Empty;
        if (request.Provider is { } provider && provider != BrowserSourceProviderKind.YouTube)
        {
            error = $"Unsupported browser source provider '{provider}'.";
            return false;
        }

        screen.ProviderKind = BrowserSourceProviderKind.YouTube;

        var name = NormalizeText(request.Name);
        if (!string.IsNullOrWhiteSpace(name))
            screen.Name = name;

        var ownerId = NormalizeText(request.OwnerId);
        if (!string.IsNullOrWhiteSpace(ownerId))
            screen.IpcOwnerId = ownerId;

        if (request.Enabled.HasValue)
            screen.Enabled = request.Enabled.Value;

        var sourceOwner = NormalizeText(request.SourceControlsOwnerId);
        if (!string.IsNullOrWhiteSpace(sourceOwner))
            screen.SourceControlsOwnerId = sourceOwner;

        if (request.SourceControlsLocked.HasValue)
        {
            screen.SourceControlsLocked = request.SourceControlsLocked.Value;
            screen.SourceControlsOwnerId = screen.SourceControlsLocked
                ? FirstNonEmpty(screen.SourceControlsOwnerId, screen.IpcOwnerId, ownerId)
                : string.Empty;
        }

        ApplyPlacementPatch(screen.Placement, request.Placement);
        return ApplyYouTubePatch(screen, request.YouTube, out error);
    }

    private static void ApplyPlacementPatch(ScreenPlacementSettings placement, ScreenPlacementPatchDto? patch)
    {
        if (patch == null)
            return;

        if (patch.Mode.HasValue)
            placement.Mode = patch.Mode.Value;
        if (patch.PositionX.HasValue)
            placement.PositionX = patch.PositionX.Value;
        if (patch.PositionY.HasValue)
            placement.PositionY = patch.PositionY.Value;
        if (patch.PositionZ.HasValue)
            placement.PositionZ = patch.PositionZ.Value;
        if (patch.YawRadians.HasValue)
            placement.YawRadians = patch.YawRadians.Value;
        if (patch.PitchRadians.HasValue)
            placement.PitchRadians = patch.PitchRadians.Value;
        if (patch.RollRadians.HasValue)
            placement.RollRadians = patch.RollRadians.Value;
        if (patch.WidthMeters.HasValue)
            placement.WidthMeters = patch.WidthMeters.Value;
        if (patch.HeightMeters.HasValue)
            placement.HeightMeters = patch.HeightMeters.Value;
        if (patch.ScreenCurveAmountMeters.HasValue)
            placement.ScreenCurveAmountMeters = patch.ScreenCurveAmountMeters.Value;
        if (patch.OccludedAlpha.HasValue)
            placement.OccludedAlpha = patch.OccludedAlpha.Value;
        if (patch.OcclusionTolerance.HasValue)
            placement.OcclusionTolerance = patch.OcclusionTolerance.Value;
        if (patch.DistanceFadeEnabled.HasValue)
            placement.EnableDistanceFade = patch.DistanceFadeEnabled.Value;
        if (patch.FadeStartMeters.HasValue)
            placement.FadeStartMeters = patch.FadeStartMeters.Value;
        if (patch.FadeStopMeters.HasValue)
            placement.FadeStopMeters = patch.FadeStopMeters.Value;

        placement.Normalize();
    }

    private static bool ApplyYouTubePatch(BrowserScreenProfile screen, YouTubeScreenPatchDto? patch, out string error)
    {
        error = string.Empty;
        if (patch == null)
            return true;

        if (patch.Url != null)
        {
            var url = patch.Url.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !YouTubeVideoId.TryParse(url, out _))
            {
                error = "YouTube URL or video ID is invalid.";
                return false;
            }

            screen.YouTubeUrl = url;
        }

        if (patch.PlaybackPaused.HasValue)
            screen.PlaybackPaused = patch.PlaybackPaused.Value;
        if (patch.Autoplay.HasValue)
            screen.YouTubeAutoplay = patch.Autoplay.Value;
        if (patch.Loop.HasValue)
            screen.LoopYouTube = patch.Loop.Value;
        if (patch.PlaybackRate.HasValue)
            screen.YouTubePlaybackRate = patch.PlaybackRate.Value;
        if (patch.BrowserWidth.HasValue)
            screen.YouTubeBrowserWidth = patch.BrowserWidth.Value;
        if (patch.BrowserHeight.HasValue)
            screen.YouTubeBrowserHeight = patch.BrowserHeight.Value;
        if (patch.CaptureFps.HasValue)
            screen.YouTubeCaptureFps = patch.CaptureFps.Value;
        if (patch.CaptureFpsManual.HasValue)
            screen.YouTubeCaptureFpsManual = patch.CaptureFpsManual.Value;
        if (patch.AudioEnabled.HasValue)
            screen.YouTubeAudioEnabled = patch.AudioEnabled.Value;
        if (patch.Volume.HasValue)
            screen.YouTubeVolume = patch.Volume.Value;
        if (patch.SpatialAudioEnabled.HasValue)
            screen.SpatialAudioEnabled = patch.SpatialAudioEnabled.Value;
        if (patch.SpatialAudioFullVolumeRadiusMeters.HasValue)
            screen.SpatialAudioFullVolumeRadiusMeters = patch.SpatialAudioFullVolumeRadiusMeters.Value;
        if (patch.SpatialAudioSilentRadiusMeters.HasValue)
            screen.SpatialAudioSilentRadiusMeters = patch.SpatialAudioSilentRadiusMeters.Value;

        return true;
    }

    private void ApplyYouTubeRuntimeControls(BrowserScreenProfile screen, YouTubeScreenPatchDto? patch)
    {
        if (patch == null)
            return;

        if (patch.Restart)
        {
            renderer.TryRestartDynamicSource(screen);
        }
        else if (patch.PositionMs.HasValue)
        {
            var seconds = Math.Max(0.0, patch.PositionMs.Value / 1000.0);
            renderer.TrySeekDynamicSourceTo(screen, seconds);
        }

        if (!patch.PlaybackPaused.HasValue)
            return;

        if (patch.PlaybackPaused.Value)
            renderer.TryPauseDynamicSource(screen);
        else
            renderer.TryPlayDynamicSource(screen);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    private void MaybeSendScreenChanged(
        ScreenStateEnvelope state,
        string? forcedScreenId,
        IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        var screen = FindBrowserScreen(state.ScreenId);
        var next = BuildFingerprint(state, screen);
        if (!localScreenFingerprints.TryGetValue(state.ScreenId, out var previous))
        {
            localScreenFingerprints[state.ScreenId] = next;
            SendScreenChanged(
                state,
                screen,
                IsForcedScreen(state.ScreenId, forcedScreenId, forcedChanges)
                    ? forcedChanges!
                    : GetCreateChangeKinds());

            return;
        }

        localScreenFingerprints[state.ScreenId] = next;
        if (IsForcedScreen(state.ScreenId, forcedScreenId, forcedChanges))
        {
            SendScreenChanged(state, screen, forcedChanges!);
            return;
        }

        var changes = GetFingerprintChanges(previous, next);
        if (changes.Count > 0)
            SendScreenChanged(state, screen, changes);
    }

    private static bool IsForcedScreen(
        string screenId,
        string? forcedScreenId,
        IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        return forcedChanges is { Count: > 0 }
            && string.Equals(screenId, NormalizeText(forcedScreenId), StringComparison.Ordinal);
    }

    private void SendScreenChanged(
        ScreenStateEnvelope state,
        BrowserScreenProfile? screen,
        IReadOnlyCollection<ScreenIpcChangeKind> changes)
    {
        var distinctChanges = changes.Distinct().ToArray();
        if (distinctChanges.Length == 0)
            return;

        var evt = new ScreenIpcChangeEvent
        {
            ScreenId = state.ScreenId,
            OwnerSessionId = configuration.OwnerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = distinctChanges,
            State = state,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        screenChangedProvider.SendMessage(JsonSerializer.Serialize(evt, JsonOptions));
    }

    private void SendUnavailableEventsForMissingLocalScreens(HashSet<string> currentScreenIds)
    {
        foreach (var screenId in knownLocalScreenIds.Except(currentScreenIds, StringComparer.Ordinal).ToList())
        {
            var screen = FindBrowserScreen(screenId);
            SendScreenUnavailable(screenId, screen);
            localScreenFingerprints.Remove(screenId);
            knownLocalScreenIds.Remove(screenId);
        }
    }

    private void SendScreenUnavailable(string screenId, BrowserScreenProfile? screen)
    {
        var evt = new ScreenIpcChangeEvent
        {
            ScreenId = screenId,
            OwnerSessionId = configuration.OwnerSessionId,
            CreatedByIpc = screen?.CreatedByIpc ?? false,
            OwnerId = screen?.IpcOwnerId ?? string.Empty,
            SourceControlsLocked = screen?.SourceControlsLocked ?? false,
            SourceControlsOwnerId = screen?.SourceControlsOwnerId ?? string.Empty,
            Changes = [ScreenIpcChangeKind.Source],
            State = null,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        screenChangedProvider.SendMessage(JsonSerializer.Serialize(evt, JsonOptions));
    }

    private void RememberKnownLocalScreens(IEnumerable<string> screenIds)
    {
        foreach (var screenId in screenIds)
        {
            if (!string.IsNullOrWhiteSpace(screenId))
                knownLocalScreenIds.Add(screenId);
        }
    }

    private static ScreenChangeFingerprint BuildFingerprint(ScreenStateEnvelope state, BrowserScreenProfile? screen)
    {
        return new ScreenChangeFingerprint(
            Placement: string.Join('|',
                state.Position.X,
                state.Position.Y,
                state.Position.Z,
                state.Rotation.X,
                state.Rotation.Y,
                state.Rotation.Z,
                state.Rotation.W,
                state.SizeMeters.X,
                state.SizeMeters.Y),
            Source: string.Join('|',
                state.Source.Kind,
                state.Source.Provider,
                state.Source.Identity,
                state.Source.Title,
                state.Source.Url,
                state.Source.VideoId,
                screen?.YouTubeAutoplay,
                screen?.LoopYouTube,
                screen?.YouTubePlaybackRate,
                screen?.YouTubeBrowserWidth,
                screen?.YouTubeBrowserHeight,
                screen?.YouTubeCaptureFps,
                screen?.YouTubeCaptureFpsManual),
            Playback: string.Join('|',
                state.Playback.State,
                state.Playback.PositionMs,
                state.Playback.DurationMs,
                state.Playback.Rate),
            Visual: string.Join('|',
                state.Visual.OccludedAlpha,
                state.Visual.OcclusionTolerance,
                state.Visual.ScreenCurveAmountMeters,
                state.Visual.DistanceFadeEnabled,
                state.Visual.FadeStartMeters,
                state.Visual.FadeStopMeters),
            Lock: string.Join('|',
                screen?.SourceControlsLocked,
                screen?.SourceControlsOwnerId));
    }

    private static List<ScreenIpcChangeKind> GetFingerprintChanges(ScreenChangeFingerprint previous, ScreenChangeFingerprint next)
    {
        var changes = new List<ScreenIpcChangeKind>();
        if (!string.Equals(previous.Placement, next.Placement, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Placement);
        if (!string.Equals(previous.Source, next.Source, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Source);
        if (!string.Equals(previous.Playback, next.Playback, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Playback);
        if (!string.Equals(previous.Visual, next.Visual, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.Visual);
        if (!string.Equals(previous.Lock, next.Lock, StringComparison.Ordinal))
            changes.Add(ScreenIpcChangeKind.SourceLock);

        return changes;
    }

    private static ScreenIpcChangeKind[] GetCreateChangeKinds()
    {
        return
        [
            ScreenIpcChangeKind.Created,
            ScreenIpcChangeKind.Placement,
            ScreenIpcChangeKind.Source,
            ScreenIpcChangeKind.Playback,
        ];
    }

    private static ScreenIpcChangeKind[] GetMutationChangeKinds(ScreenIpcMutationRequest request)
    {
        var changes = new List<ScreenIpcChangeKind>();
        if (request.Placement != null)
            changes.Add(ScreenIpcChangeKind.Placement);
        if (request.SourceControlsLocked.HasValue || !string.IsNullOrWhiteSpace(request.SourceControlsOwnerId))
            changes.Add(ScreenIpcChangeKind.SourceLock);
        AddYouTubeChangeKinds(changes, request.Provider.HasValue, request.YouTube);

        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
    }

    private static ScreenIpcChangeKind[] GetSourceUpdateChangeKinds(ScreenIpcSourceUpdateRequest request)
    {
        var changes = new List<ScreenIpcChangeKind>();
        AddYouTubeChangeKinds(changes, request.Provider.HasValue, request.YouTube);
        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
    }

    private static void AddYouTubeChangeKinds(List<ScreenIpcChangeKind> changes, bool providerChanged, YouTubeScreenPatchDto? patch)
    {
        if (providerChanged || patch is { Url: not null } || patch?.Autoplay.HasValue == true || patch?.Loop.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
        if (patch?.PlaybackPaused.HasValue == true || patch?.PositionMs.HasValue == true || patch?.Restart == true || patch?.PlaybackRate.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Playback);
        if (patch?.BrowserWidth.HasValue == true || patch?.BrowserHeight.HasValue == true || patch?.CaptureFps.HasValue == true || patch?.CaptureFpsManual.HasValue == true)
            changes.Add(ScreenIpcChangeKind.Source);
    }

    private string GetNextIpcScreenName()
    {
        for (var i = 1; ; i++)
        {
            var name = $"IPC screen {i}";
            if (configuration.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private static string SerializeMutationSuccess(BrowserScreenProfile screen, bool created)
    {
        return JsonSerializer.Serialize(new ScreenIpcMutationResponse
        {
            Success = true,
            Created = created,
            Updated = !created,
            ScreenId = screen.ScreenId,
            Screen = BuildScreenSummary(screen),
        }, JsonOptions);
    }

    private static string SerializeMutationError(string error, string screenId = "")
    {
        return JsonSerializer.Serialize(new ScreenIpcMutationResponse
        {
            Success = false,
            Error = error,
            ScreenId = NormalizeText(screenId),
        }, JsonOptions);
    }

    private static ScreenIpcScreenSummary BuildScreenSummary(BrowserScreenProfile screen)
    {
        return new ScreenIpcScreenSummary
        {
            ScreenId = screen.ScreenId,
            Name = screen.Name,
            Enabled = screen.Enabled,
            CreatedByIpc = screen.CreatedByIpc,
            OwnerId = screen.IpcOwnerId,
            SourceControlsLocked = screen.SourceControlsLocked,
            SourceControlsOwnerId = screen.SourceControlsOwnerId,
            Provider = screen.ProviderKind.ToString(),
            Placement = BuildPlacementState(screen.Placement),
        };
    }

    private static ScreenPlacementStateDto BuildPlacementState(ScreenPlacementSettings placement)
    {
        return new ScreenPlacementStateDto
        {
            Mode = placement.Mode,
            PositionX = placement.PositionX,
            PositionY = placement.PositionY,
            PositionZ = placement.PositionZ,
            YawRadians = placement.YawRadians,
            PitchRadians = placement.PitchRadians,
            RollRadians = placement.RollRadians,
            WidthMeters = placement.WidthMeters,
            HeightMeters = placement.HeightMeters,
            ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
            OccludedAlpha = placement.OccludedAlpha,
            OcclusionTolerance = placement.OcclusionTolerance,
            DistanceFadeEnabled = placement.EnableDistanceFade,
            FadeStartMeters = placement.FadeStartMeters,
            FadeStopMeters = placement.FadeStopMeters,
        };
    }

    private static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeText(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return string.Empty;
    }

    private readonly record struct ScreenChangeFingerprint(
        string Placement,
        string Source,
        string Playback,
        string Visual,
        string Lock);

    private ScreenPlaybackStateDto BuildLocalPlaybackState()
    {
        return new ScreenPlaybackStateDto
        {
            State = configuration.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
            PositionMs = 0,
            Rate = 1.0f,
            Loop = configuration.LoopLocalVideo,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private ScreenPlaybackStateDto BuildBrowserPlaybackState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        if (telemetry != null)
        {
            return new ScreenPlaybackStateDto
            {
                State = screen.PlaybackPaused ? ScreenPlaybackState.Paused : telemetry.State,
                PositionMs = telemetry.PositionMs,
                DurationMs = telemetry.DurationMs,
                Rate = telemetry.Rate,
                Loop = screen.LoopYouTube,
                HostTimestampUnixMs = telemetry.HostTimestampUnixMs,
            };
        }

        return new ScreenPlaybackStateDto
        {
            State = screen.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
            PositionMs = 0,
            Rate = screen.YouTubePlaybackRate,
            Loop = screen.LoopYouTube,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static ScreenSourceState BuildFileSourceState(ScreenSourceKind kind, string path, string fallbackIdentity)
    {
        var title = string.IsNullOrWhiteSpace(path) ? fallbackIdentity : Path.GetFileName(path);
        var hash = TryHashFile(path);
        return new ScreenSourceState
        {
            Kind = kind,
            Identity = string.IsNullOrEmpty(hash) ? fallbackIdentity : $"sha256:{hash}",
            Title = title,
            Hash = hash,
        };
    }

    private ScreenSourceState BuildLocalVideoSourceState()
    {
        var source = BuildFileSourceState(ScreenSourceKind.LocalVideo, configuration.LocalVideoPath, "local-video");
        source.Identity = $"{source.Identity}|scale={configuration.LocalVideoScalePercent.ToString("0.#", CultureInfo.InvariantCulture)}";
        return source;
    }

    private ScreenSourceState BuildBrowserSourceState(BrowserScreenProfile screen)
    {
        return screen.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => BuildYouTubeSourceState(screen),
            _ => new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = screen.ProviderKind.ToString(),
                Identity = $"browser:{screen.ProviderKind}",
                Title = $"{screen.ProviderKind} source",
            },
        };
    }

    private ScreenSourceState BuildYouTubeSourceState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        if (!YouTubeVideoId.TryParse(screen.YouTubeUrl, out var videoId))
        {
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = BrowserSourceProviderKind.YouTube.ToString(),
                Identity = "youtube:invalid",
                Title = "Invalid YouTube source",
            };
        }

        var canonicalUrl = YouTubeVideoId.BuildCanonicalWatchUrl(videoId);
        return new ScreenSourceState
        {
            Kind = ScreenSourceKind.YouTubeBrowser,
            Provider = BrowserSourceProviderKind.YouTube.ToString(),
            Identity = $"youtube:{videoId}",
            Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? "YouTube video" : telemetry.Title,
            Hash = string.Empty,
            Url = canonicalUrl,
            VideoId = videoId,
        };
    }

    private static string TryHashFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
