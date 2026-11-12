# CrystalCast

CrystalCast is a Dalamud plugin for Final Fantasy XIV that puts video screens into the game world. Place a screen on a wall in your house, above a stage at a venue, or next to your character, give it a YouTube, Twitch, Dailymotion or Vimeo link (or any web page), and it plays right there in the 3D scene, behind characters and scenery that stand in front of it.

Screens are only drawn on your own game client. Other players don't see them unless they also run CrystalCast and a plugin that shares screens between players; CrystalCast offers that plugin an interface (see [For plugin developers](#for-plugin-developers-ipc)) but does not share anything by itself.

## What you can do

- **Up to 8 screens of your own**, each with its own video, size, position and settings. Up to 8 play at the same time; more stay set up and wait.
- **Place them anywhere:** pin a screen to a spot in the zone (**Fixed in place**), have it travel with your character (**Follows you**), or keep it in view (**Follows your view**). **Place in front of me** puts it where you are standing, while the Placement tab is open on-screen handles move, turn and resize it, and **Face me** turns it toward you. Screens can also be curved, faded out with distance, and made see-through where something is in front of them.
- **Play videos and streams** from YouTube (videos, playlists, live streams), Twitch (channels and past broadcasts), Dailymotion, Vimeo, or any web page (**Generic Web**). Play, pause, seek, loop, playback speed and volume are in the main window. **Show controls** opens the site's own player for captions, quality and other settings.
- **Playlists:** queue videos from any supported site to play one after another, with Shuffle, Loop playlist, Skip and reordering. Entries show the title, channel, length and thumbnail.
- **Sound:** each screen has its own volume. Spatial audio makes a screen quieter as you walk away from it.

CrystalCast never downloads or saves videos. Each screen runs the site's own embedded player in a small hidden browser and shows what that browser draws.

## Install

1. In game, open Dalamud's settings with `/xlsettings` and go to **Experimental**.
2. Under **Custom Plugin Repositories**, add this address, tick **Enabled**, and save:

   ```text
   https://raw.githubusercontent.com/Wrepsk/crystal-cast/repo/repo.json
   ```

3. Open `/xlplugins`, search for **Crystal Cast** and install it.

**Windows:** CrystalCast uses Microsoft Edge WebView2 for its browser, which Windows 10 and 11 normally already have.

**Linux (Wine, Proton, XIVLauncher, Steam):** WebView2 isn't included in Wine. The first time you log in, a setup window offers a one-click download of it; see [Playing on Linux](#playing-on-linux).

## Getting started

1. Type `/crystalcast` to open the main window (`/ccsettings` opens the settings).
2. Paste a link into **Link** on the **Source** tab and press **Load**.
3. Open the **Placement** tab and press **Place in front of me**, then drag the handles to move or turn the screen and set **Width** to resize it.
4. Use the **Playlist** tab to queue more videos, and the **Audio** tab for volume and spatial audio.

A short guide opens the first time you use CrystalCast; **Settings > Onboarding guide** shows it again.

## Playing on Linux

CrystalCast runs under Wine and Proton. Microsoft does not officially support WebView2 there, so Linux support is experimental, but YouTube and the other sites play.

If WebView2 is missing, a setup window appears when you log in (it can be reopened from **Settings > Browser > WebView2 under Wine > Open setup**). Its **Download and set up** button gets Microsoft's WebView2 runtime (about 310 MB to download, about 700 MB on disk) and sets it up inside the game's Wine prefix. It needs nothing installed on the Linux side and works the same with Wine, Proton, XIVLauncher, Steam and Flatpak. **Settings > Browser > Downloaded WebView2** shows how much space it uses and deletes it. Installing with `winetricks webview2` is offered as an alternative.

Under Wine, video reaches the game as a stream of JPEG pictures, which uses more CPU than on Windows. If screens stutter, lower **Settings > Browser > JPEG capture size** to 720p or 480p.

There is also an experimental **Native Linux WebKitGTK** backend under **Settings > Browser** that plays YouTube, Twitch, Dailymotion and Vimeo in the system's own WebKitGTK 6.0 browser instead of WebView2. See the [tester guide](docs/LINUX_WEBKITGTK_TESTING.md).

## Good to know

- **DLSS:** with DLSS on, edges of things standing in front of a screen can look smeared. Switching the game's upscaling to **AMD FSR** with **TSCMAA** anti-aliasing avoids it. CrystalCast does not change game settings itself. See [rendering validation and findings](docs/RENDERING_VALIDATION.md).
- **Ads and sign-in** work as they do on the site: each site keeps its own browser profile. **Settings > Browser > Browser data > Clear on restart** wipes cookies, sign-ins and cache the next time CrystalCast loads.
- **Web pages (Generic Web)** run that site's own code. Only load sites you trust. Play, pause and seek work when the page has a normal video or audio player.
- **Changing zones** pauses screens pinned to the world, so a video doesn't keep playing where you left it.
- **When something goes wrong**, the main window shows what happened, and **Settings > Diagnostics > Copy full diagnostics** copies a report to include when asking for help.

## Safety and privacy

CrystalCast only draws screens and plays media on your own client. It does not automate gameplay, send game actions, change network traffic, read combat data, collect account information, or talk to FFXIV servers.

The browser blocks pop-ups, downloads, links that open other programs, and access to the camera, microphone, location, notifications and files. Playlist details (title, channel, thumbnail) come from the same site the video plays from. Web pages set by another plugin need your approval before they open. See [Browser security and privacy](#browser-security-and-privacy) for details.

---

The rest of this page is technical reference.

## Browser compatibility

CrystalCast supports two WebView2 capture paths:

- WebView2 window capture, the preferred Auto path on Windows; it uses Windows Graphics Capture to avoid CPU readback when supported.
- WebView2 JPEG capture, used as the Windows fallback and forced when running under Wine.

In Auto mode on Windows, CrystalCast prefers WebView2 window capture and falls back to WebView2 JPEG capture when Windows Graphics Capture is unavailable.

WebView2 JPEG capture receives JPEG frames from the browser's DevTools screencast (up to 1080p by default; **Settings > Browser > JPEG capture size** offers 480p, 720p, 1440p or the screen's own resolution up to 4K) and falls back to browser screenshots if the screencast cannot start. It is not a raw frame or texture feed, so it can have lower quality or higher CPU use than window capture. WebView2 window capture uses Windows Graphics Capture, synchronizes its shared D3D texture with keyed mutexes, and falls back to JPEG capture if the OS, capture session, or graphics device becomes unavailable.

YouTube accepts video URLs/IDs, playlist URLs/IDs, playlist watch URLs, channel IDs, and channel live embeds. Twitch accepts channel and VOD URLs. Dailymotion accepts video URLs/IDs and playlist URLs. Vimeo accepts video URLs/IDs, including unlisted video URLs with hashes. Generic Web accepts HTTP/HTTPS page URLs and syncs playback when it can access a page `<video>` or `<audio>` element.

YouTube no longer exposes programmatic quality selection through the IFrame API, so quality changes go through YouTube's own settings menu under **Show controls**.

| Source | Windows WebView2 | Wine WebView2 (experimental) | Notes |
|---|---:|---:|---|
| YouTube videos | Supported | Works | Wine uses JPEG capture. |
| YouTube Live | Supported | Not confirmed | Wine uses JPEG capture. |
| Twitch | Supported | Not confirmed | Wine uses JPEG capture. |
| Dailymotion | Supported | Not confirmed | Wine uses JPEG capture. |
| Vimeo | Supported | Not confirmed | Wine uses JPEG capture. |
| Generic Web | Supported | Not confirmed | Playback sync is best effort for browser-accessible HTML media; otherwise URL/page capture still works. |

If WebView2 is missing or unsupported for a source, CrystalCast reports the failure in the UI/status path and keeps the plugin running.

At most eight browser runtimes are active simultaneously. Additional enabled screens remain configured but are deferred in list order; the renderer and Diagnostics tab report this explicitly. GPU texture sampling diagnostics are disabled by default because they introduce a synchronous readback, and can be enabled temporarily from the Diagnostics tab.

Each WebView2 screen serves its player from a unique immutable in-memory page resource. Browser controls and telemetry use per-screen WebMessages protected by an instance nonce, so concurrent screens cannot overwrite a shared page or accept another screen's messages. Browser initialization and capture are cancellation-driven; commands and disposal do not synchronously wait on the render or UI thread. A screen always closes its browser when its source changes or it is removed, including a browser that is still starting, and turning the plugin off ends any CrystalCast browser process still running.

### Wine details

CrystalCast detects Wine, disables Windows Graphics Capture, and uses JPEG capture only. The browser's hidden capture window is kept off screen under Wine, because Linux window managers would otherwise show it over the game.

The **Download and set up** button downloads Microsoft's WebView2 Fixed Version runtime for x64 into `%LOCALAPPDATA%\CrystalCast\WebView2Runtime` inside the game's prefix, outside the plugin folder, so plugin updates keep it. The link comes from Microsoft's WebView2 download page, with a built-in link as a fallback, and only HTTPS links on Microsoft's download host are accepted. The archive's Microsoft signature is checked before it is unpacked. Under Wine, a signature whose root certificate the prefix doesn't trust is accepted; an unsigned or modified file is always rejected. The assistant also sets Wine's per-program Windows 7 mode for `msedgewebview2.exe` (`HKCU\Software\Wine\AppDefaults\msedgewebview2.exe`, as `winetricks webview2` does), because the WebView2 browser does not start under Wine without it (Wine bug 58921). An installed WebView2 is still preferred when present. Deleting the download closes browser screens first. The game keeps one small file (about 8 MB) loaded once a browser has run, so that file is moved aside and removed the next time CrystalCast loads.

The native WebKitGTK backend starts a native helper through Wine that renders the same player page and shares finished frames with the plugin, avoiding WebView2 and JPEG capture. Generic Web still uses WebView2. See the [helper design](native/webkitgtk-host/README.md).

## Rendering

CrystalCast draws screens with the in-repository [CrystalRenderer](CrystalRenderer/README.md) library, which renders world-space screen surfaces with scene-depth occlusion. CrystalRenderer was extracted from the screen-focused parts of a CrystalCast-specific Pictomancy fork; Pictomancy is no longer built or packaged.

The default render output on every platform, Wine included, is CrystalRenderer `RenderOutput.SceneComposite`, which composites the world screen into the game backbuffer before native UI/nameplates. **Settings > Rendering** can switch to **Native overlay**.

An unavailable scene-composite hook or a pending frame older than two seconds automatically selects the native overlay for the current graphics session. A non-device-loss scene callback exception also selects native overlay while graphics resources recover, preventing repeated failure/reinitialization loops; switch output layers and back to retry. Diagnostics report the effective output layer, fallback reason, hook activity, and scheduled/flushed frame counts. If the native overlay isn't available, screens are hidden rather than drawn through ImGui, and Settings > Rendering says so. See the [Wine scene-composite checklist](docs/RENDERING_VALIDATION.md#wine-scene-composite-checklist).

Opaque screen pixels use per-pixel reverse-depth testing, so intersecting solid screens occlude one another correctly. Transparent pixels are drawn afterward without writing depth; intersections between translucent surfaces still use distance sorting. Camera state and scene depth are sampled at the composite pass. If scene depth is unavailable or its format cannot be sampled, world screens are skipped until depth becomes usable again.

## Browser security and privacy

Provider URLs accept only credential-free HTTP/HTTPS URLs on the provider's exact host or a real subdomain; lookalike suffixes such as `youtube.com.example.org` are rejected. Provider player documents cannot navigate their top-level browser outside the generated player page. WebView2 blocks popups, downloads, external URL schemes, and sensitive browser permissions such as camera, microphone, location, notifications, and file access. It allows only its media-autoplay permission so host Play commands can operate embedded players. Browser telemetry is accepted only from the expected main document with the current screen nonce, and message size, JSON depth, text, URL, and media-time values are bounded.

WebView2 stores each provider in a separate profile. **Settings > Browser > Browser data** can schedule all CrystalCast browser cookies, local storage, cache, and saved state for deletion on the next plugin load, before a browser runtime starts. Legacy CrystalCast browser-profile locations are included in that cleanup.

Generic Web intentionally has a broader trust surface: it runs CrystalCast's media-control script inside the HTTP/HTTPS page supplied by the user. That page can track the user, navigate to other HTTP/HTTPS pages, and observe or fabricate its own playback telemetry. The nonce prevents cross-screen message confusion, but it does not make an untrusted Generic Web page trustworthy. Popups, downloads, external schemes, and sensitive browser permissions remain blocked, while media autoplay is allowed for playback control.

Playlist entries show the video's title, channel, length and thumbnail. For YouTube, Vimeo and Dailymotion links CrystalCast asks the site's public oEmbed endpoint when the entry is added (the same site the video would play from) and loads the thumbnail from that site's image host. Twitch has no such endpoint and its stream titles need API credentials, so a Twitch channel shows its name and Twitch's public live preview image (refreshed every five minutes, with Live or Offline). Twitch VODs and web pages are not fetched outside the browser; they get their title and length once they play.

## For plugin developers (IPC)

CrystalCast provides a Dalamud IPC interface (API version 11) that lets other plugins read its screens, create and control their own, manage playlists, and follow changes. It exchanges screen pose, source identity, playback state and visual settings only; pixels and audio are never shared. **Settings > IPC** can turn the whole interface off; screens created through IPC are removed while it is off.

- `CrystalCast.ApiVersion`, `CrystalCast.GetCapabilities`
- `CrystalCast.Ready` / `CrystalCast.Disposed` (events)
- `CrystalCast.Screen.GetSnapshot`, `CrystalCast.Screen.ApplyState`
- `CrystalCast.Screen.Create` / `Update` / `UpdateSource` / `SetSourceLock` / `GetSourceState` / `Remove`
- `CrystalCast.Screen.Changed` (event)
- `CrystalCast.Screen.Playlist.Get` / `Add` / `Remove` / `Clear` / `Skip` / `Move` / `SetOptions`

The full reference, with every channel's parameters, return values, error cases and C# examples, is in [docs/ipc-api.md](docs/ipc-api.md).

## Development

### Build

The repository pins its .NET SDK in `global.json`. A current Dalamud development installation is also required. CrystalRenderer is maintained in [its own repository](https://github.com/Wrepsk/crystal-renderer) and pinned here as the `CrystalRenderer` Git submodule, including its test project. Clone with `git clone --recurse-submodules`, or run `git submodule update --init --recursive` in an existing checkout before building.

1. Restore locked dependencies and build the solution:

   ```powershell
   dotnet restore CrystalCast.sln --locked-mode -p:Platform=x64
   dotnet build CrystalCast.sln -c Debug --no-restore -p:Platform=x64
   ```

2. Add `CrystalCast/bin/x64/Debug/CrystalCast.dll` as a Dalamud dev plugin.

### Tests

```powershell
dotnet test CrystalCast.sln -c Debug -p:Platform=x64
```

CrystalCast.Tests covers media and plugin behavior: the configuration model and migrations, provider URL parsers, screen and runtime budgets, playlists, IPC behavior and change filtering, remote-state sequencing, browser factory mapping, page isolation, lifecycle races, capture policies, and placement. The browser tests use an injected frame-source factory and do not start WebView2 or Windows Graphics Capture.

CrystalRenderer.Tests covers the extracted renderer: output fallback, deferred-error delivery, COM resource identity, depth-state and format policies, adaptive tessellation, native graphics error classification, and HLSL compilation with the Windows shader compiler. Pixel-level tests render crossing, transparent, and translucent screens using Windows' software D3D11 (WARP) device; they do not require FFXIV or a physical GPU.

### CI and releases

Pull requests and pushes to `master` build and test both Debug and Release configurations. Release builds produce `CrystalCast/bin/x64/Release/CrystalCast/latest.zip`; CI verifies its exact runtime contents and generated manifest before uploading it.

Pushing a numeric version tag (for example `0.9.1`) runs a Release build, tests, manifest and package verification, and publishes a GitHub Release with `CrystalCast-<version>.zip`. A final job then points `repo.json` on the `repo` branch, the custom plugin repository users add in Dalamud, at the new version and download links.

Configurations created before browser-only version 2 are migrated automatically. Existing browser screens are retained. If the active legacy source was local video, its screens are disabled during migration so browser media does not begin playing unexpectedly; the legacy placement is retained on the first browser screen, but local file and FFmpeg settings are discarded.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and release instructions, [CHANGELOG.md](CHANGELOG.md) for notable changes, [SECURITY.md](SECURITY.md) for vulnerability reporting and security boundaries, [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency provenance, and [dependency audit notes](docs/DEPENDENCY_AUDIT.md).
