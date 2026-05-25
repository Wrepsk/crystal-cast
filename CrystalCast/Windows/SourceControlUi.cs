using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class SourceProgressUiState
{
    public float DraftSeconds { get; set; } = -1.0f;
    public bool Scrubbing { get; set; }
}

internal static class SourceControlUi
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

    public static bool DrawProgressBar(
        string id,
        SourceProgressUiState state,
        WorldScreenManager renderer,
        MediaPlaybackTelemetry? telemetry,
        float width = -1.0f,
        bool interactive = true)
    {
        var progressWidth = width > 0.0f
            ? width
            : ImGui.GetContentRegionAvail().X;
        var durationMs = telemetry?.DurationMs ?? 0;
        if (durationMs <= 0)
        {
            state.DraftSeconds = -1.0f;
            state.Scrubbing = false;
            ImGui.ProgressBar(0.0f, new Vector2(progressWidth, 0.0f), "0:00");
            return false;
        }

        var changed = false;
        var durationSeconds = Math.Max(0.001f, durationMs / 1000.0f);
        var positionSeconds = Math.Clamp((telemetry?.PositionMs ?? 0) / 1000.0f, 0.0f, durationSeconds);
        if (state.DraftSeconds < 0.0f)
            state.DraftSeconds = positionSeconds;

        var start = ImGui.GetCursorScreenPos();
        width = Math.Max(1.0f, progressWidth);
        var height = Math.Max(16.0f, ImGui.GetFrameHeight());
        var size = new Vector2(width, height);
        ImGui.InvisibleButton($"##SourceProgress{id}", size);

        var active = interactive && ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (!interactive)
            state.Scrubbing = false;
        if (active)
        {
            var mouseX = ImGui.GetIO().MousePos.X;
            state.DraftSeconds = Math.Clamp((mouseX - start.X) / width * durationSeconds, 0.0f, durationSeconds);
            state.Scrubbing = true;
        }
        else if (state.Scrubbing)
        {
            var seekDeltaSeconds = state.DraftSeconds - positionSeconds;
            if (Math.Abs(seekDeltaSeconds) >= 0.25f)
            {
                renderer.TrySeekDynamicSourceBy(seekDeltaSeconds);
                changed = true;
            }

            state.DraftSeconds = -1.0f;
            state.Scrubbing = false;
        }

        var displaySeconds = state.Scrubbing
            ? Math.Clamp(state.DraftSeconds, 0.0f, durationSeconds)
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

    public static bool DrawResolutionPreset(int width, int height, Action<int, int> apply)
    {
        var current = FindResolutionPreset(width, height);
        var currentLabel = current >= 0
            ? ResolutionPresets[current].Name
            : $"Custom ({width} x {height})";

        if (!ImGui.BeginCombo("Browser resolution", currentLabel))
            return false;

        var changed = false;
        for (var i = 0; i < ResolutionPresets.Length; i++)
        {
            var preset = ResolutionPresets[i];
            var selected = i == current;
            if (ImGui.Selectable(preset.Name, selected))
            {
                apply(preset.Width, preset.Height);
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
        return changed;
    }

    public static bool IsSourceControlsLocked(BrowserScreenProfile screen)
    {
        return screen.SourceControlsLocked;
    }

    public static void DrawLockedControlsMessage(BrowserScreenProfile screen, string label)
    {
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId)
            ? $"{label} locked by IPC."
            : $"{label} locked by {screen.SourceControlsOwnerId}.");
    }

    public static ScreenPlaybackState GetPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        if (screen.PlaybackPaused)
            return ScreenPlaybackState.Paused;

        return telemetry?.State ?? ScreenPlaybackState.Stopped;
    }

    public static string FormatPlaybackPosition(long positionMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return time.TotalHours >= 1.0
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
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
}
