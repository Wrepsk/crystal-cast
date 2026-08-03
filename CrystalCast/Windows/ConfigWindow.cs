using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly (ScreenOutputMode Mode, string Name)[] OutputModes =
    [
        (ScreenOutputMode.ImGuiOverlay, "ImGui overlay"),
        (ScreenOutputMode.NativeOverlay, "Native overlay"),
        (ScreenOutputMode.SceneComposite, "Scene composite (Windows only)"),
    ];

    private static readonly string[] BrowserEngineNames =
        ["Auto", "WebView2 JPEG capture", "WebView2 window capture"];

    private static readonly BrowserMediaEngine[] BrowserEngines =
        [BrowserMediaEngine.Auto, BrowserMediaEngine.WebView2Capture, BrowserMediaEngine.WebView2WindowCapture];

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private string browserDataStatus = string.Empty;
    private string diagnosticsReport = string.Empty;
    private long diagnosticsReportUpdatedAtTick;
    private long diagnosticsCopiedAtTick;

    public ConfigWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast Settings###CrystalCastConfig")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

        Size = new Vector2(600, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;
        changed |= config.Normalize();

        CrystalCastUiTheme.DrawWindowHeader(
            "Settings",
            "CONFIGURATION",
            "CrystalCast settings",
            "Rendering, browser capture, diagnostics, and integrations.");
        ImGui.Spacing();

        if (ImGui.Button("Open onboarding guide"))
            plugin.ShowFirstRunGuide();
        ImGui.Spacing();

        CrystalCastUiTheme.PushTabStyle();
        if (ImGui.BeginTabBar("CrystalCastSettingsTabs"))
        {
            if (ImGui.BeginTabItem("Rendering"))
            {
                ImGui.Spacing();
                changed |= DrawRendering(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Browser"))
            {
                ImGui.Spacing();
                changed |= DrawBrowserMedia(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Diagnostics"))
            {
                ImGui.Spacing();
                changed |= DrawDiagnostics(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("IPC"))
            {
                ImGui.Spacing();
                DrawIpc(config);
                ImGui.EndTabItem();
            }

#if DEBUG
            if (ImGui.BeginTabItem("Debug"))
            {
                ImGui.Spacing();
                DrawDebugWindows();
                ImGui.EndTabItem();
            }
#endif

            ImGui.EndTabBar();
        }
        CrystalCastUiTheme.PopTabStyle();

        if (changed)
            config.Save();
    }

    private bool DrawRendering(Configuration config)
    {
        var changed = false;
        var activeBrowserScreen = config.GetActiveBrowserScreen();
        var outputMode = FindOutputModeIndex(config.OutputMode);

        CrystalCastUiTheme.DrawSectionHeader("Output layer", "Controls how CrystalCast is composited with the game.");
        ImGui.TextDisabled("Output layer");
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("##CrystalCastOutputLayer", OutputModes[outputMode].Name))
        {
            for (var i = 0; i < OutputModes.Length; i++)
            {
                var selected = i == outputMode;
                if (ImGui.Selectable(OutputModes[i].Name, selected))
                {
                    config.OutputMode = OutputModes[i].Mode;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(GetOutputModeDescription(config.OutputMode));
        ImGui.TextDisabled("UI mask: disabled");

#if DEBUG
        var showMarker = config.ShowDebugMarker;
        if (ImGui.Checkbox("Debug marker", ref showMarker))
        {
            config.ShowDebugMarker = showMarker;
            changed = true;
        }
#endif

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (activeBrowserScreen == null)
        {
            CrystalCastUiTheme.DrawSectionHeader("Screen visibility", "Create a screen from the main CrystalCast window to configure per-screen rendering.");
            ImGui.TextDisabled("No screen selected.");
            return changed;
        }

        var placement = activeBrowserScreen.Placement;
        var occludedAlpha = placement.OccludedAlpha;
        var tolerance = placement.OcclusionTolerance;
        var distanceFade = placement.EnableDistanceFade;
        var fadeStart = placement.FadeStartMeters;
        var fadeStop = placement.FadeStopMeters;
        CrystalCastUiTheme.DrawSectionHeader("Visibility and occlusion", $"Applies to the selected screen: {activeBrowserScreen.Name}");

        if (ImGui.SliderFloat("Occluded alpha", ref occludedAlpha, 0.0f, 1.0f))
        {
            placement.OccludedAlpha = Math.Clamp(occludedAlpha, 0.0f, 1.0f);
            changed = true;
        }

        if (ImGui.InputFloat("Occlusion tolerance", ref tolerance, 0.01f, 0.1f))
        {
            placement.OcclusionTolerance = Math.Max(0.0f, tolerance);
            changed = true;
        }

        var visibilityButtonWidth = Math.Max(80.0f, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
        if (ImGui.Button("Make fully visible", new Vector2(visibilityButtonWidth, 0.0f)))
        {
            placement.OccludedAlpha = 1.0f;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Depth occlusion", new Vector2(visibilityButtonWidth, 0.0f)))
        {
            placement.OccludedAlpha = 0.0f;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        CrystalCastUiTheme.DrawSectionHeader("Distance fade", "Optionally fade the selected screen out over long distances.");
        if (ImGui.Checkbox("Distance fade", ref distanceFade))
        {
            placement.EnableDistanceFade = distanceFade;
            changed = true;
        }

        if (distanceFade)
        {
            if (ImGui.InputFloat("Fade start", ref fadeStart, 1.0f, 5.0f))
            {
                placement.FadeStartMeters = Math.Max(0.0f, fadeStart);
                changed = true;
            }

            if (ImGui.InputFloat("Fade stop", ref fadeStop, 1.0f, 5.0f))
            {
                placement.FadeStopMeters = Math.Max(placement.FadeStartMeters + 0.01f, fadeStop);
                changed = true;
            }
        }

        return changed;
    }

    private static int FindOutputModeIndex(ScreenOutputMode outputMode)
    {
        for (var i = 0; i < OutputModes.Length; i++)
        {
            if (OutputModes[i].Mode == outputMode)
                return i;
        }

        return 0;
    }

    private bool DrawBrowserMedia(Configuration config)
    {
        var changed = false;
        var current = FindBrowserEngineIndex(config.YouTubeBrowserEngine);

        CrystalCastUiTheme.DrawSectionHeader("Browser backend", "Choose how WebView2 pages are captured into screen textures.");
        ImGui.TextDisabled("Browser backend");
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("##CrystalCastBrowserBackend", BrowserEngineNames[current]))
        {
            for (var i = 0; i < BrowserEngineNames.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(BrowserEngineNames[i], selected))
                {
                    config.YouTubeBrowserEngine = BrowserEngines[i];
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(WineEnvironment.IsWine
            ? "Wine detected: WebView2 JPEG capture is forced for every browser option."
            : "Auto uses WebView2 window capture and falls back to JPEG capture when needed.");

        if (WineEnvironment.IsWine)
        {
            ImGui.Spacing();
            var runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError);
            ImGui.TextWrapped(runtimeAvailable
                ? $"Experimental Wine support; WebView2 detected: {runtimeVersion}"
                : $"Experimental Wine support; {runtimeError}");
            if (ImGui.Button("Open Wine WebView2 setup"))
                plugin.ShowWineWebView2Setup();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        CrystalCastUiTheme.DrawSectionHeader("Browser data", "Reset browser profiles when cookies, sign-in, or cached state cause problems.");
        if (ImGui.Button("Clear browser data on restart"))
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

        ImGui.TextDisabled("Clears cookies, local storage, cache, and saved browser state after restarting CrystalCast.");
        if (!string.IsNullOrEmpty(browserDataStatus))
            ImGui.TextWrapped(browserDataStatus);
        return changed;
    }

    private static int FindBrowserEngineIndex(BrowserMediaEngine engine)
    {
        for (var i = 0; i < BrowserEngines.Length; i++)
        {
            if (BrowserEngines[i] == engine)
                return i;
        }

        return 0;
    }

    private bool DrawDiagnostics(Configuration config)
    {
        var changed = false;
        var gpuDiagnostics = config.EnableGpuDiagnostics;
        CrystalCastUiTheme.DrawSectionHeader("Diagnostic options", "Optional checks used when investigating rendering problems.");
        if (ImGui.Checkbox("Enable GPU texture sampling", ref gpuDiagnostics))
        {
            config.EnableGpuDiagnostics = gpuDiagnostics;
            diagnosticsReportUpdatedAtTick = 0;
            changed = true;
        }
        ImGui.TextDisabled("Off by default; sampling performs a GPU readback once per second per active WGC screen.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        CrystalCastUiTheme.DrawSectionHeader("Diagnostic report", "Copy the full report when asking for support.");

        CrystalCastUiTheme.PushPrimaryButtonStyle();
        if (ImGui.Button("Copy full diagnostics"))
        {
            ImGui.SetClipboardText(GetDiagnosticsReport(config, forceRefresh: true));
            diagnosticsCopiedAtTick = Environment.TickCount64;
        }
        CrystalCastUiTheme.PopPrimaryButtonStyle();

        if (diagnosticsCopiedAtTick > 0 && Environment.TickCount64 - diagnosticsCopiedAtTick < 2000)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Copied!");
        }

        ImGui.Spacing();
        if (ImGui.BeginChild("CrystalCastDiagnosticsReport", Vector2.Zero, true, ImGuiWindowFlags.None))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(GetDiagnosticsReport(config, forceRefresh: false));
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        return changed;
    }

    private static string GetOutputModeDescription(ScreenOutputMode outputMode)
    {
        return outputMode switch
        {
            ScreenOutputMode.SceneComposite => "Windows scene integration. Try another layer if output is invisible on this PC.",
            ScreenOutputMode.NativeOverlay => "Uses Dalamud's native overlay rendering path.",
            _ => "Compatibility path drawn through the plugin UI layer.",
        };
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

    private void DrawIpc(Configuration config)
    {
        CrystalCastUiTheme.DrawSectionHeader("Plugin integrations", "Allow other Dalamud plugins to create and control CrystalCast screens.");
        var enabled = config.IpcEnabled;
        if (ImGui.Checkbox("IPC enabled", ref enabled))
        {
            ipc.SetEnabled(enabled);
            config.IpcEnabled = enabled;
        }

        ImGui.TextDisabled(enabled
            ? "IPC endpoints are registered and available to integrations."
            : "IPC-created screens are removed while integration support is disabled.");
    }

#if DEBUG
    private void DrawDebugWindows()
    {
        CrystalCastUiTheme.DrawSectionHeader("First-run windows", "Preview setup windows without editing the saved configuration manually.");

        if (ImGui.Button("Open Wine WebView2 setup"))
            plugin.ShowWineWebView2Setup();

        if (ImGui.Button("Open onboarding guide"))
            plugin.ShowFirstRunGuide();
    }
#endif
}
