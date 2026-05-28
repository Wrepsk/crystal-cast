using System.Text.Json;

namespace CrystalCast.Video;

internal static class VimeoPlayerPage
{
    public const string VirtualHostName = "crystalcast.local";
    public const string PlayerOrigin = $"https://{VirtualHostName}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildHtml(
        VimeoSourceReference source,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            videoId = source.VideoId,
            hash = source.Hash,
            displayName = source.DisplayName,
            autoplay,
            loop,
            audioEnabled,
            volume = Math.Clamp(volume, 0.0f, 1.0f),
            playbackRate = Math.Clamp(playbackRate, 0.25f, 2.0f),
        }, JsonOptions);

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="referrer" content="strict-origin-when-cross-origin">
  <style>
    html,
    body {
      background: #000;
      height: 100%;
      margin: 0;
      overflow: hidden;
      width: 100%;
    }

    body {
      bottom: 0;
      left: 0;
      position: fixed;
      right: 0;
      top: 0;
    }

    #player {
      background: #000;
      bottom: 0;
      contain: strict;
      left: 0;
      overflow: hidden;
      position: fixed;
      right: 0;
      top: 0;
    }

    #player iframe {
      border: 0 !important;
      bottom: 0 !important;
      display: block !important;
      height: 100% !important;
      left: 0 !important;
      margin: 0 !important;
      max-height: none !important;
      max-width: none !important;
      min-height: 100% !important;
      min-width: 100% !important;
      overflow: hidden !important;
      padding: 0 !important;
      position: absolute !important;
      right: 0 !important;
      top: 0 !important;
      transform: none !important;
      width: 100% !important;
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
      border-top-color: rgba(26, 183, 234, 0.95);
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
    let lastKnownPositionSeconds = 0;
    let lastKnownDurationSeconds = 0;
    let lastKnownTitle = crystalCastConfig.displayName || "Vimeo";
    let lastKnownVideoId = crystalCastConfig.videoId || "";
    let lastKnownMuted = !crystalCastConfig.audioEnabled;
    let lastKnownVolume = clampVolume(crystalCastConfig.volume);
    let lastKnownRate = clampRate(crystalCastConfig.playbackRate);
    let handlingLoop = false;

    function hasValue(value) {
      return typeof value === "string" && value.length > 0;
    }

    function buildVideoUrl() {
      if (!hasValue(crystalCastConfig.hash)) {
        return "";
      }

      return "https://vimeo.com/" + encodeURIComponent(crystalCastConfig.videoId)
        + "?h=" + encodeURIComponent(crystalCastConfig.hash);
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

    function debug(message) {
      try {
        console.log("CrystalCast: " + message);
        document.title = "CrystalCast:" + message;
        post("debug", { message: message });
      } catch (_) {
      }
    }

    function postScriptError(scope, error) {
      const message = scope + ": " + (error && error.message ? error.message : error);
      post("script-error", { message: message });
      debug(message);
    }

    window.onerror = function (message, source, lineno, colno, error) {
      postScriptError("window.onerror", error || message);
    };

    window.onunhandledrejection = function (event) {
      postScriptError("unhandledrejection", event && event.reason ? event.reason : "unknown promise rejection");
    };

    function setLoadingVisible(visible) {
      const overlay = document.getElementById("loadingOverlay");
      if (overlay) {
        overlay.classList.toggle("hidden", !visible);
      }
    }

    function clampVolume(volume) {
      return Math.max(0, Math.min(1, Number(volume) || 0));
    }

    function clampRate(rate) {
      const value = Number(rate);
      return Number.isFinite(value) ? Math.max(0.25, Math.min(2, value)) : 1;
    }

    function shouldMute() {
      return !crystalCastConfig.audioEnabled || clampVolume(crystalCastConfig.volume) <= 0.001;
    }

    function normalizePlayerLayout() {
      const host = document.getElementById("player");
      if (!host) {
        return;
      }

      host.style.position = "fixed";
      host.style.inset = "0";
      host.style.overflow = "hidden";

      const nodes = host.querySelectorAll("iframe");
      for (let i = 0; i < nodes.length; i++) {
        const node = nodes[i];
        node.style.position = "absolute";
        node.style.inset = "0";
        node.style.width = "100%";
        node.style.height = "100%";
        node.style.minWidth = "100%";
        node.style.minHeight = "100%";
        node.style.margin = "0";
        node.style.padding = "0";
        node.style.border = "0";
        node.style.maxWidth = "none";
        node.style.maxHeight = "none";
        node.style.overflow = "hidden";
        node.style.transform = "none";
      }
    }

    function callPlayer(methodName) {
      if (!player || typeof player[methodName] !== "function") {
        return Promise.resolve(false);
      }

      try {
        const args = Array.prototype.slice.call(arguments, 1);
        return Promise.resolve(player[methodName].apply(player, args)).then(function () {
          return true;
        }, function (error) {
          postScriptError(methodName, error);
          return false;
        });
      } catch (error) {
        postScriptError(methodName, error);
        return Promise.resolve(false);
      }
    }

    async function updateFromPlayer() {
      if (!playerReady || !player) {
        return;
      }

      try {
        const values = await Promise.allSettled([
          player.getCurrentTime(),
          player.getDuration(),
          player.getVideoTitle(),
          player.getPaused(),
          player.getVolume(),
          player.getPlaybackRate ? player.getPlaybackRate() : Promise.resolve(lastKnownRate)
        ]);

        const position = values[0].status === "fulfilled" ? Number(values[0].value) : NaN;
        const duration = values[1].status === "fulfilled" ? Number(values[1].value) : NaN;
        const title = values[2].status === "fulfilled" ? values[2].value : "";
        const paused = values[3].status === "fulfilled" ? values[3].value : null;
        const volume = values[4].status === "fulfilled" ? Number(values[4].value) : NaN;
        const rate = values[5].status === "fulfilled" ? Number(values[5].value) : NaN;

        if (Number.isFinite(position)) {
          lastKnownPositionSeconds = position;
        }

        if (Number.isFinite(duration)) {
          lastKnownDurationSeconds = duration;
        }

        if (typeof title === "string" && title.length > 0) {
          lastKnownTitle = title;
        }

        if (typeof paused === "boolean") {
          stateCode = paused ? 2 : 1;
        }

        if (Number.isFinite(volume)) {
          lastKnownVolume = volume;
        }

        if (Number.isFinite(rate)) {
          lastKnownRate = rate;
        }
      } catch (error) {
        postScriptError("updateFromPlayer", error);
      }
    }

    async function applySettings(settings) {
      if (settings) {
        crystalCastConfig.audioEnabled = !!settings.audioEnabled;
        crystalCastConfig.volume = clampVolume(settings.volume);
        crystalCastConfig.loop = !!settings.loop;
        crystalCastConfig.playbackRate = clampRate(settings.playbackRate);
      }

      if (!player) {
        return;
      }

      const muted = shouldMute();
      await callPlayer("setMuted", muted);
      await callPlayer("setVolume", muted ? 0 : crystalCastConfig.volume);
      await callPlayer("setLoop", !!crystalCastConfig.loop);
      await callPlayer("setPlaybackRate", crystalCastConfig.playbackRate);
      lastKnownMuted = muted;
      lastKnownVolume = muted ? 0 : crystalCastConfig.volume;
      lastKnownRate = crystalCastConfig.playbackRate;
    }

    async function safePlay() {
      await applySettings();
      stateCode = 1;
      await callPlayer("play");
      window.setTimeout(applySettings, 250);
      postStatus();
    }

    async function safePause() {
      stateCode = 2;
      await callPlayer("pause");
      postStatus();
    }

    async function seekPlayer(seconds) {
      const value = Math.max(0, Number(seconds || 0));
      lastKnownPositionSeconds = value;
      await callPlayer("setCurrentTime", value);
      postStatus();
    }

    async function postStatus() {
      if (!playerReady) {
        return;
      }

      await updateFromPlayer();
      enforceLoopPolicy();
      post("status", {
        title: lastKnownTitle || crystalCastConfig.displayName || "Vimeo",
        videoId: lastKnownVideoId || crystalCastConfig.videoId || "",
        positionSeconds: lastKnownPositionSeconds,
        durationSeconds: lastKnownDurationSeconds,
        rate: lastKnownRate || 1,
        state: stateCode,
        audioEnabled: !!crystalCastConfig.audioEnabled,
        muted: lastKnownMuted || shouldMute(),
        volume: lastKnownVolume
      });
    }

    function enforceLoopPolicy() {
      if (!crystalCastConfig.loop || handlingLoop || lastKnownDurationSeconds <= 0) {
        return;
      }

      if (lastKnownPositionSeconds < 0.5 || lastKnownDurationSeconds - lastKnownPositionSeconds > 0.35) {
        return;
      }

      handlingLoop = true;
      seekPlayer(0).then(safePlay);
      window.setTimeout(function () {
        handlingLoop = false;
      }, 1200);
    }

    async function markReady(reason) {
      if (playerReady || !player) {
        return;
      }

      playerReady = true;
      normalizePlayerLayout();
      setLoadingVisible(false);
      debug(reason || "Vimeo player ready");
      post("ready", { videoId: crystalCastConfig.videoId || "" });
      await applySettings();
      if (crystalCastConfig.autoplay) {
        await safePlay();
      } else {
        stateCode = 2;
      }

      postStatus();
    }

    function installEvents() {
      player.on("loaded", function () {
        markReady("Vimeo player loaded");
      });
      player.on("play", function () {
        stateCode = 1;
        setLoadingVisible(false);
        postStatus();
      });
      player.on("pause", function () {
        stateCode = 2;
        postStatus();
      });
      player.on("ended", function () {
        stateCode = 0;
        postStatus();
      });
      player.on("timeupdate", function (data) {
        if (data && Number.isFinite(Number(data.seconds))) {
          lastKnownPositionSeconds = Number(data.seconds);
        }

        if (data && Number.isFinite(Number(data.duration))) {
          lastKnownDurationSeconds = Number(data.duration);
        }

        postStatus();
      });
      player.on("durationchange", postStatus);
      player.on("volumechange", function (data) {
        if (data) {
          lastKnownMuted = !!data.muted;
          if (Number.isFinite(Number(data.volume))) {
            lastKnownVolume = Number(data.volume);
          }
        }

        postStatus();
      });
      player.on("playbackratechange", function (data) {
        if (data && Number.isFinite(Number(data.playbackRate))) {
          lastKnownRate = Number(data.playbackRate);
        }

        postStatus();
      });
      player.on("error", function (error) {
        setLoadingVisible(false);
        post("error", { code: 0, message: "Vimeo playback error: " + (error && error.message ? error.message : error) });
      });
    }

    function createPlayer() {
      if (!window.Vimeo || typeof window.Vimeo.Player !== "function") {
        const message = "Vimeo SDK did not expose Player";
        setLoadingVisible(false);
        debug(message);
        post("error", { code: -2, message: message });
        return;
      }

      try {
        const options = {
          autopause: false,
          autoplay: !!crystalCastConfig.autoplay,
          byline: false,
          controls: false,
          dnt: true,
          loop: !!crystalCastConfig.loop,
          muted: shouldMute(),
          pip: false,
          playsinline: true,
          portrait: false,
          responsive: false,
          title: false
        };

        const videoUrl = buildVideoUrl();
        if (hasValue(videoUrl)) {
          options.url = videoUrl;
        } else {
          options.id = Number(crystalCastConfig.videoId);
        }

        player = new window.Vimeo.Player("player", options);
        normalizePlayerLayout();
        installEvents();
        window.setTimeout(function () {
          markReady("Vimeo player ready fallback");
        }, 4000);
      } catch (error) {
        const message = "failed to create Vimeo player: " + (error && error.message ? error.message : error);
        setLoadingVisible(false);
        debug(message);
        post("error", { code: -2, message: message });
      }
    }

    window.crystalCastPlay = function () {
      safePlay();
    };

    window.crystalCastPause = function () {
      safePause();
    };

    window.crystalCastApplySettings = function (settings) {
      applySettings(settings).then(postStatus);
    };

    window.crystalCastSeekBy = function (seconds) {
      seekPlayer(lastKnownPositionSeconds + Number(seconds || 0));
    };

    window.crystalCastSeekTo = function (seconds) {
      seekPlayer(Number(seconds || 0));
    };

    window.crystalCastRestart = function () {
      seekPlayer(0).then(safePlay).then(postStatus);
    };

    debug("loading Vimeo Player SDK");
    const tag = document.createElement("script");
    tag.async = true;
    tag.src = "https://player.vimeo.com/api/player.js";
    tag.onload = createPlayer;
    tag.onerror = function () {
      setLoadingVisible(false);
      debug("failed to load Vimeo Player SDK");
      post("error", { code: -1, message: "failed to load Vimeo Player SDK" });
    };
    document.head.appendChild(tag);

    window.setInterval(function () {
      normalizePlayerLayout();
      postStatus();
    }, 500);
  </script>
</body>
</html>
""";
    }
}
