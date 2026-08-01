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

    public ConfigWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast Settings###CrystalCastConfig")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

        Size = new Vector2(520, 520);
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

            ImGui.EndTabBar();
        }

        if (changed)
            config.Save();
    }

    private bool DrawRendering(Configuration config)
    {
        var changed = false;
        var activeBrowserScreen = config.GetActiveBrowserScreen();
        var placement = activeBrowserScreen.Placement;
        var occludedAlpha = placement.OccludedAlpha;
        var tolerance = placement.OcclusionTolerance;
        var distanceFade = placement.EnableDistanceFade;
        var fadeStart = placement.FadeStartMeters;
        var fadeStop = placement.FadeStopMeters;
        var outputMode = FindOutputModeIndex(config.OutputMode);

        if (ImGui.BeginCombo("Output layer", OutputModes[outputMode].Name))
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

        ImGui.TextDisabled("UI mask: disabled");

#if DEBUG
        var showMarker = config.ShowDebugMarker;
        if (ImGui.Checkbox("Debug marker", ref showMarker))
        {
            config.ShowDebugMarker = showMarker;
            changed = true;
        }
#endif

        ImGui.TextDisabled($"Visual settings: {activeBrowserScreen.Name}");

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

        if (ImGui.Button("Make fully visible"))
        {
            placement.OccludedAlpha = 1.0f;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Depth occlusion"))
        {
            placement.OccludedAlpha = 0.0f;
            changed = true;
        }

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

        if (ImGui.BeginCombo("Browser backend", BrowserEngineNames[current]))
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
            var runtimeAvailable = WebView2BrowserFrameSource.TryGetWebView2Runtime(out var runtimeVersion, out var runtimeError);
            ImGui.TextWrapped(runtimeAvailable
                ? $"Experimental Wine support; WebView2 detected: {runtimeVersion}"
                : $"Experimental Wine support; {runtimeError}");
            if (ImGui.Button("Open Wine WebView2 setup"))
                plugin.ShowWineWebView2Setup();
        }

        ImGui.Spacing();
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
        if (ImGui.Checkbox("Enable GPU texture sampling", ref gpuDiagnostics))
        {
            config.EnableGpuDiagnostics = gpuDiagnostics;
            changed = true;
        }
        ImGui.TextDisabled("Off by default; sampling performs a GPU readback once per second per active WGC screen.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Renderer: {renderer.Status}");
        ImGui.TextUnformatted($"Draw: {renderer.LastDrawStatus}");
        ImGui.TextUnformatted($"Scene composite: {renderer.SceneCompositeStatus}");
        ImGui.TextUnformatted($"Source: {renderer.SourceName}");
        ImGui.TextUnformatted($"Source status: {renderer.SourceStatus}");
        ImGui.TextUnformatted($"Audio: {renderer.AudioStatus}");
        ImGui.TextUnformatted($"Browser runtimes: {renderer.ActiveBrowserRuntimeCount}");
        ImGui.TextUnformatted($"Browser budget: {renderer.BrowserResourceBudgetStatus}");
        ImGui.TextUnformatted($"Texture: {renderer.TextureWidth} x {renderer.TextureHeight}");
        ImGui.TextUnformatted($"Uploads: {renderer.UploadCount}");
        ImGui.TextUnformatted($"Last upload: {renderer.LastUploadMilliseconds:0.000} ms");
        ImGui.TextUnformatted($"Frame age: {renderer.FrameAgeMilliseconds} ms");
        ImGui.TextUnformatted($"Audio distance: {renderer.AudioDistanceMeters:0.00} m");
        ImGui.TextUnformatted($"Audio falloff: {renderer.SpatialAudioAttenuation * 100.0f:0}%");
        ImGui.TextUnformatted($"Effective audio volume: {renderer.EffectiveAudioVolume * 100.0f:0}%");
        return changed;
    }

    private void DrawIpc(Configuration config)
    {
        var enabled = config.IpcEnabled;
        if (ImGui.Checkbox("IPC enabled", ref enabled))
        {
            ipc.SetEnabled(enabled);
            config.IpcEnabled = enabled;
        }
    }
}
