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
    public const int ApiVersion = 1;

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
    private readonly Dictionary<string, ScreenStateEnvelope> remoteScreens = new();

    public ScreenStateIpc(Configuration configuration, WorldScreenManager renderer)
    {
        this.configuration = configuration;
        this.renderer = renderer;

        apiVersionProvider = Plugin.PluginInterface.GetIpcProvider<int>("CrystalCast.ApiVersion");
        snapshotProvider = Plugin.PluginInterface.GetIpcProvider<string>("CrystalCast.Screen.GetSnapshot");
        applyStateProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.ApplyState");
        removeProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.Remove");
        localStateChangedProvider = Plugin.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.LocalStateChanged");

        apiVersionProvider.RegisterFunc(() => ApiVersion);
        snapshotProvider.RegisterFunc(GetSnapshotJson);
        applyStateProvider.RegisterFunc(ApplyStateJson);
        removeProvider.RegisterFunc(Remove);
    }

    public IReadOnlyCollection<ScreenStateEnvelope> RemoteScreens => remoteScreens.Values;

    public string PublishLocalState()
    {
        configuration.Normalize();
        var states = new List<ScreenStateEnvelope>();
        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var screensToPublish = configuration.BrowserScreens
                .Take(Configuration.MaxBrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();
            if (screensToPublish.Length == 0)
                screensToPublish = [configuration.GetActiveBrowserScreen()];

            foreach (var screen in screensToPublish)
            {
                if (!TryResolveForIpc(screen.Placement, out var resolved))
                    continue;

                screen.LocalSequence++;
                states.Add(BuildBrowserScreenState(screen, resolved));
            }
        }
        else
        {
            var placement = configuration.GetLocalVideoPlacement();
            if (TryResolveForIpc(placement, out var resolved))
            {
                configuration.LocalSequence++;
                states.Add(BuildLocalVideoState(placement, resolved));
            }
        }

        configuration.Save();
        string? firstJson = null;
        foreach (var state in states)
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            firstJson ??= json;
            localStateChangedProvider.SendMessage(json);
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
        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var enabledScreens = configuration.BrowserScreens
                .Take(Configuration.MaxBrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            if (enabledScreens.Length == 0)
                enabledScreens = [configuration.GetActiveBrowserScreen()];

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
        remoteScreens.Clear();
    }

    private string GetSnapshotJson()
    {
        var localStates = BuildLocalStates().ToArray();
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
        if (string.IsNullOrWhiteSpace(screenId))
            return false;

        return remoteScreens.Remove(screenId);
    }

    private ScreenPlaybackStateDto BuildLocalPlaybackState()
    {
        return new ScreenPlaybackStateDto
        {
            State = configuration.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
            PositionMs = 0,
            Rate = 1.0f,
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
                HostTimestampUnixMs = telemetry.HostTimestampUnixMs,
            };
        }

        return new ScreenPlaybackStateDto
        {
            State = screen.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
            PositionMs = 0,
            Rate = screen.YouTubePlaybackRate,
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
