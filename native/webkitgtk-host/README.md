# Native WebKitGTK browser host (experimental)

Linux-only helper for CrystalCast running under Wine. When **Browser backend** is
set to **Native Linux WebKitGTK (experimental)**, each YouTube, Twitch, Dailymotion
or Vimeo screen starts one helper process. Generic Web keeps WebView2. Windows is
unchanged: the option is hidden and treated as Auto outside Wine.

Tester instructions: [docs/LINUX_WEBKITGTK_TESTING.md](../../docs/LINUX_WEBKITGTK_TESTING.md).
Background measurements: [experiments/webkitgtk-wine-dxvk](../../experiments/webkitgtk-wine-dxvk/MEASUREMENTS.md).

## How it works

```text
CrystalCast (Windows .NET, inside Wine)
  1. listens on 127.0.0.1:<ephemeral> with a random 256-bit token
  2. CreateProcessW("/bin/sh" via Wine path) launch.sh <helper> --width --height --fps
       Wine runs non-PE programs directly on Linux; launch.sh marks the helper
       executable (zip extraction drops the bit) and writes its log
  3. accepts the helper's connection and checks the token (constant-time)
crystalcast-webkit-host (Linux, GTK 4 + WebKitGTK 6.0 from the distribution)
  4. loads the plugin's generated player page with webkit_web_view_load_html at the
     same https://crystalcast.local/player/<id>.html address WebView2 uses
  5. on each widget invalidation: snapshot -> GSK texture -> gdk_texture_download
     into one of three leased BGRA slots of a 0600 file in $XDG_RUNTIME_DIR
  6. sends {"type":"frame","seq","slot"}; the plugin copies the slot through its
     Wine Z: mapping and acknowledges it before the slot is reused
```

Messages use a little-endian `uint32` length followed by UTF-8 JSON, both ways.
After the plugin maps the frame file it sends `mapped`, and the helper deletes the
file name so nothing else can open it. The helper exits when the connection
closes, so ending a screen or the game ends the browser. Wine returns no process
handle for Linux programs.

The page bridge installs `window.chrome.webview` (`postMessage`,
`addEventListener("message")`) in the top-level player document only, so the
provider page scripts and `CommandBridge.js` work unchanged. Page messages are
validated by the plugin with the existing nonce and size checks. Messages to the
page are re-serialized JSON values, never script text.

Browser policy: ephemeral network session, autoplay allowed, new windows,
downloads and permission requests denied, and the top-level document may not
navigate away from the player (it is reloaded instead). The WebKit sandbox stays
enabled.

## Failure handling

The helper is a separate Linux process, so a crash in WebKit or GTK cannot take
the game down. The screen then shows its fallback texture and the reason.

- **Clear reasons.** `launch.sh` writes the helper's output and a final
  `CRYSTALCAST_HOST_EXIT <code>` line to a log. The plugin turns that into a
  short status (WebKitGTK or GTK missing with package names, glibc too old,
  sandbox blocked, no display, killed or crashed, Flatpak sandbox) and writes
  the last 40 log lines to the Dalamud log (`/xllog`). Failed logs are kept in
  `%TEMP%\CrystalCast` inside the Wine prefix; successful ones are deleted.
- **Fast failure.** A helper that exits during startup is detected from that
  exit line within about half a second instead of waiting 30 seconds.
- **No restart loops.** A failed source stays failed until the screen's
  settings change; the renderer does not relaunch it. Inside the helper, a web
  process that crashes three times within a minute stops the helper.
- **No orphans.** The helper exits when the plugin connection closes (screen
  removed, plugin unloaded, game closed or crashed). Sends time out after five
  seconds, and frames stop once three slots are unacknowledged, so a hung game
  cannot make the helper grow or spin.
- **Game thread safety.** Calls from the render and UI threads only queue small
  messages and never throw; capture on/off is sent only when it changes.
- **Hidden window.** GTK only draws a page in a shown window, so the helper keeps
  one but hides it like WebView2's: it prefers X11 (XWayland on Wayland desktops,
  which Wine already uses for the game) and marks the window borderless, below
  all others, without a taskbar or pager entry, and not accepting focus. Only
  **Show browser controls** turns it into a normal, raised window. Without X11 it
  falls back to a visible Wayland window and reports that. Set
  `CRYSTALCAST_GDK_BACKEND=wayland` before launching the game to force Wayland.
- **Health reports.** Every two seconds the helper sends redraw and frame counts
  and whether its window is shown, minimized or suspended by the desktop. Frames
  are only captured after WebKit draws something new, so a paused page sends
  none.
- **Stalls.** If a playing video sends no frame for five seconds, the status and
  the Dalamud log say why from that report (minimized, suspended by the desktop,
  not shown, WebKit not drawing, or frame slots full), with the counters.

## Wine and Proton detection

Some Proton and Wine builds hide Wine's own exports (`wine_get_version`,
`wine_get_unix_file_name`, ...) from Windows programs. CrystalCast therefore also
treats the `HKCU\Software\Wine` registry key as Wine, infers a Linux host from
`Z:\proc`, and converts paths through the `Z:` drive (Wine's view of `/`). If the
plugin folder is not on `Z:` (for example Windows XIVLauncher under Steam/Proton),
the helper and launcher are copied into a new private folder under
`$XDG_RUNTIME_DIR` (or `/tmp`) for that screen and deleted afterwards. Diagnostics
show how Wine was detected. `CRYSTALCAST_IGNORE_WINE_EXPORTS=1` forces these
fallbacks for testing.

Proton normally runs games inside Steam's Linux Runtime container, which does
not contain the system's WebKitGTK; the helper then fails with a missing-library
error, and the status says that the container is the cause.

## Build

On Ubuntu 24.04 (the binary then runs on glibc 2.39+ distributions):

```sh
sudo apt-get install build-essential pkg-config libwebkitgtk-6.0-dev libgtk-4-dev
make
```

Or from Windows with Docker: `scripts/Build-LinuxTestPackage.ps1 -BuildHelper`.
That builds the helper, the Release plugin, verifies the zip contents and writes
`artifacts/linux-test/CrystalCast-<version>-linux-webkitgtk-test.zip`. The
plugin project only packages the helper when `build/crystalcast-webkit-host`
exists, so normal Windows builds and releases are unaffected.

## Tested so far

Ubuntu 24.04 container, Xvfb software rendering, WebKitGTK 2.52.6, Wine 9.0.
Window checks with Openbox: the helper window was borderless, below other
windows, without taskbar/pager entries and not focusable; the focused window kept
focus; frames continued at about 58 fps with a full-screen window on top, and
minimizing it was reported as minimized/suspended with zero frames. Not yet
checked: GNOME, KDE and other compositors, and the Wayland fallback.
Failure cases checked under Wine: WebKitGTK removed (reported in 4 s with
package names), no display, and the helper killed mid-stream (reported as
signal 9); no helper process remained afterwards in any case.
`tools/WebKitGtkHostSmoke` (the plugin's own session code, self-contained .NET 8
under Wine) started the helper through Wine, authenticated, exchanged page
messages both ways with origin `https://crystalcast.local`, and received
57.6 frames/s at 1280x720. A real YouTube embed (Big Buck Bunny 60 fps) played
through the helper at 52 frames/s with title/duration telemetry. Not yet tested:
FFXIV/Dalamud, real GPUs, Wayland desktops, whether a covered window keeps
updating, Flatpak/Steam runtimes.

Run the smoke test yourself with Wine and a display:
`dotnet publish tools/WebKitGtkHostSmoke -c Release -o <dir>`, copy the helper and
`launch.sh` (as `crystalcast-webkit-host.sh`) to `<dir>/native/linux-x64/`, then
`wine <dir>/WebKitGtkHostSmoke.exe 'Z:\path\to\dir\native\linux-x64'`. Keep `<dir>`
on a Linux filesystem; Wine cannot execute programs from some shared mounts.

## Implementation note

The helper's JSON parsing context (`jsc_context_new`) is created after the first
web view. A JavaScriptCore VM created earlier in the UI process stops WebKit from
starting any page load.
