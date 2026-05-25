using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class LocalVideoSourceControlsPanel
{
    public bool Draw(Configuration config)
    {
        var changed = false;
        var ffmpegPath = config.FfmpegPath;
        var videoPath = config.LocalVideoPath;
        var scalePercent = config.LocalVideoScalePercent;
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

        if (ImGui.SliderFloat("Scale percent", ref scalePercent, 5.0f, 200.0f))
        {
            config.LocalVideoScalePercent = Math.Clamp(scalePercent, 5.0f, 200.0f);
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
}
