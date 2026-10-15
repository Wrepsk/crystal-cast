using System.Text;
using System.Text.RegularExpressions;

namespace CrystalCast.Video;

internal enum LinuxInstallFamily { Unknown, Arch, Debian, Fedora, SteamOs, ImmutableFedora }

internal sealed record LinuxDistribution(string Id, string Name, string Version, string Variant,
    string[] Like, string Source)
{
    public static LinuxDistribution Unknown { get; } = new("", "Unknown Linux distribution", "", "", [], "unavailable");

    public static LinuxDistribution Parse(string text, string source = "os-release")
    {
        if (text.Length > 16384) return Unknown;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || equals <= 0) continue;
            var key = trimmed[..equals];
            if (key is not ("ID" or "ID_LIKE" or "PRETTY_NAME" or "NAME" or "VERSION_ID" or "VARIANT_ID")) continue;
            var value = trimmed[(equals + 1)..].Trim();
            if (value.Length > 512) continue;
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var quote = value[0];
                if (value.Length < 2 || value[^1] != quote) continue;
                value = value[1..^1];
                if (quote == '"')
                {
                    var decoded = new StringBuilder();
                    for (var i = 0; i < value.Length; i++)
                    {
                        if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or '"' or '$' or '`') i++;
                        decoded.Append(value[i]);
                    }
                    value = decoded.ToString();
                }
            }
            // os-release is data, never shell code. No substitutions are evaluated.
            values[key] = new string(value.Where(c => !char.IsControl(c)).ToArray());
        }
        static bool ValidId(string id) => Regex.IsMatch(id, "\\A[a-z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant);
        var id = values.GetValueOrDefault("ID", "");
        if (!ValidId(id)) return Unknown;
        var like = values.GetValueOrDefault("ID_LIKE", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(ValidId).ToArray();
        var name = values.GetValueOrDefault("PRETTY_NAME", values.GetValueOrDefault("NAME", id));
        return new(id, name[..Math.Min(name.Length, 120)], values.GetValueOrDefault("VERSION_ID", ""),
            values.GetValueOrDefault("VARIANT_ID", ""), like, source);
    }

    public static LinuxDistribution Detect(Func<string, string?>? read = null)
    {
        read ??= ReadWineFile;
        // Prefer exposed host metadata when Wine itself is in a launcher sandbox.
        string[] paths = ["/run/host/etc/os-release", "/run/host/os-release", "/etc/os-release", "/usr/lib/os-release"];
        foreach (var path in paths)
        {
            try
            {
                var text = read(path);
                if (text == null) continue;
                var distro = Parse(text, path);
                if (distro.Id.Length > 0) return distro;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return Unknown;
    }

    private static string? ReadWineFile(string path)
    {
        if (!WineEnvironment.IsWine) return null;
        using var stream = new FileStream(LinuxBrowserLauncher.ToWinePath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 16384) return null;
        using var reader = new StreamReader(stream);
        var buffer = new char[16385];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return count > 16384 ? null : new string(buffer, 0, count);
    }
}

internal sealed record LinuxBrowserInstallationGuide(LinuxInstallFamily Family, string Title, string Explanation,
    string? PackageCommand, string? RepositoryCheck)
{
    public const string VersionCheck = "pkg-config --atleast-version=2.54 wpe-webkit-2.0 && pkg-config --exists wpe-platform-2.0 json-glib-1.0 gstreamer-1.0";
    public const string HelperInstall = "cd /path/to/crystal-cast/NativeLinuxBrowser &&\n" + VersionCheck
        + " &&\nmake && make install PREFIX=\"$HOME/.local\" &&\n\"$HOME/.local/bin/crystalcast-browser-helper\" --diagnose";

    public static LinuxBrowserInstallationGuide For(LinuxDistribution distro)
    {
        var id = distro.Id;
        // Immutable hosts must never inherit a mutable package-manager recipe.
        if (id is "steamos" or "holo")
            return new(LinuxInstallFamily.SteamOs, "SteamOS / Steam Deck",
                "A supported WPE 2.54+ user-space installation route is still needed. Do not disable SteamOS read-only protection or install Arch system packages. Use WebView2 meanwhile.", null, null);
        if (id is "bazzite" or "bluefin" or "aurora" or "silverblue" or "kinoite" or "fedora-coreos"
            || distro.Variant is "silverblue" or "kinoite" or "sericea" or "onyx" or "coreos"
            || distro.Variant.Contains("atomic", StringComparison.OrdinalIgnoreCase))
            return new(LinuxInstallFamily.ImmutableFedora, "Fedora Atomic / immutable system",
                "The normal Fedora dnf recipe does not apply to this immutable host. A maintained host-accessible WPE/helper installation route has not been validated. Use WebView2 meanwhile.", null, null);
        if (id is "" or "steamrt" or "org.freedesktop.platform" or "org.gnome.platform" or "org.kde.platform")
            return Unknown();
        bool IsFamily(params string[] ids) => ids.Contains(id) || distro.Like.Any(ids.Contains);
        if (IsFamily("arch"))
            return new(LinuxInstallFamily.Arch, "Arch-family dependencies",
                "The Arch package route was tested with WPE 2.54.1. Derivatives may lag behind; the version check must pass before building the helper.",
                "sudo pacman -Syu --needed gcc make pkgconf wpewebkit gst-plugins-base gst-plugins-good gst-plugins-bad gst-plugins-ugly gst-libav", null);
        if (IsFamily("ubuntu", "debian"))
            return new(LinuxInstallFamily.Debian, "Ubuntu / Debian build and media prerequisites",
                "This command installs build tools and GStreamer, not WPE. Ubuntu's available legacy WPE packages do not satisfy this helper. A maintained WPE 2.54+ build with WPEPlatform 2.0 is required separately; there is no verified complete apt installation command yet.",
                "sudo apt-get update && sudo apt-get install build-essential pkg-config libjson-glib-dev libgstreamer1.0-dev gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav",
                "apt-cache search wpewebkit");
        if (IsFamily("fedora"))
            return new(LinuxInstallFamily.Fedora, "Fedora build prerequisites",
                "These build prerequisites do not install WPE. Check whether your enabled repositories provide both required WPE pkg-config modules at version 2.54+. A complete Fedora WPE installation route is not verified; do not substitute GTK or legacy WPE packages.",
                "sudo dnf install gcc make pkgconf-pkg-config json-glib-devel gstreamer1-devel",
                "dnf provides 'pkgconfig(wpe-webkit-2.0)'\ndnf provides 'pkgconfig(wpe-platform-2.0)'");
        return Unknown();
    }

    private static LinuxBrowserInstallationGuide Unknown() => new(LinuxInstallFamily.Unknown, "Manual dependency setup",
        "The host distribution could not be matched to a verified installation recipe. A launcher sandbox may expose its own runtime instead of the host. Install matching WPE 2.54+/WPEPlatform 2.0 and GStreamer through your distribution's maintained packages; no guessed package command is shown.", null, null);
}
