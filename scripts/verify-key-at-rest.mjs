#!/usr/bin/env node
// verify-key-at-rest.mjs — the falsifiable half of task b42b1883 item 9: prove the GitHub App
// private key is at rest in EXACTLY ONE authorized location, that no agent-reachable artifact
// carries a copy, and that a terminal's environment carries no GitHub token at all.
//
// WHY THIS IS NOT LIKE THE OTHER verify-*.mjs SCRIPTS. Every other census in this folder
// (verify-writepath, verify-taskdb-gate, verify-no-secret-serialization, …) reads SOURCE and asks
// "could this code do the wrong thing?". This one reads the MACHINE and asks "did it?". The ticket's
// own words are "BY GREP NOT BY INTENTION" — a source census cannot discharge that, because the
// claim is about bytes on disk, not about shapes in C#. It therefore does NOT belong in
// `npm run verify` (there is no MT install, no key and no settings.txt on a CI runner, and a
// machine audit that finds nothing to audit must never be mistaken for a pass — see GATE 0).
//
// ── FOUR TRAPS, EACH HIT FOR REAL WHILE MEASURING. Every one produced a wrong answer first. ──
//
//  (1) GATE 0 — A SEARCH FOR A NEEDLE THAT DOES NOT EXIST IS VACUOUSLY CLEAN.
//      If no key is stored, "the key is not in SQLite" is true and meaningless. The first draft
//      would have reported a serene all-clear on a machine where the App was never configured.
//      So the needle is derived from the stored key, and its ABSENCE aborts with a distinct exit
//      code (2) that is not success. This is the single most important line in the file.
//
//  (2) THE INVARIANT IS "EXACTLY ONE COPY, IN THE AUTHORIZED PLACE" — NOT "ZERO MATCHES".
//      Zero-matches is the obvious assertion and it is wrong in both directions: it fails on the
//      legitimate store (%APPDATA%\multiterminal\settings.txt, where the key is SUPPOSED to be), and
//      if you then except that path it passes vacuously the moment the key is deleted. Requiring a
//      hit in the authorized location and nowhere else is the only form that states the design and
//      cannot go green on an empty machine. See verdictForLocations().
//
//  (3) MARKER NEEDLES MUST BE CLASSIFIED, NOT COUNTED — OR THE AUDIT FAILS ON ITSELF.
//      "-----BEGIN RSA PRIVATE KEY-----" is a reasonable thing to grep for and a terrible thing to
//      count. The measurement pass found 5 hits in multiterminal.db and every one was benign: three
//      were THE AUDIT SCRIPT'S OWN SOURCE, captured into the session transcript that MT stores in
//      SQLite, and two were the test fixture (whose body reads NOTAREALKEYJUSTAMARKER). A counting
//      check reports FAIL on the very session that runs it, which trains the reader to ignore it.
//      classifyMarkerHit() asks the only question that matters: is the marker followed by a long
//      unbroken base64 run (key material) or by prose/escapes (someone talking about keys)?
//
//  (4) THE SWEEP MUST SEE HIDDEN AND IGNORED FILES.
//      ripgrep skips .gitignored and dotted paths by default, so the first tree sweep silently never
//      looked in bin/, obj/, .git/ — or, fatally, in .claude/worktrees/, where every task branch
//      lives. It reported CLEAN over ~0 of the interesting files. `-uu` (--no-ignore --hidden) is
//      not a tuning flag here; without it the check is decoration.
//
//  (5) A SWEEP ROOT THAT DOES NOT EXIST MUST FAIL, NOT DECREMENT A COUNTER.
//      The first working version printed "sweep roots: 3/5 present" and then PASS. Two roots —
//      Deploy and staged, i.e. THE CODE THAT ACTUALLY RUNS — had resolved to nonexistent paths and
//      were never searched, because this file derives its root from its own location and it was
//      running from `.claude/worktrees/b42b1883`. Same cause narrowed the repo sweep to that ONE
//      worktree instead of the main checkout that contains them all. A missing root is now a hard
//      failure: an audit is a claim about coverage, and silently auditing less while still printing
//      PASS is the precise shape of every defect above. resolveRoots() derives the main checkout
//      from `git --git-common-dir` (worktree-correct) and honours the documented
//      MULTITERMINAL_DEPLOY_PATH / MULTITERMINAL_STAGED_PATH overrides.
//
// ── WHAT A GREEN RUN DOES *NOT* MEAN. READ THIS BEFORE QUOTING IT. ──
// It does NOT mean an agent cannot obtain the key. MultiTerminal.exe and every agent it hosts run as
// the SAME Windows identity, and DPAPI CurrentUser is scoped to that identity — so any agent that
// decides to can read settings.txt and unprotect the blob. That was verified, on a probe value
// created for the purpose rather than on the real key (--probe-dpapi reproduces it). DPAPI here is
// encryption against OTHER users and against offline access to the disk; it is not a boundary
// against code running as the user. What this script proves is narrower and still worth having:
// the key exists in one place only, and does not leak into the many artifacts an agent reads by
// routine — the task DB, project configs, repo trees, logs and session transcripts. That last one
// is not hypothetical: task ea7d9cf9 is the incident where a real PAT reached agent transcripts on
// disk and had to be rotated.
//
// Usage:
//   node scripts/verify-key-at-rest.mjs              # audit this machine; exit 1 on violation, 2 if unaudit-able
//   node scripts/verify-key-at-rest.mjs --self-test  # pure-logic fixtures; runs anywhere, no MT install
//   node scripts/verify-key-at-rest.mjs --probe-dpapi # demonstrate the honest limit above

import fs from 'fs';
import os from 'os';
import path from 'path';
import { execFileSync } from 'child_process';

const argv = process.argv.slice(2);
const doSelfTest = argv.includes('--self-test');
const doProbe = argv.includes('--probe-dpapi');

const REPO_ROOT = path.join(
  path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');

// The ONE authorized location. Per the ticket: one copy, in %APPDATA%\multiterminal, DPAPI-encrypted
// — never per-project, never in a repo, never in SQLite.
const SETTINGS_PATH = path.join(process.env.APPDATA || '', 'multiterminal', 'settings.txt');
const PEM_KEY = 'GitHub.App.PrivateKeyPem';
const CLIENT_SECRET_KEY = 'GitHub.App.ClientSecret';

// A 96-char prefix of the stored ciphertext. Long enough to be unique beyond any doubt, short enough
// that the needle itself is not a usable copy of the secret. Never printed, never written to disk.
const NEEDLE_LEN = 96;

// Env var names that would mean a GitHub credential was handed to the terminal — the thing item 6
// asserted does not happen and item 9 re-checks from the outside. CLAUDE_CODE_MESSAGING_TOKEN and
// other non-GitHub tokens are deliberately NOT matched: flagging them would make the check noise.
const GITHUB_TOKEN_ENV_RE = /^(GH_TOKEN|GH_ENTERPRISE_TOKEN|GITHUB_TOKEN|GITHUB_PAT|GH_PAT|GITHUB_ACCESS_TOKEN)$/i;

// ── PURE LOGIC (self-testable without a machine to audit) ─────────────────────────────────────────

// Trap (3). Given the text immediately AFTER a PEM marker, decide whether it is real key material.
// A PEM body is a long unbroken base64 run on the next line(s). Prose, escaped-newline source
// literals ("\\n" as two characters) and script fragments are not.
export function classifyMarkerHit(textAfterMarker) {
  // Real PEM: optional CR/LF then >=40 contiguous base64 chars. An escaped literal in source reads
  // `\n` as backslash-n, which is NOT whitespace, so it correctly fails to match here.
  const m = /^[\r\n \t]*([A-Za-z0-9+/=]{40,})/.exec(textAfterMarker);
  return m ? 'key-material' : 'benign';
}

// Trap (2). The verdict rule. `hitPaths` are the files containing the ciphertext needle.
export function verdictForLocations(hitPaths, authorizedPath) {
  const norm = p => path.resolve(p).toLowerCase();
  const auth = norm(authorizedPath);
  const authorized = hitPaths.filter(p => norm(p) === auth);
  const foreign = hitPaths.filter(p => norm(p) !== auth);
  if (authorized.length === 0) {
    // Either the key was removed between GATE 0 and the sweep, or the sweep cannot see the file it
    // read the needle from. Both mean the all-clear below would be unearned.
    return { ok: false, kind: 'VACUOUS', foreign };
  }
  if (foreign.length > 0) return { ok: false, kind: 'LEAKED', foreign };
  return { ok: true, kind: 'CONTAINED', foreign: [] };
}

export function findGitHubTokenEnvVars(env) {
  return Object.keys(env).filter(k => GITHUB_TOKEN_ENV_RE.test(k) && String(env[k]).length > 0);
}

// ── SELF-TEST ─────────────────────────────────────────────────────────────────────────────────────
function selfTest() {
  let allOk = true; let ran = 0;
  const report = (label, name, got, exp) => {
    const ok = JSON.stringify(got) === JSON.stringify(exp);
    allOk = allOk && ok; ran++;
    console.log(`  ${ok ? 'ok  ' : 'FAIL'} [${label}] ${name}`);
    if (!ok) console.log(`        got ${JSON.stringify(got)} want ${JSON.stringify(exp)}`);
  };

  // --- classifyMarkerHit. The three benign shapes below are VERBATIM from the real measurement run
  //     against multiterminal.db; all three were reported as hits by the counting draft.
  const B64 = 'MIIEpAIBAAKCAQEAx4fT0Zq9nQ6NpVvVQnrGdVvAAAABBBBCCCCDDDDEEEEFFFFGGGG';
  report('marker', 'a real PEM body (newline then a long base64 run) is key material',
    classifyMarkerHit('\n' + B64), 'key-material');
  report('marker', 'CRLF form is key material too',
    classifyMarkerHit('\r\n' + B64), 'key-material');
  report('marker', 'REAL benign hit #1: this audit script quoted in a session transcript',
    classifyMarkerHit("'\\n$needles += '-----BEGIN PRIVATE KEY-----'"), 'benign');
  report('marker', 'REAL benign hit #2: the C# test fixture with escaped newlines',
    classifyMarkerHit('\\\\n" +\n            "MIIEpAIBAAKCAQEAx4fT0Zq9nQ6NpVvVQnrGdVv'), 'benign');
  report('marker', 'REAL benign hit #3: the fixture body that says it is not a key',
    classifyMarkerHit('\\\\nNOTAREALKEYJUSTAMARKER\\\\n-----END RSA PRIVATE KEY-----'), 'benign');
  report('marker', 'prose about keys is benign',
    classifyMarkerHit(' is what a PEM file starts with, as everyone knows'), 'benign');
  report('marker', 'a short base64-ish run is not a key body',
    classifyMarkerHit('\nMIIEpAIB'), 'benign');

  // --- verdictForLocations. Trap (2) in executable form.
  const AUTH = 'C:\\Users\\x\\AppData\\Roaming\\multiterminal\\settings.txt';
  report('verdict', 'THE design: exactly one copy, in the authorized store',
    verdictForLocations([AUTH], AUTH).kind, 'CONTAINED');
  report('verdict', 'case-insensitive path match (Windows)',
    verdictForLocations([AUTH.toUpperCase()], AUTH).kind, 'CONTAINED');
  report('verdict', 'THE vacuous pass this rule exists to prevent: no copy anywhere',
    verdictForLocations([], AUTH), { ok: false, kind: 'VACUOUS', foreign: [] });
  report('verdict', 'a copy in SQLite alongside the authorized one is a LEAK',
    verdictForLocations([AUTH, 'C:\\db\\multiterminal.db'], AUTH).kind, 'LEAKED');
  report('verdict', 'a copy ONLY outside the authorized store is a leak, not a pass',
    verdictForLocations(['C:\\repo\\.claude\\project.json'], AUTH).kind, 'VACUOUS');
  report('verdict', 'a leak names the offending path',
    verdictForLocations([AUTH, 'C:\\repo\\project.json'], AUTH).foreign, ['C:\\repo\\project.json']);

  // --- env scan.
  report('env', 'THE regression: a GitHub token handed to a terminal is flagged',
    findGitHubTokenEnvVars({ GH_TOKEN: 'ghs_abc' }), ['GH_TOKEN']);
  report('env', 'GITHUB_TOKEN flagged', findGitHubTokenEnvVars({ GITHUB_TOKEN: 'x' }), ['GITHUB_TOKEN']);
  report('env', 'negative-control: the REAL env of an MT terminal (item 6 wiring, no token)',
    findGitHubTokenEnvVars({
      GIT_CONFIG_COUNT: '2',
      GIT_CONFIG_KEY_1: 'credential.https://github.com.helper',
      GIT_CONFIG_VALUE_1: '!node .../git-credential-multiterminal.mjs',
      MULTITERMINAL_GH_SHIM_DIR: 'C:\\...\\shims',
    }), []);
  report('env', 'negative-control: an unrelated non-GitHub token is not flagged',
    findGitHubTokenEnvVars({ CLAUDE_CODE_MESSAGING_TOKEN: 'abc' }), []);
  report('env', 'negative-control: an empty GH_TOKEN is not a credential',
    findGitHubTokenEnvVars({ GH_TOKEN: '' }), []);

  console.log(allOk
    ? `\nSELF-TEST PASSED (${ran}/${ran}) — the marker classifier separates real key material from `
      + 'the audit talking about keys, the location rule rejects a vacuous all-clear as firmly as a '
      + 'leak, and the env scan flags GitHub tokens without flagging every token.'
    : `\nSELF-TEST FAILED (${ran} fixtures ran).`);
  process.exit(allOk ? 0 : 1);
}

// ── MACHINE AUDIT ─────────────────────────────────────────────────────────────────────────────────
function readStoredBlob(key) {
  if (!fs.existsSync(SETTINGS_PATH)) return null;
  for (const line of fs.readFileSync(SETTINGS_PATH, 'utf8').split(/\r?\n/)) {
    if (line.startsWith(key + '=')) {
      const v = line.slice(key.length + 1);
      return v.length ? v : null;
    }
  }
  return null;
}

function rgPath() {
  const p = path.join(REPO_ROOT, 'tools', 'rg.exe');
  return fs.existsSync(p) ? p : 'rg';
}

// Files containing `needle`, searched with --no-ignore --hidden (trap 4) and binary enabled.
function filesContaining(needle, roots) {
  const existing = roots.filter(r => fs.existsSync(r));
  if (!existing.length) return [];
  try {
    const out = execFileSync(rgPath(),
      ['-a', '-uu', '-F', '--files-with-matches', '-e', needle, '--', ...existing],
      { encoding: 'utf8', maxBuffer: 1 << 28 });
    return out.split(/\r?\n/).filter(Boolean);
  } catch (e) {
    if (e.status === 1) return [];          // rg: no matches
    throw new Error(`ripgrep failed (status ${e.status}): ${e.stderr || e.message}`);
  }
}

function probeDpapi() {
  const ps = `Add-Type -AssemblyName System.Security;`
    + `$b=[Text.Encoding]::UTF8.GetBytes('probe');`
    + `$e=[Security.Cryptography.ProtectedData]::Protect($b,$null,'CurrentUser');`
    + `$d=[Security.Cryptography.ProtectedData]::Unprotect($e,$null,'CurrentUser');`
    + `Write-Output ([Text.Encoding]::UTF8.GetString($d) -eq 'probe')`;
  try {
    return execFileSync('powershell', ['-NoProfile', '-Command', ps], { encoding: 'utf8' }).trim() === 'True';
  } catch { return null; }
}

if (doSelfTest) selfTest();

if (doProbe) {
  const ok = probeDpapi();
  console.log(`DPAPI CurrentUser round-trip from this process: ${ok}`);
  console.log(ok
    ? 'This process can unprotect anything MT protected. The key is encrypted AGAINST OTHER USERS\n'
      + 'and against offline disk access — NOT against code running as this user. Say so plainly\n'
      + 'rather than letting a green audit imply otherwise.'
    : 'Could not run the probe (non-Windows, or PowerShell unavailable).');
  process.exit(0);
}

// ---- GATE 0 (trap 1): refuse to audit what is not there. ------------------------------------------
const pemBlob = readStoredBlob(PEM_KEY);
if (!pemBlob) {
  console.log('PRECONDITION NOT MET — no GitHub App private key is stored on this machine.');
  console.log(`  looked in: ${SETTINGS_PATH}`);
  console.log('  Every absence check below would pass VACUOUSLY, so none of them ran. This is exit 2,');
  console.log('  deliberately NOT exit 0: "nothing to find" must never read as "nothing was found".');
  console.log('  Register the App (item 3) and re-run, or run --self-test to exercise the pure logic.');
  process.exit(2);
}
if (pemBlob.length < NEEDLE_LEN) {
  console.log(`PRECONDITION NOT MET — stored key blob is only ${pemBlob.length} chars; expected a DPAPI`);
  console.log('  base64 blob. Refusing to build a short, non-unique needle. exit 2.');
  process.exit(2);
}

const clientSecretBlob = readStoredBlob(CLIENT_SECRET_KEY);
const needles = [{ label: 'private key', value: pemBlob.slice(0, NEEDLE_LEN) }];
if (clientSecretBlob && clientSecretBlob.length >= NEEDLE_LEN) {
  needles.push({ label: 'client secret', value: clientSecretBlob.slice(0, NEEDLE_LEN) });
}

const HOME = os.homedir();
const APPDATA_MT = path.join(process.env.APPDATA || '', 'multiterminal');

// Trap (5). REPO_ROOT is wherever this FILE sits, which inside a worktree is
// `<main>/.claude/worktrees/<id>` — sweeping that covers one branch and misses the main checkout,
// and makes `../Deploy` a path that does not exist. `git --git-common-dir` points at the MAIN
// repository's .git from any worktree, so its parent is the real checkout.
function mainCheckout() {
  try {
    const gitCommonDir = execFileSync('git',
      ['rev-parse', '--path-format=absolute', '--git-common-dir'],
      { cwd: REPO_ROOT, encoding: 'utf8' }).trim();
    return path.dirname(path.resolve(gitCommonDir));
  } catch {
    return REPO_ROOT;                          // not a git checkout; audit what we have
  }
}

const MAIN = mainCheckout();
// Documented in CLAUDE.md: these default to siblings of the checkout and are overridden per machine.
const DEPLOY = process.env.MULTITERMINAL_DEPLOY_PATH || path.resolve(MAIN, '..', 'Deploy');
const STAGED = process.env.MULTITERMINAL_STAGED_PATH || path.resolve(MAIN, '..', 'staged');

const SWEEP_ROOTS = [
  { label: 'repo (main checkout + every worktree)', p: MAIN },
  { label: 'Deploy (what actually runs)', p: DEPLOY },
  { label: 'staged (build output)', p: STAGED },
  { label: 'MT appdata (task DB + WAL, logs, settings)', p: APPDATA_MT },
  { label: 'agent session transcripts', p: path.join(HOME, '.claude', 'projects') },
];

console.log('Key-at-rest audit (task b42b1883 item 9)');
console.log(`  authorized store : ${SETTINGS_PATH}`);
console.log(`  needles          : ${needles.map(n => n.label).join(', ')} `
  + `(${NEEDLE_LEN}-char ciphertext prefixes, derived at runtime, never printed or written)`);
console.log('  sweep roots      :');
for (const r of SWEEP_ROOTS) {
  console.log(`      ${fs.existsSync(r.p) ? 'ok     ' : 'MISSING'} ${r.label} — ${r.p}`);
}

const problems = [];

// Trap (5): coverage is part of the claim. Refuse to render a verdict over a narrowed sweep.
const missingRoots = SWEEP_ROOTS.filter(r => !fs.existsSync(r.p));
if (missingRoots.length) {
  console.log(`\nCANNOT AUDIT — ${missingRoots.length} sweep root(s) do not exist:`);
  for (const r of missingRoots) console.log(`  - ${r.label}: ${r.p}`);
  console.log('\n  These were NOT searched, so any "clean" verdict would describe less ground than it');
  console.log('  appears to. Point MULTITERMINAL_DEPLOY_PATH / MULTITERMINAL_STAGED_PATH at the real');
  console.log('  locations, or fix the path, and re-run. exit 2 — unaudit-able, not clean.');
  process.exit(2);
}

const sweepPaths = SWEEP_ROOTS.map(r => r.p);
for (const needle of needles) {
  const hits = filesContaining(needle.value, sweepPaths);
  const v = verdictForLocations(hits, SETTINGS_PATH);
  if (v.ok) {
    console.log(`  ok   ${needle.label}: CONTAINED — found only in the authorized store`);
  } else if (v.kind === 'VACUOUS') {
    problems.push(`${needle.label}: the sweep did not find the ciphertext in ${SETTINGS_PATH} itself. `
      + 'Either the key moved between read and sweep, or a sweep root is missing — in both cases an '
      + '"all clear" would be unearned. Do not treat this as a pass.'
      + (v.foreign.length ? ` Foreign copies seen: ${v.foreign.join(', ')}` : ''));
  } else {
    problems.push(`${needle.label}: LEAKED — copies outside the authorized store:\n      `
      + v.foreign.join('\n      '));
  }
}

// ---- the environment half: a terminal must carry no GitHub token at all. -------------------------
const envHits = findGitHubTokenEnvVars(process.env);
if (envHits.length === 0) {
  console.log('  ok   environment: no GitHub token variable present in this process');
} else {
  problems.push(`environment: GitHub token variable(s) present: ${envHits.join(', ')}. The design `
    + 'mints per-operation via the credential helper and gh shim precisely so that no token lives in '
    + 'an environment that outlives it (an installation token expires in ~1h; an env var does not).');
}

if (problems.length) {
  console.log(`\nFAIL (${problems.length}):`);
  for (const p of problems) console.log(`  - ${p}`);
  process.exit(1);
}

console.log('\nPASS — the App credentials exist in exactly one authorized location, appear in no '
  + 'database, project config, repo tree, log or agent transcript under the sweep roots, and this '
  + "terminal's environment carries no GitHub token.");
console.log('\nSCOPE — what this does NOT prove (run --probe-dpapi): agents run as the same Windows '
  + 'user as MultiTerminal, so DPAPI CurrentUser does not stop an agent that decides to read the key. '
  + 'This audit shows the key does not LEAK into what agents read by routine; it is not a sandbox.');
