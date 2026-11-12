using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class SourceProgressUiState
{
    public float DraftSeconds { get; set; } = -1.0f;
    public bool Scrubbing { get; set; }

    /// <summary>Where the last seek was sent, shown until playback reports it; negative when none.</summary>
    public float PendingSeekSeconds { get; set; } = -1.0f;
    public double PendingSeekUntil { get; set; }
}

internal static class SourceControlUi
{
    private const double PendingSeekTimeoutSeconds = 2.0;
    private const float PendingSeekToleranceSeconds = 1.0f;

    private static readonly (string Name, int Width, int Height)[] ResolutionPresets =
    [
        ("360p", 640, 360),
        ("480p", 854, 480),
        ("720p", 1280, 720),
        ("1080p", 1920, 1080),
        ("1440p", 2560, 1440),
        ("4K", 3840, 2160),
    ];

    /// <summary>
    /// Seek bar for the transport row: slider groove and cap with an accent fill. Dragging scrubs; the seek
    /// is sent when the cap is released. Returns true when a seek was requested.
    /// </summary>
    public static bool DrawSeekBar(
        string id,
        SourceProgressUiState state,
        WorldScreenManager renderer,
        MediaPlaybackTelemetry? telemetry,
        float width,
        bool interactive,
        out float displaySeconds)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(1.0f, width), Ui.FrameHeight);
        var durationMs = telemetry?.DurationMs ?? 0;
        var enabled = interactive && durationMs > 0;

        if (!enabled)
            ImGui.BeginDisabled();
        ImGui.InvisibleButton($"##SourceProgress{id}", size);
        if (!enabled)
            ImGui.EndDisabled();

        if (durationMs <= 0)
        {
            state.DraftSeconds = -1.0f;
            state.Scrubbing = false;
            state.PendingSeekSeconds = -1.0f;
            displaySeconds = Math.Max(0.0f, (telemetry?.PositionMs ?? 0) / 1000.0f);
            Ui.DrawSliderTrack(ImGui.GetWindowDrawList(), pos, size, 0.0f, DisabledFill, false, false, disabled: true);
            return false;
        }

        var changed = false;
        var durationSeconds = Math.Max(0.001f, durationMs / 1000.0f);
        var positionSeconds = Math.Clamp((telemetry?.PositionMs ?? 0) / 1000.0f, 0.0f, durationSeconds);
        if (state.DraftSeconds < 0.0f)
            state.DraftSeconds = positionSeconds;

        var active = enabled && ImGui.IsItemActive();
        var hovered = enabled && ImGui.IsItemHovered();
        if (!enabled)
            state.Scrubbing = false;
        if (active)
        {
            var capHalf = S(5.0f);
            var fraction = Math.Clamp((ImGui.GetIO().MousePos.X - pos.X - capHalf) / Math.Max(1.0f, size.X - (capHalf * 2.0f)), 0.0f, 1.0f);
            state.DraftSeconds = fraction * durationSeconds;
            state.Scrubbing = true;
        }
        else if (state.Scrubbing)
        {
            var seekDeltaSeconds = state.DraftSeconds - positionSeconds;
            if (Math.Abs(seekDeltaSeconds) >= 0.25f)
            {
                renderer.TrySeekDynamicSourceBy(seekDeltaSeconds);
                changed = true;

                // Playback still reports the old time for a moment; keep the cap where it was dropped.
                state.PendingSeekSeconds = state.DraftSeconds;
                state.PendingSeekUntil = ImGui.GetTime() + PendingSeekTimeoutSeconds;
            }

            state.DraftSeconds = -1.0f;
            state.Scrubbing = false;
        }

        if (state.PendingSeekSeconds >= 0.0f
            && (Math.Abs(positionSeconds - state.PendingSeekSeconds) <= PendingSeekToleranceSeconds
                || ImGui.GetTime() >= state.PendingSeekUntil))
        {
            state.PendingSeekSeconds = -1.0f;
        }

        displaySeconds = state.Scrubbing
            ? Math.Clamp(state.DraftSeconds, 0.0f, durationSeconds)
            : state.PendingSeekSeconds >= 0.0f
                ? Math.Clamp(state.PendingSeekSeconds, 0.0f, durationSeconds)
                : positionSeconds;
        Ui.DrawSliderTrack(
            ImGui.GetWindowDrawList(),
            pos,
            size,
            Math.Clamp(displaySeconds / durationSeconds, 0.0f, 1.0f),
            enabled ? Accent : DisabledFill,
            hovered || active,
            active,
            disabled: !enabled);
        return changed;
    }

    public static bool DrawResolutionPreset(string id, int width, int height, Action<int, int> apply, bool disabled = false)
    {
        var current = FindResolutionPreset(width, height);
        var changed = false;
        if (!Ui.BeginDropdown(id, FormatResolution(width, height), disabled: disabled))
            return false;

        for (var i = 0; i < ResolutionPresets.Length; i++)
        {
            var preset = ResolutionPresets[i];
            if (Ui.MenuItem($"{preset.Name} · {preset.Width} × {preset.Height}##Resolution{i}", i == current))
            {
                apply(preset.Width, preset.Height);
                changed = true;
            }
        }

        Ui.EndDropdown();
        return changed;
    }

    public static string FormatResolution(int width, int height)
    {
        var current = FindResolutionPreset(width, height);
        return current >= 0
            ? $"{ResolutionPresets[current].Name} · {width} × {height}"
            : $"Custom · {width} × {height}";
    }

    public static string ShortResolution(int width, int height)
    {
        var current = FindResolutionPreset(width, height);
        return current >= 0 ? ResolutionPresets[current].Name : $"{width} × {height}";
    }

    public static bool IsSourceControlsLocked(BrowserScreenProfile screen)
    {
        return screen.SourceControlsLocked;
    }

    public static string LockedOwner(BrowserScreenProfile screen)
    {
        return string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId) ? "an IPC integration" : screen.SourceControlsOwnerId;
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

    /// <summary>
    /// Finds a real problem in a source status. Browser sources report a "; "-separated mix of capture
    /// diagnostics ("capture 30/60 fps; cadence …; callback errors 0; last fatal none; …") and occasional
    /// failures ("WebView2 init failed: Object reference…"). Only a failing segment produces a banner; its text
    /// is split into a title and detail. Returns false while everything is healthy.
    /// </summary>
    public static bool TryDescribeSourceProblem(string status, out BannerKind kind, out string title, out string detail)
    {
        kind = BannerKind.Error;
        title = string.Empty;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        string? warning = null;
        foreach (var raw in status.Split(';'))
        {
            var segment = raw.Trim();
            switch (ClassifyStatusSegment(segment))
            {
                case BannerKind.Error:
                    SplitProblem(segment, out title, out detail);
                    kind = BannerKind.Error;
                    return true;
                case BannerKind.Warning:
                    warning ??= segment;
                    break;
            }
        }

        if (warning == null)
            return false;

        SplitProblem(warning, out title, out detail);
        kind = BannerKind.Warning;
        return true;
    }

    internal static BannerKind? ClassifyStatusSegment(string segment)
    {
        if (segment.Length == 0)
            return null;

        var lower = segment.ToLowerInvariant();

        // Counters and "none" markers are diagnostics, not failures ("callback errors 0", "last fatal none").
        if (lower.StartsWith("last fatal", StringComparison.Ordinal))
            return lower is "last fatal none" or "last fatal" ? null : BannerKind.Error;
        if (lower.Contains("errors 0", StringComparison.Ordinal) && !lower.Contains("failed", StringComparison.Ordinal))
            return null;

        var failed = lower.Contains("failed", StringComparison.Ordinal)
            || lower.Contains("failure", StringComparison.Ordinal)
            || lower.Contains("unavailable", StringComparison.Ordinal)
            || lower.Contains("could not", StringComparison.Ordinal)
            || lower.Contains("cannot", StringComparison.Ordinal)
            || lower.StartsWith("error", StringComparison.Ordinal)
            || lower.Contains(" error:", StringComparison.Ordinal)
            || lower.StartsWith("invalid ", StringComparison.Ordinal);
        if (!failed)
            return null;

        // The source recovered by switching capture paths: worth knowing, but it still plays.
        return lower.Contains("fallback", StringComparison.Ordinal) ? BannerKind.Warning : BannerKind.Error;
    }

    private static void SplitProblem(string segment, out string title, out string detail)
    {
        var colon = segment.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < segment.Length - 1)
        {
            title = Capitalize(segment[..colon].Trim());
            detail = segment[(colon + 1)..].Trim();
        }
        else
        {
            title = Capitalize(segment);
            detail = string.Empty;
        }
    }

    private static string Capitalize(string text)
    {
        return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
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
