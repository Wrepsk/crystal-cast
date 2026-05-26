using System.Text.Json;

namespace CrystalCast.Video;

internal static class DailymotionPlayerPage
{
    public const string VirtualHostName = "crystalcast.local";
    public const string PlayerOrigin = $"https://{VirtualHostName}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildHtml(
        DailymotionSourceReference source,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume)
    {
        var configJson = JsonSerializer.Serialize(new
        {
            sourceKind = source.Kind.ToString(),
            videoId = source.VideoId,
            playlistId = source.PlaylistId,
            displayName = source.DisplayName,
            autoplay,
            loop,
            audioEnabled,
            volume = Math.Clamp(volume, 0.0f, 1.0f),
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

    #player > *,
    #player iframe {
      border: 0 !important;
      bottom: 0 !important;
      box-sizing: border-box !important;
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
      border-top-color: rgba(0, 110, 255, 0.95);
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
    let lastKnownTitle = crystalCastConfig.displayName || "Dailymotion";
    let lastKnownVideoId = crystalCastConfig.videoId || "";
    let lastKnownMuted = !crystalCastConfig.audioEnabled;
    let lastKnownPlayerVolume = clampVolume(crystalCastConfig.volume);
    let lastFrameLoadUnixMs = 0;
    let handlingLoop = false;
    let volumeMode = "normal";

    function installVisibilityShim() {
      try {
        Object.defineProperty(document, "hidden", { get: function () { return false; } });
        Object.defineProperty(document, "visibilityState", { get: function () { return "visible"; } });
      } catch (_) {
      }

      try {
        document.hasFocus = function () { return true; };
        window.onblur = null;
        window.onfocus = null;
      } catch (_) {
      }

      try {
        const NativeIntersectionObserver = window.IntersectionObserver;
        window.IntersectionObserver = function (callback, options) {
          const observer = NativeIntersectionObserver
            ? new NativeIntersectionObserver(callback, options)
            : null;
          this.observe = function (target) {
            if (observer) {
              observer.observe(target);
            }

            window.setTimeout(function () {
              callback([{
                boundingClientRect: target.getBoundingClientRect(),
                intersectionRatio: 1,
                intersectionRect: target.getBoundingClientRect(),
                isIntersecting: true,
                rootBounds: target.ownerDocument.documentElement.getBoundingClientRect(),
                target: target,
                time: performance.now()
              }], this);
            }.bind(this), 0);
          };
          this.unobserve = function (target) {
            if (observer) {
              observer.unobserve(target);
            }
          };
          this.disconnect = function () {
            if (observer) {
              observer.disconnect();
            }
          };
          this.takeRecords = function () {
            return observer ? observer.takeRecords() : [];
          };
        };
      } catch (_) {
      }
    }

    function hasValue(value) {
      return typeof value === "string" && value.length > 0;
    }

    function isPlaylistSource() {
      return crystalCastConfig.sourceKind === "Playlist" && hasValue(crystalCastConfig.playlistId);
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

    function shouldMute() {
      return !crystalCastConfig.audioEnabled || clampVolume(crystalCastConfig.volume) <= 0.001;
    }

    async function loadMetadata() {
      if (isPlaylistSource() || !hasValue(crystalCastConfig.videoId)) {
        return;
      }

      try {
        const response = await fetch("https://api.dailymotion.com/video/"
          + encodeURIComponent(crystalCastConfig.videoId)
          + "?fields=title,duration");
        if (!response.ok) {
          return;
        }

        const metadata = await response.json();
        if (metadata && typeof metadata.title === "string" && metadata.title.length > 0) {
          lastKnownTitle = metadata.title;
        }

        const duration = Number(metadata && metadata.duration);
        if (Number.isFinite(duration) && duration > 0) {
          lastKnownDurationSeconds = duration;
        }

        postStatus();
      } catch (error) {
        postScriptError("metadata", error);
      }
    }

    function normalizePlayerLayout() {
      const host = document.getElementById("player");
      if (!host) {
        return;
      }

      host.style.position = "fixed";
      host.style.inset = "0";
      host.style.overflow = "hidden";

      const nodes = host.querySelectorAll(":scope > *, iframe");
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

    function toStateCode(state) {
      if (!state) {
        return stateCode;
      }

      if (state.playerIsPlaying === true || state.videoIsPlaying === true) {
        return 1;
      }

      if (state.playerIsPlaying === false || state.videoIsPlaying === false) {
        return 2;
      }

      if (typeof state.playerPlaybackStatus === "string") {
        const status = state.playerPlaybackStatus.toLowerCase();
        if (status.indexOf("play") >= 0) {
          return 1;
        }

        if (status.indexOf("pause") >= 0 || status.indexOf("idle") >= 0) {
          return 2;
        }

        if (status.indexOf("end") >= 0) {
          return 0;
        }
      }

      return stateCode;
    }

    function readStateNumber(state, names, fallback) {
      if (!state) {
        return fallback;
      }

      for (let i = 0; i < names.length; i++) {
        const value = Number(state[names[i]]);
        if (Number.isFinite(value)) {
          return value;
        }
      }

      return fallback;
    }

    async function readPlayerState() {
      if (!player || typeof player.getState !== "function") {
        return null;
      }

      try {
        return await player.getState();
      } catch (_) {
        return null;
      }
    }

    async function updateFromPlayerState() {
      const state = await readPlayerState();
      if (!state) {
        return;
      }

      lastKnownPositionSeconds = readStateNumber(state, ["videoTime", "currentTime", "position"], lastKnownPositionSeconds);
      lastKnownDurationSeconds = readStateNumber(state, ["videoDuration", "duration"], lastKnownDurationSeconds);
      stateCode = toStateCode(state);

      if (typeof state.videoTitle === "string" && state.videoTitle.length > 0) {
        lastKnownTitle = state.videoTitle;
      }

      if (typeof state.videoId === "string" && state.videoId.length > 0) {
        lastKnownVideoId = state.videoId;
      }

      if (typeof state.playerIsMuted === "boolean") {
        lastKnownMuted = state.playerIsMuted;
      }

      const playerVolume = Number(state.playerVolume);
      if (Number.isFinite(playerVolume)) {
        lastKnownPlayerVolume = playerVolume > 1 ? playerVolume / 100.0 : playerVolume;
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

    async function setPlayerMute(muted) {
      const muteValue = !!muted;
      let handled = await callPlayer("setMute", muteValue);
      handled = await callPlayer("setMuted", muteValue) || handled;

      if (!handled) {
        if (muteValue) {
          handled = await callPlayer("mute") || handled;
        } else {
          handled = await callPlayer("unmute") || handled;
        }
      }

      lastKnownMuted = muteValue;
    }

    async function setPlayerVolume(volume) {
      const normalized = clampVolume(volume);
      const requested = volumeMode === "percent" ? normalized * 100.0 : normalized;
      const handled = await callPlayer("setVolume", requested)
        || await callPlayer("volume", requested);
      lastKnownPlayerVolume = normalized;

      if (!handled) {
        return;
      }

      const state = await readPlayerState();
      if (!state) {
        return;
      }

      const reported = Number(state.playerVolume);
      if (!Number.isFinite(reported)) {
        return;
      }

      const reportedNormalized = reported > 1 ? reported / 100.0 : reported;
      if (normalized > 0.05
        && normalized < 0.95
        && reportedNormalized > 0.98
        && volumeMode !== "percent") {
        volumeMode = "percent";
        await callPlayer("setVolume", normalized * 100.0);
        lastKnownPlayerVolume = normalized;
      } else {
        lastKnownPlayerVolume = reportedNormalized;
      }
    }

    async function applyAudioSettings(settings) {
      if (settings) {
        crystalCastConfig.audioEnabled = !!settings.audioEnabled;
        crystalCastConfig.volume = clampVolume(settings.volume);
        crystalCastConfig.loop = !!settings.loop;
      }

      if (!player) {
        return;
      }

      const muted = shouldMute();
      await setPlayerMute(muted);
      await setPlayerVolume(muted ? 0 : crystalCastConfig.volume);
    }

    async function safePlay() {
      await updateFromPlayerState();
      stateCode = 1;
      lastFrameLoadUnixMs = Date.now();
      await applyAudioSettings();
      await callPlayer("play");
      window.setTimeout(applyAudioSettings, 250);
      postStatus();
    }

    async function safePause() {
      await updateFromPlayerState();
      stateCode = 2;
      await callPlayer("pause");
      postStatus();
    }

    async function seekPlayer(seconds) {
      const value = Math.max(0, Number(seconds || 0));
      lastKnownPositionSeconds = value;
      lastFrameLoadUnixMs = Date.now();
      let handled = await callPlayer("seek", value);
      handled = await callPlayer("seekTo", value) || handled;
      handled = await callPlayer("setCurrentTime", value) || handled;
      if (!handled) {
        debug("Dailymotion seek method unavailable");
      }
    }

    async function postStatus() {
      if (!playerReady) {
        return;
      }

      await updateFromPlayerState();
      enforceLoopPolicy();
      post("status", {
        title: lastKnownTitle || crystalCastConfig.displayName || "Dailymotion",
        videoId: lastKnownVideoId || crystalCastConfig.videoId || "",
        positionSeconds: lastKnownPositionSeconds,
        durationSeconds: lastKnownDurationSeconds,
        rate: 1,
        state: stateCode,
        embedMode: "web-sdk",
        audioEnabled: !!crystalCastConfig.audioEnabled,
        muted: lastKnownMuted || shouldMute(),
        volume: lastKnownPlayerVolume
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

    function addPlayerEvents() {
      if (!player || typeof player.on !== "function" || !window.dailymotion || !window.dailymotion.events) {
        return;
      }

      const events = window.dailymotion.events;
      const eventMap = [
        [events.PLAYER_START, function () { markReady("Dailymotion player start"); }],
        [events.PLAYER_VIDEOCHANGE, function () { postStatus(); }],
        [events.VIDEO_PLAY, function () { stateCode = 1; postStatus(); }],
        [events.VIDEO_PLAYING, function () { stateCode = 1; setLoadingVisible(false); postStatus(); }],
        [events.VIDEO_PAUSE, function () { stateCode = 2; postStatus(); }],
        [events.VIDEO_END, function () { stateCode = 0; postStatus(); }],
        [events.VIDEO_TIMECHANGE, postStatus],
        [events.VIDEO_DURATIONCHANGE, postStatus],
        [events.PLAYER_VOLUMECHANGE, postStatus],
        [events.PLAYER_ERROR, function () {
          setLoadingVisible(false);
          post("error", { code: 0, message: "Dailymotion playback error" });
          postStatus();
        }]
      ];

      for (let i = 0; i < eventMap.length; i++) {
        const eventName = eventMap[i][0];
        if (!eventName) {
          continue;
        }

        try {
          player.on(eventName, eventMap[i][1]);
        } catch (error) {
          postScriptError("player.on:" + eventName, error);
        }
      }
    }

    async function markReady(reason) {
      if (playerReady || !player) {
        return;
      }

      playerReady = true;
      normalizePlayerLayout();
      setLoadingVisible(false);
      debug(reason || "Dailymotion player ready");
      post("ready", { videoId: crystalCastConfig.videoId || "" });
      await applyAudioSettings();
      if (crystalCastConfig.autoplay) {
        await safePlay();
      } else {
        stateCode = 2;
      }

      postStatus();
    }

    async function createPlayer() {
      if (!window.dailymotion || typeof window.dailymotion.createPlayer !== "function") {
        const message = "Dailymotion Web SDK did not expose createPlayer";
        setLoadingVisible(false);
        debug(message);
        post("error", { code: -2, message: message });
        return;
      }

      try {
        const params = {
          autoplay: crystalCastConfig.autoplay ? 1 : 0,
          mute: shouldMute() ? 1 : 0,
          controls: 0,
          "queue-enable": isPlaylistSource() ? 1 : 0,
          "sharing-enable": 0,
          "ui-logo": 0,
          "endscreen-enable": 0,
          "pip": "off",
          "pip-enable": 0,
          "playerMode": "normal",
          "start": Math.max(0, Math.floor(lastKnownPositionSeconds || 0))
        };

        const options = {
          params: params
        };

        if (isPlaylistSource()) {
          options.playlist = crystalCastConfig.playlistId;
          if (hasValue(crystalCastConfig.videoId)) {
            options.video = crystalCastConfig.videoId;
          }
        } else {
          options.video = crystalCastConfig.videoId;
        }

        player = await window.dailymotion.createPlayer("player", options);
        normalizePlayerLayout();
        addPlayerEvents();
        await markReady("Dailymotion Web SDK ready");
      } catch (error) {
        const message = "failed to create Dailymotion player: " + (error && error.message ? error.message : error);
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
      applyAudioSettings(settings).then(postStatus);
    };

    window.crystalCastSeekBy = function (seconds) {
      seekPlayer(lastKnownPositionSeconds + Number(seconds || 0)).then(postStatus);
    };

    window.crystalCastSeekTo = function (seconds) {
      seekPlayer(Number(seconds || 0)).then(postStatus);
    };

    window.crystalCastRestart = function () {
      seekPlayer(0).then(safePlay).then(postStatus);
    };

    installVisibilityShim();
    loadMetadata();
    debug("loading Dailymotion Web SDK");
    const tag = document.createElement("script");
    tag.async = true;
    tag.src = "https://geo.dailymotion.com/libs/player.js";
    tag.onload = createPlayer;
    tag.onerror = function () {
      setLoadingVisible(false);
      debug("failed to load Dailymotion Web SDK");
      post("error", { code: -1, message: "failed to load Dailymotion Web SDK" });
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
