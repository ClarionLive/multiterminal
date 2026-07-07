#!/usr/bin/env node
// verify-taskdb-gate.mjs — falsifiable guard for the "one owner per SQLite connection" invariant.
//
// Ticket bb2b0104 generalized ad08caac's single-class check to EVERY class that owns a
// connection to multiterminal.db. The invariant this asserts:
//
//   Every SQLite connection has exactly ONE owner class; the owner serializes its own
//   multithreaded access behind a per-connection gate; NO class touches another's handle.
//
// Three falsifiable checks run against each owner class in MANIFEST below:
//
//   (1) GATE — every method that references `_connection`, and isn't allowlisted (init /
//       schema / migrate / dispose / ctor — all single-threaded before the connection is
//       shared), must acquire the class's gate as an early statement, and must acquire it
//       BEFORE the first `_connection` use. Bare gate calls, comment mentions, async/iterator
//       methods all FAIL (a `using`-scoped Monitor guard must not be skipped or held across a
//       suspension). Detection runs on CODE ONLY (see maskCodeOnly) so a doc comment that
//       mentions the gate or `_connection` never counts.
//
//   (2) NO EXPOSURE (bb2b0104 condition 4; the flipped ALLOW_CONNECTION_EXPOSURE) — no owner
//       may expose its handle via a `SQLiteConnection Connection` property. The pre-bb2b0104
//       escape hatch (TaskDatabase.Connection / ProjectDatabase.Connection) was DELETED; if
//       one reappears — e.g. a 7th consumer is "made to work" by re-adding a borrow point —
//       this FAILS the build. A new feature that needs the DB opens its OWN connection via
//       MultiterminalDb.Open(); it never gets a handle from a sibling.
//
//   (3) FACTORY-ONLY OPEN (bb2b0104 condition 2) — no owner may `new SQLiteConnection(...)`
//       directly; the one factory MultiterminalDb.Open() is the sole open-site so WAL /
//       pooling / busy_timeout can't drift per-owner. (The factory itself and unrelated DB
//       families — McpGateway, the message queue — are not in MANIFEST and not checked.)
//
// Usage:
//   node scripts/verify-taskdb-gate.mjs             # --check (default): exit 1 on any violation
//   node scripts/verify-taskdb-gate.mjs --self-test # prove the checks falsify (negative fixtures)
//
// Adding a new connection-owning class? Add it to MANIFEST. Exemptions are a NAME-PATTERN
// allowlist per class (never an in-code sentinel), so a method cannot quietly opt itself out —
// changing an allowlist is an explicit, reviewable edit to THIS file.

import fs from 'fs';
import path from 'path';

const args = process.argv.slice(2);
const doSelfTest = args.includes('--self-test');

const REPO_ROOT = path.join(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');

// Gate idioms (regexes matched on CODE-ONLY text). A gate is satisfied ONLY by a real
// scope-guard/lock STATEMENT — never a bare call or a comment mention.
const GATE = {
  lockConn: /using\s*\(?\s*var\s+\w+\s*=\s*LockConn\s*\(\s*\)/,          // TaskDatabase (ad08caac idiom)
  gateEnter: /using\s*\(?\s*var\s+\w+\s*=\s*_gate\.Enter\s*\(\s*\)/,     // DbGate owners (bb2b0104), incl. `using (var g = _gate.Enter())`
  lockedScope: /using\s*\(?\s*var\s+\w+\s*=\s*Locked\s*\(\s*\)/,        // CodeGraphDatabase scope guard
  lockSync: /lock\s*\(\s*_syncLock\s*\)/,                                // CodeGraphDatabase lock block
  monitorSync: /Monitor\.Enter\s*\(\s*_syncLock\s*\)/,                   // CodeGraphDatabase transaction span
  lockDbLock: /lock\s*\(\s*_dbLock\s*\)/,                                // ProjectDatabase lock block
};

// Common allowlist fragments.
const initNames = name =>
  /^Migrate/.test(name) || name === 'CreateSchema' || name === 'InitializeDatabase' ||
  name === 'EnsureSchema' || name === 'Dispose';

// The connection-owning classes. Each is checked for GATE + NO-EXPOSURE + FACTORY-ONLY.
const MANIFEST = [
  {
    file: 'Services/TaskDatabase.cs',
    gates: [GATE.lockConn],
    // ad08caac allowlist: init/schema/migrate/dispose + two init-only helpers + the ctor.
    // P5 (1df2a534) adds the schema_migrations runner trio — all called only from InitializeDatabase,
    // single-threaded before the connection is shared, same class as CreateSchema.
    allow: name => initNames(name) || name === 'TaskDatabase' ||
      name === 'SeedDefaultProfiles' || name === 'UniqueNoteTabName' ||
      name === 'RunMigration' || name === 'IsMigrationApplied' || name === 'RecordMigration',
  },
  {
    file: 'Services/ProjectDatabase.cs',
    // Pre-existing G9 model: public methods lock (_dbLock); private reader helpers run inside
    // a locked public method. Both the lock block and (harmless) reentrant re-locks count.
    gates: [GATE.lockDbLock],
    allow: name => initNames(name) || name === 'ProjectDatabase',
  },
  {
    file: 'Services/KnowledgeDatabase.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'KnowledgeDatabase',
  },
  {
    file: 'Services/CodeGraphDatabase.cs',
    gates: [GATE.lockedScope, GATE.lockSync, GATE.monitorSync],
    allow: name => initNames(name) || name === 'CodeGraphDatabase',
  },
  {
    file: 'Services/SessionMemoryDatabase.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'SessionMemoryDatabase',
  },
  {
    file: 'Services/BranchMetadataService.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'BranchMetadataService',
  },
  {
    file: 'Services/OwnerProfileService.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'OwnerProfileService',
  },
  {
    file: 'Services/SourceControlAccountService.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'SourceControlAccountService',
  },
  {
    // Census straggler #1 (bb2b0104 pipeline Run 1): held a long-lived ungated connection, opened direct.
    file: 'Services/PlanDatabase.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'PlanDatabase',
  },
  {
    // Census straggler #2 (bb2b0104 pipeline Run 1): 10th owner, in MCPServer/Services; same conformance.
    file: 'MCPServer/Services/ActivityFeedService.cs',
    gates: [GATE.gateEnter],
    allow: name => initNames(name) || name === 'ActivityFeedService',
  },
];

// The ONLY sanctioned direct-open site: the factory. Every other `new SQLiteConnection(` must
// target a DIFFERENT database family (allowlisted below with the reason) — never multiterminal.db.
const FACTORY_FILE = 'Services/MultiterminalDb.cs';

// Separate DB families that legitimately open their own connection directly (NOT multiterminal.db,
// so the one-owner-per-multiterminal.db invariant does not apply). One line each so the allowlist
// is self-explaining (bb2b0104 census, condition 3).
const SEPARATE_DB = {
  'Services/MessageQueueDatabase.cs': 'messages.db — the inter-terminal message queue, a separate DB file.',
  'Services/GatewayIntegrationService.cs': 'McpGateway DB (_gatewayDbPath) — a separate process/DB owned by the gateway.',
};

// Files allowed to call MultiterminalDb.Open() without being a manifest owner (none today; the
// factory itself does not call Open). Kept as an explicit, reviewable seam.
const FACTORY_CALLER_ALLOW = new Set([]);

// Blank the CONTENTS of // and /* */ comments and of string / char literals (replace with
// spaces, preserving length and newlines) so gate/_connection/new-SQLiteConnection detection
// sees CODE only. A real gate, a real `_connection` identifier, and a real `new SQLiteConnection`
// only ever live in code — never inside a string or comment.
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
  const flat = stripAngleBrackets(sig);
  const m = flat.match(/([A-Za-z_]\w*)\s*\(/);
  return m ? m[1] : null;
}

// Parse block-methods using this codebase's consistent Allman/8-space style: a method body
// opens with a lone `        {` (8 spaces) after an 8-space access-modifier signature
// containing `(`, and closes with the next lone `        }` (8 spaces). Nested blocks live at
// >=12 spaces, so their braces never collide with the 8-space method frame, and braces inside
// string/SQL literals are never a whole-line `        {`/`        }`.
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
      if (/;\s*$/.test(lines[j]) || /=>/.test(lines[j])) { aborted = true; break; } // field / auto-prop / expression body
      j++;
    }
    if (aborted || j >= lines.length || lines[j].trim() !== '{' || !sig.includes('(')) continue;
    const name = methodName(sig);
    if (!name) continue;
    const bodyOpen = j;
    let k = j + 1;
    while (k < lines.length && lines[k] !== '        }') k++;
    const bodyClose = k;
    methods.push({ name, sigStart: i, bodyOpen, bodyClose });
    i = k;
  }
  return methods;
}

function bodyText(lines, m) {
  return lines.slice(m.bodyOpen + 1, m.bodyClose).join('\n');
}

// Earliest offset at which ANY of this class's gate idioms is acquired (or -1).
function firstGateIdx(body, gates) {
  let idx = -1;
  for (const re of gates) {
    const m = body.match(re);
    if (m && (idx === -1 || m.index < idx)) idx = m.index;
  }
  return idx;
}

// Per-class GATE analysis. `cfg` = { gates, allow }.
function analyzeGates(lines, cfg) {
  const methods = parseMethods(lines);
  const violations = [];
  const connMethods = [];

  for (const m of methods) {
    const body = maskCodeOnly(bodyText(lines, m));
    if (!/\b_connection\b/.test(body)) continue; // only methods that touch the handle
    connMethods.push(m);
    if (cfg.allow(m.name)) continue; // exempt: init / schema / migrate / dispose / ctor

    const gateIdx = firstGateIdx(body, cfg.gates);
    const connIdx = body.search(/\b_connection\b/);
    const isAsync = /\basync\b/.test(maskCodeOnly(lines[m.sigStart])) || /\basync\b/.test(body);
    const isIterator = /\byield\s+(return|break)\b/.test(body);

    if (gateIdx === -1) {
      violations.push({ m, kind: 'missing-gate', why: 'references _connection but never acquires the class gate (bare call / comment mention does NOT count)' });
    } else if (connIdx !== -1 && connIdx < gateIdx) {
      violations.push({ m, kind: 'gate-order', why: `touches _connection (offset ${connIdx}) BEFORE acquiring the gate (offset ${gateIdx})` });
    }
    if (isAsync) violations.push({ m, kind: 'async', why: 'is async — a using-scoped guard holds across suspension' });
    if (isIterator) violations.push({ m, kind: 'iterator', why: 'is an iterator (yield) — a using-scoped guard acquires lazily' });
  }

  // No unattributed _connection use (besides the field declaration) — catches expression-bodied
  // or otherwise unparsed sites that the method walker missed.
  const attributed = new Array(lines.length).fill(false);
  for (const m of connMethods) {
    for (let k = m.bodyOpen; k <= m.bodyClose; k++) attributed[k] = true;
  }
  const unattributed = [];
  for (let i = 0; i < lines.length; i++) {
    if (!/\b_connection\b/.test(lines[i])) continue;
    if (attributed[i]) continue;
    const t = maskCodeOnly(lines[i]).trim();
    if (!/\b_connection\b/.test(t)) continue;                          // comment/string-only mention
    if (/private\s+(readonly\s+)?SQLiteConnection\s+_connection\s*;/.test(t)) continue; // the field decl (home)
    unattributed.push(i + 1);
  }

  return { methods, connMethods, violations, unattributed };
}

// (2) NO EXPOSURE: a `SQLiteConnection Connection` property re-hands the raw handle to siblings.
function findConnectionProperty(lines) {
  const hits = [];
  for (let i = 0; i < lines.length; i++) {
    const t = maskCodeOnly(lines[i]);
    // property forms: `SQLiteConnection Connection => _connection;` or `SQLiteConnection Connection {`
    if (/\bSQLiteConnection\s+Connection\b\s*(=>|\{)/.test(t)) hits.push(i + 1);
  }
  return hits;
}

// Detects a DIRECT SQLite connection open in every realistic C# shape the census must not miss.
// Matched shapes (each has a --self-test fixture):
//   • plain OR fully-qualified `new [Namespace.]SQLiteConnection(` constructor;
//   • the ADO provider-factory `SQLiteFactory` (e.g. SQLiteFactory.Instance.CreateConnection());
//   • a `using`-alias TO SQLiteConnection (`using X = ...SQLiteConnection;` — then `new X(...)` opens);
//   • the generic ADO provider route `DbProviderFactories.GetFactory(...)` (matched string-independently
//     because maskCodeOnly blanks the "System.Data.SQLite" argument; there is zero DbProviderFactories
//     usage in this codebase, so flagging any occurrence for census accounting is safe and conservative).
// NOT matched (negative fixtures): `new SQLiteCommand(`, `new SQLiteConnectionStringBuilder(...)`.
// Broadened across pipeline Runs 2–3 as the adversary named each bypass.
//
// SCOPE (honest limit): this is a STATIC textual guard, not a formal proof. A connection obtained
// by reflection / dynamic assembly loading / Activator.CreateInstance is deliberately OUT OF SCOPE
// — closing those would require a Roslyn semantic pass, disproportionate for a lint script, and no
// such pattern exists in this codebase. The guard's job is to make the next STRAIGHTFORWARD
// straggler (the PlanDatabase/ActivityFeedService shape) fail loudly, which it does.
const SQLITE_OPEN_RE = /new\s+(?:[A-Za-z_][\w.]*\.)?SQLiteConnection\s*\(|\bSQLiteFactory\b|using\s+[A-Za-z_]\w*\s*=\s*(?:[A-Za-z_][\w.]*\.)?SQLiteConnection\b|DbProviderFactories\.GetFactory\s*\(/;

// (3) FACTORY-ONLY OPEN: only MultiterminalDb.Open() may open a connection.
function findDirectOpens(lines) {
  const hits = [];
  for (let i = 0; i < lines.length; i++) {
    const t = maskCodeOnly(lines[i]);
    if (SQLITE_OPEN_RE.test(t)) hits.push(i + 1);
  }
  return hits;
}

function analyzeFile(absPath, cfg) {
  const src = fs.readFileSync(absPath, 'utf8');
  const lines = src.split(/\r?\n/);
  const g = analyzeGates(lines, cfg);
  return {
    ...g,
    exposure: findConnectionProperty(lines),
    directOpens: findDirectOpens(lines),
  };
}

// ---- CENSUS (bb2b0104, Alice's named deliverable) -----------------------------------
// The manifest is only trustworthy if it can't silently omit an owner (that is exactly how
// PlanDatabase / ActivityFeedService hid — they were never TaskDatabase.Connection borrowers,
// so the original frame missed them). The census closes that: it walks the WHOLE solution for
// every SQLite open path and asserts each is accounted for. Two open paths exist:
//   • a direct `new SQLiteConnection(` — must be the factory OR an allowlisted separate-DB family;
//   • a `MultiterminalDb.Open()` call — its caller file MUST be a manifest owner.
// A new straggler on either path FAILS the check loudly instead of passing as green.

// Pure classifier so the self-test can pressure-test it with synthetic sites (no disk needed).
// sites: [{ file, directOpen: bool, factoryCall: bool }]  (file = repo-relative, forward slashes)
function censusViolations(sites, manifestFiles) {
  const owners = new Set(manifestFiles);
  const viol = [];
  for (const s of sites) {
    const f = s.file.replace(/\\/g, '/');
    if (s.directOpen && f !== FACTORY_FILE && !Object.hasOwn(SEPARATE_DB, f)) {
      viol.push({ file: f, kind: 'unsanctioned-direct-open',
        why: 'opens a SQLiteConnection directly but is neither the factory nor an allowlisted separate-DB family' });
    }
    if (s.factoryCall && f !== FACTORY_FILE && !owners.has(f) && !FACTORY_CALLER_ALLOW.has(f)) {
      viol.push({ file: f, kind: 'factory-open-by-non-owner',
        why: 'calls MultiterminalDb.Open() but is not a manifest owner (add it to MANIFEST so its gating is checked)' });
    }
  }
  return viol;
}

// Walk the solution and record, per file, whether it opens directly and/or calls the factory.
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.claude', 'staged', 'Deploy', 'packages', 'TestResults', '.vs']);
function scanOpenSites(root) {
  const sites = [];
  function walk(dir) {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      if (e.isDirectory()) {
        if (SKIP_DIRS.has(e.name)) continue;
        walk(path.join(dir, e.name));
      } else if (e.isFile() && e.name.endsWith('.cs')) {
        const abs = path.join(dir, e.name);
        let code;
        try { code = maskCodeOnly(fs.readFileSync(abs, 'utf8')); } catch { continue; }
        const directOpen = SQLITE_OPEN_RE.test(code);
        const factoryCall = /MultiterminalDb\.Open\s*\(/.test(code);
        if (directOpen || factoryCall) {
          const rel = path.relative(root, abs).replace(/\\/g, '/');
          sites.push({ file: rel, directOpen, factoryCall });
        }
      }
    }
  }
  walk(root);
  return sites;
}

// ---- self-test: PROVE every check falsifies (bb2b0104 carries ad08caac's falsifiability bar)
// Each negative fixture MUST be flagged; each positive control MUST pass. If any behaves the
// wrong way the check itself is broken → exit non-zero. Falsifiability is proven by fixtures,
// not asserted in prose.
function selfTest() {
  const gateCfg = { gates: [GATE.gateEnter], allow: name => name === 'Ctor' };
  const gateCases = [
    { name: 'positive: real `using var gate = _gate.Enter();`', expectFlagged: false,
      code: `        public void Good()\n        {\n            using var gate = _gate.Enter();\n            using var cmd = new SQLiteCommand("SELECT 1", _connection);\n        }` },
    { name: 'positive: block-form `using (var g = _gate.Enter())`', expectFlagged: false,
      code: `        public void GoodBlock()\n        {\n            using (var g = _gate.Enter())\n            {\n                using var cmd = new SQLiteCommand("SELECT 1", _connection);\n            }\n        }` },
    { name: '(a) bare _gate.Enter(); without using — permanent-lock footgun', expectFlagged: true,
      code: `        public void BareCall()\n        {\n            _gate.Enter();\n            using var cmd = new SQLiteCommand("SELECT 1", _connection);\n        }` },
    { name: '(b) comment-only mention of _gate.Enter(), no real gate', expectFlagged: true,
      code: `        public void CommentOnly()\n        {\n            // remember to call _gate.Enter() first\n            using var cmd = new SQLiteCommand("SELECT 1", _connection);\n        }` },
    { name: '(c) gate deleted entirely', expectFlagged: true,
      code: `        public void GateDeleted()\n        {\n            using var cmd = new SQLiteCommand("SELECT 1", _connection);\n        }` },
    { name: '(d) _connection touched BEFORE the gate', expectFlagged: true,
      code: `        public void OutOfOrder()\n        {\n            var x = _connection.State;\n            using var gate = _gate.Enter();\n        }` },
  ];
  const exposureCases = [
    { name: 'exposure: `SQLiteConnection Connection => _connection;` present', expectFlagged: true,
      code: `        public SQLiteConnection Connection => _connection;` },
    { name: 'no-exposure: no Connection property', expectFlagged: false,
      code: `        private readonly SQLiteConnection _connection;` },
  ];
  const factoryCases = [
    { name: 'factory: direct `new SQLiteConnection(cs)` present', expectFlagged: true,
      code: `            _connection = new SQLiteConnection(cs);` },
    { name: 'factory-ok: opens via MultiterminalDb.Open()', expectFlagged: false,
      code: `            _connection = MultiterminalDb.Open();` },
  ];

  let allOk = true;
  const report = (label, got, exp, name) => {
    const ok = got === exp;
    allOk = allOk && ok;
    console.log(`  ${ok ? '✅' : '❌'} [${label}] ${name} → ${got ? 'FLAGGED' : 'passed'} (expected ${exp ? 'FLAGGED' : 'passed'})`);
  };
  console.log('Self-test — negative fixtures must FAIL the check, positive controls must PASS:');
  for (const c of gateCases) {
    const flagged = analyzeGates(c.code.split('\n'), gateCfg).violations.length > 0;
    report('gate', flagged, c.expectFlagged, c.name);
  }
  for (const c of exposureCases) {
    const flagged = findConnectionProperty(c.code.split('\n')).length > 0;
    report('exposure', flagged, c.expectFlagged, c.name);
  }
  for (const c of factoryCases) {
    const flagged = findDirectOpens(c.code.split('\n')).length > 0;
    report('factory', flagged, c.expectFlagged, c.name);
  }
  // Census fixtures: a synthetic 11th open-site must FAIL (Alice's negative-fixture requirement).
  const manifestFiles = MANIFEST.map(c => c.file);
  const censusCases = [
    { name: 'census: synthetic 11th class opens SQLiteConnection directly', expectFlagged: true,
      sites: [{ file: 'Services/RogueDatabase.cs', directOpen: true, factoryCall: false }] },
    { name: 'census: synthetic class calls MultiterminalDb.Open() but is not a manifest owner', expectFlagged: true,
      sites: [{ file: 'Services/SneakyOwner.cs', directOpen: false, factoryCall: true }] },
    { name: 'census-ok: the factory itself opens directly', expectFlagged: false,
      sites: [{ file: FACTORY_FILE, directOpen: true, factoryCall: false }] },
    { name: 'census-ok: an allowlisted separate-DB family opens directly', expectFlagged: false,
      sites: [{ file: 'Services/MessageQueueDatabase.cs', directOpen: true, factoryCall: false }] },
    { name: 'census-ok: a manifest owner calls the factory', expectFlagged: false,
      sites: [{ file: manifestFiles[0], directOpen: false, factoryCall: true }] },
  ];
  for (const c of censusCases) {
    const flagged = censusViolations(c.sites, manifestFiles).length > 0;
    report('census', flagged, c.expectFlagged, c.name);
  }

  // Detection fixtures: the open-site regex must catch every realistic open shape (Run 2 adversary
  // HIGH — a fully-qualified ctor or SQLiteFactory route must not slip past) and must NOT count a
  // SQLiteCommand or a connection-string builder as an open.
  const detectCases = [
    { name: 'detect: plain new SQLiteConnection(', code: '_connection = new SQLiteConnection(cs);', expectFlagged: true },
    { name: 'detect: fully-qualified new System.Data.SQLite.SQLiteConnection(', code: '_connection = new System.Data.SQLite.SQLiteConnection(cs);', expectFlagged: true },
    { name: 'detect: SQLiteFactory-created connection', code: 'var c = SQLiteFactory.Instance.CreateConnection();', expectFlagged: true },
    { name: 'detect: using-alias to SQLiteConnection', code: 'using SqlConn = System.Data.SQLite.SQLiteConnection;', expectFlagged: true },
    { name: 'detect: DbProviderFactories SQLite provider route', code: 'var f = DbProviderFactories.GetFactory("System.Data.SQLite");', expectFlagged: true },
    { name: 'detect-negative: new SQLiteCommand( is not an open', code: 'using var cmd = new SQLiteCommand(sql, _connection);', expectFlagged: false },
    { name: 'detect-negative: new SQLiteConnectionStringBuilder is not an open', code: 'var b = new SQLiteConnectionStringBuilder { DataSource = p };', expectFlagged: false },
    { name: 'detect-negative: plain namespace using is not an alias-open', code: 'using System.Data.SQLite;', expectFlagged: false },
  ];
  for (const c of detectCases) {
    const flagged = SQLITE_OPEN_RE.test(maskCodeOnly(c.code));
    report('detect', flagged, c.expectFlagged, c.name);
  }

  console.log(allOk
    ? '\n✅ Self-test OK — gate / exposure / factory / census checks provably reject the bad shapes and accept the good ones.'
    : '\n❌ Self-test FAILED — a check does not falsify correctly.');
  process.exit(allOk ? 0 : 1);
}

if (doSelfTest) selfTest();

// -------------------------------------------------------------------------------------
let failed = false;
let totalConn = 0;
for (const cfg of MANIFEST) {
  const abs = path.join(REPO_ROOT, cfg.file);
  if (!fs.existsSync(abs)) { console.log(`❌ ${cfg.file}: file not found`); failed = true; continue; }
  const r = analyzeFile(abs, cfg);
  totalConn += r.connMethods.length;
  const problems = [];
  for (const v of r.violations) problems.push(`GATE ${v.m.name} (line ${v.m.sigStart + 1}): ${v.why}`);
  for (const ln of r.unattributed) problems.push(`GATE unattributed _connection use at line ${ln}`);
  for (const ln of r.exposure) problems.push(`EXPOSURE Connection property at line ${ln} — delete it; owners never hand out their handle`);
  for (const ln of r.directOpens) problems.push(`FACTORY direct \`new SQLiteConnection(\` at line ${ln} — open via MultiterminalDb.Open()`);
  if (problems.length) {
    failed = true;
    console.log(`\n❌ ${cfg.file} — ${problems.length} violation(s):`);
    for (const p of problems) console.log(`   - ${p}`);
  } else {
    console.log(`✅ ${cfg.file} — ${r.connMethods.length} _connection method(s) all gated; no exposure; factory-only open.`);
  }
}

// Solution-wide census: every SQLite open path must be accounted for (Alice's named deliverable).
const manifestFiles = MANIFEST.map(c => c.file);
const sites = scanOpenSites(REPO_ROOT);
const censusViols = censusViolations(sites, manifestFiles);
if (censusViols.length) {
  failed = true;
  console.log(`\n❌ CENSUS — ${censusViols.length} unaccounted SQLite open-site(s):`);
  for (const v of censusViols) console.log(`   - ${v.file} [${v.kind}]: ${v.why}`);
} else {
  const directCount = sites.filter(s => s.directOpen).length;
  const factoryCount = sites.filter(s => s.factoryCall).length;
  console.log(`✅ CENSUS — ${sites.length} open-site file(s) all accounted for (${directCount} direct: factory + ${Object.keys(SEPARATE_DB).length} separate-DB families; ${factoryCount} factory-callers, all manifest owners).`);
}

console.log(failed
  ? '\n❌ Connection-ownership invariant VIOLATED (see above).'
  : `\n✅ Invariant holds across ${MANIFEST.length} owners (${totalConn} _connection methods): one owner per connection, each gated, none exposes or hand-opens a handle, and the census finds no unaccounted open-site.`);
process.exit(failed ? 1 : 0);
