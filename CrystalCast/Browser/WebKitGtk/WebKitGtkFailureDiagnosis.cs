namespace CrystalCast.Browser.WebKitGtk;

/// <summary>
/// Turns native helper log text into a short, actionable reason for the screen status.
/// The full log is written to the Dalamud log separately.
/// </summary>
internal static class WebKitGtkFailureDiagnosis
{
    private const string InstallWebKitGtk =
        "Install WebKitGTK 6.0: libwebkitgtk-6.0-4 (Ubuntu/Debian), webkitgtk6.0 (Fedora) or webkitgtk-6.0 (Arch).";

    public static string Explain(string? logText, bool logWritten, bool insideFlatpak, bool insideSteamRuntime = false)
    {
        var flatpak = insideSteamRuntime
            ? " The game runs inside Steam's Linux Runtime container (Proton), which does not include WebKitGTK from your system, so the native browser cannot start there yet. A launcher that runs Wine directly (such as XIVLauncher.Core's own Wine) works, or use WebView2."
            : insideFlatpak
                ? " The game runs inside a Flatpak sandbox, which usually has no WebKitGTK; the native browser may not work there."
                : string.Empty;

        if (!logWritten)
        {
            return "Wine could not start the Linux helper (no log was written). Some launchers or runtimes do not allow Wine to run Linux programs."
                + flatpak;
        }

        var log = logText ?? string.Empty;
        var missingLibrary = FindMissingLibrary(log);
        if (missingLibrary != null)
        {
            if (missingLibrary.StartsWith("libwebkitgtk-6.0", StringComparison.Ordinal)
                || missingLibrary.StartsWith("libjavascriptcoregtk-6.0", StringComparison.Ordinal))
                return "WebKitGTK 6.0 is not installed on this Linux system. " + InstallWebKitGtk + flatpak;

            if (missingLibrary.StartsWith("libgtk-4", StringComparison.Ordinal))
                return "GTK 4 is not installed on this Linux system. " + InstallWebKitGtk + flatpak;

            return $"A Linux library is missing: {missingLibrary}." + flatpak;
        }

        if (Contains(log, "GLIBC_") && Contains(log, "not found"))
            return "This Linux system is too old for the helper (needs glibc 2.39 or newer, for example Ubuntu 24.04, Fedora 40 or current Arch).";

        if (Contains(log, "Exec format error"))
            return "The helper is built for x86-64 Linux and cannot run on this system.";

        if (Contains(log, "Permission denied") && !Contains(log, "bwrap"))
            return "Linux refused to run the helper. The plugin folder may be on a drive mounted without execute permission.";

        if (Contains(log, "bwrap") || Contains(log, "bubblewrap") || Contains(log, "namespace"))
            return "WebKitGTK's security sandbox could not start. On Ubuntu 24.04 and newer, AppArmor can block it; WebKitGTK's own sandbox profile may be missing.";

        if (Contains(log, "could not open the Linux desktop display") || Contains(log, "cannot open display"))
            return "The helper could not open the Linux desktop display. DISPLAY or WAYLAND_DISPLAY may not reach programs started by Wine.";

        if (Contains(log, "could not connect to the plugin"))
            return "The helper started but could not connect back to the plugin over 127.0.0.1.";

        var exit = DescribeExitCode(log);
        if (exit != null)
            return exit + " See the Dalamud log (/xllog) for the helper's output.";

        return "The native browser stopped. See the Dalamud log (/xllog) for the helper's output." + flatpak;
    }

    /// <summary>launch.sh appends "CRYSTALCAST_HOST_EXIT n"; shells report signals as 128+n.</summary>
    private static string? DescribeExitCode(string log)
    {
        const string marker = "CRYSTALCAST_HOST_EXIT ";
        var index = log.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = start;
        while (end < log.Length && char.IsAsciiDigit(log[end]))
            end++;
        if (!int.TryParse(log.AsSpan(start, end - start), out var code))
            return null;

        return code switch
        {
            0 => "The native browser closed normally.",
            137 => "The native browser was killed (signal 9). Linux may have run out of memory.",
            139 => "The native browser crashed (segmentation fault).",
            134 => "The native browser aborted (often a GTK or WebKit error; see its log).",
            143 => "The native browser was asked to stop (signal 15).",
            > 128 and < 160 => $"The native browser was stopped by signal {code - 128}.",
            _ => $"The native browser exited with code {code}.",
        };
    }

    private static string? FindMissingLibrary(string log)
    {
        const string marker = "error while loading shared libraries: ";
        var index = log.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = log.IndexOf(':', start);
        var name = (end < 0 ? log[start..] : log[start..end]).Trim();
        return name.Length is > 0 and <= 128 ? name : "unknown";
    }

    private static bool Contains(string log, string value) => log.Contains(value, StringComparison.OrdinalIgnoreCase);
}
