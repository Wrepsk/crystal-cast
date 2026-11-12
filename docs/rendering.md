# Rendering and placement

Folder `CrystalCast/Rendering/`, namespace `CrystalCast.Rendering`.

## Responsibility

Keep one runtime object per screen, get its newest video frame into a GPU texture, work out where the screen is in the world this frame, and draw all screens through the CrystalRenderer library. Also: spatial audio volume, the placement gizmo, and the policies that decide which output layer is used and whether screens stay visible when the game UI is hidden.

CrystalRenderer (the `CrystalRenderer/` submodule) does the actual drawing: world-space quads or curved surfaces, scene-depth occlusion, and the two output layers. This module does not contain shaders or hooks.

## `WorldScreenManager` (WorldScreenManager.cs)

Public class the rest of the plugin talks to. It owns the CrystalRenderer backend and a dictionary of `WorldScreenInstance` by screen ID.

- `DrawWorld()`: the per-frame entry. Suspends when not logged in, syncs instances with the configuration, checks for deferred render failures, and draws the active screens. Graphics failures are caught here: device loss resets graphics resources and retries after a delay; other failures in the scene-composite callback make `RenderOutputPolicy` fall back to the native overlay.
- `SyncBrowserScreens()` (private): creates an instance for each allowed screen (`ScreenLimitPolicy.GetAllowedScreens`), updates profiles, disposes instances of removed screens, and releases the browser of any screen `BrowserRuntimeRetentionPolicy` says should not be running.
- Player commands by screen: `TryPlayDynamicSource`, `TryPauseDynamicSource`, `TrySeekDynamicSourceTo`, `TryRestartDynamicSource`, `TryShowBrowserControls`, `TryHideBrowserControls`. The overloads without a screen act on the active screen and are used by the main window's transport bar.
- Reads by screen: `GetPlaybackTelemetry`, `GetSourceName`, `GetSourceStatus`, `GetMeasuredCaptureFps`, `GetDetectedVideoFps`, `TryGetPreviewTexture`, `AreBrowserControlsAvailable/Visible`.
- `PlaceBrowserScreenInFrontOfPlayer(screen)`.
- Status for the UI and diagnostics: `LastDrawStatus`, `LastGraphicsError`, `SceneCompositeStatus`, `EffectiveOutputMode`, `IsOutputUnavailable`, `SceneCompositeFallbackReason`, `BrowserResourceBudgetStatus`, `GetDiagnosticSnapshots()`.
- `SuspendForLogout()`, `ReleaseAllBrowserRuntimes()`, `Dispose()`.

### `WorldScreenInstance` (nested, private)

One screen at runtime: its profile, frame source, a `SharedVideoTexture` and a `DynamicVideoTexture`, cached telemetry, and audio metrics.

- `TryPrepareFrame`: stops and returns false when the screen is disabled or its placement cannot be resolved ("waiting for local player"); otherwise updates spatial audio, makes sure the frame source exists (`EnsureFrameSource`), applies live settings, uploads the newest frame, and returns the texture to draw.
- `EnsureFrameSource`: recreates the source when `BrowserSourceProviderRegistry.BuildFrameSourceSignature` changed (provider, link, size, engine, JPEG resolution). For a Generic Web screen that needs approval it first asks `GenericWebIpcApprovalService.EvaluateInitial`; while pending or denied no browser starts and the status says why.
- `ApplyLiveSourceSettings`: pushes audio, volume (after spatial attenuation), rate, loop and play-next to the player each frame, and the capture FPS.
- `DrawPrepared`: builds the appearance (occlusion alpha and tolerance, distance fade, curve) and draws a flat or curved surface on the render frame.
- Texture choice: a native frame goes through `SharedVideoTexture`; otherwise BGRA pixels go through `DynamicVideoTexture`. With no frame yet, a fallback placeholder texture is used.

`WorldScreenDiagnosticSnapshot` (same file) is the per-screen record the diagnostics report prints.

This file is about 1,600 lines. Splitting the nested instance into its own file (as a partial class) would make it easier to read; that was left out of this pass to keep the diff reviewable.

## Textures

- `SharedVideoTexture`: opens a shared D3D11 texture by handle on the game's device, takes its keyed mutex (`KeyedMutexSynchronization.TryAcquire`), and exposes a shader resource view. Optional GPU sampling diagnostics use `TextureSampleDiagnostics`.
- `DynamicVideoTexture`: creates and updates a texture from a `VideoFrame`'s BGRA pixels.
- `NativeGraphicsError`: classifies exceptions (`IsDeviceLost`, `IsWaitTimeout`).
- `GraphicsDiagnostics`: captures the graphics environment for the report.

## Placement

`ScreenPlacementSettings.Mode` is one of:

- `World`: position and rotation are world coordinates ("Fixed in place" in the UI).
- `FollowPlayer`: relative to the local player ("Follows you").
- `FollowCamera`: relative to the camera ("Follows your view").

`ScreenPlacementResolver` turns settings into a `ResolvedScreenPlacement` (world position, yaw, pitch, roll) for this frame.

- `TryResolve(placement[, predictionSeconds], out resolved)`: false when the frame of reference is unavailable (no local player).
- `TryConvertModePreservingWorld`: change mode without the screen moving.
- `TryApplyWorldPositionPreservingMode` / `TryApplyWorldRotationPreservingMode`: used by the gizmo to write a world-space result back into mode-relative settings.
- `PlaceInFrontOfPlayer`, `FaceCamera`.

`FollowFramePredictor` smooths and slightly leads the followed position and yaw so a following screen does not lag a frame behind. `PlacementPredictionContext` resets that prediction when the territory or the player object changes. `PlacementMath` has the shared `IsFinite` and `NormalizeRadians` helpers.

`ScreenPanelSizeResolver.Resolve(placement, sourceWidth, sourceHeight)` gives the panel size in metres from the configured size and the video's aspect ratio. TODO: document the exact fitting rule.

`CurvedScreenTessellation.GetSegmentCount` picks how many segments a curved screen is drawn with.

`ScreenPlacementGizmo.Draw(placement, options, resolver)` draws the in-world move, rotate and resize gizmo (shown only while the Placement tab is open) and returns whether the placement changed.

## Policies

- `RenderOutputPolicy`: scene composite is the default on every platform. `Resolve(requested, hookUnavailable, stalled)` falls back to the native overlay when the hook is missing, a frame stalled, or `ReportSceneCompositeFailure` was called, and stays there until the user switches layers or graphics reinitialize.
- `HiddenUiPolicy`: whether screens stay visible while the game UI is hidden or in GPose (scene composite only), and whether CrystalCast's windows are shown.
- `ClientRuntimePolicy.CanStart(isLoggedIn)`: nothing runs before a character is logged in.

## Spatial audio

Per screen, when enabled: the distance from the listener to the nearest point of the (possibly curved) panel is mapped between the "full volume" and "silent" radii to an attenuation, smoothed, and multiplied into the volume sent to the player. The Audio tab shows the distance, attenuation and applied volume.

TODO: confirm which position is used as the listener (player or camera) from `UpdateSpatialAudioMetrics`.

## Validation

`RENDERING_VALIDATION.md` in this folder has the manual checklist for output layers, including Wine.
