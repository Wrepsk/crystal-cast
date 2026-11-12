namespace CrystalCast.Browser.WebView2;

/// <summary>How big and how good the DevTools screencast frames should be.</summary>
internal readonly record struct WebView2ScreencastSettings(bool Enabled, int MaxWidth, int MaxHeight, int Quality, string Reason);

/// <summary>
/// Picks the screencast frame size for WebView2's JPEG capture mode. Chromium scales each frame to fit
/// the limits, so this only caps the JPEG size; the browser still lays the page out at the screen size.
/// 1080p is the default everywhere. Smaller limits cut the encode and decode work for slower machines;
/// larger ones only matter for screens set above 1080p, and cost CPU in proportion to the pixels.
/// </summary>
internal static class WebView2ScreencastPolicy
{
    /// <summary>
    /// Test switch: "off" (or "0") uses screenshots; "480", "720", "1080", "1440" or "full" overrides the setting.
    /// </summary>
    public const string OverrideVariable = "CRYSTALCAST_WEBVIEW2_SCREENCAST";

    public const int Quality = 80;

    public static WebView2ScreencastSettings Resolve(int width, int height, JpegCaptureResolution resolution, string? overrideValue)
    {
        var value = overrideValue?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value is "0" or "off" or "false" or "no")
            return new WebView2ScreencastSettings(false, 0, 0, Quality, $"{OverrideVariable}={overrideValue!.Trim()}");

        JpegCaptureResolution? overridden = value switch
        {
            "480" or "480p" => JpegCaptureResolution.Low480p,
            "720" or "720p" => JpegCaptureResolution.Reduced720p,
            "1080" or "1080p" => JpegCaptureResolution.Full1080p,
            "1440" or "1440p" => JpegCaptureResolution.High1440p,
            "full" or "max" or "screen" => JpegCaptureResolution.ScreenSize,
            _ => null,
        };
        var chosen = overridden ?? BrowserMediaEnginePolicy.Normalize(resolution);
        var (capWidth, capHeight) = GetLimit(chosen);
        var reason = overridden != null
            ? $"{OverrideVariable}={overrideValue!.Trim()}"
            : $"{Describe(chosen)} setting";

        return new WebView2ScreencastSettings(
            true,
            Math.Clamp(width, 1, capWidth),
            Math.Clamp(height, 1, capHeight),
            Quality,
            reason);
    }

    /// <summary>The largest frame for each setting; screen size means the 4K maximum screens allow.</summary>
    public static (int Width, int Height) GetLimit(JpegCaptureResolution resolution) => resolution switch
    {
        JpegCaptureResolution.Low480p => (854, 480),
        JpegCaptureResolution.Reduced720p => (1280, 720),
        JpegCaptureResolution.High1440p => (2560, 1440),
        JpegCaptureResolution.ScreenSize => (3840, 2160),
        _ => (1920, 1080),
    };

    private static string Describe(JpegCaptureResolution resolution) => resolution switch
    {
        JpegCaptureResolution.Low480p => "480p",
        JpegCaptureResolution.Reduced720p => "720p",
        JpegCaptureResolution.High1440p => "1440p",
        JpegCaptureResolution.ScreenSize => "screen size",
        _ => "1080p",
    };
}

/// <summary>
/// Decides which screencast frames to decode. Screencast pushes frames at the page's repaint rate, with
/// uneven gaps. At display-like capture rates every frame is kept; below that, frames follow a schedule
/// at the capture rate, so a 30 fps target decodes about every other frame of a 60 fps page.
/// </summary>
internal sealed class ScreencastFramePacer
{
    /// <summary>Capture rates at or above this keep every frame; screencast rarely exceeds 60.</summary>
    public const float UnthrottledFps = 50f;

    private double nextDueMs = double.NaN;

    public bool ShouldAccept(double nowMs, float targetFps)
    {
        if (targetFps >= UnthrottledFps || targetFps <= 0)
        {
            nextDueMs = double.NaN;
            return true;
        }

        var interval = 1000.0 / targetFps;
        if (!double.IsNaN(nextDueMs) && nowMs < nextDueMs - interval * 0.5)
            return false;

        // Stay on the schedule after an early or slightly late frame, but never bank time after a pause.
        nextDueMs = double.IsNaN(nextDueMs) || nowMs - nextDueMs > interval
            ? nowMs + interval
            : nextDueMs + interval;
        return true;
    }
}
