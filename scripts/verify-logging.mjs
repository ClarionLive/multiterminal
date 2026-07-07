#!/usr/bin/env node
// verify-logging.mjs — falsifiable close for the c425e3a2 logging-unification sweep (item 4).
//
// The sweep routed the HOT CENTRAL paths (the god-files + the services that already hold a logging
// sink) off Debug/Trace.WriteLine and onto the buffered, Release-surviving DebugLogService. This gate
// asserts that surface stays converted, and that EVERY remaining Debug/Trace.WriteLine site in the
// codebase is explicitly accounted for — either converted-and-must-stay-zero, or on a NAMED allowlist
// (deferred to follow-up 4c86f18d, owned by cd8ca48c this cycle, or a documented can't-convert site).
//
// enumerate-don't-prose: a NEW Debug.WriteLine in a converted god-file FAILS. A new site in an
// unlisted file FAILS (forcing an explicit convert-or-defer decision). The deferred leaves are named,
// not hand-waved. Run: node scripts/verify-logging.mjs   (exit 0 = pass, 1 = fail)

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, sep } from 'node:path';

const ROOT = process.cwd();
const CALL = /(?:System\.Diagnostics\.)?(?:Debug|Trace)\.WriteLine\s*\(/;

// ── CONVERTED: the sweep's scope. MUST hold ZERO code-level Debug/Trace.WriteLine calls. ────────────
const CONVERTED = new Set([
  'MCPServer/Services/MessageBroker.cs',
  'MCPServer/Services/TaskService.cs',
  'MainForm.cs',
  'Services/CodeReviewService.cs',
  'MCPServer/Services/HttpWebhookService.cs',
  'Services/CodeGraphWatcher.cs',
  'Services/TeamWatcherService.cs',
  'Services/OracleService.cs',
]);

// ── ALLOWLISTED SITES: convert-set files that legitimately KEEP a Debug.WriteLine, with the reason. ──
const ALLOWLIST_SITES = new Map([
  ['Services/DebugLogService.cs',
    'self-bootstrap fallback (ctor): the sink cannot log to itself when its own log file failed to open'],
  ['Services/Presence/PresenceAdapter.cs',
    'no-logger fallback default in a delegate-injected leaf; the REST host wires DebugLogService via the _log delegate'],
  ['Services/TaskDatabase.cs',
    'persistence leaf (bb2b0104) — no diagnostic dependency plumbed into the DB layer; 9 cold migration/FTS5 sites swept in 4c86f18d'],
]);

// ── OWNED BY cd8ca48c (Bob's GitExec refactor) this cycle — swept in 4c86f18d, not this ticket. ─────
const CD8CA48C = new Set([
  'Services/WorktreeManager.cs',
  'Services/WorktreeJanitorService.cs',
  'Services/WorktreeMergeService.cs',
  'Services/WorktreeListService.cs',
  'Services/WorktreeAutoCommitService.cs',
  'MCPServer/Services/SessionDiscovery.cs',
  'Services/SessionSyncService.cs',
  'Services/GitExec.cs',
]);

// ── DEFERRED dependency-free leaf services — no sink, no broker; plumbing a diagnostic ref into each
//    would regress leaf purity (the TaskDatabase ruling, applied uniformly). Swept in 4c86f18d. ──────
const DEFERRED_LEAVES = new Set([
  'MCPServer/Services/ActivityFeedService.cs',
  'MCPServer/Services/ActivityService.cs',
  'MCPServer/Services/PoolCoordinator.cs',
  'Services/ChangelogAttributionService.cs',
  'Services/ChangelogService.cs',
  'Services/CodexBrokerHealthService.cs',
  'Services/CodexConfigService.cs',
  'Services/CodexPromptService.cs',
  'Services/CompanionProcessManager.cs',
  'Services/GatewayIntegrationService.cs',
  'Services/GitAttributionService.cs',
  'Services/GitRepoManager.cs',
  'Services/InboxFileWriter.cs',
  'Services/KnowledgeDatabase.cs',
  'Services/LaunchCommandBuilder.cs',
  'Services/McpConfigService.cs',
  'Services/PlanSeeder.cs',
  'Services/ProjectAnalyticsService.cs',
  'Services/ProjectDatabase.cs',
  'Services/ProjectJsonChangelogParser.cs',
  'Services/ProjectJsonMigrationService.cs',
  'Services/ProjectService.cs',
  'Services/PromptService.cs',
  'Services/RipgrepService.cs',
  'Services/SessionContextWriter.cs',
  'Services/SessionIndexingService.cs',
  'Services/SessionLineageService.cs',
  'Services/SessionMemoryDatabase.cs',
  'Services/SettingsService.cs',
  'Services/TerminalSpawner.cs',
  'Services/TerminalStreamService.cs',
  'Services/TranscriptTailer.cs',
  'Services/WorktreeLayoutMigrationService.cs',
]);

// ── DEFERRED UI + API layer (whole directories) — deferred to 4c86f18d: depends on this buffering +
//    level-floor infra; Terminal/WebView2 threading needs its own audit; API/ waits behind Grace's P3b.
const DEFERRED_DIRS = [
  'API/', 'ActivityPanel/', 'AgentPanel/', 'ChatPanel/', 'Controls/', 'DashboardHeader/',
  'Dialogs/', 'Docking/', 'FilePreviewPanel/', 'InboxPanel/', 'OfficePanel/', 'ProfilePanel/',
  'ProjectPanel/', 'StartScreen/', 'TaskLifecycleBoard/', 'TasksPanel/', 'Terminal/',
];
const DEFERRED_FILES = new Set(['Program.cs', 'TestSpawnForm.cs']); // root-level UI/host shims

// ── walk production .cs, counting code-level calls (skip comment lines) ──────────────────────────────
function* walk(dir) {
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (e === 'obj' || e === 'bin' || e === 'node_modules' || e === '.git' || e === '.claude') continue;
    const s = statSync(p);
    if (s.isDirectory()) yield* walk(p);
    else if (e.endsWith('.cs') && !p.includes(`${sep}MultiTerminal.Tests${sep}`)) yield p;
  }
}
// Count code-level calls in a source text. Strips block comments FIRST (so a same-line
// `/* note */ Debug.WriteLine(...)` in a converted file can't evade the census by starting with `/*`),
// then strips line comments per line (so a trailing `// mentions Debug.WriteLine(x)` isn't miscounted).
// Pure + exported-by-reference so the self-test can prove these evasions are closed.
function countCallsInText(text) {
  const noBlock = text.replace(/\/\*[\s\S]*?\*\//g, ' '); // remove /* ... */ (multi-line)
  let count = 0;
  for (const raw of noBlock.split('\n')) {
    const noLine = raw.replace(/\/\/.*$/, ''); // remove // line comment
    if (CALL.test(noLine)) count++;
  }
  return count;
}
function codeLevelCount(file) {
  return countCallsInText(readFileSync(file, 'utf8'));
}
const rel = (p) => p.slice(ROOT.length + 1).split(sep).join('/');
const inDeferredDir = (r) => DEFERRED_DIRS.some((d) => r.startsWith(d));

// Pure classifier for one (relPath, code-level count) — returns a bucket or a failure string.
// Exposed so the self-test can prove the FAIL paths still fire (a census that can't falsify is theatre).
function classifyOne(r, n) {
  if (CONVERTED.has(r)) {
    return n > 0
      ? { fail: `REGRESSION: converted file ${r} has ${n} Debug/Trace.WriteLine call(s) — must be 0` }
      : { bucket: 'converted' };
  }
  if (n === 0) return { bucket: 'none' };
  if (ALLOWLIST_SITES.has(r)) return { bucket: 'allowlisted' };
  if (CD8CA48C.has(r) || DEFERRED_LEAVES.has(r) || DEFERRED_FILES.has(r) || inDeferredDir(r)) return { bucket: 'deferred' };
  return { fail: `UNACCOUNTED: ${r} has ${n} Debug/Trace.WriteLine call(s) — convert it, or add it to the allowlist/deferred set` };
}

// ── self-test: negative fixtures prove the gate falsifies (run with --self-test) ────────────────────
if (process.argv.includes('--self-test')) {
  const cases = [
    { r: 'MCPServer/Services/MessageBroker.cs', n: 1, wantFail: true, why: 'a new Debug.WriteLine in a converted god-file must FAIL' },
    { r: 'MCPServer/Services/MessageBroker.cs', n: 0, wantFail: false, why: 'a clean converted god-file passes' },
    { r: 'Services/SomeBrandNewLeaf.cs', n: 2, wantFail: true, why: 'an unlisted file with sites must FAIL (forces explicit accounting)' },
    { r: 'Services/TaskDatabase.cs', n: 9, wantFail: false, why: 'an allowlisted can\'t-convert file passes' },
    { r: 'Services/TranscriptTailer.cs', n: 18, wantFail: false, why: 'a named deferred leaf passes' },
    { r: 'Terminal/ConPtyTerminal.cs', n: 35, wantFail: false, why: 'a deferred UI/API-dir file passes' },
  ];
  let bad = 0;
  for (const c of cases) {
    const got = !!classifyOne(c.r, c.n).fail;
    const ok = got === c.wantFail;
    if (!ok) bad++;
    console.log(`${ok ? 'ok  ' : 'FAIL'} — ${c.why} (wantFail=${c.wantFail}, got=${got})`);
  }

  // countCallsInText: prove the comment-evasion holes are closed (a census that a hidden call can
  // slip past is a census that lies — the same bar we hold external scanners to).
  const countCases = [
    { t: 'Debug.WriteLine("ok");', want: 1, why: 'a plain call counts' },
    { t: '/* note */ Debug.WriteLine("x");', want: 1, why: 'a same-line leading block comment cannot hide a real call' },
    { t: 'foo(); // restored Debug.WriteLine(x)', want: 0, why: 'a trailing line-comment mention is not miscounted' },
    { t: '/* Debug.WriteLine(inside) */', want: 0, why: 'a call token inside a block comment is not counted' },
    { t: 'a();\n/* multi\nTrace.WriteLine(x)\nline */\nb();', want: 0, why: 'a call inside a multi-line block comment is not counted' },
  ];
  for (const c of countCases) {
    const got = countCallsInText(c.t);
    const ok = got === c.want;
    if (!ok) bad++;
    console.log(`${ok ? 'ok  ' : 'FAIL'} — ${c.why} (want=${c.want}, got=${got})`);
  }

  const total = cases.length + countCases.length;
  console.log(bad ? `\nSELF-TEST FAILED (${bad})` : `\nSELF-TEST PASSED (${total}/${total})`);
  process.exit(bad ? 1 : 0);
}

// ── classify the real tree ──────────────────────────────────────────────────────────────────────
const failures = [];
let convertedOk = 0, allowlisted = 0, deferred = 0;
for (const abs of walk(ROOT)) {
  const r = rel(abs);
  const res = classifyOne(r, codeLevelCount(abs));
  if (res.fail) failures.push(res.fail);
  else if (res.bucket === 'converted') convertedOk++;
  else if (res.bucket === 'allowlisted') allowlisted++;
  else if (res.bucket === 'deferred') deferred++;
}

// converted files must all exist
for (const r of CONVERTED) {
  try { statSync(join(ROOT, r)); } catch { failures.push(`MISSING: converted file ${r} not found`); }
}

console.log(`Logging sweep census (c425e3a2):`);
console.log(`  converted (zero-verified): ${convertedOk}/${CONVERTED.size}`);
console.log(`  allowlisted can't-convert sites: ${allowlisted}`);
console.log(`  deferred (4c86f18d + cd8ca48c): ${deferred} files`);
if (failures.length) {
  console.error(`\nFAIL (${failures.length}):`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`\nPASS — every Debug/Trace.WriteLine site is accounted for; the converted god-file surface is zero.`);
