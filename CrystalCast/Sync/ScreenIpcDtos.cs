namespace CrystalCast.Sync;

public sealed class ScreenIpcMutationRequest
{
    public string ScreenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
    public bool Activate { get; set; }

    /// <summary>
    /// <c>Screen.Update</c> only: create the screen with this <see cref="ScreenId"/> when it does not exist,
    /// so a sync plugin can send the same call every time. Refused while the user keeps that screen hidden.
    /// </summary>
    public bool CreateIfMissing { get; set; }
    public BrowserSourceProviderKind? Provider { get; set; }
    public bool? SourceControlsLocked { get; set; }
    public string SourceControlsOwnerId { get; set; } = string.Empty;
    public ScreenPlacementPatchDto? Placement { get; set; }
    public YouTubeScreenPatchDto? YouTube { get; set; }
    public TwitchScreenPatchDto? Twitch { get; set; }
    public DailymotionScreenPatchDto? Dailymotion { get; set; }
    public VimeoScreenPatchDto? Vimeo { get; set; }
    public GenericWebScreenPatchDto? GenericWeb { get; set; }

    /// <summary>
    /// Screens created through IPC only: lets the user add videos. Everything else in that screen's playlist
    /// stays with the plugin.
    /// Leave out to keep the current value; off on new screens.
    /// </summary>
    public bool? PlaylistAllowUserAdds { get; set; }
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

    /// <summary>The user deleted this screen and chose to keep it hidden this session; stop sending it.</summary>
    public bool HiddenByUser { get; set; }
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

    /// <summary>Optional behaviors this version supports, such as <c>createIfMissing</c> and <c>removalReason</c>.</summary>
    public string[] Features { get; set; } = [];

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
    public int MaxPlaylistEntries { get; set; }
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

    /// <summary>Playlist entries, order, play position, or its options changed. Read it with <c>Playlist.Get</c>.</summary>
    Playlist = 8,
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

    /// <summary>Why an <c>Unavailable</c> screen went away; <c>None</c> for every other event.</summary>
    public ScreenIpcRemovalReason RemovalReason { get; set; }

    public ScreenStateEnvelope? State { get; set; }
    public long TimestampUnixMs { get; set; }
}

public enum ScreenIpcRemovalReason
{
    /// <summary>Not a removal, or the screen went away for another reason (IPC turned off, plugin unloading).</summary>
    None = 0,

    /// <summary>A plugin removed it with <c>Screen.Remove</c>.</summary>
    RemovedByIpc = 1,

    /// <summary>The user deleted it; creating it again brings it back.</summary>
    RemovedByUser = 2,

    /// <summary>The user deleted it and keeps it hidden this session; creating it again is refused.</summary>
    HiddenByUser = 3,
}

/// <summary>
/// One request type for every <c>CrystalCast.Screen.Playlist.*</c> call; each call reads the fields it needs.
/// </summary>
public sealed class ScreenIpcPlaylistRequest
{
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Who is calling. Entries are recorded under it, and lock-owner calls compare it to the lock.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Add: a full YouTube, Twitch, Dailymotion, Vimeo or web link.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Add: an optional name for the list. Defaults to the video ID or page address.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Remove and Move: the entry to change.</summary>
    public string EntryId { get; set; } = string.Empty;

    /// <summary>Move: the new zero-based position.</summary>
    public int? Index { get; set; }

    /// <summary>SetOptions: leave out to keep the current value.</summary>
    public bool? Shuffle { get; set; }

    /// <summary>SetOptions: leave out to keep the current value.</summary>
    public bool? Loop { get; set; }
}

public sealed class ScreenIpcPlaylistResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ScreenId { get; set; } = string.Empty;

    /// <summary>Add: the new entry. Skip: the entry now playing.</summary>
    public string EntryId { get; set; } = string.Empty;
    public ScreenIpcPlaylistState? Playlist { get; set; }
}

public sealed class ScreenIpcPlaylistState
{
    public bool Shuffle { get; set; }
    public bool Loop { get; set; }

    /// <summary>
    /// The user's "Let other plugins add videos" switch. IPC can read it but not change it. Always false on
    /// screens created through IPC, which have no such switch.
    /// </summary>
    public bool AllowIpcAdds { get; set; }

    /// <summary>
    /// Whether the user may add videos in the UI. Always true on the
    /// user's own screens; on screens created through IPC the creating plugin sets it.
    /// </summary>
    public bool AllowUserAdds { get; set; }
    public string CurrentEntryId { get; set; } = string.Empty;
    public int MaxEntries { get; set; }
    public ScreenIpcPlaylistEntry[] Entries { get; set; } = [];
}

public sealed class ScreenIpcPlaylistEntry
{
    public string EntryId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>The name to show: the title the adding plugin gave, else the site's, else what the link points at.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The channel or uploader, when the site said.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>An https image on the site's image host, or empty.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Zero when unknown or live.</summary>
    public int DurationSeconds { get; set; }
    public bool AddedByIpc { get; set; }
    public string AddedBy { get; set; } = string.Empty;
    public bool Played { get; set; }
    public bool Current { get; set; }
}
