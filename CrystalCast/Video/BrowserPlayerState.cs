using System.Text.Json;

namespace CrystalCast.Video;

internal enum BrowserPlayerMessageResult
{
    Ignored,
    Handled,
    Ready,
}

/// <summary>
/// Player-page status and playback telemetry shared by every provider-page browser backend.
/// Messages must already be authenticated with the page nonce.
/// </summary>
internal sealed class BrowserPlayerState
{
    private readonly BrowserSourceDescriptor descriptor;
    private readonly IBrowserSourceReference source;
    private readonly object telemetryLock = new();
    private MediaPlaybackTelemetry telemetry = new();
    private volatile string playerStatus = "player not ready";
    private float detectedVideoFps;

    public BrowserPlayerState(BrowserSourceDescriptor descriptor, IBrowserSourceReference source, float playbackRate)
    {
        this.descriptor = descriptor;
        this.source = source;
        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, 0, playbackRate, string.Empty);
    }

    public string PlayerStatus
    {
        get => playerStatus;
        set => playerStatus = value;
    }

    public float DetectedVideoFps => Volatile.Read(ref detectedVideoFps);

    public MediaPlaybackTelemetry Telemetry
    {
        get
        {
            lock (telemetryLock)
            {
                return telemetry;
            }
        }
    }

    public BrowserPlayerMessageResult HandleMessage(JsonElement root, string nonce, float configuredPlaybackRate)
    {
        if (!BrowserPageMessaging.HasNonce(root, nonce)
            || !root.TryGetProperty("type", out var typeProperty))
            return BrowserPlayerMessageResult.Ignored;

        switch (typeProperty.GetString())
        {
            case "ready":
                playerStatus = $"player ready: {source.DisplayName}";
                return BrowserPlayerMessageResult.Ready;
            case "status":
                UpdateFromStatusMessage(root, configuredPlaybackRate);
                return BrowserPlayerMessageResult.Handled;
            case "video-fps":
                UpdateDetectedVideoFps(root);
                return BrowserPlayerMessageResult.Handled;
            case "error":
                playerStatus = descriptor.DescribeError(root);
                return BrowserPlayerMessageResult.Handled;
            case "script-error":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "script error");
                return BrowserPlayerMessageResult.Handled;
            case "debug":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "browser debug");
                return BrowserPlayerMessageResult.Handled;
            default:
                return BrowserPlayerMessageResult.Ignored;
        }
    }

    /// <summary>Keeps the last known position, duration and title while changing state.</summary>
    public void SetState(ScreenPlaybackState state, float playbackRate)
    {
        var current = Telemetry;
        UpdateTelemetry(state, current.PositionMs, current.DurationMs, playbackRate, current.Title);
    }

    public void UpdateTelemetry(ScreenPlaybackState state, long positionMs, long durationMs, float rate, string title, string currentVideoId = "")
    {
        if (!descriptor.IsValidVideoId(currentVideoId))
            currentVideoId = source.VideoId;

        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                DurationMs = durationMs,
                Rate = ClampPlaybackRate(rate),
                Title = title,
                VideoId = currentVideoId,
                CanonicalUrl = descriptor.BuildCanonicalSourceUrl(source, currentVideoId),
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DetectedVideoFps = DetectedVideoFps,
            };
        }
    }

    public static float ClampPlaybackRate(float rate)
    {
        if (!float.IsFinite(rate))
            return 1.0f;

        return Math.Clamp(rate, 0.25f, 2.0f);
    }

    public static float QuantizeVolume(float volume)
    {
        var clamped = float.IsFinite(volume) ? Math.Clamp(volume, 0.0f, 1.0f) : 0.0f;
        return clamped <= 0.0f
            ? 0.0f
            : MathF.Ceiling(clamped * 100.0f) / 100.0f;
    }

    private void UpdateFromStatusMessage(JsonElement root, float configuredPlaybackRate)
    {
        var title = BrowserSourceDescriptors.TryGetString(root, "title", string.Empty);
        var currentVideoId = BrowserSourceDescriptors.TryGetString(root, "videoId", source.VideoId);
        var positionSeconds = BrowserSourceDescriptors.TryGetDouble(root, "positionSeconds", 0.0);
        var durationSeconds = BrowserSourceDescriptors.TryGetDouble(root, "durationSeconds", 0.0);
        var rate = (float)BrowserSourceDescriptors.TryGetDouble(root, "rate", configuredPlaybackRate);
        var stateCode = BrowserSourceDescriptors.TryGetInt(root, "state", -1);
        var playbackState = stateCode switch
        {
            1 or 3 => ScreenPlaybackState.Playing,
            2 or 5 => ScreenPlaybackState.Paused,
            _ => ScreenPlaybackState.Stopped,
        };

        var positionMs = BrowserMessageValidator.ToBoundedMilliseconds(positionSeconds);
        var durationMs = BrowserMessageValidator.ToBoundedMilliseconds(durationSeconds);
        UpdateTelemetry(playbackState, positionMs, durationMs, rate, title, currentVideoId);
        playerStatus = descriptor.FormatPlayerStatus(title, stateCode, positionSeconds, durationSeconds, root);
    }

    private void UpdateDetectedVideoFps(JsonElement root)
    {
        var fps = (float)BrowserSourceDescriptors.TryGetDouble(root, "fps", 0.0);
        if (fps >= 1.0f && fps <= 240.0f)
        {
            Volatile.Write(ref detectedVideoFps, fps);
            playerStatus = $"detected video fps: {fps:0.#}";
        }
    }
}
