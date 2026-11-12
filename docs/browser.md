# Browser backends and capture

Folders `CrystalCast/Browser/` (with `WebView2/`, `WebKitGtk/`, `Capture/`) and `CrystalCast/Video/`.

## Responsibility

Run a hidden browser for a screen, keep it playing what the profile says, turn what it paints into frames the renderer can upload, and report the player's state. Everything here sits behind `IVideoFrameSource`, so the renderer does not know which browser is used.

## Contracts (`CrystalCast.Video`)

- `IVideoFrameSource`: `Start`, `Stop`, `TryGetLatestFrame(out VideoFrame)`, plus `Name`, `Width`, `Height`, `FramesPerSecond`, `IsRunning`, `Status`.
- `INativeVideoFrameSource` / `INativeVideoFrameAcknowledgement`: optional GPU path. `TryGetLatestNativeFrame` yields a `NativeVideoFrame` (a shared texture handle); the renderer acknowledges the handle when it is done with it.
- `IMediaPlaybackController`: `Play`, `Pause`, `SeekBy`, `SeekTo`, `Restart`, `ApplyPlaybackSettings(audio, volume, rate, loop, playNext)`.
- `IMediaPlaybackTelemetrySource`: `TryGetPlaybackTelemetry(out MediaPlaybackTelemetry)` with state, position, duration, rate, title, video ID, canonical link, detected video FPS and `Ended`.
- `VideoFrame`: BGRA pixels, pooled (`Rent`) and reference-leased (`TryAcquire`/`Dispose`).

Further optional interfaces are declared in `Sources/BrowserSourceDescriptor.cs`: `IBrowserFrameSourceRuntime` (provider, detected FPS, `UpdateCaptureFps`), `ICaptureRateSource` (measured capture FPS) and `IBrowserControlsHost` (show or hide the browser window for interaction).

## Choosing a backend

`BrowserFrameSourceFactory.Create(BrowserFrameSourceRequest)`:

- `GenericWeb` → `GenericWebBrowserFrameSource` (always WebView2).
- Any video site → `BrowserFrameSource`, a wrapper that creates its real source on `Start`:
  - under Wine with the WebKitGTK engine selected → `WebKitGtkBrowserFrameSource`;
  - otherwise → `WebView2BrowserFrameSource`.
  If creating the source throws, the wrapper substitutes an `UnavailableFrameSource` whose `Status` carries the reason, so the screen shows a message instead of failing the draw.

`BrowserPlatformPolicy` holds the two decisions:

- `ResolveCaptureMode(engine, isWine)`: JPEG capture (`WebView2CaptureMode.PreviewJpeg`) under Wine or when the JPEG engine is chosen; otherwise Windows Graphics Capture (`WindowGraphicsCapture`).
- `UsesWebKitGtk(engine, isWine, provider)`: Wine, WebKitGTK engine, and not Generic Web.

`WineEnvironment` (same file) detects Wine/Proton from ntdll's `wine_get_version` export or the `Software\Wine` registry key. `WineWebView2SetupPolicy` decides whether to offer the WebView2 download dialog.

## Shared browser pieces (`CrystalCast.Browser`)

- `BrowserPlayerPageResource` and `BrowserPageMessaging`: wrap a provider's HTML with the message protocol. Each page gets a random nonce; outgoing commands (`play`, `pause`, `seekTo`, ...) and incoming status messages carry it, and messages without the current nonce are dropped.
- `BrowserPlayerState`: turns authenticated status messages into `MediaPlaybackTelemetry` and a status string; also `ClampPlaybackRate` (0.25-2) and `QuantizeVolume`.
- `BrowserPlaybackIntent`: remembers whether the user last asked to play or pause, so a reloading page resumes the right way.
- `BrowserLifecycle`: the small state machine (start, running, stopping, stopped, faulted) the browser threads use to refuse commands after shutdown began.
- `BrowserCandidateLifetime`: TODO: describe; used when a newly created browser object must be disposed if shutdown started meanwhile.
- `BrowserSecurityPolicy.cs`:
  - `BrowserUriPolicy`: only credential-free HTTP/HTTPS addresses; host matching by exact host or real subdomain.
  - `BrowserNavigationPolicy`: a provider page may only stay on its generated document; Generic Web may go to other HTTP/HTTPS pages. Also checks a web message really came from the expected document.
  - `BrowserPermissionPolicy`: which browser permissions are granted (media autoplay only, per the README).
  - `BrowserMessageValidator`: bounds text and sizes in messages from pages.
- `BrowserProfileManager`: the WebView2 user-data folder per provider, and the "clear browser data on next start" request that `Plugin` applies before any browser runs.
- `BrowserRuntimeRetentionPolicy`: which screens keep a running browser (the active screens while the plugin is enabled).
- `BrowserAssetLoader`: reads the embedded `CommandBridge.js`.
- `BrowserCaptureFailureDiagnostics`: formats capture exceptions for the status line.

## WebView2 backend (`CrystalCast.Browser.WebView2`)

`WebView2BrowserFrameSource` (provider player pages) and `GenericWebBrowserFrameSource` (arbitrary pages) each own a nested `BrowserThread`: an STA thread with a message pump (`BrowserNativeMessagePump`, `BrowserSynchronizationContext`) on which the WebView2 environment, controller and view live. Commands from the main thread are queued to it; player messages are queued until the page reports ready.

The provider source serves its generated page from a virtual host and blocks navigation away from it. The Generic Web source navigates to the user's address, injects a controller script that finds and drives the page's media element, and asks an `IsNavigationAllowed` callback before each navigation (this is where IPC web approvals apply).

Both use:

- `WebView2BrowserGuards`: disables context menus, dev tools, accelerator keys, autofill and host objects; blocks popups and downloads; grants only allowed permissions; tracks the browser process.
- `WebView2HostWindow`: the off-screen window hosting the view, which can be brought forward for "Show controls" and returned to capture.
- Capture, one of:
  - `WebView2WindowCaptureSession`: Windows Graphics Capture of the host window into a rotating set of shared D3D11 textures with keyed mutexes. Frames reach the renderer as `NativeVideoFrame`. Uses the `Capture/` policies below. On a fatal error the source falls back to JPEG capture.
  - `WebView2ScreencastCapture`: Chromium DevTools screencast; JPEG frames decoded on a worker thread by `WicBgraDecoder` into `VideoFrame`s. `WebView2ScreencastPolicy` sets the size and quality from `JpegCaptureResolution`.
  - `WebView2JpegFrameCapture`: one screenshot per call (`CaptureAsync`). TODO: document exactly when the capture loop uses it instead of the screencast.
- `WebView2BrowserArguments`: Chromium switches for every environment.
- `WebView2ProcessTracker`: remembers browser process IDs so `Plugin.Dispose` can end leftovers.

Runtime discovery and download:

- `WebView2FixedRuntime`: finds an installed (Evergreen) runtime, else the Fixed Version runtime CrystalCast downloaded to `%LOCALAPPDATA%\CrystalCast\WebView2Runtime`; also the trash folder emptied at startup.
- `WebView2RuntimeDownloader`: downloads Microsoft's Fixed Version runtime, checks its signature (`AuthenticodeVerifier`), unpacks it (`CabinetExtractor`), and under Wine sets the registry mode WebView2 needs.

`WindowsCaptureProjectionStubs.cs` supplies a few WinRT types for the embedded CsWinRT projection; it is not CrystalCast logic.

### Known duplication

`WebView2BrowserFrameSource` and `GenericWebBrowserFrameSource` share most of their `BrowserThread` code (thread start-up and shutdown, the posted-action queue, window-capture start and fallback, the capture loop, frame publishing). The static, state-free parts were extracted into `WebView2BrowserGuards`. The rest was left as is: it is COM and threading code that only runs inside the game with a WebView2 runtime, and no test in this repository exercises it. Merging the two into a shared base class should be done together with in-game testing on Windows and Wine.

## WebKitGTK backend (`CrystalCast.Browser.WebKitGtk`)

Experimental, Wine only. `WebKitGtkBrowserFrameSource` starts the native Linux helper (`native/webkitgtk-host`) through `WineHostInterop`, which converts paths and launches a host program from inside Wine. `WebKitGtkHostSession` owns the helper process and its loopback control connection; frames arrive as BGRA slots in a shared file and are copied into `VideoFrame`s. `WebKitGtkFailureDiagnosis` and `WebKitGtkStallDiagnosis` turn helper logs and health reports into short status text. `WebKitGtkAvailability` reports whether the helper is packaged.

See `native/webkitgtk-host/README.md`, `WEBKITGTK_FRAME_CAPTURE.md` and `LINUX_WEBKITGTK_TESTING.md` for the helper itself.

## Capture policies (`CrystalCast.Browser.Capture`)

Small, separately tested pieces used by the window-capture session:

- `CaptureCallbackCoordinator`: lets capture callbacks run or be refused during shutdown, and waits for running ones before resources are released.
- `CaptureFrameDrainer`: takes the newest frame from the pool and releases older ones.
- `CaptureFrameProcessingPolicy` / `CaptureFrameCompletion`: whether to publish, skip or drop a frame.
- `CaptureFrameRateLimiter`: limits publishing to the requested FPS.
- `CaptureSlotPolicy`: the order in which shared-texture slots are tried.
- `CaptureResourceRetirementQueue<T>`: keeps replaced resources alive until the consumer acknowledged the current one.
- `FrameCadenceDiagnostics`: records frame timing for the diagnostics report.

## Security summary

Provider pages are generated locally and cannot navigate elsewhere. Popups, downloads, external schemes and sensitive permissions are blocked for every page. Each provider has its own browser profile. Generic Web pages run a CrystalCast script inside an arbitrary page and are not sandboxed beyond that; the UI says so, and pages requested over IPC wait for the user's approval. The README's "Browser security and privacy" section is the user-facing description.
