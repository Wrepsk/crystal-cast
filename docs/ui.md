# Windows and UI

Folder `CrystalCast/Windows/`, namespaces `CrystalCast.Windows`, `CrystalCast.Windows.Panels`, `CrystalCast.Windows.Style`.

## Responsibility

The ImGui windows the user sees. They edit `Configuration` and `BrowserScreenProfile` directly, send player commands through `WorldScreenManager`, and call `ScreenStateIpc.PublishLocalState()` after a change so it is saved and announced to other plugins.

All windows are registered with one Dalamud `WindowSystem` in `Plugin`.

## Windows (`CrystalCast.Windows`)

- `CrystalCastWindow`: the base class. Pushes the CrystalCast window style before each draw and pops it after (`WindowPadding` is overridable), so the windows look the same under any Dalamud theme. `DrawDetail(label, value)` draws a dialog's labelled detail row.
- `MainWindow` (`/crystalcast`): the screen list, the header with the transport bar (play, pause, restart, seek, volume) for the active screen, and the tabs **Source**, **Audio**, **Playlist**, **Placement** (plus **IPC testing** in Debug builds). It owns the panels below. `SaveAndPublish()` is the one place its changes are committed. Deleting a screen another plugin created goes through `IpcScreenDeleteWindow`.
- `ConfigWindow` (`/ccsettings`): pages **Rendering** (output layer, hidden-UI behaviour), **Browser** (engine, JPEG capture size, WebView2 download, clear browser data), **Diagnostics** (checks and the report) and **IPC** (the IPC switch, hidden screens).
- `FirstRunGuideWindow`: shown once to new users. `FirstRunGuidePolicy.ShouldShow` (same file) decides when.
- `WineWebView2SetupWindow`: under Wine (always available in Debug builds), offers to download the WebView2 Fixed Version runtime and shows progress from `WebView2RuntimeDownloader`.
- `GenericWebIpcApprovalWindow`: asks the user to allow or refuse a web page another plugin wants to open, showing the screen, the reported owner, and the address. Driven by `GenericWebIpcApprovalService`.
- `IpcScreenDeleteWindow`: asks before deleting a plugin's screen, with the choice to keep it hidden for the session.

## Panels (`CrystalCast.Windows.Panels`)

The lists below summarize what each panel offers; the panel's `Draw` method is the exact set.

- `ScreenListPanel`: the list of screens with add, duplicate, delete, enable, and select.
- `BrowserSourceControlsPanel` (Source tab): the link field (provider detected by `BrowserSourceDetector`, with a confirmation before opening an unknown site as a web page), browser controls, resolution preset, capture FPS, autoplay, loop, play-next and playback rate. Which rows appear comes from `BrowserProviderCapabilities`; values are read and written through `BrowserProviderSettings`.
- `AudioControlsPanel` (Audio tab): browser audio on/off, volume, and spatial audio; also `DrawCompactVolume`, the transport bar's volume control.
- `PlaylistPanel` (Playlist tab): add, play now, skip, reorder, remove, clear, shuffle, loop, and the "Let other plugins add videos" switch. On a screen a plugin created it is read-only except for adding when the plugin allows it.
- `PlacementPanel` (Placement tab): anchor mode, position, rotation, size, curve, occlusion and distance fade, presets, "Face me", and the in-world gizmo (`DrawGizmo`, which calls `ScreenPlacementGizmo`).
- `PlacementUndoService`: per-screen undo history for placement edits (32 steps, edits within 350 ms are merged).
- `SourceControlUi`: shared helpers: the seek bar, resolution presets and formatting, source-lock checks (`IsSourceControlsLocked`, `LockedOwner`), playback-position formatting, and `TryDescribeSourceProblem`, which turns a source status string into a banner.

Controls that a source lock protects (link, play/pause, seek, loop, autoplay, rate, placement) are disabled while `SourceControlsLocked` is set and show who holds the lock. Audio, resolution and capture FPS stay editable.

## Style (`CrystalCast.Windows.Style`)

- `CrystalCastUiTheme`: colours, spacing and sizes as constants at 100% scale; `S(...)` applies Dalamud's global scale; `PushWindowStyle` / `PopWindowStyle`.
- `CrystalCastUiWidgets` (aliased as `Ui` in most files): the controls: buttons, icon buttons, switches, sliders, text fields, drag fields, banners, section headers, row labels, tabs.
- `CrystalCastFonts`: loads the three fonts from `Assets/Fonts` next to the plugin and falls back to Dalamud's fonts when a file is missing.
- `CrystalArtwork`: the drawn artwork behind the header and in thumbnails.
- `ComReferenceHold`: keeps a texture view alive for one more UI frame, because ImGui renders after the draw callback returns.

## Conventions

- Panels return `bool changed`; the window saves and publishes once per frame when anything changed.
- UI state that is not saved (drafts, hover timers) lives in small private state classes keyed by screen ID and is cleared when a screen is deleted (`ClearScreen`).
- Widget IDs use `##` suffixes that include the provider or screen so that controls of different screens do not share ImGui state.
