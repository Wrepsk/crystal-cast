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

    private readonly Plugin plugin;
    private readonly WorldScreenRenderer renderer;
    private readonly ScreenStateIpc ipc;

    public ConfigWindow(Plugin plugin, WorldScreenRenderer renderer, ScreenStateIpc ipc)
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

        DrawSectionTitle("Rendering");
        changed |= DrawRendering(config);
        DrawSectionTitle("Diagnostics");
        DrawDiagnostics();
        DrawSectionTitle("IPC");
        DrawIpc(config);

        if (changed)
            config.Save();
    }

    private static void DrawSectionTitle(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(label);
    }

    private static bool DrawRendering(Configuration config)
    {
        var changed = false;
        var showMarker = config.ShowDebugMarker;
        var occludedAlpha = config.OccludedAlpha;
        var tolerance = config.OcclusionTolerance;
        var distanceFade = config.EnableDistanceFade;
        var fadeStart = config.FadeStartMeters;
        var fadeStop = config.FadeStopMeters;
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

        if (ImGui.SliderFloat("Occluded alpha", ref occludedAlpha, 0.0f, 1.0f))
        {
            config.OccludedAlpha = Math.Clamp(occludedAlpha, 0.0f, 1.0f);
            changed = true;
        }

        if (ImGui.InputFloat("Occlusion tolerance", ref tolerance, 0.01f, 0.1f))
        {
            config.OcclusionTolerance = Math.Max(0.0f, tolerance);
            changed = true;
        }

        if (ImGui.Button("Make fully visible"))
        {
            config.OccludedAlpha = 1.0f;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Depth occlusion"))
        {
            config.OccludedAlpha = 0.0f;
            changed = true;
        }

        if (ImGui.Checkbox("Distance fade", ref distanceFade))
        {
            config.EnableDistanceFade = distanceFade;
            changed = true;
        }

        if (config.EnableDistanceFade)
        {
            if (ImGui.InputFloat("Fade start", ref fadeStart, 1.0f, 5.0f))
            {
                config.FadeStartMeters = Math.Max(0.0f, fadeStart);
                changed = true;
            }

            if (ImGui.InputFloat("Fade stop", ref fadeStop, 1.0f, 5.0f))
            {
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

    private void DrawDiagnostics()
    {
        ImGui.TextUnformatted($"Renderer: {renderer.Status}");
        ImGui.TextUnformatted($"Draw: {renderer.LastDrawStatus}");
        ImGui.TextUnformatted($"Source: {renderer.SourceName}");
        ImGui.TextUnformatted($"Source status: {renderer.SourceStatus}");
        ImGui.TextUnformatted($"Audio: {renderer.AudioStatus}");
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
        ImGui.TextUnformatted($"Screen ID: {config.ScreenId}");
        ImGui.TextUnformatted($"Remote screens in IPC store: {ipc.RemoteScreens.Count}");

        if (ImGui.Button("Save"))
            config.Save();

        ImGui.SameLine();
        if (ImGui.Button("Broadcast state"))
            ipc.PublishLocalState();
    }
}
