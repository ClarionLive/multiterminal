// Contract pins for the `close_helper` tool (task 7f389704).
//
// A PM closes a helper it spawned. The server decides WHO may close by the caller's launch nonce, so
// the property that matters on this side is where the nonce comes from: this process's environment,
// which MT set when it launched the pane, and never an argument. An argument would let any agent
// present any pane's nonce, and the server's check would be checking nothing. The same header must be
// sent by spawn_helper, because that is how the server learns which pane spawned the helper at all.
//
// Same extract-from-the-real-source approach as spawnJob.test.mjs. Code is scanned with whole-line //
// comments STRIPPED (see .claude/rules/verification-discipline.md), and the input schema is pinned by
// its property NAMES rather than by searching for words that a comment could satisfy.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

function defBlock(name) {
  const marker = `name: "${name}"`;
  const start = src.indexOf(marker);
  assert.ok(start >= 0, `tool def ${marker} missing from index.js`);
  const next = src.indexOf('name: "', start + marker.length);
  return src.slice(start, next < 0 ? undefined : next);
}

function handlerBlock(name) {
  const marker = `      case "${name}": {`;
  const start = src.indexOf(marker);
  assert.ok(start >= 0, `dispatch handler ${marker.trim()} missing from index.js`);
  const next = src.indexOf("\n      case ", start + marker.length);
  return src.slice(start, next < 0 ? undefined : next);
}

function code(block) {
  return block
    .split(/\r?\n/)
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
}

test("close_helper has both a tool def and a dispatch handler", () => {
  assert.ok(src.includes('name: "close_helper"'), "def missing");
  assert.ok(src.includes('      case "close_helper": {'), "handler missing (or its indent drifted from the 6-space form the consistency gate matches)");
});

test("the close_helper handler was actually extracted", () => {
  // Guards the scans below against running on a near-empty string after a marker rename.
  assert.ok(code(handlerBlock("close_helper")).length > 600, "handler body is suspiciously short; extraction probably failed");
});

test("close_helper POSTs the close endpoint with the nonce read from the environment", () => {
  const body = code(handlerBlock("close_helper"));
  assert.match(body, /"\/api\/spawn\/terminal\/close",\s*"POST"/);
  assert.match(body, /const closerNonce = process\.env\.MULTITERMINAL_LAUNCH_NONCE;/);
  assert.match(body, /"X-MultiTerminal-Launch-Nonce": closerNonce/);
});

test("close_helper takes only terminalName, so a nonce or docId can never arrive as an argument", () => {
  const def = defBlock("close_helper");
  const props = def.slice(def.indexOf("properties: {"));
  const names = [...props.matchAll(/^\s{12}(\w+): \{/gm)].map((m) => m[1]);
  assert.deepEqual(names, ["terminalName"]);
});

test("spawn_helper sends its pane's nonce, so the server can record which pane spawned the helper", () => {
  const body = code(handlerBlock("spawn_helper"));
  assert.match(body, /const spawnerNonce = process\.env\.MULTITERMINAL_LAUNCH_NONCE;/);
  assert.match(body, /"X-MultiTerminal-Launch-Nonce": spawnerNonce/);
});
