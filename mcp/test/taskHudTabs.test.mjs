// Tasks HUD tab choice (GitHub #54, task 9205ebf1).
//
// The terminal HUD's Tasks tab used to pick Active or Not Active only on its first payload, so when an
// agent activated a task (set_task_active) a HUD already on Not Active stayed there and nothing showed
// a task was active. nextHudTab in Controls/TaskHudPanel/task-hud.html now decides the tab for every
// payload.
//
// House pattern (compactCap.test.mjs): read the file as TEXT, extract the real function and run it.
// This lives under mcp/test only because that is where CI runs node tests; it tests a page script.
// Extraction is asserted non-vacuous.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const htmlPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "Controls", "TaskHudPanel", "task-hud.html");
const src = readFileSync(htmlPath, "utf8");

function extractFunction(name) {
  const start = src.indexOf(`function ${name}(`);
  assert.ok(start !== -1, `${name} not found in task-hud.html`);
  let depth = 0;
  for (let i = src.indexOf("{", start); i < src.length; i++) {
    if (src[i] === "{") depth++;
    else if (src[i] === "}" && --depth === 0) return src.slice(start, i + 1);
  }
  assert.fail(`unbalanced braces after ${name}`);
}

const fnSource = extractFunction("nextHudTab");
assert.ok(fnSource.length > 150, "extraction of nextHudTab looks empty");
const nextHudTab = new Function(`${fnSource}; return nextHudTab;`)();

test("first payload: Active when a task is active, else Not Active", () => {
  assert.equal(nextHudTab(null, null, "t1", null), "active");
  assert.equal(nextHudTab(null, null, null, null), "notactive");
});

test("an agent activating a task moves a HUD on Not Active to Active (the #54 bug)", () => {
  // Previous payload had no active task; now t1 is active, and the HUD did not ask for it.
  assert.equal(nextHudTab("notactive", null, "t1", null), "active");
  // Switching from one active task to another does the same.
  assert.equal(nextHudTab("notactive", "t1", "t2", null), "active");
});

test("an ordinary refresh leaves the tab the user chose", () => {
  // Same active task as before: the user's Not Active choice stands.
  assert.equal(nextHudTab("notactive", "t1", "t1", null), "notactive");
  assert.equal(nextHudTab("active", "t1", "t1", null), "active");
  // The active task went away: no task to show, so the tab is not forced anywhere.
  assert.equal(nextHudTab("active", "t1", null, null), "active");
  assert.equal(nextHudTab("notactive", null, null, null), "notactive");
});

test("the HUD's own Activate button still lands on Active", () => {
  assert.equal(nextHudTab("notactive", "t1", "t2", "t2"), "active");
  // A pending request for another task does not force Active while t1 just stays active.
  assert.equal(nextHudTab("notactive", "t1", "t1", "t9"), "notactive");
});
