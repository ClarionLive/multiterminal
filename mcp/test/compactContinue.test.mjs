// compact_my_context asks MT to continue after the compaction (ticket c0ebcfd4).
//
// A manual /compact ends with no new turn, so the agent sat idle until the Owner typed. The handler
// sends continueAfterCompact: true on the submit POST; MT arms the pane and types a continue prompt
// when the plugin's SessionStart(compact) hook reports the compaction finished. clear_my_context must
// NOT send it: a cleared session is restarted by the /clear inject instead.
//
// Same house pattern as compactCap.test.mjs: read index.js as text, comments stripped, extraction
// asserted non-vacuous. Falsified by deleting the field (first test red) and by adding it to the
// /clear POST (second test red).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

function handler(name) {
  const start = src.indexOf(`      case "${name}": {`);
  assert.ok(start !== -1, `${name} handler not found`);
  const body = src.slice(start, src.indexOf("\n      case ", start + 10));
  assert.ok(body.length > 500, `${name} handler extraction looks vacuous (${body.length} chars)`);
  return body.split(/\r?\n/).filter((l) => !/^\s*\/\//.test(l)).join("\n");
}

test("compact_my_context asks MT to continue after the compaction", () => {
  assert.match(handler("compact_my_context"), /\/submit`, "POST", \{ text: compactText, continueAfterCompact: true \}\)/);
});

test("clear_my_context does not", () => {
  assert.doesNotMatch(handler("clear_my_context"), /continueAfterCompact/);
});
