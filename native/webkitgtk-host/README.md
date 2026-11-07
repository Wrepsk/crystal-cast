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

Ubuntu 24.04 container, Xvfb software rendering, WebKitGTK 2.52.6, Wine 9.0:
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
