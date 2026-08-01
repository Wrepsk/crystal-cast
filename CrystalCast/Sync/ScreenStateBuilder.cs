using CrystalCast.Rendering;

namespace CrystalCast.Sync;

internal sealed class ScreenStateBuilder
{
    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;

    public ScreenStateBuilder(Configuration configuration, WorldScreenManager renderer)
    {
        this.configuration = configuration;
        this.renderer = renderer;
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

    public bool TryBuildBrowserScreenState(BrowserScreenProfile screen, out ScreenStateEnvelope state)
    {
        if (!TryResolveForIpc(screen.Placement, out var resolved))
        {
            state = null!;
            return false;
        }

        state = BuildBrowserScreenState(screen, resolved);
        return true;
    }

    public ScreenStateEnvelope BuildBrowserScreenState(BrowserScreenProfile screen, ResolvedScreenPlacement resolved)
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

    public ScreenIpcSourceStateResponse BuildSourceStateResponse(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        var adapter = BrowserSourceIpcAdapters.Get(screen.ProviderKind);
        var source = adapter.BuildSourceState(screen, telemetry);
        var playback = adapter.BuildPlaybackState(screen, telemetry);

        var response = new ScreenIpcSourceStateResponse
        {
            Success = true,
            ScreenId = screen.ScreenId,
            Name = screen.Name,
            Enabled = screen.Enabled,
            CreatedByIpc = screen.CreatedByIpc,
            OwnerId = screen.IpcOwnerId,
            SourceControlsLocked = screen.SourceControlsLocked,
            SourceControlsOwnerId = screen.SourceControlsOwnerId,
            SourceKind = ScreenSourceKind.Browser,
            Provider = screen.ProviderKind.ToString(),
            SourceName = renderer.GetSourceName(screen),
            SourceStatus = renderer.GetSourceStatus(screen),
        };
        adapter.PopulateSourceStateResponse(response, screen, source, playback);
        return response;
    }

    public static bool TryResolveForIpc(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        if (ScreenPlacementResolver.TryResolve(placement, out resolved))
            return true;

        resolved = default;
        return false;
    }

    private ScreenPlaybackStateDto BuildBrowserPlaybackState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        return BrowserSourceIpcAdapters.Get(screen.ProviderKind).BuildPlaybackState(screen, telemetry);
    }

    private ScreenSourceState BuildBrowserSourceState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        return BrowserSourceIpcAdapters.Get(screen.ProviderKind).BuildSourceState(screen, telemetry);
    }
}
