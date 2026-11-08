using System.Numerics;
using CrystalCast.Rendering;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class AudioControlsPanel(WorldScreenManager renderer)
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;

    public bool Draw(BrowserScreenProfile activeScreen)
    {
        var audio = GetAudioSettings(activeScreen);
        var changed = DrawPlaybackVolume(activeScreen, audio);
        Ui.VerticalSpace(24.0f);
        changed |= DrawSpatialAudio(activeScreen);
        return changed;
    }

    private static bool DrawPlaybackVolume(BrowserScreenProfile screen, AudioSettingsAccessor audio)
    {
        var changed = false;
        var audioEnabled = audio.GetEnabled(screen);
        Ui.RowLabel("Browser audio", LabelWidth, LabelGap);
        if (Ui.Switch("##CrystalCastBrowserAudio", ref audioEnabled))
        {
            audio.SetEnabled(screen, audioEnabled);
            changed = true;
        }

        var volume = audio.GetVolume(screen);
        Ui.RowLabel("Volume", LabelWidth, LabelGap);
        if (Ui.Slider(
                "##CrystalCastVolume",
                ref volume,
                0.0f,
                1.0f,
                FormatPercent,
                disabled: !audio.GetEnabled(screen)))
        {
            audio.SetVolume(screen, Math.Clamp(volume, 0.0f, 1.0f));
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The transport bar's volume control: a mute toggle and a slider that slides out beside it. Only
    /// <paramref name="revealWidth"/> of the <paramref name="sliderWidth"/>-wide slider is visible, which lets the
    /// caller animate it open. It edits the same provider audio settings as the Audio tab, so the two stay in sync.
    /// </summary>
    internal static bool DrawCompactVolume(
        BrowserScreenProfile screen,
        string idSuffix,
        float sliderWidth,
        float revealWidth,
        out bool sliderActive,
        bool disabled = false)
    {
        sliderActive = false;
        var changed = false;
        var audio = GetAudioSettings(screen);
        var enabled = audio.GetEnabled(screen);
        if (Ui.IconButton(
                $"##Mute{idSuffix}",
                enabled ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                enabled ? "Mute browser audio" : "Turn browser audio on",
                disabled: disabled))
        {
            audio.SetEnabled(screen, !enabled);
            changed = true;
        }

        if (revealWidth < 1.0f)
            return changed;

        ImGui.SameLine(0.0f, S(4.0f));
        var volume = audio.GetVolume(screen);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = ImGui.BeginChild($"##VolumeColumn{idSuffix}", new Vector2(revealWidth, Ui.FrameHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();
        var moved = false;
        if (open)
        {
            // Keep the cap clear of the clip edge while revealing.
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(2.0f));
            moved = Ui.Slider($"##Volume{idSuffix}", ref volume, 0.0f, 1.0f, FormatPercent, disabled: disabled || !audio.GetEnabled(screen), readoutWidth: 0.0f, trackWidth: sliderWidth - S(4.0f));
            sliderActive = ImGui.IsItemActive();
        }

        ImGui.EndChild();
        if (moved)
        {
            audio.SetVolume(screen, Math.Clamp(volume, 0.0f, 1.0f));
            changed = true;
        }

        return changed;
    }

    private bool DrawSpatialAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        Ui.SectionHeader("Spatial audio", "Fade browser volume as you move away from the selected screen.");

        var enabled = screen.SpatialAudioEnabled;
        Ui.RowLabel("Distance falloff", LabelWidth, LabelGap);
        if (Ui.Switch("##CrystalCastSpatialAudio", ref enabled))
        {
            screen.SpatialAudioEnabled = enabled;
            changed = true;
        }

        var fullRadius = screen.SpatialAudioFullVolumeRadiusMeters;
        Ui.RowLabel("Full volume radius", LabelWidth, LabelGap);
        if (Ui.DragFloat("##CrystalCastFullRadius", ref fullRadius, 0.1f, format: "%.1f m", width: S(120.0f), disabled: !screen.SpatialAudioEnabled))
        {
            screen.SpatialAudioFullVolumeRadiusMeters = Math.Max(0.0f, fullRadius);
            if (screen.SpatialAudioSilentRadiusMeters <= screen.SpatialAudioFullVolumeRadiusMeters)
                screen.SpatialAudioSilentRadiusMeters = screen.SpatialAudioFullVolumeRadiusMeters + 0.1f;
            changed = true;
        }

        var silentRadius = screen.SpatialAudioSilentRadiusMeters;
        Ui.RowLabel("Silent radius", LabelWidth, LabelGap);
        if (Ui.DragFloat("##CrystalCastSilentRadius", ref silentRadius, 0.1f, format: "%.1f m", width: S(120.0f), disabled: !screen.SpatialAudioEnabled))
        {
            screen.SpatialAudioSilentRadiusMeters = Math.Max(screen.SpatialAudioFullVolumeRadiusMeters + 0.1f, silentRadius);
            changed = true;
        }

        Ui.RowLabel("Distance", LabelWidth, LabelGap);
        ReadoutRow(screen.SpatialAudioEnabled
            ? $"{renderer.AudioDistanceMeters:0.0} m · falloff {FormatPercent(renderer.SpatialAudioAttenuation)}"
            : "Falloff off");

        Ui.RowLabel("Applied volume", LabelWidth, LabelGap);
        ReadoutRow(FormatPercent(renderer.EffectiveAudioVolume));
        return changed;
    }

    private static void ReadoutRow(string text)
    {
        Ui.RowText(text, TextSecondary, f => f.Mono);
    }

    internal static AudioSettingsAccessor GetAudioSettings(BrowserScreenProfile screen)
    {
        return screen.ProviderKind switch
        {
            BrowserSourceProviderKind.Twitch => new AudioSettingsAccessor(
                s => s.TwitchAudioEnabled, (s, v) => s.TwitchAudioEnabled = v,
                s => s.TwitchVolume, (s, v) => s.TwitchVolume = v),
            BrowserSourceProviderKind.Dailymotion => new AudioSettingsAccessor(
                s => s.DailymotionAudioEnabled, (s, v) => s.DailymotionAudioEnabled = v,
                s => s.DailymotionVolume, (s, v) => s.DailymotionVolume = v),
            BrowserSourceProviderKind.Vimeo => new AudioSettingsAccessor(
                s => s.VimeoAudioEnabled, (s, v) => s.VimeoAudioEnabled = v,
                s => s.VimeoVolume, (s, v) => s.VimeoVolume = v),
            BrowserSourceProviderKind.GenericWeb => new AudioSettingsAccessor(
                s => s.GenericWebAudioEnabled, (s, v) => s.GenericWebAudioEnabled = v,
                s => s.GenericWebVolume, (s, v) => s.GenericWebVolume = v),
            _ => new AudioSettingsAccessor(
                s => s.YouTubeAudioEnabled, (s, v) => s.YouTubeAudioEnabled = v,
                s => s.YouTubeVolume, (s, v) => s.YouTubeVolume = v),
        };
    }

    internal static string FormatPercent(float value)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
            return "0%";

        var percent = value * 100.0f;
        return percent < 1.0f
            ? "<1%"
            : $"{percent:0}%";
    }

    internal sealed record AudioSettingsAccessor(
        Func<BrowserScreenProfile, bool> GetEnabled,
        Action<BrowserScreenProfile, bool> SetEnabled,
        Func<BrowserScreenProfile, float> GetVolume,
        Action<BrowserScreenProfile, float> SetVolume);
}
