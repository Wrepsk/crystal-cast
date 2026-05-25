using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class YouTubeSourceControlsPanel(WorldScreenManager renderer) : IBrowserSourceControlsPanel
{
    private readonly Dictionary<string, YouTubeUiState> uiStates = new(StringComparer.Ordinal);

    public BrowserSourceProviderKind ProviderKind => BrowserSourceProviderKind.YouTube;

    public bool Draw(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetUiState(screen);
        var fps = screen.YouTubeCaptureFps;
        var autoplay = screen.YouTubeAutoplay;
        var loop = screen.LoopYouTube;
        var playlistAutoplayNext = screen.YouTubePlaylistAutoplayNext;
        var rate = screen.YouTubePlaybackRate;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);

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
            SourceControlUi.DrawLockedControlsMessage(screen, "Source controls");

        if (draftSourceValid)
            ImGui.TextDisabled(draftSource.DisplayName);
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "YouTube source: invalid");
        else if (committedSourceValid)
            ImGui.TextDisabled($"Current {committedSource.DisplayName}");
        else
            ImGui.TextDisabled("YouTube source: empty");

        changed |= DrawPlaybackControls(screen, uiState);
        changed |= DrawResolutionPreset(screen);

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

    public void ClearScreen(string screenId)
    {
        uiStates.Remove(screenId);
    }

    private static bool DrawResolutionPreset(BrowserScreenProfile screen)
    {
        return SourceControlUi.DrawResolutionPreset(
            screen.YouTubeBrowserWidth,
            screen.YouTubeBrowserHeight,
            (width, height) =>
            {
                screen.YouTubeBrowserWidth = width;
                screen.YouTubeBrowserHeight = height;
            });
    }

    private bool DrawPlaybackControls(BrowserScreenProfile screen, YouTubeUiState uiState)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : SourceControlUi.FormatPlaybackPosition(telemetry.PositionMs);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {SourceControlUi.FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        var state = SourceControlUi.GetPlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);

        ImGui.TextDisabled($"Playback: {state} @ {position}{duration}");

        var buttonSize = ImGui.GetFrameHeight();
        var toggleLabel = isPlaying
            ? "îƒ‚##YouTubePlayPause"
            : "î‚»##YouTubePlayPause";
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
        changed |= SourceControlUi.DrawProgressBar(
            $"YouTube{screen.ScreenId}",
            uiState.Progress,
            renderer,
            telemetry,
            progressWidth,
            !sourceLocked);

        ImGui.SameLine();
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button("î€î##YouTubeRestart", new Vector2(buttonSize, buttonSize)))
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

    private YouTubeUiState GetUiState(BrowserScreenProfile screen)
    {
        if (uiStates.TryGetValue(screen.ScreenId, out var state))
            return state;

        state = new YouTubeUiState
        {
            UrlDraft = screen.YouTubeUrl,
            UrlDraftSource = screen.YouTubeUrl,
        };
        uiStates[screen.ScreenId] = state;
        return state;
    }

    private sealed class YouTubeUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public SourceProgressUiState Progress { get; } = new();
    }
}
