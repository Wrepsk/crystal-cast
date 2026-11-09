namespace CrystalCast.Video;

/// <summary>How big and how good the DevTools screencast frames should be.</summary>
internal readonly record struct WebView2ScreencastSettings(bool Enabled, int MaxWidth, int MaxHeight, int Quality, string Reason);

/// <summary>
/// Picks the screencast frame size for WebView2's JPEG capture mode. Chromium scales each frame to fit
/// the limits, so this only caps the JPEG size; the browser still lays the page out at the screen size.
/// 1080p is the default everywhere; 720p halves the encode and decode work for slower machines.
/// </summary>
internal static class WebView2ScreencastPolicy
{
    /// <summary>Test switch: "off" (or "0") uses screenshots, "720" or "1080" overrides the setting.</summary>
    public const string OverrideVariable = "CRYSTALCAST_WEBVIEW2_SCREENCAST";

    public const int Quality = 80;

    public static WebView2ScreencastSettings Resolve(int width, int height, JpegCaptureResolution resolution, string? overrideValue)
    {
        var value = overrideValue?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value is "0" or "off" or "false" or "no")
            return new WebView2ScreencastSettings(false, 0, 0, Quality, $"{OverrideVariable}={overrideValue!.Trim()}");

        var (capWidth, capHeight, reason) = value switch
        {
            "720" or "720p" => (1280, 720, $"{OverrideVariable}={overrideValue!.Trim()}"),
            "1080" or "1080p" => (1920, 1080, $"{OverrideVariable}={overrideValue!.Trim()}"),
            _ when resolution == JpegCaptureResolution.Reduced720p => (1280, 720, "720p setting"),
            _ => (1920, 1080, "1080p setting"),
        };

        return new WebView2ScreencastSettings(
            true,
            Math.Clamp(width, 1, capWidth),
            Math.Clamp(height, 1, capHeight),
            Quality,
            reason);
    }
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
