using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly string[] SourceNames =
    [
        "Static image",
        "Generated frames",
        "Local video",
        "Browser capture (later)",
        "Offscreen browser (later)",
    ];

    private readonly Plugin plugin;
    private readonly WorldScreenRenderer renderer;
    private readonly ScreenStateIpc ipc;

    public MainWindow(Plugin plugin, WorldScreenRenderer renderer, ScreenStateIpc ipc)
        : base("CrystalCast###CrystalCastMain")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

        Size = new Vector2(520, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;

        changed |= DrawEnabled(config);
        ImGui.Separator();
        changed |= DrawPlacement(config);
        ImGui.Separator();
        changed |= DrawSource(config);
        ImGui.Separator();
        changed |= DrawDepth(config);
        ImGui.Separator();
        changed |= DrawDebug(config);
        ImGui.Separator();
        DrawStatus();

        if (changed)
            SaveAndPublish();

        ImGui.Separator();
        if (ImGui.Button("Save"))
            config.Save();

        ImGui.SameLine();
        if (ImGui.Button("Broadcast state"))
            ipc.PublishLocalState();
    }

    private bool DrawEnabled(Configuration config)
    {
        var changed = false;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        var paused = config.PlaybackPaused;
        if (ImGui.Checkbox("Pause dynamic source", ref paused))
        {
            config.PlaybackPaused = paused;
            changed = true;
        }

        ImGui.TextUnformatted($"Screen ID: {config.ScreenId}");
        return changed;
    }

    private static bool DrawDebug(Configuration config)
    {
        var changed = false;
        var showMarker = config.ShowDebugMarker;
        if (ImGui.Checkbox("Debug marker", ref showMarker))
        {
            config.ShowDebugMarker = showMarker;
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

        return changed;
    }

    private bool DrawPlacement(Configuration config)
    {
        var changed = false;

        if (ImGui.Button("Place in front of player"))
        {
            changed |= renderer.PlaceInFrontOfPlayer();
        }

        var position = new Vector3(config.PositionX, config.PositionY, config.PositionZ);
        if (ImGui.InputFloat3("Position", ref position))
        {
            config.PositionX = position.X;
            config.PositionY = position.Y;
            config.PositionZ = position.Z;
            changed = true;
        }

        var rotation = new Vector3(config.YawRadians, config.PitchRadians, config.RollRadians);
        if (ImGui.InputFloat3("Yaw / Pitch / Roll", ref rotation))
        {
            config.YawRadians = rotation.X;
            config.PitchRadians = rotation.Y;
            config.RollRadians = rotation.Z;
            changed = true;
        }

        var width = config.WidthMeters;
        if (ImGui.InputFloat("Width meters", ref width, 0.1f, 0.5f))
        {
            config.WidthMeters = Math.Max(0.1f, width);
            changed = true;
        }

        var height = config.HeightMeters;
        if (ImGui.InputFloat("Height meters", ref height, 0.1f, 0.5f))
        {
            config.HeightMeters = Math.Max(0.1f, height);
            changed = true;
        }

        return changed;
    }

    private bool DrawSource(Configuration config)
    {
        var changed = false;
        var current = Math.Clamp((int)config.SourceKind, 0, SourceNames.Length - 1);

        if (ImGui.BeginCombo("Source", SourceNames[current]))
        {
            for (var i = 0; i < SourceNames.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(SourceNames[i], selected))
                {
                    config.SourceKind = (ScreenSourceKind)i;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        switch (config.SourceKind)
        {
            case ScreenSourceKind.Generated:
                changed |= DrawGeneratedSource(config);
                break;
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoSource(config);
                break;
            case ScreenSourceKind.BrowserCapture:
            case ScreenSourceKind.OffscreenBrowser:
                ImGui.TextUnformatted("This source is reserved for a later phase.");
                break;
        }

        return changed;
    }

    private static bool DrawGeneratedSource(Configuration config)
    {
        var changed = false;
        var width = config.GeneratedWidth;
        var height = config.GeneratedHeight;
        var fps = config.GeneratedFps;

        if (ImGui.InputInt("Generated width", ref width))
        {
            config.GeneratedWidth = Math.Clamp(width, 64, 3840);
            changed = true;
        }

        if (ImGui.InputInt("Generated height", ref height))
        {
            config.GeneratedHeight = Math.Clamp(height, 64, 2160);
            changed = true;
        }

        if (ImGui.InputFloat("Generated FPS", ref fps, 1.0f, 5.0f))
        {
            config.GeneratedFps = Math.Clamp(fps, 1.0f, 120.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawLocalVideoSource(Configuration config)
    {
        var changed = false;
        var ffmpegPath = config.FfmpegPath;
        var videoPath = config.LocalVideoPath;
        var width = config.LocalVideoWidth;
        var height = config.LocalVideoHeight;
        var fps = config.LocalVideoFps;
        var loop = config.LoopLocalVideo;

        if (ImGui.InputText("FFmpeg path", ref ffmpegPath, 512))
        {
            config.FfmpegPath = ffmpegPath;
            changed = true;
        }

        if (ImGui.InputText("Video path", ref videoPath, 1024))
        {
            config.LocalVideoPath = videoPath;
            changed = true;
        }

        if (ImGui.InputInt("Output width", ref width))
        {
            config.LocalVideoWidth = Math.Clamp(width, 64, 3840);
            changed = true;
        }

        if (ImGui.InputInt("Output height", ref height))
        {
            config.LocalVideoHeight = Math.Clamp(height, 64, 2160);
            changed = true;
        }

        if (ImGui.InputFloat("Output FPS", ref fps, 1.0f, 5.0f))
        {
            config.LocalVideoFps = Math.Clamp(fps, 1.0f, 120.0f);
            changed = true;
        }

        if (ImGui.Checkbox("Loop video", ref loop))
        {
            config.LoopLocalVideo = loop;
            changed = true;
        }

        return changed;
    }

    private static bool DrawDepth(Configuration config)
    {
        var changed = false;
        var occludedAlpha = config.OccludedAlpha;
        var tolerance = config.OcclusionTolerance;
        var distanceFade = config.EnableDistanceFade;
        var fadeStart = config.FadeStartMeters;
        var fadeStop = config.FadeStopMeters;

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

    private void DrawStatus()
    {
        ImGui.TextUnformatted($"Renderer: {renderer.Status}");
        ImGui.TextUnformatted($"Draw: {renderer.LastDrawStatus}");
        ImGui.TextUnformatted($"Source: {renderer.SourceName}");
        ImGui.TextUnformatted($"Source status: {renderer.SourceStatus}");
        ImGui.TextUnformatted($"Texture: {renderer.TextureWidth} x {renderer.TextureHeight}");
        ImGui.TextUnformatted($"Uploads: {renderer.UploadCount}");
        ImGui.TextUnformatted($"Last upload: {renderer.LastUploadMilliseconds:0.000} ms");
        ImGui.TextUnformatted($"Frame age: {renderer.FrameAgeMilliseconds} ms");
        ImGui.TextUnformatted($"Remote screens in IPC store: {ipc.RemoteScreens.Count}");
    }

    private void SaveAndPublish()
    {
        ipc.PublishLocalState();
    }
}
