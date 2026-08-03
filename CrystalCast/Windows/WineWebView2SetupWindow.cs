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
        Size = new Vector2(660, 520);
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
        DrawHero();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().ItemSpacing.Y * 2.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        if (ImGui.BeginChild("CrystalCastWineSetupContent", new Vector2(0.0f, -footerHeight), false, ImGuiWindowFlags.None))
        {
            DrawPreparationCard();
            DrawCommandCard();
            DrawStatusCallout();
            DrawCompatibilityCard();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.Spacing();
        if (ImGui.Button("Remind me later"))
            IsOpen = false;

        var dismissLabel = "Don't show again";
        var dismissWidth = ImGui.CalcTextSize(dismissLabel).X + (ImGui.GetStyle().FramePadding.X * 2.0f);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - dismissWidth));
        ImGui.PushStyleColor(ImGuiCol.Button, CrystalCastUiTheme.WithAlpha(CrystalCastUiTheme.Accent, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CrystalCastUiTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CrystalCastUiTheme.AccentActive);
        if (ImGui.Button(dismissLabel, new Vector2(dismissWidth, 0.0f)))
        {
            plugin.Configuration.WineWebView2SetupDismissed = true;
            plugin.Configuration.Save();
            IsOpen = false;
        }
        ImGui.PopStyleColor(3);
    }

    private static void DrawHero()
    {
        var accent = CrystalCastUiTheme.Accent;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(accent.X * 0.11f, accent.Y * 0.11f, accent.Z * 0.11f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(accent, 0.42f));
        if (ImGui.BeginChild("CrystalCastWineSetupHero", new Vector2(0.0f, 105.0f), true, ImGuiWindowFlags.None))
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(
                windowPos,
                new Vector2(windowPos.X + 5.0f, windowPos.Y + windowSize.Y),
                ImGui.GetColorU32(accent),
                10.0f);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextColored(CrystalCastUiTheme.AccentText, "LINUX / WINE  •  BROWSER SETUP");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextUnformatted("Install WebView2 in your FFXIV prefix");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextDisabled("One dependency is required before CrystalCast can render browser sources under Wine.");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.Spacing();
    }

    private static void DrawPreparationCard()
    {
        DrawCard(
            "Preparation",
            "Before you install",
            "Use a recent Winetricks release and make sure WINEPREFIX points to the prefix that runs FINAL FANTASY XIV.",
            "Back up that prefix first if it contains settings or data you cannot easily replace.");
    }

    private void DrawCommandCard()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 0.64f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(CrystalCastUiTheme.Accent, 0.38f));
        if (ImGui.BeginChild("CrystalCastWineCommandCard", new Vector2(0.0f, 142.0f), true, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(CrystalCastUiTheme.AccentText, "Run the setup command");
            ImGui.Separator();

            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5.0f);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.045f, 0.045f, 0.055f, 0.95f));
            if (ImGui.BeginChild("CrystalCastWineCommand", new Vector2(0.0f, 52.0f), false, ImGuiWindowFlags.None))
            {
                ImGui.TextWrapped(GetInstallCommand());
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();

            if (ImGui.Button("Copy setup command"))
            {
                ImGui.SetClipboardText(GetInstallCommand());
                runtimeStatus = "Setup command copied. Check the prefix path before running it.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Retry detection"))
                RefreshRuntimeStatus();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.Spacing();
    }

    private void DrawStatusCallout()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CrystalCastUiTheme.WithAlpha(CrystalCastUiTheme.Accent, 0.10f));
        if (ImGui.BeginChild("CrystalCastWineStatus", new Vector2(0.0f, 66.0f), false, ImGuiWindowFlags.None))
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(
                windowPos,
                new Vector2(windowPos.X + 4.0f, windowPos.Y + windowSize.Y),
                ImGui.GetColorU32(CrystalCastUiTheme.Accent),
                7.0f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7.0f);
            ImGui.TextColored(CrystalCastUiTheme.AccentText, "DETECTION STATUS");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7.0f);
            ImGui.TextWrapped(runtimeStatus);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.Spacing();
    }

    private static void DrawCompatibilityCard()
    {
        DrawCard(
            "Compatibility",
            "How Wine capture works",
            "WebView2 must be installed in the same Wine prefix as the game; another prefix will not be visible to CrystalCast.",
            "Wine always uses WebView2 JPEG capture. Windows Graphics Capture is disabled in this environment.",
            "Wine support is experimental and still needs broader testing on real Linux installations.");
    }

    private static void DrawCard(string id, string title, params string[] bullets)
    {
        var height = 48.0f + (bullets.Length * 35.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 0.64f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(CrystalCastUiTheme.Accent, 0.38f));
        if (ImGui.BeginChild($"CrystalCastWineCard{id}", new Vector2(0.0f, height), true, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(CrystalCastUiTheme.AccentText, title);
            ImGui.Separator();
            foreach (var bullet in bullets)
            {
                ImGui.TextColored(CrystalCastUiTheme.AccentText, "•");
                ImGui.SameLine();
                ImGui.TextWrapped(bullet);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.Spacing();
    }

    private void RefreshRuntimeStatus()
    {
        runtimeStatus = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var version, out var error)
            ? $"WebView2 detected: {version}"
            : error;
    }
}
