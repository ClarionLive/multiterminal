// Contract pins for the optional note category (task 9a4a9d5e).
//
// add_project_note may pass a category; the server decides what to do with it (an existing category is
// used for a new note, an unknown one lands in General). The properties that matter on this side: the
// parameter is declared and OPTIONAL, the handler forwards it to the REST body only when given, and the
// reply surfaces the server's categoryNote so an agent learns its category was not used.
//
// Same extract-from-the-real-source approach as closeHelper.test.mjs: code is scanned with whole-line //
// comments stripped, and the schema is pinned by property NAMES.
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

test("the add_project_note handler was actually extracted", () => {
  assert.ok(code(handlerBlock("add_project_note")).length > 800, "handler body is suspiciously short; extraction probably failed");
});

test("add_project_note declares category as an optional property", () => {
  const def = code(defBlock("add_project_note"));
  const props = def.slice(def.indexOf("properties: {"), def.indexOf("required:"));
  assert.match(props, /^\s*category: \{ type: "string"/m, "category is not a declared property");
  const required = def.slice(def.indexOf("required:"));
  assert.doesNotMatch(required.split("\n")[0], /"category"/, "category must stay optional");
});

test("the handler forwards category to the REST body only when one was given", () => {
  const body = code(handlerBlock("add_project_note"));
  assert.match(body, /\.\.\.\(args\.category \? \{ category: args\.category \} : \{\}\)/);
});

test("the reply surfaces the server's categoryNote", () => {
  const body = code(handlerBlock("add_project_note"));
  assert.match(body, /result\.categoryNote \?/);
});
