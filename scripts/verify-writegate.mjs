#!/usr/bin/env node
// verify-writegate.mjs — falsifiable guard for the SqliteWriteGate fairness invariant (task a5ac5f71
// Phase 4; the machine-checked form of the folded ticket 7737d613).
//
// WHY THIS EXISTS. Phase 1 measured the defect: CodeGraph.ChunkedWritePass ran 9 write transactions
// across an 8.86s window with 0-1ms gaps (~99.6% write-lock duty cycle), starving a competitor that
// burned its full 5s busy_timeout without ever seeing a window wider than 1ms. The longest single
// hold (3151ms) was UNDER the timeout, so this was never a timeout bug — it was a FAIRNESS bug.
// Phase 2 fixed it with Services/SqliteWriteGate.cs, a process-wide queued gate.
//
// The fix is only as durable as its enforcement. Phase 2's "every write transaction is gated" was
// verified BY GREP, once, by hand. That is exactly the kind of guarantee that rots: the next person
// to add a transaction has no way to know the rule exists. This script makes it fail the build
// instead, in the same "enumerate, don't prose" dialect as verify-taskdb-gate.mjs / verify-logging.mjs
// / verify-writepath.mjs.
//
// THREE falsifiable checks (each has --self-test negative fixtures):
//
//   (1) TXN GATING — every real transaction-open call site (`<receiver>.BeginTransaction(`) in
//       production code must sit in a method that entered SqliteWriteGate FIRST. A missing gate, or a
//       gate acquired AFTER the transaction opens (which leaves the open racing unfairly), FAILS.
//       A transaction-open line the method walker cannot attribute also FAILS, so an oddly-formatted
//       site cannot hide. Runs on CODE ONLY (see maskCodeOnly): the comments and the method
//       DECLARATION `public SQLiteTransaction BeginTransaction()` are not call sites, and counting
//       them is precisely the mistake a naive grep makes here — Services/CodeGraphDatabase.cs looks
//       like 4 transaction sites to grep and is really 1.
//
//   (2) CO-EXTENSIVE ENTRY POINT (design property 4) — no production file may call
//       `WriteContentionDiagnostics.BeginWrite(` directly; only SqliteWriteGate may. The gate scope
//       owns BOTH the semaphore and the Phase 1 diagnostics registration, so routing around
//       EnterWrite to get the diagnostics alone would silently produce an OBSERVED-but-UNGATED write
//       — a site that looks instrumented in the busy dump while still racing. This keeps the gated
//       set and the observed set provably identical: one omission to catch, not two.
//
//   (3) BOUNDED ACQUIRE (design property 3) — SqliteWriteGate's own semaphore wait must pass a
//       timeout. A bare `Gate.Wait()` would wait forever and reintroduce an unbounded hang, and the
//       fail-soft fall-through exists specifically because some writes run on the UI thread
//       (PurgeOrphanEmptyNoteTabs from MainForm startup), where blocking behind a code-graph chunk
//       visibly freezes the app. This regression would be silent in review and loud only in
//       production, which is what makes it worth a guard.
//
// KNOWN LIMITATION — READ BEFORE TRUSTING A GREEN RUN. This census covers write TRANSACTIONS, which
// is the scope the Phase 2 plan specifies. It does NOT prove that every single-statement autocommit
// write is gated. That class is real: Phase 1 caught TaskDatabase.SaveTerminalActivity losing
// SQLITE_BUSY as a bare ExecuteNonQuery, swallowed by RaiseSafe, silently never writing its row and
// producing no busy dump because it was not a wrapped transaction. That one site is now gated, but
// autocommit writers as a CLASS are out of scope here — enumerating them means classifying SQL text
// that maskCodeOnly deliberately blanks. So: "green" means every write transaction is gated, NOT
// that every write is gated. Do not let this script's PASS line be read as the stronger claim.
//
// Usage:
//   node scripts/verify-writegate.mjs             # exit 1 on any violation
//   node scripts/verify-writegate.mjs --self-test # prove the checks falsify (negative fixtures)
//
// Adding a transaction? Wrap it: `using var writeGate = SqliteWriteGate.EnterWrite("Owner.Method", detail);`
// BEFORE the BeginTransaction call. Exemptions live in DELEGATING_ACCESSORS below — a NAMED,
// reviewable edit to THIS file, never an in-code sentinel a method can quietly apply to itself.

import fs from 'fs';
import path from 'path';

const doSelfTest = process.argv.slice(2).includes('--self-test');

const REPO_ROOT = path.join(
  path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');

// A real transaction OPEN: a call through a receiver. The bare declaration
// `public SQLiteTransaction BeginTransaction()` has no receiver and is therefore not matched —
// see check (1)'s note about CodeGraphDatabase.
const TXN_OPEN_RE = /\b[A-Za-z_]\w*\s*\.\s*BeginTransaction\s*\(/;
const GATE_RE = /\bSqliteWriteGate\s*\.\s*EnterWrite\s*\(/;
const RAW_DIAG_RE = /\bWriteContentionDiagnostics\s*\.\s*BeginWrite\s*\(/;

// Local (per-owner) lock acquisitions. Check (4) requires the GLOBAL gate to be taken before any of
// these. Mirrors the gate idioms in verify-taskdb-gate.mjs, minus the read-only variants.
const LOCAL_LOCK_RE = /using\s*\(?\s*var\s+\w+\s*=\s*LockConn\s*\(\s*\)|using\s*\(?\s*var\s+\w+\s*=\s*_gate\.Enter\s*\(\s*\)|using\s*\(?\s*var\s+\w+\s*=\s*Locked\s*\(\s*\)|lock\s*\(\s*_syncLock\s*\)|lock\s*\(\s*_dbLock\s*\)/;

const GATE_FILE = 'Services/SqliteWriteGate.cs';

// ── CHECK (5): NAMED REQUIRED-GATE LIST ───────────────────────────────────────────────────────────
// Check (1) can only see TRANSACTIONS. Single-statement autocommit writes also take the SQLite write
// lock, and there are ~204 ExecuteNonQuery sites across the owner files — far too many to demand a
// gate on (gating all of them would serialize the whole app's writes, which is a different and much
// riskier design). So instead of a sweep we pin the ones that DEMONSTRABLY contend, by name.
//
// Every entry here was gated in response to concrete evidence, not on suspicion. If a future edit
// drops the gate from one of these, this check fails — which is what makes the fix durable rather
// than grep-verified. Adding to this list is the correct response to any NEW autocommit writer found
// losing a race; it is deliberately a reviewable edit to THIS file.
const REQUIRED_GATED_METHODS = new Map([
  ['Services/TaskDatabase.cs::SaveTerminalActivity',
    'Phase 1 caught this losing SQLITE_BUSY (12:47:14.504) via ActivityService.UpdateActivity <- '
    + 'OnMcpTerminalRegistered; the loss was swallowed by MessageBroker.RaiseSafe so the activity row '
    + 'silently never wrote and no busy dump fired.'],
  ['Services/SessionLineageService.cs::RegisterSession',
    'Holds the SINGLE write-gate admission for the whole register_session unit. Load-bearing for the '
    + "endpoint's LATENCY BUDGET, not just fairness: the inner gates on CloseOpenSessions and "
    + 'SaveSessionLineage pass through reentrantly because of this wrap, so the path costs ONE acquire '
    + 'instead of two. Remove the wrap and the acquire term in SqliteBusyRetryTests.BudgetHeadroom_* '
    + 'doubles, pushing the endpoint past the MCP client 15s timeout — and because SqliteBusyRetry only '
    + 'checks its deadline BETWEEN attempts, the retry backstop would silently stop firing (a5ac5f71 '
    + 'pipeline Run 2, found by two independent gates).'],
  ['Services/TaskDatabase.cs::SaveSessionLineage',
    'THE ticket victim. register_session reaches here via SessionLineageService.RegisterSession. Found '
    + 'ungated by the pipeline Run 1 cross-model adversary (HIGH) after the first Phase 2 pass gated '
    + 'only the sites Phase 1 had instrumented.'],
  ['Services/TaskDatabase.cs::CloseOpenSessions',
    'The other half of the register_session write path (close prior open sessions, then upsert). Same '
    + 'Run 1 finding.'],
  ['Services/CodeGraphDatabase.cs::ClearProjectRelationships',
    'Largest-class indexer write (correlated-subquery DELETE over cg_relationships) running immediately '
    + 'before the gated chunk pass — an ungated STARVER, found by the Run 1 debugger gate.'],
  ['Services/CodeGraphDatabase.cs::ClearProject',
    'Same starver class as ClearProjectRelationships; multi-statement autocommit DELETEs.'],
  ['Services/CodeGraphDatabase.cs::ClearAll',
    'Same starver class; deletes every cg_ table.'],
]);

// Transaction opens that legitimately have NO gate of their own because they DELEGATE the
// transaction to a caller. Each entry must state why, and the delegated-to caller is still checked
// by (1) independently — so the invariant holds transitively rather than on trust. If someone adds a
// SECOND, ungated caller of a delegating accessor, that new CALL SITE fails check (1).
const DELEGATING_ACCESSORS = new Map([
  ['Services/CodeGraphDatabase.cs::BeginTransaction',
    'pure delegating accessor — hands the SQLiteTransaction to its caller and holds no write itself. '
    + 'Its only PRODUCTION caller is CSharpCodeGraphIndexer.ChunkedWritePass, which check (1) verifies '
    + 'IS gated; gating here too would double-enter (harmless, the gate is reentrant) but would '
    + 'misreport the owner in the busy dump as the accessor instead of the actual write pass. '
    + 'PRODUCTION is load-bearing in that sentence: MultiTerminal.Tests/CrossConnectionConcurrencyTests '
    + 'also calls this accessor UNGATED, and MultiTerminal.Tests is in SKIP_DIRS, so the transitive '
    + 'argument covers production callers only — a test caller is out of census scope by construction.'],
]);

// ── ORDERING EXEMPTIONS for check (4) ─────────────────────────────────────────────────────────────
// Global-before-local is the rule, but it is a means, not the end: the point is that no thread holds a
// contended lock while WAITING. There is one shape where hoisting the gate makes things strictly WORSE
// — when expensive NON-write work sits between the local lock and the transaction. Hoisting would then
// hold the process-wide WRITE gate across that work, blocking every other writer for its duration,
// which is the very starvation this whole ticket exists to remove. In that case the narrow convoy is
// the lesser evil and the site is exempted BY NAME with the reason.
const ORDERING_EXEMPT = new Map([
  ['Services/SessionMemoryDatabase.cs::IndexSessionFile',
    'Between `_gate.Enter()` and the transaction this method reads the JSONL, filters it, chunks it, and '
    + 'EMBEDS every chunk (embedder.Embed per chunk — ML inference, seconds for a large session). '
    + 'Hoisting EnterWrite above the owner lock would hold the global WRITE gate across all of that '
    + 'non-write work and starve every other writer — strictly worse than the convoy it would prevent. '
    + 'The convoy here is also narrow: SessionMemoryDatabase._gate is contended only by session-memory '
    + 'operations, not by TaskDatabase._dbLock\'s ~162 app-wide sites. The clean fix is to move the '
    + 'parse/embed phase outside the owner lock and then take global-then-local; that is a restructure '
    + 'of a method with several early returns and an under-lock IsSessionIndexed check, so it belongs in '
    + 'its own ticket rather than in a fairness fix.'],
]);

// Separate DB families — NOT multiterminal.db, so the multiterminal.db write gate does not apply.
// Mirrors verify-taskdb-gate.mjs's SEPARATE_DB so the two censuses agree on what is in scope.
const SEPARATE_DB = new Set([
  'Services/MessageQueueDatabase.cs',      // messages.db — the inter-terminal message queue.
  'Services/GatewayIntegrationService.cs', // McpGateway DB — a separate process/DB.
]);

// Build artifacts, the test project (tests drive an isolated temp DB and may open transactions on it
// without the production gate), and nested worktrees under .claude are out of scope.
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.claude', 'staged', 'Deploy',
  'packages', 'TestResults', '.vs', 'MultiTerminal.Tests']);

// Blank the CONTENTS of comments and string/char literals (preserving length and newlines) so
// detection sees CODE only. Same masker as verify-taskdb-gate.mjs — a real call never lives inside a
// comment or a string, and this codebase's SQL literals are full of words that would false-positive.
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

function stripAngleBrackets(s) {
  let prev;
  do { prev = s; s = s.replace(/<[^<>]*>/g, ''); } while (s !== prev);
  return s;
}

function methodName(sig) {
  const m = stripAngleBrackets(sig).match(/([A-Za-z_]\w*)\s*\(/);
  return m ? m[1] : null;
}

// Parse block-methods using this codebase's consistent Allman/8-space style (same walker as
// verify-taskdb-gate.mjs): an 8-space access-modifier signature containing `(`, a body opening at a
// lone 8-space `{`, closing at the next lone `        }`. Nested blocks sit at >=12 spaces so their
// braces never collide with the method frame.
function parseMethods(lines) {
  const methods = [];
  const ACCESS = /^ {8}(public|private|internal|protected)\b/;
  for (let i = 0; i < lines.length; i++) {
    if (!ACCESS.test(lines[i])) continue;
    let j = i;
    let sig = '';
    let aborted = false;
    while (j < lines.length && lines[j].trim() !== '{') {
      sig += lines[j] + ' ';
      if (/;\s*$/.test(lines[j]) || /=>/.test(lines[j])) { aborted = true; break; }
      j++;
    }
    if (aborted || j >= lines.length || lines[j].trim() !== '{' || !sig.includes('(')) continue;
    const name = methodName(sig);
    if (!name) continue;
    let k = j + 1;
    while (k < lines.length && lines[k] !== '        }') k++;
    methods.push({ name, sigStart: i, bodyOpen: j, bodyClose: k });
    i = k;
  }
  return methods;
}

// (1) TXN GATING for one file. `allowKey(name)` decides the DELEGATING_ACCESSORS exemption.
// Pure over `lines` so the self-test can feed it synthetic snippets with no disk access.
function analyzeTxnGating(lines, allowKey = () => false) {
  const methods = parseMethods(lines);
  const violations = [];
  const gated = [];
  const exempt = [];
  const attributed = new Array(lines.length).fill(false);

  for (const m of methods) {
    for (let k = m.bodyOpen; k <= m.bodyClose; k++) attributed[k] = true;
    const body = maskCodeOnly(lines.slice(m.bodyOpen + 1, m.bodyClose).join('\n'));
    const txnIdx = body.search(TXN_OPEN_RE);
    if (txnIdx === -1) continue; // no transaction opened here

    if (allowKey(m.name)) { exempt.push(m); continue; }

    const gateIdx = body.search(GATE_RE);
    if (gateIdx === -1) {
      violations.push({ m, kind: 'missing-gate',
        why: 'opens a transaction but never enters SqliteWriteGate — this write races unfairly '
          + '(add `using var writeGate = SqliteWriteGate.EnterWrite("Owner.Method", detail);`)' });
    } else if (gateIdx > txnIdx) {
      violations.push({ m, kind: 'gate-order',
        why: `enters the gate (offset ${gateIdx}) AFTER opening the transaction (offset ${txnIdx}) `
          + '— the open itself is still unfair; hoist the gate above BeginTransaction' });
    } else {
      gated.push(m);
    }
  }

  // A transaction-open the method walker could not attribute (expression body, unusual indentation)
  // must FAIL rather than pass silently unchecked.
  const unattributed = [];
  for (let i = 0; i < lines.length; i++) {
    if (attributed[i]) continue;
    if (TXN_OPEN_RE.test(maskCodeOnly(lines[i]))) unattributed.push(i + 1);
  }

  return { violations, gated, exempt, unattributed };
}

// (4) LOCK ORDERING — global gate BEFORE any local per-owner lock.
// The reverse order shipped in the first Phase 2 pass and was a real defect: the acquire can wait up
// to the budget, and serving that wait while holding TaskDatabase._dbLock stalls all ~162 LockConn
// sites (reads included) instead of just the writers. Any method using BOTH must gate first.
// (5) REQUIRED GATE — a named autocommit writer must still enter the gate.
// Pure over `lines` so the self-test can drive it with synthetic snippets.
function analyzeOrderingAndRequired(
  lines, relPath, requiredNames = new Set(), orderExempt = () => false, exemptNames = new Set()) {
  const methods = parseMethods(lines);
  const violations = [];
  const orderedOk = [];
  const requiredOk = [];
  const orderExempted = [];
  const seenRequired = new Set();

  for (const m of methods) {
    const body = maskCodeOnly(lines.slice(m.bodyOpen + 1, m.bodyClose).join('\n'));
    const gateIdx = body.search(GATE_RE);
    const localIdx = body.search(LOCAL_LOCK_RE);

    if (gateIdx !== -1 && localIdx !== -1) {
      if (orderExempt(m.name)) {
        orderExempted.push(m);
      } else if (localIdx < gateIdx) {
        violations.push({ m, kind: 'lock-order',
          why: `acquires its LOCAL lock (offset ${localIdx}) BEFORE the global write gate (offset `
            + `${gateIdx}). Global-before-local is mandatory: a gate wait served while holding the `
            + "owner's local lock stalls that owner's READS too, not just writers. Hoist the "
            + 'SqliteWriteGate.EnterWrite line above the local lock.' });
      } else {
        orderedOk.push(m);
      }
    }

    if (requiredNames.has(m.name)) {
      seenRequired.add(m.name);
      if (gateIdx === -1) {
        violations.push({ m, kind: 'required-gate-missing',
          why: 'is on the NAMED REQUIRED-GATE list in this script (it is a single-statement autocommit '
            + 'write with evidence of losing a real race) but does not enter SqliteWriteGate. Restore '
            + 'the gate, or remove it from REQUIRED_GATED_METHODS with a documented reason.' });
      } else {
        requiredOk.push(m);
      }
    }
  }

  // A required method that VANISHED (renamed/deleted) must fail too — otherwise the guarantee quietly
  // evaporates on rename and the list rots into decoration.
  const missing = [...requiredNames].filter(n => !seenRequired.has(n));

  // Same trap for the ORDERING exemptions. An exemption is a documented WAIVER of a safety rule; if the
  // method it waives no longer exists, the waiver must not silently persist and quietly pre-authorize a
  // future method that happens to reuse the name. (verify-logging.mjs applies the same rule to its
  // allowlist; check (5) above already did — (4) was the inconsistent one.)
  const staleExempt = [...exemptNames].filter(n => !methods.some(m => m.name === n));

  return { violations, orderedOk, requiredOk, orderExempted, missing, staleExempt, relPath };
}

// (2) Direct WriteContentionDiagnostics.BeginWrite use — only the gate itself may.
function findRawDiagnostics(lines) {
  const hits = [];
  for (let i = 0; i < lines.length; i++) {
    if (RAW_DIAG_RE.test(maskCodeOnly(lines[i]))) hits.push(i + 1);
  }
  return hits;
}

// (3) The gate's acquire must be BOUNDED: a wait with an argument, never a bare Wait().
// Returns a violation string, or null when the invariant holds.
function checkBoundedAcquire(src) {
  const code = maskCodeOnly(src);
  if (/\bGate\s*\.\s*Wait\s*\(\s*\)/.test(code)) {
    return 'SqliteWriteGate performs a BARE `Gate.Wait()` — an unbounded wait. Property 3 of the '
      + 'design requires a timeout so a UI-thread write can never block behind a code-graph chunk; '
      + 'pass the acquire budget (timeoutMs).';
  }
  if (!/\bGate\s*\.\s*Wait\s*\(\s*[^)\s]/.test(code)) {
    return 'SqliteWriteGate has no recognizable bounded `Gate.Wait(<timeout>)` acquire — the gate '
      + 'must acquire with an explicit timeout (or this guard has gone stale and needs updating '
      + 'alongside the refactor).';
  }
  return null;
}

function scanFiles(root) {
  const files = [];
  function walk(dir) {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      if (e.isDirectory()) {
        if (SKIP_DIRS.has(e.name)) continue;
        walk(path.join(dir, e.name));
      } else if (e.isFile() && e.name.endsWith('.cs')) {
        files.push(path.join(dir, e.name));
      }
    }
  }
  walk(root);
  return files;
}

// ---- self-test: negative fixtures prove each check falsifies. A census that cannot fail is theatre.
function selfTest() {
  let allOk = true;
  // Counted and PRINTED (like verify-logging's "SELF-TEST PASSED (n/n)"): a hand-derived fixture count
  // in a commit message was already wrong once. Let the script be the source of truth.
  let ran = 0;
  const report = (label, name, got, exp) => {
    const ok = got === exp;
    allOk = allOk && ok;
    ran++;
    console.log(`  ${ok ? 'ok  ' : 'FAIL'} [${label}] ${name} → ${got ? 'FLAGGED' : 'passed'} (expected ${exp ? 'FLAGGED' : 'passed'})`);
  };

  const gateCases = [
    { name: 'positive: gate entered before BeginTransaction', exp: false,
      code: '        public void Good()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.Good");\n            using var tx = _connection.BeginTransaction();\n        }' },
    { name: 'positive: block-form using + gate first', exp: false,
      code: '        public void GoodBlock()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.GoodBlock");\n            using (var tx = _connection.BeginTransaction())\n            {\n            }\n        }' },
    { name: 'positive: gated txn opened through another owner (_db receiver)', exp: false,
      code: '        private void Chunk()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("CodeGraph.ChunkedWritePass");\n            var txn = _db.BeginTransaction();\n        }' },
    { name: '(a) THE regression: gate deleted, transaction remains', exp: true,
      code: '        public void GateDeleted()\n        {\n            using var tx = _connection.BeginTransaction();\n        }' },
    { name: '(b) gate acquired AFTER the transaction opens', exp: true,
      code: '        public void OutOfOrder()\n        {\n            using var tx = _connection.BeginTransaction();\n            using var writeGate = SqliteWriteGate.EnterWrite("X.OutOfOrder");\n        }' },
    { name: '(c) only the OLD diagnostics call, no gate (the Phase 1 -> Phase 2 backslide)', exp: true,
      code: '        public void DiagOnly()\n        {\n            using var contention = WriteContentionDiagnostics.BeginWrite("X.DiagOnly");\n            using var tx = _connection.BeginTransaction();\n        }' },
    { name: '(d) comment-only mention of the gate does not satisfy it', exp: true,
      code: '        public void CommentOnly()\n        {\n            // remember SqliteWriteGate.EnterWrite("X") here\n            using var tx = _connection.BeginTransaction();\n        }' },
    { name: 'negative-control: no transaction at all is not flagged', exp: false,
      code: '        public void NoTxn()\n        {\n            using var cmd = new SQLiteCommand("SELECT 1", _connection);\n        }' },
    { name: 'negative-control: the BeginTransaction DECLARATION is not a call site', exp: false,
      code: '        public SQLiteTransaction BeginTransaction()\n        {\n            return null;\n        }' },
  ];
  for (const c of gateCases) {
    const r = analyzeTxnGating(c.code.split('\n'));
    report('txn', c.name, r.violations.length > 0 || r.unattributed.length > 0, c.exp);
  }

  // The exemption must be NAMED, and must not become a blanket pass for the whole file.
  const accessor = '        public SQLiteTransaction BeginTransaction()\n        {\n            using var g = Locked();\n            return _connection.BeginTransaction();\n        }';
  report('exempt', 'a NAMED delegating accessor is exempt',
    analyzeTxnGating(accessor.split('\n'), n => n === 'BeginTransaction').violations.length > 0, false);
  report('exempt', 'the SAME accessor without the exemption FAILS (proves the entry is load-bearing)',
    analyzeTxnGating(accessor.split('\n')).violations.length > 0, true);
  const otherMethod = '        public void Unrelated()\n        {\n            using var tx = _connection.BeginTransaction();\n        }';
  report('exempt', 'exempting one method does NOT exempt a sibling in the same file',
    analyzeTxnGating(otherMethod.split('\n'), n => n === 'BeginTransaction').violations.length > 0, true);

  // Unattributed sites must fail rather than slip through unchecked.
  report('attrib', 'a transaction open outside any parsed method FAILS',
    analyzeTxnGating(['    var tx = _connection.BeginTransaction();']).unattributed.length > 0, true);

  const diagCases = [
    { name: 'a direct WriteContentionDiagnostics.BeginWrite call is detected', exp: true,
      code: 'using var c = WriteContentionDiagnostics.BeginWrite("X");' },
    { name: 'a commented-out one is not', exp: false,
      code: '// using var c = WriteContentionDiagnostics.BeginWrite("X");' },
    { name: 'the gate call is not mistaken for it', exp: false,
      code: 'using var g = SqliteWriteGate.EnterWrite("X");' },
  ];
  for (const c of diagCases) {
    report('coext', c.name, findRawDiagnostics(c.code.split('\n')).length > 0, c.exp);
  }

  // (4) lock ordering — the defect that shipped in the first Phase 2 pass must now FAIL.
  const orderCases = [
    { name: 'positive: gate BEFORE LockConn', exp: false,
      code: '        public void Good()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.Good");\n            using var gate = LockConn();\n        }' },
    { name: 'THE Run-1 defect: LockConn BEFORE gate must FAIL', exp: true,
      code: '        public void Bad()\n        {\n            using var gate = LockConn();\n            using var writeGate = SqliteWriteGate.EnterWrite("X.Bad");\n        }' },
    { name: 'THE Run-1 defect, _syncLock form: lock() before gate must FAIL', exp: true,
      code: '        public void BadSync()\n        {\n            lock (_syncLock)\n            {\n                using var writeGate = SqliteWriteGate.EnterWrite("X.BadSync");\n            }\n        }' },
    { name: 'positive: gate before lock (_syncLock)', exp: false,
      code: '        public void GoodSync()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.GoodSync");\n            lock (_syncLock)\n            {\n            }\n        }' },
    { name: 'positive: gate before _gate.Enter()', exp: false,
      code: '        public void GoodOwnerGate()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.GoodOwnerGate");\n            using var g = _gate.Enter();\n        }' },
    { name: 'negative-control: local lock with NO gate is not an ordering violation', exp: false,
      code: '        public void LockOnly()\n        {\n            using var gate = LockConn();\n        }' },
    { name: 'negative-control: gate with no local lock is not an ordering violation', exp: false,
      code: '        public void GateOnly()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X.GateOnly");\n        }' },
    { name: 'a comment mentioning LockConn does not create a false ordering violation', exp: false,
      code: '        public void CommentLock()\n        {\n            // using var gate = LockConn(); would go here\n            using var writeGate = SqliteWriteGate.EnterWrite("X.CommentLock");\n        }' },
  ];
  for (const c of orderCases) {
    const r = analyzeOrderingAndRequired(c.code.split('\n'), 'Fixture.cs');
    report('order', c.name, r.violations.some(v => v.kind === 'lock-order'), c.exp);
  }

  // The ordering exemption must be NAMED, load-bearing, and must not leak to siblings.
  const badOrder = '        public void IndexSessionFile()\n        {\n            using var gate = _gate.Enter();\n            using var writeGate = SqliteWriteGate.EnterWrite("X");\n        }';
  report('order-exempt', 'a NAMED ordering exemption passes despite local-before-gate',
    analyzeOrderingAndRequired(badOrder.split('\n'), 'Fixture.cs', new Set(), n => n === 'IndexSessionFile')
      .violations.some(v => v.kind === 'lock-order'), false);
  report('order-exempt', 'the SAME method without the exemption FAILS (proves the entry is load-bearing)',
    analyzeOrderingAndRequired(badOrder.split('\n'), 'Fixture.cs')
      .violations.some(v => v.kind === 'lock-order'), true);
  report('order-exempt', 'a STALE ordering exemption (method gone) must FAIL, like the required-gate trap',
    analyzeOrderingAndRequired(
      '        public void SomethingElse()\n        {\n        }'.split('\n'),
      'Fixture.cs', new Set(), n => n === 'IndexSessionFile', new Set(['IndexSessionFile'])).staleExempt.length > 0, true);
  report('order-exempt', 'a LIVE ordering exemption is not reported stale',
    analyzeOrderingAndRequired(badOrder.split('\n'), 'Fixture.cs', new Set(),
      n => n === 'IndexSessionFile', new Set(['IndexSessionFile'])).staleExempt.length > 0, false);
  report('order-exempt', 'exempting one method does NOT exempt a sibling with the same defect',
    analyzeOrderingAndRequired(
      '        public void OtherWrite()\n        {\n            using var gate = _gate.Enter();\n            using var writeGate = SqliteWriteGate.EnterWrite("X");\n        }'.split('\n'),
      'Fixture.cs', new Set(), n => n === 'IndexSessionFile').violations.some(v => v.kind === 'lock-order'), true);

  // (5) named required-gate list.
  const requiredOne = new Set(['SaveSessionLineage']);
  report('required', 'a required autocommit writer WITH the gate passes',
    analyzeOrderingAndRequired(
      '        public void SaveSessionLineage()\n        {\n            using var writeGate = SqliteWriteGate.EnterWrite("X");\n            using var gate = LockConn();\n        }'.split('\n'),
      'Fixture.cs', requiredOne).violations.some(v => v.kind === 'required-gate-missing'), false);
  report('required', 'THE regression: gate REMOVED from a required writer must FAIL',
    analyzeOrderingAndRequired(
      '        public void SaveSessionLineage()\n        {\n            using var gate = LockConn();\n        }'.split('\n'),
      'Fixture.cs', requiredOne).violations.some(v => v.kind === 'required-gate-missing'), true);
  report('required', 'a required writer RENAMED away must FAIL (list cannot rot into decoration)',
    analyzeOrderingAndRequired(
      '        public void SaveSessionLineageV2()\n        {\n            using var gate = LockConn();\n        }'.split('\n'),
      'Fixture.cs', requiredOne).missing.length > 0, true);
  report('required', 'an unlisted method without the gate is NOT flagged by check (5)',
    analyzeOrderingAndRequired(
      '        public void SomeOtherWrite()\n        {\n            using var gate = LockConn();\n        }'.split('\n'),
      'Fixture.cs', requiredOne).violations.some(v => v.kind === 'required-gate-missing'), false);

  const boundedCases = [
    { name: 'bounded `Gate.Wait(timeoutMs)` passes', exp: false, code: 'acquired = Gate.Wait(timeoutMs);' },
    { name: 'THE regression: bare `Gate.Wait()` is unbounded and FAILS', exp: true, code: 'acquired = Gate.Wait();' },
    { name: 'no wait at all FAILS (guard cannot silently go stale)', exp: true, code: 'acquired = true;' },
    { name: 'a commented-out bounded wait does not satisfy the guard', exp: true, code: '// acquired = Gate.Wait(timeoutMs);' },
  ];
  for (const c of boundedCases) {
    report('bounded', c.name, checkBoundedAcquire(c.code) !== null, c.exp);
  }

  console.log(allOk
    ? `\nSELF-TEST PASSED (${ran}/${ran}) — txn-gating, exemptions, attribution, co-extensiveness, `
      + 'lock ordering, required-gate and boundedness all provably reject the bad shapes.'
    : `\nSELF-TEST FAILED (${ran} fixtures ran) — a check does not falsify correctly.`);
  process.exit(allOk ? 0 : 1);
}

if (doSelfTest) selfTest();

// -------------------------------------------------------------------------------------
let failed = false;
const problems = [];
let totalGated = 0;
let totalExempt = 0;
let totalOrdered = 0;
let totalRequired = 0;
let totalOrderExempt = 0;
let autocommitSiteCount = 0;
const filesWithTxns = [];

for (const abs of scanFiles(REPO_ROOT)) {
  const rel = path.relative(REPO_ROOT, abs).replace(/\\/g, '/');
  const src = fs.readFileSync(abs, 'utf8');
  const lines = src.split(/\r?\n/);

  // (2) applies to EVERY production file, including ones with no transactions: routing around the
  // single entry point is the failure this catches, and it can happen anywhere.
  if (rel !== GATE_FILE) {
    for (const ln of findRawDiagnostics(lines)) {
      problems.push(`${rel}:${ln} CO-EXTENSIVE — calls WriteContentionDiagnostics.BeginWrite directly; `
        + 'go through SqliteWriteGate.EnterWrite so the write is GATED as well as observed');
    }
  }

  // Self-counted so the printed scope caveat states a REAL number. A hand-derived count in this file's
  // own commit message was already wrong once (fixtures: "32" vs the actual 35), which is exactly the
  // failure mode a census exists to prevent — so the script counts its own denominators.
  autocommitSiteCount += (maskCodeOnly(src).match(/\bExecuteNonQuery\s*\(/g) || []).length;

  if (SEPARATE_DB.has(rel)) continue; // different DB file — the multiterminal.db gate does not apply

  // (4) + (5)
  const requiredNames = new Set(
    [...REQUIRED_GATED_METHODS.keys()]
      .filter(k => k.startsWith(`${rel}::`))
      .map(k => k.slice(rel.length + 2)));
  const exemptNames = new Set(
    [...ORDERING_EXEMPT.keys()]
      .filter(k => k.startsWith(`${rel}::`))
      .map(k => k.slice(rel.length + 2)));
  const ord = analyzeOrderingAndRequired(
    lines, rel, requiredNames, name => ORDERING_EXEMPT.has(`${rel}::${name}`), exemptNames);
  totalOrderExempt += ord.orderExempted.length;
  for (const name of ord.staleExempt) {
    problems.push(`${rel} ORDERING EXEMPTION — method ${name}() is in ORDERING_EXEMPT but was NOT FOUND in `
      + 'this file (renamed, moved, or deleted). Remove the waiver in the same commit — a stale exemption '
      + 'silently pre-authorizes any future method that reuses the name.');
  }
  for (const v of ord.violations) {
    problems.push(`${rel}:${v.m.sigStart + 1} ${v.kind === 'lock-order' ? 'LOCK ORDERING' : 'REQUIRED GATE'} `
      + `in ${v.m.name}(): ${v.why}`);
  }
  for (const name of ord.missing) {
    problems.push(`${rel} REQUIRED GATE — method ${name}() is on the REQUIRED_GATED_METHODS list but was `
      + 'NOT FOUND in this file (renamed, moved, or deleted). Update the list in the same commit so the '
      + 'guarantee does not silently evaporate on rename.');
  }
  totalOrdered += ord.orderedOk.length;
  totalRequired += ord.requiredOk.length;

  const allowKey = name => DELEGATING_ACCESSORS.has(`${rel}::${name}`);
  const r = analyzeTxnGating(lines, allowKey);
  if (r.gated.length || r.exempt.length || r.violations.length || r.unattributed.length) {
    filesWithTxns.push({ rel, r });
  }
  totalGated += r.gated.length;
  totalExempt += r.exempt.length;
  for (const v of r.violations) {
    problems.push(`${rel}:${v.m.sigStart + 1} TXN GATING [${v.kind}] in ${v.m.name}(): ${v.why}`);
  }
  for (const ln of r.unattributed) {
    problems.push(`${rel}:${ln} TXN GATING — transaction open not attributable to a parsed method, `
      + 'so its gating cannot be verified; reformat it into a normal method body');
  }
}

// (3) the gate's own boundedness.
const gateAbs = path.join(REPO_ROOT, GATE_FILE);
if (!fs.existsSync(gateAbs)) {
  problems.push(`${GATE_FILE} NOT FOUND — the write gate is the subject of this invariant; if it was `
    + 'renamed or removed, update GATE_FILE (and reconsider whether the fairness fix still exists).');
} else {
  const boundedViolation = checkBoundedAcquire(fs.readFileSync(gateAbs, 'utf8'));
  if (boundedViolation) problems.push(`${GATE_FILE} BOUNDED ACQUIRE — ${boundedViolation}`);
}

if (problems.length) {
  failed = true;
  console.log(`\nFAIL (${problems.length}):`);
  for (const p of problems) console.log(`  - ${p}`);
} else {
  console.log('Write-gate census (a5ac5f71 Phase 4 — machine-checked form of folded 7737d613):');
  for (const { rel, r } of filesWithTxns.sort((a, b) => a.rel.localeCompare(b.rel))) {
    const bits = [`${r.gated.length} gated`];
    if (r.exempt.length) bits.push(`${r.exempt.length} exempt delegating accessor`);
    console.log(`  ok  ${rel} — ${bits.join(', ')}`);
  }
  console.log(`\n  write transactions gated: ${totalGated}`);
  console.log(`  named delegating-accessor exemptions: ${totalExempt}`);
  for (const [key, why] of DELEGATING_ACCESSORS) console.log(`    - ${key}: ${why.split(' — ')[0]}`);
  console.log(`  methods taking BOTH gate and a local lock, in correct global-before-local order: ${totalOrdered}`);
  console.log(`  named ordering exemptions (hoisting would hold the gate across expensive non-write work): ${totalOrderExempt}`);
  for (const key of ORDERING_EXEMPT.keys()) console.log(`    - ${key}`);
  console.log(`  named autocommit writers required to stay gated: ${totalRequired}/${REQUIRED_GATED_METHODS.size}`);
  for (const key of REQUIRED_GATED_METHODS.keys()) console.log(`    - ${key}`);
  console.log('\nSCOPE (a green run does NOT mean "every write is gated"): check (1) covers write '
    + 'TRANSACTIONS. Single-statement autocommit writes are covered ONLY for the names in check (5) — '
    + `${REQUIRED_GATED_METHODS.size} methods with concrete evidence of losing a race — out of `
    + `${autocommitSiteCount} ExecuteNonQuery sites counted across the scanned files. Demanding a gate on `
    + 'all of them would serialize '
    + "the whole app's writes, which is a different and riskier design. So an ungated autocommit write "
    + 'that has never been observed contending can still be added without failing this census; the '
    + 'correct response to finding a new one is to gate it AND add it to REQUIRED_GATED_METHODS.');
  console.log('\nPASS — every write transaction enters SqliteWriteGate before opening, every named '
    + 'autocommit writer is still gated, gate-before-local-lock ordering holds, the gate is the sole '
    + 'entry point to the contention census, and its acquire is bounded.');
}

process.exit(failed ? 1 : 0);
