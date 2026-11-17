// Persistent rooms: a room that stays until its owner deletes it or everyone leaves. Every member may share
// their own screens into it, and everyone else in the room sees them where the sharer is. The owner can
// protect the room with a password, promote moderators, hand the room over, and owner and moderators can
// kick, ban, and hide a screen for everyone.
//
// Members are known by a secret key their CrystalCast makes once and keeps; the relay only stores a hash of
// it, as the member's id. The password is only needed the first time someone joins: after that the relay
// knows their id. Nothing here wakes up on its own: a room costs relay time only when someone connects or
// sends something.

import { DurableObject } from "cloudflare:workers";
import { cleanName, json, randomCode, randomHex, sha256 } from "./util";

/** Most members a room remembers, online or not. */
const MAX_MEMBERS = 200;
/** Most screens one member can share into a room at once. */
const MAX_SCREENS_PER_MEMBER = 8;
/** Longest room name, in characters. */
const MAX_ROOM_NAME_LENGTH = 40;
/** A state with a full 100-video playlist of links is well under this. */
const MAX_MESSAGE_BYTES = 32 * 1024;
/** Longest link a member may send for someone's playlist. */
const MAX_LINK_LENGTH = 2048;
/** Messages one member may send per second; more are dropped. */
const MESSAGES_PER_SECOND = 20;
/** Least time between two playlist links, and between two name changes, from the same member. */
const ADD_INTERVAL_MS = 2000;
const RENAME_INTERVAL_MS = 1000;
/** Most banned people listed to moderators. */
const MAX_LISTED_BANS = 100;

/** Close codes the plugin understands: don't reconnect after these. */
const CLOSE_REPLACED = 4000;
const CLOSE_ROOM_GONE = 4001;
const CLOSE_REMOVED = 4002;
const CLOSE_BANNED = 4003;

type Role = "owner" | "mod" | "member";

interface StoredRoom {
  code: string;
  name: string;
  salt: string;
  /** Empty when the room has no password. */
  passwordHash: string;
  createdAt: number;
}

interface StoredMember {
  name: string;
  role: Role;
  joinedAt: number;
}

interface StoredBan {
  name: string;
  at: number;
}

interface SharedScreen {
  owner: string;
  screenId: string;
  state: unknown;
  receivedAt: number;
}

interface Attachment {
  id: string;
  lastAddAt?: number;
  lastRenameAt?: number;
}

type Incoming = {
  type?: string;
  t?: number;
  name?: unknown;
  screenId?: unknown;
  owner?: unknown;
  id?: unknown;
  url?: unknown;
  password?: unknown;
  state?: unknown;
};

/** POST /persistent with { name, password, key, username }: makes a room owned by that key. */
export async function createPersistentRoom(request: Request, rooms: DurableObjectNamespace<PersistentRoom>): Promise<Response> {
  const text = await request.text();
  if (text.length > 4096) {
    return json({ error: "Request too large." }, 413);
  }

  let body: { name?: unknown; password?: unknown; key?: unknown; username?: unknown };
  try {
    body = JSON.parse(text);
  } catch {
    return json({ error: "Expected JSON." }, 400);
  }

  if (!isMemberKey(body.key)) {
    return json({ error: "Missing member key." }, 400);
  }

  for (let attempt = 0; attempt < 5; attempt++) {
    const code = randomCode();
    const response = await rooms.get(rooms.idFromName(code)).fetch("https://room/init", {
      method: "POST",
      body: JSON.stringify({
        code,
        name: cleanRoomName(body.name),
        password: typeof body.password === "string" ? body.password : "",
        ownerId: await memberId(body.key),
        ownerName: cleanName(typeof body.username === "string" ? body.username : null),
      }),
    });

    if (response.status === 200) {
      return json({ code });
    }

    if (response.status !== 409) {
      return json({ error: "Could not create the room." }, 500);
    }
  }

  return json({ error: "Could not create the room." }, 503);
}

export class PersistentRoom extends DurableObject {
  // Kept in memory between messages; reloaded from storage after the object wakes up.
  private room: StoredRoom | null | undefined;
  private members: Record<string, StoredMember> | undefined;
  private bans: Record<string, StoredBan> | undefined;
  private screens: Map<string, SharedScreen> | undefined;
  private hidden: Set<string> | undefined;
  private readonly rate = new Map<string, { start: number; count: number }>();

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/init") {
      return this.init(request);
    }

    return this.openSocket(request, url);
  }

  private async init(request: Request): Promise<Response> {
    if (await this.loadRoom()) {
      return new Response("exists", { status: 409 });
    }

    const { code, name, password, ownerId, ownerName } = (await request.json()) as {
      code: string;
      name: string;
      password: string;
      ownerId: string;
      ownerName: string;
    };
    const now = Date.now();
    const salt = randomHex(16);
    this.room = { code, name, salt, passwordHash: password ? await sha256(salt + password) : "", createdAt: now };
    this.members = { [ownerId]: { name: ownerName, role: "owner", joinedAt: now } };
    await this.ctx.storage.put({ room: this.room, members: this.members });
    return new Response("ok");
  }

  private async openSocket(request: Request, url: URL): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return json({ error: "Expected a WebSocket." }, 426);
    }

    const room = await this.loadRoom();
    if (!room) {
      return refuse(404, "gone", "That room doesn't exist or was deleted.");
    }

    const key = url.searchParams.get("key");
    if (!isMemberKey(key)) {
      return refuse(400, "key", "Missing member key.");
    }

    const id = await memberId(key);
    const bans = await this.loadBans();
    if (bans[id]) {
      return refuse(403, "banned", "You're banned from this room.");
    }

    const members = await this.loadMembers();
    const name = cleanName(url.searchParams.get("name"));
    const now = Date.now();
    if (!members[id]) {
      const password = url.searchParams.get("password") ?? "";
      if (room.passwordHash && (await sha256(room.salt + password)) !== room.passwordHash) {
        return refuse(403, "password", password ? "Wrong password." : "This room needs a password.");
      }

      if (Object.keys(members).length >= MAX_MEMBERS) {
        return refuse(503, "full", "This room is full.");
      }

      members[id] = { name, role: "member", joinedAt: now };
      await this.saveMembers();
    } else if (members[id].name !== name) {
      members[id].name = name;
      await this.saveMembers();
    }

    // One connection per member: a reconnect replaces the old one.
    for (const old of this.ctx.getWebSockets(id)) {
      old.close(CLOSE_REPLACED, "Replaced by a newer connection.");
    }

    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    this.ctx.acceptWebSocket(server, [id]);
    server.serializeAttachment({ id } satisfies Attachment);

    const screens = await this.loadScreens();
    const hidden = await this.loadHidden();
    this.send(server, {
      type: "welcome",
      code: room.code,
      name: room.name,
      passwordSet: room.passwordHash.length > 0,
      you: id,
      role: members[id].role,
      relayTime: now,
      // This member's screens the relay still has from before, so its CrystalCast can drop ones it no
      // longer shares.
      yours: [...screens.values()].filter((screen) => screen.owner === id).map((screen) => screen.screenId),
      hiddenScreens: [...hidden].map(splitScreenKey),
    });

    // Then everyone else's screens, one message each (a single message could get too large).
    const online = this.onlineIds();
    for (const screen of screens.values()) {
      if (screen.owner !== id && online.has(screen.owner) && !hidden.has(screenKey(screen.owner, screen.screenId))) {
        this.send(server, { type: "state", owner: screen.owner, screenId: screen.screenId, state: screen.state, receivedAt: screen.receivedAt });
      }
    }

    await this.sendMembers();
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > MAX_MESSAGE_BYTES) {
      return;
    }

    let data: Incoming;
    try {
      data = JSON.parse(message);
    } catch {
      return;
    }

    const attachment = this.attachmentOf(ws);
    const id = attachment.id;
    const members = await this.loadMembers();
    const me = members[id];
    if (!me) {
      ws.close(CLOSE_REMOVED, "You're no longer in this room.");
      return;
    }

    if (data.type === "ping") {
      this.send(ws, { type: "pong", t: data.t ?? 0, relayTime: Date.now() });
      return;
    }

    if (!this.allowMessage(id)) {
      return;
    }

    switch (data.type) {
      case "name":
        await this.rename(ws, attachment, me, data.name);
        return;
      case "state":
        await this.shareState(ws, id, data.screenId, data.state);
        return;
      case "unshare":
        if (isScreenId(data.screenId)) {
          await this.removeScreen(id, data.screenId);
        }
        return;
      case "add":
        await this.forwardAdd(ws, attachment, data.owner, data.screenId, data.url);
        return;
      case "kick":
      case "ban":
        await this.moderate(ws, me, data.type, data.id);
        return;
      case "unban":
        if (rank(me.role) >= rank("mod") && typeof data.id === "string") {
          const bans = await this.loadBans();
          if (bans[data.id]) {
            delete bans[data.id];
            await this.ctx.storage.put("bans", bans);
            await this.sendMembers();
          }
        }
        return;
      case "promote":
      case "demote":
      case "transfer":
        await this.changeRole(ws, id, me, data.type, data.id);
        return;
      case "hide":
      case "unhide":
        await this.setHidden(ws, me, data.owner, data.screenId, data.type === "hide");
        return;
      case "password":
        if (me.role === "owner" && typeof data.password === "string" && data.password.length <= 128) {
          const room = (await this.loadRoom())!;
          room.salt = randomHex(16);
          room.passwordHash = data.password ? await sha256(room.salt + data.password) : "";
          await this.ctx.storage.put("room", room);
          this.broadcastRoom(room);
        }
        return;
      case "roomName":
        if (me.role === "owner") {
          const room = (await this.loadRoom())!;
          room.name = cleanRoomName(data.name);
          await this.ctx.storage.put("room", room);
          this.broadcastRoom(room);
        }
        return;
      case "delete":
        if (me.role === "owner") {
          await this.deleteRoom("The owner deleted the room.");
        }
        return;
      case "leave":
        await this.leave(id, me);
        return;
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    await this.sendMembers(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.sendMembers(ws);
  }

  private async rename(ws: WebSocket, attachment: Attachment, me: StoredMember, raw: unknown): Promise<void> {
    if (typeof raw !== "string") {
      return;
    }

    const now = Date.now();
    const name = cleanName(raw);
    if (now - (attachment.lastRenameAt ?? 0) < RENAME_INTERVAL_MS || name === me.name) {
      return;
    }

    ws.serializeAttachment({ ...attachment, lastRenameAt: now } satisfies Attachment);
    me.name = name;
    await this.saveMembers();
    await this.sendMembers();
  }

  private async shareState(ws: WebSocket, id: string, screenId: unknown, state: unknown): Promise<void> {
    if (!isScreenId(screenId) || state === undefined || state === null) {
      return;
    }

    const screens = await this.loadScreens();
    const key = screenKey(id, screenId);
    if (!screens.has(key) && [...screens.values()].filter((screen) => screen.owner === id).length >= MAX_SCREENS_PER_MEMBER) {
      this.send(ws, { type: "error", reason: `You can share up to ${MAX_SCREENS_PER_MEMBER} screens in a room.` });
      return;
    }

    const shared: SharedScreen = { owner: id, screenId, state, receivedAt: Date.now() };
    screens.set(key, shared);
    await this.ctx.storage.put(`screen:${key}`, shared);
    if (!(await this.loadHidden()).has(key)) {
      this.broadcast({ type: "state", owner: id, screenId, state, receivedAt: shared.receivedAt }, ws);
    }
  }

  private async removeScreen(owner: string, screenId: string): Promise<void> {
    const screens = await this.loadScreens();
    const key = screenKey(owner, screenId);
    if (!screens.delete(key)) {
      return;
    }

    await this.ctx.storage.delete(`screen:${key}`);
    const hidden = await this.loadHidden();
    if (hidden.delete(key)) {
      await this.ctx.storage.put("hidden", [...hidden]);
    }

    this.broadcast({ type: "unshare", owner, screenId });
  }

  private async forwardAdd(ws: WebSocket, attachment: Attachment, owner: unknown, screenId: unknown, url: unknown): Promise<void> {
    if (typeof owner !== "string" || !isScreenId(screenId) || typeof url !== "string" || url.length === 0 || url.length > MAX_LINK_LENGTH) {
      return;
    }

    const now = Date.now();
    if (now - (attachment.lastAddAt ?? 0) < ADD_INTERVAL_MS || (await this.loadHidden()).has(screenKey(owner, screenId))) {
      return;
    }

    ws.serializeAttachment({ ...attachment, lastAddAt: now } satisfies Attachment);
    for (const socket of this.ctx.getWebSockets(owner)) {
      this.send(socket, { type: "add", screenId, url });
    }
  }

  private async moderate(ws: WebSocket, me: StoredMember, action: "kick" | "ban", targetId: unknown): Promise<void> {
    const members = await this.loadMembers();
    if (typeof targetId !== "string" || !members[targetId] || !canModerate(me.role, members[targetId].role)) {
      this.send(ws, { type: "error", reason: "You can't do that to this person." });
      return;
    }

    if (action === "ban") {
      const bans = await this.loadBans();
      bans[targetId] = { name: members[targetId].name, at: Date.now() };
      await this.ctx.storage.put("bans", bans);
      await this.removeMember(targetId, CLOSE_BANNED, "You were banned from this room.");
    } else {
      await this.removeMember(targetId, CLOSE_REMOVED, "You were removed from this room.");
    }
  }

  private async changeRole(ws: WebSocket, id: string, me: StoredMember, action: "promote" | "demote" | "transfer", targetId: unknown): Promise<void> {
    const members = await this.loadMembers();
    const target = typeof targetId === "string" && targetId !== id ? members[targetId] : undefined;
    if (me.role !== "owner" || !target) {
      this.send(ws, { type: "error", reason: "Only the owner can change roles." });
      return;
    }

    if (action === "promote" && target.role === "member") {
      target.role = "mod";
    } else if (action === "demote" && target.role === "mod") {
      target.role = "member";
    } else if (action === "transfer") {
      target.role = "owner";
      me.role = "mod";
    } else {
      return;
    }

    await this.saveMembers();
    await this.sendMembers();
  }

  private async setHidden(ws: WebSocket, me: StoredMember, owner: unknown, screenId: unknown, hide: boolean): Promise<void> {
    const members = await this.loadMembers();
    if (typeof owner !== "string" || !isScreenId(screenId) || !members[owner] || !canModerate(me.role, members[owner].role)) {
      this.send(ws, { type: "error", reason: "You can't hide this screen." });
      return;
    }

    const hidden = await this.loadHidden();
    const key = screenKey(owner, screenId);
    if (hide === hidden.has(key)) {
      return;
    }

    if (hide) {
      hidden.add(key);
    } else {
      hidden.delete(key);
    }

    await this.ctx.storage.put("hidden", [...hidden]);
    this.broadcast({ type: "hidden", owner, screenId, hidden: hide });
    const screen = (await this.loadScreens()).get(key);
    if (!hide && screen) {
      for (const socket of this.ctx.getWebSockets()) {
        if (this.attachmentOf(socket).id !== owner) {
          this.send(socket, { type: "state", owner, screenId, state: screen.state, receivedAt: screen.receivedAt });
        }
      }
    }
  }

  private async leave(id: string, me: StoredMember): Promise<void> {
    if (me.role === "owner") {
      // The room goes on with the longest-standing moderator, or else the longest-standing member.
      const members = await this.loadMembers();
      const successor = Object.entries(members)
        .filter(([other]) => other !== id)
        .sort(([, a], [, b]) => rank(b.role) - rank(a.role) || a.joinedAt - b.joinedAt)[0];
      if (successor) {
        successor[1].role = "owner";
      }
    }

    await this.removeMember(id, CLOSE_REMOVED, "You left the room.");
  }

  /** Takes someone out of the room: their screens go, their connection closes, and they're forgotten. */
  private async removeMember(id: string, closeCode: number, reason: string): Promise<void> {
    const members = await this.loadMembers();
    delete members[id];
    for (const screen of [...(await this.loadScreens()).values()]) {
      if (screen.owner === id) {
        await this.removeScreen(id, screen.screenId);
      }
    }

    for (const socket of this.ctx.getWebSockets(id)) {
      this.send(socket, { type: "closed", reason });
      socket.close(closeCode, reason);
    }

    if (Object.keys(members).length === 0) {
      await this.deleteRoom("Everyone left the room.");
      return;
    }

    await this.saveMembers();
    await this.sendMembers(...this.ctx.getWebSockets(id));
  }

  private async deleteRoom(reason: string): Promise<void> {
    for (const socket of this.ctx.getWebSockets()) {
      this.send(socket, { type: "closed", reason });
      socket.close(CLOSE_ROOM_GONE, reason);
    }

    this.room = null;
    this.members = {};
    this.bans = {};
    this.screens = new Map();
    this.hidden = new Set();
    await this.ctx.storage.deleteAll();
  }

  private broadcastRoom(room: StoredRoom): void {
    this.broadcast({ type: "room", name: room.name, passwordSet: room.passwordHash.length > 0 });
  }

  /**
   * Sends everyone online who is in the room, online or not: the owner first, then moderators, then members,
   * each group in the order they joined. Moderators and the owner also get the ban list.
   */
  private async sendMembers(...leaving: WebSocket[]): Promise<void> {
    const members = await this.loadMembers();
    const allBans = await this.loadBans();
    const sockets = this.ctx.getWebSockets().filter((socket) => !leaving.includes(socket));
    const online = new Set(sockets.map((socket) => this.attachmentOf(socket).id));
    const sorted = Object.entries(members).sort(([, a], [, b]) => rank(b.role) - rank(a.role) || a.joinedAt - b.joinedAt);
    const bans = Object.entries(allBans)
      .sort(([, a], [, b]) => b.at - a.at)
      .slice(0, MAX_LISTED_BANS)
      .map(([id, ban]) => ({ id, name: ban.name }));

    for (const socket of sockets) {
      const you = this.attachmentOf(socket).id;
      const list = sorted.map(([id, member]) => ({ id, name: member.name, role: member.role, online: online.has(id), you: id === you }));
      const moderator = rank(members[you]?.role ?? "member") >= rank("mod");
      this.send(socket, { type: "members", members: list, bans: moderator ? bans : [] });
    }
  }

  private onlineIds(): Set<string> {
    return new Set(this.ctx.getWebSockets().map((socket) => this.attachmentOf(socket).id));
  }

  private allowMessage(id: string): boolean {
    const now = Date.now();
    const window = this.rate.get(id);
    if (!window || now - window.start >= 1000) {
      this.rate.set(id, { start: now, count: 1 });
      return true;
    }

    window.count++;
    return window.count <= MESSAGES_PER_SECOND;
  }

  private async loadRoom(): Promise<StoredRoom | null> {
    if (this.room === undefined) {
      this.room = (await this.ctx.storage.get<StoredRoom>("room")) ?? null;
    }

    return this.room;
  }

  private async loadMembers(): Promise<Record<string, StoredMember>> {
    this.members ??= (await this.ctx.storage.get<Record<string, StoredMember>>("members")) ?? {};
    return this.members;
  }

  private async saveMembers(): Promise<void> {
    await this.ctx.storage.put("members", this.members ?? {});
  }

  private async loadBans(): Promise<Record<string, StoredBan>> {
    this.bans ??= (await this.ctx.storage.get<Record<string, StoredBan>>("bans")) ?? {};
    return this.bans;
  }

  private async loadScreens(): Promise<Map<string, SharedScreen>> {
    if (!this.screens) {
      const stored = await this.ctx.storage.list<SharedScreen>({ prefix: "screen:" });
      this.screens = new Map([...stored.values()].map((screen) => [screenKey(screen.owner, screen.screenId), screen]));
    }

    return this.screens;
  }

  private async loadHidden(): Promise<Set<string>> {
    this.hidden ??= new Set((await this.ctx.storage.get<string[]>("hidden")) ?? []);
    return this.hidden;
  }

  private attachmentOf(ws: WebSocket): Attachment {
    return (ws.deserializeAttachment() ?? { id: "" }) as Attachment;
  }

  private broadcast(message: unknown, except?: WebSocket): void {
    const text = JSON.stringify(message);
    for (const socket of this.ctx.getWebSockets()) {
      if (socket === except) {
        continue;
      }

      try {
        socket.send(text);
      } catch {
        // Already gone; its close handler cleans up.
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

function rank(role: Role): number {
  return role === "owner" ? 3 : role === "mod" ? 2 : 1;
}

/** Owners act on anyone else; moderators on plain members only. */
function canModerate(actor: Role, target: Role): boolean {
  return rank(actor) >= rank("mod") && rank(actor) > rank(target);
}

function isMemberKey(key: unknown): key is string {
  return typeof key === "string" && /^[0-9a-f]{32,128}$/.test(key);
}

function isScreenId(id: unknown): id is string {
  return typeof id === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(id);
}

/** A member's public id: half a hash of their secret key, never the key itself. */
async function memberId(key: string): Promise<string> {
  return (await sha256(`crystalcast-member\n${key}`)).slice(0, 32);
}

function screenKey(owner: string, screenId: string): string {
  return `${owner}:${screenId}`;
}

function splitScreenKey(key: string): { owner: string; screenId: string } {
  const at = key.indexOf(":");
  return { owner: key.slice(0, at), screenId: key.slice(at + 1) };
}

function cleanRoomName(raw: unknown): string {
  const spaced = (typeof raw === "string" ? raw : "").replace(/\s+/g, " ").replace(/\p{C}/gu, "").replace(/ {2,}/g, " ");
  const cleaned = [...spaced.trim()].slice(0, MAX_ROOM_NAME_LENGTH).join("").trim();
  return cleaned.length > 0 ? cleaned : "Persistent room";
}

/** A refused connection. The plugin can't read the body of a failed WebSocket upgrade, so the reason also goes in a header. */
function refuse(status: number, reason: string, message: string): Response {
  return new Response(JSON.stringify({ error: message }), {
    status,
    headers: { "Content-Type": "application/json", "X-CrystalCast-Refusal": reason },
  });
}
