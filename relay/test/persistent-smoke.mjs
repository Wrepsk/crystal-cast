// Drives a persistent room against a running relay (default: wrangler dev on port 8787): create with a
// password, join, share screens, moderate, transfer, leave and delete.
// Usage: npm run smoke:persistent   or   RELAY=https://your-relay.workers.dev npm run smoke:persistent
import { createHash, randomBytes } from "node:crypto";
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";

const relay = (process.env.RELAY ?? "http://127.0.0.1:8787").replace(/\/$/, "");
const wsBase = relay.replace(/^http/, "ws");

const fail = (message) => { console.error("FAIL:", message); process.exit(1); };
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const key = () => randomBytes(32).toString("hex");
const hashPassword = (password) => createHash("sha256").update(`crystalcast-room-password\n${password}`).digest("hex");

function socketUrl(code, memberKey, name, password) {
  const query = new URLSearchParams({ key: memberKey, name });
  if (password) query.set("password", hashPassword(password));
  return `${wsBase}/persistent/${code}/socket?${query}`;
}

function open(url) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url);
    const messages = [];
    const client = { socket, messages };
    socket.onmessage = (event) => messages.push(JSON.parse(event.data));
    socket.onopen = () => resolve(client);
    socket.onerror = () => reject(new Error(`could not connect: ${url}`));
  });
}

// What the relay says when it refuses a connection: the X-CrystalCast-Refusal header, as the plugin reads it.
function refusal(url) {
  const target = new URL(url.replace(/^ws/, "http"));
  const request = target.protocol === "https:" ? httpsRequest : httpRequest;
  return new Promise((resolve, reject) => {
    const req = request(target, {
      headers: { Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": randomBytes(16).toString("base64") },
    });
    req.on("response", (response) => {
      response.resume();
      resolve(response.headers["x-crystalcast-refusal"] ?? `status ${response.statusCode}`);
    });
    req.on("upgrade", (_response, socket) => {
      socket.destroy();
      resolve("accepted");
    });
    req.on("error", reject);
    req.end();
  });
}

async function waitFor(messages, predicate, what) {
  for (let i = 0; i < 50; i++) {
    const found = messages.find(predicate);
    if (found) return found;
    await sleep(100);
  }
  fail(`timed out waiting for ${what}`);
}

// The relay says why before it closes; the plugin goes by that message (the close code is a second signal
// that local wrangler doesn't always deliver to Node's client).
async function waitClosed(client, reason, what) {
  const closed = await waitFor(client.messages, (m) => m.type === "closed", what);
  if (!closed.reason.includes(reason)) fail(`${what}: closed with "${closed.reason}"`);
}

const send = (client, message) => client.socket.send(JSON.stringify(message));
const latestMembers = (client) => [...client.messages].reverse().find((m) => m.type === "members");

const ownerKey = key();
const created = await (await fetch(`${relay}/persistent`, {
  method: "POST",
  body: JSON.stringify({ name: "  Movie\u0000 Night  ", password: hashPassword("hunter2"), key: ownerKey, username: "Owner" }),
})).json();
if (!/^[2-9A-HJKMNP-Z]{8}$/.test(created.code ?? "")) fail(`bad create answer ${JSON.stringify(created)}`);
const code = created.code;
console.log("persistent room", code);

// The owner is a member already: no password needed.
const owner = await open(socketUrl(code, ownerKey, "Owner"));
const ownerWelcome = await waitFor(owner.messages, (m) => m.type === "welcome", "owner welcome");
if (ownerWelcome.role !== "owner" || ownerWelcome.name !== "Movie Night" || ownerWelcome.passwordSet !== true) fail(`bad owner welcome ${JSON.stringify(ownerWelcome)}`);

// Newcomers need the password, once.
const aliceKey = key();
if ((await refusal(socketUrl(code, aliceKey, "Alice"))) !== "password") fail("joined without a password");
if ((await refusal(socketUrl(code, aliceKey, "Alice", "wrong"))) !== "password") fail("joined with a wrong password");
let alice = await open(socketUrl(code, aliceKey, "Alice", "hunter2"));
const aliceWelcome = await waitFor(alice.messages, (m) => m.type === "welcome", "alice welcome");
if (aliceWelcome.role !== "member") fail(`bad alice welcome ${JSON.stringify(aliceWelcome)}`);
const members = await waitFor(owner.messages, (m) => m.type === "members" && m.members.length === 2, "two members");
if (members.members[0].role !== "owner" || !members.members[0].you || members.members[1].name !== "Alice" || !members.members[1].online) fail(`bad members ${JSON.stringify(members)}`);
const aliceId = members.members[1].id;

// Both share; each sees the other's screen, never their own echoed back.
send(owner, { type: "state", screenId: "stage", state: { url: "https://youtu.be/a", positionMs: 1000 } });
const ownerScreen = await waitFor(alice.messages, (m) => m.type === "state" && m.screenId === "stage", "owner screen at alice");
if (!(ownerScreen.receivedAt > 0) || ownerScreen.owner === aliceId) fail(`bad forwarded state ${JSON.stringify(ownerScreen)}`);
send(alice, { type: "state", screenId: "bar", state: { url: "https://youtu.be/b" } });
await waitFor(owner.messages, (m) => m.type === "state" && m.screenId === "bar" && m.owner === aliceId, "alice screen at owner");
await sleep(200);
if (owner.messages.some((m) => m.type === "state" && m.screenId === "stage")) fail("owner got its own screen back");

// Reconnecting needs no password, gets the others' screens and its own leftovers.
alice.socket.close();
await sleep(300);
alice = await open(socketUrl(code, aliceKey, "Alice"));
const back = await waitFor(alice.messages, (m) => m.type === "welcome", "alice back");
if (!back.yours.includes("bar")) fail(`alice's own screen not listed ${JSON.stringify(back)}`);
await waitFor(alice.messages, (m) => m.type === "state" && m.screenId === "stage", "stored owner screen after reconnect");

// Members can't moderate; the owner can promote, hide and kick.
send(alice, { type: "kick", id: members.members[0].id });
await waitFor(alice.messages, (m) => m.type === "error", "refused kick");
send(owner, { type: "promote", id: aliceId });
await waitFor(alice.messages, (m) => m.type === "members" && m.members.find((x) => x.you)?.role === "mod", "alice is a moderator");
send(owner, { type: "hide", owner: aliceId, screenId: "bar" });
await waitFor(alice.messages, (m) => m.type === "hidden" && m.hidden === true, "screen hidden");

// A banned member can't come back, even with the password; unbanned they can.
const bobKey = key();
const bob = await open(socketUrl(code, bobKey, "Bob", "hunter2"));
await waitFor(bob.messages, (m) => m.type === "welcome", "bob welcome");
const bobId = latestMembers(owner)?.members.find((m) => m.name === "Bob")?.id ?? (await waitFor(owner.messages, (m) => m.type === "members" && m.members.some((x) => x.name === "Bob"), "bob listed")).members.find((m) => m.name === "Bob").id;
send(alice, { type: "ban", id: bobId });
await waitClosed(bob, "banned", "bob banned");
if ((await refusal(socketUrl(code, bobKey, "Bob", "hunter2"))) !== "banned") fail("banned member came back");
const withBans = await waitFor(alice.messages, (m) => m.type === "members" && m.bans?.some((b) => b.id === bobId), "ban list for moderators");
if (!withBans) fail("no ban list");
send(alice, { type: "unban", id: bobId });
await waitFor(owner.messages, (m) => m.type === "members" && m.bans?.length === 0, "unbanned");

// Password changes; the owner hands the room over and leaves; the new owner deletes it.
send(owner, { type: "password", password: "" });
await waitFor(alice.messages, (m) => m.type === "room" && m.passwordSet === false, "password removed");
send(owner, { type: "leave" });
await waitClosed(owner, "left", "owner left");
await waitFor(alice.messages, (m) => m.type === "members" && m.members.length === 1 && m.members[0].role === "owner", "alice owns the room");
send(alice, { type: "delete" });
await waitClosed(alice, "deleted", "room deleted");
if ((await refusal(socketUrl(code, aliceKey, "Alice"))) !== "gone") fail("deleted room still open");

console.log("persistent smoke test passed");
process.exit(0);
