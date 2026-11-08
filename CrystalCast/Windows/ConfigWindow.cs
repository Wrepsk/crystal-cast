using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private const float LabelWidth = SettingsLabelWidth;
    private const float LabelGap = SettingsLabelGap;
    private const float SectionGap = 32.0f;

    private static readonly (ScreenOutputMode Mode, string Name)[] OutputModes =
    [
        (ScreenOutputMode.SceneComposite, "Scene composite"),
        (ScreenOutputMode.NativeOverlay, "Native overlay"),
        (ScreenOutputMode.ImGuiOverlay, "ImGui overlay"),
    ];

    private static readonly (BrowserMediaEngine Engine, string Name)[] BrowserEngines =
    [
        (BrowserMediaEngine.Auto, "Auto"),
        (BrowserMediaEngine.WebView2Capture, "WebView2 JPEG capture"),
        (BrowserMediaEngine.WebView2WindowCapture, "WebView2 window capture"),
        (BrowserMediaEngine.WebKitGtk, "Native Linux WebKitGTK (experimental)"),
    ];

    private static readonly Page[] Pages =
    [
        new(PageId.Rendering, "Rendering", FontAwesomeIcon.LayerGroup, "How screens are composited with the game and how each screen is occluded."),
        new(PageId.Browser, "Browser", FontAwesomeIcon.Globe, "Which browser renders pages and how they are captured into screen textures."),
        new(PageId.Diagnostics, "Diagnostics", FontAwesomeIcon.WaveSquare, "Checks and a full report to share when asking for support."),
        new(PageId.Ipc, "IPC", FontAwesomeIcon.Link, "Let other Dalamud plugins create and control CrystalCast screens."),
#if DEBUG
        new(PageId.Debug, "Debug", FontAwesomeIcon.Terminal, "Preview setup windows without editing the saved configuration."),
#endif
    ];

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private PageId currentPage = PageId.Rendering;
    private string browserDataStatus = string.Empty;
    private string diagnosticsReport = string.Empty;
    private long diagnosticsReportUpdatedAtTick;
    private long diagnosticsCopiedAtTick;
    private long ipcDomainMemoryClearedAtTick;

    public ConfigWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast Settings###CrystalCastConfig", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

        Size = new Vector2(1040, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 480),
            MaximumSize = new Vector2(2000, 2000),
        };
    }

    private enum PageId
    {
        Rendering,
        Browser,
        Diagnostics,
        Ipc,
        Debug,
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        PushWindowStyle(Vector2.Zero);
    }

    public override void PostDraw()
    {
        PopWindowStyle();
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;
        changed |= config.Normalize();

        DrawTitleBar();

        var navWidth = S(200.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Raised);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(12.0f, 16.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, S(0.0f, 2.0f));
        var navOpen = ImGui.BeginChild("##CrystalCastSettingsNav", new Vector2(navWidth, 0.0f), false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor();
        if (navOpen)
            DrawNav();
        ImGui.EndChild();

        ImGui.SameLine(0.0f, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(36.0f, 28.0f));
        var contentOpen = ImGui.BeginChild("##CrystalCastSettingsContent", Vector2.Zero, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (contentOpen)
        {
            var page = Pages.FirstOrDefault(entry => entry.Id == currentPage) ?? Pages[0];
            DrawPageTitle(page);
            changed |= page.Id switch
            {
                PageId.Rendering => DrawRendering(config),
                PageId.Browser => DrawBrowserMedia(config),
                PageId.Diagnostics => DrawDiagnostics(config),
                PageId.Ipc => DrawIpc(config),
#if DEBUG
                PageId.Debug => DrawDebugWindows(),
#endif
                _ => false,
            };
        }

        ImGui.EndChild();

        if (changed)
            config.Save();
    }

    private void DrawTitleBar()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowWidth();
        var height = S(TitleBarHeight);
        var dl = ImGui.GetWindowDrawList();

        CrystalArtwork.DrawLogo(dl, new Vector2(start.X + S(22.0f), start.Y + (height * 0.5f)));
        float nameWidth;
        using (Ui.Font(f => f.SemiBold))
        {
            nameWidth = ImGui.CalcTextSize("CrystalCast").X;
            dl.AddText(new Vector2(start.X + S(38.0f), start.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), U32(CrystalCastUiTheme.Text), "CrystalCast");
        }

        using (Ui.Font(f => f.Body))
            dl.AddText(new Vector2(start.X + S(38.0f) + nameWidth + S(10.0f), start.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), U32(TextTertiary), "Settings");

        dl.AddRectFilled(new Vector2(start.X, start.Y + height), new Vector2(start.X + width, start.Y + height + 1.0f), U32(Hairline));

        ImGui.SetCursorScreenPos(new Vector2(start.X + width - S(8.0f) - Ui.FrameHeight, start.Y + ((height - Ui.FrameHeight) * 0.5f)));
        if (Ui.IconButton("##CloseSettings", FontAwesomeIcon.Times, "Close window"))
            IsOpen = false;

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height + 1.0f));
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height + 1.0f));
    }

    private void DrawNav()
    {
        foreach (var page in Pages)
        {
            if (Ui.NavItem(page.Name, page.Icon, page.Id == currentPage))
                currentPage = page.Id;
        }

        var guideHeight = S(36.0f);
        var remaining = ImGui.GetContentRegionAvail().Y - guideHeight;
        if (remaining > 0.0f)
            ImGui.Dummy(new Vector2(1.0f, remaining));
        if (Ui.NavItem("Onboarding guide", FontAwesomeIcon.BookOpen, active: false))
            plugin.ShowFirstRunGuide();
    }

    private static void DrawPageTitle(Page page)
    {
        Ui.Text(page.Name, TitleText, f => f.PageTitle);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - S(4.0f));
        Ui.Text(page.Description, TextSecondary, f => f.Body, wrap: true);
        Ui.VerticalSpace(SectionGap);
    }

    // ---------------------------------------------------------------- Rendering

    private bool DrawRendering(Configuration config)
    {
        var changed = false;
        Ui.SectionHeader("Output layer", "Controls how CrystalCast is composited with the game.");

        var tileGap = S(16.0f);
        var tilesPerRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + tileGap) / (S(200.0f) + tileGap)));
        for (var i = 0; i < OutputModes.Length; i++)
        {
            if (i % tilesPerRow != 0)
                ImGui.SameLine(0.0f, tileGap);
            else if (i > 0)
                ImGui.Dummy(new Vector2(0.0f, S(4.0f)));
            var (mode, name) = OutputModes[i];
            if (Ui.ModeTile($"##OutputMode{mode}", name, mode, config.OutputMode == mode) && config.OutputMode != mode)
            {
                config.OutputMode = mode;
                changed = true;
            }
        }

        Ui.VerticalSpace(14.0f);
        Ui.Label(GetOutputModeDescription(config.OutputMode), wrap: true);

        if (WineEnvironment.IsWine && config.OutputMode == ScreenOutputMode.SceneComposite)
        {
            ImGui.Spacing();
            var enableWineSceneComposite = config.EnableWineSceneComposite;
            Ui.RowLabel("Under Wine", LabelWidth, LabelGap);
            if (Ui.SwitchLabeled("##WineSceneComposite", "Try experimental scene composite", ref enableWineSceneComposite))
            {
                config.EnableWineSceneComposite = enableWineSceneComposite;
                changed = true;
            }

            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta("Unverified on Linux. Falls back to overlay output if scene compositing fails or stops responding.");
        }

        if (config.OutputMode == ScreenOutputMode.SceneComposite && renderer.SceneCompositeFallbackReason is { } fallback)
        {
            ImGui.Spacing();
            Ui.Banner("SceneFallback", BannerKind.Warning, "Using overlay output", $"{fallback}. Switch output layers and back to retry.");
        }

        ImGui.Spacing();
        Ui.RowLabel("UI mask", LabelWidth, LabelGap);
        AlignedText("Disabled", TextTertiary);

#if DEBUG
        var showMarker = config.ShowDebugMarker;
        Ui.RowLabel("Debug marker", LabelWidth, LabelGap);
        if (Ui.Switch("##DebugMarker", ref showMarker))
        {
            config.ShowDebugMarker = showMarker;
            changed = true;
        }
#endif

        Ui.VerticalSpace(SectionGap);
        var activeScreen = config.GetActiveBrowserScreen();
        Ui.SectionHeader("Visibility and occlusion");
        if (activeScreen == null)
        {
            Ui.Meta("Create a screen from the main CrystalCast window to configure per-screen rendering.");
            return changed;
        }

        changed |= DrawScreenPicker(config, activeScreen);
        var placement = activeScreen.Placement;
        var occludedAlpha = placement.OccludedAlpha;
        Ui.RowLabel("Occluded alpha", LabelWidth, LabelGap);
        if (SettingsSlider("##OccludedAlpha", ref occludedAlpha, 0.0f, 1.0f, value => $"{value:0.000}"))
        {
            placement.OccludedAlpha = Math.Clamp(occludedAlpha, 0.0f, 1.0f);
            changed = true;
        }

        var tolerance = placement.OcclusionTolerance;
        Ui.RowLabel("Occlusion tolerance", LabelWidth, LabelGap);
        if (SettingsSlider("##OcclusionTolerance", ref tolerance, 0.0f, Math.Max(0.1f, tolerance), value => $"{value:0.000}"))
        {
            placement.OcclusionTolerance = Math.Max(0.0f, tolerance);
            changed = true;
        }

        Ui.RowLabel("Presets", LabelWidth, LabelGap);
        if (Ui.Button("Make fully visible", tooltip: "Draw the screen over geometry that hides it"))
        {
            placement.OccludedAlpha = 1.0f;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Depth occlusion", tooltip: "Hide the parts of the screen behind geometry"))
        {
            placement.OccludedAlpha = 0.0f;
            changed = true;
        }

        Ui.VerticalSpace(SectionGap);
        Ui.SectionHeader("Distance fade");
        var distanceFade = placement.EnableDistanceFade;
        Ui.RowLabel("Distance fade", LabelWidth, LabelGap);
        if (Ui.Switch("##DistanceFade", ref distanceFade))
        {
            placement.EnableDistanceFade = distanceFade;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        AlignedText("Fade the selected screen out over long distances.", TextTertiary);

        var fadeStart = placement.FadeStartMeters;
        Ui.RowLabel("Fade start", LabelWidth, LabelGap);
        if (Ui.DragFloat("##FadeStart", ref fadeStart, 0.1f, format: "%.1f m", width: S(120.0f), disabled: !placement.EnableDistanceFade))
        {
            placement.FadeStartMeters = Math.Max(0.0f, fadeStart);
            changed = true;
        }

        var fadeStop = placement.FadeStopMeters;
        Ui.RowLabel("Fade stop", LabelWidth, LabelGap);
        if (Ui.DragFloat("##FadeStop", ref fadeStop, 0.1f, format: "%.1f m", width: S(120.0f), disabled: !placement.EnableDistanceFade))
        {
            placement.FadeStopMeters = Math.Max(placement.FadeStartMeters + 0.01f, fadeStop);
            changed = true;
        }

        return changed;
    }

    private static bool DrawScreenPicker(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        Ui.RowLabel("Applies to", LabelWidth, LabelGap);
        var seed = CrystalArtwork.SeedFor(activeScreen.ScreenId);
        if (Ui.BeginDropdown("##AppliesTo", activeScreen.Name, S(240.0f), drawPrefix: (dl, min, max) => CrystalArtwork.DrawChip(dl, min, max, seed)))
        {
            foreach (var screen in config.BrowserScreens)
            {
                if (Ui.MenuItem($"{screen.Name}##AppliesTo{screen.ScreenId}", screen.ScreenId == activeScreen.ScreenId)
                    && screen.ScreenId != activeScreen.ScreenId)
                {
                    config.ActiveBrowserScreenId = screen.ScreenId;
                    changed = true;
                }
            }

            Ui.EndDropdown();
        }

        return changed;
    }

    /// <summary>Settings sliders (with their readout) stay within a 420 px column.</summary>
    private static bool SettingsSlider(string id, ref float value, float min, float max, Func<float, string> readout)
    {
        var width = Math.Min(ImGui.GetContentRegionAvail().X, S(420.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = ImGui.BeginChild($"{id}Column", new Vector2(width, Ui.FrameHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();
        var changed = open && Ui.Slider(id, ref value, min, max, readout);
        ImGui.EndChild();
        return changed;
    }

    private static string GetOutputModeDescription(ScreenOutputMode outputMode)
    {
        return outputMode switch
        {
            ScreenOutputMode.SceneComposite => WineEnvironment.IsWine
                ? "Draws into the game scene before native UI. Requires the experimental option below."
                : "Draws into the game scene before native UI, behind nameplates.",
            ScreenOutputMode.NativeOverlay => "Uses Dalamud's native overlay path: behind everything except nameplates.",
            _ => "Compatibility path drawn through the plugin UI layer, in front of all UI.",
        };
    }

    // ---------------------------------------------------------------- Browser

    private bool DrawBrowserMedia(Configuration config)
    {
        var changed = false;
        Ui.SectionHeader("Browser backend");

        var current = Array.FindIndex(BrowserEngines, entry => entry.Engine == config.YouTubeBrowserEngine);
        if (current < 0)
            current = 0;
        Ui.RowLabel("Backend", LabelWidth, LabelGap);
        if (Ui.BeginDropdown("##BrowserBackend", BrowserEngines[current].Name, S(320.0f)))
        {
            for (var i = 0; i < BrowserEngines.Length; i++)
            {
                if (BrowserEngines[i].Engine == BrowserMediaEngine.WebKitGtk && !WineEnvironment.IsWine)
                    continue;

                if (Ui.MenuItem($"{BrowserEngines[i].Name}##Engine{i}", i == current) && i != current)
                {
                    config.YouTubeBrowserEngine = BrowserEngines[i].Engine;
                    changed = true;
                }
            }

            Ui.EndDropdown();
        }

        var webKitGtkSelected = WineEnvironment.IsWine && config.YouTubeBrowserEngine == BrowserMediaEngine.WebKitGtk;
        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(webKitGtkSelected
            ? "YouTube, Twitch, Dailymotion and Vimeo play in a native Linux browser window. Generic Web still uses WebView2 JPEG capture."
            : WineEnvironment.IsWine
                ? "Wine detected: WebView2 JPEG capture is used unless the native Linux WebKitGTK backend is selected."
                : "Auto uses WebView2 window capture and falls back to JPEG capture when needed.");

        if (webKitGtkSelected)
        {
            ImGui.Spacing();
            Ui.Banner(
                "WebKitGtkNotes",
                BannerKind.Warning,
                "Experimental: keep the hidden browser window open",
                $"Requires WebKitGTK 6.0; {WebKitGtkAvailability.Describe()}. The browser runs in a hidden \"CrystalCast browser\" window. Minimizing or closing it pauses the video; Show controls brings it forward. If a screen stays black or freezes, Diagnostics explains why.");
        }

        if (WineEnvironment.IsWine)
        {
            ImGui.Spacing();
            var runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError);
            Ui.RowLabel("WebView2 under Wine", LabelWidth, LabelGap);
            if (Ui.Button("Open setup"))
                plugin.ShowWineWebView2Setup();
            ImGui.SameLine(0.0f, S(12.0f));
            AlignedText(runtimeAvailable ? $"Detected: {runtimeVersion}" : runtimeError, runtimeAvailable ? TextTertiary : Warning);
        }

        Ui.VerticalSpace(SectionGap);
        Ui.SectionHeader("Browser data", "Reset browser profiles when cookies, sign-in, or cached state cause problems.");
        Ui.RowLabel("Profiles", LabelWidth, LabelGap);
        if (Ui.Button("Clear on restart"))
        {
            try
            {
                browserDataStatus = BrowserProfileManager.RequestClearOnNextStart();
            }
            catch (Exception ex)
            {
                browserDataStatus = $"Could not schedule browser data clearing: {ex.GetBaseException().Message}";
            }
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(string.IsNullOrEmpty(browserDataStatus)
            ? "Clears cookies, local storage, cache, and saved browser state after restarting CrystalCast."
            : browserDataStatus);
        return changed;
    }

    // ---------------------------------------------------------------- Diagnostics

    private bool DrawDiagnostics(Configuration config)
    {
        var changed = false;
        Ui.SectionHeader("Diagnostic options", "Optional checks used when investigating rendering problems.");
        var gpuDiagnostics = config.EnableGpuDiagnostics;
        Ui.RowLabel("GPU texture sampling", LabelWidth, LabelGap);
        if (Ui.Switch("##GpuDiagnostics", ref gpuDiagnostics))
        {
            config.EnableGpuDiagnostics = gpuDiagnostics;
            diagnosticsReportUpdatedAtTick = 0;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        AlignedText("Reads back one sample per second per active window-capture screen.", TextTertiary);

        Ui.VerticalSpace(SectionGap);
        Ui.SectionHeader("Diagnostic report", "Copy the full report when asking for support.");
        if (Ui.Button("Copy full diagnostics", ButtonKind.Primary))
        {
            ImGui.SetClipboardText(GetDiagnosticsReport(config, forceRefresh: true));
            diagnosticsCopiedAtTick = Environment.TickCount64;
        }

        if (diagnosticsCopiedAtTick > 0 && Environment.TickCount64 - diagnosticsCopiedAtTick < 2000)
        {
            ImGui.SameLine(0.0f, S(12.0f));
            AlignedText("Copied", TextTertiary);
        }

        ImGui.Spacing();
        var reportPos = ImGui.GetCursorScreenPos();
        var reportSize = new Vector2(ImGui.GetContentRegionAvail().X, Math.Max(S(160.0f), ImGui.GetContentRegionAvail().Y));
        ImGui.GetWindowDrawList().AddRectFilled(reportPos, reportPos + reportSize, U32(Inset), S(4.0f));
        ImGui.GetWindowDrawList().AddRect(reportPos, reportPos + reportSize, U32(Hairline), S(4.0f), ImDrawFlags.None, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(12.0f, 10.0f));
        var open = ImGui.BeginChild("##DiagnosticsReport", reportSize, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (open)
        {
            using (Ui.Font(f => f.MonoDetail))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(GetDiagnosticsReport(config, forceRefresh: false));
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();
        return changed;
    }

    private string GetDiagnosticsReport(Configuration config, bool forceRefresh)
    {
        var now = Environment.TickCount64;
        if (forceRefresh
            || string.IsNullOrEmpty(diagnosticsReport)
            || diagnosticsReportUpdatedAtTick == 0
            || now - diagnosticsReportUpdatedAtTick >= 1000)
        {
            diagnosticsReport = DiagnosticsReportBuilder.Build(config, renderer);
            diagnosticsReportUpdatedAtTick = now;
        }

        return diagnosticsReport;
    }

    // ---------------------------------------------------------------- IPC

    private bool DrawIpc(Configuration config)
    {
        Ui.SectionHeader("Plugin integrations");
        var enabled = config.IpcEnabled;
        Ui.RowLabel("IPC", LabelWidth, LabelGap);
        if (Ui.Switch("##IpcEnabled", ref enabled))
        {
            ipc.SetEnabled(enabled);
            config.IpcEnabled = enabled;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        AlignedText(enabled
                ? "Endpoints are registered and available to integrations."
                : "IPC-created screens are removed while integrations are off.",
            TextTertiary);

        Ui.VerticalSpace(SectionGap);
        Ui.SectionHeader("Generic Web domain memory", "Clear temporary trusted and blocked website decisions without reloading CrystalCast.");
        Ui.RowLabel("Session decisions", LabelWidth, LabelGap);
        if (Ui.Button("Reset domain memory"))
        {
            plugin.ResetIpcWebDomainMemory();
            ipcDomainMemoryClearedAtTick = Environment.TickCount64;
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(ipcDomainMemoryClearedAtTick > 0 && Environment.TickCount64 - ipcDomainMemoryClearedAtTick < 3000
            ? "Domain memory cleared."
            : "Active IPC Generic Web screens will ask for permission again before loading.");

        // Same as before the redesign: this page does not mark the configuration dirty itself.
        return false;
    }

#if DEBUG
    // ---------------------------------------------------------------- Debug

    private bool DrawDebugWindows()
    {
        Ui.SectionHeader("First-run windows");
        Ui.RowLabel("Wine", LabelWidth, LabelGap);
        if (Ui.Button("Open Wine WebView2 setup"))
            plugin.ShowWineWebView2Setup();

        Ui.RowLabel("Onboarding", LabelWidth, LabelGap);
        if (Ui.Button("Open onboarding guide"))
            plugin.ShowFirstRunGuide();
        return false;
    }
#endif

    private static void AlignedText(string text, Vector4 color)
    {
        Ui.RowText(text, color, f => f.Small, wrap: true);
    }

    private sealed record Page(PageId Id, string Name, FontAwesomeIcon Icon, string Description);
}
