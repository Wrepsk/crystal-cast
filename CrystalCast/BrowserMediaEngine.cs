namespace CrystalCast;

public enum BrowserMediaEngine
{
    Auto = 0,
    WebView2Capture = 2,
    WebView2WindowCapture = 3,
    /// <summary>Experimental native Linux WebKitGTK browser; only used under Wine.</summary>
    WebKitGtk = 4,
}

/// <summary>
/// Largest frame size WebView2 JPEG capture asks the browser for. Frames never exceed the screen's own
/// resolution. The numbers are saved in the configuration, so existing values must not change.
/// </summary>
public enum JpegCaptureResolution
{
    Full1080p = 0,
    /// <summary>Half the pixels: less CPU for encoding and decoding, mainly for slower Wine/Linux machines.</summary>
    Reduced720p = 1,
    /// <summary>About half of 720p again, for weak machines or many screens.</summary>
    Low480p = 2,
    /// <summary>About 1.8 times the pixels of 1080p.</summary>
    High1440p = 3,
    /// <summary>No limit: frames match the screen's resolution, up to 4K.</summary>
    ScreenSize = 4,
}

internal static class BrowserMediaEnginePolicy
{
    public static JpegCaptureResolution Normalize(JpegCaptureResolution resolution)
    {
        return Enum.IsDefined(resolution) ? resolution : JpegCaptureResolution.Full1080p;
    }

    public static BrowserMediaEngine Normalize(BrowserMediaEngine engine)
    {
        return engine is BrowserMediaEngine.Auto
            or BrowserMediaEngine.WebView2Capture
            or BrowserMediaEngine.WebView2WindowCapture
            or BrowserMediaEngine.WebKitGtk
            ? engine
            : BrowserMediaEngine.Auto;
    }
}
