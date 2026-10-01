// Sender identity for send_message / broadcast_message (ticket eb585e6e)
//
// A model often does not know its own MultiTerminal name (a ClarionAssistant tab is never told it), so
// the MCP server works it out: the live row bound to this session's claude.exe is authoritative; the
// name this process claimed and the launch env name are defaults only, because either can be stale
// after a rename. These tests run the REAL block sliced out of mcp/index.js (house pattern, see
// claimedIdentity.test.mjs: importing index.js would start the whole server), with the lookup injected.
//
// What these tests do NOT prove: that the real channel-identity call returns this session's name on a
// live MT. The lookup is injected here. That is checklist item 6 (live check after deploy).

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const START = "// ── Sender identity (ticket eb585e6e) ──";
const END = "// ── end sender identity ──";

function extractBlock(source) {
  const i = source.indexOf(START);
  assert.ok(i >= 0, "sender-identity block start marker not found in mcp/index.js");
  const j = source.indexOf(END, i);
  assert.ok(j > i, "sender-identity block end marker not found");
  const block = source.slice(i, j);
  // Vacuity guard: an emptied block would make every test below pass against nothing.
  assert.ok(block.length > 1500, `sender-identity block suspiciously short (${block.length} chars)`);
  return block;
}

const BLOCK = extractBlock(src);

function load() {
  const fn = new Function(
    "PLACEHOLDER_TERMINAL_NAME", "claimedTerminalName", "apiCall", "isClarionEmbedded",
    `${BLOCK}
     return { resolveSenderIdentity, chooseSender, unregisteredError };`,
  );
  const apiCall = async () => { throw new Error("apiCall must not be reached: tests inject deps.get"); };
  // The real predicate lives outside this block; the CA tests below pass deps.embedded explicitly, so
  // this stub only decides the default for the MT-pane tests (never embedded).
  const isClarionEmbedded = () => false;
  return fn("Unassigned", null, apiCall, isClarionEmbedded);
}

const { resolveSenderIdentity, chooseSender, unregisteredError } = load();

// A lookup that answers for exactly one pid, like the broker: anything else is a 404.
function liveLookup(map) {
  const calls = [];
  const get = async (endpoint) => {
    calls.push(endpoint);
    const m = /ppid=(\d+)$/.exec(endpoint);
    const name = m && map[m[1]];
    if (!name) { const e = new Error("API error: 404 Not Found"); e.status = 404; throw e; }
    return { name };
  };
  return { get, calls };
}

// ── resolveSenderIdentity ─────────────────────────────────────────────────────────────────────────

test("resolve: the live row bound to our claude.exe wins over a stale claim and a stale env name", async () => {
  const { get, calls } = liveLookup({ 4242: "CA-Terminal-1-CC-2" });
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: "OldClaim", env: { MULTITERMINAL_NAME: "LaunchName" } });
  assert.deepEqual(r, { name: "CA-Terminal-1-CC-2", authoritative: true });
  assert.deepEqual(calls, ["/api/messaging/channel-identity?ppid=4242"]);
});

test("resolve: no live row (404) falls back to the claimed name, NOT authoritative", async () => {
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: "Claimed", env: { MULTITERMINAL_NAME: "LaunchName" } });
  assert.deepEqual(r, { name: "Claimed", authoritative: false });
});

test("resolve: no live row and no claim falls back to the env name, NOT authoritative", async () => {
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: { MULTITERMINAL_NAME: "LaunchName" } });
  assert.deepEqual(r, { name: "LaunchName", authoritative: false });
});

test("resolve: MT unreachable still falls back and never throws, but marks the default UNCONFIRMED", async () => {
  // Adversary run 1: a failed lookup is not the same as "no row". After a rename the default is stale,
  // so the caller must be able to say so. A 404 (an answer) must NOT set it: see the 404 tests above,
  // whose deepEqual has no `unconfirmed` key.
  for (const err of [new Error("connect ECONNREFUSED"), Object.assign(new Error("API error: 500"), { status: 500 })]) {
    const r = await resolveSenderIdentity({ get: async () => { throw err; }, ppid: 4242, held: null, env: { MULTITERMINAL_NAME: "LaunchName" } });
    assert.equal(r.name, "LaunchName");
    assert.equal(r.authoritative, false);
    assert.ok(r.unconfirmed && r.unconfirmed.includes(err.message), `unconfirmed: ${r.unconfirmed}`);
  }
});

test("resolve: a live answer without a usable name is ignored, not trusted, and marks the default unconfirmed", async () => {
  for (const body of [null, {}, { name: "" }, { name: 7 }]) {
    const r = await resolveSenderIdentity({ get: async () => body, ppid: 4242, held: null, env: { MULTITERMINAL_NAME: "LaunchName" } });
    assert.equal(r.name, "LaunchName", `body ${JSON.stringify(body)}`);
    assert.equal(r.authoritative, false);
    assert.ok(r.unconfirmed, `body ${JSON.stringify(body)} should be unconfirmed`);
  }
});

test("resolve: the shared 'Unassigned' placeholder identifies nobody, in any case and with padding", async () => {
  const { get } = liveLookup({});
  for (const ph of ["Unassigned", "UNASSIGNED", " unassigned "]) {
    const viaEnv = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: { MULTITERMINAL_NAME: ph } });
    assert.deepEqual(viaEnv, { name: null, authoritative: false }, `env ${JSON.stringify(ph)}`);
    const viaClaim = await resolveSenderIdentity({ get, ppid: 4242, held: ph, env: { MULTITERMINAL_NAME: "LaunchName" } });
    assert.deepEqual(viaClaim, { name: "LaunchName", authoritative: false }, `claim ${JSON.stringify(ph)}`);
  }
});

test("resolve: an invalid ppid skips the live lookup entirely", async () => {
  for (const ppid of [0, -1, null, 1.5]) {
    let called = false;
    const r = await resolveSenderIdentity({ get: async () => { called = true; return { name: "X" }; }, ppid, held: null, env: {} });
    assert.equal(called, false, `ppid ${ppid}`);
    assert.deepEqual(r, { name: null, authoritative: false });
  }
});

// ── ClarionAssistant tab: the env name is a REQUEST, not an identity (c175492a pipeline) ──────────

const CA_ENV = { CLARION_ASSISTANT_EMBEDDED: "1", MULTITERMINAL_NAME: "CA-Terminal-1-CC" };

test("CA tab, refused: no live row and no claim means NOT REGISTERED, never the env name", async () => {
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: CA_ENV, embedded: true, outcome: "refused" });
  assert.equal(r.name, null);
  assert.deepEqual(r.unregistered, { as: "CA-Terminal-1-CC", outcome: "refused" });
});

test("CA tab, refused: send fails with the phrases CA's prompt keys on, whether or not a name is given", async () => {
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: CA_ENV, embedded: true, outcome: "refused" });
  // The given name is exactly the one the tab was told: it must not get past.
  for (const given of [undefined, "CA-Terminal-1-CC", "Someone-Else"]) {
    const c = chooseSender(given, r);
    assert.equal(c.from, undefined, `given ${given}`);
    assert.ok(/not registered as CA-Terminal-1-CC/.test(c.error), c.error);
    assert.ok(/held by another terminal/.test(c.error), c.error);
  }
});

test("CA tab, still registering: a retry message WITHOUT the stop phrases CA's prompt keys on", async () => {
  // Code-reviewer run 1: a few-second startup race must not read as "stop and tell the developer".
  const { get } = liveLookup({});
  const pending = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: CA_ENV, embedded: true, outcome: null });
  const msg = unregisteredError(pending);
  assert.ok(/registration as CA-Terminal-1-CC is still in progress; try again/.test(msg), msg);
  for (const stop of ["not registered", "held by another terminal", "tell the developer"]) {
    assert.ok(!msg.includes(stop), `pending message contains stop phrase "${stop}": ${msg}`);
  }
});

test("CA tab, failed (not refused): not registered, but NOT 'held by another terminal'", async () => {
  // Code-reviewer run 1: a timeout is not a name clash. registerEmbeddedTab now returns "failed" for it.
  const { get } = liveLookup({});
  const failed = await resolveSenderIdentity({ get, ppid: 4242, held: null, env: CA_ENV, embedded: true, outcome: "failed" });
  const msg = unregisteredError(failed);
  assert.ok(/not registered as CA-Terminal-1-CC: MultiTerminal registration did not complete \(failed\)/.test(msg), msg);
  assert.ok(!msg.includes("held by another terminal"), msg);
});

test("CA tab, registered: the live row answers, so it sends normally", async () => {
  const { get } = liveLookup({ 4242: "CA-Terminal-1-CC" });
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: "CA-Terminal-1-CC", env: CA_ENV, embedded: true, outcome: "registered" });
  assert.deepEqual(r, { name: "CA-Terminal-1-CC", authoritative: true });
  assert.equal(unregisteredError(r), null);
});

test("CA tab, registered but MT briefly unreachable: its successful claim is a default, not an error", async () => {
  const get = async () => { throw new Error("connect ECONNREFUSED"); };
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: "CA-Terminal-1-CC", env: CA_ENV, embedded: true, outcome: "registered" });
  assert.equal(r.name, "CA-Terminal-1-CC");
  assert.equal(r.authoritative, false);
  assert.equal(r.unregistered, undefined);
  assert.ok(r.unconfirmed, "an unreachable MT leaves the claim unconfirmed");
});

test("embedded WITH an inherited docId: never the env name; not registered, and says why", async () => {
  // Debugger run 1 (HIGH). A Clarion IDE started from an MT shell inherits the pane's MULTITERMINAL_*
  // env, docId included. selfRegisterTerminal skips it ("skipped") because that pane is not this
  // session's, so the env name must not become its sender either. (An earlier version of this file
  // pinned the opposite, calling embedded+docId "an MT pane". A real MT pane is never embedded.)
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: null, embedded: true, outcome: "skipped",
    env: { ...CA_ENV, MULTITERMINAL_DOC_ID: "7eebd355" } });
  assert.equal(r.name, null);
  assert.deepEqual(r.unregistered, { as: "CA-Terminal-1-CC", outcome: "skipped" });
  assert.ok(/not registered as CA-Terminal-1-CC: this session inherited another terminal's/.test(unregisteredError(r)));
});

test("discriminator: a real MT pane (NOT embedded, has a docId) keeps the env default when the lookup misses", async () => {
  // Same inputs as above except `embedded`. MT pre-registers a pane's launch name for that pane, so it
  // is the pane's own. If the embedded check were dropped, every such pane would be refused.
  const { get } = liveLookup({});
  const r = await resolveSenderIdentity({ get, ppid: 4242, held: null, embedded: false, outcome: "registered",
    env: { MULTITERMINAL_NAME: "Alice", MULTITERMINAL_DOC_ID: "7eebd355" } });
  assert.deepEqual(r, { name: "Alice", authoritative: false });
});

// ── chooseSender ──────────────────────────────────────────────────────────────────────────────────

const LIVE = { name: "CA-Terminal-1-CC-2", authoritative: true };
const STALE = { name: "LaunchName", authoritative: false };
const NONE = { name: null, authoritative: false };

test("choose: omitted sender defaults to our own name, from either source, with no note", () => {
  assert.deepEqual(chooseSender(undefined, LIVE), { from: "CA-Terminal-1-CC-2", note: null });
  assert.deepEqual(chooseSender("", STALE), { from: "LaunchName", note: null });
});

test("choose: omitted sender with no identity is an error, not a guess", () => {
  const r = chooseSender(undefined, NONE);
  assert.ok(r.error && /fromTerminalId is required/.test(r.error));
  assert.equal(r.from, undefined);
});

test("choose: the RIGHT name given passes through unchanged, compared case-insensitively like the broker", () => {
  assert.deepEqual(chooseSender("CA-Terminal-1-CC-2", LIVE), { from: "CA-Terminal-1-CC-2", note: null });
  assert.deepEqual(chooseSender("ca-terminal-1-cc-2", LIVE), { from: "ca-terminal-1-cc-2", note: null });
});

test("choose: a WRONG name given is corrected to the live identity, with a note naming both", () => {
  // Today's bug exactly: the second IDE's tab guessed the first tab's name.
  const r = chooseSender("CA-Terminal-1-CC", LIVE);
  assert.equal(r.from, "CA-Terminal-1-CC-2");
  assert.ok(r.note.includes("CA-Terminal-1-CC-2") && r.note.includes('"CA-Terminal-1-CC"'), r.note);
});

test("choose: a different name is NOT overridden when our identity is only a default (stale env after a rename)", () => {
  // The discriminator for the authoritative flag: same inputs as the correction case except the
  // flag. If chooseSender ignored it, this would wrongly rewrite a renamed pane's correct name.
  assert.deepEqual(chooseSender("RenamedPane", STALE), { from: "RenamedPane", note: null });
});

test("choose: an omitted sender whose default could not be confirmed is sent, with a note saying so", () => {
  const r = chooseSender(undefined, { name: "LaunchName", authoritative: false, unconfirmed: "could not reach MultiTerminal to confirm: timeout" });
  assert.equal(r.from, "LaunchName");
  assert.ok(r.note && r.note.includes("could not reach MultiTerminal") && r.note.includes("pass fromTerminalId"), r.note);
});

test("choose: a subagent's own 'Agent <label>' name is never 'corrected' to its parent's", () => {
  // Debugger run 1. A subagent shares its parent's MCP server, so the live identity is the PARENT's.
  for (const given of ["Agent Explore", "agent explore", "AGENT Verifier"]) {
    assert.deepEqual(chooseSender(given, LIVE), { from: given, note: null }, given);
  }
  // Not a prefix match on the bare word: "Agentic" is an ordinary name and is corrected as usual.
  assert.equal(chooseSender("Agentic", LIVE).from, "CA-Terminal-1-CC-2");
});

test("choose: comparison is untrimmed, as the broker's is: padding is a different name", () => {
  const r = chooseSender(" CA-Terminal-1-CC-2", LIVE);
  assert.equal(r.from, "CA-Terminal-1-CC-2");
  assert.ok(r.note);
});

// ── schema ────────────────────────────────────────────────────────────────────────────────────────

function toolDef(name) {
  const i = src.indexOf(`name: "${name}",`);
  assert.ok(i >= 0, `tool def ${name} not found`);
  // index.js may have CRLF line endings; the def ends at the first 6-space-indented closing brace.
  const end = /\r?\n {6}\},\r?\n/g;
  end.lastIndex = i;
  const m = end.exec(src);
  assert.ok(m, `end of tool def ${name} not found`);
  const def = src.slice(i, m.index);
  assert.ok(def.length > 200, `tool def ${name} suspiciously short (${def.length} chars)`);
  return def;
}

for (const tool of ["send_message", "broadcast_message"]) {
  test(`schema: ${tool} still accepts fromTerminalId but no longer requires it`, () => {
    const def = toolDef(tool);
    const required = /required:\s*\[([^\]]*)\]/.exec(def);
    assert.ok(required, `${tool}: no required array found`);
    assert.ok(!/["']fromTerminalId["']/.test(required[1]), `${tool} still requires fromTerminalId: [${required[1]}]`);
    assert.ok(/fromTerminalId:\s*\{/.test(def), `${tool}: fromTerminalId property was removed, not made optional`);
  });
}
