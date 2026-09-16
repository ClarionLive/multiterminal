// Contract pins for the `get_my_spawn_job` tool (task 8b270b37).
//
// A spawned helper PULLS its job with this tool; MT no longer pushes it. consistency.test.mjs proves
// def/handler parity but cannot see both being removed together, which would leave every spawned
// helper with no way to get its job while every other test stayed green.
//
// The property that matters most is that the tool can only ever collect the CALLER'S OWN job. The
// docId must come from the process environment MT launched the pane with, never from an argument:
// collecting consumes the job, so an argument would let one agent take another helper's work.
//
// Text is scanned with comments STRIPPED (see .claude/rules/verification-discipline.md): the handler
// carries comments explaining why it refuses args, and an unstripped "must not contain args." scan
// would be tripped by that explanation rather than by code.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const DEF_MARKER = 'name: "get_my_spawn_job"';
const HANDLER_MARKER = '      case "get_my_spawn_job": {';

function defBlock() {
  const start = src.indexOf(DEF_MARKER);
  assert.ok(start >= 0, `tool def ${DEF_MARKER} missing from index.js`);
  const next = src.indexOf('name: "', start + DEF_MARKER.length);
  return src.slice(start, next < 0 ? undefined : next);
}

function handlerBlock() {
  const start = src.indexOf(HANDLER_MARKER);
  assert.ok(start >= 0, `dispatch handler ${HANDLER_MARKER.trim()} missing from index.js`);
  const next = src.indexOf("\n      case ", start + HANDLER_MARKER.length);
  return src.slice(start, next < 0 ? undefined : next);
}

// Drops whole-line // comments only. That is enough for this handler, and deliberately simple: a
// general JS comment stripper would have to understand strings, and "//" occurs inside none of the
// handler's strings today. The length assertion below catches the day that stops being true badly
// enough to empty the body.
function code(block) {
  return block
    .split(/\r?\n/)
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
}

test("get_my_spawn_job has both a tool def and a dispatch handler", () => {
  assert.ok(src.includes(DEF_MARKER), "def missing");
  assert.ok(src.includes(HANDLER_MARKER), "handler missing (or its indent drifted from the 6-space form the consistency gate matches)");
});

test("the handler was actually extracted", () => {
  // Without this, a renamed marker would make every scan below run against a near-empty string and
  // the "does not contain" fact would pass vacuously.
  assert.ok(code(handlerBlock()).length > 800, "handler body is suspiciously short; extraction probably failed");
});

test("the handler POSTs the collect endpoint", () => {
  const h = code(handlerBlock());
  assert.ok(/apiCall\(\s*`\/api\/spawn\/job\/\$\{seg\([^`]*\)\}\/collect`,\s*"POST"/.test(h),
    "handler must POST /api/spawn/job/{docId}/collect with the docId passed through seg()");
});

// Pipeline Run 1: the endpoint refuses a docId without its own pane's launch nonce. Without the header
// every collect is a 401, so every spawned helper would fail to get its job while this file's other facts
// stayed green. The nonce, like the docId, must come from the environment only.
test("the handler sends the pane's launch nonce from the environment in the nonce header", () => {
  const h = code(handlerBlock());
  assert.ok(/const launchNonce = process\.env\.MULTITERMINAL_LAUNCH_NONCE;/.test(h),
    "handler must read MULTITERMINAL_LAUNCH_NONCE from the environment");
  assert.ok(/\{\s*"X-MultiTerminal-Launch-Nonce":\s*launchNonce\s*\}/.test(h),
    "handler must pass the nonce to apiCall in the X-MultiTerminal-Launch-Nonce header");
});

test("apiCall actually sends caller-supplied headers", () => {
  // The handler passing a header object proves nothing if apiCall drops it.
  const start = src.indexOf("async function apiCall(");
  assert.ok(start >= 0, "apiCall missing");
  const head = src.slice(start, start + 400);
  assert.ok(/extraHeaders/.test(head.split("\n")[0]), "apiCall must accept an extraHeaders parameter");
  assert.ok(/headers:\s*\{[^}]*\.\.\.\(extraHeaders \|\| \{\}\)/.test(head), "apiCall must spread extraHeaders into the request headers");
});

test("the docId comes from the environment and never from arguments", () => {
  const h = code(handlerBlock());
  assert.ok(h.includes("process.env.MULTITERMINAL_DOC_ID"), "handler must read MULTITERMINAL_DOC_ID from the environment");
  assert.ok(!/\bargs\b/.test(h), "handler must not read args at all: an argument docId would let an agent collect another helper's job");
  assert.ok(/properties:\s*\{\s*\}/.test(defBlock()), "the tool must declare no input properties");
});

test("the handler renders all four outcomes the endpoint can return", () => {
  const h = code(handlerBlock());
  assert.ok(h.includes('r.status === "collected"'), "must handle collected");
  assert.ok(h.includes('r.status === "refetched"'), "must handle refetched, or a re-fetched job is reported as 'no job'");
  assert.ok(/do NOT start over/.test(h), "refetched must tell a helper that already started not to redo the job");
  assert.ok(h.includes('r.status === "already_collected"'), "must handle already_collected");
  assert.ok(/No job is waiting/.test(h), "must render the no_job fallthrough");
  assert.ok(/do NOT guess/.test(h), "already_collected must tell the helper not to reconstruct a job it cannot remember");
});
