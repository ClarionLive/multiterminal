// Claimed-identity duties of the MCP server (ticket 0ff1b520 item 13)
//
// A session MT did not launch joins MT only through register_terminal. The retired channel server
// used to give such a session its native delivery and its release on /quit; this server now does.
// These tests run the REAL block sliced out of mcp/index.js (house pattern, see pushRatio.test.mjs:
// importing index.js would start the whole server), with fetch, console and the process probe
// injected.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const START = "// ── Claimed-identity duties (ticket 0ff1b520 item 13)";
const END = "// ── end claimed-identity duties ──";

function extractBlock(source) {
  const i = source.indexOf(START);
  assert.ok(i >= 0, "claimed-identity block start marker not found in mcp/index.js");
  const j = source.indexOf(END, i);
  assert.ok(j > i, "claimed-identity block end marker not found");
  const block = source.slice(i, j);
  // Vacuity guard: an emptied block would make every test below pass against nothing.
  assert.ok(block.length > 2000, `claimed-identity block suspiciously short (${block.length} chars)`);
  return block;
}

const BLOCK = extractBlock(src);

const SENTINEL_TOKEN = "SENTINEL-TOKEN-7f3a9c2e41b8d05a";
const GOOD_SOCKET = "\\\\.\\pipe\\LOCAL\\cc-msg-0123456789abcdef0123456789abcdef";

// Fresh module state per test: the block keeps claimedTerminalName / releasingClaimedName.
function load() {
  const logged = [];
  const fakeConsole = {
    log: (...a) => logged.push(a.join(" ")),
    error: (...a) => logged.push(a.join(" ")),
    warn: (...a) => logged.push(a.join(" ")),
  };
  const fn = new Function(
    "API_BASE", "console",
    `${BLOCK}
     return {
       messagingCredentialsFromEnv, postClaimedCredentials, releaseClaimedName, parentExitedWithin,
       claim: (n) => { claimedTerminalName = n; },
     };`,
  );
  return { mod: fn("http://mt.test", fakeConsole), logged };
}

function recordingFetch({ ok = true, throws = false } = {}) {
  const calls = [];
  const impl = async (url, opts) => {
    calls.push({ url, body: opts && opts.body ? JSON.parse(opts.body) : null, raw: opts && opts.body });
    if (throws) throw new Error("connect ECONNREFUSED");
    return { ok, status: ok ? 200 : 400 };
  };
  return { impl, calls };
}

const goodEnv = { CLAUDE_CODE_MESSAGING_SOCKET: GOOD_SOCKET, CLAUDE_CODE_MESSAGING_TOKEN: SENTINEL_TOKEN };

test("credentials: well-formed env is accepted; malformed values are refused, not repaired", () => {
  const { mod } = load();
  assert.deepEqual(mod.messagingCredentialsFromEnv(goodEnv), { socket: GOOD_SOCKET, token: SENTINEL_TOKEN });
  assert.equal(mod.messagingCredentialsFromEnv({}), null);
  assert.equal(mod.messagingCredentialsFromEnv({ ...goodEnv, CLAUDE_CODE_MESSAGING_SOCKET: "\\\\.\\pipe\\cc-msg-abc" }), null);
  assert.equal(mod.messagingCredentialsFromEnv({ ...goodEnv, CLAUDE_CODE_MESSAGING_SOCKET: " " + GOOD_SOCKET }), null);
  // A newline could smuggle a second line into the two-line pipe handshake.
  assert.equal(mod.messagingCredentialsFromEnv({ ...goodEnv, CLAUDE_CODE_MESSAGING_TOKEN: SENTINEL_TOKEN + "\n{}" }), null);
  assert.equal(mod.messagingCredentialsFromEnv({ ...goodEnv, CLAUDE_CODE_MESSAGING_TOKEN: "short" }), null);
});

test("post: sends name, session id, socket and token to the credentials endpoint", async () => {
  const { mod } = load();
  const f = recordingFetch();
  const status = await mod.postClaimedCredentials("Robin", goodEnv, "sess-1", f.impl);
  assert.equal(status, "sent");
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, "http://mt.test/api/messaging/credentials");
  assert.deepEqual(f.calls[0].body, { name: "Robin", sessionId: "sess-1", socket: GOOD_SOCKET, token: SENTINEL_TOKEN });
});

test("post: no credentials means no request at all; broker refusal and outage are reported, not thrown", async () => {
  const { mod } = load();
  const none = recordingFetch();
  assert.equal(await mod.postClaimedCredentials("Robin", {}, "s", none.impl), "unavailable");
  assert.equal(none.calls.length, 0);
  assert.equal(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ ok: false }).impl), "refused");
  assert.equal(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ throws: true }).impl), "failed");
});

test("the token never reaches a log line or a return value, on any path", async () => {
  const { mod, logged } = load();
  const results = [];
  results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch().impl));
  results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ ok: false }).impl));
  results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ throws: true }).impl));
  mod.claim("Robin");
  results.push(await mod.releaseClaimedName("test", { ppid: 1, waitMs: 0, isAlive: () => false, fetchImpl: recordingFetch().impl }));
  // Non-vacuous: the release path did log something, so the scan below is looking at real output.
  assert.ok(logged.length > 0, "expected the release path to log");
  for (const line of [...logged, ...results.map(String)]) {
    assert.ok(!line.includes(SENTINEL_TOKEN), `token leaked into: ${line}`);
  }
});

test("release: a server that claimed nothing releases nothing", async () => {
  const { mod } = load();
  const f = recordingFetch();
  assert.equal(await mod.releaseClaimedName("stdin closed", { ppid: 1, waitMs: 0, isAlive: () => false, fetchImpl: f.impl }), "nothing-claimed");
  assert.equal(f.calls.length, 0);
});

test("release: NOT while the parent session is alive (an MCP-server restart must not drop a live terminal)", async () => {
  const { mod } = load();
  mod.claim("Robin");
  const f = recordingFetch();
  const status = await mod.releaseClaimedName("stdin closed", { ppid: 4242, waitMs: 0, isAlive: () => true, fetchImpl: f.impl });
  assert.equal(status, "parent-alive");
  assert.equal(f.calls.length, 0, "a disconnect was posted while the session was still running");
});

test("release: once the parent has exited, disconnects exactly the claimed name, exactly once", async () => {
  const { mod } = load();
  mod.claim("Robin");
  const f = recordingFetch();
  assert.equal(await mod.releaseClaimedName("stdin closed", { ppid: 4242, waitMs: 0, isAlive: () => false, fetchImpl: f.impl }), "released");
  assert.equal(await mod.releaseClaimedName("SIGTERM", { ppid: 4242, waitMs: 0, isAlive: () => false, fetchImpl: f.impl }), "already-releasing");
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, "http://mt.test/api/messaging/disconnect");
  assert.deepEqual(f.calls[0].body, { name: "Robin" });
});

test("release: waits for a parent that is still shutting down", async () => {
  const { mod } = load();
  let probes = 0;
  // Alive for the first two probes, then gone: a normal /quit where claude closes stdin before exiting.
  const exited = await mod.parentExitedWithin(4242, 1000, () => ++probes <= 2);
  assert.equal(exited, true);
  assert.ok(probes >= 3);
});

// Wiring. These read code with // comments stripped, so a comment naming a call cannot satisfy them.
function stripLineComments(s) {
  return s.split("\n").map((l) => l.replace(/^\s*\/\/.*$/, "")).join("\n");
}

test("wiring: register_terminal claims the name only AFTER the broker accepted the registration", () => {
  const i = src.indexOf('case "register_terminal": {');
  assert.ok(i >= 0, "register_terminal case not found");
  const body = stripLineComments(src.slice(i, src.indexOf("case \"get_messages\"", i)));
  assert.ok(body.length > 500, "register_terminal body suspiciously short");
  const reg = body.indexOf('apiCall("/api/messaging/register"');
  const claim = body.indexOf("claimedTerminalName = args.name");
  const post = body.indexOf("postClaimedCredentials(args.name");
  assert.ok(reg >= 0 && claim >= 0 && post >= 0, "register / claim / credential post not all present");
  assert.ok(reg < claim && claim < post, "the claim or the credential post happens before the registration succeeded");
});

test("wiring: main() installs the shutdown release", () => {
  const i = src.indexOf("async function main()");
  assert.ok(i >= 0, "main() not found");
  const body = stripLineComments(src.slice(i, src.indexOf("\n}\n", i)));
  assert.ok(body.includes("installClaimedIdentityShutdown()"), "main() does not install the shutdown release");
});
