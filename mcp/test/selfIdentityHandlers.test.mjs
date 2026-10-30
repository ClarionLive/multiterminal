// selfIdentityHandlers.test.mjs — GH #18: the self-scoped read tools really go through selfIdentityArg.
//
// senderIdentity.test.mjs proves the rule (chooseSelfIdentity). This proves the five handlers use it:
// each handler's `case` block is sliced out of mcp/index.js with comments stripped (so a comment naming
// the call cannot satisfy it), the slice is checked to be non-trivial (so a renamed case cannot make
// the check vacuous), and the call is asserted. A handler that reads args.<name> directly again would
// pass undefined into seg() and throw "apiCall path segment is required (got undefined)", which is the
// bug. Importing index.js would start the server, hence the slice (house pattern).

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8").replace(/\r\n/g, "\n");

function stripComments(code) {
  // Line comments only when "//" is not inside a URL-like string ("http://"): good enough for these
  // handler blocks, whose template literals hold API paths that never contain "//".
  return code.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:"'`])\/\/.*$/gm, "$1");
}

function handlerBlock(tool) {
  const head = `      case "${tool}": {\n`;
  const i = src.indexOf(head);
  assert.ok(i >= 0, `handler for ${tool} not found in mcp/index.js`);
  const j = src.indexOf('\n      case "', i + head.length);
  assert.ok(j > i, `end of the ${tool} handler not found`);
  const block = stripComments(src.slice(i, j));
  assert.ok(block.length > 300, `${tool} handler suspiciously short (${block.length} chars)`);
  return block;
}

const DEFAULTING = [
  ["get_messages", "terminalId"],
  ["get_my_pickable_tasks", "agentName"],
  ["get_my_active_task", "agentName"],
  ["get_active_worktree", "agentName"],
];

for (const [tool, arg] of DEFAULTING) {
  test(`${tool} resolves ${arg} through selfIdentityArg and never reads args.${arg} directly`, () => {
    const block = handlerBlock(tool);
    assert.ok(block.includes(`selfIdentityArg(args, "${arg}")`), `${tool} must call selfIdentityArg(args, "${arg}")`);
    assert.ok(!block.includes(`args.${arg}`), `${tool} reads args.${arg} directly, which is undefined when omitted`);
  });
}

test("get_inbox goes through selfIdentityArg with no self default", () => {
  const block = handlerBlock("get_inbox");
  assert.ok(block.includes('selfIdentityArg(args, "userId", { allowSelf: false })'),
    "get_inbox must call selfIdentityArg(args, \"userId\", { allowSelf: false })");
  assert.ok(!block.includes("args.userId"), "get_inbox reads args.userId directly, which is undefined when omitted");
});
