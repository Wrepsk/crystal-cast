using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using CrystalCast.Video;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Dalamud.Plugin.Services;

namespace CrystalCast.Browser.WebView2;

/// <summary>
/// Shows any web page. The page is loaded as it is, and a controller script injected into it finds the
/// page's media element, carries out play, pause and seek, and reports playback back.
/// </summary>
internal sealed class GenericWebBrowserFrameSource : WebView2FrameSourceBase
{
    private const string PlaybackUnavailableStatus = "Playback sync unavailable for this page";

    private readonly GenericWebSourceReference source;
    private readonly string messageNonce = BrowserPageMessaging.CreateNonce();
    private readonly Func<string, bool>? isNavigationAllowed;
    private readonly object telemetryLock = new();
    private MediaPlaybackTelemetry telemetry = new();
    private bool settingsPublished;
    private volatile string playerStatus = "player not ready";

    public GenericWebBrowserFrameSource(
        string input,
        int width,
        int height,
        float captureFps,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate,
        WebView2CaptureMode captureMode,
        IPluginLog log,
        Func<string, bool>? isNavigationAllowed = null,
        JpegCaptureResolution jpegResolution = JpegCaptureResolution.Full1080p)
        : base(
            BrowserSourceDescriptors.GenericWeb,
            input,
            GenericWebUrl.TryParseSource(input, out var parsedSource),
            width,
            height,
            captureFps,
            autoplay,
            loop,
            audioEnabled,
            volume,
            playbackRate,
            captureMode,
            log,
            jpegResolution)
    {
        source = parsedSource;
        this.isNavigationAllowed = isNavigationAllowed;
        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, 0, PlaybackRate, string.Empty, input);
    }

    public override float DetectedVideoFps => 0.0f;

    protected override string PlayerStatus
    {
        get => playerStatus;
        set => playerStatus = value;
    }

    protected override string MessageNonce => messageNonce;

    protected override string StartUrl => source.Url;

    protected override string LoadingDescription => "Generic Web page";

    protected override string CaptureLogName => "Generic Web";

    protected override MediaPlaybackTelemetry CurrentTelemetry
    {
        get
        {
            lock (telemetryLock)
            {
                return telemetry;
            }
        }
    }

    /// <summary>There is no player page to wait for, so window capture starts with the page.</summary>
    protected override bool StartsWindowCaptureWithPage => true;

    /// <summary>Each page the user navigates to gets a fresh controller that reports ready again.</summary>
    protected override bool PlayerRestartsOnNavigation => true;

    public override void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop, bool playlistAutoplayNext)
    {
        volume = BrowserPlayerState.QuantizeVolume(volume);
        playbackRate = BrowserPlayerState.ClampPlaybackRate(playbackRate);

        if (settingsPublished && MatchesPlaybackSettings(audioEnabled, volume, playbackRate, loop))
            return;

        StorePlaybackSettings(audioEnabled, volume, playbackRate, loop);
        SendPlaybackSettings(playlistAutoplayNext: true);
        settingsPublished = true;
    }

    protected override void OnBrowserThreadCreated()
    {
        settingsPublished = false;
        ApplyPlaybackSettings(AudioEnabled, Volume, PlaybackRate, Loop, true);
    }

    protected override void SetPlaybackState(ScreenPlaybackState state)
    {
        UpdateTelemetry(state, GetTelemetryPositionMs(), GetTelemetryDurationMs(), PlaybackRate, GetTelemetryTitle(), GetTelemetryUrl());
    }

    protected override void HandleWebMessage(JsonElement root)
    {
        if (!BrowserPageMessaging.HasNonce(root, messageNonce)
            || !root.TryGetProperty("type", out var typeProperty))
            return;

        var type = typeProperty.GetString();
        switch (type)
        {
            case "ready":
                playerStatus = "media controller ready";
                MarkPlayerReady();
                SendPlaybackSettings(playlistAutoplayNext: true);
                settingsPublished = true;
                PlayIfRequested();
                break;
            case "status":
                UpdateFromStatusMessage(root);
                break;
            case "script-error":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "script error");
                break;
            case "debug":
                playerStatus = BrowserSourceDescriptors.TryGetString(root, "message", "browser debug");
                break;
        }
    }

    protected override bool IsFromExpectedDocument(string messageSource, string? currentDocument)
    {
        return BrowserNavigationPolicy.IsExpectedMessageSource(messageSource, currentDocument);
    }

    protected override void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs args)
    {
        if (!BrowserNavigationPolicy.IsAllowedGenericDocument(args.Uri))
        {
            args.Cancel = true;
            BrowserStatus = "blocked non-HTTP browser navigation";
            return;
        }

        if (isNavigationAllowed?.Invoke(args.Uri) == false)
        {
            args.Cancel = true;
            BrowserStatus = "waiting for approval for cross-domain navigation";
        }
    }

    protected override async Task PreparePageAsync(CoreWebView2 webView, CoreWebView2Environment environment)
    {
        await webView.AddScriptToExecuteOnDocumentCreatedAsync(BuildControllerScript(messageNonce));
    }

    protected override async Task OnPageLoadedAsync(CoreWebView2 webView)
    {
        BrowserStatus = "WebView2 Generic Web page loaded";
        try
        {
            await webView.ExecuteScriptAsync(BuildControllerScript(messageNonce));
        }
        catch (Exception ex)
        {
            playerStatus = $"controller injection failed: {ex.Message}";
        }
    }

    private void UpdateFromStatusMessage(JsonElement root)
    {
        var title = BrowserSourceDescriptors.TryGetString(root, "title", string.Empty);
        var url = BrowserSourceDescriptors.TryGetString(root, "url", source.Url, BrowserUriPolicy.MaximumUrlLength);
        var noMedia = BrowserSourceDescriptors.TryGetBool(root, "noMedia", false);
        var positionSeconds = BrowserSourceDescriptors.TryGetDouble(root, "positionSeconds", 0.0);
        var durationSeconds = BrowserSourceDescriptors.TryGetDouble(root, "durationSeconds", 0.0);
        var rate = (float)BrowserSourceDescriptors.TryGetDouble(root, "rate", PlaybackRate);
        var stateCode = BrowserSourceDescriptors.TryGetInt(root, "state", 0);
        var playbackState = noMedia
            ? ScreenPlaybackState.Stopped
            : stateCode switch
            {
                1 => ScreenPlaybackState.Playing,
                2 => ScreenPlaybackState.Paused,
                _ => ScreenPlaybackState.Stopped,
            };

        var positionMs = BrowserMessageValidator.ToBoundedMilliseconds(positionSeconds);
        var durationMs = BrowserMessageValidator.ToBoundedMilliseconds(durationSeconds);
        UpdateTelemetry(playbackState, positionMs, durationMs, rate, title, url, ended: !noMedia && stateCode == 0);
        playerStatus = noMedia
            ? PlaybackUnavailableStatus
            : string.IsNullOrWhiteSpace(title)
                ? $"media state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s"
                : $"{title}; state {stateCode}; {positionSeconds:0.0}s / {durationSeconds:0.0}s";
    }

    private void UpdateTelemetry(ScreenPlaybackState state, long positionMs, long durationMs, float rate, string title, string url, bool ended = false)
    {
        if (!GenericWebUrl.TryParseSource(url, out var parsed))
            parsed = source;

        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                DurationMs = durationMs,
                Rate = BrowserPlayerState.ClampPlaybackRate(rate),
                Title = title,
                VideoId = string.Empty,
                CanonicalUrl = parsed.Url,
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DetectedVideoFps = 0.0f,
                Ended = ended && state == ScreenPlaybackState.Stopped,
            };
        }
    }

    private string GetTelemetryTitle()
    {
        lock (telemetryLock)
        {
            return telemetry.Title;
        }
    }

    private string GetTelemetryUrl()
    {
        lock (telemetryLock)
        {
            return string.IsNullOrWhiteSpace(telemetry.CanonicalUrl) ? source.Url : telemetry.CanonicalUrl;
        }
    }

    private long GetTelemetryPositionMs()
    {
        lock (telemetryLock)
        {
            return telemetry.PositionMs;
        }
    }

    private long GetTelemetryDurationMs()
    {
        lock (telemetryLock)
        {
            return telemetry.DurationMs;
        }
    }

    private static string BuildControllerScript(string nonce)
    {
        var script = """
(() => {
  try {
    if (window.__crystalCastGenericWebInstalled || window.top !== window) {
      return;
    }
    window.__crystalCastGenericWebInstalled = true;

    const config = {
      audioEnabled: true,
      volume: 0.7,
      playbackRate: 1.0,
      loop: false
    };

    function post(type, data) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(Object.assign({ type }, data || {}));
        }
      } catch (_) {
      }
    }

    function collectMedia(targetWindow, depth, output) {
      if (!targetWindow || depth > 8) {
        return output;
      }

      let doc;
      try {
        doc = targetWindow.document;
      } catch (_) {
        return output;
      }

      if (!doc) {
        return output;
      }

      try {
        doc.querySelectorAll("video,audio").forEach((media) => output.push(media));
      } catch (_) {
      }

      try {
        doc.querySelectorAll("iframe,frame").forEach((frame) => {
          try {
            collectMedia(frame.contentWindow, depth + 1, output);
          } catch (_) {
          }
        });
      } catch (_) {
      }

      return output;
    }

    function isUsable(media) {
      return !!media && typeof media.play === "function" && typeof media.pause === "function";
    }

    function visibleArea(media) {
      if (!media || media.tagName !== "VIDEO") {
        return 0;
      }

      try {
        const rect = media.getBoundingClientRect();
        const style = media.ownerDocument.defaultView.getComputedStyle(media);
        if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) {
          return 0;
        }

        return Math.max(0, rect.width) * Math.max(0, rect.height);
      } catch (_) {
        return 0;
      }
    }

    function findMedia() {
      const media = collectMedia(window, 0, []).filter(isUsable);
      if (media.length === 0) {
        return null;
      }

      const playing = media.find((item) => !item.paused && !item.ended);
      if (playing) {
        return playing;
      }

      const videos = media
        .filter((item) => item.tagName === "VIDEO")
        .map((item) => ({ item, area: visibleArea(item) }))
        .sort((a, b) => b.area - a.area);
      if (videos.length > 0 && videos[0].area > 0) {
        return videos[0].item;
      }

      return media[0];
    }

    function finiteNumber(value, fallback) {
      return Number.isFinite(Number(value)) ? Number(value) : fallback;
    }

    function applySettings(media) {
      if (!media) {
        return;
      }

      try {
        media.muted = !config.audioEnabled || config.volume <= 0.001;
        media.volume = Math.max(0, Math.min(1, config.volume));
      } catch (_) {
      }

      try {
        media.playbackRate = Math.max(0.25, Math.min(2.0, config.playbackRate));
      } catch (_) {
      }

      try {
        media.loop = !!config.loop;
      } catch (_) {
      }
    }

    function seekTo(media, seconds) {
      if (!media || !Number.isFinite(Number(seconds))) {
        return;
      }

      try {
        const target = Math.max(0, Number(seconds));
        const duration = finiteNumber(media.duration, 0);
        media.currentTime = duration > 0 ? Math.min(target, duration) : target;
      } catch (_) {
      }
    }

    function publishStatus() {
      const media = findMedia();
      const title = document.title || "";
      const url = location.href || "";
      if (!media) {
        post("status", {
          noMedia: true,
          title,
          url,
          state: 0,
          positionSeconds: 0,
          durationSeconds: 0,
          rate: config.playbackRate
        });
        return;
      }

      applySettings(media);
      const duration = finiteNumber(media.duration, 0);
      const position = finiteNumber(media.currentTime, 0);
      const rate = finiteNumber(media.playbackRate, config.playbackRate);
      const state = media.ended ? 0 : (media.paused ? 2 : 1);
      post("status", {
        noMedia: false,
        title,
        url,
        state,
        positionSeconds: position,
        durationSeconds: duration,
        rate
      });
    }

    window.crystalCastPlay = function () {
      const media = findMedia();
      if (!media) {
        publishStatus();
        return;
      }

      applySettings(media);
      try {
        const result = media.play();
        if (result && typeof result.catch === "function") {
          result.catch(() => publishStatus());
        }
      } catch (_) {
      }
      publishStatus();
    };

    window.crystalCastPause = function () {
      const media = findMedia();
      if (media) {
        try {
          media.pause();
        } catch (_) {
        }
      }
      publishStatus();
    };

    window.crystalCastApplySettings = function (settings) {
      if (settings) {
        config.audioEnabled = !!settings.audioEnabled;
        config.volume = Math.max(0, Math.min(1, finiteNumber(settings.volume, config.volume)));
        config.playbackRate = Math.max(0.25, Math.min(2.0, finiteNumber(settings.playbackRate, config.playbackRate)));
        config.loop = !!settings.loop;
      }

      applySettings(findMedia());
      publishStatus();
    };

    window.crystalCastSeekBy = function (seconds) {
      const media = findMedia();
      if (media) {
        seekTo(media, finiteNumber(media.currentTime, 0) + finiteNumber(seconds, 0));
      }
      publishStatus();
    };

    window.crystalCastSeekTo = function (seconds) {
      seekTo(findMedia(), seconds);
      publishStatus();
    };

    window.crystalCastRestart = function () {
      const media = findMedia();
      if (media) {
        seekTo(media, 0);
        window.crystalCastPlay();
      } else {
        publishStatus();
      }
    };

    window.addEventListener("error", (event) => {
      post("script-error", { message: event && event.message ? event.message : "Generic Web script error" });
    });

    post("ready", { url: location.href || "" });
    publishStatus();
    setInterval(publishStatus, 500);
  } catch (error) {
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({
          type: "script-error",
          message: error && error.message ? error.message : String(error)
        });
      }
    } catch (_) {
    }
  }
})();
""";
        var nonceJson = JsonSerializer.Serialize(nonce);
        script = script.Replace(
            "Object.assign({ type }, data || {})",
            $"Object.assign({{ type, nonce: {nonceJson} }}, data || {{}})",
            StringComparison.Ordinal);
        return script + "\n" + BrowserPageMessaging.BuildCommandBridge(nonce);
    }
}
