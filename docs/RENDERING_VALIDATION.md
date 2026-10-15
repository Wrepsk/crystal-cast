# Rendering validation

Automated tests cover output selection and fallback latching, canonical COM identity, deferred error delivery, supported depth formats, and shader compilation. Software D3D11 (WARP) tests inspect rendered pixels for intersecting opaque screens, invisible and translucent pixels, cancelled submissions, texture lifetime across deferred draws, point-sampled depth boundaries, and linear video sampling across consecutive composite passes.

These tests do not validate the timing of FFXIV's render passes, graphics-driver recovery, or Wine translation. The following checks still need an actual game session.

| Setup | Checks | Expected result |
| --- | --- | --- |
| Windows, NVIDIA / AMD / Intel | Place a world screen partly behind a wall and a character; orbit the camera | Occlusion stays aligned with the scene; the stationary screen does not swim |
| Windows, native / DLSS / FSR / dynamic resolution | Repeat occlusion checks and change render resolution | Screens remain aligned; no persistent output loss or stale depth |
| Windows, temporal AA / DLSS, all placements and output layers | View a stationary screen edge and text with the camera still, then move and zoom the camera; repeat without temporal AA | No projection-induced subpixel vibration; camera motion remains immediate; wall and character occlusion stays aligned |
| Custom camera offsets / roll / orthographic camera | Repeat positioning and occlusion checks | Supplied camera projections are retained without applying a pixel-sized offset threshold; validate alignment and depth handling for each custom camera |
| Any output layer | Cross two solid screens at an angle; view from both sides | The nearer part wins at each pixel |
| Any output layer | Repeat with distance fade or partially visible occluded screens | Translucent pixels blend without blocking solid screens behind them; intersections between two translucent surfaces retain distance-sorting limitations |
| Windows, 30 / 60 / 144+ FPS | Walk, turn, orbit rapidly, stop abruptly, and use follow modes | No persistent drift; assess remaining follow latency and transient smoothing |
| Windows, fullscreen / borderless / windowed | Alt-tab, minimize/restore, and change resolution | Rendering resumes; unavailable depth produces blank screens rather than old occlusion |
| Windows, hook unavailable or stalled | Inspect output diagnostics | Native overlay is selected; a timeout fallback occurs after two seconds; the reason is reported |
| Wine / Proton / CrossOver, experimental option off | Load a new configuration and one saved with SceneComposite | Effective output is native overlay; browser capture remains JPEG |
| Wine / Proton / CrossOver, experimental option on | Select Scene composite and run the Linux trial below | Scene frames flush into the backbuffer, or diagnostics explain the automatic fallback; browser capture remains JPEG |
| Native overlay / ImGui | Put game UI over a screen | Native output stays behind native UI; ImGui masking is approximate around translucent UI |
| Any output layer | Delete a screen, switch its source/resolution, disable the plugin, and log out while video is playing | Cancelled screens do not replay; deferred texture references remain valid |

After automatic fallback, switch to another output layer and back to retry scene compositing. Graphics reinitialization also resets hook/timeout fallback. An experimental Wine callback failure stays latched across graphics recovery until the user changes layers or toggles the experimental option. Report the output mode, graphics settings, game FPS, GPU/driver, operating system or Wine version, and CrystalCast diagnostics when recording a failure.

## Experimental Wine scene-composite trial

Status: opt-in implementation available; no native Linux game-session validation yet. Windows/WARP tests exercise output policy, timeout and callback-failure latching, rendering, cancellation, and recovery. They do not exercise the native hook through Wine/DXVK. An installed hook alone is not proof of correct render timing or visible output.

The default remains native overlay. Existing configurations, including those already set to Scene composite, do not opt in automatically. No native Linux browser helper or new browser dependency is included, and capture remains WebView2 JPEG. The trial uses the existing game D3D11 device and backbuffer integration; it does not create a separate Vulkan renderer.

1. On a native Linux system running FFXIV and Dalamud, first confirm that the same browser screen is visible using Native overlay. A browser that cannot start is a separate prerequisite failure.
2. Copy baseline diagnostics and record the Linux distribution, X11/Wayland session, GPU/driver, Wine/Proton runner and DXVK versions, resolution, upscaler, and game FPS. CrystalCast's Windows OS report under Wine does not identify the host distribution.
3. Open Settings > Rendering, select Scene composite, and enable Try experimental scene composite under Wine. Copy diagnostics after video has been visible for at least ten seconds.
4. Confirm Effective output layer is Scene composite, Scene composite fallback is none, and scheduled/flushed counts keep increasing. Hook calls and backbuffer binds should increase too. Scene-stage binds identify binds with a depth buffer or resolution scaling; relaxed flushes count the existing delayed path for backbuffer binds without depth. A fallback to overlay is a recovered failure, not a successful scene-composite test.
5. Place a screen partly behind a wall/character, move and orbit the camera, and repeat for World, Follow player, and Follow camera placements. Check screen stability, depth alignment, native UI/nameplates in front, and loading/cutscene fades. A visible video with incorrect UI ordering does not pass.
6. Change resolution/upscaler, alt-tab, minimize/restore, switch layers while video plays, disable/re-enable the plugin, delete a screen, and log out/in. Check for invisible/stale frames, delayed rendering, or a sustained FPS drop. Copy diagnostics immediately after a failure.
7. Disable the experimental checkbox while Scene composite is selected. The effective layer should return to overlay and pending scene submissions must not replay. Re-enable it to retry. Hook installation errors and two-second pending-frame timeouts select overlay automatically; non-device-loss callback failures also select overlay after graphics recovery and stay disabled until an explicit retry.

Diagnostics distinguish common failure stages: hook unavailable/error means installation failed; hook calls without backbuffer binds suggest resource matching or a different render path; binds without successful flushes require investigation of scheduling, scene capture, or render exceptions. Increasing flushed counts with wrong occlusion/UI ordering points to render-stage compatibility rather than a missing hook. Retain the last graphics error when reporting callback failures.

WSL can help with isolated compatibility checks, but its Windows-backed virtual GPU does not validate native Linux graphics drivers or the full FFXIV render-hook path. A native Linux/Steam Deck game session remains necessary before promoting this option to supported/default output.

During live investigation on 2026-10-15, at 1920 x 1080, the render camera's `ProjectionMatrix` had zero X/Y offsets while `ProjectionMatrix2` varied by subpixel amounts each sample. The gameplay `Control.ViewProjectionMatrix`, transformed back through the render view, contained the previous sample's offset. Geometry and depth now use the two render-camera projections with a shared view snapshot. The user confirmed that screen vibration and transient object-shaped cutouts during camera movement were eliminated. The temporary measurement logging was removed after investigation.

## Known DLSS silhouette distortion

Foreground silhouette distortion at the screen boundary remains unresolved. In the live railing/lamp comparison, it appeared with DLSS in both Scene composite and Native overlay output, and disappeared with AMD FSR and TSCMAA. The user also observed the artifact in Aetherphone's MogCast. This is evidence of a shared DLSS/compositing limitation, rather than proof that it is unavoidable.

The tested workaround is to select **AMD FSR** in the game's graphics upscaling settings and use **TSCMAA** anti-aliasing. CrystalCast leaves those settings under the user's control.

Captured scene-depth and swap-chain-depth images were identical at 1920 x 1080 in the examined setup. Across stationary-camera samples, the depth silhouette tracked the render camera's current jitter more closely than the gameplay matrix's previous offset. These checks did not reveal a better depth-buffer choice or a simple resolution-scale correction.

A 2-by-2 visibility coverage filter did not visibly improve the artifact. A one-pixel foreground guard also failed and introduced a visible gap. Both experiments and all temporary readback/probe code were removed. Future investigation may require integrating screen color before DLSS reconstruction, including its color-space and temporal-history requirements, rather than changing the current mask width again.

Separately, a WARP regression test reproduced false foreground occlusion caused by an unbound sampler: Direct3D's default linear filtering interpolated unrelated foreground and background depth values. The verified fix binds explicit point/clamp depth sampling while retaining linear/clamp video sampling and explicit samplers in the composite/UI-mask passes. This fixes the reproducible sampling bug; the live comparison confirmed that it does not resolve the DLSS silhouette artifact.
