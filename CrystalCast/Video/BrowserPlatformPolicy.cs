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

    /// <summary>
    /// The native WebKitGTK backend renders provider player pages only. Generic Web keeps WebView2.
    /// </summary>
    public static bool UsesWebKitGtk(BrowserMediaEngine engine, bool isWine, BrowserSourceProviderKind provider)
    {
        return isWine
            && BrowserMediaEnginePolicy.Normalize(engine) == BrowserMediaEngine.WebKitGtk
            && provider != BrowserSourceProviderKind.GenericWeb;
    }
}

internal static class WineWebView2SetupPolicy
{
    /// <summary>
    /// The native WebKitGTK backend does not need WebView2 for provider players, so the
    /// automatic setup prompt stays closed when it is selected.
    /// </summary>
    public static bool ShouldShow(bool isWine, bool dismissed, bool runtimeAvailable, bool nativeBrowserSelected = false)
    {
        return isWine && !dismissed && !runtimeAvailable && !nativeBrowserSelected;
    }
}

internal sealed record WineDetection(bool IsWine, bool ExportsVisible, string Description);

/// <summary>
/// Detects Wine and Proton. Some Proton and Wine builds hide Wine's own exports
/// (wine_get_version and friends) from Windows programs, so the Wine registry key that
/// every prefix has is checked too. Detection never throws.
/// </summary>
internal static class WineEnvironment
{
    private static readonly Lazy<WineDetection> Detection = new(Detect);

    public static bool IsWine => Detection.Value.IsWine;

    /// <summary>False when Wine hides its helper exports; path conversion then uses Z: instead.</summary>
    public static bool ExportsVisible => Detection.Value.ExportsVisible;

    public static string Description => Detection.Value.Description;

    internal static WineDetection Evaluate(bool exportsVisible, bool registryKeyFound, bool protonEnvironment)
    {
        var isWine = exportsVisible || registryKeyFound;
        var kind = protonEnvironment ? "Proton" : "Wine";
        var description = !isWine
            ? "not detected"
            : exportsVisible
                ? $"{kind}; Wine functions visible"
                : $"{kind}; detected from the Wine registry key, Wine functions are hidden from Windows programs";
        return new WineDetection(isWine, exportsVisible, description);
    }

    private static WineDetection Detect()
    {
        if (!OperatingSystem.IsWindows())
            return Evaluate(false, false, false);

        return Evaluate(HasWineExport(), HasWineRegistryKey(), HasProtonEnvironment());
    }

    private static bool HasWineExport()
    {
        try
        {
            // GetModuleHandle does not change ntdll's reference count; nothing is loaded or freed.
            var ntdll = GetModuleHandleW("ntdll.dll");
            return ntdll != 0 && GetProcAddress(ntdll, "wine_get_version") != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasWineRegistryKey()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine");
            if (user != null)
                return true;

            using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Wine");
            return machine != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasProtonEnvironment()
    {
        try
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH"));
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string procName);
}
