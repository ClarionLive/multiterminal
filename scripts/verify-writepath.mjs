#!/usr/bin/env node
// verify-writepath.mjs — falsifiable census for the "single write path" invariant (P5 / ticket 1df2a534).
//
// The write path (MessageBroker) is: clone the cached entity -> mutate the clone -> persist -> swap into
// the cache ONLY on success -> raise the change event. Its whole point is that cache and DB can't diverge.
// That guarantee holds only if EVERY core persist and EVERY raw cache write goes through the write-path
// helpers — a "bypass" that writes the DB and/or the cache directly reintroduces the divergence bug.
//
// This census asserts: across the write-path source files, every method that performs a CORE PERSIST or a
// RAW CACHE WRITE is either (a) one of the write-path helper methods, or (b) a NAMED, ratified bypass. Any
// OTHER method that writes a task/project/profile row or the _tasks/_projects/_profiles cache directly FAILS
// the check — so a new bypass added anywhere else trips the build loudly (the same census-assertion move as
// the DB gate in verify-taskdb-gate.mjs). Ratified bypasses are ENUMERATED here, never inferred; the
// follow-up ticket e1643ccc converts RegisterTerminal/UnregisterTerminal off the list, shrinking it over time.
//
// THREE FILES since the broker decomposition: the Kanban-task cache + CRUD + write-path helpers
// (MutateTaskInternal/TryMutateTask/Insert/Delete + the LoadPersistedTasks/ReorderTask/SetTaskActive task
// bypasses) were RELOCATED to TaskService.cs (e7e89f4b); the team-member-profile cache + CRUD + write-path
// helpers (MutateProfileInternal/Insert/Delete + LoadPersistedProfiles) were RELOCATED to ProfileService.cs
// (86f3fd21, the second region). The project helpers still live in MessageBroker.cs. The census scans ALL
// THREE files with the union allowlist — the load-bearing point Alice flagged: if it kept scanning only the
// files a region hasn't left yet it would go SILENTLY GREEN on an emptied broker while a raw _tasks/_profiles
// write in the relocated service sailed through. Concrete negative fixtures (a TaskService-shaped raw _tasks
// write AND a ProfileService-shaped raw _profiles write, each in a non-allowlisted method) prove the check
// still falsifies post-extraction.
//
// Detection runs on CODE-ONLY text (comments and string literals are masked), so a doc comment that names
// _taskDb.SaveTask never counts.
//
// Note: targeted COLUMN / side-table writers (_taskDb.AddTaskHelper, UpdateTaskAssignee, SetProfileOnline/
// Offline, _taskDb.UpdateSortOrder, ...) are NOT core persists — they are passed as the persist action to
// a write-path helper (or paired with a cache refresh FROM the DB, as in ReorderTask). They don't create
// the clone/swap divergence this census guards, so they are intentionally out of scope.
//
// Usage:
//   node scripts/verify-writepath.mjs             # --check (default): exit 1 on any un-allowlisted bypass
//   node scripts/verify-writepath.mjs --self-test # prove the check falsifies (negative fixtures)

import fs from 'fs';
import path from 'path';

const args = process.argv.slice(2);
const doSelfTest = args.includes('--self-test');

const REPO_ROOT = path.join(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
// All write-path source files (e7e89f4b split the task region into TaskService.cs; 86f3fd21 split the profile
// region into ProfileService.cs). The census scans the union so a relocated write can't escape the check by
// moving files.
const TARGETS = ['MCPServer/Services/MessageBroker.cs', 'MCPServer/Services/TaskService.cs', 'MCPServer/Services/ProfileService.cs'];

// Methods permitted to contain a core persist / raw cache write.
const WRITE_PATH_HELPERS = new Set([
  'MutateTaskInternal', 'TryMutateTask', 'InsertTaskInternal', 'DeleteTaskInternal', 'SaveTask',
  'MutateProjectInternal', 'InsertProjectInternal',
  'MutateProfile', 'InsertProfile', 'DeleteProfileInternal',   // public since e1643ccc (registration write path)
]);

// Ratified, enumerated bypasses (NOT inferred). e1643ccc CONVERTED RegisterTerminal/UnregisterTerminal onto
// the profile write path and removed them + the TryAddProfile cache-only primitive from this set (its close
// condition) — the set shrinks as debt is paid, it does not accrete. A raw profile write reappearing in
// RegisterTerminal/UnregisterTerminal now FAILS the census (proven by the self-test regression fixture).
const NAMED_BYPASSES = new Set([
  'LoadPersistedTasks',      // startup bootstrap seed FROM the DB (not a mutation)
  'LoadPersistedProjects',   // startup bootstrap seed
  'LoadPersistedProfiles',   // startup bootstrap seed
  'ReorderTask',             // sort_order via dedicated column writers + bulk rebalance + cache refresh FROM db
  'DeleteProject',           // canonical unregister via ProjectService + coherent cache-restore-on-failure
  'SetTaskActive',           // project-id self-heal SaveTask (its own revert-on-failure), rest via the write path
  'ActivateExclusively',     // 7c59c004: the shared make-active primitive — relocated SetTaskActive swap. Multi-row
                             // clone→swap (target + each paused sibling) AFTER the atomic pause-by-id txn
                             // (SetTaskActiveTransactional) succeeds, under the assignee lock + per-task locks.
]);

const ALLOWED = new Set([...WRITE_PATH_HELPERS, ...NAMED_BYPASSES]);

// ── KNOWN UNLOCKED-WRITE EXPOSURES (enumerated, deferred — 7c59c004) ─────────────────────────────────
// The single-write-path census guards the DB/cache DIVERGENCE invariant (clone→persist→swap). 7c59c004 added
// per-task write LOCKS so concurrent same-task writers can't lose each other's field changes. That lock is
// serialized at the TaskService write-path helpers (above). This census cannot mechanically detect a
// read-modify-write that spans TWO calls in a DIFFERENT file (GetTask → mutate the returned CACHED reference
// in place → SaveTask), so the ONE such exposure is NAMED here instead of prose-buried, tagged with its
// hardening ticket. Close condition for 1f327236: remove this entry when CodeReviewService no longer mutates
// a GetTask-returned reference in place (dedicated review-notes write method, or GetTask returns a snapshot).
const KNOWN_UNLOCKED_EXPOSURES = [
  {
    site: 'Services/CodeReviewService.cs (GetTask@~499 → mutate cached ref → SaveTask@~524,569)',
    exposure: 'External caller mutates the GetTask-returned cached KanbanTask in place before persisting via ' +
              'the public TaskService.SaveTask; the in-place mutation is outside the per-task write lock, so a ' +
              'concurrent MutateTaskInternal can still race it (field-level lost update / cache-ahead-of-DB).',
    hardening: '1f327236',
  },
  {
    site: 'MCPServer/Services/TaskService.cs: RecalculateAutoStatus (auto-status checklist-derived activation)',
    exposure: 'The single-active-per-assignee invariant is now enforced under the per-assignee activation lock at ' +
              'ALL DELIBERATE make-active paths — SetTaskActive, UpdateTaskStatus auto-resume, AND ClaimTask, which ' +
              'routes through the shared ActivateExclusively primitive (7c59c004). The ONE remaining make-active path ' +
              'is RecalculateAutoStatus: it sets sub_status=active as a SIDE-EFFECT of checklist progress ' +
              '(in_progress && sub_status==null) WITHOUT pausing the assignee current active task. Closing it is NOT ' +
              'mechanical locking — it is a SEMANTIC PRECEDENCE decision: should an auto-progressing task STEAL ' +
              '"active" from the assignee explicit active work? Almost certainly not, but that is a behavior change to ' +
              'auto-status across 4 entangled callers with lifecycle-board risk. OWNER-scoped decision (change ' +
              'auto-status to defer to explicit active, OR ratify the narrow auto-derived edge as a residual) → 651105b3.',
    hardening: '651105b3',
  },
  {
    site: 'Services/TaskDatabase.cs: GetActiveTaskForAgent + GetStaleTasksForTerminal (assignee COLLATE NOCASE read queries)',
    exposure: 'These read-resolution queries filter tasks.assignee with COLLATE NOCASE, which case-folds ONLY ASCII, ' +
              'whereas the C# agent-name domain uses OrdinalIgnoreCase (folds non-ASCII too, e.g. É↔é). For a ' +
              'non-ASCII case-variant name, GetActiveTaskForAgent("élodie") can MISS an "Élodie" active row → ' +
              'returns null (self-correcting: the caller re-resolves / user re-activates). NOT durable corruption ' +
              '(the durable SetTaskActive two-active path no longer depends on collation — it pauses by C#-discovered ' +
              'id). Correct under the "agent names are ASCII" assumption, which ValidateAgentName does not currently ' +
              'enforce. The follow-up decides: an ASCII ingress gate (make the assumption provable) OR canonicalize ' +
              'assignee — AFTER confirming whether non-ASCII agent names are ever legitimate (do not tighten validation blind).',
    hardening: '153fde77',
  },
  {
    site: 'MCPServer/Services/TaskService.cs: ReorderTask rebalance (RebalanceSortOrder before the affected per-task locks)',
    exposure: 'ReorderTask calls _taskDb.RebalanceSortOrder(column) OUTSIDE the affected tasks per-task locks. A ' +
              'concurrent MutateTaskInternal on an affected sibling can clone the old cached SortOrder, then its ' +
              'full-row SaveTask (sort_order = COALESCE(@sortOrder, sort_order)) persists the STALE rank after the ' +
              'rebalance, undoing it for that row. SORT/DISPLAY-RANK ONLY — self-healing on the next reorder, cache ' +
              'and DB stay coherent, NOT state corruption. Pre-existing (rebalance was never under per-task locks; ' +
              "7c59c004's per-task lock is new). Full fix = hold all column locks across rebalance (heavy) or make " +
              'rebalance the authoritative sort writer. Deferred.',
    hardening: '503aa430',
  },
];

// CORE PERSIST + RAW CACHE WRITE patterns (the divergence-creating writes). Targeted column/side-table
// writers are deliberately excluded (see header).
const MUTATION_PATTERNS = [
  /_taskDb\.SaveTask\s*\(/g,
  /_taskDb\.DeleteTask\s*\(/g,
  /_taskDb\.SaveProfile\s*\(/g,
  /_taskDb\.DeleteProfile\s*\(/g,
  /_projectDb\.SaveProject\s*\(/g,
  /_projectDb\.DeleteProject\s*\(/g,
  // raw cache writes: indexer-assign (not ==), TryAdd / TryRemove / Clear on the three caches
  /_tasks\s*\[[^\]]*\]\s*=(?!=)/g,
  /_projects\s*\[[^\]]*\]\s*=(?!=)/g,
  /_profiles\s*\[[^\]]*\]\s*=(?!=)/g,
  /_tasks\.(?:TryAdd|TryRemove|Clear)\s*\(/g,
  /_projects\.(?:TryAdd|TryRemove|Clear)\s*\(/g,
  /_profiles\.(?:TryAdd|TryRemove|Clear)\s*\(/g,
];

// ── MAKE-ACTIVE STATE-WRITE AUTHORITY (7c59c004 Codex class-close) ───────────────────────────────────
// A SECOND invariant, distinct from the write-path/divergence one above. The single-active-per-assignee
// rule needs EVERY write that makes a task active or paused (SubStatus = "active" | "paused") to run under
// the per-assignee activation lock; otherwise an off-lock write can clobber a serialized activation — the
// urgent-ClaimTask stale-pause → durable ZERO-active bug Codex caught (PauseTaskWithSummary re-paused the
// pre-lock active task after ActivateExclusively released the lock, erasing a concurrent re-activation).
// "Under the lock" isn't statically provable, so we ENUMERATE the methods permitted to contain such a
// write, each human-verified:
//   • ActivateExclusively   — the SOLE authority: pauses sibling(s) + activates the target in one txn,
//                              under the assignee lock + per-task locks.
//   • UpdateTaskStatus      — its done→auto-resume writes SubStatus="active" UNDER the same assignee lock
//                              (the New-1 guard block); verified at that site.
//   • RecalculateAutoStatus — the ONE Owner-ratified (ii) exception: auto-status derives active from
//                              checklist progress OFF the lock, without pausing the assignee's current
//                              active. Ratified as a documented residual → 651105b3.
// Any OTHER method writing SubStatus="active"/"paused" is a NEW off-lock make-active state write and FAILS
// loudly — this is what ENDS the whack-a-mole: after ActivateExclusively became the authority, a caller-side
// re-pause/re-activate can't sneak back in unseen. Scoped to the "active"/"paused" literals: "queued"
// (QueueTaskForTerminal) and SubStatus=null vacates (done/todo/reprioritize transitions) are NOT activations
// of the single-active invariant, so they're intentionally out of scope.
const MAKE_ACTIVE_AUTHORITIES = new Set(['ActivateExclusively', 'UpdateTaskStatus', 'RecalculateAutoStatus']);
const MAKE_ACTIVE_PATTERNS = [
  /SubStatus\s*=\s*"active"/g,
  /SubStatus\s*=\s*"paused"/g,
];
// Only TaskService.cs owns the kanban make-active surface (verified by a repo-wide grep — the DB-layer
// sub_status writes all live inside SetTaskActiveTransactional, which only ActivateExclusively calls).
const MAKE_ACTIVE_TARGET = 'MCPServer/Services/TaskService.cs';

// Blank comment + string content (offset-preserving). Same state machine as verify-taskdb-gate.mjs.
function maskCodeOnly(src) {
  let out = '';
  let state = 'code';
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
    if (state === 'line') { if (c === '\n') { state = 'code'; out += '\n'; continue; } out += (c === '\r' ? '\r' : ' '); continue; }
    if (state === 'block') { if (c === '*' && c2 === '/') { state = 'code'; out += '  '; i++; continue; } out += (c === '\n' ? '\n' : c === '\r' ? '\r' : ' '); continue; }
    if (state === 'str') { if (c === '\\') { out += '  '; i++; continue; } if (c === '"') { state = 'code'; out += ' '; continue; } out += (c === '\n' ? '\n' : ' '); continue; }
    if (state === 'verq') { if (c === '"' && c2 === '"') { out += '  '; i++; continue; } if (c === '"') { state = 'code'; out += ' '; continue; } out += (c === '\n' ? '\n' : ' '); continue; }
    if (state === 'chr') { if (c === '\\') { out += '  '; i++; continue; } if (c === '\'') { state = 'code'; out += ' '; continue; } out += ' '; continue; }
  }
  return out;
}

// Blank ONLY comments (offset-preserving), PRESERVING string literals. The make-active patterns key on the
// "active"/"paused" STRING LITERAL, which maskCodeOnly (used for the write-path scan) blanks — so they need
// this variant instead. We still blank comments (so a doc-comment mention of SubStatus="active" never counts)
// and still track string state (so a `//` inside a string isn't mistaken for a comment). A real string
// literal that happened to contain the assignment text is a non-issue: it would over-count in a non-authority
// method and fail loudly for a human, never a false negative.
function maskCommentsOnly(src) {
  let out = '';
  let state = 'code';
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    const c2 = i + 1 < src.length ? src[i + 1] : '';
    if (state === 'code') {
      if (c === '/' && c2 === '/') { state = 'line'; out += '  '; i++; continue; }
      if (c === '/' && c2 === '*') { state = 'block'; out += '  '; i++; continue; }
      if (c === '@' && c2 === '"') { state = 'verq'; out += '@"'; i++; continue; }
      if (c === '"') { state = 'str'; out += '"'; continue; }
      if (c === '\'') { state = 'chr'; out += '\''; continue; }
      out += c; continue;
    }
    if (state === 'line') { if (c === '\n') { state = 'code'; out += '\n'; continue; } out += (c === '\r' ? '\r' : ' '); continue; }
    if (state === 'block') { if (c === '*' && c2 === '/') { state = 'code'; out += '  '; i++; continue; } out += (c === '\n' ? '\n' : c === '\r' ? '\r' : ' '); continue; }
    if (state === 'str') { if (c === '\\') { out += c + c2; i++; continue; } if (c === '"') { state = 'code'; out += '"'; continue; } out += c; continue; }
    if (state === 'verq') { if (c === '"' && c2 === '"') { out += '""'; i++; continue; } if (c === '"') { state = 'code'; out += '"'; continue; } out += c; continue; }
    if (state === 'chr') { if (c === '\\') { out += c + c2; i++; continue; } if (c === '\'') { state = 'code'; out += '\''; continue; } out += c; continue; }
  }
  return out;
}

// Index every method declaration (name + start offset). C# methods don't nest (local functions aside,
// which don't declare our mutations), so the enclosing method of any offset is the nearest declaration
// before it. Matches an access modifier + return type + name + '(' — good enough to anchor enclosure.
// The return type is EITHER a plain type token (List<T>, Foo?, string[], ...) OR a parenthesized TUPLE
// (List<string> A, string B) — the tuple arm is load-bearing: without it a tuple-return method (e.g.
// ActivateExclusively) is NOT indexed, and the census misattributes its cache writes to the preceding
// decl (7c59c004 — this exact blind spot hid ActivateExclusively's swaps; the tuple negative fixture
// in the self-test guards it). \s+ between the two spans the newline C# style splits tuple-sig + name onto.
const METHOD_DECL = /(?:^|\n)\s*(?:public|private|internal|protected)(?:\s+(?:static|async|override|virtual|sealed|new|unsafe))*\s+(?:\([^)]*\)|[\w<>,.[\]?]+)\s+(\w+)\s*\(/g;

function indexMethods(code) {
  const methods = [];
  for (const m of code.matchAll(METHOD_DECL)) {
    methods.push({ name: m[1], index: m.index });
  }
  return methods;
}

function lineOf(src, index) {
  let line = 1;
  for (let i = 0; i < index && i < src.length; i++) if (src[i] === '\n') line++;
  return line;
}

function enclosingMethod(methods, index) {
  let found = null;
  for (const m of methods) {
    if (m.index <= index) found = m;
    else break;
  }
  return found;
}

// Analyze a source string. Returns { violations: [{method, line, snippet}], mutationCount }.
function analyze(src) {
  const code = maskCodeOnly(src);
  const methods = indexMethods(code);
  const violations = [];
  let mutationCount = 0;

  for (const pat of MUTATION_PATTERNS) {
    for (const hit of code.matchAll(pat)) {
      mutationCount++;
      const encl = enclosingMethod(methods, hit.index);
      const name = encl ? encl.name : '(top-level)';
      if (!ALLOWED.has(name)) {
        violations.push({ method: name, line: lineOf(src, hit.index), snippet: src.slice(hit.index, hit.index + 40).split('\n')[0].trim() });
      }
    }
  }
  return { violations, mutationCount };
}

// Analyze make-active state writes (7c59c004): every SubStatus="active"/"paused" must sit inside a
// MAKE_ACTIVE_AUTHORITIES method. Same masking + enclosing-method machinery as the write-path census.
function analyzeMakeActive(src) {
  const codeMasked = maskCodeOnly(src);       // method indexing: strings + comments blanked
  const strMasked = maskCommentsOnly(src);    // scan: comments blanked, string literals PRESERVED
  const methods = indexMethods(codeMasked);
  const violations = [];
  let writeCount = 0;
  for (const pat of MAKE_ACTIVE_PATTERNS) {
    for (const hit of strMasked.matchAll(pat)) {
      writeCount++;
      const encl = enclosingMethod(methods, hit.index);
      const name = encl ? encl.name : '(top-level)';
      if (!MAKE_ACTIVE_AUTHORITIES.has(name)) {
        violations.push({ method: name, line: lineOf(src, hit.index), snippet: src.slice(hit.index, hit.index + 40).split('\n')[0].trim() });
      }
    }
  }
  return { violations, writeCount };
}

// ---- self-test ------------------------------------------------------------------------------------
function selfTest() {
  const fixtures = [
    {
      name: 'write in a helper method PASSES',
      expectOk: true,
      src: `class B {\n        private KanbanTask MutateTaskInternal(string id) {\n            _taskDb.SaveTask(updated);\n            _tasks[id] = updated;\n            return updated;\n        }\n}`,
    },
    {
      name: 'write in a NAMED bypass PASSES',
      expectOk: true,
      src: `class B {\n        private void LoadPersistedTasks() {\n            foreach (var t in x) { _tasks.TryAdd(t.Id, t); }\n        }\n}`,
    },
    {
      name: 'core persist in a NON-allowlisted method FAILS',
      expectOk: false,
      src: `class B {\n        public void SneakyUpdate(string id) {\n            var t = _tasks[id];\n            t.Status = "done";\n            _taskDb.SaveTask(t);\n        }\n}`,
    },
    {
      name: 'raw cache write in a NON-allowlisted method FAILS',
      expectOk: false,
      src: `class B {\n        public void SneakyCache(string id, KanbanTask t) {\n            _tasks[id] = t;\n        }\n}`,
    },
    {
      name: 'project cache TryAdd in a NON-allowlisted method FAILS',
      expectOk: false,
      src: `class B {\n        public void SneakyProject(Project p) {\n            _projects.TryAdd(p.Id, p);\n        }\n}`,
    },
    {
      name: 'write only inside a // comment PASSES (masking works)',
      expectOk: true,
      src: `class B {\n        public void Reader(string id) {\n            // old code did _taskDb.SaveTask(t); and _tasks[id] = t;\n            return;\n        }\n}`,
    },
    {
      name: 'targeted column writer (AddTaskHelper) is NOT a core persist — PASSES anywhere',
      expectOk: true,
      src: `class B {\n        public void AddHelperElsewhere(string id) {\n            _taskDb.AddTaskHelper(id, "x", "y");\n        }\n}`,
    },
    {
      name: 'comparison (_tasks[id] == other) is not a write — PASSES',
      expectOk: true,
      src: `class B {\n        public bool Cmp(string id, KanbanTask o) {\n            return _tasks[id] == o;\n        }\n}`,
    },
    {
      // e7e89f4b extraction safety-net: the write path moved to TaskService.cs. Prove the census still
      // falsifies on a raw _tasks write in a TaskService method that ISN'T an allowlisted helper/bypass —
      // exactly the "silently green on an emptied broker" hole Alice flagged as non-negotiable.
      name: 'post-extraction: raw _tasks write in a non-allowlisted TaskService method FAILS',
      expectOk: false,
      src: `class TaskService {\n        public void SneakyRelocatedWrite(string id, KanbanTask t) {\n            _tasks[id] = t;\n        }\n}`,
    },
    {
      name: 'post-extraction: the relocated write-path helper (MutateTaskInternal) PASSES',
      expectOk: true,
      src: `class TaskService {\n        private KanbanTask MutateTaskInternal(string id) {\n            _taskDb.SaveTask(updated);\n            _tasks[id] = updated;\n            return updated;\n        }\n}`,
    },
    {
      // 86f3fd21 second-region safety-net: the profile write path moved to ProfileService.cs. Prove the
      // census still falsifies on a raw _profiles write in a ProfileService method that ISN'T an allowlisted
      // helper/bypass — the same "silently green on an emptied broker" hole, now for the profile cache.
      name: 'post-extraction: raw _profiles write in a non-allowlisted ProfileService method FAILS',
      expectOk: false,
      src: `class ProfileService {\n        public void SneakyRelocatedProfileWrite(string id, TeamMemberProfile p) {\n            _profiles[id] = p;\n        }\n}`,
    },
    {
      name: 'post-extraction: the relocated profile write-path helper (MutateProfile) PASSES',
      expectOk: true,
      src: `class ProfileService {\n        public TeamMemberProfile MutateProfile(string id) {\n            _taskDb.SaveProfile(updated);\n            _profiles[id] = updated;\n            return updated;\n        }\n}`,
    },
    {
      // e1643ccc close condition: RegisterTerminal/UnregisterTerminal were CONVERTED onto the write path and
      // REMOVED from NAMED_BYPASSES. Prove the census now FAILS if a raw profile write reappears in one of
      // them — the whole point of paying the debt is that the bypass can't silently come back.
      name: 'post-conversion: raw _profiles/SaveProfile write in RegisterTerminal now FAILS (no longer a bypass)',
      expectOk: false,
      src: `class MessageBroker {\n        public RegisterResult RegisterTerminal(string name) {\n            _profiles.TryAdd(name, newProfile);\n            _taskDb.SaveProfile(newProfile);\n            return ok;\n        }\n}`,
    },
    {
      // The persist-first primitive (e1643ccc): InsertProfile is now the public write-path entry the
      // registration region uses instead of a cache-only add — allowlisted as a write-path helper.
      name: 'post-conversion: InsertProfile persist-first write-path helper PASSES',
      expectOk: true,
      src: `class ProfileService {\n        public TeamMemberProfile InsertProfile(TeamMemberProfile profile) {\n            _taskDb.SaveProfile(profile);\n            _profiles[profile.Id] = profile;\n            return profile;\n        }\n}`,
    },
    {
      // 7c59c004 census-blind-spot close: a TUPLE-return method must be INDEXED so a raw write inside it is
      // attributed to IT, not the preceding decl. This is the exact hole that hid ActivateExclusively's swaps
      // at b9c046e (they were misattributed to the preceding AssigneeActivationLock). A raw _tasks write in a
      // tuple-return, NON-allowlisted method MUST FAIL — if the METHOD_DECL tuple arm regresses, this flips to
      // a false PASS and trips here.
      name: 'tuple-return: raw _tasks write in a non-allowlisted tuple-return method FAILS',
      expectOk: false,
      src: `class TaskService {\n        private (List<string> A, string B) SneakyTupleWrite(string id, KanbanTask t) {\n            _tasks[id] = t;\n            return (null, null);\n        }\n}`,
    },
    {
      // The positive side: once the tuple arm indexes it AND it's a NAMED_BYPASS, a write in the real shape —
      // tuple signature split across a newline before the name, as ActivateExclusively is written — PASSES
      // (correctly attributed to the allowlisted primitive, not the preceding decl).
      name: 'tuple-return: raw _tasks write in the allowlisted multi-line tuple primitive (ActivateExclusively) PASSES',
      expectOk: true,
      src: `class TaskService {\n        private (List<string> A, string B)\n            ActivateExclusively(string id, KanbanTask t) {\n            _tasks[id] = t;\n            return (null, null);\n        }\n}`,
    },
    {
      // 7c59c004 make-active authority census. The bug: an off-lock SubStatus="paused" re-write in a
      // caller (PauseTaskWithSummary) could clobber a serialized re-activation → durable zero-active. Prove
      // a make-active state write (active/paused) in a NON-authority method FAILS — the whack-a-mole ender.
      kind: 'makeactive',
      name: 'make-active: SubStatus="paused" in a non-authority method FAILS (the off-lock re-pause bug)',
      expectOk: false,
      src: `class TaskService {\n        private void EmitPauseSummaries(string id) {\n            MutateTaskInternal(id, t => { t.SubStatus = "paused"; });\n        }\n}`,
    },
    {
      kind: 'makeactive',
      name: 'make-active: SubStatus="active" in ActivateExclusively (the sole authority) PASSES',
      expectOk: true,
      src: `class TaskService {\n        private (List<string> A, string B) ActivateExclusively(string id) {\n            activated.SubStatus = "active";\n            return (null, null);\n        }\n}`,
    },
    {
      kind: 'makeactive',
      name: 'make-active: SubStatus="active" in RecalculateAutoStatus (Owner-ratified (ii) exception) PASSES',
      expectOk: true,
      src: `class TaskService {\n        private void RecalculateAutoStatus(KanbanTask task) {\n            task.SubStatus = "active";\n        }\n}`,
    },
    {
      kind: 'makeactive',
      name: 'make-active: SubStatus="queued" is NOT an activation of the single-active invariant — PASSES anywhere',
      expectOk: true,
      src: `class TaskService {\n        private void QueueTaskForTerminal(string id) {\n            MutateTaskInternal(id, t => { t.SubStatus = "queued"; });\n        }\n}`,
    },
  ];

  let pass = 0;
  for (const f of fixtures) {
    const r = (f.kind === 'makeactive' ? analyzeMakeActive(f.src) : analyze(f.src));
    const ok = r.violations.length === 0;
    const good = ok === f.expectOk;
    if (good) pass++;
    console.log(`${good ? 'PASS' : 'FAIL'}  [self-test] expect ${f.expectOk ? 'OK' : 'VIOLATION'} — ${f.name}` +
      (good ? '' : `  (got ${ok ? 'OK' : 'VIOLATION'}: ${r.violations.map(v => `${v.method}@${v.line}`).join(', ')})`));
  }
  console.log(`\nself-test: ${pass}/${fixtures.length} fixtures behaved as expected.`);
  process.exit(pass === fixtures.length ? 0 : 1);
}

// ---- real check -----------------------------------------------------------------------------------
function realCheck() {
  let totalMutations = 0;
  let anyViolation = false;
  for (const target of TARGETS) {
    const file = path.join(REPO_ROOT, target);
    if (!fs.existsSync(file)) {
      console.error(`verify-writepath: target not found: ${file}`);
      process.exit(2);
    }
    const src = fs.readFileSync(file, 'utf8');
    const { violations, mutationCount } = analyze(src);
    totalMutations += mutationCount;
    if (violations.length === 0) {
      console.log(`PASS  ${target}: ${mutationCount} core-persist/raw-cache write(s), all inside a write-path helper or a named bypass.`);
    } else {
      anyViolation = true;
      console.error(`FAIL  ${target}: ${violations.length} write(s) bypass the write path and aren't allowlisted:`);
      for (const v of violations) console.error(`  - ${v.method} (line ${v.line}): ${v.snippet}`);
    }
  }

  // Second invariant (7c59c004 Codex class-close): make-active state-write authority. Every
  // SubStatus="active"/"paused" write in the kanban make-active surface must sit in a MAKE_ACTIVE_AUTHORITIES
  // method (ActivateExclusively / the under-lock UpdateTaskStatus auto-resume / the Owner-ratified
  // RecalculateAutoStatus) — a new off-lock make-active state write fails here.
  {
    const maFile = path.join(REPO_ROOT, MAKE_ACTIVE_TARGET);
    if (!fs.existsSync(maFile)) {
      console.error(`verify-writepath: make-active target not found: ${maFile}`);
      process.exit(2);
    }
    const { violations, writeCount } = analyzeMakeActive(fs.readFileSync(maFile, 'utf8'));
    if (violations.length === 0) {
      console.log(`PASS  ${MAKE_ACTIVE_TARGET}: ${writeCount} make-active state write(s) (SubStatus=active/paused), all in a make-active authority (${[...MAKE_ACTIVE_AUTHORITIES].join(', ')}).`);
    } else {
      anyViolation = true;
      console.error(`FAIL  ${MAKE_ACTIVE_TARGET}: ${violations.length} make-active state write(s) OFF the assignee-lock authority:`);
      for (const v of violations) console.error(`  - ${v.method} (line ${v.line}): ${v.snippet}`);
      console.error(`    Every SubStatus="active"/"paused" write must be inside ActivateExclusively (the sole authority),`);
      console.error(`    the UpdateTaskStatus auto-resume (under the assignee lock), or the Owner-ratified RecalculateAutoStatus (651105b3).`);
    }
  }

  if (anyViolation) {
    console.error(`\nRoute the write through a write-path helper (MutateTaskInternal / TryMutateTask / Insert* / Delete*),`);
    console.error(`or, if it is a genuine ratified bypass, add its method name to NAMED_BYPASSES in this file.`);
    process.exit(1);
  }
  console.log(`\nOK  ${totalMutations} core-persist/raw-cache write(s) across ${TARGETS.length} files, all allowlisted (${ALLOWED.size} allowed methods).`);
  if (KNOWN_UNLOCKED_EXPOSURES.length) {
    console.log(`\nKnown unlocked-write exposures (enumerated, deferred — NOT failures):`);
    for (const e of KNOWN_UNLOCKED_EXPOSURES) {
      console.log(`  - [${e.hardening}] ${e.site}\n      ${e.exposure}`);
    }
  }
  process.exit(0);
}

if (doSelfTest) selfTest();
else realCheck();
