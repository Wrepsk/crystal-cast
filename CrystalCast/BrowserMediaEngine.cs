namespace CrystalCast;

public enum BrowserMediaEngine
{
    Auto = 0,
    WebView2Capture = 2,
    WebView2WindowCapture = 3,
    /// <summary>Experimental native Linux WebKitGTK browser; only used under Wine.</summary>
    WebKitGtk = 4,
}

/// <summary>Largest frame size WebView2 JPEG capture asks the browser for.</summary>
public enum JpegCaptureResolution
{
    Full1080p = 0,
    /// <summary>Half the pixels: less CPU for encoding and decoding, mainly for slower Wine/Linux machines.</summary>
    Reduced720p = 1,
}

internal static class BrowserMediaEnginePolicy
{
    public static JpegCaptureResolution Normalize(JpegCaptureResolution resolution)
    {
        return resolution is JpegCaptureResolution.Full1080p or JpegCaptureResolution.Reduced720p
            ? resolution
            : JpegCaptureResolution.Full1080p;
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
