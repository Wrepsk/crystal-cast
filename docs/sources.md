# Sources and providers

Folder `CrystalCast/Sources/`, namespaces `CrystalCast.Sources` and `CrystalCast.Sources.Providers`.

## Responsibility

Answer, without starting a browser: what does this link or ID point at, which provider plays it, what HTML page plays it, and where on a screen's profile are that provider's settings.

A *provider* is one of `BrowserSourceProviderKind`: `YouTube`, `Twitch`, `Dailymotion`, `Vimeo`, `GenericWeb`. The four video sites are played inside a player page CrystalCast generates around the site's embed API. Generic Web loads the user's page directly and injects a media-control script.

## Key types

### `BrowserProviderSettings`

One instance per provider, holding getter and setter delegates for that provider's fields on `BrowserScreenProfile`: link, browser width and height, capture FPS and its manual switch, autoplay, audio on/off, volume, and where the provider has them, loop, "play next in playlist" and playback rate. A provider without a setting reads a fixed default (loop off, play-next on, rate 1.0) and ignores writes; `HasPlaybackRate` says whether the rate is real.

`BrowserProviderSettings.For(provider)` / `For(screen)` is the only place that maps a provider to its fields. The registry, the IPC adapters and the UI panels all read through it.

### `BrowserSourceDescriptor` and `BrowserSourceDescriptors`

A descriptor is what the browser layer needs to run a provider's player:

- `TryParse(input, out IBrowserSourceReference)`: parse a link or ID.
- `BuildHtml(source, BrowserPlaybackSettings)`: the player page.
- `BuildCanonicalSourceUrl(source, currentVideoId)`: a full link for what is playing.
- `IsValidVideoId`, `DescribeError` (player error codes to text), `FormatStatusSuffix`, `JsonOptions`.
- `VirtualHostName` / `PlayerOrigin`: the page is served from `https://crystalcast.local`.
- `WebView2AdditionalBrowserArguments`.

`BrowserSourceDescriptors.YouTube`, `.Twitch`, `.Dailymotion`, `.Vimeo`, `.GenericWeb` are the instances; `Get(provider)` returns one. The same file holds the small interfaces browser sources implement (`IBrowserFrameSourceRuntime`, `ICaptureRateSource`, `IBrowserControlsHost`) and JSON helpers for reading player messages (`TryGetString`, `TryGetDouble`, ...).

### `BrowserSourceProviderRegistry`

What the rest of the plugin asks about a screen's source:

- `Options`, `GetCapabilities(provider)`: display names and `BrowserProviderCapabilities` (playback rate, loop, playlist, browser controls).
- `CreateFrameSource(screen, engine, factory, ...)`: builds a `BrowserFrameSourceRequest` from the profile and hands it to the factory. With a playlist entry waiting, the video's own loop is turned off so the video can end. For Generic Web it also passes the navigation check and an approved start address.
- `BuildFrameSourceSignature(...)`: a string that changes when the frame source must be recreated (provider, link, size, engine, JPEG resolution).
- `GetSnapshot(screen)`: link, dimensions, runtime settings (audio, volume, rate, loop, play-next) and capture settings in one read.
- `GetUrl`, `SetUrl`, `ApplyCaptureFps`, `GetDetectedVideoFps`.
- `NormalizeProviderSettings(screen)`: clamps every provider's width (320-3840), height (180-2160), capture FPS (1-120), volume (0-1) and rate (0.25-2).

### `BrowserSourceDetector`

`TryDetect(input, currentProvider, out DetectedBrowserSource)` picks the provider for what the user pastes. A full link is matched by its site; any other valid web address becomes Generic Web (`IsGenericWebFallback`, which the UI confirms first); a bare ID stays with the screen's current provider because IDs are ambiguous across sites.

## Providers

`Sources/Providers/` has, per video site:

- `<Site>VideoId`: `TryParseSource(input, out <Site>SourceReference)`, `BuildCanonicalSourceUrl`, `IsValidVideoId`, and site-specific validators. The source reference is a record struct implementing `IBrowserSourceReference` (`DisplayName`, `VideoId`).
  - YouTube: video, playlist, or live channel (`YouTubeSourceKind`).
  - Twitch: channel or VOD (`TwitchSourceKind`).
  - Dailymotion: video or playlist (`DailymotionSourceKind`).
  - Vimeo: video, with an optional private-link hash.
- `<Site>PlayerPage`: `BuildHtml(...)` returns the HTML document that embeds the site's player and reports status back to the plugin.

`GenericWebUrl.TryParseSource` accepts any credential-free HTTP/HTTPS address (`BrowserUriPolicy`, in Browser). `ProviderUrlParsing` holds the path and query splitting the parsers share.

## Adding a provider

TODO: this list comes from reading the code, not from having added one; treat it as a starting point.

1. Add the enum member to `BrowserSourceProviderKind` and the per-provider fields to `BrowserScreenProfile` (and to `CloneAsNew`).
2. Add its entry to `BrowserProviderSettings`.
3. Add an ID parser and player page under `Providers/`, and a descriptor in `BrowserSourceDescriptors` (including `All` and `Get`).
4. Add its definition to `BrowserSourceProviderRegistry.CreateDefinitions`.
5. Add its adapter to `BrowserSourceIpcAdapters` (Ipc).
6. Teach `BrowserSourceDetector` its site, and `PlaylistOEmbed` if it has an oEmbed endpoint.

`IpcSourcePatchTests.EveryProviderHasSettingsAndAnAdapter` fails when steps 2, 4 or 5 are missed.
