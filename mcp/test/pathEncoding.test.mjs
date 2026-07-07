// Falsifiable gate for the path-parameter encoding sweep (ticket 6dcf3fa2).
//
// The vulnerability class: a user/arg-controlled value interpolated into an
// apiCall() URL PATH SEGMENT without encoding. Node's URL parser normalizes
// "../" dot-segments, so `/api/projects/${args.projectId}` with
// projectId="../tasks/<id>" becomes a request against a sibling route. The sweep
// routes every path-segment interpolation through seg() (encodeURIComponent),
// which turns "../" into "..%2F" — no longer normalized into a separator.
//
// This test extracts the SHIPPED mcp/index.js as text (never imports it — that
// would boot the MCP server) and asserts that NO api-path segment interpolation
// is raw. The scanner is deliberately line-based rather than backtick-paired:
// index.js contains template literals with escaped backticks (markdown in tool
// output), which defeats naive `...` pairing. api endpoint literals are
// single-line, so "any /${...} on a line containing /api/ must be seg()" is both
// robust and precise. Query-string interpolations (?x=${y}, &x=${y}) are NOT
// path segments and are intentionally out of scope (Node's parser can't
// route-traverse via the query string).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

// Find raw (non-seg) path-segment interpolations in api template literals.
// A path segment is an interpolation immediately preceded by "/": `/${expr}`.
// Comment lines are skipped so documentation mentioning the pattern doesn't
// register as a finding.
function findRawApiPathSegments(source) {
  const violations = [];
  source.split(/\r?\n/).forEach((line, idx) => {
    const trimmed = line.trimStart();
    if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*")) return;
    if (!line.includes("/api/")) return;
    const re = /\/\$\{([^{}]+)\}/g;
    let m;
    while ((m = re.exec(line)) !== null) {
      const expr = m[1].trim();
      if (!expr.startsWith("seg(")) {
        violations.push({ line: idx + 1, expr, text: line.trim() });
      }
    }
  });
  return violations;
}

// Count the sanctioned (seg-wrapped) api path segments — used to prove the
// scanner actually reaches across the whole file, so a future regression that
// silently stops scanning can't pass by returning an empty violation list.
function countSafeApiPathSegments(source) {
  let n = 0;
  source.split(/\r?\n/).forEach((line) => {
    const trimmed = line.trimStart();
    if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*")) return;
    if (!line.includes("/api/")) return;
    const re = /\/\$\{\s*seg\(/g;
    while (re.exec(line) !== null) n++;
  });
  return n;
}

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

test("no raw path-segment interpolation survives in mcp/index.js", () => {
  const violations = findRawApiPathSegments(src);
  assert.deepEqual(
    violations,
    [],
    "Every apiCall path segment must go through seg(). Raw interpolations found:\n" +
      violations.map((v) => `  L${v.line}: ${v.expr}  |  ${v.text}`).join("\n")
  );
});

test("scanner reaches the whole file (non-vacuous — many seg() sites detected)", () => {
  // The real file has ~74 path segments; assert a healthy floor so a broken
  // scanner (or a mass revert) is caught rather than silently passing.
  const safe = countSafeApiPathSegments(src);
  assert.ok(safe >= 60, `expected >= 60 seg()-wrapped api path segments, found ${safe}`);
});

// ---- Falsifiability: the SAME scanner must fail on a raw interpolation ----

test("scanner FLAGS a raw path segment (negative fixture)", () => {
  const raw = 'const x = await apiCall(`/api/tasks/${args.taskId}/status`, "PATCH");';
  const v = findRawApiPathSegments(raw);
  assert.equal(v.length, 1, "a raw /${args.taskId} path segment must be flagged");
  assert.equal(v[0].expr, "args.taskId");
});

test("scanner FLAGS only the raw segment in a mixed multi-segment path", () => {
  const mixed = 'await apiCall(`/api/tasks/${seg(args.taskId)}/checklist/${args.itemIndex}/transition`);';
  const v = findRawApiPathSegments(mixed);
  assert.equal(v.length, 1, "the seg()-wrapped segment passes; the raw one is flagged");
  assert.equal(v[0].expr, "args.itemIndex");
});

test("scanner PASSES a properly seg()-wrapped path", () => {
  const safe = 'const x = await apiCall(`/api/tasks/${seg(args.taskId)}/status`, "PATCH");';
  assert.equal(findRawApiPathSegments(safe).length, 0);
});

test("scanner ignores query-string interpolations (not path segments)", () => {
  // Path is seg()-wrapped; the raw ?unreadOnly=/&limit= query values are query
  // params, not path segments — the URL parser cannot route-traverse via them.
  const q = 'await apiCall(`/api/tasks/inbox/${seg(args.userId)}?unreadOnly=${unreadOnly}&limit=${limit}`);';
  assert.equal(findRawApiPathSegments(q).length, 0);
});

test("scanner ignores non-api display strings with ${a}/${b} shapes", () => {
  // `${done}/${total}` in a status line is not an api path — the line has no
  // /api/, so it is out of scope and must not be flagged.
  const display = "text += `\\n📊 Checklist Progress: ${summary.done}/${summary.total} done`;";
  assert.equal(findRawApiPathSegments(display).length, 0);
});
