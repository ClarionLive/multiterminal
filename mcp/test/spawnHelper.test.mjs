// Named-presence + contract pins for the `spawn_helper` tool (task 77d1182f).
//
// consistency.test.mjs proves every tool DEF has a HANDLER and vice versa — a set-equality
// check. It therefore cannot see the one regression this file exists for: the def and the
// handler being removed TOGETHER, which leaves the sets equal and the capability gone. Before
// 77d1182f no tool pointed at /api/spawn/terminal at all, and agents concluded spawning was
// impossible; this pins that it stays discoverable.
//
// It also pins the decision rule INSIDE the description. The rule ("a subagent is a worker, a
// helper is a peer; default to a subagent") is the safety rail for the tool: a helper used as a
// mere worker was measured strictly worse than a subagent. A tidy-up that trims the description
// to a one-liner would remove the rail while every other test stayed green.
//
// Same extract-and-assert-against-the-real-source contract as consistency.test.mjs: no server
// boot, no SDK import, just index.js text.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const DEF_MARKER = 'name: "spawn_helper"';
const HANDLER_MARKER = '      case "spawn_helper": {';

// The def block: from its name to the next tool's name.
function defBlock() {
  const start = src.indexOf(DEF_MARKER);
  assert.ok(start >= 0, `tool def ${DEF_MARKER} missing from index.js`);
  const next = src.indexOf('name: "', start + DEF_MARKER.length);
  return src.slice(start, next < 0 ? undefined : next);
}

// The handler block: from its case label to the next case label.
function handlerBlock() {
  const start = src.indexOf(HANDLER_MARKER);
  assert.ok(start >= 0, `dispatch handler ${HANDLER_MARKER.trim()} missing from index.js`);
  const next = src.indexOf("\n      case ", start + HANDLER_MARKER.length);
  return src.slice(start, next < 0 ? undefined : next);
}

test("spawn_helper has both a tool def and a dispatch handler", () => {
  assert.ok(src.includes(DEF_MARKER), "def missing");
  assert.ok(src.includes(HANDLER_MARKER), "handler missing (or its indent drifted from the 6-space form the consistency gate matches)");
});

test("spawn_helper handler calls the spawn endpoint and forwards the four fields plus spawnerName", () => {
  const h = handlerBlock();
  assert.ok(h.includes('apiCall("/api/spawn/terminal", "POST"'), "handler must POST /api/spawn/terminal");
  for (const field of ["agentName", "workingDir", "projectId", "initialPrompt", "spawnerName"]) {
    assert.ok(new RegExp(`\\b${field}:`).test(h), `handler body must forward '${field}'`);
  }
});

test("spawn_helper refuses a missing spawnerName instead of letting the API default it to the phone app", () => {
  const h = handlerBlock();
  assert.ok(/spawnerName is required/.test(h), "handler must refuse an omitted spawnerName");
  assert.ok(/isError:\s*true/.test(h), "the refusal must be an error result, not prose in a success");
});

test("spawn_helper requires agentName and spawnerName in its schema", () => {
  const d = defBlock();
  const m = d.match(/required:\s*\[([^\]]*)\]/);
  assert.ok(m, "def has no required[]");
  const required = [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]).sort();
  assert.deepEqual(required, ["agentName", "spawnerName"]);
});

test("spawn_helper's description carries the subagent-vs-helper decision rule", () => {
  const d = defBlock();
  // Each phrase is a load-bearing half of the rule; losing any one of them changes what an
  // agent reading the tool would conclude.
  for (const phrase of [
    "A SUBAGENT IS A WORKER; A SPAWNED HELPER IS A PEER",
    "Default to the Agent tool",
    "outlives your turn",
    "watch or steer",
    "MT identity",
    "own process environment",
    "DISAGREE",
    "Do not replace subagents wholesale",
  ]) {
    assert.ok(d.includes(phrase), `description lost the rule phrase: ${phrase}`);
  }
});

test("list_terminals renders the channel port the spawn_helper readiness instruction points at", () => {
  // Pipeline Run 2 (cross-model adversary): spawn_helper tells a caller its helper is
  // messageable "once list_terminals shows it with a channel port". Before this pin,
  // formatTerminals used channelPort only for a health heuristic and never printed it, so the
  // instruction sent agents to a field they could not see. Extract the REAL function body and
  // require both the port-present and port-absent renderings to be visible text.
  const start = src.indexOf("function formatTerminals(");
  assert.ok(start >= 0, "formatTerminals missing from index.js");
  const end = src.indexOf("\nfunction ", start + 1);
  const body = src.slice(start, end < 0 ? undefined : end);

  assert.ok(/channel:\s*port \$\{t\.channelPort\}/.test(body), "must render the channel port when present");
  assert.ok(/channel:\s*none/.test(body), "must render an explicit no-port state so a pre-registered pane is distinguishable from a live helper");
  assert.ok(/output \+= `• \$\{t\.name\}[^`]*\$\{channel\}/.test(body), "the channel text must be part of the per-terminal row, not a separate optional line");
});

test("spawn_helper's result text promises the same failure delivery the description does", () => {
  // Pipeline Run 3 (debugger LOW): the tool DESCRIPTION gained the "and in the Owner's inbox if
  // your spawnerName is not a live terminal" clause but the runtime RESULT text did not, so an
  // agent reading only the result was told the message lands solely in its own inbox. Both
  // surfaces must say the same thing, and neither may promise a "notification" — the store an
  // agent reads is the inbox (get_inbox).
  const d = defBlock();
  const h = handlerBlock();
  for (const surface of [["description", d], ["result text", h]]) {
    const [name, text] = surface;
    assert.ok(/get_inbox/.test(text), `${name} must name get_inbox as where the failure lands`);
    assert.ok(/Owner'?s inbox|in the Owner's/.test(text), `${name} must state the Owner-copy fallback`);
    assert.ok(!/get a notification instead/.test(text), `${name} must not promise a "notification" — that store is not agent-readable`);
  }
});

test("spawn_helper's description states the initialPrompt delivery contract", () => {
  const d = defBlock();
  assert.ok(d.includes("ONE prompt"), "must say the prompt arrives as one prompt");
  // Task 8b270b37: the helper collects the job (get_my_spawn_job), so nothing is typed and line breaks
  // now SURVIVE. The old
  // assertion required the description to warn they were collapsed, which is now false.
  assert.ok(/line breaks are kept/i.test(d), "must say line breaks are kept");
  assert.ok(!/line breaks become spaces|line breaks are collapsed/i.test(d), "must not claim line breaks are collapsed");
  assert.ok(d.includes("MULTITERMINAL_SPAWNER"), "must say what spawnerName becomes in the child");
});

test("no spawn_helper surface claims the helper runs /session-start", () => {
  // Live test 2026-09-14. Every agent-facing surface used to promise the helper "runs
  // /session-start and registers itself". It does not, and cannot: the plugin's SessionStart
  // hook short-circuits on MULTITERMINAL_SPAWNER ("Skip kanban/plan context for spawned
  // agents") and hands the helper a spawned-agent briefing INSTEAD of the auto-run instruction.
  // A caller who believed it waited for a startup menu that never appears.
  //
  // The claim survived four pipeline rounds because it was UNFALSIFIABLE until this ticket
  // landed: the old spawn path passed no --plugin-dir, so no hook ran at all and that branch
  // had never once executed for a spawned pane. Pin it now that it can be observed.
  for (const [name, text] of [["description", defBlock()], ["result text", handlerBlock()]]) {
    assert.ok(
      !/runs \/session-start|\/session-start menu|to run \/session-start/.test(text),
      `${name} must not claim a spawned helper runs /session-start or shows its menu`,
    );
  }
});

test("spawn_helper states prompt delivery, and that a long silence is a failure", () => {
  // Task 7806024f made delivery fire on the helper registering a channel port — MEASURED 4.9s from
  // spawn to delivery (2026-09-15, deployed 977ab2d), replacing the ~120s fallback every spawned
  // helper used to wait out. The surfaces previously told callers to EXPECT ~120s and to read two
  // minutes of silence as normal; that is now false and inverted — a wait of minutes means the
  // helper never came alive.
  //
  // ⚠️ Do NOT weaken this back to /120s|120 s|two minutes/. That regex was satisfied by the
  // corrected text's mention of the give-up BOUND, so it stayed green across the very change it
  // existed to catch. Each surface must carry BOTH halves, asserted separately.
  for (const [name, text] of [["description", defBlock()], ["result text", handlerBlock()]]) {
    assert.ok(
      /within seconds|4\.9s/.test(text),
      `${name} must state that delivery is prompt, not a ~120s wait`);
    assert.ok(
      !/about 120s|two minutes as a failure|silence before then is normal/.test(text),
      `${name} still tells callers to expect the pre-7806024f ~120s delay`);
  }

  // The 120s itself must survive as the give-up bound — it is still the real deadline after which
  // the job is never sent and spawn_failed is sent. Dropping it would trade one wrong expectation
  // for no expectation at all.
  assert.ok(/120s/.test(defBlock()), "description must still document the 120s give-up bound");
});
