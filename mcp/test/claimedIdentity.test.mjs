// Claimed-identity duties of the MCP server (ticket 0ff1b520 item 13)
//
// A session MT did not launch joins MT only through register_terminal. The retired channel server
// used to give such a session its native delivery; this server now does, by posting the session's
// messaging credentials under the claimed name. Release on /quit is NOT done here: a stdio server
// cannot see its parent exit while the parent waits on it (pipeline run 2), so the broker's liveness
// reaper releases a dead owner's row. These tests run the REAL block sliced out of mcp/index.js
// (house pattern, see pushRatio.test.mjs: importing index.js would start the whole server), with
// fetch and console injected.

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

// Fresh module state per test: the block keeps claimedTerminalName.
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
     return { messagingCredentialsFromEnv, postClaimedCredentials, releasePreviousClaim };`,
  );
  return { mod: fn("http://mt.test", fakeConsole), logged };
}

function recordingFetch({ ok = true, throws = false } = {}) {
  const calls = [];
  const impl = async (url, opts) => {
    calls.push({ url, body: opts && opts.body ? JSON.parse(opts.body) : null });
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

test("post: sends name, session id, socket, token and ownerPid to the credentials endpoint", async () => {
  const { mod } = load();
  const f = recordingFetch();
  const status = await mod.postClaimedCredentials("Robin", goodEnv, "sess-1", f.impl, 4242);
  assert.equal(status, "sent");
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, "http://mt.test/api/messaging/credentials");
  // No MULTITERMINAL_LAUNCH_NONCE in the env: the key is absent, not empty.
  assert.deepEqual(f.calls[0].body, { name: "Robin", sessionId: "sess-1", socket: GOOD_SOCKET, token: SENTINEL_TOKEN, ownerPid: 4242 });
});

test("post: owner proof (ticket 9a731cda) — the env's launch nonce is sent, and ownerPid defaults to process.ppid", async () => {
  const { mod } = load();
  const f = recordingFetch();
  assert.equal(await mod.postClaimedCredentials("Robin", { ...goodEnv, MULTITERMINAL_LAUNCH_NONCE: "nonce-xyz" }, "s", f.impl), "sent");
  assert.equal(f.calls[0].body.nonce, "nonce-xyz");
  // Discriminator: the test runner's ppid is a real, non-zero pid, so an omitted ownerPid fails here.
  assert.ok(process.ppid > 0);
  assert.equal(f.calls[0].body.ownerPid, process.ppid);
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
  const origError = console.error;
  const captured = [];
  // Belt and braces: also catch anything written to the REAL console, not only the injected one.
  console.error = (...a) => captured.push(a.join(" "));
  try {
    const results = [];
    results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch().impl));
    results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ ok: false }).impl));
    results.push(await mod.postClaimedCredentials("Robin", goodEnv, "s", recordingFetch({ throws: true }).impl));
    for (const line of [...logged, ...captured, ...results.map(String)]) {
      assert.ok(!line.includes(SENTINEL_TOKEN), `token leaked into: ${line}`);
    }
  } finally {
    console.error = origError;
  }
});

test("re-claim: claiming a NEW name releases the name held before, so it stops routing here", async () => {
  const { mod } = load();
  const f = recordingFetch();
  assert.equal(await mod.releasePreviousClaim("Bravo", { held: "Alpha", fetchImpl: f.impl }), "released");
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, "http://mt.test/api/messaging/disconnect");
  assert.deepEqual(f.calls[0].body, { name: "Alpha" });
});

test("re-claim: the same name (any case) or nothing held releases nothing", async () => {
  const { mod } = load();
  const f = recordingFetch();
  // Different case on purpose: the broker keys names case-insensitively, so this IS the same name.
  assert.equal(await mod.releasePreviousClaim("alpha", { held: "ALPHA", fetchImpl: f.impl }), "nothing-to-release");
  assert.equal(await mod.releasePreviousClaim("Alpha", { held: null, fetchImpl: f.impl }), "nothing-to-release");
  assert.equal(f.calls.length, 0);
});

test("re-claim: never releases the shared placeholder name (it would tear down ANOTHER pane's live row)", async () => {
  const { mod } = load();
  const f = recordingFetch();
  assert.equal(await mod.releasePreviousClaim("Bravo", { held: "unassigned", fetchImpl: f.impl }), "nothing-to-release");
  assert.equal(f.calls.length, 0);
});

test("re-claim: the name MT launched the session under is not this server's to release", async () => {
  const { mod } = load();
  const f = recordingFetch();
  const saved = process.env.MULTITERMINAL_NAME;
  process.env.MULTITERMINAL_NAME = "Alpha";
  try {
    // Nothing claimed through register_terminal yet: the launch name must not be used as the held name.
    assert.equal(await mod.releasePreviousClaim("Bravo", { fetchImpl: f.impl }), "nothing-to-release");
    assert.equal(f.calls.length, 0);
  } finally {
    if (saved === undefined) delete process.env.MULTITERMINAL_NAME; else process.env.MULTITERMINAL_NAME = saved;
  }
});

test("re-claim: an unreachable broker is reported, not thrown", async () => {
  const { mod } = load();
  assert.equal(await mod.releasePreviousClaim("Bravo", { held: "Alpha", fetchImpl: recordingFetch({ throws: true }).impl }), "failed");
  assert.equal(await mod.releasePreviousClaim("Bravo", { held: "Alpha", fetchImpl: recordingFetch({ ok: false }).impl }), "refused");
});

// Wiring. These read CODE: whole-line // comments are stripped. index.js is CRLF. An earlier version
// split on "\n" and matched /^\s*\/\/.*$/: every line kept a trailing \r, which `.` cannot match
// before `$`, so nothing was ever stripped. This version splits on \r?\n and tests only the prefix,
// so it doesn't depend on either. The next test pins that against the file's real line endings.
function codeOnly(s) {
  return s.split(/\r?\n/).filter((l) => !/^\s*\/\//.test(l)).join("\n");
}

function registerTerminalCode() {
  const i = src.indexOf('case "register_terminal": {');
  assert.ok(i >= 0, "register_terminal case not found");
  const j = src.indexOf('case "get_messages"', i);
  assert.ok(j > i, "end of register_terminal case not found");
  const body = codeOnly(src.slice(i, j));
  assert.ok(body.length > 500, `register_terminal body suspiciously short (${body.length})`);
  return body;
}

test("wiring: register_terminal releases the old claim and posts credentials only AFTER the broker accepted the new one", () => {
  const body = registerTerminalCode();
  const reg = body.indexOf('apiCall("/api/messaging/register"');
  const release = body.indexOf("releasePreviousClaim(args.name)");
  const claim = body.indexOf("claimedTerminalName = args.name");
  const post = body.indexOf("postClaimedCredentials(args.name");
  assert.ok(reg >= 0 && release >= 0 && claim >= 0 && post >= 0, "register / release / claim / post not all present in code");
  assert.ok(reg < release, "the old claim is released before the new registration succeeded");
  assert.ok(release < claim, "the held name is overwritten before it is released");
  assert.ok(claim < post, "credentials are posted before the claim is recorded");
});

test("the helper strips comments on this file's real line endings", () => {
  // Guards the guard: if codeOnly stopped stripping (e.g. a "\n" split on a CRLF file), a comment
  // naming a call would satisfy the wiring test above.
  const eol = src.includes("\r\n") ? "\r\n" : "\n";
  const sample = ["  // releasePreviousClaim(args.name)", "  realCode();"].join(eol);
  assert.ok(!codeOnly(sample).includes("releasePreviousClaim"), "codeOnly left a comment line in place");
  assert.ok(codeOnly(sample).includes("realCode()"), "codeOnly dropped real code");
});

test("no release-on-exit handler: a stdio server cannot see its parent exit while the parent waits on it", () => {
  const code = codeOnly(src);
  assert.ok(code.length > 100000, "index.js code suspiciously short");
  assert.ok(!/process\.stdin\.(on|once|addListener)\(\s*["'](end|close)["']/.test(code), "a stdin end/close handler is back; see the block comment for why it cannot release");
  assert.ok(!/process\.(on|once|addListener)\(\s*["'](SIGINT|SIGTERM|exit|beforeExit)["']/.test(code), "a process exit/signal handler is back; a stdio server cannot see its parent exit, and on Windows signal handlers never run");
});
