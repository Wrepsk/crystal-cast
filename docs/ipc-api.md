# CrystalCast IPC API

For developers of other Dalamud plugins. API version **11**.

CrystalCast lets other plugins read its screens, create and control screens of their own, manage playlists, and follow changes. Everything goes through Dalamud's call gates (`IDalamudPluginInterface.GetIpcSubscriber`). Only descriptions travel over IPC: where a screen is, what link it plays, and where playback is. Pixels and audio are never exchanged.

- [Basics](#basics)
- [Availability and versioning](#availability-and-versioning)
- [Channel overview](#channel-overview)
- [Shared types](#shared-types)
- [Channels](#channels)
- [Ownership, locks and what the user controls](#ownership-locks-and-what-the-user-controls)
- [How to build an integration](#how-to-build-an-integration)

## Basics

**Payloads are JSON strings.** Requests and answers that carry more than a number are JSON text passed as `string`.

- Property names are **camelCase** and are matched **case-sensitively** on input (`screenId`, not `ScreenId`).
- Enums are written as their **names** (`"Playing"`, `"YouTube"`). Numbers are accepted on input.
- Unknown properties are ignored. A property you leave out of a request keeps the screen's current value, unless stated otherwise.
- Answers always include every property; strings are `""` when empty, never missing.

**Threads.** You may call from any thread. Every handler runs on the game's main thread. A call made from another thread is handed over and waits up to **5 seconds**; if the main thread does not take it in time (for example during a loading screen) the answer is an error with a message starting `CrystalCast's game thread did not take the call`. Calling from the framework thread never waits.

**Limits.**

| Limit | Value |
| --- | --- |
| Any JSON argument | 64 KiB (65,536 UTF-8 bytes) |
| A screen ID passed as a bare string | 256 characters |
| Screens created through IPC | 56 |
| Screens in total (user + IPC) | 64 |
| Screens running a browser at once | 8 (the first 8 enabled screens in list order) |
| Remote states kept by `ApplyState` | 256 |
| Playlist entries per screen | 100 |

Read the live values from [`GetCapabilities`](#crystalcastgetcapabilities) instead of hard-coding them.

**Errors.** No function throws for bad input. Each returns an answer with `success: false` and a human-readable `error`. What *can* throw is Dalamud itself: invoking a function that is not registered throws `Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError`. Always wrap invocations in `try`/`catch`.

## Availability and versioning

The functions are registered only while the user has **Settings > IPC** switched on (it is on by default) and CrystalCast is loaded.

- `CrystalCast.Ready` (event) fires right after the functions are registered: when CrystalCast loads with IPC on, and when the user switches IPC on.
- `CrystalCast.Disposed` (event) fires right before they are unregistered: when CrystalCast unloads, and when the user switches IPC off.

Because `Ready` fires during CrystalCast's own start-up, a plugin that loads *after* CrystalCast will not see it. Do both: subscribe to `Ready`, and also try `ApiVersion` once when your plugin starts.

When the user switches IPC off, CrystalCast sends an `Unavailable` change for every screen it had announced, deletes all screens created through IPC, forgets the stored remote states, and then sends `Disposed`.

**`CrystalCast.ApiVersion`** returns a single integer. It is raised whenever the interface changes. There is no minor version and no promise that a higher version still accepts older requests, so check for the exact versions you tested against, or at least a minimum:

```csharp
if (apiVersion < 11) { /* too old: tell the user to update CrystalCast */ }
```

Changes can also be detected by feature: `GetCapabilities().features` lists optional behaviours by name (`createIfMissing`, `removalReason`).

Version history:

| Version | Change |
| --- | --- |
| 11 | One `source` object replaces the per-provider objects in requests and in `GetSourceState`. `ApplyState` returns JSON; `ApplyStateDetailed` removed. `Remove` returns JSON. `LocalStateChanged` removed. `GetSnapshot` returns `localScreens` and `remoteScreens`. |
| 10 | Calls may come from any thread; payload and screen ID limits. |

TODO: versions before 10 are not documented here; see `CHANGELOG.md`.

### Connecting

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

public sealed class CrystalCastClient : IDisposable
{
    private const int MinimumApiVersion = 11;

    private readonly ICallGateSubscriber<int> apiVersion;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<object> disposed;

    public bool Available { get; private set; }

    public CrystalCastClient(IDalamudPluginInterface pluginInterface)
    {
        apiVersion = pluginInterface.GetIpcSubscriber<int>("CrystalCast.ApiVersion");
        ready = pluginInterface.GetIpcSubscriber<object>("CrystalCast.Ready");
        disposed = pluginInterface.GetIpcSubscriber<object>("CrystalCast.Disposed");

        ready.Subscribe(OnReady);
        disposed.Subscribe(OnDisposed);
        OnReady(); // CrystalCast may already be running.
    }

    private void OnReady()
    {
        try
        {
            Available = apiVersion.InvokeFunc() >= MinimumApiVersion;
        }
        catch
        {
            Available = false; // Not installed, not loaded, or IPC switched off.
        }
    }

    private void OnDisposed() => Available = false;

    public void Dispose()
    {
        ready.Unsubscribe(OnReady);
        disposed.Unsubscribe(OnDisposed);
    }
}
```

The examples below assume a `pluginInterface` and this serializer setup:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

static readonly JsonSerializerOptions Json = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
};
```

`WhenWritingNull` leaves out the properties you did not set, which is how a request says "keep the current value".

The example types (`Snapshot`, `ChangeEvent`, `MutationAnswer`, `SourceStateAnswer`, `PlaylistAnswer`) stand for your own classes with the properties of the JSON shown; enum values are read as strings in them.

## Channel overview

| Channel | Kind | Signature | Purpose |
| --- | --- | --- | --- |
| `CrystalCast.ApiVersion` | function | `() => int` | Interface version |
| `CrystalCast.GetCapabilities` | function | `() => string` | Versions, providers, limits, browser situation |
| `CrystalCast.Ready` | event | `()` | Functions were registered |
| `CrystalCast.Disposed` | event | `()` | Functions are about to be unregistered |
| `CrystalCast.Screen.GetSnapshot` | function | `() => string` | All local screen states and stored remote states |
| `CrystalCast.Screen.Changed` | event | `(string eventJson)` | A screen changed or went away |
| `CrystalCast.Screen.Create` | function | `(string requestJson) => string` | Create a screen |
| `CrystalCast.Screen.Update` | function | `(string requestJson) => string` | Change a screen (optionally create it) |
| `CrystalCast.Screen.UpdateSource` | function | `(string requestJson) => string` | Change only what a screen plays |
| `CrystalCast.Screen.SetSourceLock` | function | `(string requestJson) => string` | Lock or unlock the user's source controls |
| `CrystalCast.Screen.GetSourceState` | function | `(string screenId) => string` | What one screen plays and where playback is |
| `CrystalCast.Screen.Remove` | function | `(string screenId) => string` | Remove a screen created through IPC |
| `CrystalCast.Screen.ApplyState` | function | `(string stateJson) => string` | Store a remote screen state for snapshot readers |
| `CrystalCast.Screen.Playlist.Get` | function | `(string screenId) => string` | Read a playlist |
| `CrystalCast.Screen.Playlist.Add` | function | `(string requestJson) => string` | Add a video |
| `CrystalCast.Screen.Playlist.Remove` | function | `(string requestJson) => string` | Remove an entry |
| `CrystalCast.Screen.Playlist.Clear` | function | `(string requestJson) => string` | Remove all entries |
| `CrystalCast.Screen.Playlist.Skip` | function | `(string requestJson) => string` | Play the next entry now |
| `CrystalCast.Screen.Playlist.Move` | function | `(string requestJson) => string` | Reorder an entry |
| `CrystalCast.Screen.Playlist.SetOptions` | function | `(string requestJson) => string` | Shuffle and loop |

The names are also available as constants in `CrystalCast.Ipc.IpcChannels`, and the JSON shapes as classes in `CrystalCast.Ipc` (`ScreenIpcDtos.cs`, `ScreenStateEnvelope.cs`). You do not need to reference CrystalCast's assembly; copying the classes you use is the usual approach.

## Shared types

### Enums

| Enum | Values |
| --- | --- |
| provider | `YouTube`, `Twitch`, `Dailymotion`, `Vimeo`, `GenericWeb` |
| placement mode | `World` (fixed in the world), `FollowPlayer`, `FollowCamera` |
| playback state | `Stopped`, `Playing`, `Paused` |
| change kind | `Created`, `Placement`, `Source`, `Playback`, `Visual`, `SourceLock`, `Unavailable`, `Playlist` |
| removal reason | `None`, `RemovedByIpc`, `RemovedByUser`, `HiddenByUser` |
| apply-state result | `Applied`, `IgnoredSelf`, `IgnoredDuplicate`, `IgnoredStale`, `RejectedInvalid`, `RejectedCapacity` |
| source kind | `Browser` (the only kind) |

### Screen state envelope

One screen as it is right now. Used in snapshots, in change events, and as the input of `ApplyState`.

| Property | Type | Meaning |
| --- | --- | --- |
| `schemaVersion` | int | `1` |
| `screenId` | string | The screen's ID |
| `ownerSessionId` | string | A random ID CrystalCast picks each time it loads; identifies *this running copy* of CrystalCast |
| `territoryId` | ushort | The territory the player is in |
| `position` | `{x, y, z}` floats | World position of the screen's centre, already resolved from the placement mode |
| `rotation` | `{x, y, z, w}` floats | World rotation as a quaternion |
| `sizeMeters` | `{x, y}` floats | Width and height of the panel |
| `source` | object | `kind` (`"Browser"`), `provider`, `identity` (a stable key such as `youtube:<id>`, `twitch:channel:<name>`, `vimeo:video:<id>`), `title`, `hash` (always empty), `url` (a full link to what is playing), `videoId` |
| `playback` | object | `state`, `positionMs`, `durationMs`, `rate`, `loop`, `playlistAutoplayNext`, `hostTimestampUnixMs` (when `positionMs` was measured) |
| `visual` | object | `occludedAlpha`, `occlusionTolerance`, `screenCurveAmountMeters`, `distanceFadeEnabled`, `fadeStartMeters`, `fadeStopMeters` |
| `timestampUnixMs` | long | When the envelope was built |
| `sequence` | long | Increases each time CrystalCast publishes this screen |

To get the position *now* from an envelope: if `playback.state` is `Playing`, add `(now - hostTimestampUnixMs) * rate` to `positionMs`, and cap at `durationMs` when that is above zero.

Before the player has reported anything, `playback` is a placeholder: `Playing` (or `Paused` if the screen is paused), `positionMs` 0, `durationMs` 0.

### Source patch

The `source` object of `Create`, `Update` and `UpdateSource`. Every property is optional.

| Property | Type | Meaning |
| --- | --- | --- |
| `url` | string | A link or ID the provider accepts. `""` unloads the source. Invalid for the provider: the whole call fails and nothing changes |
| `playbackPaused` | bool | Pause (`true`) or play (`false`) |
| `positionMs` | long | Seek to this position. Ignored when `restart` is `true` |
| `restart` | bool | Reload the player once. Not a setting; default `false` |
| `autoplay` | bool | Start playing when a source loads |
| `loop` | bool | Loop the video. Ignored on Twitch |
| `playlistAutoplayNext` | bool | Continue to the next video of a YouTube playlist link. Ignored elsewhere |
| `playbackRate` | float | 0.25 to 2. Ignored on Twitch and Dailymotion |
| `browserWidth`, `browserHeight` | int | Page size in pixels; clamped to 320-3840 by 180-2160 |
| `captureFps` | float | 1 to 120; used when `captureFpsManual` is `true` |
| `captureFpsManual` | bool | `false` follows the video's detected frame rate |
| `audioEnabled` | bool | |
| `volume` | float | 0 to 1 |
| `spatialAudioEnabled` | bool | Fade volume with distance |
| `spatialAudioFullVolumeRadiusMeters`, `spatialAudioSilentRadiusMeters` | float | |

The patch applies to the screen's provider. To change provider, set `provider` on the request; without it the screen keeps its current provider, and a link of another site is refused as invalid.

Out-of-range numbers are clamped, not refused.

### Placement patch

The `placement` object of `Create` and `Update`. Every property is optional: `mode`, `positionX`, `positionY`, `positionZ`, `yawRadians`, `pitchRadians`, `rollRadians`, `widthMeters`, `heightMeters`, `screenCurveAmountMeters`, `occludedAlpha`, `occlusionTolerance`, `distanceFadeEnabled`, `fadeStartMeters`, `fadeStopMeters`.

Position and rotation are relative to the mode: world coordinates for `World`, offsets from the player or camera otherwise. Width and height are at least 0.1; `occludedAlpha` is clamped to 0-1; the curve is limited by the width.

### Mutation answer

Returned by `Create`, `Update`, `UpdateSource`, `SetSourceLock` and `Remove`.

| Property | Type | Meaning |
| --- | --- | --- |
| `success` | bool | |
| `error` | string | Why it failed; `""` on success |
| `hiddenByUser` | bool | The user deleted this screen and keeps it hidden this session. Stop sending it |
| `created` | bool | A screen was created |
| `updated` | bool | An existing screen was changed |
| `screenId` | string | The screen the call was about |
| `screen` | object or null | Summary after the change: `screenId`, `name`, `enabled`, `createdByIpc`, `ownerId`, `sourceControlsLocked`, `sourceControlsOwnerId`, `provider`, `placement` (all placement properties, as stored) |

## Channels

### `CrystalCast.ApiVersion`

Function, `() => int`. Returns `11`. Does not touch the game thread.

### `CrystalCast.GetCapabilities`

Function, `() => string`.

```json
{
  "schemaVersion": 1,
  "apiVersion": 11,
  "pluginVersion": "0.9.1.4",
  "enabled": true,
  "providers": ["YouTube", "Twitch", "Dailymotion", "Vimeo", "GenericWeb"],
  "features": ["createIfMissing", "removalReason"],
  "limits": {
    "maxUserScreens": 8, "maxIpcScreens": 56, "maxScreens": 64, "maxActiveScreens": 8,
    "maxRemoteScreens": 256, "maxPayloadBytes": 65536, "ipcScreensInUse": 0, "maxPlaylistEntries": 100
  },
  "browser": {
    "isWine": false, "backend": "Auto", "captureMode": "WindowGraphicsCapture",
    "nativeLinuxBrowserForProviders": false,
    "webView2Available": true, "webView2Version": "…", "webView2Error": ""
  }
}
```

- `enabled`: whether the user has CrystalCast's screens switched on. IPC calls work either way, but nothing is drawn and snapshots are empty while it is off.
- `browser.webView2Available` `false` with `webView2Error` set means screens cannot play (except provider pages under Wine with the native browser, `nativeLinuxBrowserForProviders`).
- On a main-thread timeout the answer is a mutation answer with `success: false`.

```csharp
var caps = JsonSerializer.Deserialize<JsonElement>(
    pluginInterface.GetIpcSubscriber<string>("CrystalCast.GetCapabilities").InvokeFunc());
var canPlay = caps.GetProperty("browser").GetProperty("webView2Available").GetBoolean();
```

### `CrystalCast.Ready` and `CrystalCast.Disposed`

Events without arguments. See [Availability and versioning](#availability-and-versioning). A failing `Disposed` subscriber is logged by CrystalCast and does not stop the unload.

### `CrystalCast.Screen.GetSnapshot`

Function, `() => string`.

```json
{ "schemaVersion": 1, "apiVersion": 11, "localScreens": [ /* envelopes */ ], "remoteScreens": [ /* envelopes */ ] }
```

- `localScreens`: this player's screens that are enabled, within the screen limits, and can be placed right now, in screen-list order. This includes screens created through IPC (yours and other plugins'); `GetSourceState` or a `Changed` event tells you who created one. It is empty while CrystalCast is disabled, and a screen is missing while its placement cannot be resolved (before the local player exists, for example).
- `remoteScreens`: the states stored with `ApplyState`, ordered by `ownerSessionId` then `screenId`.

Reading a snapshot does not change sequences.

```csharp
var json = pluginInterface.GetIpcSubscriber<string>("CrystalCast.Screen.GetSnapshot").InvokeFunc();
var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Json)!;
foreach (var screen in snapshot.LocalScreens)
    Log.Info($"{screen.ScreenId}: {screen.Source.Url} at {screen.Playback.PositionMs} ms");
```

### `CrystalCast.Screen.Changed`

Event, `(string eventJson)`.

```json
{
  "schemaVersion": 1,
  "screenId": "…",
  "ownerSessionId": "…",
  "createdByIpc": false,
  "ownerId": "",
  "sourceControlsLocked": false,
  "sourceControlsOwnerId": "",
  "changes": ["Source", "Playback"],
  "removalReason": "None",
  "state": { /* envelope, or null */ },
  "timestampUnixMs": 1760000000000
}
```

When it fires:

- After the user changes something in the UI, after a successful IPC mutation, after a playlist change, and on a territory change that paused the user's screens. Each screen whose published state differs from what was last sent gets one event listing what differs.
- The first time a screen is announced, normally with `Created`, `Placement`, `Source`, `Playback`.
- Four times a second CrystalCast checks for changes made *inside the player*: pause or play in the browser's own controls, a video ending, the next item of a site playlist, a title arriving, a duration arriving, or a seek (a position more than 2 seconds from where playback should be). Ordinary playback progress does not fire events.
- When a screen that was announced is no longer published (disabled, deleted, CrystalCast disabled, IPC switched off): `changes` is `["Unavailable"]`, `state` is `null`, and `removalReason` says why:
  - `RemovedByIpc`: a plugin called `Remove`.
  - `RemovedByUser`: the user deleted a plugin's screen; creating it again brings it back.
  - `HiddenByUser`: the user deleted it and keeps it hidden; creating it again is refused until the game restarts or the user un-hides it.
  - `None`: anything else.

Events for your own mutations reach you too. `createdByIpc` and `ownerId` let you skip them.

`Playlist` in `changes` means entries, order, the current entry or the options changed; read them with `Playlist.Get`.

Handlers run on the game's main thread. Keep them short and never block.

```csharp
var changed = pluginInterface.GetIpcSubscriber<string, object>("CrystalCast.Screen.Changed");
changed.Subscribe(OnScreenChanged);

void OnScreenChanged(string eventJson)
{
    var evt = JsonSerializer.Deserialize<ChangeEvent>(eventJson, Json);
    if (evt == null || evt.CreatedByIpc) return;           // not one of the user's own screens
    if (evt.Changes.Contains("Unavailable")) { /* forget evt.ScreenId */ return; }
    /* evt.State is the new envelope */
}
// later: changed.Unsubscribe(OnScreenChanged);
```

### `CrystalCast.Screen.Create`

Function, `(string requestJson) => string` (mutation answer).

Request:

| Property | Type | Meaning |
| --- | --- | --- |
| `screenId` | string | Your ID for the screen. Leave out to get a generated one. Use a prefix unique to your plugin |
| `name` | string | Shown in the user's screen list. Default `IPC screen N` |
| `ownerId` | string | Who is creating it; see [ownership](#ownership-locks-and-what-the-user-controls) |
| `enabled` | bool | Default `true` |
| `activate` | bool | Also select it in the user's UI. Default `false` |
| `provider` | provider | Default `YouTube` |
| `sourceControlsLocked` | bool | Lock the user's source controls |
| `sourceControlsOwnerId` | string | Name shown as the lock holder. Defaults to `ownerId` when locking |
| `placement` | placement patch | Default: `World` mode at x 0, y 1.6, z 3, 3 by 1.6875 metres |
| `source` | source patch | |
| `playlistAllowUserAdds` | bool | Let the user add videos to this screen's playlist. Default `false` |

A created screen has `createdByIpc: true`, spatial audio on, and audio on.

Fails when: the body is empty or not valid JSON; the user keeps this `screenId` hidden (`hiddenByUser: true`); the IPC or total screen limit is reached; a screen with this `screenId` exists (`error` contains `already exists`); the provider is not supported; the `source.url` is not valid for the provider.

```csharp
var create = pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.Create");
var request = new
{
    screenId = "myplugin:lobby",
    name = "My Plugin - Lobby",
    ownerId = "MyPlugin",
    provider = "YouTube",
    sourceControlsLocked = true,
    placement = new { mode = "World", positionX = 0f, positionY = 2f, positionZ = 5f, widthMeters = 4f, heightMeters = 2.25f },
    source = new { url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ", volume = 0.3f },
};
var answer = JsonSerializer.Deserialize<MutationAnswer>(create.InvokeFunc(JsonSerializer.Serialize(request, Json)), Json)!;
if (!answer.Success) Log.Warning(answer.Error);
```

### `CrystalCast.Screen.Update`

Function, `(string requestJson) => string` (mutation answer).

Same request as `Create`, plus:

| Property | Type | Meaning |
| --- | --- | --- |
| `screenId` | string | Required: the screen to change |
| `createIfMissing` | bool | Create the screen with this `screenId` when it does not exist, exactly as `Create` would. Default `false` |

Only the properties you send change. `name` and `ownerId` change only when not empty. Works on any screen, including the user's own; `playlistAllowUserAdds` is ignored on the user's screens.

The change is applied to a copy and swapped in only if all of it is valid, so a failed call changes nothing. After storing, `restart`, `positionMs` and `playbackPaused` are sent to the running player.

Fails when: the body is empty or invalid; the screen does not exist and `createIfMissing` is not set (`error` contains `was not found`); any `Create` failure when it creates; unsupported provider; invalid link.

With `createIfMissing: true` you can send the same call every time and never call `Create`:

```csharp
var update = pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.Update");
var answer = JsonSerializer.Deserialize<MutationAnswer>(update.InvokeFunc(JsonSerializer.Serialize(new
{
    screenId = "myplugin:lobby",
    createIfMissing = true,
    ownerId = "MyPlugin",
    placement = new { positionX = 1.5f },
}, Json)), Json)!;
if (answer.HiddenByUser) { /* the user does not want this screen; stop until told otherwise */ }
```

### `CrystalCast.Screen.UpdateSource`

Function, `(string requestJson) => string` (mutation answer).

Changes only what a screen plays. Use it for play, pause, seek and loading another link.

| Property | Type | Meaning |
| --- | --- | --- |
| `screenId` | string | Required |
| `activate` | bool | Also select the screen in the user's UI |
| `provider` | provider | Switch provider. Leave out to keep the current one |
| `source` | source patch | |

Fails when: the body is empty or invalid; the screen is not found; unsupported provider; invalid link.

```csharp
var updateSource = pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.UpdateSource");

// Pause.
updateSource.InvokeFunc("""{"screenId":"myplugin:lobby","source":{"playbackPaused":true}}""");

// Load a Twitch channel and play.
updateSource.InvokeFunc(JsonSerializer.Serialize(new
{
    screenId = "myplugin:lobby",
    provider = "Twitch",
    source = new { url = "https://www.twitch.tv/somechannel", playbackPaused = false },
}, Json));

// Seek to 1:30.
updateSource.InvokeFunc("""{"screenId":"myplugin:lobby","source":{"positionMs":90000}}""");
```

A seek or play sent in the same call that loads a new link may arrive before the player is ready. If exact position matters, send the position again shortly after loading, or compare with `GetSourceState` and correct.

### `CrystalCast.Screen.SetSourceLock`

Function, `(string requestJson) => string` (mutation answer).

| Property | Type | Meaning |
| --- | --- | --- |
| `screenId` | string | Required |
| `ownerId` | string | Name shown as the lock holder. When empty, the existing lock owner or the screen's `ownerId` is used |
| `locked` | bool | `true` locks, `false` unlocks and clears the lock holder |

While locked, the user cannot change the link, play/pause, seek, loop, autoplay, rate or placement of that screen in CrystalCast's UI, and sees who holds the lock. Audio, spatial audio, resolution and capture FPS stay adjustable. The lock does not restrict IPC calls: any plugin can still update the screen.

Fails when the body is empty or invalid, or the screen is not found.

```csharp
pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.SetSourceLock")
    .InvokeFunc("""{"screenId":"myplugin:lobby","ownerId":"MyPlugin","locked":true}""");
```

### `CrystalCast.Screen.GetSourceState`

Function, `(string screenId) => string`. An empty `screenId` means the screen selected in the user's UI.

```json
{
  "success": true, "error": "",
  "screenId": "…", "name": "…", "enabled": true, "createdByIpc": true, "ownerId": "MyPlugin",
  "sourceControlsLocked": true, "sourceControlsOwnerId": "MyPlugin",
  "sourceKind": "Browser", "provider": "YouTube",
  "sourceName": "…", "sourceStatus": "…",
  "source": {
    "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
    "canonicalUrl": "…", "videoId": "dQw4w9WgXcQ",
    "channelName": "", "playlistId": "", "hash": "",
    "title": "…", "state": "Playing", "positionMs": 12345, "durationMs": 212000, "rate": 1.0,
    "hostTimestampUnixMs": 1760000000000
  }
}
```

- `source.url` is the link as configured; `canonicalUrl` is a full link to what is playing now (the current video of a playlist, for example) and is empty when the configured link is not valid.
- `channelName` is set for a Twitch channel, `playlistId` for a Dailymotion playlist, `hash` for a Vimeo private link.
- `sourceName` and `sourceStatus` are diagnostic text for people; do not parse them.
- Unlike the snapshot, this works for screens that are disabled or cannot be placed.

Fails (`success: false`, `source: null`) when the screen is not found, the ID is longer than 256 characters, or the main thread timed out.

```csharp
var json = pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.GetSourceState").InvokeFunc("myplugin:lobby");
var state = JsonSerializer.Deserialize<SourceStateAnswer>(json, Json)!;
if (state.Success && state.Source!.State == "Playing") { /* … */ }
```

### `CrystalCast.Screen.Remove`

Function, `(string screenId) => string` (mutation answer with `success`, `error`, `screenId`; `screen` is null).

Removes a screen that was created through IPC, by any plugin. If no such screen exists, it removes the remote states stored under this screen ID with `ApplyState` instead. The user's own screens cannot be removed.

A removed screen sends `Changed` with `Unavailable` and `removalReason: "RemovedByIpc"`. Removing never hides a screen: it can be created again at once.

Fails when: the ID is empty or longer than 256 characters; the screen exists but was not created through IPC; nothing with this ID was found.

```csharp
var answer = JsonSerializer.Deserialize<MutationAnswer>(
    pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.Remove").InvokeFunc("myplugin:lobby"), Json)!;
```

### `CrystalCast.Screen.ApplyState`

Function, `(string stateJson) => string`.

Hands CrystalCast a [screen state envelope](#screen-state-envelope) that describes a screen somewhere else. CrystalCast **stores** it and returns it in `GetSnapshot().remoteScreens`. It does **not** draw it. To show another player's screen, create a screen with `Create`/`Update` instead. This channel is a shared, validated store for plugins that want to exchange states through CrystalCast.

Answer:

```json
{ "success": true, "result": "Applied", "error": "", "screenId": "…", "ownerSessionId": "…" }
```

| `result` | `success` | Meaning |
| --- | --- | --- |
| `Applied` | true | Stored |
| `IgnoredSelf` | true | `ownerSessionId` is this CrystalCast's own |
| `IgnoredDuplicate` | true | Same `sequence` as the stored state |
| `IgnoredStale` | true | Lower `sequence` than the stored state |
| `RejectedInvalid` | false | Empty, larger than 64 KiB, not valid JSON, or failed validation; `error` says which |
| `RejectedCapacity` | false | 256 states are already stored and this would be a new one |

States are keyed by `ownerSessionId` + `screenId` and expire five minutes after they were last accepted.

Validation: `schemaVersion` 1; `screenId` and `ownerSessionId` required, at most 128 characters, no surrounding whitespace; `sequence` not negative; timestamps not negative and at most five minutes in the future; finite coordinates within ±100,000; size between 0.1 and 100 metres; a usable quaternion; `source.kind` `Browser` with a supported `provider`; text at most 512 characters and `url` at most 4,096 and HTTP or HTTPS; playback rate 0.1 to 4; visual values in range.

```csharp
var answer = JsonSerializer.Deserialize<JsonElement>(
    pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.ApplyState").InvokeFunc(envelopeJson));
var stored = answer.GetProperty("result").GetString() == "Applied";
```

### `CrystalCast.Screen.Playlist.*`

Each screen has a playlist: videos that play after the current one ends.

**Answer** of every playlist call:

```json
{
  "success": true, "error": "", "screenId": "…", "entryId": "",
  "playlist": {
    "shuffle": false, "loop": false,
    "allowIpcAdds": false, "allowUserAdds": true,
    "currentEntryId": "…", "maxEntries": 100,
    "entries": [
      { "entryId": "…", "provider": "YouTube", "url": "…", "title": "…", "author": "…",
        "thumbnailUrl": "…", "durationSeconds": 212, "addedByIpc": true, "addedBy": "MyPlugin",
        "played": false, "current": true }
    ]
  }
}
```

- `entryId`: for `Add` the new entry, for `Skip` the entry now playing, otherwise `""`.
- `playlist` is null when the call failed. Call `Get` for the current list.
- `allowIpcAdds`: the user's "Let other plugins add videos" switch. Read-only for plugins; always `false` on screens created through IPC.
- `allowUserAdds`: whether the user may add videos in the UI. Always `true` on the user's screens.
- `title`: the one given to `Add`, else the site's, else what the link points at. Titles, authors, thumbnails and durations arrive shortly after adding and are announced with a `Playlist` change. `durationSeconds` is 0 when unknown or live.

**Request** of every call except `Get` (each call reads the properties it needs):

| Property | Used by | Meaning |
| --- | --- | --- |
| `screenId` | all | Required |
| `ownerId` | all | Who is calling; checked as described below |
| `url` | `Add` | A full link. Bare IDs are not accepted |
| `title` | `Add` | Optional display name |
| `entryId` | `Remove`, `Move` | The entry |
| `index` | `Move` | New zero-based position |
| `shuffle`, `loop` | `SetOptions` | Leave out to keep |

**Who may call what.** "Screen owner" means: the `ownerId` equals the screen's source-lock holder while the lock is on, or the screen was created through IPC with that `ownerId`.

| Call | Allowed for |
| --- | --- |
| `Get` | anyone |
| `Add` | the screen owner; or anyone, on a user's screen whose "Let other plugins add videos" switch is on |
| `Remove` | the screen owner; or the `ownerId` that added that entry |
| `Clear`, `Skip`, `Move`, `SetOptions` | the screen owner |

`ownerId` is whatever the caller says it is. These rules keep cooperating plugins out of each other's way; they are not a security boundary. The user's switch is.

Other failures: screen not found; entry not in the playlist; the playlist is full; the link is not valid; `Move` without `index`; `Skip` with nothing left to play (`Nothing is left to play.`).

A web page (`GenericWeb`) added through IPC still needs the user's approval before it opens.

```csharp
var add = pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.Playlist.Add");
var answer = JsonSerializer.Deserialize<PlaylistAnswer>(add.InvokeFunc(JsonSerializer.Serialize(new
{
    screenId = "myplugin:lobby",
    ownerId = "MyPlugin",
    url = "https://vimeo.com/123456789",
    title = "Intermission",
}, Json)), Json)!;
var newEntryId = answer.EntryId;

var playlist = JsonSerializer.Deserialize<PlaylistAnswer>(
    pluginInterface.GetIpcSubscriber<string, string>("CrystalCast.Screen.Playlist.Get").InvokeFunc("myplugin:lobby"), Json)!.Playlist;
```

## Ownership, locks and what the user controls

- **`ownerId`** is a free-form name for your integration (or, for a sync plugin, the remote person a screen belongs to). It is stored on screens you create, shown in events and answers, and compared in playlist checks. It is coordination data, not authentication.
- **`createdByIpc`** marks screens a plugin created. They count against the IPC limit (56), are deleted when the user switches IPC off, and their Playlist tab is read-only for the user unless you set `playlistAllowUserAdds`.
- **Source lock** greys out the user's source and placement controls and shows `sourceControlsOwnerId`. Use it on screens whose content you drive, so the user is not confused by controls that would be overwritten.
- **The user can always delete a screen.** Deleting a plugin's screen asks whether to keep it hidden for the session (the default). A hidden `screenId` is refused by `Create` and by `Update` with `createIfMissing` (`hiddenByUser: true`) until the game restarts or the user chooses **Settings > IPC > Hidden screens > Show them again**. Respect it: stop sending that screen until a later call succeeds. The list holds at most 512 IDs.
- **Web pages need approval.** A `GenericWeb` address set through IPC, on any screen, does not load until the user allows it in a dialog that shows the address and your `ownerId`. Until then the screen shows a status instead of the page. The video sites need no approval.
- **Zone changes.** CrystalCast pauses the *user's own* world-fixed screens when the territory changes. Screens created through IPC are left alone; decide yourself what to do with them (a snapshot's `territoryId` tells you where a state was taken).

## How to build an integration

General advice, drawn from how a sync plugin uses this API. Adapt it to your design.

### A: Control your own screens

For a plugin that shows something on a screen (a venue schedule, a watch-party plugin):

1. Connect as shown in [Connecting](#connecting); read `GetCapabilities` and bail out with a clear message if `webView2Available` is false.
2. Pick a `screenId` prefix that is yours (`myplugin:`) and a stable `ownerId`.
3. Send `Update` with `createIfMissing: true` for placement and settings, and `UpdateSource` for play, pause, seek and new links. Send only what changed.
4. Lock the source controls for screens you drive.
5. On `hiddenByUser`, stop. On other errors, log `error` and retry later, not in a tight loop.
6. `Remove` your screens when your plugin unloads or the feature is switched off. After `Disposed`, forget what you created: CrystalCast deletes IPC screens when IPC is switched off.

### B: Mirror screens between players

For a plugin that shows other players the screens the local player placed:

**Sending side**

1. Subscribe to `Screen.Changed`. On an event that is not for a screen you created (`createdByIpc` false and not your prefix), schedule a publish. Debounce it (tens of milliseconds): one user action can fire several events.
2. To publish, call `GetSnapshot` and take `localScreens`, leaving out your own mirrored screens. Send those envelopes over your transport. Cap how many you send and how large the payload is.
3. Also publish on a slow timer (every few seconds) while something is playing, so receivers can correct drift.
4. Do not flood: remember what you last sent and skip a publish when nothing but the natural progress of `positionMs` changed.

**Receiving side**

1. For each envelope, build a local `screenId` that cannot collide: your prefix + the sender's identity + the sender's `screenId`.
2. `Update` with `createIfMissing: true`: `placement` from the envelope (`mode: "World"`, position, yaw/pitch/roll from the quaternion, size, visual values), `enabled: true`, `ownerId` = the sender, `sourceControlsLocked: true`.
3. `UpdateSource` with `provider` and `source.url` from `source.provider`/`source.url`, `playbackPaused` from `playback.state`, `loop`, `playbackRate`, and `positionMs` = the envelope's position advanced to now (see [the envelope](#screen-state-envelope)). Clocks differ between machines; if you can, measure the offset in your own protocol.
4. Choose receiver-side quality yourself (`browserWidth`, `browserHeight`, `captureFps`, `volume`): the sender's settings are not in the envelope, and a receiver showing many screens may want lower values.
5. Avoid needless reloads. Sending the same `url` again does not reload the player, but a `positionMs` is a seek every time. Keep what you last applied per screen and send a position only when the source changed, the play state changed, or the position jumped by more than a tolerance (around a second). For periodic correction, read `GetSourceState` and seek only if the local player has drifted.
6. When a sender's screen disappears from their list, or the sender leaves, `Remove` the mirrored screen. Remove all of yours on unload.
7. Check the territory: only mirror screens whose `territoryId` matches where the receiver is, if your plugin can span zones.

**Avoiding loops.** Your mirrored screens appear in the receiver's own snapshot and fire `Changed` events. Filter by `createdByIpc` and by your prefix on the sending side, or two clients will mirror each other's mirrors.

**Trust.** Treat envelopes from the network as untrusted: check sizes, clamp numbers, and remember that a `GenericWeb` link from a peer will ask the receiving user for approval. Consider not mirroring `GenericWeb` at all, or making it opt-in.

### C: Add videos to the user's screens

For a request-queue style plugin: call `Playlist.Add` with the user's active screen ID (from `GetSourceState("")`). It only works after the user turned on "Let other plugins add videos" for that screen; if it fails with the message about not accepting videos from other plugins, tell the user where the switch is (the screen's Playlist tab). You can remove what you added; you cannot reorder or skip unless you hold the screen's source lock.

### General

- Invoke from the framework thread when you can; it avoids the cross-thread wait entirely.
- Never invoke inside a `Changed` handler in a way that can recurse without bound: a mutation fires another event.
- Deserialize defensively. New properties may be added to answers without a version bump being meaningful to you; unknown enum names should not crash your plugin.
- Keep a single wrapper class around the subscribers, so a version change touches one file.
