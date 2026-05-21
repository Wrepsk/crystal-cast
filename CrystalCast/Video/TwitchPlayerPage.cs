using System.Text.Json;

namespace CrystalCast.Video;

internal static class TwitchPlayerPage
{
    public const string VirtualHostName = "crystalcast.local";
    public const string PlayerOrigin = $"https://{VirtualHostName}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildHtml(
        TwitchSourceReference source,
        bool autoplay,
        bool audioEnabled,
        float volume)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            sourceKind = source.Kind.ToString(),
            videoId = source.VideoId,
            channelName = source.ChannelName,
            displayName = source.DisplayName,
            autoplay,
            audioEnabled,
            volume = Math.Clamp(volume, 0.0f, 1.0f),
            parent = VirtualHostName,
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
      border-top-color: rgba(145, 70, 255, 0.95);
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
    let stateCode = crystalCastConfig.autoplay ? 1 : 2;

    function isVodSource() {
      return crystalCastConfig.sourceKind === "Video" && crystalCastConfig.videoId;
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

    function enforceAudioSettings() {
      if (!playerReady || !player) {
        return;
      }

      try {
        const volume = Math.max(0, Math.min(1, Number(crystalCastConfig.volume) || 0));
        const muted = !crystalCastConfig.audioEnabled || volume <= 0.001;
        player.setMuted(muted);
        player.setVolume(muted ? 0 : volume);
      } catch (error) {
        postScriptError("setVolume", error);
      }
    }

    function applySettings(settings) {
      if (settings) {
        crystalCastConfig.audioEnabled = !!settings.audioEnabled;
        crystalCastConfig.volume = Math.max(0, Math.min(1, Number(settings.volume) || 0));
      }

      enforceAudioSettings();
    }

    function safePlay() {
      if (!playerReady || !player) {
        return;
      }

      try {
        if (crystalCastConfig.audioEnabled && Number(crystalCastConfig.volume) > 0.001 && player.setMuted) {
          player.setMuted(true);
        }

        player.play();
        stateCode = 1;
        window.setTimeout(enforceAudioSettings, 0);
        window.setTimeout(enforceAudioSettings, 250);
        window.setTimeout(enforceAudioSettings, 1000);
      } catch (error) {
        postScriptError("play", error);
      }
    }

    function safePause() {
      if (!playerReady || !player) {
        return;
      }

      try {
        player.pause();
        stateCode = 2;
      } catch (error) {
        postScriptError("pause", error);
      }
    }

    function markReady(reason) {
      if (playerReady || !player) {
        return;
      }

      playerReady = true;
      setLoadingVisible(false);
      applySettings();
      debug(reason || "Twitch player ready");
      post("ready", { videoId: crystalCastConfig.videoId || "" });
      if (crystalCastConfig.autoplay) {
        safePlay();
      }
      postStatus();
    }

    function addPlayerEvent(eventName, callback) {
      try {
        if (eventName && player && player.addEventListener) {
          player.addEventListener(eventName, callback);
        }
      } catch (error) {
        postScriptError("addEventListener", error);
      }
    }

    function getPositionSeconds() {
      if (!isVodSource() || !player || !player.getCurrentTime) {
        return 0;
      }

      try {
        const value = Number(player.getCurrentTime());
        return Number.isFinite(value) ? value : 0;
      } catch (_) {
        return 0;
      }
    }

    function getDurationSeconds() {
      if (!isVodSource() || !player || !player.getDuration) {
        return 0;
      }

      try {
        const value = Number(player.getDuration());
        return Number.isFinite(value) ? value : 0;
      } catch (_) {
        return 0;
      }
    }

    function getVideoId() {
      if (!isVodSource() || !player || !player.getVideo) {
        return crystalCastConfig.videoId || "";
      }

      try {
        return player.getVideo() || crystalCastConfig.videoId || "";
      } catch (_) {
        return crystalCastConfig.videoId || "";
      }
    }

    function postStatus() {
      if (!playerReady || !player) {
        return;
      }

      try {
        if (player.isPaused && player.isPaused()) {
          stateCode = 2;
        } else if (stateCode !== 0) {
          stateCode = 1;
        }

        post("status", {
          title: crystalCastConfig.displayName || "Twitch",
          videoId: getVideoId(),
          positionSeconds: getPositionSeconds(),
          durationSeconds: getDurationSeconds(),
          rate: 1,
          state: stateCode
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
      if (!playerReady || !player || !isVodSource()) {
        postStatus();
        return;
      }

      try {
        player.seek(Math.max(0, getPositionSeconds() + Number(seconds || 0)));
        postStatus();
      } catch (error) {
        postScriptError("seekBy", error);
      }
    };

    window.crystalCastSeekTo = function (seconds) {
      if (!playerReady || !player || !isVodSource()) {
        postStatus();
        return;
      }

      try {
        player.seek(Math.max(0, Number(seconds || 0)));
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
        if (isVodSource()) {
          player.seek(0);
        }

        safePlay();
        postStatus();
      } catch (error) {
        postScriptError("restart", error);
      }
    };

    function createPlayer() {
      debug("Twitch embed API ready");
      const options = {
        width: "100%",
        height: "100%",
        parent: [crystalCastConfig.parent],
        autoplay: !!crystalCastConfig.autoplay,
        muted: !!crystalCastConfig.autoplay || !crystalCastConfig.audioEnabled || Number(crystalCastConfig.volume) <= 0.001
      };

      if (isVodSource()) {
        options.video = crystalCastConfig.videoId;
      } else {
        options.channel = crystalCastConfig.channelName;
      }

      try {
        player = new Twitch.Player("player", options);
      } catch (error) {
        const message = "failed to create Twitch player: " + (error && error.message ? error.message : error);
        setLoadingVisible(false);
        debug(message);
        post("error", { code: -2, message: message });
        return;
      }

      window.setTimeout(function () {
        markReady("Twitch player ready fallback");
      }, 1500);
      window.setTimeout(function () {
        markReady("Twitch player late ready fallback");
      }, 4000);
      addPlayerEvent(Twitch.Player.READY, function () {
        markReady("Twitch player ready");
      });
      addPlayerEvent(Twitch.Player.PLAY, function () {
        markReady("Twitch player play event");
        stateCode = 1;
        postStatus();
      });
      addPlayerEvent(Twitch.Player.PLAYING, function () {
        markReady("Twitch player playing");
        stateCode = 1;
        setLoadingVisible(false);
        window.setTimeout(enforceAudioSettings, 0);
        window.setTimeout(enforceAudioSettings, 250);
        postStatus();
      });
      addPlayerEvent(Twitch.Player.PAUSE, function () {
        stateCode = 2;
        postStatus();
      });
      addPlayerEvent(Twitch.Player.ENDED, function () {
        stateCode = 0;
        postStatus();
      });
      addPlayerEvent(Twitch.Player.OFFLINE, function () {
        stateCode = 0;
        postStatus();
      });
      addPlayerEvent(Twitch.Player.ONLINE, function () {
        if (crystalCastConfig.autoplay) {
          stateCode = 1;
        }
        postStatus();
      });
      addPlayerEvent(Twitch.Player.SEEK, postStatus);
      addPlayerEvent(Twitch.Player.PLAYBACK_BLOCKED, function () {
        stateCode = 2;
        post("error", { code: 1, message: "Twitch playback was blocked" });
        postStatus();
      });
    }

    debug("loading Twitch embed API");
    const tag = document.createElement("script");
    tag.src = "https://player.twitch.tv/js/embed/v1.js";
    tag.onload = createPlayer;
    tag.onerror = function () {
      setLoadingVisible(false);
      debug("failed to load Twitch embed API");
      post("error", { code: -1, message: "failed to load Twitch embed API" });
    };
    document.head.appendChild(tag);
    window.setInterval(postStatus, 500);
  </script>
</body>
</html>
""";
    }
}
