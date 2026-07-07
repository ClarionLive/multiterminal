// Falsifiable gate for the path-parameter encoding sweep (ticket 6dcf3fa2).
//
// The vulnerability class: a user/arg-controlled value interpolated into an
// apiCall() URL without being neutralized. Two sub-classes:
//   • PATH SEGMENT — the WHATWG URL parser normalizes "." / ".." segments, so an
//     unencoded segment can escape/collapse the route. Sanctioned form: seg(x)
//     (rejects bare dot/empty, then encodeURIComponent's).
//   • QUERY VALUE — an unencoded value can inject extra `&k=v` pairs. Sanctioned
//     form: encodeURIComponent(x).
//
// TWO gates:
//   1. A STRUCTURAL SOURCE CENSUS (reads mcp/index.js as text — never imports it,
//      which would boot the MCP server) asserting NO raw path segment and NO raw
//      keyed query value survives. It is name-INDEPENDENT (every census miss on
//      this ticket came from hand-enumerating by param name) and POSITION-AWARE:
//      it tracks URL region (path vs query) across each whole template literal, so
//      it enforces the invariant for COMPOSITE positions too — `prefix-${raw}`,
//      `${seg(id)}-${raw}`, `?k=pre${raw}`, `?k=${enc(a)}-${raw}` — not just
//      interpolations that start a segment/value. "Safe" means the interpolation
//      is EXACTLY seg(...)/encodeURIComponent(...) wrapping the whole expression,
//      not merely starting with it.
//   2. A BEHAVIOR gate that EXTRACTS the shipped seg() (regex + eval, pinning the
//      real implementation) and reproduces-then-blocks the dot-segment traversal.
//
// ACCEPTED RESIDUALS (PM ruling, ticket 6dcf3fa2 — Owner's-behalf desk authority).
// The census enforces the invariant for ALL single- and composite-interpolation
// positions in path and keyed-query contexts. Three exotic-shape gaps remain and
// are ACCEPTED, not chased — each is defense-in-depth, not the primary control:
// closing them would require a future author to (a) write a NEW raw interpolation
// in the exotic shape, (b) get it past code review, AND (c) have no runtime test
// catch it. They are:
//   • REGEX LITERALS are not lexed — a future /re/ with a backtick/quote before an
//     api literal could desync the template walker.
//   • SPLIT /api/ PREFIX — an endpoint whose "/api/" lives in a separate variable
//     concatenated into a template lacking "/api/" (`const b="/api/x"; apiCall(
//     `${b}/${raw}`)`) is out of the census's /api/-in-literal scope (that scope
//     is what prevents false-flagging display strings like `${done}/${total}`).
//   • STRING-EMBEDDED PARENS/BRACES — the paren/brace matchers in wrapsWhole /
//     findInterpolations don't skip string contents, so `seg(")")` or
//     `${seg(f("}"))}` could misclassify.
// None has an instance in the current file (the census reports 0 violations).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

// Escape-aware template-literal extractor. A naive /`[^`]*`/ mispairs on this file
// (template literals with ESCAPED backticks — markdown fences in tool output). This
// walker honors "\\" escapes and ${} nesting, delimiting whole (incl. multi-line)
// literals correctly.
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

// True iff `expr` is EXACTLY a single call to `fname` wrapping the WHOLE expression
// (starts with `fname(` and that call's matching close paren is the last char).
// Rejects the prefix-plus-raw bypass (`seg(id) + '/' + raw`).
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

function inScope(lit) {
  return lit.includes("/api/") || /^`[?&]/.test(lit);
}

// Walk a literal tracking URL POSITION and yield {expr, region, inValue} for each
// ${...} interpolation. region flips path->query at a literal "?"; within query,
// inValue is true between "=" and the next "&"/"#". Non-api in-scope literals are
// query fragments (they start with ?/&) so they begin in the query region. Brace-
// matched capture means nested-template/object expressions are captured whole
// (never skipped); "\\" escapes are honored so an escaped ${ or ` can't desync.
function classifyInterpolations(lit, isApi) {
  const out = [];
  let region = isApi ? "path" : "query";
  let inValue = false;
  for (let i = 1; i < lit.length; i++) {
    const ch = lit[i];
    if (ch === "\\") { i++; continue; }
    if (ch === "$" && lit[i + 1] === "{") {
      let depth = 0;
      let j = i + 1;
      for (; j < lit.length; j++) {
        if (lit[j] === "{") depth++;
        else if (lit[j] === "}") { depth--; if (depth === 0) break; }
      }
      out.push({ expr: lit.slice(i + 2, j).trim(), region, inValue });
      i = j;
      continue;
    }
    if (region === "path") {
      if (ch === "?") { region = "query"; inValue = false; }
    } else {
      if (ch === "=") inValue = true;
      else if (ch === "&" || ch === "#") inValue = false;
    }
  }
  return out;
}

function findViolations(source) {
  const violations = [];
  for (const lit of extractTemplateLiterals(source)) {
    if (!inScope(lit)) continue;
    const isApi = lit.includes("/api/");
    for (const { expr, region, inValue } of classifyInterpolations(lit, isApi)) {
      if (region === "path") {
        // Only api literals hold route segments; a "/${}" in a bare query fragment
        // isn't a route position. Every path-region interpolation must be seg().
        if (isApi && !wrapsWhole(expr, "seg")) {
          violations.push({ kind: "path", expr, lit: squash(lit) });
        }
      } else if (inValue) {
        // Keyed query value — must be encodeURIComponent-wrapped.
        if (!wrapsWhole(expr, "encodeURIComponent")) {
          violations.push({ kind: "query", expr, lit: squash(lit) });
        }
      }
      // query, non-value (key/whole position, e.g. ?${params.toString()}) => exempt.
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
    for (const { expr, region, inValue } of classifyInterpolations(lit, isApi)) {
      if (region === "path" && isApi && wrapsWhole(expr, "seg")) pathSeg++;
      else if (region === "query" && inValue && wrapsWhole(expr, "encodeURIComponent")) queryVal++;
    }
  }
  return { pathSeg, queryVal };
}

// ===========================================================================
// Gate 1 — structural, position-aware census over mcp/index.js
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

// ---- PATH falsifiability ----

test("census FLAGS a raw path segment (single-line)", () => {
  const raw = 'await apiCall(`/api/tasks/${args.taskId}/status`, "PATCH");';
  const v = findViolations(raw);
  assert.equal(v.length, 1);
  assert.equal(v[0].kind, "path");
  assert.equal(v[0].expr, "args.taskId");
});

test("census FLAGS a raw segment in a MULTI-LINE api template (line-based blind spot)", () => {
  const multiline = "await apiCall(`/api/tasks\n/${args.taskId}/status`);";
  assert.equal(findViolations(multiline).length, 1);
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
  const bypass = "await apiCall(`/api/tasks/${seg(args.taskId) + '/' + args.raw}/status`);";
  const v = findViolations(bypass);
  assert.equal(v.length, 1);
  assert.equal(v[0].kind, "path");
});

test("census FLAGS a raw segment hidden in a NESTED template expression (brace-skip bypass)", () => {
  const nested = "await apiCall(`/api/tasks/${`${args.raw}`}/status`);";
  assert.equal(findViolations(nested).length, 1);
});

test("census FLAGS a raw interpolation with a LITERAL PREFIX inside a segment (Run 3 composite)", () => {
  // `prefix-${raw}` — raw is preceded by '-', not '/'. Single-char classification
  // missed it; position tracking catches it (it's still inside a path segment).
  const composite = "await apiCall(`/api/tasks/prefix-${args.raw}/status`);";
  const v = findViolations(composite);
  assert.equal(v.length, 1, "literal-prefixed raw path segment must be flagged");
  assert.equal(v[0].kind, "path");
});

test("census FLAGS a raw interpolation appended to a seg() in the same segment (Run 3 composite)", () => {
  // `${seg(id)}-${raw}` — the seg() part is fine, the appended raw part is not.
  const composite = "await apiCall(`/api/tasks/${seg(args.id)}-${args.raw}/status`);";
  const v = findViolations(composite);
  assert.equal(v.length, 1, "raw part of a composite segment must be flagged even next to a seg()");
  assert.equal(v[0].expr, "args.raw");
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

// ---- QUERY falsifiability + composite + M1 retro-detection ----

test("census FLAGS a raw keyed query value", () => {
  const rawq = "await apiCall(`/api/tasks/${seg(args.taskId)}/reports?limit=${args.limit}`);";
  const v = findViolations(rawq);
  assert.equal(v.length, 1);
  assert.equal(v[0].kind, "query");
  assert.equal(v[0].expr, "args.limit");
});

test("census FLAGS a raw interpolation with a LITERAL PREFIX inside a query value (Run 3 composite)", () => {
  const composite = "await apiCall(`/api/x/${seg(a)}?q=prefix-${args.raw}`);";
  const v = findViolations(composite);
  assert.equal(v.length, 1, "literal-prefixed raw query value must be flagged");
  assert.equal(v[0].kind, "query");
});

test("census FLAGS a raw interpolation appended to encodeURIComponent in the same query value (Run 3 composite)", () => {
  const composite = "await apiCall(`/api/x/${seg(a)}?q=${encodeURIComponent(b)}-${args.raw}`);";
  const v = findViolations(composite);
  assert.equal(v.length, 1, "raw part of a composite query value must be flagged");
  assert.equal(v[0].expr, "args.raw");
});

test("RETRO-DETECTION: the census would have caught the M1 bug (?lines=${...})", () => {
  const preM1 = "const lines = args.count || 200;\n          let query = `?lines=${lines}`;";
  const v = findViolations(preM1);
  assert.equal(v.length, 1);
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

test("census PASSES a query string appended by concatenation (`.../stats` + ctxQs)", () => {
  // The Run-4 refactor moved whole-querystring appends out of the path literal so
  // the path literal contains only seg()-wrapped segments. The append var's OWN
  // literal (`?docId=${encodeURIComponent(x)}`) is censused as a query fragment.
  const src2 = "const stats = await apiCall(`/api/terminals/${seg(ctxName)}/stats` + ctxQs);\n" +
    "const ctxQs = ctxDocId ? `?docId=${encodeURIComponent(ctxDocId)}` : \"\";";
  assert.equal(findViolations(src2).length, 0);
});

test("census ignores non-api / non-query display strings with ${a}/${b} shapes", () => {
  const display = "text += `\\n📊 Checklist Progress: ${summary.done}/${summary.total} done`;";
  assert.equal(findViolations(display).length, 0);
});

// ===========================================================================
// Gate 2 — behavior: the shipped seg() blocks the dot-segment traversal class
// ===========================================================================

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
