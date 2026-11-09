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
    private bool runtimeDownloaded;
    private bool showOtherOptions;
    private WebView2DownloadStage lastSeenStage = WebView2DownloadStage.Idle;

    public WineWebView2SetupWindow(Plugin plugin)
        : base("CrystalCast Wine Setup###CrystalCastWineWebView2Setup")
    {
        this.plugin = plugin;
        Size = new Vector2(660, 620);
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

    private WebView2RuntimeDownloader Downloader => plugin.WebView2Downloader;

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
        var progress = Downloader.Progress;
        if (progress.Stage != lastSeenStage)
        {
            lastSeenStage = progress.Stage;
            if (!progress.IsBusy)
                RefreshRuntimeStatus();
        }

        Ui.Hero(
            "Linux / Wine · browser setup",
            runtimeAvailable ? "WebView2 is ready" : "Get WebView2 for browser screens",
            runtimeAvailable
                ? "Browser screens can play now. Nothing else to install."
                : "CrystalCast plays YouTube, Twitch and web pages in Microsoft's WebView2 browser. Under Wine it can download it for you.");

        var footerHeight = Ui.FrameHeight + (ImGui.GetStyle().ItemSpacing.Y * 2.0f);
        if (ImGui.BeginChild("CrystalCastWineSetupContent", new Vector2(0.0f, -footerHeight), false, ImGuiWindowFlags.None))
        {
            DrawDownloadCard(progress);
            DrawOtherOptions();
        }

        ImGui.EndChild();

        ImGui.Spacing();
        if (Ui.Button(runtimeAvailable ? "Close" : "Remind me later", ButtonKind.Ghost))
            IsOpen = false;

        if (!runtimeAvailable)
        {
            const string dismissLabel = "Don't show again";
            ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - Ui.ButtonWidth(dismissLabel)));
            if (Ui.Button(dismissLabel))
            {
                plugin.Configuration.WineWebView2SetupDismissed = true;
                plugin.Configuration.Save();
                IsOpen = false;
            }
        }
    }

    private void DrawDownloadCard(WebView2DownloadProgress progress)
    {
        Ui.BeginCard();
        Ui.Heading(runtimeDownloaded || !runtimeAvailable ? "Download WebView2 (recommended)" : "WebView2");
        Ui.Text(
            "Downloads Microsoft's WebView2 runtime (about 310 MB, 700 MB unpacked) straight from Microsoft into CrystalCast's folder in this Wine prefix. It is not an installer: nothing runs in the background, and it works the same with Wine, Proton, XIVLauncher and Steam or Flatpak containers. Nothing is needed on the Linux side.",
            TextSecondary,
            f => f.Body,
            wrap: true);

        ImGui.Spacing();
        if (progress.Stage == WebView2DownloadStage.Removing)
        {
            Ui.Meta(progress.Message);
        }
        else if (progress.IsBusy)
        {
            DrawProgressBar(progress);
            if (Ui.Button("Cancel"))
                Downloader.Cancel();
        }
        else
        {
            var label = runtimeDownloaded ? "Download again" : "Download and set up";
            var kind = runtimeAvailable ? ButtonKind.Secondary : ButtonKind.Primary;
            if (Ui.Button(label, kind))
                Downloader.Start();

            ImGui.SameLine(0.0f, S(8.0f));
            if (Ui.Button("Check again"))
                RefreshRuntimeStatus();

        }

        Ui.Meta("Changed your mind later? Settings > Browser > Downloaded WebView2 shows how much space it uses and deletes it.");

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(2.0f));
        Ui.StatusLine(
            runtimeAvailable ? Accent : Warning,
            runtimeAvailable ? "WebView2 detected" : "WebView2 not detected",
            string.Empty);
        Ui.Meta(runtimeStatus);
        switch (progress.Stage)
        {
            case WebView2DownloadStage.Failed:
                Ui.ErrorText($"Download failed: {progress.Message} The Dalamud log (/xllog) has the details.");
                break;
            case WebView2DownloadStage.RemoveFailed:
                Ui.ErrorText($"Delete failed: {progress.Message}");
                break;
            case WebView2DownloadStage.Cancelled:
            case WebView2DownloadStage.Done:
            case WebView2DownloadStage.Removed:
                Ui.Meta(progress.Message);
                break;
        }

        Ui.Meta("Under Wine this also sets Windows 7 mode for msedgewebview2.exe in this prefix, which the WebView2 browser needs to start (the same setting winetricks makes).");
        Ui.EndCard();
    }

    private static void DrawProgressBar(WebView2DownloadProgress progress)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - S(16.0f);
        var size = new Vector2(width, S(8.0f));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, U32(Inset), S(4.0f));
        var fraction = progress.TotalBytes > 0 ? progress.Fraction : 0.0f;
        if (fraction > 0.0f)
            dl.AddRectFilled(pos, pos + new Vector2(size.X * fraction, size.Y), U32(Accent), S(4.0f));
        ImGui.Dummy(size);

        var detail = progress.TotalBytes > 0 && progress.Stage == WebView2DownloadStage.Downloading
            ? $"{progress.Message}: {progress.DoneBytes / (1024 * 1024)} of {progress.TotalBytes / (1024 * 1024)} MB"
            : progress.Message;
        Ui.Meta(detail);
    }

    private void DrawOtherOptions()
    {
        if (Ui.Button(showOtherOptions ? "Hide other ways" : "Other ways to set this up", ButtonKind.Ghost))
            showOtherOptions = !showOtherOptions;

        if (!showOtherOptions)
            return;

        DrawCommandCard();
        DrawNativeBrowserCard();
    }

    private void DrawNativeBrowserCard()
    {
        Ui.BeginCard();
        Ui.Heading("Use your Linux browser (experimental)");
        Ui.Text(
            "YouTube, Twitch, Dailymotion and Vimeo can play in your Linux system's WebKitGTK 6.0 instead of WebView2. Install WebKitGTK 6.0 with your package manager (libwebkitgtk-6.0-4 on Ubuntu/Debian, webkitgtk6.0 on Fedora, webkitgtk-6.0 on Arch). It does not work inside Steam's Linux runtime container, and Generic Web screens still need WebView2.",
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
        Ui.Heading("Install with winetricks");
        Ui.Text(
            "Installs the regular WebView2 into the prefix instead. Needs a recent winetricks and Wine on the Linux side, and WINEPREFIX must point to the prefix that runs FINAL FANTASY XIV.",
            TextSecondary,
            f => f.Body,
            wrap: true);

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

        if (Ui.Button("Copy command"))
        {
            ImGui.SetClipboardText(command);
            runtimeStatus = "Command copied. Check the prefix path before running it.";
        }

        Ui.EndCard();
    }

    private void RefreshRuntimeStatus()
    {
        WebView2FixedRuntime.Invalidate();
        runtimeAvailable = WebView2FixedRuntime.TryFind(out var runtime, out var error);
        runtimeDownloaded = WebView2FixedRuntime.FindDownloadedFolder() != null;
        runtimeStatus = runtimeAvailable ? $"WebView2 {runtime.Describe()}" : error;
    }
}
