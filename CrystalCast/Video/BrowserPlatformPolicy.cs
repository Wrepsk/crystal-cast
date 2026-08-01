using System.Runtime.InteropServices;

namespace CrystalCast.Video;

internal static class BrowserPlatformPolicy
{
    public static WebView2CaptureMode ResolveCaptureMode(BrowserMediaEngine engine, bool isWine)
    {
        var normalized = BrowserMediaEnginePolicy.Normalize(engine);
        return isWine || normalized == BrowserMediaEngine.WebView2Capture
            ? WebView2CaptureMode.PreviewJpeg
            : WebView2CaptureMode.WindowGraphicsCapture;
    }
}

internal static class WineWebView2SetupPolicy
{
    public static bool ShouldShow(bool isWine, bool dismissed, bool runtimeAvailable)
    {
        return isWine && !dismissed && !runtimeAvailable;
    }
}

internal static class WineEnvironment
{
    private static readonly Lazy<bool> Detection = new(Detect);

    public static bool IsWine => Detection.Value;

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("ntdll.dll", out var module))
            return false;

        try
        {
            return NativeLibrary.TryGetExport(module, "wine_get_version", out _);
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }
}
