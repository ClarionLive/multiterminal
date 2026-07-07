// Falsifiable gate for the path-parameter encoding sweep (ticket 6dcf3fa2).
//
// The vulnerability class: a user/arg-controlled value interpolated into an
// apiCall() URL PATH SEGMENT without encoding. Node's WHATWG URL parser
// normalizes "." / ".." dot-segments, so an unencoded segment can escape or
// collapse the intended route. The sweep routes every path-segment interpolation
// through seg(), which rejects bare dot/empty segments and encodeURIComponent's
// everything else.
//
// This file has TWO jobs:
//   1. A SOURCE-SCAN gate (extract mcp/index.js as text, never import it — that
//      would boot the MCP server) asserting no api-path segment interpolation is
//      raw. The scanner walks whole template literals (escape-aware), NOT lines,
//      so a multi-line literal or a variable-built endpoint cannot hide a raw
//      interpolation from the census. (Pipeline Run 1 adversary [medium]: a
//      line-based scanner shares the sweep's blind spot; the gate is the
//      regression barrier for this class, so it must not lie by construction.)
//   2. A BEHAVIOR gate that EXTRACTS the shipped seg() (regex + eval, pinning the
//      real implementation like projectId.test.mjs pins PROJECT_ID_RE) and proves
//      the dot-segment traversal is reproduced-then-blocked. (Run 1 Codex
//      security-auditor [critical]: encodeURIComponent does not encode ".".)
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

// ---------------------------------------------------------------------------
// Escape-aware template-literal extractor.
//
// A naive /`[^`]*`/ match mispairs on this file because it contains template
// literals with ESCAPED backticks (markdown code fences in tool output). This
// walker honors "\\" escapes and tracks ${...} nesting depth, so it delimits
// whole literals correctly — including multi-line ones. That is what lets the
// scan below cover a path spread across lines or assembled into an `endpoint`
// variable, closing the line-based blind spot.
// ---------------------------------------------------------------------------
function extractTemplateLiterals(source) {
  const lits = [];
  const n = source.length;
  let i = 0;
  while (i < n) {
    const c = source[i];
    if (c === "`") {
      let j = i + 1;
      let depth = 0;
      while (j < n) {
        const d = source[j];
        if (d === "\\") { j += 2; continue; }              // escaped char (incl. \`)
        if (d === "`" && depth === 0) { j++; break; }        // closing backtick
        if (d === "$" && source[j + 1] === "{") { depth++; j += 2; continue; }
        if (d === "}" && depth > 0) { depth--; j++; continue; }
        j++;
      }
      lits.push(source.slice(i, j));
      i = j;
    } else if (c === '"' || c === "'") {
      // Skip a regular string so a backtick inside it can't open a false literal.
      let j = i + 1;
      while (j < n) {
        const d = source[j];
        if (d === "\\") { j += 2; continue; }
        if (d === c || d === "\n") { j++; break; }
        j++;
      }
      i = j;
    } else if (c === "/" && source[i + 1] === "/") {
      let j = i + 2; while (j < n && source[j] !== "\n") j++; i = j;
    } else if (c === "/" && source[i + 1] === "*") {
      let j = i + 2; while (j < n && !(source[j] === "*" && source[j + 1] === "/")) j++; i = j + 2;
    } else {
      i++;
    }
  }
  return lits;
}

// A path segment is an interpolation immediately preceded by "/": `/${expr}`.
// The one sanctioned form is `/${seg(...)}`. Everything else in path position
// (raw args, bare encodeURIComponent, anything) is a violation. Query-string
// interpolations (?x=${y}, &x=${y}, or a `${query}` var appended after the path)
// are preceded by "?", "=", "&", or a word char — never "/" — so they are out of
// scope by construction (the query string cannot route-traverse).
function findRawApiPathSegments(source) {
  const violations = [];
  for (const lit of extractTemplateLiterals(source)) {
    if (!lit.includes("/api/")) continue;
    const re = /\/\$\{([^{}]+)\}/g;
    let m;
    while ((m = re.exec(lit)) !== null) {
      const expr = m[1].trim();
      if (!expr.startsWith("seg(")) {
        violations.push({ expr, lit: lit.replace(/\s+/g, " ").slice(0, 100) });
      }
    }
  }
  return violations;
}

function countSafeApiPathSegments(source) {
  let n = 0;
  for (const lit of extractTemplateLiterals(source)) {
    if (!lit.includes("/api/")) continue;
    const re = /\/\$\{\s*seg\(/g;
    while (re.exec(lit) !== null) n++;
  }
  return n;
}

// ===========================================================================
// Gate 1 — source scan: no raw path-segment interpolation survives
// ===========================================================================

test("no raw path-segment interpolation survives in mcp/index.js", () => {
  const violations = findRawApiPathSegments(src);
  assert.deepEqual(
    violations,
    [],
    "Every apiCall path segment must go through seg(). Raw interpolations found:\n" +
      violations.map((v) => `  ${v.expr}  |  ${v.lit}`).join("\n")
  );
});

test("scanner reaches the whole file (non-vacuous — many seg() sites detected)", () => {
  const safe = countSafeApiPathSegments(src);
  assert.ok(safe >= 60, `expected >= 60 seg()-wrapped api path segments, found ${safe}`);
});

// ---- Falsifiability of the SCANNER (same code, crafted inputs) ----

test("scanner FLAGS a raw path segment (single-line negative fixture)", () => {
  const raw = 'const x = await apiCall(`/api/tasks/${args.taskId}/status`, "PATCH");';
  const v = findRawApiPathSegments(raw);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.taskId");
});

test("scanner FLAGS a raw segment in a MULTI-LINE api template (the line-based blind spot)", () => {
  // /api/ on one physical line, the raw interpolation on the next. A line-based
  // scanner skips the second line (no /api/ on it) and misses this. The
  // whole-literal walker must catch it — this is the regression the hardening buys.
  const multiline = "await apiCall(`/api/tasks\n/${args.taskId}/status`);";
  const v = findRawApiPathSegments(multiline);
  assert.equal(v.length, 1, "multi-line raw path segment must be flagged");
  assert.equal(v[0].expr, "args.taskId");
});

test("scanner FLAGS a raw segment in a variable-built endpoint literal", () => {
  // Endpoint assembled into a variable then passed to apiCall. As long as the
  // literal itself carries /api/, the whole-literal scan covers it — which is
  // exactly the shape of the 5 real endpoint/rendpoint/path/latestUrl builders.
  const varbuilt = "const endpoint = `/api/projects/${args.projectId}/agents`; await apiCall(endpoint);";
  const v = findRawApiPathSegments(varbuilt);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.projectId");
});

test("scanner is not fooled by an ESCAPED-backtick literal preceding an api call", () => {
  // Mirrors the real file: a markdown literal with an escaped backtick, then a
  // raw api path. A naive `[^`]*` pairing mispairs here and would miss the raw
  // segment; the escape-aware walker must still flag it.
  const tricky = "const md = `run \\`cmd\\` now`;\nawait apiCall(`/api/tasks/${args.taskId}`);";
  const v = findRawApiPathSegments(tricky);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.taskId");
});

test("scanner PASSES a properly seg()-wrapped path", () => {
  const safe = 'const x = await apiCall(`/api/tasks/${seg(args.taskId)}/status`, "PATCH");';
  assert.equal(findRawApiPathSegments(safe).length, 0);
});

test("scanner FLAGS only the raw segment in a mixed multi-segment path", () => {
  const mixed = 'await apiCall(`/api/tasks/${seg(args.taskId)}/checklist/${args.itemIndex}/transition`);';
  const v = findRawApiPathSegments(mixed);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.itemIndex");
});

test("scanner ignores query-string interpolations (not path segments)", () => {
  const q = 'await apiCall(`/api/tasks/inbox/${seg(args.userId)}?unreadOnly=${x}&limit=${y}`);';
  assert.equal(findRawApiPathSegments(q).length, 0);
});

test("scanner ignores non-api display strings with ${a}/${b} shapes", () => {
  const display = "text += `\\n📊 Checklist Progress: ${summary.done}/${summary.total} done`;";
  assert.equal(findRawApiPathSegments(display).length, 0);
});

// KNOWN RESIDUAL (documented, no instance in the current file): an endpoint
// whose /api/ prefix lives in a SEPARATE variable and is concatenated in —
// e.g. `const base = "/api/tasks"; apiCall(`${base}/${args.raw}/x`)`. The inner
// literal `${base}/${args.raw}/x` carries no "/api/", so the scan skips it, and
// `${args.raw}` is preceded by "/" but never inspected. Catching that requires
// value-flow/AST analysis. It is called out here so a future maintainer knows
// the census assumes the /api/ prefix appears literally in the same template as
// the interpolation (true for all 77 current api literals, incl. the 5
// variable-built endpoints, whose literals each contain /api/ inline).

// ===========================================================================
// Gate 2 — behavior: the shipped seg() blocks the dot-segment traversal class
// ===========================================================================

// Extract and pin the REAL seg() from source (like projectId.test.mjs pins
// PROJECT_ID_RE), so this proves the shipped implementation — not a copy that
// could drift. seg() uses only globals (String, encodeURIComponent, Error).
const segMatch = src.match(/function seg\(value\) \{[\s\S]*?\n\}/);
assert.ok(segMatch, "seg(value) function not found in mcp/index.js — did it move or get renamed?");
const seg = new Function(`${segMatch[0]}; return seg;`)();

test("seg() rejects the bare dot/empty segments that encodeURIComponent leaves intact", () => {
  for (const bad of [".", "..", ""]) {
    assert.throws(() => seg(bad), `seg(${JSON.stringify(bad)}) must throw`);
  }
});

test("seg() reproduces-then-blocks the '..' route-escape (the [critical])", () => {
  // REPRODUCE the vulnerability with the naive encoding the sweep replaced:
  // encodeURIComponent does NOT encode ".", so the URL parser collapses "..".
  const naive = "http://h/api/tasks/" + encodeURIComponent("..") + "/status";
  assert.equal(new URL(naive).pathname, "/api/status",
    "sanity: the unguarded encoding really does normalize '..' into a route escape");
  // BLOCK: the shipped seg() cannot produce that segment at all.
  assert.throws(() => seg(".."), "seg('..') must refuse to build the escaping segment");
});

test("seg() reproduces-then-blocks the '.' route-collapse", () => {
  const naive = "http://h/api/tasks/" + encodeURIComponent(".") + "/status";
  assert.equal(new URL(naive).pathname, "/api/tasks/status",
    "sanity: unguarded '.' drops the /tasks/ segment's successor boundary");
  assert.throws(() => seg("."), "seg('.') must refuse to build the collapsing segment");
});

test("seg() keeps a slash-bearing payload as ONE encoded segment (URL-parse proof)", () => {
  // '../x' is not a bare dot segment, so seg() encodes it rather than throwing.
  // The proof the route is intact: the pathname still contains the /tasks/
  // segment and the payload stays a single %2F-encoded segment (no normalization).
  const url = new URL("http://h/api/tasks/" + seg("../x") + "/status");
  assert.equal(url.pathname, "/api/tasks/..%2Fx/status");
  assert.ok(url.pathname.includes("/tasks/"), "intended /tasks/ segment preserved");
});

test("seg() passes ordinary values through, encoding reserved chars", () => {
  assert.equal(seg("2d9643b7"), "2d9643b7");                 // hex short id — no-op
  assert.equal(seg("a b"), "a%20b");                          // space encoded
  assert.equal(seg(0), "0");                                  // itemIndex:0 is valid, not nullish
  assert.equal(seg("3138ca72-4462-4ffd-a955-4049a76ad6c0"), "3138ca72-4462-4ffd-a955-4049a76ad6c0");
});

test("seg() still rejects nullish (unchanged contract)", () => {
  assert.throws(() => seg(undefined));
  assert.throws(() => seg(null));
});
