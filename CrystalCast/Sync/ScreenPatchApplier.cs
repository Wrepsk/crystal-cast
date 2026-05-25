using CrystalCast.Video;

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

        screen.ProviderKind = ResolveRequestedProvider(screen, request.Provider, request.YouTube, request.Twitch);

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
        return ApplyProviderPatch(screen, screen.ProviderKind, request.YouTube, request.Twitch, out error);
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
        return BrowserSourceProviderRegistry.IsSupported(provider);
    }

    public static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind? provider,
        YouTubeScreenPatchDto? youtube,
        TwitchScreenPatchDto? twitch)
    {
        if (provider.HasValue)
            return provider.Value;

        if (twitch != null && youtube == null)
            return BrowserSourceProviderKind.Twitch;

        if (youtube != null && twitch == null)
            return BrowserSourceProviderKind.YouTube;

        return screen.ProviderKind;
    }

    public static bool ApplyProviderPatch(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind provider,
        YouTubeScreenPatchDto? youtube,
        TwitchScreenPatchDto? twitch,
        out string error)
    {
        return provider switch
        {
            BrowserSourceProviderKind.YouTube => ApplyYouTubePatch(screen, youtube, out error),
            BrowserSourceProviderKind.Twitch => ApplyTwitchPatch(screen, twitch, out error),
            _ => UnsupportedProvider(provider, out error),
        };
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

    private static bool ApplyYouTubePatch(BrowserScreenProfile screen, YouTubeScreenPatchDto? patch, out string error)
    {
        error = string.Empty;
        if (patch == null)
            return true;

        if (patch.Url != null)
        {
            var url = patch.Url.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !YouTubeVideoId.TryParseSource(url, out _))
            {
                error = "YouTube URL, video ID, playlist, or live channel is invalid.";
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
        if (patch.PlaylistAutoplayNext.HasValue)
            screen.YouTubePlaylistAutoplayNext = patch.PlaylistAutoplayNext.Value;
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

    private static bool ApplyTwitchPatch(BrowserScreenProfile screen, TwitchScreenPatchDto? patch, out string error)
    {
        error = string.Empty;
        if (patch == null)
            return true;

        if (patch.Url != null)
        {
            var url = patch.Url.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !TwitchVideoId.TryParseSource(url, out _))
            {
                error = "Twitch channel or VOD URL is invalid.";
                return false;
            }

            screen.TwitchUrl = url;
        }

        if (patch.PlaybackPaused.HasValue)
            screen.PlaybackPaused = patch.PlaybackPaused.Value;
        if (patch.Autoplay.HasValue)
            screen.TwitchAutoplay = patch.Autoplay.Value;
        if (patch.BrowserWidth.HasValue)
            screen.TwitchBrowserWidth = patch.BrowserWidth.Value;
        if (patch.BrowserHeight.HasValue)
            screen.TwitchBrowserHeight = patch.BrowserHeight.Value;
        if (patch.CaptureFps.HasValue)
            screen.TwitchCaptureFps = patch.CaptureFps.Value;
        if (patch.CaptureFpsManual.HasValue)
            screen.TwitchCaptureFpsManual = patch.CaptureFpsManual.Value;
        if (patch.AudioEnabled.HasValue)
            screen.TwitchAudioEnabled = patch.AudioEnabled.Value;
        if (patch.Volume.HasValue)
            screen.TwitchVolume = patch.Volume.Value;
        if (patch.SpatialAudioEnabled.HasValue)
            screen.SpatialAudioEnabled = patch.SpatialAudioEnabled.Value;
        if (patch.SpatialAudioFullVolumeRadiusMeters.HasValue)
            screen.SpatialAudioFullVolumeRadiusMeters = patch.SpatialAudioFullVolumeRadiusMeters.Value;
        if (patch.SpatialAudioSilentRadiusMeters.HasValue)
            screen.SpatialAudioSilentRadiusMeters = patch.SpatialAudioSilentRadiusMeters.Value;

        return true;
    }
}
