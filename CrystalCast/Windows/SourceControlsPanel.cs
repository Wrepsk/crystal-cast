using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class SourceControlsPanel(WorldScreenManager renderer)
{
    private static readonly (string Name, int Width, int Height)[] ResolutionPresets =
    [
        ("360p (640 x 360)", 640, 360),
        ("480p (854 x 480)", 854, 480),
        ("720p (1280 x 720)", 1280, 720),
        ("1080p (1920 x 1080)", 1920, 1080),
        ("1440p (2560 x 1440)", 2560, 1440),
        ("4K (3840 x 2160)", 3840, 2160),
    ];

    private readonly Dictionary<string, YouTubeUiState> youtubeUiStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TwitchUiState> twitchUiStates = new(StringComparer.Ordinal);

    public bool Draw(Configuration config, BrowserScreenProfile activeScreen)
    {
        var changed = false;
        switch (config.SourceKind)
        {
            case ScreenSourceKind.LocalVideo:
                changed |= DrawLocalVideoSource(config);
                break;
            case ScreenSourceKind.YouTubeBrowser:
                changed |= activeScreen.ProviderKind == BrowserSourceProviderKind.Twitch
                    ? DrawTwitchSource(activeScreen)
                    : DrawYouTubeSource(activeScreen);
                break;
        }

        return changed;
    }

    public void ClearScreen(string screenId)
    {
        youtubeUiStates.Remove(screenId);
        twitchUiStates.Remove(screenId);
    }

    private bool DrawYouTubeProgressBar(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry, float width = -1.0f, bool interactive = true)
    {
        var uiState = GetYouTubeUiState(screen);
        var progressWidth = width > 0.0f
            ? width
            : ImGui.GetContentRegionAvail().X;
        var durationMs = telemetry?.DurationMs ?? 0;
        if (durationMs <= 0)
        {
            uiState.ProgressDraftSeconds = -1.0f;
            uiState.ProgressScrubbing = false;
            ImGui.ProgressBar(0.0f, new Vector2(progressWidth, 0.0f), "0:00");
            return false;
        }

        var changed = false;
        var durationSeconds = Math.Max(0.001f, durationMs / 1000.0f);
        var positionSeconds = Math.Clamp((telemetry?.PositionMs ?? 0) / 1000.0f, 0.0f, durationSeconds);
        if (uiState.ProgressDraftSeconds < 0.0f)
            uiState.ProgressDraftSeconds = positionSeconds;

        var start = ImGui.GetCursorScreenPos();
        width = Math.Max(1.0f, progressWidth);
        var height = Math.Max(16.0f, ImGui.GetFrameHeight());
        var size = new Vector2(width, height);
        ImGui.InvisibleButton($"##YouTubeProgress{screen.ScreenId}", size);

        var active = interactive && ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (!interactive)
            uiState.ProgressScrubbing = false;
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

    private bool DrawYouTubeSource(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetYouTubeUiState(screen);
        var fps = screen.YouTubeCaptureFps;
        var autoplay = screen.YouTubeAutoplay;
        var loop = screen.LoopYouTube;
        var playlistAutoplayNext = screen.YouTubePlaylistAutoplayNext;
        var rate = screen.YouTubePlaybackRate;
        var sourceLocked = IsSourceControlsLocked(screen);

        if (!string.Equals(uiState.UrlDraftSource, screen.YouTubeUrl, StringComparison.Ordinal))
        {
            uiState.UrlDraft = screen.YouTubeUrl;
            uiState.UrlDraftSource = screen.YouTubeUrl;
        }

        var committedSourceValid = YouTubeVideoId.TryParseSource(screen.YouTubeUrl, out var committedSource);
        var draft = uiState.UrlDraft;
        if (sourceLocked)
            ImGui.BeginDisabled();
        var pressedEnter = ImGui.InputText("YouTube URL / ID", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        var draftSourceValid = YouTubeVideoId.TryParseSource(uiState.UrlDraft, out var draftSource);

        ImGui.SameLine();
        if (ImGui.Button("Load") || pressedEnter)
        {
            if (draftSourceValid)
            {
                screen.YouTubeUrl = uiState.UrlDraft.Trim();
                uiState.UrlDraftSource = screen.YouTubeUrl;
                screen.PlaybackPaused = false;
                changed = true;
            }
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        if (sourceLocked)
            DrawLockedControlsMessage(screen, "Source controls");

        if (draftSourceValid)
            ImGui.TextDisabled(draftSource.DisplayName);
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "YouTube source: invalid");
        else if (committedSourceValid)
            ImGui.TextDisabled($"Current {committedSource.DisplayName}");
        else
            ImGui.TextDisabled("YouTube source: empty");

        changed |= DrawYouTubePlaybackControls(screen);
        changed |= DrawYouTubeResolutionPreset(screen);

        var manualFps = screen.YouTubeCaptureFpsManual;
        if (ImGui.Checkbox("Set capture FPS manually", ref manualFps))
        {
            screen.YouTubeCaptureFpsManual = manualFps;
            changed = true;
        }

        if (screen.YouTubeCaptureFpsManual)
        {
            if (ImGui.InputFloat("Capture FPS", ref fps, 1.0f, 5.0f))
            {
                screen.YouTubeCaptureFps = Math.Clamp(fps, 1.0f, 120.0f);
                changed = true;
            }
        }
        else
        {
            var detectedFps = renderer.GetDetectedVideoFps(screen);
            var autoFps = detectedFps > 0.0f ? detectedFps : 60.0f;
            ImGui.TextDisabled($"Capture FPS: {autoFps:0.#} ({(detectedFps > 0.0f ? "auto-detected" : "default")})");
        }

        if (sourceLocked)
            ImGui.BeginDisabled();
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

        if (committedSourceValid && committedSource.Kind == YouTubeSourceKind.Playlist
            && ImGui.Checkbox("Autoplay next playlist video", ref playlistAutoplayNext))
        {
            screen.YouTubePlaylistAutoplayNext = playlistAutoplayNext;
            changed = true;
        }

        if (ImGui.SliderFloat("Playback rate", ref rate, 0.25f, 2.0f))
        {
            screen.YouTubePlaybackRate = Math.Clamp(rate, 0.25f, 2.0f);
            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        return changed;
    }

    private static bool DrawYouTubeResolutionPreset(BrowserScreenProfile screen)
    {
        var current = FindResolutionPreset(screen.YouTubeBrowserWidth, screen.YouTubeBrowserHeight);
        var currentLabel = current >= 0
            ? ResolutionPresets[current].Name
            : $"Custom ({screen.YouTubeBrowserWidth} x {screen.YouTubeBrowserHeight})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < ResolutionPresets.Length; i++)
        {
            var preset = ResolutionPresets[i];
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

    private static int FindResolutionPreset(int width, int height)
    {
        for (var i = 0; i < ResolutionPresets.Length; i++)
        {
            var preset = ResolutionPresets[i];
            if (preset.Width == width && preset.Height == height)
                return i;
        }

        return -1;
    }

    private bool DrawTwitchSource(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetTwitchUiState(screen);
        var fps = screen.TwitchCaptureFps;
        var autoplay = screen.TwitchAutoplay;
        var sourceLocked = IsSourceControlsLocked(screen);

        if (!string.Equals(uiState.UrlDraftSource, screen.TwitchUrl, StringComparison.Ordinal))
        {
            uiState.UrlDraft = screen.TwitchUrl;
            uiState.UrlDraftSource = screen.TwitchUrl;
        }

        var committedSourceValid = TwitchVideoId.TryParseSource(screen.TwitchUrl, out var committedSource);
        var draft = uiState.UrlDraft;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var loadButtonWidth = ImGui.CalcTextSize("Load").X + (ImGui.GetStyle().FramePadding.X * 2.0f);
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var keepLoadInline = rowWidth >= loadButtonWidth + spacing + 120.0f;
        ImGui.TextUnformatted("Twitch channel / VOD URL");
        ImGui.SetNextItemWidth(keepLoadInline
            ? Math.Max(120.0f, rowWidth - loadButtonWidth - spacing)
            : Math.Max(120.0f, rowWidth));
        if (sourceLocked)
            ImGui.BeginDisabled();
        var pressedEnter = ImGui.InputText("##TwitchUrl", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        var draftSourceValid = TwitchVideoId.TryParseSource(uiState.UrlDraft, out var draftSource);

        if (keepLoadInline)
            ImGui.SameLine();
        if (ImGui.Button("Load", new Vector2(Math.Min(loadButtonWidth, ImGui.GetContentRegionAvail().X), 0.0f)) || pressedEnter)
        {
            if (draftSourceValid)
            {
                screen.TwitchUrl = uiState.UrlDraft.Trim();
                uiState.UrlDraftSource = screen.TwitchUrl;
                screen.PlaybackPaused = false;
                changed = true;
            }
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        if (sourceLocked)
            DrawLockedControlsMessage(screen, "Source controls");

        if (draftSourceValid)
            ImGui.TextDisabled(draftSource.DisplayName);
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Twitch source: invalid");
        else if (committedSourceValid)
            ImGui.TextDisabled($"Current {committedSource.DisplayName}");
        else
            ImGui.TextDisabled("Twitch source: empty");

        changed |= DrawTwitchPlaybackControls(screen);
        changed |= DrawTwitchResolutionPreset(screen);

        var manualFps = screen.TwitchCaptureFpsManual;
        if (ImGui.Checkbox("Set capture FPS manually", ref manualFps))
        {
            screen.TwitchCaptureFpsManual = manualFps;
            changed = true;
        }

        if (screen.TwitchCaptureFpsManual)
        {
            if (ImGui.InputFloat("Capture FPS", ref fps, 1.0f, 5.0f))
            {
                screen.TwitchCaptureFps = Math.Clamp(fps, 1.0f, 120.0f);
                changed = true;
            }
        }
        else
        {
            var detectedFps = renderer.GetDetectedVideoFps(screen);
            var autoFps = detectedFps > 0.0f ? detectedFps : 60.0f;
            ImGui.TextDisabled($"Capture FPS: {autoFps:0.#} ({(detectedFps > 0.0f ? "auto-detected" : "default")})");
        }

        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Autoplay on load", ref autoplay))
        {
            screen.TwitchAutoplay = autoplay;
            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        return changed;
    }

    private static bool DrawTwitchResolutionPreset(BrowserScreenProfile screen)
    {
        var current = FindResolutionPreset(screen.TwitchBrowserWidth, screen.TwitchBrowserHeight);
        var currentLabel = current >= 0
            ? ResolutionPresets[current].Name
            : $"Custom ({screen.TwitchBrowserWidth} x {screen.TwitchBrowserHeight})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < ResolutionPresets.Length; i++)
        {
            var preset = ResolutionPresets[i];
            var selected = i == current;
            if (ImGui.Selectable(preset.Name, selected))
            {
                screen.TwitchBrowserWidth = preset.Width;
                screen.TwitchBrowserHeight = preset.Height;
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    private bool DrawTwitchPlaybackControls(BrowserScreenProfile screen)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        var state = GetPlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;
        var sourceLocked = IsSourceControlsLocked(screen);

        ImGui.TextDisabled($"Playback: {state} @ {position}{duration}");

        var buttonSize = ImGui.GetFrameHeight();
        var toggleLabel = isPlaying
            ? "Pause##TwitchPlayPause"
            : "Play##TwitchPlayPause";
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button(toggleLabel, new Vector2(Math.Max(buttonSize * 2.5f, 52.0f), buttonSize)))
        {
            if (isPlaying)
            {
                screen.PlaybackPaused = true;
                renderer.TryPauseDynamicSource();
            }
            else
            {
                screen.PlaybackPaused = false;
                renderer.TryPlayDynamicSource();
            }

            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : isPlaying ? "Pause" : "Play");

        ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var restartWidth = Math.Max(buttonSize * 3.0f, 68.0f);
        var availableAfterToggle = ImGui.GetContentRegionAvail().X;
        var keepRestartInline = availableAfterToggle >= restartWidth + spacing + 48.0f;
        var progressWidth = keepRestartInline
            ? Math.Max(48.0f, availableAfterToggle - restartWidth - spacing)
            : Math.Max(24.0f, availableAfterToggle);
        changed |= DrawYouTubeProgressBar(screen, telemetry, progressWidth, !sourceLocked && telemetry is { DurationMs: > 0 });

        if (keepRestartInline)
            ImGui.SameLine();
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button("Restart##TwitchRestart", new Vector2(Math.Min(restartWidth, ImGui.GetContentRegionAvail().X), buttonSize)))
        {
            screen.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : "Restart");

        return changed;
    }

    private bool DrawYouTubePlaybackControls(BrowserScreenProfile screen)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : FormatPlaybackPosition(telemetry.PositionMs);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        var state = GetPlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;
        var sourceLocked = IsSourceControlsLocked(screen);

        ImGui.TextDisabled($"Playback: {state} @ {position}{duration}");

        var buttonSize = ImGui.GetFrameHeight();
        var toggleLabel = isPlaying
            ? "##YouTubePlayPause"
            : "##YouTubePlayPause";
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button(toggleLabel, new Vector2(buttonSize, buttonSize)))
        {
            if (isPlaying)
            {
                screen.PlaybackPaused = true;
                renderer.TryPauseDynamicSource();
            }
            else
            {
                screen.PlaybackPaused = false;
                renderer.TryPlayDynamicSource();
            }

            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : isPlaying ? "Pause" : "Play");

        ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var progressWidth = Math.Max(48.0f, ImGui.GetContentRegionAvail().X - buttonSize - spacing);
        changed |= DrawYouTubeProgressBar(screen, telemetry, progressWidth, !sourceLocked);

        ImGui.SameLine();
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button("##YouTubeRestart", new Vector2(buttonSize, buttonSize)))
        {
            screen.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : "Restart");

        return changed;
    }

    private static bool IsSourceControlsLocked(BrowserScreenProfile screen)
    {
        return screen.SourceControlsLocked;
    }

    private static void DrawLockedControlsMessage(BrowserScreenProfile screen, string label)
    {
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId)
            ? $"{label} locked by IPC."
            : $"{label} locked by {screen.SourceControlsOwnerId}.");
    }

    private static ScreenPlaybackState GetPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        if (screen.PlaybackPaused)
            return ScreenPlaybackState.Paused;

        return telemetry?.State ?? ScreenPlaybackState.Stopped;
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

    private TwitchUiState GetTwitchUiState(BrowserScreenProfile screen)
    {
        if (twitchUiStates.TryGetValue(screen.ScreenId, out var state))
            return state;

        state = new TwitchUiState
        {
            UrlDraft = screen.TwitchUrl,
            UrlDraftSource = screen.TwitchUrl,
        };
        twitchUiStates[screen.ScreenId] = state;
        return state;
    }

    private sealed class YouTubeUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public float ProgressDraftSeconds { get; set; } = -1.0f;
        public bool ProgressScrubbing { get; set; }
    }

    private sealed class TwitchUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
    }
}
