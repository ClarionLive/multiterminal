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
//      classifyMarkerHit() asks the only question that matters: is the marker followed by enough of a
//      base64 run to be a USABLE key (key material), or by prose (someone talking about keys)?
//      ⚠️ IT USED TO SAY "prose/escapes", AND THE WORD "escapes" WAS A DEFECT, NOT A DESCRIPTION.
//      Treating an escaped newline as evidence of prose made the sweep blind to a key written into
//      JSON — `-----BEGIN … -----\nMII…` with a literal backslash-n — which is the encoding of BOTH
//      artifacts item 9's acceptance names by hand: project.json (checked in, so a leak there becomes
//      public) and agent transcripts (JSONL, which MT then imports into multiterminal.db). Pipeline
//      Run 2 found it, independently, on two different model providers (ticket 27002183).
//      The discriminator is now SEMANTIC — "is there enough of it to be a key?" — and never about
//      encoding shape, because an encoding says nothing about usability. See classifyMarkerHit.
//      ⭐ THE REUSABLE LESSON, AND IT IS NOT ABOUT REGEXES: the old comment asserted the escaped form
//      "correctly" failed to match, and the self-test had a fixture blessing that behaviour. A
//      confident comment plus a test agreeing with it is how a gap survives review — three reviewers
//      read that line and stopped. A test that encodes an assumption cannot also validate it.
//      ⚠️ AND IT MUST ACTUALLY RUN. For several sessions this trap was described here and exercised
//      only by --self-test: classifyMarkerHit() had no caller in the audit path, so the sweep looked
//      solely for the DPAPI CIPHERTEXT and a readable key in a project.json or a transcript — the
//      literal wording of item 9's acceptance — would not have been searched for at all. The audit
//      passed its own unit tests while never performing the measurement this paragraph promised, and
//      the header made it read as though it did. Pipeline Run 1 caught it; markerHits() +
//      verdictForMarkerHits() are the wiring. A trap that is documented but not executed is worse
//      than one that was never claimed, because it is trusted.
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
// A PEM body is a long unbroken base64 run on the next line(s). Prose and script fragments are not.
//
// ⚠️ THE ESCAPED FORM IS KEY MATERIAL, NOT PROSE — AND THIS FUNCTION USED TO SAY OTHERWISE.
// It required a REAL CR/LF before the base64 run, and its own comment called the escaped form
// "correctly" failing to match. That was wrong in the worst available direction. A key serialized
// into JSON reads `-----BEGIN … -----\nMII…` with a LITERAL backslash-n, and JSON is the encoding of
// the two sweep roots most likely to ever carry a leak: `.claude/project.json` (checked in, so a leak
// there becomes PUBLIC) and agent transcripts (JSONL — which MT's session pipeline then copies into
// multiterminal.db). Item 9's acceptance names "a project.json or a transcript" verbatim, so the one
// question this sweep exists to answer was the one encoding it could not see.
// Found by pipeline Run 2, independently by two gates on two different model providers (ticket
// 27002183). The word "correctly" is what made it survive three reviews: it told every later reader
// the case had been considered and settled.
//
// So: normalise escape sequences FIRST, then ask whether enough key follows to be USABLE.
//
// ⚠️ WHY THERE IS A SECOND TEST AT ALL, AND WHY IT IS SEMANTIC. Maximal strictness is not available:
// B64_FRAGMENT below lives in an agent transcript that cannot be cleaned, and trap (3)'s whole point
// is that an audit which fails on the session running it gets ignored — which is how the missing-caller
// defect survived. But the exemption must ask "is there enough of this to be a key?", a question about
// USABILITY, and never "does it have escaped newlines?", which says nothing about usability and is
// precisely what created the bug above. Owner decision 2026-09-13: END marker present, OR run >= 200.
//
// ⚠️ ACCEPTED GAP, recorded so it is never found as a surprise: a run of 40-199 chars with NO closing
// END marker is NOT flagged. Such a fragment cannot be used as a key. That is a deliberate choice
// about what this audit may miss, not an oversight — the levers are the threshold and the END clause.
export const KEY_MATERIAL_MIN_RUN = 200;

// JSON, JSONL and most source literals carry a line break as two characters. Collapse them to the
// real thing so one rule covers every encoding a key can be written in.
export function normaliseEscapedNewlines(s) {
  return s.replace(/\\r\\n|\\n|\\r/g, '\n');
}

// Split one rg match into the tail of EVERY PEM marker inside it.
//
// ⚠️ WHY THIS IS NOT `indexOf` ONCE. It used to be, and that was safe only while the capture window
// was 64 bytes. At 2400 bytes rg's greedy match absorbs any further BEGIN markers that fall inside it,
// so they never arrive as matches of their own — and classifying only the first lets a BENIGN marker
// SHADOW a real key a few hundred bytes behind it. That is the same silent false negative this whole
// trap exists to prevent, reintroduced from the opposite direction by the fix for it. It was caught
// only because the hit count FELL from 165 to 112 across the widening, and transcripts only ever grow:
// a count moving the wrong way was the single observable symptom.
//
// Pure and exported so the shadowing case is testable without a filesystem — the same reason
// classifyMarkerHit and verdictForMarkerHits are.
export function markerTailsIn(text) {
  const tails = [];
  for (let b = text.indexOf('-----BEGIN'); b >= 0; b = text.indexOf('-----BEGIN', b + 1)) {
    const t = text.indexOf(PEM_MARKER_TAIL, b);
    // The tail must belong to THIS marker. `-----BEGIN [A-Z ]{0,24}PRIVATE KEY-----` is at most ~40
    // chars, so a farther hit belongs to a later marker — or this BEGIN opens a CERTIFICATE block,
    // which must not be allowed to claim some unrelated private key's body further down the file.
    if (t < 0 || t - b > 40) continue;
    const from = t + PEM_MARKER_TAIL.length;
    // Bound each tail at the next BEGIN so one key's body can never be read as another's.
    const next = text.indexOf('-----BEGIN', from);
    tails.push(next < 0 ? text.slice(from) : text.slice(from, next));
  }
  return tails;
}

export function classifyMarkerHit(textAfterMarker) {
  const text = normaliseEscapedNewlines(textAfterMarker);
  const m = /^[\r\n \t]*([A-Za-z0-9+/=]{40,})/.exec(text);
  if (!m) return 'benign';
  // A complete key closes with an END marker; a truncated one is still key material once there is
  // enough of it. Either trigger suffices.
  // ⚠️ BOTH TRIGGERS REQUIRE THE WIDE CAPTURE WINDOW. Inside the 64-byte tail this function used to
  // be given, a real key and a 67-char fragment are LITERALLY indistinguishable — both present as
  // "40+ contiguous base64 chars". Widening PEM_MARKER_RE is a precondition of this rule, not a
  // refinement of it; narrow the window again and the discriminator silently stops discriminating.
  const hasEnd = text.includes('-----END');
  return (hasEnd || m[1].length >= KEY_MATERIAL_MIN_RUN) ? 'key-material' : 'benign';
}

// Trap (3), the verdict half. Given classified marker hits, decide the audit's answer.
//
// ⚠️ THE RULE IS "NO KEY MATERIAL ANYWHERE", INCLUDING THE AUTHORIZED STORE — which is the opposite
// of verdictForLocations() below, and the difference is the whole point. The ciphertext needle is
// SUPPOSED to live in settings.txt, so its rule is "exactly one copy, there". A PLAINTEXT PEM is
// supposed to exist nowhere at all: the authorized store holds a DPAPI blob, so a readable key
// sitting in it would mean the protection had failed, not that the key was where it belonged.
// Excepting the authorized path here would hide precisely that.
//
// Benign hits are expected and must NOT fail: this very file names the marker, and session
// transcripts quote it. See trap (3) — counting instead of classifying makes the audit fail on the
// session that runs it, which teaches the reader to ignore it.
export function verdictForMarkerHits(hits) {
  const keyMaterial = hits.filter(h => classifyMarkerHit(h.after) === 'key-material');
  return {
    ok: keyMaterial.length === 0,
    keyMaterialPaths: [...new Set(keyMaterial.map(h => h.path))],
    benignCount: hits.length - keyMaterial.length,
  };
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

  // --- classifyMarkerHit. The benign shapes below are VERBATIM from the real measurement run
  //     against multiterminal.db; all were reported as hits by the counting draft.
  //
  // ⚠️ B64_FRAGMENT WAS CALLED "a real PEM body" UNTIL 2026-09-13 AND IS NOT ONE. It is 67 base64
  //    chars — a DER prefix, not a key. Nothing can be unlocked with it. It stayed unchallenged
  //    because the old 64-byte capture window could not observe anything longer, so a fixture that
  //    short LOOKED indistinguishable from the real thing. That is now the point it tests.
  //    It is also the literal string pipeline Run 2 found in multiterminal.db: this file's own
  //    fixture, captured into a session transcript MT imports into SQLite. Keeping it benign is what
  //    stops the audit failing on the session that runs it (trap 3).
  const B64_FRAGMENT = 'MIIEpAIBAAKCAQEAx4fT0Zq9nQ6NpVvVQnrGdVvAAAABBBBCCCCDDDDEEEEFFFFGGGG';
  // A realistic body: long enough to be usable, so long enough to flag. 260 chars.
  const B64_FULL = B64_FRAGMENT + B64_FRAGMENT + B64_FRAGMENT + B64_FRAGMENT;
  const BS = String.fromCharCode(92);   // one literal backslash — never written as an escape here

  report('marker', 'a real PEM body (newline then a long base64 run) is key material',
    classifyMarkerHit('\n' + B64_FULL), 'key-material');
  report('marker', 'CRLF form is key material too',
    classifyMarkerHit('\r\n' + B64_FULL), 'key-material');

  // ⭐ THE REGRESSION. Its absence is why this self-test passed 23/23 while the sweep was blind to the
  //    one encoding that matters. A key written into JSON carries a LITERAL backslash-n, and JSON is
  //    what project.json and every agent transcript are. Built with fromCharCode(92) deliberately: a
  //    backslash-sensitive fixture written as a source escape is one editor away from silently
  //    becoming a real newline and testing nothing.
  report('marker', 'THE REGRESSION: a JSON-escaped real body is key material, not prose',
    classifyMarkerHit(BS + 'n' + B64_FULL), 'key-material');
  report('marker', 'JSON-escaped CRLF form too',
    classifyMarkerHit(BS + 'r' + BS + 'n' + B64_FULL), 'key-material');
  report('marker', 'a SHORT body still counts when a closing END marker proves it complete '
    + '(an EC key body is far shorter than RSA)',
    classifyMarkerHit(BS + 'n' + B64_FRAGMENT + BS + 'n-----END EC PRIVATE KEY-----'), 'key-material');

  report('marker', 'the 67-char fragment alone is NOT usable key material — this is the row that sits '
    + 'in multiterminal.db, and flagging it would make the audit permanently red',
    classifyMarkerHit(BS + 'n' + B64_FRAGMENT), 'benign');
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
  report('marker', 'normaliseEscapedNewlines collapses the two-char form and leaves real ones alone',
    [normaliseEscapedNewlines(BS + 'n' + 'x'), normaliseEscapedNewlines('\nx')], ['\nx', '\nx']);

  // --- markerTailsIn. Guards the shadowing regression that WIDENING the capture window introduced:
  //     a benign marker must never hide a real key that follows it inside the same rg match.
  const BEGIN = '-----BEGIN RSA PRIVATE KEY-----';
  report('tails', 'one marker yields one tail',
    markerTailsIn(BEGIN + '\nabc'), ['\nabc']);
  report('tails', 'THE SHADOWING REGRESSION: a benign marker does not hide a real key behind it',
    markerTailsIn(BEGIN + ' prose about keys, at length. ' + BEGIN + '\n' + B64_FULL)
      .map(classifyMarkerHit),
    ['benign', 'key-material']);
  report('tails', 'each tail stops at the next BEGIN, so one key body is never read as another\'s',
    markerTailsIn(BEGIN + '\n' + B64_FRAGMENT + BEGIN + '\nxyz'),
    ['\n' + B64_FRAGMENT, '\nxyz']);
  report('tails', 'an END marker does not open a tail of its own (it also contains "PRIVATE KEY-----")',
    markerTailsIn(BEGIN + '\nbody\n-----END RSA PRIVATE KEY-----\ntrailing').length, 1);
  report('tails', 'a CERTIFICATE block cannot claim a later private key\'s body',
    markerTailsIn('-----BEGIN CERTIFICATE-----\nzzz\n' + BEGIN + '\n' + B64_FULL)
      .map(classifyMarkerHit),
    ['key-material']);
  report('tails', 'text with no marker at all yields nothing',
    markerTailsIn('nothing to see here'), []);

  // --- verdictForMarkerHits. The half that was missing entirely until pipeline Run 1: classification
  //     is useless unless something turns classified hits into a PASS/FAIL. These assert the rule is
  //     "no key material ANYWHERE" — deliberately unlike verdictForLocations, which requires a hit in
  //     the authorized store.
  const AUTH_STORE = 'C:\\Users\\x\\AppData\\Roaming\\multiterminal\\settings.txt';
  report('markerVerdict', 'benign hits alone are a PASS — the audit must not fail on its own source',
    verdictForMarkerHits([
      { path: 'scripts/verify-key-at-rest.mjs', after: " is what a PEM file starts with" },
      { path: 'transcript.jsonl', after: "'\\n$needles += '-----BEGIN PRIVATE KEY-----'" },
    ]),
    { ok: true, keyMaterialPaths: [], benignCount: 2 });
  report('markerVerdict', 'one real key body anywhere is a FAIL',
    verdictForMarkerHits([
      { path: 'scripts/verify-key-at-rest.mjs', after: ' prose' },
      { path: 'C:\\repo\\.claude\\project.json', after: '\n' + B64_FULL },
    ]),
    { ok: false, keyMaterialPaths: ['C:\\repo\\.claude\\project.json'], benignCount: 1 });
  report('markerVerdict', 'THE difference from verdictForLocations: key material in the AUTHORIZED '
    + 'store still fails, because that store holds ciphertext',
    verdictForMarkerHits([{ path: AUTH_STORE, after: '\r\n' + B64_FULL }]),
    { ok: false, keyMaterialPaths: [AUTH_STORE], benignCount: 0 });
  report('markerVerdict', 'no hits at all is a PASS (a machine that never registered an App)',
    verdictForMarkerHits([]), { ok: true, keyMaterialPaths: [], benignCount: 0 });
  // ⭐ THE REGRESSION AT THE VERDICT LEVEL, not just the classifier level. This is the shape the sweep
  //    reported as "plaintext PEM: NONE" for four sessions: a key in a project.json, JSON-encoded.
  //    Asserting it here as well as on classifyMarkerHit is deliberate — the earlier defect was a
  //    correct classifier with no caller, so proving the CLASSIFIER works has already once been
  //    insufficient to prove the AUDIT works.
  report('markerVerdict', 'THE REGRESSION: a JSON-encoded key in a project.json is a FAIL',
    verdictForMarkerHits([{ path: 'C:\\repo\\.claude\\project.json', after: BS + 'n' + B64_FULL }]),
    { ok: false, keyMaterialPaths: ['C:\\repo\\.claude\\project.json'], benignCount: 0 });
  report('markerVerdict', 'the transcript fragment that lives in multiterminal.db stays a PASS, so the '
    + 'audit does not fail on the session that runs it',
    verdictForMarkerHits([{ path: 'multiterminal.db', after: BS + 'n' + B64_FRAGMENT }]),
    { ok: true, keyMaterialPaths: [], benignCount: 1 });
  report('markerVerdict', 'the same file twice is reported once',
    verdictForMarkerHits([
      { path: 'dup.json', after: '\n' + B64_FULL },
      { path: 'dup.json', after: '\n' + B64_FULL },
    ]),
    { ok: false, keyMaterialPaths: ['dup.json'], benignCount: 0 });

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

// Trap (3), the measurement half. Returns every PEM-marker occurrence under `roots` together with
// the bytes that FOLLOW it, so classifyMarkerHit() can tell a real key body from prose about keys.
//
// Why this is not filesContaining(): that returns filenames, and a filename cannot be classified.
// The marker's meaning is entirely in what comes next. Matched with -U (multiline) because a real PEM
// body begins on the NEXT line, so a line-scoped match would always see an empty tail and classify
// every genuine key as benign — failing open, silently.
//
// --json rather than -o alone: with -U the matched text contains newlines, so line-splitting rg's
// plain output cannot tell one match from the next.
//
// ⚠️ THE WINDOW SIZE IS LOAD-BEARING — DO NOT SHRINK IT BACK. It was 64 bytes, which is smaller than
// any real key body, so classifyMarkerHit() could only ever observe "40+ base64 chars follow" and had
// no way to distinguish a genuine key from a 67-char fragment of one. Both of its current triggers
// (a closing END marker, or a run >= KEY_MATERIAL_MIN_RUN) are unobservable inside 64 bytes. 2400 is
// sized to contain a full RSA-2048 PKCS#1 body (~1600 base64 chars plus line breaks) AND its END
// marker, so a complete key is always classifiable from one match. Cost is bounded: a few hundred KB
// of rg output across the whole sweep, against maxBuffer 1<<28.
const PEM_MARKER_TAIL = 'PRIVATE KEY-----';
const PEM_MARKER_RE = String.raw`-----BEGIN [A-Z ]{0,24}PRIVATE KEY-----[\s\S]{0,2400}`;

function markerHits(roots) {
  const existing = roots.filter(r => fs.existsSync(r));
  if (!existing.length) return [];
  let out;
  try {
    out = execFileSync(rgPath(),
      ['-a', '-uu', '-U', '-o', '--json', '-e', PEM_MARKER_RE, '--', ...existing],
      { encoding: 'utf8', maxBuffer: 1 << 28 });
  } catch (e) {
    if (e.status === 1) return [];          // rg: no matches
    throw new Error(`ripgrep failed (status ${e.status}): ${e.stderr || e.message}`);
  }

  const hits = [];
  for (const line of out.split(/\r?\n/)) {
    if (!line) continue;
    let ev;
    try { ev = JSON.parse(line); } catch { continue; }
    if (ev.type !== 'match') continue;
    const p = ev.data?.path?.text ?? '(unknown path)';
    for (const sm of ev.data?.submatches ?? []) {
      // ⚠️ rg emits `bytes` (base64) instead of `text` when the match is not valid UTF-8 — which is
      // exactly what a key inside a SQLite page or a binary blob looks like. The old code dropped
      // those submatches silently, so a hit could vanish UNEXAMINED from a census whose only job is
      // examining them. Decode and classify instead; latin1 is right because every character the
      // classifier cares about (base64, CR, LF, the END marker) is single-byte ASCII.
      let text = sm?.match?.text;
      if (typeof text !== 'string') {
        const b64 = sm?.match?.bytes;
        if (typeof b64 !== 'string') continue;
        text = Buffer.from(b64, 'base64').toString('latin1');
      }
      for (const after of markerTailsIn(text)) hits.push({ path: p, after });
    }
  }
  return hits;
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

// ---- the PLAINTEXT half. ------------------------------------------------------------------------
//
// ⚠️ THE CIPHERTEXT SWEEP ABOVE CANNOT SEE THIS, AND FOR A LONG TIME NOTHING DID. Its needles are
// prefixes of the DPAPI blob, so it finds the ENCRYPTED key copied somewhere it should not be — and
// is blind to the far worse case of a READABLE key written to a project.json, a repo working tree or
// an agent transcript, which is the literal wording of item 9's acceptance. classifyMarkerHit() and
// trap (3) were written for exactly this check, and then it was never wired up: the function's only
// callers were in selfTest(), so the audit passed its own unit tests while never performing the
// measurement the header advertised. Found by pipeline Run 1 (code-reviewer, MAJOR).
const markers = markerHits(sweepPaths);
const markerVerdict = verdictForMarkerHits(markers);
if (markerVerdict.ok) {
  console.log(`  ok   plaintext PEM: NONE — ${markers.length} marker hit(s), all classified benign`
    + ' (this audit script, fixtures and transcripts quoting the marker)');
} else {
  problems.push('plaintext PEM: LEAKED — a PEM marker is followed by real key material in:\n      '
    + markerVerdict.keyMaterialPaths.join('\n      ')
    + '\n      Note this fails even if the file IS the authorized store: that holds a DPAPI blob, so a'
    + '\n      readable key there means the protection failed, not that the key was where it belonged.');
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
