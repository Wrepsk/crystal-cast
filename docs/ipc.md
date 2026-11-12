# IPC (implementation)

Folder `CrystalCast/Ipc/`, namespace `CrystalCast.Ipc`. This page is about how the IPC is built. What other plugins see is in [ipc-api.md](ipc-api.md).

## Responsibility

- Register CrystalCast's call-gate channels while the user has IPC switched on.
- Answer calls on the game's main thread, whatever thread they came from.
- Turn JSON requests into changes of `BrowserScreenProfile` and commands to the running player.
- Describe screens as state envelopes, and announce changes exactly once.
- Hold two small pieces of session state: the remote states other plugins handed in, and the IDs of plugin screens the user deleted and keeps hidden.
- Gate web pages requested by plugins behind the user's approval.

## Files

| File | Role |
| --- | --- |
| `IpcChannels.cs` | The channel names, as public constants |
| `ScreenStateIpc.cs` | Entry point: registration, dispatch, publishing, snapshot, capabilities |
| `IpcMainThreadDispatcher.cs` | Runs handlers on the main thread with a 5 second wait |
| `IpcPayloadLimit.cs` | Size checks before any JSON is parsed |
| `IpcJsonService.cs` | The serializer options and the common answer builders |
| `ScreenIpcDtos.cs`, `ScreenStateEnvelope.cs` | The public request, answer and event classes |
| `ScreenIpcMutationService.cs` | `Create`, `Update`, `UpdateSource`, `SetSourceLock`, `Remove`, `GetSourceState` |
| `ScreenPatchApplier.cs` | Applies a request to a copy of a profile |
| `BrowserSourceIpcAdapters.cs` | Per-provider translation between profile settings and IPC shapes |
| `ScreenStateBuilder.cs` | Builds envelopes and source-state answers |
| `ScreenChangePublisher.cs` | Fingerprints screens and sends `Screen.Changed` |
| `ScreenPlaybackWatcher.cs` | Notices changes made inside the player |
| `ScreenPlaylistIpcService.cs` | The `Screen.Playlist.*` calls |
| `RemoteScreenStateStore.cs`, `RemoteScreenStateAcceptance.cs`, `ScreenStateValidator.cs` | The store behind `ApplyState` |
| `HiddenIpcScreens.cs` | Screen IDs the user keeps hidden this session |
| `GenericWebIpcApprovalService.cs` | The user's decisions about plugin-requested web pages |

## `ScreenStateIpc`

Created by `Plugin` with the configuration, the `WorldScreenManager`, the services record and the playlist advancer.

**Registration.** The constructor builds a table of `IpcFunction(Register, Unregister)` pairs, one per function channel, with `AddFunction(channel, handler)`. `Register()` runs the table and sends `Ready`; `Unregister()` sends `Disposed` and then unregisters each function. `UpdateRegistration()` picks one from `Configuration.IpcEnabled`. Adding a channel is one constant in `IpcChannels` and one `AddFunction` line; `IpcSourcePatchTests.ChannelNamesAreStable` then needs the new name.

**Dispatch.** Every handler except `ApiVersion` goes through `IpcMainThreadDispatcher.Invoke(handler, onTimeout)`. JSON-taking handlers first pass `IpcPayloadLimit.TryCheck` (`Mutate`, `MutatePlaylist`). Each family has its own error shape so a timeout or an oversize payload answers in the type the caller expects (`MutationError`, `PlaylistError`, `SourceStateError`, `ApplyStateError`).

**Publishing.** `PublishLocalState()` is what the UI and `Plugin` call after any change:

1. Normalize the configuration and refresh registration.
2. For each enabled, allowed screen whose placement resolves: increment `LocalSequence` and build its envelope.
3. Request a save.
4. Hand each envelope to `ScreenChangePublisher.MaybeSendScreenChanged`, which sends an event only if the screen's fingerprint changed (or the change was forced).
5. Send `Unavailable` for every previously announced screen that is no longer in the list.

The private overload takes a screen ID and forced change kinds; IPC mutations and playlist changes use it so their event always goes out with the right kinds, even if the fingerprint is unchanged or the screen cannot be placed at the moment. `PublishPlaylistAdvance` also restarts the player when the next entry has the same link.

**Player watching.** On `IFramework.Update`, at most every 250 ms while registered, `WatchPlayback` builds each screen's envelope and asks `ScreenPlaybackWatcher.Observe` what changed since last time. Changes are sent with `ScreenChangePublisher.SendObservedChanges`, which only reports screens that were already announced.

**Other members.** `SetEnabled` (the settings switch: announces everything unavailable, deletes IPC screens, clears stores, updates registration), `OnUserDeletedScreen`, `HiddenScreenCount`, `ShowHiddenScreens`, `GetCapabilitiesJson`, `GetSnapshotJson`, `ApplyStateJson`, `RemoveJson`.

`ownerSessionId` is a GUID made in the constructor. It goes into every envelope and event so that a consumer, and the remote store, can tell this running copy of CrystalCast from any other.

## Mutations

`ScreenIpcMutationService.Handle<TRequest>(json, verb, subject, handler)` deserializes, answers "Request body is empty." for a null body, and turns any exception into a logged error answer. The handlers:

- **Create**: refuses hidden IDs, checks limits, duplicate IDs and the provider, makes a default IPC screen, applies the request with `ScreenPatchApplier.TryApplyScreenMutation`, adds it, saves, and publishes with the create change kinds.
- **Update / UpdateSource**: find the screen (Update may fall through to Create with `createIfMissing`), apply the request to a **clone**, and `Commit` it: replace the profile in the list, save, send the patch's player commands (`BrowserSourceIpcAdapters.ApplyRuntimeControls`), and publish with the change kinds computed from the request (`ScreenChangePublisher.GetMutationChangeKinds` / `GetSourceUpdateChangeKinds`).
- **SetSourceLock**: changes the lock in place, saves, publishes `SourceLock`.
- **Remove**: only for `CreatedByIpc` screens; sends `Unavailable` with `RemovedByIpc`.
- **RemovedByUser**: called by the UI after the user deleted a plugin's screen; optionally hides the ID and sends `Unavailable` with `RemovedByUser` or `HiddenByUser`.

Applying to a clone is what makes a failed request change nothing: `ScreenPatchApplier` returns the original when any step fails.

`ScreenPatchApplier.TrySelectProvider` switches the provider only when the request names one. `ApplyPlacementPatch` copies the present fields and normalizes the placement.

## Provider adapters

`BrowserSourceIpcAdapter` (one per provider, listed in `BrowserSourceIpcAdapters`) holds what differs between providers: the descriptor, the messages and titles for an invalid source, how to build the source identity string, and which extra fields a source-state answer gets. Everything else is shared and reads the profile through `BrowserProviderSettings`:

- `ApplyPatch(screen, patch, out error)`: validates the link with the provider's parser, then sets each present field. A Generic Web link set this way marks `GenericWebUrlFromIpc`.
- `BuildPlaybackState` / `BuildSourceState`: from the profile and the player's telemetry; without telemetry, a placeholder.
- `BuildSourceDetails`: the `source` object of `GetSourceState`.
- `AddFingerprintParts`: the settings whose change counts as a `Source` change.

## Change detection

`ScreenChangePublisher` keeps, per screen ID, a fingerprint of six strings: placement, source, playback, visual, lock, playlist. Comparing the previous and next fingerprint gives the change kinds. It also remembers which screen IDs it has announced, so it can send `Unavailable` once when one disappears. `Clear()` forgets everything (IPC switched off, unload).

The playback fingerprint includes `positionMs`, so a publish while a video plays reports `Playback`. Events between publishes come from the watcher, which ignores normal progress: `ScreenPlaybackWatcher` compares state, duration, rate, loop, play-next and source text, and treats a position as a seek only when it is more than 2 seconds (`SeekToleranceMs`) from where playback should have reached.

## Remote state store

`RemoteScreenStateStore.Apply(candidate, localOwnerSessionId, out error)`:

1. prune entries older than five minutes;
2. `ScreenStateValidator.TryValidate` (shape, lengths, finite and bounded numbers, supported provider, HTTP(S) link);
3. `RemoteScreenStateAcceptance.Evaluate` (own session, duplicate sequence, stale sequence, or accept);
4. refuse a new key beyond 256 entries;
5. store under `(OwnerSessionId, ScreenId)`.

It returns the public `ScreenIpcApplyStateResult`. Nothing in the plugin reads the store except `GetSnapshot` and `Remove`; the renderer never draws from it.

## Hidden screens and web approval

`HiddenIpcScreens` is an in-memory set of screen IDs (at most 512). `Create` checks it; **Settings > IPC > Show them again** clears it; nothing is saved.

`GenericWebIpcApprovalService` decides whether a Generic Web page on a screen that `RequiresGenericWebIpcApproval` may load and navigate. `EvaluateInitial(screen)` answers `Allowed`, `Pending` or `Denied` for the configured address and queues a request for the dialog when pending; `IsNavigationAllowed(screen, candidate)` is the callback the browser asks on each navigation; `Approve` and `Reject` record the user's decision per screen and origin, optionally for the whole session; `GetScreenRevision` changes when a decision changes so the renderer re-evaluates. Decisions are kept in memory only.

TODO: document the exact rules for redirects and for when a session-wide trust or block applies; they are in `EvaluateLocked`.

## Tests

`CrystalCast.Tests/Ipc/` covers patch application, change kinds, the publisher, the remote store, the validator, acceptance rules, hidden screens, payload limits, the approval service, JSON compatibility of `ScreenSourceKind`, and the channel name list. The dispatcher and registration need Dalamud and are not unit-tested.
