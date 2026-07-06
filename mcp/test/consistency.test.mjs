// CI gate for the tool-def <-> dispatch-handler parity that ec97c446 added as a
// startup assert (mcp/index.js `assertToolDefHandlerConsistency`). That assert
// runs at server boot and only WARNS to stderr (non-fatal by design, so a live
// terminal never dies over it). CI needs the same check to FAIL the build, so a
// drift can't merge silently.
//
// Rather than boot the whole MCP server (which imports the SDK — an extra
// `npm ci` in mcp/ — and would need a spawn/timeout/kill dance for a stdio
// server that otherwise runs forever), this test re-runs the assert's exact
// logic against index.js source with zero dependencies. To stop this copy from
// silently drifting away from the real function, it FIRST pins the two matcher
// literals + the region delimiters that live in index.js; if the real assert is
// re-tuned, these pins fail loudly and whoever edited it updates this test too.
// Same "extract-and-assert-against-the-real-source" contract as projectId.test.mjs.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

// These four strings are copied verbatim from assertToolDefHandlerConsistency
// (index.js). The pin test below asserts each still occurs in the source, so if
// the real check is retuned the mismatch surfaces here instead of going stale.
const LIST_MARKER = 'server.setRequestHandler(ListToolsRequestSchema';
const CALL_MARKER = 'server.setRequestHandler(CallToolRequestSchema';
const DEF_RE_SRC = '/\\bname:\\s*"([^"]+)"/g';
const HANDLER_RE_SRC = '/^      case\\s+"([^"]+)":/gm';

test("consistency check literals still present in index.js (drift guard)", () => {
  assert.ok(src.includes(LIST_MARKER), `ListTools marker missing — did the handler move? (${LIST_MARKER})`);
  assert.ok(src.includes(CALL_MARKER), `CallTool marker missing — did the handler move? (${CALL_MARKER})`);
  assert.ok(
    src.includes(DEF_RE_SRC),
    `tool-def matcher literal drifted from ${DEF_RE_SRC} — update this test to match index.js`,
  );
  assert.ok(
    src.includes(HANDLER_RE_SRC),
    `dispatch-handler matcher literal drifted from ${HANDLER_RE_SRC} — update this test to match index.js`,
  );
});

test("every tool definition has a dispatch handler and vice versa", () => {
  const listIdx = src.indexOf(LIST_MARKER);
  const callIdx = src.indexOf(CALL_MARKER);
  assert.ok(listIdx >= 0 && callIdx > listIdx, "could not locate ListTools/CallTool handler regions");

  const defsRegion = src.slice(listIdx, callIdx);
  const handlersRegion = src.slice(callIdx);
  // Mirror index.js:assertToolDefHandlerConsistency exactly.
  const defs = new Set([...defsRegion.matchAll(/\bname:\s*"([^"]+)"/g)].map((m) => m[1]));
  const handlers = new Set([...handlersRegion.matchAll(/^      case\s+"([^"]+)":/gm)].map((m) => m[1]));

  const defsNoHandler = [...defs].filter((d) => !handlers.has(d)).sort();
  const handlersNoDef = [...handlers].filter((h) => !defs.has(h)).sort();

  assert.ok(defs.size > 0, "found zero tool definitions — region slicing is wrong");
  assert.deepEqual(defsNoHandler, [], `tool def(s) with NO dispatch handler (would error at call time): ${defsNoHandler.join(", ")}`);
  assert.deepEqual(handlersNoDef, [], `dispatch handler(s) with NO tool def (dead / unreachable): ${handlersNoDef.join(", ")}`);
  assert.equal(defs.size, handlers.size, `def count ${defs.size} != handler count ${handlers.size}`);
});
