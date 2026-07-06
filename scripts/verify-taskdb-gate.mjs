#!/usr/bin/env node
// verify-taskdb-gate.mjs — falsifiable guard for TaskDatabase's single-connection gate.
//
// Task ad08caac (item 5). TaskDatabase.cs serves concurrent callers over ONE shared
// SQLiteConnection. Every RUNTIME access to `_connection` must be serialized behind the
// `_dbLock` Monitor via the scope-guard `using var gate = LockConn();`. This script is the
// falsifiable assertion that no `_connection` use escapes the gate.
//
// A method is EXEMPT (allowed to touch `_connection` directly) iff its name matches the
// allowlist — the ONLY exemption mechanism, by design (task ad08caac): a NAME PATTERN, not
// an in-code sentinel, so a new method cannot quietly opt itself out. Changing the
// allowlist is an explicit, reviewable edit to THIS file.
//
// Allowlist rationale: InitializeDatabase / CreateSchema / Migrate* / Dispose all run
// single-threaded at construction or teardown, before the connection is shared.
// SeedDefaultProfiles is called only from InitializeDatabase; UniqueNoteTabName only from
// MigrateNormalizeNoteTabPaths — both init-only helpers.
//
// Usage:
//   node scripts/verify-taskdb-gate.mjs           # --check (default): exit 1 on any violation
//   node scripts/verify-taskdb-gate.mjs --fix     # one-time codemod: insert gates + converge lock(_dbLock)
//
// Checks (all must pass):
//   1. Every method that references `_connection`, is not allowlisted, contains the gate.
//   2. In gated methods, the first gate acquisition precedes the first `_connection` use
//      (no connection touch before the lock is held).
//   3. No `_connection`-referencing method is `async` or an iterator (`yield`) — a
//      `using var` guard in those acquires late / holds across suspension.
//   4. No `_connection` textual use is left unattributed to a method body (catches
//      expression-bodied or otherwise unparsed sites), except the field declaration.

import fs from 'fs';
import path from 'path';

const args = process.argv.slice(2);
const fix = args.includes('--fix');
const fileArg = args.find(a => a.endsWith('.cs'));
const csPath = fileArg
  ? fileArg
  : path.join(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..', 'Services', 'TaskDatabase.cs');

const ALLOWLIST = name =>
  /^Migrate/.test(name) ||
  name === 'CreateSchema' ||
  name === 'InitializeDatabase' ||
  name === 'Dispose' ||
  name === 'SeedDefaultProfiles' ||
  name === 'UniqueNoteTabName';

const GATE_LINE = '            using var gate = LockConn();';

function stripAngleBrackets(s) {
  let prev;
  do { prev = s; s = s.replace(/<[^<>]*>/g, ''); } while (s !== prev);
  return s;
}

function methodName(sig) {
  const flat = stripAngleBrackets(sig);
  const m = flat.match(/([A-Za-z_]\w*)\s*\(/);
  return m ? m[1] : null;
}

// Parse the file's block-methods using this file's consistent Allman/8-space style:
// a method body opens with a lone `        {` (8 spaces) after an 8-space access-modifier
// signature containing `(`, and closes with the next lone `        }` (8 spaces). Nested
// blocks live at >=12 spaces, so their braces never collide with the 8-space method frame,
// and braces inside string/SQL literals are never a whole-line `        {`/`        }`.
function parseMethods(lines) {
  const methods = [];
  const ACCESS = /^        (public|private|internal|protected)\b/;
  for (let i = 0; i < lines.length; i++) {
    if (!ACCESS.test(lines[i])) continue;
    // gather signature lines up to (but not including) the opening-brace line
    let j = i;
    let sig = '';
    let aborted = false;
    while (j < lines.length && lines[j].trim() !== '{') {
      sig += lines[j] + ' ';
      if (/;\s*$/.test(lines[j]) || /=>/.test(lines[j])) { aborted = true; break; } // field / auto-prop / expression body
      j++;
    }
    if (aborted || j >= lines.length || lines[j].trim() !== '{' || !sig.includes('(')) continue;
    const name = methodName(sig);
    if (!name) continue;
    const bodyOpen = j; // index of the `        {`
    let k = j + 1;
    while (k < lines.length && lines[k] !== '        }') k++;
    const bodyClose = k; // index of the `        }`
    methods.push({ name, sigStart: i, bodyOpen, bodyClose });
    i = k;
  }
  return methods;
}

function bodyText(lines, m) {
  return lines.slice(m.bodyOpen + 1, m.bodyClose).join('\n');
}

// ---- lock(_dbLock) -> scope-guard convergence (fix mode) ----------------------------
// STRIPS each `            lock (_dbLock)` + `            {` ... `            }` wrapper,
// de-indenting the block body by 4 spaces. It deliberately does NOT emit the gate line
// here — the subsequent insert pass adds exactly ONE top-of-method gate. This keeps a
// method with two lock blocks from getting two `using var gate` declarations (CS0128),
// and unifies the idiom to one gate per method (held from method entry).
function convergeLockBlocks(lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (lines[i] === '            lock (_dbLock)' && lines[i + 1] === '            {') {
      // find matching 12-space close (nested blocks live at >=16 spaces, so the next
      // lone `            }` at 12 spaces is this block's close)
      let close = -1;
      for (let k = i + 2; k < lines.length; k++) {
        if (lines[k] === '            }') { close = k; break; }
      }
      if (close === -1) { out.push(lines[i]); continue; } // malformed; leave as-is
      for (let k = i + 2; k < close; k++) {
        const l = lines[k];
        out.push(l.startsWith('    ') ? l.slice(4) : l); // de-indent body by 4
      }
      i = close; // drop the closing brace line
      continue;
    }
    out.push(lines[i]);
  }
  return out;
}

function analyze(lines) {
  const methods = parseMethods(lines);
  const violations = [];
  const gatedMethods = [];

  for (const m of methods) {
    const body = bodyText(lines, m);
    const refsConn = /\b_connection\b/.test(body);
    if (!refsConn) continue;
    gatedMethods.push(m);
    const allow = ALLOWLIST(m.name);
    const gateIdx = body.indexOf('LockConn(');
    const connIdx = body.search(/\b_connection\b/);
    const isAsync = / async /.test(lines[m.sigStart]) || /\basync\b/.test(bodyText(lines, m));
    const isIterator = /\byield\s+(return|break)\b/.test(body);

    if (allow) continue; // exempt: init/dispose/migrate
    if (gateIdx === -1) {
      violations.push({ m, why: 'references _connection but has no LockConn() gate' });
    } else if (connIdx !== -1 && connIdx < gateIdx) {
      violations.push({ m, why: `touches _connection (offset ${connIdx}) BEFORE acquiring the gate (offset ${gateIdx})` });
    }
    if (isAsync) violations.push({ m, why: 'is async — a using-var guard holds across suspension' });
    if (isIterator) violations.push({ m, why: 'is an iterator (yield) — a using-var guard acquires lazily' });
  }

  // Check 4: no unattributed _connection line (besides the field declaration).
  const attributed = new Array(lines.length).fill(false);
  for (const m of gatedMethods) {
    for (let k = m.bodyOpen; k <= m.bodyClose; k++) attributed[k] = true;
  }
  const unattributed = [];
  let exposesConnection = false;
  for (let i = 0; i < lines.length; i++) {
    if (!/\b_connection\b/.test(lines[i])) continue;
    if (attributed[i]) continue;
    const t = lines[i].trim();
    if (t.startsWith('//') || t.startsWith('*') || t.startsWith('/*')) continue; // comment mention
    if (/private\s+SQLiteConnection\s+_connection;/.test(t)) continue;            // the field decl (home)
    // The `internal SQLiteConnection Connection => _connection;` accessor hands the raw
    // handle to KnowledgeDatabase, which runs ungated commands on it. That is a real
    // cross-class hole in "serialize all connection access" — report it distinctly.
    if (/\bConnection\s*=>\s*_connection\s*;/.test(t)) { exposesConnection = true; continue; }
    unattributed.push(i + 1);
  }

  return { methods, gatedMethods, violations, unattributed, exposesConnection };
}

// -------------------------------------------------------------------------------------
let src = fs.readFileSync(csPath, 'utf8');
const nl = src.includes('\r\n') ? '\r\n' : '\n';
let lines = src.split(/\r?\n/);

if (fix) {
  // 1) converge existing lock(_dbLock) blocks onto the scope-guard
  lines = convergeLockBlocks(lines);
  // 2) insert a top-of-body gate into every remaining ungated _connection method
  const { violations } = analyze(lines);
  // apply bottom-up so indices stay valid
  const inserts = violations
    .filter(v => /no LockConn/.test(v.why))
    .map(v => v.m.bodyOpen)
    .sort((a, b) => b - a);
  for (const openIdx of inserts) {
    lines.splice(openIdx + 1, 0, GATE_LINE);
  }
  fs.writeFileSync(csPath, lines.join(nl));
  const after = analyze(lines);
  console.log(`[fix] converged lock blocks + inserted ${inserts.length} gates; remaining violations: ${after.violations.length}, unattributed: ${after.unattributed.length}`);
  process.exit(0);
}

const { methods, gatedMethods, violations, unattributed, exposesConnection } = analyze(lines);
console.log(`Parsed ${methods.length} block-methods; ${gatedMethods.length} reference _connection.`);
let failed = false;
if (violations.length) {
  failed = true;
  console.log(`\n❌ ${violations.length} gate violation(s):`);
  for (const v of violations) console.log(`  - ${v.m.name} (line ${v.m.sigStart + 1}): ${v.why}`);
}
if (unattributed.length) {
  failed = true;
  console.log(`\n❌ ${unattributed.length} unattributed _connection use(s) (not inside any analyzed method body): lines ${unattributed.join(', ')}`);
}
if (exposesConnection) {
  // Not a TaskDatabase.cs gate violation, but a real cross-class hole: the shared handle
  // is used by SIX sibling classes (KnowledgeDatabase, CodeGraphDatabase,
  // SessionMemoryDatabase, BranchMetadataService, OwnerProfileService,
  // SourceControlAccountService), two of them under their OWN private locks — so the
  // handle is NOT globally race-free. Routing all consumers through one gate is tracked
  // in ticket bb2b0104. This is a STANDING tripwire: keep ALLOW_CONNECTION_EXPOSURE true
  // until bb2b0104 removes the raw accessor (or gates every consumer), then flip to false
  // so a SEVENTH consumer added later fails the check loudly.
  const ALLOW_CONNECTION_EXPOSURE = true;
  const marker = ALLOW_CONNECTION_EXPOSURE ? '⚠️ ' : '❌';
  console.log(`\n${marker} TaskDatabase exposes the raw connection via \`internal SQLiteConnection Connection => _connection;\`.`);
  console.log(`   6 sibling classes run commands on this shared handle (2 under their own private locks) —`);
  console.log(`   the handle is NOT globally race-free. Tracked in ticket bb2b0104 (the deferred`);
  console.log(`   "dc33e59c-style" cross-service serialization the codebase already knew it owed).`);
  if (!ALLOW_CONNECTION_EXPOSURE) failed = true;
}
if (!failed) {
  console.log(`\n✅ All ${gatedMethods.length} _connection-referencing methods in TaskDatabase.cs are gated (or allowlisted). No in-file _connection use escapes _dbLock.`);
}
process.exit(failed ? 1 : 0);
