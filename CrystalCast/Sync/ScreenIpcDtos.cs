namespace CrystalCast.Sync;

public sealed class ScreenIpcMutationRequest
{
    public string ScreenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
    public bool Activate { get; set; }
    public BrowserSourceProviderKind? Provider { get; set; }
    public bool? SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public ScreenPlacementPatchDto? Placement { get; set; }
    public YouTubeScreenPatchDto? YouTube { get; set; }
    public TwitchScreenPatchDto? Twitch { get; set; }
    public DailymotionScreenPatchDto? Dailymotion { get; set; }
    public VimeoScreenPatchDto? Vimeo { get; set; }
    public GenericWebScreenPatchDto? GenericWeb { get; set; }
}

public sealed class ScreenIpcSourceLockRequest
{
    public string ScreenId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public bool Locked { get; set; }
}

public sealed class ScreenIpcSourceUpdateRequest
{
    public string ScreenId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public bool Activate { get; set; }
    public BrowserSourceProviderKind? Provider { get; set; }
    public YouTubeScreenPatchDto? YouTube { get; set; }
    public TwitchScreenPatchDto? Twitch { get; set; }
    public DailymotionScreenPatchDto? Dailymotion { get; set; }
    public VimeoScreenPatchDto? Vimeo { get; set; }
    public GenericWebScreenPatchDto? GenericWeb { get; set; }
}

public sealed class ScreenIpcMutationResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public bool Created { get; set; }
    public bool Updated { get; set; }
    public string ScreenId { get; set; } = string.Empty;
    public ScreenIpcScreenSummary? Screen { get; set; }
}

public sealed class ScreenIpcSourceStateResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ScreenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool CreatedByIpc { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public bool SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public ScreenSourceKind SourceKind { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceStatus { get; set; } = string.Empty;
    public YouTubeSourceStateDto? YouTube { get; set; }
    public TwitchSourceStateDto? Twitch { get; set; }
    public DailymotionSourceStateDto? Dailymotion { get; set; }
    public VimeoSourceStateDto? Vimeo { get; set; }
    public GenericWebSourceStateDto? GenericWeb { get; set; }
}

public sealed class ScreenIpcScreenSummary
{
    public string ScreenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool CreatedByIpc { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public bool SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public ScreenPlacementStateDto Placement { get; set; } = new();
}

public sealed class ScreenPlacementPatchDto
{
    public ScreenPlacementMode? Mode { get; set; }
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? PositionZ { get; set; }
    public float? YawRadians { get; set; }
    public float? PitchRadians { get; set; }
    public float? RollRadians { get; set; }
    public float? WidthMeters { get; set; }
    public float? HeightMeters { get; set; }
    public float? ScreenCurveAmountMeters { get; set; }
    public float? OccludedAlpha { get; set; }
    public float? OcclusionTolerance { get; set; }
    public bool? DistanceFadeEnabled { get; set; }
    public float? FadeStartMeters { get; set; }
    public float? FadeStopMeters { get; set; }
}

public sealed class ScreenPlacementStateDto
{
    public ScreenPlacementMode Mode { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float YawRadians { get; set; }
    public float PitchRadians { get; set; }
    public float RollRadians { get; set; }
    public float WidthMeters { get; set; }
    public float HeightMeters { get; set; }
    public float ScreenCurveAmountMeters { get; set; }
    public float OccludedAlpha { get; set; }
    public float OcclusionTolerance { get; set; }
    public bool DistanceFadeEnabled { get; set; }
    public float FadeStartMeters { get; set; }
    public float FadeStopMeters { get; set; }
}

public sealed class YouTubeScreenPatchDto
{
    public string? Url { get; set; }
    public bool? PlaybackPaused { get; set; }
    public long? PositionMs { get; set; }
    public bool Restart { get; set; }
    public bool? Autoplay { get; set; }
    public bool? Loop { get; set; }
    public bool? PlaylistAutoplayNext { get; set; }
    public float? PlaybackRate { get; set; }
    public int? BrowserWidth { get; set; }
    public int? BrowserHeight { get; set; }
    public float? CaptureFps { get; set; }
    public bool? CaptureFpsManual { get; set; }
    public bool? AudioEnabled { get; set; }
    public float? Volume { get; set; }
    public bool? SpatialAudioEnabled { get; set; }
    public float? SpatialAudioFullVolumeRadiusMeters { get; set; }
    public float? SpatialAudioSilentRadiusMeters { get; set; }
}

public sealed class YouTubeSourceStateDto
{
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

public sealed class TwitchScreenPatchDto
{
    public string? Url { get; set; }
    public bool? PlaybackPaused { get; set; }
    public long? PositionMs { get; set; }
    public bool Restart { get; set; }
    public bool? Autoplay { get; set; }
    public int? BrowserWidth { get; set; }
    public int? BrowserHeight { get; set; }
    public float? CaptureFps { get; set; }
    public bool? CaptureFpsManual { get; set; }
    public bool? AudioEnabled { get; set; }
    public float? Volume { get; set; }
    public bool? SpatialAudioEnabled { get; set; }
    public float? SpatialAudioFullVolumeRadiusMeters { get; set; }
    public float? SpatialAudioSilentRadiusMeters { get; set; }
}

public sealed class TwitchSourceStateDto
{
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

public sealed class DailymotionScreenPatchDto
{
    public string? Url { get; set; }
    public bool? PlaybackPaused { get; set; }
    public long? PositionMs { get; set; }
    public bool Restart { get; set; }
    public bool? Autoplay { get; set; }
    public bool? Loop { get; set; }
    public int? BrowserWidth { get; set; }
    public int? BrowserHeight { get; set; }
    public float? CaptureFps { get; set; }
    public bool? CaptureFpsManual { get; set; }
    public bool? AudioEnabled { get; set; }
    public float? Volume { get; set; }
    public bool? SpatialAudioEnabled { get; set; }
    public float? SpatialAudioFullVolumeRadiusMeters { get; set; }
    public float? SpatialAudioSilentRadiusMeters { get; set; }
}

public sealed class DailymotionSourceStateDto
{
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string PlaylistId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

public sealed class VimeoScreenPatchDto
{
    public string? Url { get; set; }
    public bool? PlaybackPaused { get; set; }
    public long? PositionMs { get; set; }
    public bool Restart { get; set; }
    public bool? Autoplay { get; set; }
    public bool? Loop { get; set; }
    public float? PlaybackRate { get; set; }
    public int? BrowserWidth { get; set; }
    public int? BrowserHeight { get; set; }
    public float? CaptureFps { get; set; }
    public bool? CaptureFpsManual { get; set; }
    public bool? AudioEnabled { get; set; }
    public float? Volume { get; set; }
    public bool? SpatialAudioEnabled { get; set; }
    public float? SpatialAudioFullVolumeRadiusMeters { get; set; }
    public float? SpatialAudioSilentRadiusMeters { get; set; }
}

public sealed class VimeoSourceStateDto
{
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

public sealed class GenericWebScreenPatchDto
{
    public string? Url { get; set; }
    public bool? PlaybackPaused { get; set; }
    public long? PositionMs { get; set; }
    public bool Restart { get; set; }
    public bool? Autoplay { get; set; }
    public bool? Loop { get; set; }
    public float? PlaybackRate { get; set; }
    public int? BrowserWidth { get; set; }
    public int? BrowserHeight { get; set; }
    public float? CaptureFps { get; set; }
    public bool? CaptureFpsManual { get; set; }
    public bool? AudioEnabled { get; set; }
    public float? Volume { get; set; }
    public bool? SpatialAudioEnabled { get; set; }
    public float? SpatialAudioFullVolumeRadiusMeters { get; set; }
    public float? SpatialAudioSilentRadiusMeters { get; set; }
}

public sealed class GenericWebSourceStateDto
{
    public string Url { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Stopped;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

/// <summary>Answer of <c>CrystalCast.GetCapabilities</c>.</summary>
public sealed class ScreenIpcCapabilities
{
    public int SchemaVersion { get; set; } = 1;
    public int ApiVersion { get; set; }
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Whether CrystalCast's screens are switched on; IPC calls work either way.</summary>
    public bool Enabled { get; set; }

    public string[] Providers { get; set; } = [];
    public ScreenIpcLimits Limits { get; set; } = new();
    public ScreenIpcBrowserCapabilities Browser { get; set; } = new();
}

public sealed class ScreenIpcLimits
{
    public int MaxUserScreens { get; set; }
    public int MaxIpcScreens { get; set; }
    public int MaxScreens { get; set; }
    public int MaxActiveScreens { get; set; }
    public int MaxRemoteScreens { get; set; }
    public int MaxPayloadBytes { get; set; }
    public int IpcScreensInUse { get; set; }
}

/// <summary>Whether this player can show browser screens, and with what.</summary>
public sealed class ScreenIpcBrowserCapabilities
{
    public bool IsWine { get; set; }
    public string Backend { get; set; } = string.Empty;
    public string CaptureMode { get; set; } = string.Empty;
    public bool NativeLinuxBrowserForProviders { get; set; }
    public bool WebView2Available { get; set; }
    public string WebView2Version { get; set; } = string.Empty;
    public string WebView2Error { get; set; } = string.Empty;
}

public enum ScreenIpcChangeKind
{
    Created = 1,
    Placement = 2,
    Source = 3,
    Playback = 4,
    Visual = 5,
    SourceLock = 6,
    Unavailable = 7,
}

public enum ScreenIpcApplyStateResult
{
    Applied = 1,
    IgnoredSelf = 2,
    IgnoredDuplicate = 3,
    IgnoredStale = 4,
    RejectedInvalid = 5,
    RejectedCapacity = 6,
}

public sealed class ScreenIpcApplyStateResponse
{
    public bool Success { get; set; }
    public ScreenIpcApplyStateResult Result { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ScreenId { get; set; } = string.Empty;
    public string OwnerSessionId { get; set; } = string.Empty;
}

public sealed class ScreenIpcChangeEvent
{
    public int SchemaVersion { get; set; } = 1;
    public string ScreenId { get; set; } = string.Empty;
    public string OwnerSessionId { get; set; } = string.Empty;
    public bool CreatedByIpc { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public bool SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public ScreenIpcChangeKind[] Changes { get; set; } = [];
    public ScreenStateEnvelope? State { get; set; }
    public long TimestampUnixMs { get; set; }
}
