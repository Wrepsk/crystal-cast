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
- `GET /rooms/{code}/socket?role=viewer` or `?role=host&key={hostKey}` opens the room's WebSocket.
- The host sends `{ type: "state", change, state }` (`change: false` for heartbeats) and `{ type: "close" }`.
- Viewers get `welcome` (with the latest state), `state` (stamped with the relay's `receivedAt`), `host`
  (`connected` true or false) and `closed`. Anyone can send `{ type: "ping", t }` and gets a `pong` with the
  relay's clock.
- A viewer may send `{ type: "add", url }`; the relay passes it to the host (at most one every 2 seconds per
  viewer), and the host's CrystalCast decides whether to add it to the playlist.
- Limits: 200 viewers per room, 32 KiB per message, 20 host messages per second.
