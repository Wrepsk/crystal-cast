namespace CrystalCast;

public enum ScreenSourceKind
{
    StaticImage = 0,
    Generated = 1,
    LocalVideo = 2,
    BrowserCapture = 3,
    OffscreenBrowser = 4,
}

public enum ScreenPlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
}
