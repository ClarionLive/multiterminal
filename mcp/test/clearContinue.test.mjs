// clear_my_context carries a continueWith note into the fresh session (ticket cc2e51ff).
//
// A PM's brief "write your notes, clear_my_context, then claim card X" lost its second half every time:
// the tool only typed /clear, and the fresh session got nothing of the conversation. The handler now sends
// continueWith as continueAfterClear on the /clear submit POST; MT holds it for the pane and types it after
// the clear. A blank one is not sent, and the field is declared in the tool's schema (an undeclared
// argument is not offered to the model).
//
// Same house pattern as compactContinue.test.mjs: index.js read as text, comments stripped, extraction
// asserted non-vacuous. Falsified by sending { text: "/clear" } alone (first test red) and by deleting the
// schema property (second test red).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

function stripComments(text) {
  return text.split(/\r?\n/).filter((l) => !/^\s*\/\//.test(l)).join("\n");
}

function handler(name) {
  const start = src.indexOf(`      case "${name}": {`);
  assert.ok(start !== -1, `${name} handler not found`);
  const body = src.slice(start, src.indexOf("\n      case ", start + 10));
  assert.ok(body.length > 500, `${name} handler extraction looks vacuous (${body.length} chars)`);
  return stripComments(body);
}

function toolDef(name) {
  const start = src.indexOf(`        name: "${name}",`);
  assert.ok(start !== -1, `${name} tool definition not found`);
  const body = src.slice(start, src.indexOf(`\n        name: "`, start + 10));
  assert.ok(body.length > 300, `${name} tool definition extraction looks vacuous (${body.length} chars)`);
  return stripComments(body);
}

test("clear_my_context sends continueWith as continueAfterClear on the /clear submit, and only when not blank", () => {
  const h = handler("clear_my_context");
  assert.match(h, /\/submit`, "POST", \{ text: "\/clear", continueAfterClear: clrContinue \}\)/);
  assert.match(h, /const clrContinue = typeof args\.continueWith === "string" && args\.continueWith\.trim\(\) \? args\.continueWith : undefined;/);
});

test("clear_my_context declares continueWith in its schema", () => {
  assert.match(toolDef("clear_my_context"), /continueWith: \{\s*type: "string",/);
});
