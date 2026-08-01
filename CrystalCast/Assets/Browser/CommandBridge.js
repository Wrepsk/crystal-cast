(function () {
  if (!window.chrome || !window.chrome.webview || window.crystalCastHostBridgeInstalled) {
    return;
  }

  window.crystalCastHostBridgeInstalled = true;
  const expectedNonce = __CRYSTALCAST_NONCE__;
  window.chrome.webview.addEventListener("message", function (event) {
    const message = event && event.data ? event.data : {};
    if (message.nonce !== expectedNonce) {
      return;
    }
    switch (message.type) {
      case "play":
        window.crystalCastPlay && window.crystalCastPlay();
        break;
      case "pause":
        window.crystalCastPause && window.crystalCastPause();
        break;
      case "settings":
        window.crystalCastApplySettings && window.crystalCastApplySettings(message.settings || {});
        break;
      case "seekBy":
        window.crystalCastSeekBy && window.crystalCastSeekBy(Number(message.seconds || 0));
        break;
      case "seekTo":
        window.crystalCastSeekTo && window.crystalCastSeekTo(Number(message.seconds || 0));
        break;
      case "restart":
        window.crystalCastRestart && window.crystalCastRestart();
        break;
    }
  });
})();
