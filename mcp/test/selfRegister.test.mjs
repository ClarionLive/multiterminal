// Startup self-registration (task 54005ee7, ported to this branch by ticket 9a731cda) and the
// ClarionAssistant-tab branch (ticket 9a731cda items 5 and 11).
//
// The MCP server registers a terminal MT launched as soon as it starts, so /session-start no longer
// spends a model turn on register_terminal. Properties pinned here:
//   1. It sends exactly what register_terminal sends. Both build the body with buildRegisterPayload,
//      and the handler is pinned to call it, so the two cannot drift apart.
//   2. An MT pane is refreshed only with both MULTITERMINAL_NAME and MULTITERMINAL_DOC_ID set. The
//      shared placeholder "Unassigned" is never self-registered (any case), in either branch.
//   3. A ClarionAssistant tab (EMBEDDED + NAME, no DOC_ID) registers by name only when MT advertises
//      "ca-embedded-v1" AND the parent is claude.exe, and posts credentials only after the broker
//      ACCEPTED the registration. EMBEDDED + DOC_ID (an IDE that inherited an MT shell's env) does nothing.
//   4. It can never crash or block startup: every failure is swallowed, each CA step has a deadline,
//      and main() does not await it. The messaging token never reaches a log line or a return value.
//
// House pattern (pushRatio.test.mjs, claimedIdentity.test.mjs): read mcp/index.js as TEXT (importing it
// would start the server), extract the real code and execute it with injected dependencies. Extraction
// is asserted non-vacuous. Scans of handler and main() text drop whole-line // comments.
//
// Tripwire: the extracted code's own `fetch` and `execFile` are replaced with functions that RECORD and
// THROW. The live MultiTerminal listens on localhost:5050, and a broken guard that fell through to a real
// request would otherwise hit it (and the code under test swallows the throw), so every test asserts
// the tripwire log is empty. Every await on code under test runs under a rejecting 2s deadline, so a
// hang fails the test instead of hanging the suite.
//
// FALSIFIED (ticket 9a731cda, 2026-09-29) by a script that, per mutation: ran the named test green on
// the real file, applied ONE string replacement to mcp/index.js (asserting the find string occurred
// exactly once and the guard expression was absent afterwards), ran the named test, and restored the
// file byte-for-byte. Results:
//   - capability check replaced by `if (false)`            -> "CA: capabilities ..." red
//   - Array.isArray dropped (`health.capabilities || []`)  -> "CA: capabilities ..." red (string case)
//   - `if (env.MULTITERMINAL_DOC_ID) return "skipped"` removed from the embedded branch
//                                                          -> "CA: EMBEDDED with a docId ..." red
//   - image check replaced by `if (false)`                 -> "CA: parent image ..." red
//   - `return "refused"` removed (credentials posted after a failed register)
//                                                          -> "CA: a refused, failed or hung ..." red
//   - register not awaited (`withDeadline(...).catch()` without await)
//                                                          -> "CA: a refused, failed or hung ..." red
//   - placeholder skip line removed                        -> "the shared placeholder ..." red
//   - credentials `ownerPid` / `nonce` lines removed       -> claimedIdentity "post: ..." tests red
//
// FALSIFIED again for pipeline run 1 (2026-09-29), same method, each named test green before and red after:
//   - case-1 postClaimedCredentials call short-circuited   -> "MT pane: after the broker ACCEPTS ..." red
//   - case-1 `return "failed"` removed (refusal falls through to the post)
//                                                          -> "MT pane: a refused or failed refresh ..." red
//   - `if (probe.kind !== "retry") break;` -> `break;`     -> "CA: capability absent twice ...",
//                                                             "CA: unreachable twice ...",
//                                                             "CA: health 404, bad JSON or a hang forever ..." red
//   - `health.service !== MT_HEALTH_SERVICE_MARKER` -> false
//                                                          -> "CA: a health body without MT's exact service marker ..." red
//   - marker mismatch returned as kind "retry"             -> "CA: definitive refusals are not retried ..." red
//   - call site `isClarionEmbedded(env)` -> `env.CLARION_ASSISTANT_EMBEDDED`
//                                                          -> "both call sites honour it ..." red
//   - predicate body -> `return !!v;`                      -> "isClarionEmbedded: the plugin's truth table" red
//   - unrefSleep's `timer.unref()` line removed            -> "CA: the default retry wait uses unref'd timers ..." red
import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
// Normalized to LF: the checkout may carry CRLF, and the extraction below looks for a line "}".
const src = readFileSync(indexPath, "utf8").replace(/\r\n/g, "\n");

// A top-level function runs from its header to the first line that is exactly "}".
function topLevelFunction(header) {
  const start = src.indexOf(header);
  assert.ok(start >= 0, `${header} missing from index.js`);
  const end = src.indexOf("\n}\n", start);
  assert.ok(end > start, `could not find the end of ${header}`);
  return src.slice(start, end + 2);
}

function code(block) {
  return block
    .split(/\r?\n/)
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
}

const builderSrc = topLevelFunction("function buildRegisterPayload(");
const selfRegSrc = topLevelFunction("async function selfRegisterTerminal(");

// selfRegisterTerminal lives in the claimed-identity block: the CA branch sets claimedTerminalName and
// calls postClaimedCredentials, so the whole block is executed, with the builder alongside.
const BLOCK_START = "// ── Claimed-identity duties (ticket 0ff1b520 item 13)";
const BLOCK_END = "// ── end claimed-identity duties ──";
const blockStart = src.indexOf(BLOCK_START);
const BLOCK = src.slice(blockStart, src.indexOf(BLOCK_END, blockStart));

const REAL_API_BASE = "http://localhost:5050";
const SENTINEL_TOKEN = "SENTINEL-TOKEN-9a731cda-5e1f0b77c4d2";
const GOOD_SOCKET = "\\\\.\\pipe\\LOCAL\\cc-msg-0123456789abcdef0123456789abcdef";

let tripped = [];
afterEach(() => {
  const hits = tripped;
  tripped = [];
  assert.deepEqual(hits, [], `code under test reached a real transport: ${JSON.stringify(hits)}`);
});

function load({ execFile, timers } = {}) {
  const logged = [];
  const fakeConsole = {
    log: (...a) => logged.push(a.join(" ")),
    error: (...a) => logged.push(a.join(" ")),
    warn: (...a) => logged.push(a.join(" ")),
  };
  const tripFetch = (url) => {
    tripped.push(`fetch ${url}`);
    throw new Error(`TRIPWIRE: real fetch to ${url}`);
  };
  const tripExecFile = (file, args) => {
    tripped.push(`execFile ${file} ${JSON.stringify(args)}`);
    throw new Error(`TRIPWIRE: real execFile ${file}`);
  };
  const mod = new Function(
    "API_BASE", "console", "fetch", "execFile", "setTimeout", "clearTimeout",
    `${builderSrc}\n${BLOCK}\nreturn { buildRegisterPayload, selfRegisterTerminal, getProcessImageName,
       postClaimedCredentials, isClarionEmbedded, getClaimed: () => claimedTerminalName,
       MT_HEALTH_SERVICE_MARKER, CA_HEALTH_RETRY_DELAYS_MS };`,
  )(REAL_API_BASE, fakeConsole, tripFetch, execFile || tripExecFile,
    timers ? timers.setTimeout : setTimeout, timers ? timers.clearTimeout : clearTimeout);
  return { mod, logged };
}

// Rejects if the promise has not settled in `ms`: a hang fails the test rather than the suite.
function within(promise, ms = 2000) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`did not settle within ${ms}ms`)), ms); }),
  ]).finally(() => clearTimeout(timer));
}

const ENV = {
  MULTITERMINAL_NAME: "Alice",
  MULTITERMINAL_DOC_ID: "doc-123",
  MULTITERMINAL_LAUNCH_NONCE: "nonce-abc",
};

test("the extracted functions are real", () => {
  assert.ok(builderSrc.length > 200, "buildRegisterPayload extraction is suspiciously short");
  assert.ok(selfRegSrc.length > 300, "selfRegisterTerminal extraction is suspiciously short");
  assert.ok(blockStart >= 0 && BLOCK.length > 6000, `claimed-identity block missing or short (${BLOCK.length})`);
  assert.ok(BLOCK.includes(selfRegSrc.trim()), "selfRegisterTerminal is not inside the claimed-identity block");
});

// ── Ported from 54005ee7 (cb94e2e, d2077c4) ────────────────────────────────────────────────────────

test("buildRegisterPayload carries name, env docId, nonce and ownerPid", () => {
  const { mod } = load();
  assert.deepEqual(mod.buildRegisterPayload("Alice", "made-up", ENV, 4242), {
    name: "Alice",
    docId: "doc-123",
    nonce: "nonce-abc",
    ownerPid: 4242,
  });
  // No env docId: the argument is the fallback. No nonce or pid: those keys are absent, not empty.
  assert.deepEqual(mod.buildRegisterPayload("Bob", "arg-doc", {}, 0), { name: "Bob", docId: "arg-doc" });
});

test("register_terminal builds its body with the shared builder", () => {
  const start = src.indexOf('      case "register_terminal": {');
  assert.ok(start >= 0, "register_terminal handler missing");
  const body = code(src.slice(start, src.indexOf("\n      case ", start + 10)));
  assert.match(body, /const regPayload = buildRegisterPayload\(args\.name, args\.docId, process\.env, process\.ppid\);/);
  assert.match(body, /apiCall\("\/api\/messaging\/register", "POST", regPayload\)/);
});

test("self-registration posts the same body register_terminal would", async () => {
  const { mod } = load();
  const calls = [];
  const outcome = await within(mod.selfRegisterTerminal(ENV, 4242, async (endpoint, body) => {
    calls.push({ endpoint, body });
    return { terminalId: "t1" };
  }));
  assert.equal(outcome, "registered");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].endpoint, "/api/messaging/register");
  assert.deepEqual(calls[0].body, mod.buildRegisterPayload("Alice", undefined, ENV, 4242));
});

test("self-registration is skipped without both name and docId, and for an IDE that inherited an MT pane's env", async () => {
  const { mod } = load();
  let posted = 0;
  const post = async () => { posted++; return {}; };
  assert.equal(await within(mod.selfRegisterTerminal({ MULTITERMINAL_NAME: "Alice" }, 1, post)), "skipped");
  assert.equal(await within(mod.selfRegisterTerminal({ MULTITERMINAL_DOC_ID: "doc-123" }, 1, post)), "skipped");
  assert.equal(await within(mod.selfRegisterTerminal({}, 1, post)), "skipped");
  // Inside the Clarion IDE addin the environment may be inherited from a MultiTerminal shell.
  assert.equal(await within(mod.selfRegisterTerminal({ ...ENV, CLARION_ASSISTANT_EMBEDDED: "1" }, 1, post)), "skipped");
  assert.equal(posted, 0);
});

test("every failure is swallowed: MT down, a refusal, a synchronous throw", async () => {
  const { mod } = load();
  const down = async () => { throw new Error("MultiTerminal isn't running on http://localhost:5050"); };
  const refused = async () => { const e = new Error("API error: 400"); e.status = 400; throw e; };
  const syncThrow = () => { throw new TypeError("boom"); };
  for (const post of [down, refused, syncThrow]) {
    assert.equal(await within(mod.selfRegisterTerminal(ENV, 1, post)), "failed");
  }
});

// ── Case 1 credentials post (ticket 9a731cda, pipeline run 1) ───────────────────────────────────────

const PANE_ENV = {
  ...ENV,
  CLAUDE_CODE_SESSION_ID: "sess-pane",
  CLAUDE_CODE_MESSAGING_SOCKET: GOOD_SOCKET,
  CLAUDE_CODE_MESSAGING_TOKEN: SENTINEL_TOKEN,
};

function paneDeps() {
  const seq = [];
  const creds = [];
  const deps = {
    fetchImpl: async (url, opts) => { seq.push(`fetch ${url}`); creds.push(JSON.parse(opts.body)); return { ok: true, status: 200 }; },
    stepTimeoutMs: 100,
  };
  const post = async (endpoint) => { seq.push(`post ${endpoint}`); return { terminalId: "t1" }; };
  return { seq, creds, deps, post };
}

test("MT pane: after the broker ACCEPTS the refresh, the launch name's credentials are posted, with ownerPid and nonce", async () => {
  const { mod, logged } = load();
  const p = paneDeps();
  assert.equal(await within(mod.selfRegisterTerminal(PANE_ENV, 4242, p.post, p.deps)), "registered");
  assert.deepEqual(p.seq, ["post /api/messaging/register", "fetch http://localhost:5050/api/messaging/credentials"]);
  assert.deepEqual(p.creds[0], {
    name: "Alice", sessionId: "sess-pane", socket: GOOD_SOCKET, token: SENTINEL_TOKEN, ownerPid: 4242, nonce: "nonce-abc",
  });
  // The launch name is MT's to manage: it is not recorded as this process's claim.
  assert.equal(mod.getClaimed(), null);
  assert.ok(logged.some((l) => /native credentials: sent/.test(l)), JSON.stringify(logged));
  for (const line of logged) assert.ok(!line.includes(SENTINEL_TOKEN), `token leaked into: ${line}`);
});

test("MT pane: a refused or failed refresh posts no credentials", async () => {
  const refused = async () => { const e = new Error("API error: 409 — name held"); e.status = 409; throw e; };
  const down = async () => { throw new Error("MultiTerminal isn't running"); };
  const syncThrow = () => { throw new TypeError("boom"); };
  for (const reg of [refused, down, syncThrow]) {
    const { mod, logged } = load();
    const p = paneDeps();
    assert.equal(await within(mod.selfRegisterTerminal(PANE_ENV, 4242, reg, p.deps)), "failed");
    assert.deepEqual(p.seq, [], "credentials were posted after a failed registration");
    for (const line of logged) assert.ok(!line.includes(SENTINEL_TOKEN), `token leaked into: ${line}`);
  }
});

test("MT pane: a 409 or a hang on the credentials post is swallowed; the registration stands", async () => {
  for (const fetchImpl of [async () => ({ ok: false, status: 409 }), () => new Promise(() => {})]) {
    const { mod } = load();
    const p = paneDeps();
    p.deps.fetchImpl = fetchImpl;
    assert.equal(await within(mod.selfRegisterTerminal(PANE_ENV, 4242, p.post, p.deps)), "registered");
  }
});

test("main() starts self-registration without awaiting it, on the short timeouts", () => {
  const body = code(topLevelFunction("async function main("));
  assert.ok(body.length > 200, "main() extraction is suspiciously short");
  assert.match(body, /selfRegisterTerminal\(process\.env, process\.ppid,/);
  assert.match(body, /apiCall\(endpoint, "POST", body, SELF_REGISTER_TIMEOUT_MS\)/);
  assert.match(body, /get: \(endpoint\) => apiCall\(endpoint, "GET", null, HEALTH_TIMEOUT_MS\)/);
  assert.doesNotMatch(body, /await\s+selfRegisterTerminal/);
  assert.doesNotMatch(body, /selfRegisterTerminal\([^;]*\)\s*\.then/);
});

// ── Amendment (a): the shared placeholder is never self-registered ──────────────────────────────────

test("the shared placeholder 'Unassigned' is never self-registered, in any case, on either branch", async () => {
  const { mod } = load();
  const h = harness();
  for (const placeholder of ["Unassigned", "unassigned", "UNASSIGNED"]) {
    // MT-pane branch.
    assert.equal(await within(mod.selfRegisterTerminal({ ...ENV, MULTITERMINAL_NAME: placeholder }, 1, h.post, h.deps)), "skipped");
    // CA branch, with every other condition satisfied.
    assert.equal(await within(mod.selfRegisterTerminal({ ...CA_ENV, MULTITERMINAL_NAME: placeholder }, 777, h.post, h.deps)), "skipped");
  }
  assert.deepEqual(h.seq, []);
  // Discriminator: the same two calls with a real name do register.
  assert.equal(await within(mod.selfRegisterTerminal(ENV, 1, h.post, h.deps)), "registered");
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
});

// ── ClarionAssistant tab branch (ticket 9a731cda item 5) ───────────────────────────────────────────

const CA_ENV = {
  CLARION_ASSISTANT_EMBEDDED: "1",
  MULTITERMINAL_NAME: "CA-foo",
  CLAUDE_CODE_SESSION_ID: "sess-ca",
  CLAUDE_CODE_MESSAGING_SOCKET: GOOD_SOCKET,
  CLAUDE_CODE_MESSAGING_TOKEN: SENTINEL_TOKEN,
};

// HealthIdentity.ServiceMarker, hard-coded here on purpose: "the MT marker matches HealthIdentity.cs"
// below reads the C# file, so a change on either side goes red there.
const MT_MARKER = "multiterminal-rest-api";
const mtHealth = (capabilities) => ({ service: MT_MARKER, status: "ok", capabilities });
const HEALTH_GETS = (n) => Array(n).fill("get /api/health");
const SCHEDULE = [2000, 4000, 8000, 15000, 30000];

// One sequence log across every dependency, so ordering and "nothing else happened" are both visible.
// `sleep` is injected and resolves at once, recording each requested wait; nothing waits in real time.
function harness({
  health = mtHealth(["ca-embedded-v1"]),
  image = "claude.exe",
  register = async () => ({ terminalId: "t-ca" }),
  credOk = true,
  credStatus = 200,
} = {}) {
  const seq = [];
  const bodies = {};
  const sleeps = [];
  const post = (endpoint, body) => {
    seq.push(`post ${endpoint}`);
    bodies.register = JSON.parse(JSON.stringify(body)); // what goes on the wire
    return register(endpoint, body);
  };
  const deps = {
    get: (endpoint) => {
      seq.push(`get ${endpoint}`);
      return typeof health === "function" ? health() : Promise.resolve(health);
    },
    imageName: (pid) => {
      seq.push(`image ${pid}`);
      return typeof image === "function" ? image(pid) : Promise.resolve(image);
    },
    fetchImpl: async (url, opts) => {
      seq.push(`fetch ${url}`);
      bodies.credentials = JSON.parse(opts.body);
      return { ok: credOk, status: credStatus };
    },
    sleep: async (ms) => { sleeps.push(ms); },
    stepTimeoutMs: 100,
  };
  return { seq, bodies, sleeps, post, deps };
}

test("CA: capability + claude.exe registers name-only with ownerPid, THEN posts credentials, and records the claim", async () => {
  const { mod } = load();
  const h = harness();
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
  assert.deepEqual(h.seq, [
    "get /api/health",
    "image 777",
    "post /api/messaging/register",
    "fetch http://localhost:5050/api/messaging/credentials",
  ]);
  assert.deepEqual(h.bodies.register, { name: "CA-foo", ownerPid: 777 });
  assert.deepEqual(h.bodies.credentials, {
    name: "CA-foo", sessionId: "sess-ca", socket: GOOD_SOCKET, token: SENTINEL_TOKEN, ownerPid: 777,
  });
  assert.equal(mod.getClaimed(), "CA-foo");
});

test("CA: an inherited launch nonce is not this tab's, and is sent neither to register nor with credentials", async () => {
  const { mod } = load();
  const h = harness();
  const env = { ...CA_ENV, MULTITERMINAL_LAUNCH_NONCE: "inherited-nonce" };
  assert.equal(await within(mod.selfRegisterTerminal(env, 777, h.post, h.deps)), "registered");
  assert.ok(!("nonce" in h.bodies.register) && !("docId" in h.bodies.register), JSON.stringify(h.bodies.register));
  assert.ok(!("nonce" in h.bodies.credentials), JSON.stringify(h.bodies.credentials));
});

test("CA: EMBEDDED with a docId (IDE inherited an MT pane's env) makes no call at all", async () => {
  const { mod } = load();
  const h = harness();
  assert.equal(await within(mod.selfRegisterTerminal({ ...CA_ENV, MULTITERMINAL_DOC_ID: "doc-pane" }, 777, h.post, h.deps)), "skipped");
  assert.deepEqual(h.seq, []);
  assert.equal(mod.getClaimed(), null);
  // Discriminator: the identical env minus the docId does register.
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
  assert.ok(h.seq.includes("post /api/messaging/register"));
});

test("CA: capabilities missing, not an array, or without ca-embedded-v1 -> health only, retried, then given up", async () => {
  // A string capabilities value would pass a bare .includes(); only an array counts.
  for (const health of [{ service: MT_MARKER, status: "ok" }, mtHealth([]), mtHealth(["other-v1"]),
    mtHealth("ca-embedded-v1"), mtHealth(["CA-EMBEDDED-V1"])]) {
    const { mod } = load();
    const h = harness({ health });
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "no-capability", JSON.stringify(health));
    assert.deepEqual(h.seq, HEALTH_GETS(6), JSON.stringify(health));
    assert.deepEqual(h.sleeps, SCHEDULE);
    assert.equal(mod.getClaimed(), null);
  }
});

test("CA: health 404, bad JSON or a hang forever is retried on the bounded schedule, then given up with one line", async () => {
  const notFound = () => { const e = new Error("API error: 404 Not Found"); e.status = 404; return Promise.reject(e); };
  const badJson = () => Promise.reject(new SyntaxError("Unexpected token < in JSON"));
  const refused = () => Promise.reject(new Error("MultiTerminal isn't running on http://localhost:5050"));
  const hang = () => new Promise(() => {});
  for (const health of [notFound, badJson, refused, hang]) {
    const { mod, logged } = load();
    const h = harness({ health });
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "no-capability");
    assert.deepEqual(h.seq, HEALTH_GETS(6));
    assert.deepEqual(h.sleeps, SCHEDULE);
    assert.ok(SCHEDULE.reduce((a, b) => a + b, 0) <= 60000, "retry budget exceeds 60s");
    assert.equal(logged.length, 1, `expected one give-up line, got ${JSON.stringify(logged)}`);
    assert.match(logged[0], /gave up after 6 attempts/);
  }
});

// ── CA retry and fingerprint (ticket 9a731cda, pipeline run 1) ─────────────────────────────────────

test("CA: capability absent twice, then present -> registers on the third health attempt", async () => {
  const { mod } = load();
  let n = 0;
  const h = harness({ health: () => Promise.resolve(++n < 3 ? mtHealth([]) : mtHealth(["ca-embedded-v1"])) });
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
  assert.deepEqual(h.seq, [
    ...HEALTH_GETS(3), "image 777", "post /api/messaging/register",
    "fetch http://localhost:5050/api/messaging/credentials",
  ]);
  assert.deepEqual(h.sleeps, [2000, 4000]);
  assert.equal(mod.getClaimed(), "CA-foo");
});

test("CA: unreachable twice, then healthy -> registers (an MT that starts after the tab)", async () => {
  const { mod } = load();
  let n = 0;
  const h = harness({ health: () => (++n < 3 ? Promise.reject(new Error("ECONNREFUSED")) : Promise.resolve(mtHealth(["ca-embedded-v1"]))) });
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
  assert.deepEqual(h.sleeps, [2000, 4000]);
});

test("CA: definitive refusals are not retried: register refused, wrong parent, wrong service marker", async () => {
  const cases = [
    [{ register: async () => { const e = new Error("API error: 409 — name held"); e.status = 409; throw e; } }, "refused",
      ["get /api/health", "image 777", "post /api/messaging/register"]],
    [{ image: "node.exe" }, "wrong-parent", ["get /api/health", "image 777"]],
    [{ health: { service: "something-else", capabilities: ["ca-embedded-v1"] } }, "not-multiterminal", ["get /api/health"]],
  ];
  for (const [opts, outcome, seq] of cases) {
    const { mod } = load();
    const h = harness(opts);
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), outcome);
    assert.deepEqual(h.seq, seq, outcome);
    assert.deepEqual(h.sleeps, [], `${outcome} was retried`);
    assert.equal(mod.getClaimed(), null);
  }
});

test("CA: a health body without MT's exact service marker is not trusted, even when it lists ca-embedded-v1", async () => {
  const caps = ["ca-embedded-v1"];
  for (const health of [{ capabilities: caps }, { service: "MultiTerminal-REST-API", capabilities: caps },
    { service: ` ${MT_MARKER}`, capabilities: caps }, { service: "some-other-api", capabilities: caps },
    { app: "MultiTerminal", capabilities: caps }, null, "ok", [MT_MARKER]]) {
    const { mod } = load();
    const h = harness({ health });
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "not-multiterminal", JSON.stringify(health));
    assert.deepEqual(h.seq, ["get /api/health"], JSON.stringify(health));
    assert.equal(mod.getClaimed(), null);
  }
  // Discriminator: the same capabilities under the exact marker register.
  const { mod } = load();
  const h = harness({ health: { service: MT_MARKER, capabilities: caps } });
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
});

test("the MT marker matches HealthIdentity.ServiceMarker in Services/Startup/HealthIdentity.cs", () => {
  const cs = readFileSync(path.join(path.dirname(indexPath), "..", "Services", "Startup", "HealthIdentity.cs"), "utf8");
  const m = cs.match(/public const string ServiceMarker = "([^"]*)";/);
  assert.ok(m, "ServiceMarker constant not found in HealthIdentity.cs");
  const { mod } = load();
  assert.equal(mod.MT_HEALTH_SERVICE_MARKER, m[1]);
  assert.equal(MT_MARKER, m[1]);
});

test("CA: the default retry wait uses unref'd timers on the documented schedule", async () => {
  // Fake timers: waits of the schedule's lengths fire at once; every other timer (the step deadlines)
  // never fires. Health rejects immediately, so no deadline is needed to end an attempt.
  const made = [];
  const timers = {
    setTimeout: (fn, ms) => {
      const t = { ms, unrefed: false, unref() { this.unrefed = true; return this; } };
      made.push(t);
      if (SCHEDULE.includes(ms)) setImmediate(fn);
      return t;
    },
    clearTimeout: () => {},
  };
  const { mod } = load({ timers });
  const h = harness({ health: () => Promise.reject(new Error("ECONNREFUSED")) });
  delete h.deps.sleep;
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "no-capability");
  const waits = made.filter((t) => SCHEDULE.includes(t.ms));
  assert.deepEqual(waits.map((t) => t.ms), SCHEDULE);
  assert.ok(made.length > waits.length, "expected step deadlines too");
  assert.ok(made.every((t) => t.unrefed), "a timer would keep the process alive");
  assert.deepEqual(mod.CA_HEALTH_RETRY_DELAYS_MS, SCHEDULE);
});

// ── isClarionEmbedded (MIRRORED in the plugin's hooks/embedded-session.js) ──────────────────────────

test("isClarionEmbedded: the plugin's truth table", () => {
  const { mod } = load();
  for (const v of ["1", "true", "yes", "TRUE"]) {
    assert.equal(mod.isClarionEmbedded({ CLARION_ASSISTANT_EMBEDDED: v }), true, JSON.stringify(v));
  }
  for (const v of ["", "0", "false", "FALSE", " 0 ", undefined]) {
    assert.equal(mod.isClarionEmbedded({ CLARION_ASSISTANT_EMBEDDED: v }), false, JSON.stringify(v));
  }
  assert.equal(mod.isClarionEmbedded({}), false);
  assert.equal(mod.isClarionEmbedded(null), false);
});

test("both call sites honour it: '0'/'false' without a docId is not a CA tab; with a docId it is an MT pane", async () => {
  for (const v of ["0", "false", " 0 "]) {
    const { mod } = load();
    const h = harness();
    // No DOC_ID: not a CA tab, and not an MT pane either -> nothing at all.
    assert.equal(await within(mod.selfRegisterTerminal({ ...CA_ENV, CLARION_ASSISTANT_EMBEDDED: v }, 777, h.post, h.deps)), "skipped", v);
    assert.deepEqual(h.seq, [], v);
    // With a DOC_ID: the MT-pane refresh, not the "IDE inherited an MT env" skip.
    assert.equal(await within(mod.selfRegisterTerminal({ ...CA_ENV, CLARION_ASSISTANT_EMBEDDED: v, MULTITERMINAL_DOC_ID: "doc-9" }, 777, h.post, h.deps)), "registered", v);
    assert.equal(h.bodies.register.docId, "doc-9");
  }
  // Discriminator: "yes" is embedded, so the same env takes the CA branch.
  const { mod } = load();
  const h = harness();
  assert.equal(await within(mod.selfRegisterTerminal({ ...CA_ENV, CLARION_ASSISTANT_EMBEDDED: "yes" }, 777, h.post, h.deps)), "registered");
  assert.equal(h.seq[0], "get /api/health");
});

test("CA: parent image must be claude.exe (any case); node.exe, volta.exe, unknown or a hang -> no register", async () => {
  for (const image of ["node.exe", "volta.exe", null, "claude.exe.bak", () => new Promise(() => {})]) {
    const { mod } = load();
    const h = harness({ image });
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "wrong-parent", String(image));
    assert.deepEqual(h.seq, ["get /api/health", "image 777"], String(image));
    assert.equal(mod.getClaimed(), null);
  }
  const { mod } = load();
  const h = harness({ image: "Claude.EXE" });
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
});

test("CA: a refused, failed or hung registration posts no credentials and records no claim", async () => {
  const refused = async () => { const e = new Error("API error: 400 — name in use"); e.status = 400; throw e; };
  const syncThrow = () => { throw new TypeError("boom"); };
  const hang = () => new Promise(() => {});
  for (const register of [refused, syncThrow, hang]) {
    const { mod } = load();
    const h = harness({ register });
    assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "refused");
    assert.deepEqual(h.seq, ["get /api/health", "image 777", "post /api/messaging/register"]);
    assert.equal(mod.getClaimed(), null);
  }
});

test("CA: a 409 on the credentials post is quiet; the registration stands", async () => {
  const { mod } = load();
  const h = harness({ credOk: false, credStatus: 409 });
  assert.equal(await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps)), "registered");
  assert.equal(h.seq.length, 4);
  assert.equal(mod.getClaimed(), "CA-foo");
});

test("CA: the messaging token never appears in a log line or a return value, on any path", async () => {
  const scenarios = [
    {}, { credOk: false, credStatus: 409 }, { health: mtHealth([]) }, { health: { capabilities: ["ca-embedded-v1"] } },
    { image: "node.exe" },
    { register: async () => { throw new Error("API error: 400"); } },
  ];
  for (const opts of scenarios) {
    const { mod, logged } = load();
    const h = harness(opts);
    const result = await within(mod.selfRegisterTerminal(CA_ENV, 777, h.post, h.deps));
    assert.ok(logged.length > 0, "expected a reason on stderr");
    for (const line of [...logged, String(result)]) {
      assert.ok(!line.includes(SENTINEL_TOKEN), `token leaked into: ${line}`);
    }
  }
});

// ── getProcessImageName (ticket 9a731cda item 11) ──────────────────────────────────────────────────

test("getProcessImageName: one tasklist call, filtered to the pid, windowless and time-bounded", async () => {
  const { mod } = load();
  const calls = [];
  const run = async (file, args, opts) => {
    calls.push({ file, args, opts });
    return '"claude.exe","4242","Console","1","123,456 K"\r\n';
  };
  assert.equal(await within(mod.getProcessImageName(4242, run)), "claude.exe");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].file, "tasklist");
  assert.deepEqual(calls[0].args, ["/FI", "PID eq 4242", "/FO", "CSV", "/NH"]);
  assert.equal(calls[0].opts.windowsHide, true);
  assert.ok(calls[0].opts.timeout > 0 && calls[0].opts.timeout <= 3000);
});

test("getProcessImageName: quoted fields parse, including escaped quotes; a row for another pid is ignored", async () => {
  const { mod } = load();
  assert.equal(await mod.getProcessImageName(7, async () => '"we""ird, name.exe","7","Console","1","1 K"'), 'we"ird, name.exe');
  assert.equal(await mod.getProcessImageName(7, async () => '"claude.exe","77","Console","1","1 K"'), null);
});

test("getProcessImageName: no match, a throwing runner, or a bad pid -> null", async () => {
  const { mod } = load();
  assert.equal(await mod.getProcessImageName(4242, async () => "INFO: No tasks are running which match the specified criteria.\r\n"), null);
  assert.equal(await mod.getProcessImageName(4242, async () => { throw new Error("spawn tasklist ENOENT"); }), null);
  assert.equal(await mod.getProcessImageName(4242, () => { throw new Error("sync"); }), null);
  let ran = 0;
  const counting = async () => { ran++; return ""; };
  for (const pid of [0, -1, 1.5, "4242", undefined, null]) {
    assert.equal(await mod.getProcessImageName(pid, counting), null);
  }
  assert.equal(ran, 0, "a non-pid reached the runner");
});

test("getProcessImageName: the default runner goes through execFile (never a shell) and reads its stdout", async () => {
  const seen = [];
  const fakeExecFile = (file, args, opts, cb) => {
    seen.push({ file, args, opts });
    cb(null, '"claude.exe","99","Console","1","10 K"\r\n');
  };
  const { mod } = load({ execFile: fakeExecFile });
  assert.equal(await within(mod.getProcessImageName(99)), "claude.exe");
  assert.equal(seen.length, 1);
  assert.equal(seen[0].file, "tasklist");
  assert.equal(seen[0].opts.windowsHide, true);
});
