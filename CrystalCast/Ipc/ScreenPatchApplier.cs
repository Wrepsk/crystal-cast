namespace CrystalCast.Sync;

internal static class ScreenPatchApplier
{
    public static bool TryApplyScreenMutation(
        BrowserScreenProfile current,
        ScreenIpcMutationRequest request,
        out BrowserScreenProfile updated,
        out string error)
    {
        updated = current.Clone();
        if (!ApplyScreenMutationInPlace(updated, request, out error))
        {
            updated = current;
            return false;
        }

        return true;
    }

    public static bool TryApplySourceMutation(
        BrowserScreenProfile current,
        ScreenIpcSourceUpdateRequest request,
        out BrowserScreenProfile updated,
        out string error)
    {
        updated = current.Clone();
        if (!TrySelectProvider(updated, request.Provider, out error)
            || !BrowserSourceIpcAdapters.Get(updated.ProviderKind).ApplyPatch(updated, request.Source, out error))
        {
            updated = current;
            return false;
        }

        return true;
    }

    public static void ApplySourceLock(BrowserScreenProfile screen, ScreenIpcSourceLockRequest request)
    {
        screen.SourceControlsLocked = request.Locked;
        screen.SourceControlsOwnerId = request.Locked
            ? IpcJsonService.FirstNonEmpty(request.OwnerId, screen.SourceControlsOwnerId, screen.IpcOwnerId)
            : string.Empty;
    }

    private static bool ApplyScreenMutationInPlace(
        BrowserScreenProfile screen,
        ScreenIpcMutationRequest request,
        out string error)
    {
        if (!TrySelectProvider(screen, request.Provider, out error))
            return false;

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

        // Only a plugin's own screen has this setting; the user's screens ignore it.
        if (request.PlaylistAllowUserAdds is { } allowUserAdds && screen.CreatedByIpc)
            screen.PlaylistAllowUserAdds = allowUserAdds;

        ApplyPlacementPatch(screen.Placement, request.Placement);
        return BrowserSourceIpcAdapters.Get(screen.ProviderKind).ApplyPatch(screen, request.Source, out error);
    }

    /// <summary>Switches the screen to <paramref name="provider"/> when one is named; a provider left out keeps the current one.</summary>
    private static bool TrySelectProvider(BrowserScreenProfile screen, BrowserSourceProviderKind? provider, out string error)
    {
        error = string.Empty;
        if (provider is not { } requested)
            return true;

        if (!BrowserSourceIpcAdapters.IsSupported(requested))
        {
            error = UnsupportedProviderMessage(requested);
            return false;
        }

        screen.ProviderKind = requested;
        return true;
    }

    public static string UnsupportedProviderMessage(BrowserSourceProviderKind provider)
    {
        return $"Unsupported browser source provider '{provider}'.";
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
}
