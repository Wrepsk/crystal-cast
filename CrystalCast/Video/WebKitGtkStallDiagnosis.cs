namespace CrystalCast.Video;

/// <summary>One two-second health report from the native helper.</summary>
internal sealed record WebKitGtkHostHealth(
    long Invalidations,
    long Frames,
    long BusyDrops,
    bool Mapped,
    bool Minimized,
    bool Suspended,
    bool CaptureEnabled,
    bool PageReady)
{
    public string Summary =>
        $"last 2 s: {Invalidations} redraws, {Frames} frames, {BusyDrops} frames dropped while slots were full; " +
        $"window shown {YesNo(Mapped)}, minimized {YesNo(Minimized)}, suspended by the desktop {YesNo(Suspended)}";

    private static string YesNo(bool value) => value ? "yes" : "no";
}

/// <summary>
/// Explains why a playing video stopped producing frames, from the helper's latest health report.
/// </summary>
internal static class WebKitGtkStallDiagnosis
{
    public static string Explain(WebKitGtkHostHealth? health, string windowMode)
    {
        if (health == null)
            return "the native browser has not reported its state yet";

        if (!health.Mapped)
            return "the browser window is not shown, so WebKit stops drawing. Restart the screen; if it keeps happening, report your desktop";

        if (health.Minimized)
            return "the browser window is minimized, so WebKit stops drawing. Restore the \"CrystalCast browser\" window (it can stay behind the game)";

        if (health.Suspended)
            return $"your desktop paused the browser window because it is covered or off-screen ({windowMode}). Please report your desktop and session type";

        if (!health.PageReady)
            return "the player page has not finished loading";

        if (health.Invalidations == 0)
        {
            return windowMode == "wayland"
                ? "WebKit is not drawing new frames. On Wayland the desktop may pause covered windows; please report your desktop"
                : $"WebKit is not drawing new frames although the video is playing ({windowMode} window). The desktop may pause covered windows, or video decoding stalled; please report your desktop and the Dalamud log";
        }

        if (health.Frames == 0 && health.BusyDrops > 0)
            return "the browser is drawing, but the plugin is not taking its frames (all frame slots are full)";

        if (health.Frames == 0)
            return "the browser is drawing, but capturing its frames failed; see the Dalamud log (/xllog)";

        return "frames are arriving again";
    }
}
