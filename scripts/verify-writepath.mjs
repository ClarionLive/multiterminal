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
]);

const ALLOWED = new Set([...WRITE_PATH_HELPERS, ...NAMED_BYPASSES]);

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

// Index every method declaration (name + start offset). C# methods don't nest (local functions aside,
// which don't declare our mutations), so the enclosing method of any offset is the nearest declaration
// before it. Matches an access modifier + return type + name + '(' — good enough to anchor enclosure.
const METHOD_DECL = /(?:^|\n)\s*(?:public|private|internal|protected)(?:\s+(?:static|async|override|virtual|sealed|new|unsafe))*\s+[\w<>,.\[\]?]+\s+(\w+)\s*\(/g;

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
  ];

  let pass = 0;
  for (const f of fixtures) {
    const r = analyze(f.src);
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
  if (anyViolation) {
    console.error(`\nRoute the write through a write-path helper (MutateTaskInternal / TryMutateTask / Insert* / Delete*),`);
    console.error(`or, if it is a genuine ratified bypass, add its method name to NAMED_BYPASSES in this file.`);
    process.exit(1);
  }
  console.log(`\nOK  ${totalMutations} core-persist/raw-cache write(s) across ${TARGETS.length} files, all allowlisted (${ALLOWED.size} allowed methods).`);
  process.exit(0);
}

if (doSelfTest) selfTest();
else realCheck();
