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
  // ── c425e3a2: hot central paths + sink-holding services ──
  'MCPServer/Services/MessageBroker.cs',
  'MCPServer/Services/TaskService.cs',
  'MainForm.cs',
  'Services/CodeReviewService.cs',
  'MCPServer/Services/HttpWebhookService.cs',
  'Services/CodeGraphWatcher.cs',
  'Services/TeamWatcherService.cs',
  'Services/OracleService.cs',

  // ── 4c86f18d: UI + API layer (converted to <sink>?.Level("Tag", msg); sink = broker/field/property) ──
  // Panels
  'ActivityPanel/ActivityPanelDocument.cs',
  'ActivityPanel/ActivityPanelRenderer.cs',
  'OfficePanel/OfficePanelDocument.cs',
  'OfficePanel/OfficePanelRenderer.cs',
  'ChatPanel/ChatPanelControl.cs',
  'DashboardHeader/DashboardHeaderControl.cs',
  'InboxPanel/InboxPanelControl.cs',
  'TaskLifecycleBoard/TaskLifecycleBoardForm.cs',
  'TasksPanel/TasksPanelControl.cs',
  'FilePreviewPanel/FilePreviewPanelDocument.cs',
  'AgentPanel/AgentPanelControl.cs',
  'Docking/ProjectPanelDocument.cs',
  'ProjectPanel/ProjectPanelRenderer.cs',
  // Dialogs
  'Dialogs/CodeReviewPopupForm.cs',
  'Dialogs/CodeReviewPopupManager.cs',
  // Hot-path renderers (Trace-downgraded per the hot-path audit)
  'Controls/HudGitPanel/HudGitRenderer.cs',
  'Controls/TaskHudPanel/TaskHudRenderer.cs',
  'ProfilePanel/ProfilePanelDocument.cs',
  'ProfilePanel/ProfilePanelRenderer.cs',
  'StartScreen/StartScreenControl.cs',
  // Terminal hot-path layer
  'Docking/TerminalDocument.cs',
  'Controls/TerminalControl.cs',
  'Terminal/ConPtyTerminal.cs',
  'Terminal/WebViewTerminalRenderer.cs',
  // API layer
  'API/Gateway/MultiRemoteGatewayHost.cs',
  'API/Controllers/KnowledgeController.cs',
  'API/Controllers/SessionMemoryController.cs',
  'API/Controllers/TasksController.cs',
  'API/Controllers/TerminalsController.cs',
  'API/Controllers/WorktreesController.cs',
]);

// ── ALLOWLISTED SITES: files that legitimately KEEP a Debug/Trace.WriteLine, with the reason. ────────
// c425e3a2 seeded the first three (DebugLogService self-bootstrap, PresenceAdapter delegate leaf,
// TaskDatabase persistence leaf). 4c86f18d added the 33 dependency-free leaf services below: ruling #1
// (Alice's dispatch) — "any dependency-free leaf without a sink gets ALLOWLISTED, not plumbed (same call
// as TaskDatabase)". Applied uniformly so leaf purity is preserved; a diagnostic sink is not plumbed into
// a sink-less/broker-less leaf. Debug.WriteLine is [Conditional("DEBUG")] (Release no-op); the few Trace
// sites are low-volume startup/diagnostic paths the purity ruling deliberately keeps un-plumbed.
const ALLOWLIST_SITES = new Map([
  ['Services/DebugLogService.cs',
    'self-bootstrap fallback (ctor): the sink cannot log to itself when its own log file failed to open'],
  ['Services/Presence/PresenceAdapter.cs',
    'no-logger fallback default in a delegate-injected leaf; the REST host wires DebugLogService via the _log delegate'],
  ['Services/TaskDatabase.cs',
    'persistence leaf (bb2b0104) — no diagnostic dependency plumbed into the DB layer; 9 cold migration/FTS5 sites'],

  // ── 4c86f18d leaf sweep — DB / persistence leaves (kept dependency-free like TaskDatabase) ──────────
  ['Services/ProjectDatabase.cs',      'persistence leaf — dependency-free like TaskDatabase (ruling #1); Debug diagnostics compile out of Release'],
  ['Services/SessionMemoryDatabase.cs', 'persistence leaf — dependency-free like TaskDatabase (ruling #1); Debug diagnostics compile out of Release'],
  ['Services/KnowledgeDatabase.cs',    'persistence leaf — dependency-free like TaskDatabase (ruling #1); Debug diagnostics compile out of Release'],

  // ── 4c86f18d leaf sweep — static utility leaves (no instance seam for a sink; not plumbed) ──────────
  ['Services/CodexBrokerHealthService.cs',       'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/CodexConfigService.cs',             'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/CodexPromptService.cs',             'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/InboxFileWriter.cs',                'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/LaunchCommandBuilder.cs',           'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/PlanSeeder.cs',                     'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/SessionContextWriter.cs',           'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],
  ['Services/WorktreeLayoutMigrationService.cs', 'static utility leaf — no instance seam for a sink; dependency-free, not plumbed (ruling #1)'],

  // ── 4c86f18d leaf sweep — dependency-free instance leaves (no sink, no broker; not plumbed) ─────────
  ['MCPServer/Services/ActivityFeedService.cs', 'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['MCPServer/Services/ActivityService.cs',     'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['MCPServer/Services/PoolCoordinator.cs',     'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ChangelogAttributionService.cs',   'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ChangelogService.cs',              'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/CompanionProcessManager.cs',       'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/GatewayIntegrationService.cs',     'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/GitAttributionService.cs',         'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/GitRepoManager.cs',                'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/McpConfigService.cs',              'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ProjectAnalyticsService.cs',       'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ProjectJsonChangelogParser.cs',    'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ProjectJsonMigrationService.cs',   'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/ProjectService.cs',                'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/PromptService.cs',                 'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/RipgrepService.cs',                'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/SessionIndexingService.cs',        'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/SessionLineageService.cs',         'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/SettingsService.cs',               'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/TerminalSpawner.cs',               'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/TerminalStreamService.cs',         'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],
  ['Services/TranscriptTailer.cs',              'dependency-free leaf — no sink/broker; not plumbed (ruling #1)'],

  // ── 4c86f18d UI+API sweep — sites that legitimately KEEP a Debug/Trace.WriteLine (no clean sink seam) ─
  ['API/MultiTerminalRestServer.cs',
    'converted except 1 self-fallback: the PresenceAdapter log callback writes Debug.WriteLine ONLY when _broker.DebugLogService is null — cannot route a "sink is null" fallback through the null sink (same pattern as PresenceAdapter)'],
  ['Dialogs/EditProjectDialog.cs',
    'single benign catch (source-control combo-box population); dialog has no broker/sink field and its caller chain (ProjectManagerDialog→MainForm) has none within 1 hop — no clean seam without cross-file plumbing'],
  ['Docking/PromptTreeDocument.cs',
    'dead/orphaned class — never constructed in the live app (MainForm GetContentFromPersistString maps its old persist-string to ProjectPanelDocument); no reachable seam to wire a sink into'],
  ['Program.cs',
    'app entry/composition shim — the 4 Trace lines are splash/loading lifecycle events that fire before/around DebugLogService construction (created inside MainForm); no sink in scope'],
  ['Terminal/WebView2EnvironmentCache.cs',
    'static utility leaf — no instance seam for a sink; single cold Debug diagnostic (data-folder create failure)'],
  ['TestSpawnForm.cs',
    'dead/dev-only spawn harness Form — never instantiated in production (no `new TestSpawnForm`); no broker seam; Trace user-action diagnostics'],
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

// ── DEFERRED dependency-free leaf services — RESOLVED in 4c86f18d. Per ruling #1 (dependency-free leaf
//    without a sink is ALLOWLISTED, not plumbed — the TaskDatabase ruling applied uniformly), all 33
//    leaves moved to ALLOWLIST_SITES above. This set is intentionally empty; the leaf layer is no longer
//    in "deferred" limbo. (Kept as a named const so classifyOne's shape is unchanged.)
const DEFERRED_LEAVES = new Set([]);

// ── DEFERRED UI + API layer — RESOLVED in 4c86f18d. The whole-directory deferral is gone: every file in
//    these dirs is now EXPLICITLY CONVERTED (strict-zero) or ALLOWLISTED above, so a new/residual site in
//    any of them FAILS as UNACCOUNTED (the tightened falsifiable close). Both sets intentionally empty.
const DEFERRED_DIRS = [];
const DEFERRED_FILES = new Set([]); // Program.cs + TestSpawnForm.cs are now ALLOWLISTED, not dir-deferred

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
// Mask string/char literals AND comments to spaces (newlines preserved), via a real char-scanner.
// This REPLACES the earlier greedy `text.replace(/\/\*[\s\S]*?\*\//g,' ')` approach, which UNDERCOUNTED:
// a stray `/*` inside a string/message (e.g. `"a /* b"` ... later `*/` in another string) made the
// non-greedy block-comment regex eat everything BETWEEN — swallowing real Debug/Trace.WriteLine calls in
// that span and silently dropping them from the census (a census a hidden call can slip past is a census
// that lies — 4c86f18d found this eating 12 real calls in HudGitRenderer.cs). The scanner is C#-literal
// aware ("…", $"…", @"…", $@"…"/@$"…", '…') so `/*`/`*/`/`//` inside a literal are NOT treated as comments,
// and comment bodies are blanked so a commented-out call still can't inflate the count.
function maskCommentsAndStrings(text) {
  let out = '';
  const n = text.length;
  let state = 'code'; // code | line | block | str | verbatim | char
  for (let i = 0; i < n;) {
    const c = text[i], d = text[i + 1], e = text[i + 2];
    if (state === 'code') {
      if (c === '/' && d === '/') { out += '  '; i += 2; state = 'line'; continue; }
      if (c === '/' && d === '*') { out += '  '; i += 2; state = 'block'; continue; }
      if ((c === '$' && d === '@' && e === '"') || (c === '@' && d === '$' && e === '"')) { out += '   '; i += 3; state = 'verbatim'; continue; }
      if (c === '@' && d === '"') { out += '  '; i += 2; state = 'verbatim'; continue; }
      if (c === '$' && d === '"') { out += '  '; i += 2; state = 'str'; continue; }
      if (c === '"') { out += ' '; i += 1; state = 'str'; continue; }
      if (c === "'") { out += ' '; i += 1; state = 'char'; continue; }
      out += c; i += 1; continue;
    }
    if (state === 'line') {
      if (c === '\n') { out += '\n'; i += 1; state = 'code'; continue; }
      out += (c === '\t' ? '\t' : ' '); i += 1; continue;
    }
    if (state === 'block') {
      if (c === '*' && d === '/') { out += '  '; i += 2; state = 'code'; continue; }
      out += (c === '\n' ? '\n' : ' '); i += 1; continue;
    }
    if (state === 'str') { // regular OR interpolated (non-verbatim) — \ escapes, ends at unescaped "
      if (c === '\\') { out += (d === '\n' ? ' \n' : '  '); i += 2; continue; }
      if (c === '"') { out += ' '; i += 1; state = 'code'; continue; }
      out += (c === '\n' ? '\n' : ' '); i += 1; continue;
    }
    if (state === 'verbatim') { // @"…" / $@"…" — no \ escapes; "" is an escaped quote
      if (c === '"' && d === '"') { out += '  '; i += 2; continue; }
      if (c === '"') { out += ' '; i += 1; state = 'code'; continue; }
      out += (c === '\n' ? '\n' : ' '); i += 1; continue;
    }
    // state === 'char'
    if (c === '\\') { out += '  '; i += 2; continue; }
    if (c === "'") { out += ' '; i += 1; state = 'code'; continue; }
    out += (c === '\n' ? '\n' : ' '); i += 1; continue;
  }
  return out;
}
// Count code-level calls: mask literals+comments, then match per line. Pure + exported-by-reference so
// the self-test can prove both the comment-evasion AND the stray-`/*`-in-string over-eat holes are closed.
function countCallsInText(text) {
  let count = 0;
  for (const line of maskCommentsAndStrings(text).split('\n')) {
    if (CALL.test(line)) count++;
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
    { r: 'Services/TranscriptTailer.cs', n: 18, wantFail: false, why: 'a leaf now ALLOWLISTED (ruling #1) passes with sites' },
    { r: 'Terminal/ConPtyTerminal.cs', n: 1, wantFail: true, why: '4c86f18d: a now-CONVERTED terminal file must FAIL if a call reappears' },
    { r: 'Terminal/ConPtyTerminal.cs', n: 0, wantFail: false, why: 'the converted terminal file passes at zero' },
    { r: 'ChatPanel/ChatPanelDocument.cs', n: 2, wantFail: true, why: '4c86f18d: dir-deferral removed — a UI-dir file not explicitly converted/allowlisted now FAILS as UNACCOUNTED' },
    { r: 'API/MultiTerminalRestServer.cs', n: 1, wantFail: false, why: 'the RestServer self-fallback site is allowlisted (cannot log a null-sink fallback through the null sink)' },
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
    // ── 4c86f18d regression fixtures: a stray /* */ INSIDE a string must NOT eat a real call (the bug) ──
    { t: 'var s = "/* not a comment */"; Debug.WriteLine("real");', want: 1, why: 'a /* */ inside a string literal must not be treated as a comment that eats the following real call' },
    { t: 'Debug.WriteLine($"path /* x */ {y}");', want: 1, why: 'a call whose interpolated message contains /* */ counts once, not eaten' },
    { t: 'var a = @"line1 /* x\nline2 */"; \nTrace.WriteLine("after");', want: 1, why: 'a verbatim multi-line string containing /* */ does not swallow a following call' },
    { t: 'var u = "http://x"; Debug.WriteLine("y");', want: 1, why: 'a // inside a string is not a line comment' },
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
