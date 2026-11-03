// GitHub #92: set_task_active, claim_task and update_task_plan tell the agent when the task has no
// checklist, because the Owner's Pending/Coding/Testing/Done view is empty until it adds items.
//
// House pattern (compactCap.test.mjs): read mcp/index.js as TEXT, extract the real helper and run it;
// never import index.js (that boots the server). Extraction is asserted non-vacuous. The handler
// facts are scoped to each handler's own body, comments stripped, so a comment naming the helper
// cannot satisfy them.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

function slice(startMarker, endMarker, label) {
  const a = src.indexOf(startMarker);
  assert.ok(a !== -1, `${label}: start marker not found in mcp/index.js`);
  const b = src.indexOf(endMarker, a + startMarker.length);
  assert.ok(b !== -1, `${label}: end marker not found in mcp/index.js`);
  return src.slice(a, b);
}

const block = slice("const EMPTY_CHECKLIST_NOTE", "const fetchTaskDetail", "emptyChecklistNote block");
assert.ok(block.includes("async function emptyChecklistNote"), "extraction lost emptyChecklistNote");
const { emptyChecklistNote, EMPTY_CHECKLIST_NOTE } = new Function(
  `${block}; return { emptyChecklistNote, EMPTY_CHECKLIST_NOTE };`,
)();

const detailWith = (total) => async () => ({ task: { id: "t1" }, checklistSummary: { total }, checklist: [] });

test("a task with no checklist items gets the note, on its own line", async () => {
  const note = await emptyChecklistNote("t1", detailWith(0));
  assert.equal(note, `\n${EMPTY_CHECKLIST_NOTE}`);
  assert.match(EMPTY_CHECKLIST_NOTE, /append_checklist_items/);
});

test("a task that has checklist items gets nothing", async () => {
  assert.equal(await emptyChecklistNote("t1", detailWith(3)), "");
});

test("a failed or odd lookup adds nothing and never throws", async () => {
  const failing = async () => { throw new Error("API error: 404"); };
  assert.equal(await emptyChecklistNote("t1", failing), "");
  assert.equal(await emptyChecklistNote("t1", async () => ({})), "");
  assert.equal(await emptyChecklistNote("t1", async () => null), "");
});

/** A handler's body, from its case marker to the next `case "`, with // and /* comments removed. */
function handlerBody(name) {
  const marker = `case "${name}": {`;
  const start = src.indexOf(marker);
  assert.ok(start !== -1, `handler ${name} not found`);
  const rest = src.slice(start + marker.length);
  const next = rest.search(/^ {6}case\s+"/m);
  const body = (next === -1 ? rest : rest.slice(0, next))
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/^\s*\/\/.*$/gm, "");
  assert.ok(body.length > 200, `handler ${name}: extraction looks empty (${body.length} chars)`);
  return body;
}

for (const name of ["set_task_active", "claim_task", "update_task_plan"]) {
  test(`${name} adds the note to what it returns`, () => {
    const body = handlerBody(name);
    assert.match(body, /await emptyChecklistNote\(args\.taskId, fetchTaskDetail\)/,
      `${name} no longer asks whether the task has a checklist`);
  });
}
