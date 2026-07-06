#!/usr/bin/env node
// verify-raisesafe.mjs — falsifiable guard for the "resilient event dispatch" invariant.
//
// Ticket 1df2a534 (P5) item 0. MessageBroker raised ~30 events with the bare idiom
// `SomeEvent?.Invoke(this, args)` (plus one captured-local variant, `var h = Evt; h.Invoke(...)`).
// Both idioms invoke the whole multicast delegate as a single call, so the FIRST subscriber that
// throws aborts delivery to every later subscriber AND bubbles the exception back into the REST/MCP
// call that raised the event. RaiseSafe<T> snapshots the invocation list and invokes each subscriber
// inside its own try/catch, so one bad subscriber can no longer starve the others.
//
// The invariant this asserts (on CODE-ONLY text — comments and string literals are masked out so a
// doc comment that illustrates the old idiom never counts):
//
//   (1) NO BARE RAISE — zero occurrences of `.Invoke(this,` remain in MessageBroker.cs. This single
//       pattern catches BOTH idioms: `Event?.Invoke(this, ...)` and the captured-local
//       `handler.Invoke(this, ...)`. RaiseSafe's own internal dispatch calls the subscriber delegate
//       directly (`subscriber(this, args)`), NOT via `.Invoke(this,`, so it does not match.
//   (2) HELPER PRESENT — the `private void RaiseSafe<T>(...)` helper is defined exactly once.
//   (3) HELPER USED — at least one `RaiseSafe(...)` call site exists (the raises actually route
//       through it). The count is reported for eyeballing against the known event-raise total.
//
// Usage:
//   node scripts/verify-raisesafe.mjs             # --check (default): exit 1 on any violation
//   node scripts/verify-raisesafe.mjs --self-test # prove the checks falsify (negative fixtures)
//
// Adding a new event to MessageBroker? Raise it with RaiseSafe(MyEvent, args) — never
// `MyEvent?.Invoke(this, args)` — and this check keeps passing. A bare raise fails the build.

import fs from 'fs';
import path from 'path';

const args = process.argv.slice(2);
const doSelfTest = args.includes('--self-test');

const REPO_ROOT = path.join(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const TARGET = 'MCPServer/Services/MessageBroker.cs';

// Blank out comment + string content so patterns inside them never count. Identical state machine to
// verify-taskdb-gate.mjs (kept standalone so each verifier is self-contained). Preserves offsets and
// newlines; replaces comment/string bytes with spaces.
function maskCodeOnly(src) {
  let out = '';
  let state = 'code'; // code | line | block | str | verq | chr
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    const c2 = i + 1 < src.length ? src[i + 1] : '';
    if (state === 'code') {
      if (c === '/' && c2 === '/') { state = 'line'; out += '  '; i++; continue; }
      if (c === '/' && c2 === '*') { state = 'block'; out += '  '; i++; continue; }
      if (c === '@' && c2 === '"') { state = 'verq'; out += '  '; i++; continue; }
      if (c === '"') { state = 'str'; out += ' '; continue; }
      if (c === '\'') { state = 'chr'; out += ' '; continue; }
      out += c; continue;
    }
    if (state === 'line') {
      if (c === '\n') { state = 'code'; out += '\n'; continue; }
      out += (c === '\r' ? '\r' : ' '); continue;
    }
    if (state === 'block') {
      if (c === '*' && c2 === '/') { state = 'code'; out += '  '; i++; continue; }
      out += (c === '\n' ? '\n' : c === '\r' ? '\r' : ' '); continue;
    }
    if (state === 'str') {
      if (c === '\\') { out += '  '; i++; continue; }
      if (c === '"') { state = 'code'; out += ' '; continue; }
      out += (c === '\n' ? '\n' : ' '); continue;
    }
    if (state === 'verq') {
      if (c === '"' && c2 === '"') { out += '  '; i++; continue; }
      if (c === '"') { state = 'code'; out += ' '; continue; }
      out += (c === '\n' ? '\n' : ' '); continue;
    }
    if (state === 'chr') {
      if (c === '\\') { out += '  '; i++; continue; }
      if (c === '\'') { state = 'code'; out += ' '; continue; }
      out += ' '; continue;
    }
  }
  return out;
}

// Compute line numbers for a set of match indices in the ORIGINAL source (for readable violations).
function lineOf(src, index) {
  let line = 1;
  for (let i = 0; i < index && i < src.length; i++) if (src[i] === '\n') line++;
  return line;
}

// The three falsifiable checks against a source string. Returns { bareRaises:[lines], helperDefs, calls }.
function analyze(src) {
  const code = maskCodeOnly(src);
  const bareRaises = [];
  for (const m of code.matchAll(/\.Invoke\s*\(\s*this\s*,/g)) bareRaises.push(lineOf(src, m.index));
  const helperDefs = [...code.matchAll(/private\s+void\s+RaiseSafe\s*<\s*\w+\s*>\s*\(/g)].length;
  // RaiseSafe call sites: `RaiseSafe(` — the generic DEFINITION is `RaiseSafe<T>(` (angle bracket
  // before the paren) so this pattern matches call sites only, never the def.
  const calls = [...code.matchAll(/\bRaiseSafe\s*\(/g)].length;
  return { bareRaises, helperDefs, calls };
}

function checkSource(label, src, { requireHelper = true } = {}) {
  const { bareRaises, helperDefs, calls } = analyze(src);
  const problems = [];
  if (bareRaises.length > 0) {
    problems.push(`${bareRaises.length} bare event-raise(s) still present (\`.Invoke(this,\`) at line(s): ${bareRaises.join(', ')}`);
  }
  if (requireHelper && helperDefs !== 1) {
    problems.push(`expected exactly 1 RaiseSafe<T> helper definition, found ${helperDefs}`);
  }
  if (requireHelper && calls < 1) {
    problems.push(`RaiseSafe helper is never called (0 call sites) — raises are not routed through it`);
  }
  return { ok: problems.length === 0, problems, bareRaises, helperDefs, calls };
}

// ---- self-test: prove the checks actually falsify -------------------------------------------------
function selfTest() {
  const fixtures = [
    {
      name: 'positive control (all RaiseSafe, no bare raise)',
      expectOk: true,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { foreach (EventHandler<T> s in h.GetInvocationList()) s(this, a); }
        void Broadcast() { RaiseSafe(TasksUpdated, tasks); RaiseSafe(MessageSent, msg); }
      `,
    },
    {
      name: 'bare null-conditional raise in CODE must FAIL',
      expectOk: false,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { }
        void Broadcast() { TasksUpdated?.Invoke(this, tasks); }
      `,
    },
    {
      name: 'captured-local raise in CODE must FAIL',
      expectOk: false,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { }
        void Notify() { var handler = SomeEvent; if (handler != null) handler.Invoke(this, args); }
      `,
    },
    {
      name: 'bare raise inside // line comment must PASS (masking works)',
      expectOk: true,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { }
        // old idiom was TasksUpdated?.Invoke(this, tasks);
        void Broadcast() { RaiseSafe(TasksUpdated, tasks); }
      `,
    },
    {
      name: 'bare raise inside /* block */ comment must PASS (masking works)',
      expectOk: true,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { }
        /* migration note: replaced Foo?.Invoke(this, x) with RaiseSafe */
        void Broadcast() { RaiseSafe(Foo, x); }
      `,
    },
    {
      name: 'bare raise inside a string literal must PASS (masking works)',
      expectOk: true,
      src: `
        private void RaiseSafe<T>(EventHandler<T> h, T a) { }
        void Broadcast() { LogTrace("was Evt.Invoke(this, args)"); RaiseSafe(Evt, args); }
      `,
    },
    {
      name: 'missing helper definition must FAIL',
      expectOk: false,
      src: `
        void Broadcast() { RaiseSafe(TasksUpdated, tasks); }
      `,
    },
  ];

  let pass = 0;
  for (const f of fixtures) {
    const r = checkSource(f.name, f.src);
    const got = r.ok;
    const good = got === f.expectOk;
    if (good) pass++;
    console.log(`${good ? 'PASS' : 'FAIL'}  [self-test] expect ${f.expectOk ? 'OK' : 'VIOLATION'} — ${f.name}` +
      (good ? '' : `  (got ${got ? 'OK' : 'VIOLATION'}: ${r.problems.join('; ')})`));
  }
  console.log(`\nself-test: ${pass}/${fixtures.length} fixtures behaved as expected.`);
  process.exit(pass === fixtures.length ? 0 : 1);
}

// ---- real check -----------------------------------------------------------------------------------
function realCheck() {
  const file = path.join(REPO_ROOT, TARGET);
  if (!fs.existsSync(file)) {
    console.error(`verify-raisesafe: target not found: ${file}`);
    process.exit(2);
  }
  const src = fs.readFileSync(file, 'utf8');
  const r = checkSource(TARGET, src);
  if (r.ok) {
    console.log(`PASS  ${TARGET}: 0 bare event-raises; RaiseSafe<T> helper defined (x${r.helperDefs}) and used at ${r.calls} call site(s).`);
    process.exit(0);
  }
  console.error(`FAIL  ${TARGET}:`);
  for (const p of r.problems) console.error(`  - ${p}`);
  console.error(`\nEvery event must be raised via RaiseSafe(MyEvent, args), not MyEvent?.Invoke(this, args).`);
  process.exit(1);
}

if (doSelfTest) selfTest();
else realCheck();
