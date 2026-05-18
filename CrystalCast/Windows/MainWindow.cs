using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly (string Name, int Width, int Height)[] YouTubeResolutionPresets =
    [
        ("360p (640 x 360)", 640, 360),
        ("480p (854 x 480)", 854, 480),
        ("720p (1280 x 720)", 1280, 720),
        ("1080p (1920 x 1080)", 1920, 1080),
        ("1440p (2560 x 1440)", 2560, 1440),
        ("4K (3840 x 2160)", 3840, 2160),
    ];

    private static readonly string[] SourceNames =
        ["Local video", "YouTube browser"];

    private static readonly ScreenSourceKind[] SourceKinds =
        [ScreenSourceKind.LocalVideo, ScreenSourceKind.YouTubeBrowser];

    private readonly Plugin plugin;
    private readonly WorldScreenRenderer renderer;
    private readonly ScreenStateIpc ipc;
    private string youtubeUrlDraft = string.Empty;
    private string youtubeUrlDraftSource = string.Empty;

    public MainWindow(Plugin plugin, WorldScreenRenderer renderer, ScreenStateIpc ipc)
        : base("CrystalCast###CrystalCastMain")
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

        DrawHeader();
        changed |= DrawPlaybackShell(config);
        DrawSectionTitle("Source");
        changed |= DrawSource(config);
        DrawSectionTitle("Placement");
        changed |= DrawPlacement(config);

        if (changed)
            SaveAndPublish();
    }

    private static void DrawHeader()
    {
        ImGui.TextUnformatted("CrystalCast");
        ImGui.SameLine();
        ImGui.TextDisabled("World screen controls");
        ImGui.Separator();
    }

    private bool DrawPlaybackShell(Configuration config)
    {
        var changed = false;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        ImGui.SameLine();
        var source = SourceNames[FindSourceIndex(config.SourceKind)];
        ImGui.TextDisabled($"Source: {source}");

        if (config.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var telemetry = renderer.PlaybackTelemetry;
            var position = telemetry == null
                ? "0:00"
                : FormatPlaybackPosition(telemetry.PositionMs);
            var state = telemetry?.State.ToString() ?? (config.PlaybackPaused ? "Paused" : "Playing");
            ImGui.TextUnformatted($"{state} @ {position}");
        }
        else
        {
            var paused = config.PlaybackPaused;
            if (ImGui.Checkbox("Paused", ref paused))
            {
                config.PlaybackPaused = paused;
                changed = true;
            }
        }

        ImGui.TextDisabled(ShortStatus(renderer.SourceStatus));
        return changed;
    }

    private static void DrawSectionTitle(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(label);
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

        ImGui.TextDisabled(renderer.TextureWidth > 0 && renderer.TextureHeight > 0
            ? $"Auto height: {config.WidthMeters * renderer.TextureHeight / renderer.TextureWidth:0.###} m"
            : "Auto height: waiting for texture");

        return changed;
    }

    private bool DrawSource(Configuration config)
    {
        var changed = false;
        var current = FindSourceIndex(config.SourceKind);

        if (ImGui.BeginCombo("Source", SourceNames[current]))
        {
            for (var i = 0; i < SourceNames.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(SourceNames[i], selected))
                {
                    config.SourceKind = SourceKinds[i];
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoSource(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= DrawYouTubeSource(config);
                break;
        }

        changed |= DrawSpatialAudio(config);
        return changed;
    }

    private static int FindSourceIndex(ScreenSourceKind sourceKind)
    {
        for (var i = 0; i < SourceKinds.Length; i++)
        {
            if (SourceKinds[i] == sourceKind)
                return i;
        }

        return 0;
    }

    private bool DrawYouTubeSource(Configuration config)
    {
        var changed = false;
        var fps = config.YouTubeCaptureFps;
        var autoplay = config.YouTubeAutoplay;
        var loop = config.LoopYouTube;
        var audioEnabled = config.YouTubeAudioEnabled;
        var volume = config.YouTubeVolume;
        var rate = config.YouTubePlaybackRate;

        if (!string.Equals(youtubeUrlDraftSource, config.YouTubeUrl, StringComparison.Ordinal))
        {
            youtubeUrlDraft = config.YouTubeUrl;
            youtubeUrlDraftSource = config.YouTubeUrl;
        }

        var committedVideoIdValid = YouTubeVideoId.TryParse(config.YouTubeUrl, out var committedVideoId);
        var draft = youtubeUrlDraft;
        var pressedEnter = ImGui.InputText("YouTube URL / ID", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        youtubeUrlDraft = draft;
        var draftVideoIdValid = YouTubeVideoId.TryParse(youtubeUrlDraft, out var draftVideoId);

        ImGui.SameLine();
        if (ImGui.Button("Load") || pressedEnter)
        {
            if (draftVideoIdValid)
            {
                config.YouTubeUrl = youtubeUrlDraft.Trim();
                youtubeUrlDraftSource = config.YouTubeUrl;
                config.PlaybackPaused = false;
                changed = true;
            }
        }

        if (draftVideoIdValid)
            ImGui.TextDisabled($"Video ID: {draftVideoId}");
        else if (!string.IsNullOrWhiteSpace(youtubeUrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Video ID: invalid");
        else if (committedVideoIdValid)
            ImGui.TextDisabled($"Current video ID: {committedVideoId}");
        else
            ImGui.TextDisabled("Video ID: empty");

        changed |= DrawYouTubePlaybackControls(config);

        changed |= DrawYouTubeResolutionPreset(config);

        if (ImGui.InputFloat("Capture FPS", ref fps, 1.0f, 5.0f))
        {
            config.YouTubeCaptureFps = Math.Clamp(fps, 1.0f, 60.0f);
            changed = true;
        }

        if (ImGui.Checkbox("Autoplay on load", ref autoplay))
        {
            config.YouTubeAutoplay = autoplay;
            changed = true;
        }

        if (ImGui.Checkbox("Loop YouTube video", ref loop))
        {
            config.LoopYouTube = loop;
            changed = true;
        }

        if (ImGui.Checkbox("Browser audio", ref audioEnabled))
        {
            config.YouTubeAudioEnabled = audioEnabled;
            changed = true;
        }

        if (config.YouTubeAudioEnabled && ImGui.SliderFloat("YouTube volume", ref volume, 0.0f, 1.0f))
        {
            config.YouTubeVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        if (ImGui.SliderFloat("Playback rate", ref rate, 0.25f, 2.0f))
        {
            config.YouTubePlaybackRate = Math.Clamp(rate, 0.25f, 2.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawYouTubeResolutionPreset(Configuration config)
    {
        var current = FindYouTubeResolutionPreset(config.YouTubeBrowserWidth, config.YouTubeBrowserHeight);
        var currentLabel = current >= 0
            ? YouTubeResolutionPresets[current].Name
            : $"Custom ({config.YouTubeBrowserWidth} x {config.YouTubeBrowserHeight})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < YouTubeResolutionPresets.Length; i++)
        {
            var preset = YouTubeResolutionPresets[i];
            var selected = i == current;
            if (ImGui.Selectable(preset.Name, selected))
            {
                config.YouTubeBrowserWidth = preset.Width;
                config.YouTubeBrowserHeight = preset.Height;
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private static int FindYouTubeResolutionPreset(int width, int height)
    {
        for (var i = 0; i < YouTubeResolutionPresets.Length; i++)
        {
            var preset = YouTubeResolutionPresets[i];
            if (preset.Width == width && preset.Height == height)
                return i;
        }

        return -1;
    }

    private bool DrawYouTubePlaybackControls(Configuration config)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var state = telemetry?.State.ToString() ?? (config.PlaybackPaused ? "Paused" : "Playing");

        ImGui.TextDisabled($"Playback: {state} @ {position}");

        if (ImGui.Button("Play"))
        {
            config.PlaybackPaused = false;
            renderer.TryPlayDynamicSource();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Pause"))
        {
            config.PlaybackPaused = true;
            renderer.TryPauseDynamicSource();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Restart"))
        {
            config.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }

        if (ImGui.Button("-10s"))
        {
            renderer.TrySeekDynamicSourceBy(-10.0);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+10s"))
        {
            renderer.TrySeekDynamicSourceBy(10.0);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("-60s"))
        {
            renderer.TrySeekDynamicSourceBy(-60.0);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+60s"))
        {
            renderer.TrySeekDynamicSourceBy(60.0);
            changed = true;
        }

        return changed;
    }

    private static string FormatPlaybackPosition(long positionMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return time.TotalHours >= 1.0
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static bool DrawLocalVideoSource(Configuration config)
    {
        var changed = false;
        var ffmpegPath = config.FfmpegPath;
        var videoPath = config.LocalVideoPath;
        var scalePercent = config.LocalVideoScalePercent;
        var fps = config.LocalVideoFps;
        var loop = config.LoopLocalVideo;
        var audioEnabled = config.AudioEnabled;
        var audioVolume = config.AudioVolume;

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

        if (ImGui.Checkbox("Audio", ref audioEnabled))
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

    private bool DrawSpatialAudio(Configuration config)
    {
        var changed = false;
        var enabled = config.SpatialAudioEnabled;
        var fullRadius = config.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = config.SpatialAudioSilentRadiusMeters;

        DrawSectionTitle("Audio falloff");
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

    private static string ShortStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return string.Empty;

        const int maxLength = 96;
        return status.Length <= maxLength
            ? status
            : $"{status[..maxLength]}...";
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

    private void SaveAndPublish()
    {
        ipc.PublishLocalState();
    }
}
