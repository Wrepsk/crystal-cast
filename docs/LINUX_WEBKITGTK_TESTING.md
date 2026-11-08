# Testing the native Linux browser in game

This is an **experimental** CrystalCast build for Linux players. Instead of the
Windows WebView2 browser (slow JPEG capture under Wine), YouTube, Twitch,
Dailymotion and Vimeo screens can play in your Linux system's own browser engine,
WebKitGTK, which sends finished frames straight to the game.

Thank you for testing! Nothing here changes your game files or Wine prefix.

## 1. Check that your system has WebKitGTK 6.0

Install these with your package manager if they are missing:

| Distribution | Packages |
|---|---|
| Ubuntu 24.04 or newer, Debian 13 | `libwebkitgtk-6.0-4 gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-libav` |
| Fedora 40 or newer | `webkitgtk6.0 gstreamer1-plugins-good gstreamer1-plugins-bad-free gstreamer1-plugin-libav` (the last one may need RPM Fusion) |
| Arch, Manjaro, EndeavourOS | `webkitgtk-6.0 gst-plugins-good gst-plugins-bad gst-libav` |

The GStreamer plugins let the browser decode video. Older distributions (for
example Ubuntu 22.04) do not have WebKitGTK 6.0. SteamOS on the Steam Deck is not
expected to work yet.

## 2. Install the test build

1. Extract the test zip into a folder of its own, for example `~/crystalcast-test`.
   Keep the `native` folder next to `CrystalCast.dll`.
2. In game, open Dalamud's settings (`/xlsettings`), go to **Experimental**, and add
   the full path to `CrystalCast.dll` under **Dev Plugin Locations**. Under Wine the
   path looks like `Z:\home\you\crystalcast-test\CrystalCast.dll`.
3. Open the plugin installer (`/xlplugins`), find **Crystal Cast** under
   **Dev Tools**, and enable it. Disable any other installed copy of CrystalCast first.

## 3. Turn on the native Linux browser

1. Open CrystalCast with `/crystalcast` and open its **Settings**.
2. On the **Browser** tab, set **Browser backend** to
   **Native Linux WebKitGTK (experimental)**.
3. Create or select a YouTube screen and press play.

The browser runs in a hidden window: it has no taskbar entry, never takes focus,
and stays behind your other windows, so you should not notice it. If you do see a
"CrystalCast browser" window, please say so in your report, and do not minimize
or close it (that pauses the video). **Show browser controls** brings it to the
front so you can use the player's own menus (quality, captions); close that window
to return.

Sound plays through your Linux desktop directly; the CrystalCast volume slider
still controls it.

## 4. What to report

Please send:

- Does the video appear on the in-game screen? Is it smooth?
- Does it keep playing smoothly in windowed and in fullscreen mode? Did you ever
  see a "CrystalCast browser" window or taskbar entry?
- Your distribution, desktop (GNOME, KDE, …), and whether you use Wayland or X11.
- How you launch the game (XIVLauncher.Core native package, Flatpak, Steam, …).
- **Settings → Diagnostics → Copy full diagnostics**, pasted into your message.
  Look at the **Source status** line for each screen; it shows the frame rate,
  the browser state, and any error.

If the native browser does not work, the game keeps running: the screen shows a
black placeholder and the **Source status** line says why in plain words, for
example which package to install. If playback freezes, it says why too (for
example a minimized or paused browser window), with numbers that help us. The Dalamud log (`/xllog`, lines starting with
"CrystalCast") has the browser's own output. When the browser fails, its log is
also kept in your Wine prefix under `drive_c/users/<you>/AppData/Local/Temp/CrystalCast/` (for
XIVLauncher.Core the prefix is usually `~/.xlcore/wineprefix`). Please attach it.

## Known limits

- Only YouTube, Twitch, Dailymotion and Vimeo use the native browser. Generic Web
  screens still use WebView2.
- Signing in and cookies are not kept between sessions.
- The Flatpak version of XIVLauncher, and launching through Steam with Proton,
  may not be able to start the browser, because those sandboxes do not include
  your system's WebKitGTK. The status line says so when this is the cause.
  Reports either way help.
- To go back, set **Browser backend** to **Auto**.
