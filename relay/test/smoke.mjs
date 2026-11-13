// Drives a host and two viewers against a running relay (default: wrangler dev on port 8787).
// Usage: npm run smoke   or   RELAY=https://your-relay.workers.dev npm run smoke
const relay = (process.env.RELAY ?? "http://127.0.0.1:8787").replace(/\/$/, "");
const wsBase = relay.replace(/^http/, "ws");

const fail = (message) => { console.error("FAIL:", message); process.exit(1); };
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function open(url) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url);
    const messages = [];
    socket.onmessage = (event) => messages.push(JSON.parse(event.data));
    socket.onopen = () => resolve({ socket, messages });
    socket.onerror = () => reject(new Error(`could not connect: ${url}`));
  });
}

async function refused(url, what) {
  try {
    const { socket } = await open(url);
    socket.close();
  } catch {
    return;
  }
  fail(`${what} was not refused`);
}

async function waitFor(messages, predicate, what) {
  for (let i = 0; i < 50; i++) {
    const found = messages.find(predicate);
    if (found) return found;
    await sleep(100);
  }
  fail(`timed out waiting for ${what}`);
}

const created = await (await fetch(`${relay}/rooms`, { method: "POST" })).json();
if (!/^[2-9A-HJKMNP-Z]{8}$/.test(created.code ?? "") || !created.hostKey) fail(`bad create answer ${JSON.stringify(created)}`);
console.log("room", created.code);

await refused(`${wsBase}/rooms/${created.code}/socket?role=host&key=wrong`, "wrong host key");

const host = await open(`${wsBase}/rooms/${created.code}/socket?role=host&key=${created.hostKey}`);
await waitFor(host.messages, (m) => m.type === "welcome" && m.role === "host", "host welcome");

const early = await open(`${wsBase}/rooms/${created.code.toLowerCase()}/socket?role=viewer`);
const earlyWelcome = await waitFor(early.messages, (m) => m.type === "welcome", "viewer welcome");
if (earlyWelcome.state !== null || earlyWelcome.hostConnected !== true) fail(`unexpected first welcome ${JSON.stringify(earlyWelcome)}`);
await waitFor(host.messages, (m) => m.type === "viewers" && m.count === 1, "viewer count 1");

host.socket.send(JSON.stringify({ type: "state", change: true, state: { url: "https://youtu.be/x", positionMs: 1000 } }));
const forwarded = await waitFor(early.messages, (m) => m.type === "state", "forwarded state");
if (forwarded.state.positionMs !== 1000 || !(forwarded.receivedAt > 0)) fail(`bad forwarded state ${JSON.stringify(forwarded)}`);

early.socket.send(JSON.stringify({ type: "state", change: true, state: { url: "https://evil" } }));
await sleep(300);
if (early.messages.some((m) => m.type === "state" && m.state.url === "https://evil")) fail("viewer was able to send a state");

early.socket.send(JSON.stringify({ type: "ping", t: 42 }));
const pong = await waitFor(early.messages, (m) => m.type === "pong", "pong");
if (pong.t !== 42 || !(pong.relayTime > 0)) fail(`bad pong ${JSON.stringify(pong)}`);

const late = await open(`${wsBase}/rooms/${created.code}/socket?role=viewer`);
const lateWelcome = await waitFor(late.messages, (m) => m.type === "welcome", "late viewer welcome");
if (lateWelcome.state?.positionMs !== 1000) fail(`late viewer did not get the latest state ${JSON.stringify(lateWelcome)}`);
await waitFor(host.messages, (m) => m.type === "viewers" && m.count === 2, "viewer count 2");

host.socket.close();
await waitFor(early.messages, (m) => m.type === "host" && m.connected === false, "host disconnected notice");

const host2 = await open(`${wsBase}/rooms/${created.code}/socket?role=host&key=${created.hostKey}`);
await waitFor(early.messages, (m) => m.type === "host" && m.connected === true, "host reconnected notice");
host2.socket.send(JSON.stringify({ type: "close" }));
await waitFor(early.messages, (m) => m.type === "closed", "room closed notice");

await sleep(300);
await refused(`${wsBase}/rooms/${created.code}/socket?role=viewer`, "closed room");
await refused(`${wsBase}/rooms/OIL10000/socket?role=viewer`, "invalid code");

for (const s of [early.socket, late.socket]) try { s.close(); } catch {}
console.log("PASS: create, host key check, forward, viewer cannot send, ping, late join, host leave/return, close, invalid code");
process.exit(0);
