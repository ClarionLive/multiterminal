// Falsifiable gate for the path-parameter encoding sweep (ticket 6dcf3fa2).
//
// The vulnerability class: a user/arg-controlled value interpolated into an
// apiCall() URL without being neutralized. Two sub-classes:
//   • PATH SEGMENT (`/${x}`) — the WHATWG URL parser normalizes "." / ".."
//     segments, so an unencoded segment can escape/collapse the route. Sanctioned
//     form: `/${seg(x)}` (seg rejects bare dot/empty and encodeURIComponent's).
//   • QUERY VALUE (`?k=${x}` / `&k=${x}`) — an unencoded value can inject extra
//     `&k=v` pairs (query-param injection). Sanctioned form:
//     `k=${encodeURIComponent(x)}`.
//
// This file has TWO gates:
//   1. A SOURCE CENSUS (reads mcp/index.js as text, never imports it — importing
//      would boot the MCP server) asserting NO raw path segment and NO raw keyed
//      query value survives. The census is STRUCTURAL, not name-based: it finds
//      raw interpolations by shape, regardless of param name. That property is
//      load-bearing — every census miss on this ticket came from hand-enumerating
//      by name (e.g. "lines" hid because the name wasn't on a list). It walks
//      WHOLE template literals (escape-aware, brace-matched) so a multi-line
//      literal, a variable-built endpoint, a nested-template expression, or a
//      seg()-prefixed-but-not-seg()-wrapped expression cannot slip past. "Safe"
//      means the interpolation is EXACTLY seg(...)/encodeURIComponent(...) wrapping
//      the whole expression — not merely starting with it (Run 2 adversary
//      [medium]: a prefix match is falsifiable in the wrong direction).
//   2. A BEHAVIOR gate that EXTRACTS the shipped seg() (regex + eval, pinning the
//      real implementation) and proves the dot-segment traversal is
//      reproduced-then-blocked (Run 1 Codex security [critical]).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

// ---------------------------------------------------------------------------
// Escape-aware template-literal extractor. A naive /`[^`]*`/ mispairs on this
// file (it has template literals with ESCAPED backticks — markdown fences in
// tool output). This walker honors "\\" escapes and ${} nesting depth, so it
// delimits whole literals — including multi-line ones — correctly.
//
// KNOWN RESIDUALS (documented, no instance in the current file):
//   • REGEX LITERALS are not modeled. A future /re/ containing a backtick or an
//     unbalanced quote placed before an api literal could desync the walk. (Run 2
//     code-reviewer NIT.)
//   • SPLIT /api/ PREFIX: an endpoint whose "/api/" lives in a separate variable
//     and is concatenated into a template that itself lacks "/api/"
//     (`const b="/api/tasks"; apiCall(`${b}/${raw}/x`)`) is not covered, because
//     the census scopes to literals that contain "/api/" (or are query fragments)
//     to avoid false-flagging display strings like `${done}/${total}`.
// Both would need value-flow/AST analysis. Called out so the gate's boundary is
// honest rather than implied-total.
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
        if (d === "\\") { j += 2; continue; }
        if (d === "`" && depth === 0) { j++; break; }
        if (d === "$" && source[j + 1] === "{") { depth++; j += 2; continue; }
        if (d === "}" && depth > 0) { depth--; j++; continue; }
        j++;
      }
      lits.push(source.slice(i, j));
      i = j;
    } else if (c === '"' || c === "'") {
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

// Every ${...} interpolation in a literal, brace-matched (so a nested template or
// object expression is captured WHOLE, never truncated/skipped), tagged with the
// char immediately before "$" — which classifies its position:
//   "/" => path segment,  "=" => keyed query value,  "?"/"&" => whole querystring
//   (e.g. ?${params.toString()} — a URLSearchParams builder, exempt),  else => n/a.
function findInterpolations(lit) {
  const out = [];
  for (let i = 0; i < lit.length; i++) {
    if (lit[i] === "$" && lit[i + 1] === "{") {
      const before = i > 0 ? lit[i - 1] : "";
      let depth = 0;
      let j = i + 1;
      for (; j < lit.length; j++) {
        if (lit[j] === "{") depth++;
        else if (lit[j] === "}") { depth--; if (depth === 0) break; }
      }
      out.push({ before, expr: lit.slice(i + 2, j).trim() });
      i = j;
    }
  }
  return out;
}

// True iff `expr` is EXACTLY a single call to `fname` wrapping the WHOLE
// expression — i.e. it starts with `fname(` and that call's matching close paren
// is the last character. This is the fix for the prefix-match bypass: it rejects
// `seg(id) + '/' + raw` (close paren is not last) and anything that only begins
// with the sanctioned call.
function wrapsWhole(expr, fname) {
  if (!expr.startsWith(fname + "(")) return false;
  let depth = 0;
  for (let i = fname.length; i < expr.length; i++) {
    const c = expr[i];
    if (c === "(") depth++;
    else if (c === ")") { depth--; if (depth === 0) return i === expr.length - 1; }
  }
  return false;
}

// A literal participates in the census if it is an api endpoint (contains /api/)
// or a query fragment (starts with `?` or `&`, e.g. `?lines=...`, `&skip=...`).
// Non-URL display strings (`${done}/${total}`) are neither, so they're skipped —
// which is why the path rule can't false-flag them.
function inScope(lit) {
  return lit.includes("/api/") || /^`[?&]/.test(lit);
}

function findViolations(source) {
  const violations = [];
  for (const lit of extractTemplateLiterals(source)) {
    if (!inScope(lit)) continue;
    const isApi = lit.includes("/api/");
    for (const { before, expr } of findInterpolations(lit)) {
      if (before === "/") {
        // Path segment. Only api literals have real route segments; a "/${}" in a
        // bare query fragment isn't a route position.
        if (isApi && !wrapsWhole(expr, "seg")) {
          violations.push({ kind: "path", expr, lit: squash(lit) });
        }
      } else if (before === "=") {
        // Keyed query value (?k=${x} / &k=${x}). Must be encodeURIComponent-wrapped.
        if (!wrapsWhole(expr, "encodeURIComponent")) {
          violations.push({ kind: "query", expr, lit: squash(lit) });
        }
      }
      // before "?" or "&" (whole querystring, e.g. URLSearchParams) => exempt.
    }
  }
  return violations;
}

function squash(lit) {
  return lit.replace(/\s+/g, " ").slice(0, 110);
}

function countSafe(source) {
  let pathSeg = 0;
  let queryVal = 0;
  for (const lit of extractTemplateLiterals(source)) {
    if (!inScope(lit)) continue;
    const isApi = lit.includes("/api/");
    for (const { before, expr } of findInterpolations(lit)) {
      if (before === "/" && isApi && wrapsWhole(expr, "seg")) pathSeg++;
      else if (before === "=" && wrapsWhole(expr, "encodeURIComponent")) queryVal++;
    }
  }
  return { pathSeg, queryVal };
}

// ===========================================================================
// Gate 1 — structural census over mcp/index.js
// ===========================================================================

test("no raw path segment or keyed query value survives in mcp/index.js", () => {
  const violations = findViolations(src);
  assert.deepEqual(
    violations,
    [],
    "Every apiCall path segment must be seg()-wrapped and every keyed query value " +
      "encodeURIComponent-wrapped. Raw interpolations found:\n" +
      violations.map((v) => `  [${v.kind}] ${v.expr}  |  ${v.lit}`).join("\n")
  );
});

test("census reaches the whole file (non-vacuous — many wrapped sites detected)", () => {
  const { pathSeg, queryVal } = countSafe(src);
  assert.ok(pathSeg >= 60, `expected >= 60 seg()-wrapped path segments, found ${pathSeg}`);
  assert.ok(queryVal >= 10, `expected >= 10 encodeURIComponent-wrapped query values, found ${queryVal}`);
});

// ---- PATH falsifiability (same census code, crafted inputs) ----

test("census FLAGS a raw path segment (single-line)", () => {
  const raw = 'await apiCall(`/api/tasks/${args.taskId}/status`, "PATCH");';
  const v = findViolations(raw);
  assert.equal(v.length, 1);
  assert.equal(v[0].kind, "path");
  assert.equal(v[0].expr, "args.taskId");
});

test("census FLAGS a raw segment in a MULTI-LINE api template (line-based blind spot)", () => {
  const multiline = "await apiCall(`/api/tasks\n/${args.taskId}/status`);";
  const v = findViolations(multiline);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.taskId");
});

test("census FLAGS a raw segment in a variable-built endpoint literal", () => {
  const varbuilt = "const endpoint = `/api/projects/${args.projectId}/agents`; await apiCall(endpoint);";
  assert.equal(findViolations(varbuilt).length, 1);
});

test("census is not fooled by an ESCAPED-backtick literal preceding an api call", () => {
  const tricky = "const md = `run \\`cmd\\` now`;\nawait apiCall(`/api/tasks/${args.taskId}`);";
  const v = findViolations(tricky);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.taskId");
});

test("census FLAGS seg() used as a PREFIX, not wrapping the whole segment (Run 2 bypass)", () => {
  // The prefix-match hole: expression STARTS with seg( but appends a raw fragment.
  // args.raw contributes an unencoded '/' + value at runtime — must be flagged.
  const bypass = "await apiCall(`/api/tasks/${seg(args.taskId) + '/' + args.raw}/status`);";
  const v = findViolations(bypass);
  assert.equal(v.length, 1, "seg()-prefixed-but-not-wrapping expression must be flagged");
  assert.equal(v[0].kind, "path");
});

test("census FLAGS a raw segment hidden in a NESTED template expression (brace-skip bypass)", () => {
  // The old /[^{}]+/ regex could not match an interpolation containing braces, so
  // it SKIPPED (didn't report) it. Brace-matched extraction captures it whole; the
  // inner `${raw}` value is not seg()-wrapped -> flagged.
  const nested = "await apiCall(`/api/tasks/${`${args.raw}`}/status`);";
  const v = findViolations(nested);
  assert.equal(v.length, 1, "nested-template raw segment must be flagged, not skipped");
});

test("census PASSES a properly seg()-wrapped path", () => {
  const safe = 'await apiCall(`/api/tasks/${seg(args.taskId)}/status`, "PATCH");';
  assert.equal(findViolations(safe).length, 0);
});

test("census FLAGS only the raw segment in a mixed multi-segment path", () => {
  const mixed = 'await apiCall(`/api/tasks/${seg(args.taskId)}/checklist/${args.itemIndex}/transition`);';
  const v = findViolations(mixed);
  assert.equal(v.length, 1);
  assert.equal(v[0].expr, "args.itemIndex");
});

// ---- QUERY falsifiability + M1 retro-detection ----

test("census FLAGS a raw keyed query value", () => {
  const rawq = "await apiCall(`/api/tasks/${seg(args.taskId)}/reports?limit=${args.limit}`);";
  const v = findViolations(rawq);
  assert.equal(v.length, 1);
  assert.equal(v[0].kind, "query");
  assert.equal(v[0].expr, "args.limit");
});

test("RETRO-DETECTION: the hardened census would have caught the M1 bug (?lines=${...})", () => {
  // The exact pre-M1 shape: debug_logs' file branch built the query in a
  // `?`-prefixed fragment var. A name-based enumeration missed "lines"; the
  // STRUCTURAL census flags it because the value is not encodeURIComponent-wrapped.
  // A census that couldn't have found the hand-found bug isn't done (Alice, Run 2).
  const preM1 = "const lines = args.count || 200;\n          let query = `?lines=${lines}`;";
  const v = findViolations(preM1);
  assert.equal(v.length, 1, "the M1 raw query injection must be structurally detected");
  assert.equal(v[0].kind, "query");
  assert.equal(v[0].expr, "lines");
});

test("census PASSES an encodeURIComponent-wrapped query value", () => {
  const safeq = "await apiCall(`/api/x/${seg(a)}/reports?limit=${encodeURIComponent(args.limit || 50)}`);";
  assert.equal(findViolations(safeq).length, 0);
});

test("census EXEMPTS a whole-querystring URLSearchParams interpolation (?${params.toString()})", () => {
  const usp = "await apiCall(`/api/knowledge/search?${params.toString()}`);";
  assert.equal(findViolations(usp).length, 0);
});

test("census ignores non-api / non-query display strings with ${a}/${b} shapes", () => {
  const display = "text += `\\n📊 Checklist Progress: ${summary.done}/${summary.total} done`;";
  assert.equal(findViolations(display).length, 0);
});

// ===========================================================================
// Gate 2 — behavior: the shipped seg() blocks the dot-segment traversal class
// ===========================================================================

// Extract and pin the REAL seg() from source (like projectId.test.mjs pins
// PROJECT_ID_RE), proving the shipped implementation — not a copy that could drift.
const segMatch = src.match(/function seg\(value\) \{[\s\S]*?\n\}/);
assert.ok(segMatch, "seg(value) function not found in mcp/index.js — did it move or get renamed?");
const seg = new Function(`${segMatch[0]}; return seg;`)();

test("seg() rejects the bare dot/empty segments that encodeURIComponent leaves intact", () => {
  for (const bad of [".", "..", ""]) {
    assert.throws(() => seg(bad), `seg(${JSON.stringify(bad)}) must throw`);
  }
});

test("seg() reproduces-then-blocks the '..' route-escape (the [critical])", () => {
  const naive = "http://h/api/tasks/" + encodeURIComponent("..") + "/status";
  assert.equal(new URL(naive).pathname, "/api/status",
    "sanity: the unguarded encoding really does normalize '..' into a route escape");
  assert.throws(() => seg(".."), "seg('..') must refuse to build the escaping segment");
});

test("seg() reproduces-then-blocks the '.' route-collapse", () => {
  const naive = "http://h/api/tasks/" + encodeURIComponent(".") + "/status";
  assert.equal(new URL(naive).pathname, "/api/tasks/status",
    "sanity: unguarded '.' collapses the segment");
  assert.throws(() => seg("."), "seg('.') must refuse to build the collapsing segment");
});

test("seg() keeps a slash-bearing payload as ONE encoded segment (URL-parse proof)", () => {
  const url = new URL("http://h/api/tasks/" + seg("../x") + "/status");
  assert.equal(url.pathname, "/api/tasks/..%2Fx/status");
  assert.ok(url.pathname.includes("/tasks/"), "intended /tasks/ segment preserved");
});

test("seg() passes ordinary values through, encoding reserved chars", () => {
  assert.equal(seg("2d9643b7"), "2d9643b7");
  assert.equal(seg("a b"), "a%20b");
  assert.equal(seg(0), "0");
  assert.equal(seg("3138ca72-4462-4ffd-a955-4049a76ad6c0"), "3138ca72-4462-4ffd-a955-4049a76ad6c0");
});

test("seg() still rejects nullish (unchanged contract)", () => {
  assert.throws(() => seg(undefined));
  assert.throws(() => seg(null));
});
