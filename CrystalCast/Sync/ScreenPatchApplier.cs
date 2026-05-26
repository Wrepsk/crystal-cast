namespace CrystalCast.Sync;

internal static class ScreenPatchApplier
{
    public static bool ApplyScreenMutation(
        BrowserScreenProfile screen,
        ScreenIpcMutationRequest request,
        out string error)
    {
        error = string.Empty;
        if (request.Provider is { } provider && !IsSupportedBrowserProvider(provider))
        {
            error = $"Unsupported browser source provider '{provider}'.";
            return false;
        }

        screen.ProviderKind = ResolveRequestedProvider(screen, request.Provider, request);

        var name = IpcJsonService.NormalizeText(request.Name);
        if (!string.IsNullOrWhiteSpace(name))
            screen.Name = name;

        var ownerId = IpcJsonService.NormalizeText(request.OwnerId);
        if (!string.IsNullOrWhiteSpace(ownerId))
            screen.IpcOwnerId = ownerId;

        if (request.Enabled.HasValue)
            screen.Enabled = request.Enabled.Value;

        var sourceOwner = IpcJsonService.NormalizeText(request.SourceControlsOwnerId);
        if (!string.IsNullOrWhiteSpace(sourceOwner))
            screen.SourceControlsOwnerId = sourceOwner;

        if (request.SourceControlsLocked.HasValue)
        {
            screen.SourceControlsLocked = request.SourceControlsLocked.Value;
            screen.SourceControlsOwnerId = screen.SourceControlsLocked
                ? IpcJsonService.FirstNonEmpty(screen.SourceControlsOwnerId, screen.IpcOwnerId, ownerId)
                : string.Empty;
        }

        ApplyPlacementPatch(screen.Placement, request.Placement);
        return ApplyProviderPatch(screen, screen.ProviderKind, request, out error);
    }

    public static void ApplySourceLock(BrowserScreenProfile screen, ScreenIpcSourceLockRequest request)
    {
        screen.SourceControlsLocked = request.Locked;
        screen.SourceControlsOwnerId = request.Locked
            ? IpcJsonService.FirstNonEmpty(request.OwnerId, screen.SourceControlsOwnerId, screen.IpcOwnerId)
            : string.Empty;
    }

    public static bool IsSupportedBrowserProvider(BrowserSourceProviderKind provider)
    {
        return BrowserSourceIpcAdapters.IsSupported(provider);
    }

    public static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind? provider,
        ScreenIpcMutationRequest request)
    {
        return BrowserSourceIpcAdapters.ResolveRequestedProvider(screen, provider, request);
    }

    public static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind? provider,
        ScreenIpcSourceUpdateRequest request)
    {
        return BrowserSourceIpcAdapters.ResolveRequestedProvider(screen, provider, request);
    }

    public static bool ApplyProviderPatch(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind provider,
        ScreenIpcMutationRequest request,
        out string error)
    {
        if (!BrowserSourceIpcAdapters.IsSupported(provider))
            return UnsupportedProvider(provider, out error);

        return BrowserSourceIpcAdapters.Get(provider).ApplyPatch(screen, request, out error);
    }

    public static bool ApplyProviderPatch(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind provider,
        ScreenIpcSourceUpdateRequest request,
        out string error)
    {
        if (!BrowserSourceIpcAdapters.IsSupported(provider))
            return UnsupportedProvider(provider, out error);

        return BrowserSourceIpcAdapters.Get(provider).ApplyPatch(screen, request, out error);
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

    private static bool UnsupportedProvider(BrowserSourceProviderKind provider, out string error)
    {
        error = $"Unsupported browser source provider '{provider}'.";
        return false;
    }

}
