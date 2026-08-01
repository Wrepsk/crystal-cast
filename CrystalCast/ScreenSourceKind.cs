namespace CrystalCast;

public enum ScreenSourceKind
{
    // Deserialization-only compatibility values for version 1 configuration and IPC payloads.
    [Obsolete("Local video sources are no longer supported.")]
    LocalVideo = 2,
    [Obsolete("Use Browser.")]
    YouTubeBrowser = 3,
    Browser = 3,
}

public enum ScreenPlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
}
