# Rendering validation

Automated tests cover output selection and fallback latching, canonical COM identity, deferred error delivery, supported depth formats, and shader compilation. Software D3D11 (WARP) tests inspect rendered pixels for intersecting opaque screens, invisible and translucent pixels, cancelled submissions, and texture lifetime across deferred draws.

These tests do not validate the timing of FFXIV's render passes, graphics-driver recovery, or Wine translation. The following checks still need an actual game session.

| Setup | Checks | Expected result |
| --- | --- | --- |
| Windows, NVIDIA / AMD / Intel | Place a world screen partly behind a wall and a character; orbit the camera | Occlusion stays aligned with the scene; the stationary screen does not swim |
| Windows, native / DLSS / FSR / dynamic resolution | Repeat occlusion checks and change render resolution | Screens remain aligned; no persistent output loss or stale depth |
| Any output layer | Cross two solid screens at an angle; view from both sides | The nearer part wins at each pixel |
| Any output layer | Repeat with distance fade or partially visible occluded screens | Translucent pixels blend without blocking solid screens behind them; intersections between two translucent surfaces retain distance-sorting limitations |
| Windows, 30 / 60 / 144+ FPS | Walk, turn, orbit rapidly, stop abruptly, and use follow modes | No persistent drift; assess remaining follow latency and transient smoothing |
| Windows, fullscreen / borderless / windowed | Alt-tab, minimize/restore, and change resolution | Rendering resumes; unavailable depth produces blank screens rather than old occlusion |
| Windows, hook unavailable or stalled | Inspect output diagnostics | Native overlay is selected; a timeout fallback occurs after two seconds; the reason is reported |
| Wine / Proton / CrossOver | Load a new configuration and one saved with SceneComposite | Effective output is native overlay; browser capture remains JPEG |
| Native overlay / ImGui | Put game UI over a screen | Native output stays behind native UI; ImGui masking is approximate around translucent UI |
| Any output layer | Delete a screen, switch its source/resolution, disable the plugin, and log out while video is playing | Cancelled screens do not replay; deferred texture references remain valid |

After automatic fallback, switch to another output layer and back to retry scene compositing. Graphics reinitialization also resets the fallback. Report the output mode, graphics settings, game FPS, GPU/driver, operating system or Wine version, and CrystalCast diagnostics when recording a failure.
