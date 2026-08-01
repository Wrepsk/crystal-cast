namespace CrystalCast;

public enum BrowserMediaEngine
{
    Auto = 0,
    WebView2Capture = 2,
    WebView2WindowCapture = 3,
}

internal static class BrowserMediaEnginePolicy
{
    public static BrowserMediaEngine Normalize(BrowserMediaEngine engine)
    {
        return engine is BrowserMediaEngine.Auto
            or BrowserMediaEngine.WebView2Capture
            or BrowserMediaEngine.WebView2WindowCapture
            ? engine
            : BrowserMediaEngine.Auto;
    }
}
