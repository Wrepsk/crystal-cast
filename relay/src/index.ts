// CrystalCast sync relay: passes screen states from a host to the viewers in the same room.
//
// One Durable Object per room code. The host creates a room (POST /rooms) and gets a code plus a secret
// host key; everyone else joins with the code alone. Only the host can send states; the relay stamps each
// with its own clock and forwards it to every viewer. No video, audio or player IP addresses pass between
// players: each CrystalCast only ever talks to this relay.

import { DurableObject } from "cloudflare:workers";

export interface Env {
  ROOMS: DurableObjectNamespace<Room>;
}

/** No 0/O, 1/I/L: codes are read aloud and typed by hand. */
const CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
const CODE_LENGTH = 8;
const MAX_VIEWERS = 200;
const MAX_MESSAGE_BYTES = 32 * 1024;
/** A room nobody hosts for this long is deleted. */
const IDLE_ROOM_MS = 24 * 60 * 60 * 1000;
/** Host messages allowed per second; more are dropped. */
const HOST_MESSAGES_PER_SECOND = 20;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const parts = url.pathname.split("/").filter(Boolean);

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ ok: true });
    }

    if (request.method === "POST" && parts.length === 1 && parts[0] === "rooms") {
      return createRoom(env);
    }

    if (request.method === "GET" && parts.length === 3 && parts[0] === "rooms" && parts[2] === "socket") {
      const code = normalizeCode(parts[1]);
      if (!code) {
        return json({ error: "That room code isn't valid." }, 400);
      }

      return env.ROOMS.get(env.ROOMS.idFromName(code)).fetch(request);
    }

    return json({ error: "Not found." }, 404);
  },
};

async function createRoom(env: Env): Promise<Response> {
  for (let attempt = 0; attempt < 5; attempt++) {
    const code = randomCode();
    const hostKey = randomHex(32);
    const stub = env.ROOMS.get(env.ROOMS.idFromName(code));
    const response = await stub.fetch("https://room/init", {
      method: "POST",
      body: JSON.stringify({ code, hostKeyHash: await sha256(hostKey) }),
    });

    if (response.status === 200) {
      return json({ code, hostKey });
    }

    if (response.status !== 409) {
      return json({ error: "Could not create the room." }, 500);
    }
  }

  return json({ error: "Could not create the room." }, 503);
}

type Role = "host" | "viewer";

interface StoredRoom {
  code: string;
  hostKeyHash: string;
  createdAt: number;
  lastHostSeen: number;
}

interface LatestState {
  state: unknown;
  receivedAt: number;
}

export class Room extends DurableObject<Env> {
  /** Kept in memory between messages; reloaded from storage after the object wakes up. */
  private latest: LatestState | null | undefined = undefined;
  private hostWindowStart = 0;
  private hostWindowCount = 0;

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/init") {
      return this.init(request);
    }

    return this.openSocket(request, url);
  }

  private async init(request: Request): Promise<Response> {
    if (await this.ctx.storage.get<StoredRoom>("room")) {
      return new Response("exists", { status: 409 });
    }

    const { code, hostKeyHash } = (await request.json()) as { code: string; hostKeyHash: string };
    const now = Date.now();
    await this.ctx.storage.put<StoredRoom>("room", { code, hostKeyHash, createdAt: now, lastHostSeen: now });
    await this.ctx.storage.setAlarm(now + IDLE_ROOM_MS);
    return new Response("ok");
  }

  private async openSocket(request: Request, url: URL): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return json({ error: "Expected a WebSocket." }, 426);
    }

    const room = await this.ctx.storage.get<StoredRoom>("room");
    if (!room) {
      return json({ error: "That room doesn't exist or has closed." }, 404);
    }

    const role: Role = url.searchParams.get("role") === "host" ? "host" : "viewer";
    if (role === "host") {
      const key = url.searchParams.get("key") ?? "";
      if ((await sha256(key)) !== room.hostKeyHash) {
        return json({ error: "Wrong host key." }, 403);
      }
    } else if (this.ctx.getWebSockets("viewer").length >= MAX_VIEWERS) {
      return json({ error: "This room is full." }, 503);
    }

    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];

    if (role === "host") {
      // One host connection at a time: a reconnecting host replaces the old one.
      for (const old of this.ctx.getWebSockets("host")) {
        old.close(4000, "Replaced by a newer host connection.");
      }
    }

    this.ctx.acceptWebSocket(server, [role]);

    if (role === "host") {
      await this.touchHost(room);
      this.send(server, { type: "welcome", role, code: room.code, relayTime: Date.now(), viewers: this.viewerCount() });
      this.broadcast({ type: "host", connected: true });
    } else {
      const latest = await this.loadLatest();
      this.send(server, {
        type: "welcome",
        role,
        code: room.code,
        relayTime: Date.now(),
        hostConnected: this.ctx.getWebSockets("host").length > 0,
        state: latest?.state ?? null,
        receivedAt: latest?.receivedAt ?? 0,
      });
      this.notifyHostOfViewers();
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > MAX_MESSAGE_BYTES) {
      return;
    }

    let data: { type?: string; t?: number; state?: unknown; change?: boolean };
    try {
      data = JSON.parse(message);
    } catch {
      return;
    }

    const role = this.roleOf(ws);
    if (data.type === "ping") {
      this.send(ws, { type: "pong", t: data.t ?? 0, relayTime: Date.now() });
      return;
    }

    if (role !== "host") {
      return;
    }

    if (data.type === "state" && data.state !== undefined) {
      if (!this.allowHostMessage()) {
        return;
      }

      const latest: LatestState = { state: data.state, receivedAt: Date.now() };
      this.latest = latest;
      // Heartbeats only move the position forward, which viewers extrapolate from receivedAt anyway; storing
      // only real changes keeps storage writes within the free tier.
      if (data.change !== false) {
        await this.ctx.storage.put("latest", latest);
      }

      this.broadcast({ type: "state", state: latest.state, receivedAt: latest.receivedAt });
      return;
    }

    if (data.type === "close") {
      await this.closeRoom("The host stopped sharing.");
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    await this.onDisconnect(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.onDisconnect(ws);
  }

  async alarm(): Promise<void> {
    const room = await this.ctx.storage.get<StoredRoom>("room");
    if (!room) {
      return;
    }

    const hostConnected = this.ctx.getWebSockets("host").length > 0;
    if (!hostConnected && Date.now() - room.lastHostSeen >= IDLE_ROOM_MS) {
      await this.closeRoom("The room expired.");
      return;
    }

    await this.ctx.storage.setAlarm(Date.now() + IDLE_ROOM_MS);
  }

  private async onDisconnect(ws: WebSocket): Promise<void> {
    if (this.roleOf(ws) === "host") {
      const room = await this.ctx.storage.get<StoredRoom>("room");
      if (room) {
        await this.touchHost(room);
      }

      if (this.ctx.getWebSockets("host").every((socket) => socket === ws)) {
        this.broadcast({ type: "host", connected: false });
      }
    } else {
      this.notifyHostOfViewers(ws);
    }
  }

  private async closeRoom(reason: string): Promise<void> {
    this.broadcast({ type: "closed", reason });
    for (const socket of this.ctx.getWebSockets()) {
      socket.close(4001, reason);
    }

    this.latest = null;
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
  }

  private async touchHost(room: StoredRoom): Promise<void> {
    room.lastHostSeen = Date.now();
    await this.ctx.storage.put("room", room);
    await this.ctx.storage.setAlarm(room.lastHostSeen + IDLE_ROOM_MS);
  }

  private async loadLatest(): Promise<LatestState | null> {
    if (this.latest === undefined) {
      this.latest = (await this.ctx.storage.get<LatestState>("latest")) ?? null;
    }

    return this.latest;
  }

  private allowHostMessage(): boolean {
    const now = Date.now();
    if (now - this.hostWindowStart >= 1000) {
      this.hostWindowStart = now;
      this.hostWindowCount = 0;
    }

    this.hostWindowCount++;
    return this.hostWindowCount <= HOST_MESSAGES_PER_SECOND;
  }

  private notifyHostOfViewers(leaving?: WebSocket): void {
    const viewers = this.ctx.getWebSockets("viewer").filter((socket) => socket !== leaving).length;
    for (const host of this.ctx.getWebSockets("host")) {
      this.send(host, { type: "viewers", count: viewers });
    }
  }

  private viewerCount(): number {
    return this.ctx.getWebSockets("viewer").length;
  }

  private roleOf(ws: WebSocket): Role {
    return this.ctx.getTags(ws).includes("host") ? "host" : "viewer";
  }

  private broadcast(message: unknown): void {
    const text = JSON.stringify(message);
    for (const viewer of this.ctx.getWebSockets("viewer")) {
      try {
        viewer.send(text);
      } catch {
        // The viewer is already gone; its close handler cleans up.
      }
    }
  }

  private send(ws: WebSocket, message: unknown): void {
    try {
      ws.send(JSON.stringify(message));
    } catch {
      // Closed between the check and the send.
    }
  }
}

function normalizeCode(raw: string): string | null {
  const code = raw.toUpperCase().replace(/[^0-9A-Z]/g, "");
  if (code.length !== CODE_LENGTH) {
    return null;
  }

  for (const character of code) {
    if (!CODE_ALPHABET.includes(character)) {
      return null;
    }
  }

  return code;
}

function randomCode(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
  let code = "";
  for (const byte of bytes) {
    code += CODE_ALPHABET[byte % CODE_ALPHABET.length];
  }

  return code;
}

function randomHex(byteCount: number): string {
  return [...crypto.getRandomValues(new Uint8Array(byteCount))]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

async function sha256(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
