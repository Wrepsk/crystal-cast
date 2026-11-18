# CrystalCast sync relay

A small Cloudflare Worker that passes screen states between CrystalCast players in the same room. The host
shares a screen and gets a room code; everyone who joins with the code gets a copy of the screen that follows
the host's. Only the host can send states. No video, audio or player IP addresses pass between players: each
CrystalCast only talks to this relay.

One Durable Object per room holds its connections and the latest state. Heartbeats are forwarded but not
stored, and rooms nobody hosts for 24 hours are deleted, which keeps it well inside the Workers Free plan.

## Deploy

You need a Cloudflare account (the free plan is enough) and Node.js.

```bash
cd relay
npm install
npx wrangler login
npx wrangler deploy
```

`wrangler deploy` prints the address, for example `https://crystalcast-relay.<your-subdomain>.workers.dev`.
CrystalCast uses `https://crystalcast-relay.crystalcast.workers.dev` by default. To use your own deployment,
put its address in **Settings > Rooms > Relay address**.

## Run locally

```bash
npm run dev      # relay on http://127.0.0.1:8787
npm run smoke    # drives a host and two viewers against it
```

Set `RELAY=https://…` before `npm run smoke` to test a deployed relay. The plugin's end-to-end test runs when
`CRYSTALCAST_TEST_RELAY` points at a relay:

```bash
CRYSTALCAST_TEST_RELAY=http://127.0.0.1:8787 dotnet test CrystalCast.Tests
```

## Protocol

- `POST /rooms` answers `{ code, hostKey }`. Keep the host key secret; it proves you are the host.
- `GET /rooms/{code}/socket?role=viewer` or `?role=host&key={hostKey}` opens the room's WebSocket. Add
  `&name={username}` to choose the name others see (cleaned to at most 24 characters; empty is `Guest`).
- Everyone, host included, gets `{ type: "members", members: [{ name, host, you }] }` whenever someone joins,
  leaves or renames: the host first, then viewers in the order they joined, with `you` set on the recipient's
  own entry. Anyone can send `{ type: "name", name }` to rename (at most once a second).
- The host sends `{ type: "state", change, state }` and `{ type: "close" }`. CrystalCast 0.9.9.3 and later send a
  state only when something changes; older versions also send heartbeats with `change: false`, which the relay
  forwards but doesn't store.
- Viewers get `welcome` (with the latest state), `state` (stamped with the relay's `receivedAt`), `host`
  (`connected` true or false) and `closed`. Anyone can send `{ type: "ping", t }` and gets a `pong` with the
  relay's clock.
- A viewer may send `{ type: "add", url }`; the relay passes it to the host (at most one every 2 seconds per
  viewer), and the host's CrystalCast decides whether to add it to the playlist.
- Limits: 200 viewers per room, 32 KiB per message, 20 host messages per second.

### Persistent rooms

A persistent room stays until its owner deletes it or every member leaves; nothing in it wakes the relay on
its own. Members are known by a secret key their CrystalCast makes once; the relay stores
`sha256("crystalcast-member
" + key)` (first 32 hex digits) as their public id.

- `POST /persistent` with `{ name, password, key, username }` answers `{ code }`. `password` is what the
  plugin sends instead of the password itself, `sha256("crystalcast-room-password
" + password)` in hex, or
  empty for none; the relay hashes it again with a per-room salt.
- `GET /persistent/{code}/socket?key={key}&name={username}[&password={hash}]` connects. The password is only
  checked the first time someone joins. A refused connection answers an HTTP error with an
  `X-CrystalCast-Refusal` header: `gone`, `banned`, `password`, `full` or `key`.
- `welcome` has `{ code, name, passwordSet, you, role, relayTime, yours, hiddenScreens }`, followed by a `state`
  message for every screen shared by someone online. `yours` lists the recipient's own screens the relay still
  has, so its CrystalCast can unshare stale ones.
- Members send `{ type: "state", screenId, state }` and `{ type: "unshare", screenId }` for their own screens
  (at most 8 each); everyone else gets `{ type: "state", owner, screenId, state, receivedAt }` and
  `{ type: "unshare", owner, screenId }`. `{ type: "add", owner, screenId, url }` asks a screen's owner to add
  a link to its playlist.
- `members` has `{ members: [{ id, name, role, online, you }], bans }`; `bans` is only filled for moderators
  and the owner. Roles are `owner`, `mod` and `member`.
- Moderation: `kick`, `ban` and `unban` with `{ id }` (owner on anyone, moderators on members),
  `hide`/`unhide` with `{ owner, screenId }` (same rule; everyone gets `{ type: "hidden", owner, screenId,
  hidden }`), and owner only: `promote`/`demote`/`transfer` with `{ id }`, `{ type: "password", password }`
  (empty removes it), `{ type: "roomName", name }` and `{ type: "delete" }`. Anyone can `{ type: "leave" }`; an
  owner who leaves hands the room to the longest-standing moderator, or else member.
- Before closing a connection for good the relay sends `{ type: "closed", reason }`, then closes with 4001
  (room deleted), 4002 (removed or left) or 4003 (banned).
- Limits: 200 members per room, 8 shared screens per member, 20 messages per member per second.

Test with `npm run smoke:persistent` against `npm run dev`.
