# Plugin entry and configuration

Files at the root of `CrystalCast/`, namespace `CrystalCast`.

## Responsibility

- Start and stop the plugin (`Plugin`).
- Hold everything the user configured, per screen and globally (`Configuration`, `BrowserScreenProfile`).
- Bring saved settings of any earlier version into a valid current shape (`ConfigurationMigration`).
- Save without writing the file on every slider tick (`ConfigurationSaveCoordinator`).
- Decide how many screens may exist and how many may run (`ScreenLimitPolicy`).

## Plugin.cs

`Plugin : IDalamudPlugin` is the entry point Dalamud constructs.

Constructor, in order:

1. Empties the WebView2 trash folder and applies a pending "clear browser data" request, before any browser can start.
2. Creates `ScreenPlacementResolver`, `GenericWebIpcApprovalService`, and the `CrystalCastServices` record.
3. Loads the configuration, attaches persistence, normalizes it, and saves if normalizing changed anything.
4. Creates the fonts, `WorldScreenManager`, `ScreenPlaylistAdvancer`, `PlaylistMetadataService`, `ScreenStateIpc`, and the windows.
5. Registers the `/crystalcast` (main window) and `/ccsettings` (settings) commands, the draw and open-UI callbacks, and the territory, login and logout handlers.

Other members:

- `OnDraw`: the per-frame callback; see [architecture.md](architecture.md#per-frame-flow).
- `AdvancePlaylists`: runs the playlist advancer and the metadata service, saves, and publishes each change through `ScreenStateIpc`.
- `OnTerritoryChanged`: pauses the user's own screens that are fixed in the world (`ScreenPlacementMode.World`, not created by IPC), then saves and publishes.
- `OnLogout`: suspends rendering and closes the dialogs.
- `Dispose`: unregisters everything, disposes in reverse order, ends leftover WebView2 browser processes (`WebView2ProcessTracker.EndRemaining`), and flushes a pending save.

## Configuration.cs

`Configuration : IPluginConfiguration` is the root saved object.

- Global settings: `Enabled`, `IpcEnabled`, `OutputMode`, `KeepScreensWhenUiHidden`, `KeepScreensInGpose`, `YouTubeBrowserEngine` (the browser engine for all providers, despite its name), `WebView2JpegCaptureResolution`, gizmo options, diagnostics switches, first-run flags.
- `BrowserScreens`: the list of `BrowserScreenProfile`, in screen-list order; `ActiveBrowserScreenId` is the one selected in the UI.
- `PlacementPresets` and `ActivePlacementPresetId`.
- Limits as constants: `MaxBrowserScreens` (8, created by the user), `MaxIpcBrowserScreens` (56, created by plugins), `MaxRenderableBrowserScreens` (64), `MaxActiveBrowserScreens` (8 running at once).
- Version 1 compatibility fields (`PositionX`, `YouTubeUrl`, ... directly on `Configuration`). They are read once, by `CreateBrowserScreenFromLegacySettings`, to build the first screen.
- `FindBrowserScreen(screenId)`: plain lookup by ID. `GetActiveBrowserScreen()`: normalizes, then returns the active screen or the first.
- `Save()` only requests a save; `ProcessPendingSave()` (each frame) and `FlushPendingSave()` (unload) perform it.

`BrowserScreenProfile` is one screen:

- Identity and ownership: `ScreenId`, `Name`, `Enabled`, `CreatedByIpc`, `IpcOwnerId`, `SourceControlsLocked`, `SourceControlsOwnerId`, `LocalSequence`.
- `Placement` (`ScreenPlacementSettings`): mode, position, rotation, size, curve, occlusion and distance-fade values.
- `ProviderKind` and one block of settings **per provider** (`YouTubeUrl`, `TwitchUrl`, `VimeoVolume`, ...). All five blocks are kept so that switching provider and back loses nothing. Code never switches on the provider to pick a field; it goes through `BrowserProviderSettings` (see [sources.md](sources.md)).
- `PlaybackPaused`, spatial audio settings.
- Playlist: `Playlist` (`PlaylistEntry` list), `PlaylistCurrentEntryId`, `PlaylistShuffle`, `PlaylistLoop`, `PlaylistAllowIpcAdds`, `PlaylistAllowUserAdds`.
- `GenericWebUrlFromIpc` and `RequiresGenericWebIpcApproval`: whether this screen's web page must be approved by the user before it loads.
- `Clone()` is an exact copy (IPC updates replace a profile with a changed clone); `CloneAsNew(name)` is the UI's "duplicate" and drops ownership, lock, sequence and plugin permissions.

Other saved types in the same file: `ScreenPlacementSettings`, `ScreenPlacementPreset`, `PlaylistEntry`, and the enums `BrowserSourceProviderKind`, `ScreenPlacementMode`, `ScreenPlacementGizmoOperation`.

### Why the configuration classes cannot move

Dalamud saves plugin configuration with type names in the JSON. Renaming `Configuration`, `BrowserScreenProfile`, `ScreenPlacementSettings`, `ScreenPlacementPreset` or `PlaylistEntry`, or moving them to another namespace, would make existing users' saved settings fail to load. They therefore stay in namespace `CrystalCast` at the project root. Enum members keep their numeric values for the same reason.

TODO: this is based on how Dalamud's configuration serializer is known to behave; confirm against the Dalamud version in use before ever changing those names.

The per-provider fields on `BrowserScreenProfile` are duplicated five times for the same reason: restructuring them into nested objects needs a configuration version bump and a tested migration.

## ConfigurationMigration.cs

`ConfigurationMigration.Normalize(configuration)` is called from `Configuration.Normalize()`, which most code calls before reading the screen list. It returns whether anything changed. It:

- fixes missing IDs and out-of-range enum values;
- builds the first screen from version 1 fields when `BrowserScreensInitialized` is false;
- moves a Wine configuration from native overlay to scene composite once (`WineSceneCompositeDefaultApplied`);
- maps removed output modes and browser engines to current ones (`ScreenOutputModePolicy`, `BrowserMediaEnginePolicy`);
- normalizes every screen (`BrowserScreenProfile.Normalize`, which clamps provider settings through `BrowserSourceProviderRegistry.NormalizeProviderSettings` and the playlist through `ScreenPlaylistPolicy.Normalize`);
- disables enabled screens beyond the limits (`ScreenLimitPolicy.DisableScreensOutsideLimits`);
- disables all screens when the saved source was the removed local-video source, so nothing starts playing unexpectedly;
- keeps `ActiveBrowserScreenId` and `ActivePlacementPresetId` pointing at something that exists.

## ConfigurationSaveCoordinator.cs

`Request()` marks a save pending and restarts a 300 ms debounce. `Process()` saves when the debounce has elapsed; `Flush()` saves immediately if one is pending. Both are thread-safe.

## ScreenLimitPolicy.cs

Pure functions over the screen list:

- `CanCreateUserScreen` / `CanCreateIpcScreen`: creation limits (8 user, 56 IPC, 64 total).
- `GetAllowedScreens`: the screens within those limits, in list order.
- `GetActiveScreens`: the first 8 enabled allowed screens; only these get a running browser and are drawn.
- `CountDeferredActiveScreens`: enabled screens beyond the 8.
- `DisableScreensOutsideLimits`: used by normalization.

## Setting enums

- `ScreenSourceKind`: only `Browser` today; the JSON converter still reads the old `LocalVideo` and `YouTubeBrowser` names and numbers.
- `ScreenOutputMode`: `NativeOverlay`, `SceneComposite`; `ImGuiOverlay` is kept only so old settings load.
- `BrowserMediaEngine`: `Auto`, `WebView2Capture` (JPEG), `WebView2WindowCapture`, `WebKitGtk` (Wine only).
- `JpegCaptureResolution`: the frame size cap for JPEG capture.
- `ScreenPlaybackState`: `Stopped`, `Playing`, `Paused`.

## DiagnosticsReportBuilder.cs

Builds the plain-text report on **Settings > Diagnostics** from the configuration, `WorldScreenManager` diagnostics, the Wine detection, and the IPC API version.
