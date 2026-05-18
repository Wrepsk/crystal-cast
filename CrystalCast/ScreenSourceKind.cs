namespace CrystalCast;

public enum ScreenSourceKind
{
    StaticImage = 0,
    Generated = 1,
    LocalVideo = 2,
    YouTubeBrowser = 3,
    BrowserCapture = 4,
    OffscreenBrowser = 5,
}

public enum ScreenPlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
}
