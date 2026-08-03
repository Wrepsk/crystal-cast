using CrystalCast.Rendering;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class AudioControlsPanel(WorldScreenManager renderer)
{
    public bool Draw(BrowserScreenProfile activeScreen)
    {
        var changed = activeScreen.ProviderKind switch
        {
            BrowserSourceProviderKind.Twitch => DrawTwitchAudio(activeScreen),
            BrowserSourceProviderKind.Dailymotion => DrawDailymotionAudio(activeScreen),
            BrowserSourceProviderKind.Vimeo => DrawVimeoAudio(activeScreen),
            BrowserSourceProviderKind.GenericWeb => DrawGenericWebAudio(activeScreen),
            _ => DrawYouTubeAudio(activeScreen),
        };
        changed |= DrawSpatialAudio(activeScreen);

        return changed;
    }

    private static bool DrawYouTubeAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.YouTubeAudioEnabled;
        var volume = screen.YouTubeVolume;

        ImGui.TextUnformatted("Playback volume");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.YouTubeAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.YouTubeAudioEnabled && CrystalCastUiWidgets.SliderFloat("YouTube volume", ref volume, 0.0f, 1.0f))
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

        ImGui.TextUnformatted("Playback volume");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.TwitchAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.TwitchAudioEnabled && CrystalCastUiWidgets.SliderFloat("Twitch volume", ref volume, 0.0f, 1.0f))
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

        ImGui.TextUnformatted("Playback volume");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.DailymotionAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.DailymotionAudioEnabled && CrystalCastUiWidgets.SliderFloat("Dailymotion volume", ref volume, 0.0f, 1.0f))
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

        ImGui.TextUnformatted("Playback volume");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.VimeoAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.VimeoAudioEnabled && CrystalCastUiWidgets.SliderFloat("Vimeo volume", ref volume, 0.0f, 1.0f))
        {
            screen.VimeoVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawGenericWebAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var audioEnabled = screen.GenericWebAudioEnabled;
        var volume = screen.GenericWebVolume;

        ImGui.TextUnformatted("Playback volume");
        if (ImGui.Checkbox("Enable browser audio", ref audioEnabled))
        {
            screen.GenericWebAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.GenericWebAudioEnabled && CrystalCastUiWidgets.SliderFloat("Generic Web volume", ref volume, 0.0f, 1.0f))
        {
            screen.GenericWebVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
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
        ImGui.TextUnformatted("Spatial audio");
        ImGui.TextDisabled("Fade browser volume as you move away from the selected screen.");
        if (ImGui.Checkbox("Spatial audio", ref enabled))
        {
            screen.SpatialAudioEnabled = enabled;
            changed = true;
        }

        if (screen.SpatialAudioEnabled)
        {
            if (CrystalCastUiWidgets.DragFloat("Full volume radius", ref fullRadius, 0.1f))
            {
                screen.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
                if (screen.SpatialAudioSilentRadiusMeters <= screen.SpatialAudioFullVolumeRadiusMeters)
                    screen.SpatialAudioSilentRadiusMeters = screen.SpatialAudioFullVolumeRadiusMeters + 0.1f;
                changed = true;
            }

            if (CrystalCastUiWidgets.DragFloat("Silent radius", ref silentRadius, 0.1f))
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
