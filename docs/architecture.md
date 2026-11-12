# Architecture

CrystalCast is a Dalamud plugin for FINAL FANTASY XIV that plays web video (YouTube, Twitch, Dailymotion, Vimeo, or any web page) on screens placed in the game world. A hidden browser plays the video, its frames are copied into a texture, and that texture is drawn on a surface in the scene with depth occlusion. Other plugins can read and control the screens through IPC.

This document describes the code as it is on this branch. Each module has its own page:

| Module | Folder | Page |
| --- | --- | --- |
| Plugin entry and configuration | `CrystalCast/` (root) | [configuration.md](configuration.md) |
| Sources and providers | `CrystalCast/Sources/` | [sources.md](sources.md) |
| Browser backends and capture | `CrystalCast/Browser/`, `CrystalCast/Video/` | [browser.md](browser.md) |
| Rendering and placement | `CrystalCast/Rendering/` | [rendering.md](rendering.md) |
| Playlists | `CrystalCast/Playlist/` | [playlist.md](playlist.md) |
| IPC (implementation) | `CrystalCast/Ipc/` | [ipc.md](ipc.md) |
| IPC (reference for other plugins) | | [ipc-api.md](ipc-api.md) |
| Windows and UI | `CrystalCast/Windows/` | [ui.md](ui.md) |

## Repository layout

```
CrystalCast.sln
CrystalCast/                 the plugin
  Plugin.cs                  IDalamudPlugin entry: builds everything, owns the draw callback
  Configuration.cs           saved settings and the per-screen profile
  ConfigurationMigration.cs  normalizes and upgrades saved settings
  ConfigurationSaveCoordinator.cs  debounced saving
  CrystalCastServices.cs     the Dalamud services and shared objects passed around
  ScreenLimitPolicy.cs       how many screens may exist and run
  ScreenSourceKind.cs, ScreenOutputMode.cs, BrowserMediaEngine.cs  setting enums and their normalizers
  DiagnosticsReportBuilder.cs  the text report on Settings > Diagnostics
  Assets/                    embedded browser script, UI fonts
  Sources/                   what a link is, per provider
    Providers/               one ID parser and one player page per video site
  Browser/                   running a browser and reading its player
    WebView2/                the WebView2 backend, its runtime download, JPEG and window capture
    WebKitGtk/               the native Linux helper backend used under Wine
    Capture/                 policies for the window-capture frame pipeline
  Video/                     the frame-source contracts and frame types
  Rendering/                 textures, placement, and drawing through CrystalRenderer
  Playlist/                  per-screen playlists and video metadata
  Ipc/                       the IPC interface for other plugins
  Windows/                   the ImGui windows
    Panels/                  the main window's tabs and shared controls
    Style/                   theme tokens, widgets, fonts, artwork
CrystalCast.Tests/           xUnit tests, in folders that mirror the plugin's
CrystalRenderer/             git submodule: the world-space screen renderer
native/webkitgtk-host/       C source of the Linux WebKitGTK helper
tools/WebKitGtkHostSmoke/    developer smoke test for the helper session (not in the solution)
scripts/, .github/           packaging checks and CI
experiments/                 measurements and prototypes, not shipped
docs/                        this folder
```

Namespaces follow folders (`CrystalCast.Browser.WebView2`, `CrystalCast.Windows.Panels`, ...) with three deliberate exceptions:

- The files at the project root are in `CrystalCast`. The configuration classes must stay there; see [configuration.md](configuration.md#why-the-configuration-classes-cannot-move).
- `Browser/WebView2/WindowsCaptureProjectionStubs.cs` declares types in `Windows.*` and `ABI.Windows.*` for the embedded CsWinRT projection.
- Test classes are all in `CrystalCast.Tests`, whatever folder they are in.

## Main objects

`Plugin` creates one of each of these and disposes them in reverse:

- `Configuration`: the saved settings, including the list of `BrowserScreenProfile` (one per screen).
- `WorldScreenManager` (Rendering): keeps one `WorldScreenInstance` per allowed screen, each owning a frame source and its textures, and draws them every frame.
- `ScreenPlaylistAdvancer` and `PlaylistMetadataService` (Playlist): move a screen to its next video when one ends, and fill in titles and thumbnails.
- `ScreenStateIpc` (Ipc): registers the IPC channels and announces changes.
- `GenericWebIpcApprovalService` (Ipc): holds the user's decisions about web pages other plugins asked to open.
- The windows (Windows): `MainWindow`, `ConfigWindow`, and four dialogs.

`CrystalCastServices` is a record carrying the Dalamud services (`IDalamudPluginInterface`, `IFramework`, `IClientState`, `IObjectTable`, `IGameGui`, `ITextureProvider`, `IPluginLog`) plus the `ScreenPlacementResolver`, the `IBrowserFrameSourceFactory`, and the approval service. It is passed to the renderer and the IPC instead of each reading `Plugin`'s static properties.

## How the modules depend on each other

```
Windows ──► Rendering ──► Browser ──► Sources
   │            │            │
   ├──► Playlist ◄───────────┤ (telemetry)
   │
   └──► Ipc ──► Rendering, Playlist, Sources

everything ──► Configuration (root), Video (frame contracts)
```

- **Sources** knows nothing about browsers being run. It parses links and builds player pages.
- **Browser** implements `IVideoFrameSource` (in **Video**) for a source, using WebView2 or the WebKitGTK helper.
- **Rendering** asks `BrowserSourceProviderRegistry` (Sources) for a frame source through the injected `IBrowserFrameSourceFactory` (Browser), uploads its frames, and draws.
- **Playlist** changes which link a screen's profile points at; Rendering notices and reloads.
- **Ipc** changes profiles and commands players through `WorldScreenManager`, and reads state from it.
- **Windows** edits profiles directly and calls `ScreenStateIpc.PublishLocalState()` so changes are saved and announced.

## Per-frame flow

Dalamud calls `Plugin.OnDraw` once per frame on the game's main thread:

1. If the character is logged in: update the first-run guide, refresh the web-approval dialog, and run `AdvancePlaylists()` (playlist advancer and metadata service; a change is saved and published over IPC).
2. Draw the windows, unless the game UI is hidden (`HiddenUiPolicy`).
3. `WorldScreenManager.DrawWorld()`:
   - `SyncBrowserScreens()` creates, updates and disposes `WorldScreenInstance`s to match the configuration, and releases browsers of screens that should not run.
   - For each active screen, `TryPrepareFrame` resolves the placement, makes sure the frame source exists and is started, and uploads the newest frame to a texture.
   - The prepared screens are drawn into one CrystalRenderer frame on the chosen output layer.
4. `Configuration.ProcessPendingSave()` writes the configuration if a save was requested and the debounce elapsed.
5. `ApplyHiddenUiPolicy()` tells Dalamud whether to keep calling the draw callback while the UI is hidden or in GPose.

Separately, `ScreenStateIpc` runs on `IFramework.Update` four times a second to notice changes made inside the player (see [ipc.md](ipc.md)).

## Data flow: from a link to pixels

```
link typed in the UI / sent over IPC / next playlist entry
        │  stored in BrowserScreenProfile (per-provider fields)
        ▼
BrowserSourceProviderRegistry.CreateFrameSource      (Sources)
        │  BrowserFrameSourceRequest
        ▼
BrowserFrameSourceFactory                             (Browser)
        ├─ GenericWeb ──────────► GenericWebBrowserFrameSource (WebView2, loads the page itself)
        └─ video sites ─────────► BrowserFrameSource
                                     ├─ Wine + WebKitGTK engine ► WebKitGtkBrowserFrameSource
                                     └─ otherwise ──────────────► WebView2BrowserFrameSource
                                                                    (loads the provider's player page)
        │  frames: NativeVideoFrame (shared GPU texture) or VideoFrame (BGRA pixels)
        ▼
WorldScreenInstance                                   (Rendering)
        ├─ SharedVideoTexture   opens the shared texture
        └─ DynamicVideoTexture  uploads BGRA pixels
        ▼
CrystalRenderer frame  ►  scene composite or native overlay
```

Playback state flows the other way: the player page posts status messages to the plugin, `BrowserPlayerState` turns them into `MediaPlaybackTelemetry`, the screen instance caches it, and the UI, the playlist advancer and the IPC read it through `WorldScreenManager.GetPlaybackTelemetry`.

## Threads

- The game's main thread runs the draw callback, all UI, configuration changes, and every IPC handler (`IpcMainThreadDispatcher` moves calls from other threads onto it).
- Each WebView2 browser has its own STA thread with a message pump (`BrowserThread` inside `WebView2FrameSourceBase`).
- JPEG decoding, window-capture callbacks, the WebKitGTK helper connection, metadata lookups and the WebView2 download run on worker threads and hand results back through thread-safe fields or queues.

## Platforms

The plugin targets Windows x64. Under Wine/Proton (`WineEnvironment.IsWine`) Windows Graphics Capture is not used: WebView2 falls back to JPEG screencast capture, or the experimental native WebKitGTK helper renders provider pages. See [browser.md](browser.md) and the existing notes in `WEBKITGTK_FRAME_CAPTURE.md` and `LINUX_WEBKITGTK_TESTING.md`.

## Tests

`CrystalCast.Tests` covers the parts that do not need the game: configuration and migration, link parsing, limits, playlists, IPC shapes and change detection, remote-state rules, the frame-source factory mapping, page security policies, lifecycle and capture policies, and placement maths. Browser tests inject a fake `IBrowserFrameSourceFactory`; nothing starts WebView2, Windows Graphics Capture or the game. `CrystalRenderer.Tests` lives in the submodule.

```powershell
dotnet build CrystalCast.sln -c Debug -p:Platform=x64
dotnet test CrystalCast.sln -c Debug -p:Platform=x64
```
