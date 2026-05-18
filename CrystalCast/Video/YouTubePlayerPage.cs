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
        string videoId,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            videoId,
            autoplay,
            loop,
            audioEnabled,
            volume = (int)Math.Round(volume * 100.0f),
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
    html, body, #player {
      background: #000;
      height: 100%;
      margin: 0;
      overflow: hidden;
      width: 100%;
    }

    iframe {
      display: block;
      height: 100%;
      width: 100%;
    }
  </style>
</head>
<body>
  <div id="player"></div>
  <script>
    const crystalCastConfig = {{configJson}};
    let player = null;
    let playerReady = false;

    function post(type, data) {
      const payload = data || {};
      payload.type = type;
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(payload);
      } else if (window.CefSharp && window.CefSharp.PostMessage) {
        window.CefSharp.PostMessage(JSON.stringify(payload));
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
        crystalCastConfig.volume = Math.max(0, Math.min(100, Math.round(settings.volume * 100)));
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
        player.playVideo();
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

    function postStatus() {
      if (!playerReady || !player) {
        return;
      }

      try {
        const data = player.getVideoData ? player.getVideoData() : {};
        post("status", {
          title: data && data.title ? data.title : "",
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

    window.crystalCastRestart = function () {
      if (!playerReady || !player) {
        return;
      }

      try {
        player.seekTo(0, true);
        player.playVideo();
        postStatus();
      } catch (error) {
        postScriptError("restart", error);
      }
    };

    window.onYouTubeIframeAPIReady = function () {
      debug("YouTube IFrame API ready");
      player = new YT.Player("player", {
        width: "100%",
        height: "100%",
        videoId: crystalCastConfig.videoId,
        playerVars: {
          autoplay: crystalCastConfig.autoplay ? 1 : 0,
          controls: 0,
          disablekb: 1,
          fs: 0,
          iv_load_policy: 3,
          loop: crystalCastConfig.loop ? 1 : 0,
          modestbranding: 1,
          playsinline: 1,
          playlist: crystalCastConfig.loop ? crystalCastConfig.videoId : undefined,
          rel: 0,
          origin: crystalCastConfig.origin
        },
        events: {
          onReady: function () {
            playerReady = true;
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
              if (crystalCastConfig.loop && player && player.getPlayerState && player.getPlayerState() === YT.PlayerState.ENDED) {
                player.seekTo(0, true);
                player.playVideo();
              }
            } catch (error) {
              postScriptError("loop", error);
            }
            postStatus();
          },
          onError: function (event) {
            debug("YouTube player error " + (event && event.data ? event.data : 0));
            post("error", { code: event && event.data ? event.data : 0 });
          }
        }
      });
    };

    debug("loading YouTube IFrame API");
    const tag = document.createElement("script");
    tag.src = "https://www.youtube.com/iframe_api";
    tag.onerror = function () {
      debug("failed to load YouTube IFrame API");
      post("error", { code: -1, message: "failed to load YouTube IFrame API" });
    };
    document.head.appendChild(tag);
    window.setInterval(postStatus, 500);
  </script>
</body>
</html>
""";
    }
}
