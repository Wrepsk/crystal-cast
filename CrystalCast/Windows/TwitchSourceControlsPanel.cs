using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class TwitchSourceControlsPanel(WorldScreenManager renderer) : IBrowserSourceControlsPanel
{
    private readonly Dictionary<string, TwitchUiState> uiStates = new(StringComparer.Ordinal);

    public BrowserSourceProviderKind ProviderKind => BrowserSourceProviderKind.Twitch;

    public bool Draw(BrowserScreenProfile screen)
    {
        var changed = false;
        var uiState = GetUiState(screen);
        var fps = screen.TwitchCaptureFps;
        var autoplay = screen.TwitchAutoplay;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);

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
            SourceControlUi.DrawLockedControlsMessage(screen, "Source controls");

        if (draftSourceValid)
            ImGui.TextDisabled(draftSource.DisplayName);
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), "Twitch source: invalid");
        else if (committedSourceValid)
            ImGui.TextDisabled($"Current {committedSource.DisplayName}");
        else
            ImGui.TextDisabled("Twitch source: empty");

        changed |= DrawPlaybackControls(screen, uiState);
        changed |= DrawResolutionPreset(screen);

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

    public void ClearScreen(string screenId)
    {
        uiStates.Remove(screenId);
    }

    private static bool DrawResolutionPreset(BrowserScreenProfile screen)
    {
        return SourceControlUi.DrawResolutionPreset(
            screen.TwitchBrowserWidth,
            screen.TwitchBrowserHeight,
            (width, height) =>
            {
                screen.TwitchBrowserWidth = width;
                screen.TwitchBrowserHeight = height;
            });
    }

    private bool DrawPlaybackControls(BrowserScreenProfile screen, TwitchUiState uiState)
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
        changed |= SourceControlUi.DrawProgressBar(
            $"Twitch{screen.ScreenId}",
            uiState.Progress,
            renderer,
            telemetry,
            progressWidth,
            !sourceLocked && telemetry is { DurationMs: > 0 });

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

    private TwitchUiState GetUiState(BrowserScreenProfile screen)
    {
        if (uiStates.TryGetValue(screen.ScreenId, out var state))
            return state;

        state = new TwitchUiState
        {
            UrlDraft = screen.TwitchUrl,
            UrlDraftSource = screen.TwitchUrl,
        };
        uiStates[screen.ScreenId] = state;
        return state;
    }

    private sealed class TwitchUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public SourceProgressUiState Progress { get; } = new();
    }
}
