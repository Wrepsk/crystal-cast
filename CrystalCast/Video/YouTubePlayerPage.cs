using System.Text.Json;

namespace CrystalCast.Video;

internal static class YouTubePlayerPage
{
    public const string VirtualHostName = "crystalcast.local";
    public const string PlayerOrigin = $"https://{VirtualHostName}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildHtml(
        YouTubeSourceReference source,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            sourceKind = source.Kind.ToString(),
            videoId = source.VideoId,
            playlistId = source.PlaylistId,
            liveChannelId = source.LiveChannelId,
            autoplay,
            loop,
            audioEnabled,
            volume = ToPlayerVolumePercent(volume),
            playbackRate,
            origin = PlayerOrigin,
        }, JsonOptions);

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="referrer" content="strict-origin-when-cross-origin">
  <style>
    html, body {
      background: #000;
      height: 100%;
      margin: 0;
      overflow: hidden;
      width: 100%;
    }

    body {
      position: relative;
    }

    #player {
      background: #000;
      height: 100%;
      width: 100%;
    }

    iframe {
      display: block;
      height: 100%;
      width: 100%;
    }

    #loadingOverlay {
      align-items: center;
      background: #050506;
      display: flex;
      inset: 0;
      justify-content: center;
      opacity: 1;
      pointer-events: none;
      position: absolute;
      transition: opacity 180ms ease;
      z-index: 10;
    }

    #loadingOverlay.hidden {
      opacity: 0;
    }

    .loadingWrap {
      align-items: center;
      display: flex;
      height: 96px;
      justify-content: center;
      position: relative;
      width: 96px;
    }

    .loadingRing {
      animation: crystalCastSpin 900ms linear infinite;
      border: 6px solid rgba(255, 255, 255, 0.14);
      border-radius: 50%;
      border-top-color: rgba(132, 213, 255, 0.95);
      box-sizing: border-box;
      height: 76px;
      width: 76px;
    }

    .loadingCore {
      animation: crystalCastPulse 1200ms ease-in-out infinite;
      background: rgba(255, 255, 255, 0.92);
      border-radius: 50%;
      height: 12px;
      position: absolute;
      width: 12px;
    }

    @keyframes crystalCastSpin {
      to {
        transform: rotate(360deg);
      }
    }

    @keyframes crystalCastPulse {
      0%, 100% {
        opacity: 0.38;
        transform: scale(0.85);
      }

      50% {
        opacity: 0.95;
        transform: scale(1.2);
      }
    }
  </style>
</head>
<body>
  <div id="player"></div>
  <div id="loadingOverlay" aria-hidden="true">
    <div class="loadingWrap">
      <div class="loadingRing"></div>
      <div class="loadingCore"></div>
    </div>
  </div>
  <script>
    const crystalCastConfig = {{configJson}};
    let player = null;
    let playerReady = false;

    function hasValue(value) {
      return typeof value === "string" && value.length > 0;
    }

    function isPlaylistSource() {
      return crystalCastConfig.sourceKind === "Playlist" && hasValue(crystalCastConfig.playlistId);
    }

    function isLiveChannelSource() {
      return crystalCastConfig.sourceKind === "LiveChannel" && hasValue(crystalCastConfig.liveChannelId);
    }

    function toPlayerVolumePercent(volume) {
      const clamped = Math.max(0, Math.min(1, Number(volume) || 0));
      if (clamped <= 0) {
        return 0;
      }

      return Math.max(1, Math.min(100, Math.ceil(clamped * 100)));
    }

    function post(type, data) {
      const payload = data || {};
      payload.type = type;
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(payload);
      } else if (window.CefSharp && window.CefSharp.PostMessage) {
        window.CefSharp.PostMessage(JSON.stringify(payload));
      }
    }

    function setLoadingVisible(visible) {
      const overlay = document.getElementById("loadingOverlay");
      if (overlay) {
        overlay.classList.toggle("hidden", !visible);
      }
    }

    function debug(message) {
      try {
        console.log("CrystalCast: " + message);
        document.title = "CrystalCast:" + message;
        post("debug", { message: message });
      } catch (_) {
      }
    }

    function postScriptError(scope, error) {
      post("script-error", { message: scope + ": " + (error && error.message ? error.message : error) });
      debug(scope + ": " + (error && error.message ? error.message : error));
    }

    window.onerror = function (message, source, lineno, colno, error) {
      postScriptError("window.onerror", error || message);
    };

    window.onunhandledrejection = function (event) {
      postScriptError("unhandledrejection", event && event.reason ? event.reason : "unknown promise rejection");
    };

    function applySettings(settings) {
      if (settings) {
        crystalCastConfig.audioEnabled = !!settings.audioEnabled;
        crystalCastConfig.volume = toPlayerVolumePercent(settings.volume);
        crystalCastConfig.playbackRate = settings.playbackRate;
        crystalCastConfig.loop = !!settings.loop;
      }

      if (!playerReady || !player) {
        return;
      }

      try {
        player.setPlaybackRate(crystalCastConfig.playbackRate);
      } catch (error) {
        postScriptError("setPlaybackRate", error);
      }

      enforceAudioSettings();
    }

    function enforceAudioSettings() {
      if (!playerReady || !player) {
        return;
      }

      try {
        const effectiveVolume = crystalCastConfig.audioEnabled ? crystalCastConfig.volume : 0;
        player.setVolume(effectiveVolume);
        if (effectiveVolume > 0) {
          player.unMute();
        } else {
          player.mute();
        }
      } catch (error) {
        postScriptError("setVolume", error);
      }
    }

    function safePlay() {
      if (!playerReady || !player) {
        return;
      }

      try {
        enforceAudioSettings();
        player.playVideo();
        window.setTimeout(enforceAudioSettings, 0);
        window.setTimeout(enforceAudioSettings, 250);
      } catch (error) {
        postScriptError("playVideo", error);
      }
    }

    function safePause() {
      if (!playerReady || !player) {
        return;
      }

      try {
        player.pauseVideo();
      } catch (error) {
        postScriptError("pauseVideo", error);
      }
    }

    function isLikelyLivePlayback() {
      if (!playerReady || !player || !player.getDuration) {
        return false;
      }

      try {
        const duration = Number(player.getDuration());
        return !Number.isFinite(duration) || duration <= 0;
      } catch (error) {
        return false;
      }
    }

    function postStatus() {
      if (!playerReady || !player) {
        return;
      }

      try {
        const data = player.getVideoData ? player.getVideoData() : {};
        post("status", {
          title: data && data.title ? data.title : "",
          videoId: data && (data.video_id || data.videoId) ? (data.video_id || data.videoId) : crystalCastConfig.videoId,
          playlistIndex: player.getPlaylistIndex ? player.getPlaylistIndex() : -1,
          positionSeconds: player.getCurrentTime ? player.getCurrentTime() : 0,
          durationSeconds: player.getDuration ? player.getDuration() : 0,
          rate: player.getPlaybackRate ? player.getPlaybackRate() : crystalCastConfig.playbackRate,
          state: player.getPlayerState ? player.getPlayerState() : -1
        });
      } catch (error) {
        postScriptError("postStatus", error);
      }
    }

    window.crystalCastPlay = function () {
      safePlay();
    };

    window.crystalCastPause = function () {
      safePause();
    };

    window.crystalCastApplySettings = function (settings) {
      applySettings(settings);
      postStatus();
    };

    window.crystalCastSeekBy = function (seconds) {
      if (!playerReady || !player) {
        return;
      }

      try {
        const current = player.getCurrentTime ? player.getCurrentTime() : 0;
        player.seekTo(Math.max(0, current + seconds), true);
        postStatus();
      } catch (error) {
        postScriptError("seekBy", error);
      }
    };

    window.crystalCastSeekTo = function (seconds) {
      if (!playerReady || !player) {
        return;
      }

      try {
        if (isLikelyLivePlayback()) {
          postStatus();
          return;
        }

        player.seekTo(Math.max(0, seconds), true);
        postStatus();
      } catch (error) {
        postScriptError("seekTo", error);
      }
    };

    window.crystalCastRestart = function () {
      if (!playerReady || !player) {
        return;
      }

      try {
        if (!isLikelyLivePlayback()) {
          player.seekTo(0, true);
        }

        enforceAudioSettings();
        player.playVideo();
        window.setTimeout(enforceAudioSettings, 0);
        window.setTimeout(enforceAudioSettings, 250);
        postStatus();
      } catch (error) {
        postScriptError("restart", error);
      }
    };

    window.onYouTubeIframeAPIReady = function () {
      debug("YouTube IFrame API ready");
      const playerVars = {
        autoplay: crystalCastConfig.autoplay ? 1 : 0,
        controls: 0,
        disablekb: 1,
        fs: 0,
        iv_load_policy: 3,
        loop: 0,
        modestbranding: 1,
        playsinline: 1,
        rel: 0,
        origin: crystalCastConfig.origin
      };

      if (isPlaylistSource()) {
        playerVars.loop = crystalCastConfig.loop ? 1 : 0;
        playerVars.listType = "playlist";
        playerVars.list = crystalCastConfig.playlistId;
      }

      const playerOptions = {
        width: "100%",
        height: "100%",
        playerVars: playerVars,
        events: {
          onReady: function () {
            playerReady = true;
            setLoadingVisible(false);
            applySettings();
            debug("YouTube player ready");
            post("ready", { videoId: crystalCastConfig.videoId });
            postStatus();
            if (crystalCastConfig.autoplay) {
              safePlay();
            }
          },
          onStateChange: function () {
            try {
              if (crystalCastConfig.loop && player && player.getPlayerState && player.getPlayerState() === YT.PlayerState.ENDED && !isPlaylistSource()) {
                player.seekTo(0, true);
                player.playVideo();
              } else if (crystalCastConfig.loop && player && player.getPlayerState && player.getPlayerState() === YT.PlayerState.ENDED && isPlaylistSource() && player.getPlaylist && player.getPlaylistIndex) {
                const playlist = player.getPlaylist();
                const index = player.getPlaylistIndex();
                if (playlist && playlist.length > 0 && index >= playlist.length - 1) {
                  player.playVideoAt(0);
                }
              }
            } catch (error) {
              postScriptError("loop", error);
            }
            postStatus();
          },
          onError: function (event) {
            setLoadingVisible(false);
            debug("YouTube player error " + (event && event.data ? event.data : 0));
            post("error", { code: event && event.data ? event.data : 0 });
          }
        }
      };

      if (hasValue(crystalCastConfig.videoId)) {
        playerOptions.videoId = crystalCastConfig.videoId;
      }

      if (isLiveChannelSource()) {
        const frame = document.createElement("iframe");
        frame.id = "youtubeLiveFrame";
        frame.setAttribute("frameborder", "0");
        frame.setAttribute("allow", "autoplay; encrypted-media");
        frame.src = "https://www.youtube.com/embed/live_stream?channel="
          + encodeURIComponent(crystalCastConfig.liveChannelId)
          + "&enablejsapi=1&autoplay=" + (crystalCastConfig.autoplay ? "1" : "0")
          + "&controls=0&disablekb=1&fs=0&iv_load_policy=3&playsinline=1&rel=0&origin="
          + encodeURIComponent(crystalCastConfig.origin);
        const host = document.getElementById("player");
        while (host.firstChild) {
          host.removeChild(host.firstChild);
        }

        host.appendChild(frame);
        player = new YT.Player("youtubeLiveFrame", playerOptions);
      } else {
        player = new YT.Player("player", playerOptions);
      }
    };

    debug("loading YouTube IFrame API");
    const tag = document.createElement("script");
    tag.src = "https://www.youtube.com/iframe_api";
    tag.onerror = function () {
      setLoadingVisible(false);
      debug("failed to load YouTube IFrame API");
      post("error", { code: -1, message: "failed to load YouTube IFrame API" });
    };
    document.head.appendChild(tag);
    window.setInterval(postStatus, 500);

    var crystalCastFpsDetected = false;

    function snapToStandardFps(fps) {
      var standards = [24, 25, 30, 50, 60];
      for (var i = 0; i < standards.length; i++) {
        if (Math.abs(fps - standards[i]) / standards[i] <= 0.1) {
          return standards[i];
        }
      }

      return Math.round(fps);
    }

    function tryDetectVideoFps() {
      if (crystalCastFpsDetected || !playerReady || !player) {
        return;
      }

      try {
        var iframe = document.querySelector("#player iframe");
        if (!iframe || !iframe.contentDocument) {
          return;
        }

        var video = iframe.contentDocument.querySelector("video");
        if (!video) {
          return;
        }

        if (typeof video.requestVideoFrameCallback === "function") {
          var samples = [];
          function onFrame(now, metadata) {
            if (crystalCastFpsDetected) {
              return;
            }

            samples.push(metadata.mediaTime);
            if (samples.length >= 15) {
              var intervals = [];
              for (var i = 1; i < samples.length; i++) {
                intervals.push(samples[i] - samples[i - 1]);
              }

              intervals.sort(function (a, b) { return a - b; });
              var median = intervals[Math.floor(intervals.length / 2)];
              if (median > 0) {
                var fps = snapToStandardFps(1.0 / median);
                crystalCastFpsDetected = true;
                post("video-fps", { fps: fps });
              }

              return;
            }

            video.requestVideoFrameCallback(onFrame);
          }

          video.requestVideoFrameCallback(onFrame);
          return;
        }

        if (typeof video.getVideoPlaybackQuality === "function") {
          var q0 = video.getVideoPlaybackQuality();
          var t0 = performance.now();
          setTimeout(function () {
            if (crystalCastFpsDetected) {
              return;
            }

            try {
              var q1 = video.getVideoPlaybackQuality();
              var dt = (performance.now() - t0) / 1000.0;
              var dFrames = q1.totalVideoFrames - q0.totalVideoFrames;
              if (dt > 0.5 && dFrames > 5) {
                var fps = snapToStandardFps(dFrames / dt);
                crystalCastFpsDetected = true;
                post("video-fps", { fps: fps });
              }
            } catch (_) {
            }
          }, 1500);
        }
      } catch (_) {
      }
    }

    window.setInterval(tryDetectVideoFps, 1000);
  </script>
</body>
</html>
""";
    }

    private static int ToPlayerVolumePercent(float volume)
    {
        if (!float.IsFinite(volume))
            return 0;

        var clamped = Math.Clamp(volume, 0.0f, 1.0f);
        return clamped <= 0.0f
            ? 0
            : Math.Clamp((int)MathF.Ceiling(clamped * 100.0f), 1, 100);
    }
}
