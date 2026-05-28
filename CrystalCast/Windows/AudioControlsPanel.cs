using CrystalCast.Rendering;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class AudioControlsPanel(WorldScreenManager renderer)
{
    public bool Draw(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoAudio(config);
                changed |= DrawSpatialAudio(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= activeScreen.ProviderKind switch
                {
                    BrowserSourceProviderKind.Twitch => DrawTwitchAudio(activeScreen),
                    BrowserSourceProviderKind.Dailymotion => DrawDailymotionAudio(activeScreen),
                    BrowserSourceProviderKind.Vimeo => DrawVimeoAudio(activeScreen),
                    _ => DrawYouTubeAudio(activeScreen),
                };
                changed |= DrawSpatialAudio(activeScreen);
                break;
        }

        return changed;
    }

    private static bool DrawLocalVideoAudio(Configuration config)
    {
        var changed = false;
        var audioEnabled = config.AudioEnabled;
        var audioVolume = config.AudioVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable local video audio", ref audioEnabled))
        {
            config.AudioEnabled = audioEnabled;
            changed = true;
        }

        if (config.AudioEnabled && ImGui.SliderFloat("Audio volume", ref audioVolume, 0.0f, 1.0f))
        {
            config.AudioVolume = Math.Clamp(audioVolume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawYouTubeAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.YouTubeAudioEnabled;
        var volume = screen.YouTubeVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.YouTubeAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.YouTubeAudioEnabled && ImGui.SliderFloat("YouTube volume", ref volume, 0.0f, 1.0f))
        {
            screen.YouTubeVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawTwitchAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.TwitchAudioEnabled;
        var volume = screen.TwitchVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.TwitchAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.TwitchAudioEnabled && ImGui.SliderFloat("Twitch volume", ref volume, 0.0f, 1.0f))
        {
            screen.TwitchVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawDailymotionAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.DailymotionAudioEnabled;
        var volume = screen.DailymotionVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.DailymotionAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.DailymotionAudioEnabled && ImGui.SliderFloat("Dailymotion volume", ref volume, 0.0f, 1.0f))
        {
            screen.DailymotionVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawVimeoAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.VimeoAudioEnabled;
        var volume = screen.VimeoVolume;

        ImGui.TextUnformatted("Playback audio");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.VimeoAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.VimeoAudioEnabled && ImGui.SliderFloat("Vimeo volume", ref volume, 0.0f, 1.0f))
        {
            screen.VimeoVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private bool DrawSpatialAudio(Configuration config)
    {
        var changed = false;
        var enabled = config.SpatialAudioEnabled;
        var fullRadius = config.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = config.SpatialAudioSilentRadiusMeters;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Distance falloff");
        if (ImGui.Checkbox("Spatial audio", ref enabled))
        {
            config.SpatialAudioEnabled = enabled;
            changed = true;
        }

        if (config.SpatialAudioEnabled)
        {
            if (ImGui.InputFloat("Full volume radius", ref fullRadius, 0.5f, 2.0f))
            {
                config.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
                if (config.SpatialAudioSilentRadiusMeters <= config.SpatialAudioFullVolumeRadiusMeters)
                    config.SpatialAudioSilentRadiusMeters = config.SpatialAudioFullVolumeRadiusMeters + 0.1f;
                changed = true;
            }

            if (ImGui.InputFloat("Silent radius", ref silentRadius, 0.5f, 2.0f))
            {
                config.SpatialAudioSilentRadiusMeters = Math.Max(config.SpatialAudioFullVolumeRadiusMeters + 0.1f, silentRadius);
                changed = true;
            }

            ImGui.TextDisabled($"Distance: {renderer.AudioDistanceMeters:0.0} m  Falloff: {FormatPercent(renderer.SpatialAudioAttenuation)}");
            ImGui.TextDisabled($"Applied volume: {FormatPercent(renderer.EffectiveAudioVolume)}");
        }
        else
        {
            ImGui.TextDisabled("Distance falloff disabled");
        }

        return changed;
    }

    private bool DrawSpatialAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var enabled = screen.SpatialAudioEnabled;
        var fullRadius = screen.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = screen.SpatialAudioSilentRadiusMeters;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Distance falloff");
        if (ImGui.Checkbox("Spatial audio", ref enabled))
        {
            screen.SpatialAudioEnabled = enabled;
            changed = true;
        }

        if (screen.SpatialAudioEnabled)
        {
            if (ImGui.InputFloat("Full volume radius", ref fullRadius, 0.5f, 2.0f))
            {
                screen.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
                if (screen.SpatialAudioSilentRadiusMeters <= screen.SpatialAudioFullVolumeRadiusMeters)
                    screen.SpatialAudioSilentRadiusMeters = screen.SpatialAudioFullVolumeRadiusMeters + 0.1f;
                changed = true;
            }

            if (ImGui.InputFloat("Silent radius", ref silentRadius, 0.5f, 2.0f))
            {
                screen.SpatialAudioSilentRadiusMeters = Math.Max(screen.SpatialAudioFullVolumeRadiusMeters + 0.1f, silentRadius);
                changed = true;
            }

            ImGui.TextDisabled($"Distance: {renderer.AudioDistanceMeters:0.0} m  Falloff: {FormatPercent(renderer.SpatialAudioAttenuation)}");
            ImGui.TextDisabled($"Applied volume: {FormatPercent(renderer.EffectiveAudioVolume)}");
        }
        else
        {
            ImGui.TextDisabled("Distance falloff disabled");
        }

        return changed;
    }

    private static string FormatPercent(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
            return "0%";

        var percent = value * 100.0f;
        return percent < 1.0f
            ? "<1%"
            : $"{percent:0}%";
    }
}
