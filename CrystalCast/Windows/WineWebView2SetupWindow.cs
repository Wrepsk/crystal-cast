using System.Numerics;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

internal sealed class WineWebView2SetupWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string runtimeStatus = string.Empty;

    public WineWebView2SetupWindow(Plugin plugin)
        : base("CrystalCast Wine Setup###CrystalCastWineWebView2Setup")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = ShouldOpenAutomatically(plugin.Configuration);
    }

    internal static bool ShouldOpenAutomatically(Configuration configuration)
    {
        var runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out _, out _);
        return WineWebView2SetupPolicy.ShouldShow(
            WineEnvironment.IsWine,
            configuration.WineWebView2SetupDismissed,
            runtimeAvailable);
    }

    internal static string GetInstallCommand()
    {
        var prefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = "/path/to/your/ffxiv-wine-prefix";

        return $"WINEPREFIX=\"{prefix}\" winetricks webview2";
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextWrapped("CrystalCast detected Wine. Wine support is experimental and has not yet been validated on a real Linux installation.");
        ImGui.Spacing();
        ImGui.TextWrapped("CrystalCast requires Microsoft Edge WebView2 in the same Wine prefix that runs FINAL FANTASY XIV. Use a recent Winetricks release and run:");
        ImGui.Spacing();
        ImGui.TextWrapped(GetInstallCommand());

        if (ImGui.Button("Copy setup command"))
        {
            ImGui.SetClipboardText(GetInstallCommand());
            runtimeStatus = "Command copied. Check the prefix path before running it.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Retry detection"))
        {
            runtimeStatus = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var version, out var error)
                ? $"WebView2 detected: {version}"
                : error;
        }

        ImGui.Spacing();
        ImGui.TextWrapped("Important: installing WebView2 into a different prefix will not make it available to CrystalCast. Back up the prefix first if it contains anything important.");
        ImGui.TextDisabled("Wine always uses WebView2 JPEG capture; Windows Graphics Capture is disabled.");

        if (!string.IsNullOrWhiteSpace(runtimeStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(runtimeStatus);
        }

        ImGui.Spacing();
        if (ImGui.Button("Remind me later"))
            IsOpen = false;

        ImGui.SameLine();
        if (ImGui.Button("Don't show again"))
        {
            plugin.Configuration.WineWebView2SetupDismissed = true;
            plugin.Configuration.Save();
            IsOpen = false;
        }
    }
}
