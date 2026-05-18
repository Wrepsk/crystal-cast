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
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly Dictionary<string, YouTubeUiState> youtubeUiStates = new(StringComparer.Ordinal);
    private string renamingScreenId = string.Empty;
    private string renameDraft = string.Empty;

    public MainWindow(Plugin plugin, WorldScreenManager renderer, ScreenStateIpc ipc)
        : base("CrystalCast###CrystalCastMain")
    {
        this.plugin = plugin;
        this.renderer = renderer;
        this.ipc = ipc;

        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var config = plugin.Configuration;
        config.Normalize();
        var activeBrowserScreen = config.GetActiveBrowserScreen();

        DrawHeader();
        changed |= DrawTopControls(config, activeBrowserScreen);
        activeBrowserScreen = config.GetActiveBrowserScreen();
        changed |= DrawPlaybackShell(config, activeBrowserScreen);
        DrawSectionTitle("Source");
        changed |= DrawSource(config, activeBrowserScreen);
        DrawSectionTitle("Placement");
        changed |= config.SourceKind == ScreenSourceKind.YouTubeBrowser
            ? DrawPlacement(activeBrowserScreen.Placement)
            : DrawPlacement(config);

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

    private bool DrawTopControls(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = DrawSourceCombo(config);
        if (config.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            ImGui.Spacing();
            changed |= DrawBrowserScreenControls(config, activeScreen);
        }

        return changed;
    }

    private static bool DrawSourceCombo(Configuration config)
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

        return changed;
    }

    private bool DrawBrowserScreenControls(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var activeIndex = Math.Max(0, config.BrowserScreens.FindIndex(screen => screen.ScreenId == activeScreen.ScreenId));

        if (ImGui.BeginCombo("Screen", activeScreen.Name))
        {
            for (var i = 0; i < config.BrowserScreens.Count; i++)
            {
                var screen = config.BrowserScreens[i];
                var selected = i == activeIndex;
                if (ImGui.Selectable($"{screen.Name}##Screen{screen.ScreenId}", selected))
                {
                    config.ActiveBrowserScreenId = screen.ScreenId;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Add YouTube"))
        {
            if (config.BrowserScreens.Count < Configuration.MaxBrowserScreens)
            {
                var screen = config.CreateDefaultBrowserScreen(GetNextScreenName(config));
                config.BrowserScreens.Add(screen);
                config.ActiveBrowserScreenId = screen.ScreenId;
                renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            if (config.BrowserScreens.Count < Configuration.MaxBrowserScreens)
            {
                var copy = activeScreen.CloneAsNew(GetDuplicateScreenName(config, activeScreen.Name));
                OffsetDuplicatePlacement(copy.Placement);
                config.BrowserScreens.Add(copy);
                config.ActiveBrowserScreenId = copy.ScreenId;
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            renamingScreenId = activeScreen.ScreenId;
            renameDraft = activeScreen.Name;
        }

        ImGui.SameLine();
        var canDelete = config.BrowserScreens.Count > 1;
        if (!canDelete)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete") && canDelete)
        {
            var removedId = activeScreen.ScreenId;
            config.BrowserScreens.RemoveAll(screen => screen.ScreenId == removedId);
            youtubeUiStates.Remove(removedId);
            if (renamingScreenId == removedId)
                renamingScreenId = string.Empty;
            config.ActiveBrowserScreenId = config.BrowserScreens[Math.Clamp(activeIndex - 1, 0, config.BrowserScreens.Count - 1)].ScreenId;
            changed = true;
        }
        if (!canDelete)
            ImGui.EndDisabled();

        if (config.BrowserScreens.Count >= Configuration.MaxBrowserScreens)
            ImGui.TextDisabled($"Screen limit: {Configuration.MaxBrowserScreens}");

        if (renamingScreenId == activeScreen.ScreenId)
        {
            var draft = renameDraft;
            var pressedEnter = ImGui.InputText("Screen name", ref draft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            renameDraft = draft;
            ImGui.SameLine();
            if (ImGui.Button("Save name") || pressedEnter)
            {
                var trimmed = renameDraft.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    activeScreen.Name = trimmed;
                    changed = true;
                }

                renamingScreenId = string.Empty;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                renamingScreenId = string.Empty;
        }

        var enabled = activeScreen.Enabled;
        if (ImGui.Checkbox("Screen enabled", ref enabled))
        {
            activeScreen.Enabled = enabled;
            changed = true;
        }

        return changed;
    }

    private bool DrawPlaybackShell(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
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
            var state = telemetry?.State.ToString() ?? (activeScreen.PlaybackPaused ? "Paused" : "Playing");
            var duration = telemetry is { DurationMs: > 0 }
                ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
                : string.Empty;
            ImGui.TextUnformatted($"{state} @ {position}{duration}");
            changed |= DrawYouTubeProgressBar(activeScreen, telemetry);
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

    private bool DrawYouTubeProgressBar(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        var uiState = GetYouTubeUiState(screen);
        var durationMs = telemetry?.DurationMs ?? 0;
        if (durationMs <= 0)
        {
            uiState.ProgressDraftSeconds = -1.0f;
            uiState.ProgressScrubbing = false;
            ImGui.ProgressBar(0.0f, new Vector2(-1.0f, 0.0f), "0:00");
            return false;
        }

        var changed = false;
        var durationSeconds = Math.Max(0.001f, durationMs / 1000.0f);
        var positionSeconds = Math.Clamp((telemetry?.PositionMs ?? 0) / 1000.0f, 0.0f, durationSeconds);
        if (uiState.ProgressDraftSeconds < 0.0f)
            uiState.ProgressDraftSeconds = positionSeconds;

        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1.0f, ImGui.GetContentRegionAvail().X);
        var height = Math.Max(16.0f, ImGui.GetFrameHeight());
        var size = new Vector2(width, height);
        ImGui.InvisibleButton($"##YouTubeProgress{screen.ScreenId}", size);

        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (active)
        {
            var mouseX = ImGui.GetIO().MousePos.X;
            uiState.ProgressDraftSeconds = Math.Clamp((mouseX - start.X) / width * durationSeconds, 0.0f, durationSeconds);
            uiState.ProgressScrubbing = true;
        }
        else if (uiState.ProgressScrubbing)
        {
            var seekDeltaSeconds = uiState.ProgressDraftSeconds - positionSeconds;
            if (Math.Abs(seekDeltaSeconds) >= 0.25f)
            {
                renderer.TrySeekDynamicSourceBy(seekDeltaSeconds);
                changed = true;
            }

            uiState.ProgressDraftSeconds = -1.0f;
            uiState.ProgressScrubbing = false;
        }

        var displaySeconds = uiState.ProgressScrubbing
            ? Math.Clamp(uiState.ProgressDraftSeconds, 0.0f, durationSeconds)
            : positionSeconds;
        var progressFraction = Math.Clamp(displaySeconds / durationSeconds, 0.0f, 1.0f);
        var lineHeight = active || hovered ? 5.0f : 3.0f;
        var lineY = start.Y + (height * 0.5f);
        var fillX = start.X + (width * progressFraction);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            new Vector2(start.X, lineY - (lineHeight * 0.5f)),
            new Vector2(start.X + width, lineY + (lineHeight * 0.5f)),
            ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 1.0f)),
            lineHeight * 0.5f);
        drawList.AddRectFilled(
            new Vector2(start.X, lineY - (lineHeight * 0.5f)),
            new Vector2(fillX, lineY + (lineHeight * 0.5f)),
            ImGui.GetColorU32(new Vector4(1.0f, 0.0f, 0.0f, 1.0f)),
            lineHeight * 0.5f);
        drawList.AddCircleFilled(
            new Vector2(fillX, lineY),
            active || hovered ? 6.0f : 4.0f,
            ImGui.GetColorU32(new Vector4(1.0f, 0.0f, 0.0f, 1.0f)));
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
            changed |= renderer.PlaceInFrontOfPlayer();

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

        changed |= DrawPlacementSizeAndCurve(config);
        return changed;
    }

    private bool DrawPlacement(ScreenPlacementSettings placement)
    {
        var changed = false;

        if (ImGui.Button("Place in front of player"))
            changed |= renderer.PlaceInFrontOfPlayer();

        var position = new Vector3(placement.PositionX, placement.PositionY, placement.PositionZ);
        if (ImGui.InputFloat3("Position", ref position))
        {
            placement.PositionX = position.X;
            placement.PositionY = position.Y;
            placement.PositionZ = position.Z;
            changed = true;
        }

        var rotation = new Vector3(placement.YawRadians, placement.PitchRadians, placement.RollRadians);
        if (ImGui.InputFloat3("Yaw / Pitch / Roll", ref rotation))
        {
            placement.YawRadians = rotation.X;
            placement.PitchRadians = rotation.Y;
            placement.RollRadians = rotation.Z;
            changed = true;
        }

        changed |= DrawPlacementSizeAndCurve(placement);
        return changed;
    }

    private bool DrawPlacementSizeAndCurve(Configuration config)
    {
        var changed = false;
        var width = config.WidthMeters;
        if (ImGui.InputFloat("Width meters", ref width, 0.1f, 0.5f))
        {
            config.WidthMeters = Math.Max(0.1f, width);
            changed = true;
        }

        ImGui.TextDisabled(renderer.TextureWidth > 0 && renderer.TextureHeight > 0
            ? $"Auto height: {config.WidthMeters * renderer.TextureHeight / renderer.TextureWidth:0.###} m"
            : "Auto height: waiting for texture");

        var maxCurveAmount = Math.Max(0.001f, config.WidthMeters / MathF.PI);
        var curveAmount = Math.Clamp(config.ScreenCurveAmountMeters, 0.0f, maxCurveAmount);
        if (Math.Abs(config.ScreenCurveAmountMeters - curveAmount) > 0.0001f)
        {
            config.ScreenCurveAmountMeters = curveAmount;
            changed = true;
        }

        if (ImGui.SliderFloat("Curve amount", ref curveAmount, 0.0f, maxCurveAmount))
        {
            config.ScreenCurveAmountMeters = Math.Clamp(curveAmount, 0.0f, maxCurveAmount);
            changed = true;
        }

        return changed;
    }

    private bool DrawPlacementSizeAndCurve(ScreenPlacementSettings placement)
    {
        var changed = false;
        var width = placement.WidthMeters;
        if (ImGui.InputFloat("Width meters", ref width, 0.1f, 0.5f))
        {
            placement.WidthMeters = Math.Max(0.1f, width);
            changed = true;
        }

        ImGui.TextDisabled(renderer.TextureWidth > 0 && renderer.TextureHeight > 0
            ? $"Auto height: {placement.WidthMeters * renderer.TextureHeight / renderer.TextureWidth:0.###} m"
            : "Auto height: waiting for texture");

        var maxCurveAmount = Math.Max(0.001f, placement.WidthMeters / MathF.PI);
        var curveAmount = Math.Clamp(placement.ScreenCurveAmountMeters, 0.0f, maxCurveAmount);
        if (Math.Abs(placement.ScreenCurveAmountMeters - curveAmount) > 0.0001f)
        {
            placement.ScreenCurveAmountMeters = curveAmount;
            changed = true;
        }

        if (ImGui.SliderFloat("Curve amount", ref curveAmount, 0.0f, maxCurveAmount))
        {
            placement.ScreenCurveAmountMeters = Math.Clamp(curveAmount, 0.0f, maxCurveAmount);
            changed = true;
        }

        return changed;
    }

    private bool DrawSource(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoSource(config);
                changed |= DrawSpatialAudio(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= DrawYouTubeSource(activeScreen);
                changed |= DrawSpatialAudio(activeScreen);
                break;
        }

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

    private bool DrawYouTubeSource(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetYouTubeUiState(screen);
        var fps = screen.YouTubeCaptureFps;
        var autoplay = screen.YouTubeAutoplay;
        var loop = screen.LoopYouTube;
        var audioEnabled = screen.YouTubeAudioEnabled;
        var volume = screen.YouTubeVolume;
        var rate = screen.YouTubePlaybackRate;

        if (!string.Equals(uiState.UrlDraftSource, screen.YouTubeUrl, StringComparison.Ordinal))
        {
            uiState.UrlDraft = screen.YouTubeUrl;
            uiState.UrlDraftSource = screen.YouTubeUrl;
        }

        var committedVideoIdValid = YouTubeVideoId.TryParse(screen.YouTubeUrl, out var committedVideoId);
        var draft = uiState.UrlDraft;
        var pressedEnter = ImGui.InputText("YouTube URL / ID", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        var draftVideoIdValid = YouTubeVideoId.TryParse(uiState.UrlDraft, out var draftVideoId);

        ImGui.SameLine();
        if (ImGui.Button("Load") || pressedEnter)
        {
            if (draftVideoIdValid)
            {
                screen.YouTubeUrl = uiState.UrlDraft.Trim();
                uiState.UrlDraftSource = screen.YouTubeUrl;
                screen.PlaybackPaused = false;
                changed = true;
            }
        }

        if (draftVideoIdValid)
            ImGui.TextDisabled($"Video ID: {draftVideoId}");
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Video ID: invalid");
        else if (committedVideoIdValid)
            ImGui.TextDisabled($"Current video ID: {committedVideoId}");
        else
            ImGui.TextDisabled("Video ID: empty");

        changed |= DrawYouTubePlaybackControls(screen);
        changed |= DrawYouTubeResolutionPreset(screen);

        if (ImGui.InputFloat("Capture FPS", ref fps, 1.0f, 5.0f))
        {
            screen.YouTubeCaptureFps = Math.Clamp(fps, 1.0f, 60.0f);
            changed = true;
        }

        if (ImGui.Checkbox("Autoplay on load", ref autoplay))
        {
            screen.YouTubeAutoplay = autoplay;
            changed = true;
        }

        if (ImGui.Checkbox("Loop YouTube video", ref loop))
        {
            screen.LoopYouTube = loop;
            changed = true;
        }

        if (ImGui.Checkbox("Browser audio", ref audioEnabled))
        {
            screen.YouTubeAudioEnabled = audioEnabled;
            changed = true;
        }

        if (screen.YouTubeAudioEnabled && ImGui.SliderFloat("YouTube volume", ref volume, 0.0f, 1.0f))
        {
            screen.YouTubeVolume = Math.Clamp(volume, 0.0f, 1.0f);
            changed = true;
        }

        if (ImGui.SliderFloat("Playback rate", ref rate, 0.25f, 2.0f))
        {
            screen.YouTubePlaybackRate = Math.Clamp(rate, 0.25f, 2.0f);
            changed = true;
        }

        return changed;
    }

    private static bool DrawYouTubeResolutionPreset(BrowserScreenProfile screen)
    {
        var current = FindYouTubeResolutionPreset(screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight);
        var currentLabel = current >= 0
            ? YouTubeResolutionPresets[current].Name
            : $"Custom ({screen.YouTubeBrowserWidth} x {screen.YouTubeBrowserHeight})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < YouTubeResolutionPresets.Length; i++)
        {
            var preset = YouTubeResolutionPresets[i];
            var selected = i == current;
            if (ImGui.Selectable(preset.Name, selected))
            {
                screen.YouTubeBrowserWidth = preset.Width;
                screen.YouTubeBrowserHeight = preset.Height;
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

    private bool DrawYouTubePlaybackControls(BrowserScreenProfile screen)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var state = telemetry?.State.ToString() ?? (screen.PlaybackPaused ? "Paused" : "Playing");

        ImGui.TextDisabled($"Playback: {state} @ {position}");

        if (ImGui.Button("Play"))
        {
            screen.PlaybackPaused = false;
            renderer.TryPlayDynamicSource();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Pause"))
        {
            screen.PlaybackPaused = true;
            renderer.TryPauseDynamicSource();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Restart"))
        {
            screen.PlaybackPaused = false;
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

    private bool DrawSpatialAudio(BrowserScreenProfile screen)
    {
        var changed = false;
        var enabled = screen.SpatialAudioEnabled;
        var fullRadius = screen.SpatialAudioFullVolumeRadiusMeters;
        var silentRadius = screen.SpatialAudioSilentRadiusMeters;

        DrawSectionTitle("Audio falloff");
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

    private YouTubeUiState GetYouTubeUiState(BrowserScreenProfile screen)
    {
        if (youtubeUiStates.TryGetValue(screen.ScreenId, out var state))
            return state;

        state = new YouTubeUiState
        {
            UrlDraft = screen.YouTubeUrl,
            UrlDraftSource = screen.YouTubeUrl,
        };
        youtubeUiStates[screen.ScreenId] = state;
        return state;
    }

    private static string GetNextScreenName(Configuration config)
    {
        for (var i = 1; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"YouTube screen {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"YouTube screen {config.BrowserScreens.Count + 1}";
    }

    private static string GetDuplicateScreenName(Configuration config, string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "YouTube screen" : $"{sourceName} copy";
        if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var i = 2; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"{baseName} {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"{baseName} {config.BrowserScreens.Count + 1}";
    }

    private static void OffsetDuplicatePlacement(ScreenPlacementSettings placement)
    {
        var right = new Vector3(MathF.Cos(placement.YawRadians), 0.0f, -MathF.Sin(placement.YawRadians));
        placement.PositionX += right.X * 0.35f;
        placement.PositionZ += right.Z * 0.35f;
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

    private sealed class YouTubeUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public float ProgressDraftSeconds { get; set; } = -1.0f;
        public bool ProgressScrubbing { get; set; }
    }
}
