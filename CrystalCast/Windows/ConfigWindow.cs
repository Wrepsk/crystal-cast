using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] OutputModeNames =
    [
        "ImGui overlay",
        "Native overlay",
        "Scene composite (Windows only)",
    ];

    private static readonly string[] BrowserEngineNames =
        ["Auto (CEF, then WebView2)", "CEF offscreen", "WebView2 capture"];

    private static readonly BrowserMediaEngine[] BrowserEngines =
        [BrowserMediaEngine.Auto, BrowserMediaEngine.CefOffScreen, BrowserMediaEngine.WebView2Capture];

    private readonly Plugin plugin;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;

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
                DrawDiagnostics();
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
        var browserVisual = config.SourceKind == ScreenSourceKind.YouTubeBrowser;
        var placement = activeBrowserScreen.Placement;
        var showMarker = config.ShowDebugMarker;
        var occludedAlpha = browserVisual ? placement.OccludedAlpha : config.OccludedAlpha;
        var tolerance = browserVisual ? placement.OcclusionTolerance : config.OcclusionTolerance;
        var distanceFade = browserVisual ? placement.EnableDistanceFade : config.EnableDistanceFade;
        var fadeStart = browserVisual ? placement.FadeStartMeters : config.FadeStartMeters;
        var fadeStop = browserVisual ? placement.FadeStopMeters : config.FadeStopMeters;
        var outputMode = GetOutputModeIndex(config.OutputMode);

        if (ImGui.BeginCombo("Output layer", OutputModeNames[outputMode]))
        {
            for (var i = 0; i < OutputModeNames.Length; i++)
            {
                var selected = i == outputMode;
                if (ImGui.Selectable(OutputModeNames[i], selected))
                {
                    config.OutputMode = i switch
                    {
                        1 => Configuration.OutputModeNativeOverlay,
                        2 => Configuration.OutputModeSceneComposite,
                        _ => Configuration.OutputModeImGuiOverlay,
                    };
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled("UI mask: disabled");

        if (ImGui.Checkbox("Debug marker", ref showMarker))
        {
            config.ShowDebugMarker = showMarker;
            changed = true;
        }

        ImGui.TextDisabled(browserVisual
            ? $"Visual settings: {activeBrowserScreen.Name}"
            : "Visual settings: Local video");

        if (ImGui.SliderFloat("Occluded alpha", ref occludedAlpha, 0.0f, 1.0f))
        {
            if (browserVisual)
                placement.OccludedAlpha = Math.Clamp(occludedAlpha, 0.0f, 1.0f);
            else
                config.OccludedAlpha = Math.Clamp(occludedAlpha, 0.0f, 1.0f);
            changed = true;
        }

        if (ImGui.InputFloat("Occlusion tolerance", ref tolerance, 0.01f, 0.1f))
        {
            if (browserVisual)
                placement.OcclusionTolerance = Math.Max(0.0f, tolerance);
            else
                config.OcclusionTolerance = Math.Max(0.0f, tolerance);
            changed = true;
        }

        if (ImGui.Button("Make fully visible"))
        {
            if (browserVisual)
                placement.OccludedAlpha = 1.0f;
            else
                config.OccludedAlpha = 1.0f;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Depth occlusion"))
        {
            if (browserVisual)
                placement.OccludedAlpha = 0.0f;
            else
                config.OccludedAlpha = 0.0f;
            changed = true;
        }

        if (ImGui.Checkbox("Distance fade", ref distanceFade))
        {
            if (browserVisual)
                placement.EnableDistanceFade = distanceFade;
            else
                config.EnableDistanceFade = distanceFade;
            changed = true;
        }

        if (distanceFade)
        {
            if (ImGui.InputFloat("Fade start", ref fadeStart, 1.0f, 5.0f))
            {
                if (browserVisual)
                    placement.FadeStartMeters = Math.Max(0.0f, fadeStart);
                else
                    config.FadeStartMeters = Math.Max(0.0f, fadeStart);
                changed = true;
            }

            if (ImGui.InputFloat("Fade stop", ref fadeStop, 1.0f, 5.0f))
            {
                if (browserVisual)
                    placement.FadeStopMeters = Math.Max(placement.FadeStartMeters + 0.01f, fadeStop);
                else
                    config.FadeStopMeters = Math.Max(config.FadeStartMeters + 0.01f, fadeStop);
                changed = true;
            }
        }

        return changed;
    }

    private static int GetOutputModeIndex(int outputMode)
    {
        return outputMode switch
        {
            Configuration.OutputModeNativeOverlay => 1,
            Configuration.OutputModeSceneComposite or 3 => 2,
            _ => 0,
        };
    }

    private static bool DrawBrowserMedia(Configuration config)
    {
        var changed = false;
        var current = FindBrowserEngineIndex(config.YouTubeBrowserEngine);

        if (ImGui.BeginCombo("YouTube backend", BrowserEngineNames[current]))
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

        ImGui.TextDisabled("Auto prefers CEF offscreen and falls back to WebView2 capture.");
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

    private void DrawDiagnostics()
    {
        ImGui.TextUnformatted($"Renderer: {renderer.Status}");
        ImGui.TextUnformatted($"Draw: {renderer.LastDrawStatus}");
        ImGui.TextUnformatted($"Scene composite: {renderer.SceneCompositeStatus}");
        ImGui.TextUnformatted($"Source: {renderer.SourceName}");
        ImGui.TextUnformatted($"Source status: {renderer.SourceStatus}");
        ImGui.TextUnformatted($"Audio: {renderer.AudioStatus}");
        ImGui.TextUnformatted($"Browser runtimes: {renderer.ActiveBrowserRuntimeCount}");
        ImGui.TextUnformatted($"Texture: {renderer.TextureWidth} x {renderer.TextureHeight}");
        ImGui.TextUnformatted($"Uploads: {renderer.UploadCount}");
        ImGui.TextUnformatted($"Last upload: {renderer.LastUploadMilliseconds:0.000} ms");
        ImGui.TextUnformatted($"Frame age: {renderer.FrameAgeMilliseconds} ms");
        ImGui.TextUnformatted($"Audio distance: {renderer.AudioDistanceMeters:0.00} m");
        ImGui.TextUnformatted($"Audio falloff: {renderer.SpatialAudioAttenuation * 100.0f:0}%");
        ImGui.TextUnformatted($"Effective audio volume: {renderer.EffectiveAudioVolume * 100.0f:0}%");
    }

    private void DrawIpc(Configuration config)
    {
        var activeBrowserScreen = config.GetActiveBrowserScreen();
        var screenId = config.SourceKind == ScreenSourceKind.YouTubeBrowser ? activeBrowserScreen.ScreenId : config.ScreenId;
        ImGui.TextUnformatted($"Active screen ID: {screenId}");
        ImGui.TextUnformatted($"Remote screens in IPC store: {ipc.RemoteScreens.Count}");

        if (ImGui.Button("Save"))
            config.Save();

        ImGui.SameLine();
        if (ImGui.Button("Broadcast state"))
            ipc.PublishLocalState();
    }
}
