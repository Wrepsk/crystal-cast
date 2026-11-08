using System.Numerics;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class WineWebView2SetupWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string runtimeStatus = string.Empty;
    private bool runtimeAvailable;

    public WineWebView2SetupWindow(Plugin plugin)
        : base("CrystalCast Wine Setup###CrystalCastWineWebView2Setup")
    {
        this.plugin = plugin;
        Size = new Vector2(660, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = ShouldOpenAutomatically(plugin.Configuration);
        RefreshRuntimeStatus();
    }

    internal static bool ShouldOpenAutomatically(Configuration configuration)
    {
        var runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out _, out _);
        return WineWebView2SetupPolicy.ShouldShow(
            WineEnvironment.IsWine,
            configuration.WineWebView2SetupDismissed,
            runtimeAvailable,
            configuration.YouTubeBrowserEngine == BrowserMediaEngine.WebKitGtk);
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

    public override void PreDraw()
    {
        PushWindowStyle(S(28.0f, 24.0f));
    }

    public override void PostDraw()
    {
        PopWindowStyle();
    }

    public override void Draw()
    {
        Ui.Hero(
            "Linux / Wine · browser setup",
            "Install WebView2 in your FFXIV prefix",
            "One dependency is required before CrystalCast can render browser sources under Wine.");

        var footerHeight = Ui.FrameHeight + (ImGui.GetStyle().ItemSpacing.Y * 2.0f);
        if (ImGui.BeginChild("CrystalCastWineSetupContent", new Vector2(0.0f, -footerHeight), false, ImGuiWindowFlags.None))
        {
            DrawNativeBrowserCard();
            Ui.Card(
                "Before you install",
                "Use a recent Winetricks release and make sure WINEPREFIX points to the prefix that runs FINAL FANTASY XIV.",
                "Back up that prefix first if it contains settings or data you cannot easily replace.");
            DrawCommandCard();
            Ui.Card(
                "How Wine capture works",
                "WebView2 must be installed in the same Wine prefix as the game; another prefix will not be visible to CrystalCast.",
                "With WebView2, Wine uses JPEG capture. Windows Graphics Capture is disabled in this environment.",
                "Wine support is experimental and still needs broader testing on real Linux installations.");
        }

        ImGui.EndChild();

        ImGui.Spacing();
        if (Ui.Button("Remind me later", ButtonKind.Ghost))
            IsOpen = false;

        const string dismissLabel = "Don't show again";
        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - Ui.ButtonWidth(dismissLabel)));
        if (Ui.Button(dismissLabel))
        {
            plugin.Configuration.WineWebView2SetupDismissed = true;
            plugin.Configuration.Save();
            IsOpen = false;
        }
    }

    private void DrawNativeBrowserCard()
    {
        Ui.BeginCard();
        Ui.Heading("Alternative: use your Linux browser (experimental)");
        Ui.Text(
            "YouTube, Twitch, Dailymotion and Vimeo can play in your Linux system's WebKitGTK 6.0 instead of WebView2. No winetricks needed; install WebKitGTK 6.0 with your package manager (libwebkitgtk-6.0-4 on Ubuntu/Debian, webkitgtk6.0 on Fedora, webkitgtk-6.0 on Arch). Generic Web screens still need WebView2.",
            TextSecondary,
            f => f.Body,
            wrap: true);
        Ui.Meta(WebKitGtkAvailability.Describe());
        if (Ui.Button("Use the native Linux browser"))
        {
            plugin.Configuration.YouTubeBrowserEngine = BrowserMediaEngine.WebKitGtk;
            plugin.Configuration.Save();
            IsOpen = false;
        }

        Ui.EndCard();
    }

    private void DrawCommandCard()
    {
        Ui.BeginCard();
        Ui.Heading("Run the setup command");

        var command = GetInstallCommand();
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - S(16.0f);
        float textHeight;
        using (Ui.Font(f => f.Mono))
            textHeight = ImGui.CalcTextSize(command, false, width - S(20.0f)).Y;
        var boxSize = new Vector2(width, textHeight + S(16.0f));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + boxSize, U32(Inset), S(4.0f));
        dl.AddRect(pos, pos + boxSize, U32(Hairline), S(4.0f), ImDrawFlags.None, 1.0f);
        ImGui.SetCursorScreenPos(pos + S(10.0f, 8.0f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - S(20.0f));
        Ui.Text(command, CrystalCastUiTheme.Text, f => f.Mono);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(boxSize);

        if (Ui.Button("Copy setup command", ButtonKind.Primary))
        {
            ImGui.SetClipboardText(command);
            runtimeStatus = "Setup command copied. Check the prefix path before running it.";
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Retry detection"))
            RefreshRuntimeStatus();

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(2.0f));
        Ui.StatusLine(runtimeAvailable ? Accent : Warning, runtimeAvailable ? "WebView2 detected" : "WebView2 not detected", string.Empty);
        Ui.Meta(runtimeStatus);
        Ui.EndCard();
    }

    private void RefreshRuntimeStatus()
    {
        runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var version, out var error);
        runtimeStatus = runtimeAvailable ? $"WebView2 {version}" : error;
    }
}
