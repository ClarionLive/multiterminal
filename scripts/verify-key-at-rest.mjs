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
//      classifyMarkerHit() asks the only question that matters, and asks it by DECODING rather than by
//      inspecting shape: does the text after the marker decode to an actual private key (key material),
//      or is it prose that merely mentions one? See looksLikePrivateKeyDer().
//      ⚠️ IT USED TO SAY "prose/escapes", AND THE WORD "escapes" WAS A DEFECT, NOT A DESCRIPTION.
//      Treating an escaped newline as evidence of prose made the sweep blind to a key written into
//      JSON — `-----BEGIN … -----\nMII…` with a literal backslash-n — which is the encoding of BOTH
//      artifacts item 9's acceptance names by hand: project.json (checked in, so a leak there becomes
//      public) and agent transcripts (JSONL, which MT then imports into multiterminal.db). Pipeline
//      Run 2 found it, independently, on two different model providers (ticket 27002183).
//      ⚠️ THE FIRST TWO ATTEMPTS AT A REPLACEMENT WERE ALSO SHAPE-BASED, AND BOTH FAILED. "A run of
//      200+ chars, or a closing END marker in the window" read as semantic but was still surface
//      inspection, and it was defeated twice over: an unnormalised escape stopped the run from ever
//      starting (so BOTH branches fell together), and the run length was measured with a character class
//      that EXCLUDED newlines — which, since real PEMs wrap at 64 chars, made that branch unreachable for
//      every real key at every size. RSA-4096 was benign. Four consecutive fix rounds each produced a new
//      defect in this detector before the rule stopped guessing and started decoding.
//      ⇒ THE LESSON IS NOT "PICK A BETTER HEURISTIC". It is that the question "is this a key?" has an
//      exact answer available — base64-decode it and read the ASN.1 — and every attempt to approximate
//      that answer from the surface was wrong in a way no fixture caught. See looksLikePrivateKeyDer.
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
import { generateKeyPairSync } from 'crypto';

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
// So: normalise every escape spelling to a real line break, so that what reaches the classifier is the
// same bytes regardless of how the artifact happened to serialise them. This function's only job is to
// make encoding STOP MATTERING; the decision about whether the result is a key belongs to
// looksLikePrivateKeyDer(), which decodes it rather than inspecting its shape.
//
// ⚠️ WHY NORMALISATION MUST BE COMPLETE RATHER THAN INCREMENTAL. An unhandled escape does not merely
// weaken the classifier, it BLINDS it: the base64 run never starts, so every later test is skipped
// whatever it tests. That is how a key with a closing END marker sitting right beside it was still
// reported benign. Adding escapes one at a time as they are discovered would keep reopening the hole,
// which is why the list above is exhaustive over spellings rather than over observed incidents.
// How many DECODED bytes of a key must be visible before a truncated one counts. 200 sits comfortably
// above the 46B that the 67-char DER prefix in this file's own fixtures decodes to, and comfortably
// below any real key. See looksLikePrivateKeyDer for why a valid header alone is not enough.
export const KEY_MATERIAL_MIN_DECODED_BYTES = 200;

// JSON, JSONL and most source literals carry a line break as something other than a raw byte.
// Collapse every spelling to the real thing so one rule covers every encoding a key can be written in.
//
// ⚠️ THE SHORT VERSION OF THIS FUNCTION WAS ITSELF A FINDING. It handled only `\n`, `\r` and `\r\n`,
// which left FIVE working encodings classified benign — found by the codex security gate on the very
// commit that introduced this function, and then measured. Written descriptively below rather than
// literally, because a literal escape sequence in this comment is itself processed by tooling on the
// way into the file — which happened while writing it, splitting these very lines:
//     backslash-u-000a            valid JSON; JSON.parse turns it into a real newline
//     backslash-u-000d + -000a    the CRLF spelling of the same
//     backslash-u-000A            the same again with uppercase hex, which JSON also accepts
//     backslash-backslash-n       DOUBLE-escaped — how a key looks inside NESTED JSON, i.e. a JSON
//                                 payload quoted inside an agent transcript, which is a swept root
//     percent-30-A                a key pasted into a URL or an HTTP log line
//     any of the above WITH a closing -----END marker
//
// ⭐ THAT LAST CASE IS WHY THIS MUST BE COMPLETE RATHER THAN INCREMENTAL. The discriminator has two
// branches (a complete key, or enough decoded bytes of one) and an unnormalised escape defeats BOTH
// AT ONCE: the base64 run never starts, the regex fails before `hasEnd` is ever consulted, and the
// END marker sitting right there rescues nothing. Normalisation is not one input to the rule; it is
// the precondition for either branch meaning anything. Adding escapes one at a time as they are
// discovered would keep re-opening the same hole.
//
// Backslash RUNS are matched (`\\+`) rather than a single backslash, because each level of JSON
// nesting doubles them. Percent-encoding is handled too: a key pasted inside a URL or an HTTP log
// line is a plausible transcript shape, and over-matching here can only make the audit MORE likely
// to flag — the safe direction for a detector whose failure mode is silence.
export function normaliseEscapedNewlines(s) {
  return s
    .replace(/\\+u000[aA]/g, '\n')
    .replace(/\\+u000[dD]/g, '\r')
    .replace(/\\+n/g, '\n')
    .replace(/\\+r/g, '\r')
    .replace(/%0[aA]/g, '\n')
    .replace(/%0[dD]/g, '\r');
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

// RFC 1421 armor headers sit BETWEEN the BEGIN line and the base64 body, ended by a blank line:
//     -----BEGIN RSA PRIVATE KEY-----
//     Proc-Type: 4,ENCRYPTED
//     DEK-Info: AES-128-CBC,1B2C3D...
//
//     MIIEpAIBAAKC...
// ⚠️ Without this, such a key classifies BENIGN even with a full body and an END marker in the same
// window, because the matcher saw `Proc-Type` where it wanted base64. An encrypted PEM is still a
// private key sitting in an agent-reachable artifact — the passphrase is a separate secret, not a
// reason for the audit to stay silent. Found by the cross-model adversary gate, which reproduced it.
// The `Key: value` shape is matched generally rather than by a list of known header names: over-
// matching can only make the audit MORE likely to flag, and a detector whose failure mode is silence
// should err that way.
function stripPemArmorHeaders(text) {
  const m = /^[\r\n \t]*(?:[A-Za-z][A-Za-z0-9-]*:[^\r\n]*(?:\r?\n)+)+/.exec(text);
  return m ? text.slice(m[0].length) : text;
}

// Smallest and largest plausible DER-encoded private key, in DECODED bytes. An EC P-256 key is ~120B;
// an RSA-4096 PKCS#8 is ~2370B. The upper bound exists only to reject absurd declared lengths from
// random bytes that happen to begin 0x30 0x82.
const KEY_DER_MIN_BYTES = 64;
const KEY_DER_MAX_BYTES = 20000;

/**
 * Does this base64 run DECODE to something that IS a private key, rather than merely looking like one?
 *
 * ⭐ THIS REPLACED TWO HEURISTICS — a 200-char run threshold, or a closing END marker inside the
 * capture window — WHICH BETWEEN THEM PRODUCED A NEW DEFECT IN EACH OF FOUR CONSECUTIVE FIX ROUNDS.
 * The last one was decisive: the run threshold measured a character class that EXCLUDED newlines, and a
 * real PEM wraps its body at 64 chars, so the threshold could never fire on ANY real key at ANY size
 * and detection silently rested on the END marker alone. Measured: RSA-2048 caught, RSA-3072 on the
 * cliff, RSA-4096 BENIGN. Found by the debugger gate.
 *
 * The heuristics were guessing at "is this a key?" from surface shape. This decodes it and asks. That is
 * why it is not simply a fifth heuristic:
 *   - ENCODING-INDEPENDENT: escapes are normalised and whitespace stripped first, so wrapped, unwrapped,
 *     JSON-escaped and percent-encoded bodies all reduce to the same bytes.
 *   - WINDOW-INDEPENDENT FOR DETECTION: a key truncated by the capture window still presents a valid
 *     SEQUENCE header with a key-sized declared length, which is enough. MEASURED: a real RSA-4096 body
 *     truncated at 2400 bytes is caught (declares 2370B, 1768B visible).
 *   - TWO-SIDED BY CONSTRUCTION: a fragment fails because its declared length is unmet and too little
 *     follows; a complete key passes because it is met. The old rules could only say "long" or "short".
 *
 * ⚠️ `complete || substantial`, NOT `complete` alone — requiring completeness would reintroduce window
 * dependence by the back door, and a key truncated by the window is still a key in the file.
 * ⚠️ And `substantial` is what keeps the 67-char DER PREFIX benign. That prefix decodes to a PERFECTLY
 * VALID SEQUENCE header declaring 1188 bytes, because it is the genuine opening of an RSA key. Header
 * validity alone is therefore NOT sufficient to call something key material — which is the subtlety
 * that makes this function longer than it first appears to need to be.
 */
export function looksLikePrivateKeyDer(b64) {
  let buf;
  try { buf = Buffer.from(b64, 'base64'); } catch { return false; }
  if (buf.length < 8) return false;
  if (buf[0] !== 0x30) return false;                  // ASN.1 SEQUENCE — every PKCS#1/#8/SEC1 key
  let declared;
  let header;
  if (buf[1] === 0x82) { declared = (buf[2] << 8) | buf[3]; header = 4; }
  else if (buf[1] === 0x81) { declared = buf[2]; header = 3; }
  else if (buf[1] < 0x80) { declared = buf[1]; header = 2; }
  else return false;                                  // 3+ byte length forms: not a key of any size
  if (declared < KEY_DER_MIN_BYTES || declared > KEY_DER_MAX_BYTES) return false;

  const payload = buf.length - header;
  return payload >= declared || payload >= KEY_MATERIAL_MIN_DECODED_BYTES;
}

export function classifyMarkerHit(textAfterMarker) {
  const text = normaliseEscapedNewlines(textAfterMarker);
  // ⚠️ THE CAPTURE MUST CROSS LINE BREAKS. A real PEM wraps its body at 64 characters, so a
  // newline-EXCLUDING class only ever sees the first line — which is how the previous 200-char run
  // threshold came to be unreachable for every real key at every size. Whitespace is stripped before
  // decoding, so wrapped and unwrapped bodies reduce to the same bytes.
  const m = /^[\r\n \t]*((?:[A-Za-z0-9+/=]+[\r\n \t]*)+)/.exec(stripPemArmorHeaders(text));
  if (!m) return 'benign';

  const b64 = m[1].replace(/\s+/g, '');
  if (b64.length < 40) return 'benign';
  return looksLikePrivateKeyDer(b64) ? 'key-material' : 'benign';
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
  const BS = String.fromCharCode(92);   // one literal backslash — never written as an escape here

  // ⭐⭐ THE FIXTURE IS A REAL KEY, GENERATED HERE, AND THAT IS THE WHOLE POINT OF THIS SECTION.
  // The previous fixture was B64_FRAGMENT repeated four times: 268 CONTIGUOUS base64 chars. No PEM
  // writer emits that — real bodies wrap at 64 — so it certified a code path that could never fire in
  // the field, and the run-length trigger it blessed turned out to be unreachable for EVERY real key at
  // EVERY size. The debugger gate found that by generating actual keys; this fixture exists so the same
  // mistake cannot be made again. THE THIRD TIME on this ticket that a fixture certified an assumption
  // instead of testing it — see trap (3).
  // Generated in-process and never written anywhere, so no key-shaped literal enters this source file
  // (which is itself a swept root, and whose old fixture is the reason multiterminal.db has a hit).
  // RSA-2048 only: keygen is ~100 ms, while RSA-4096 is seconds. The 4096 case — a body TRUNCATED by
  // the 2400-byte capture window — is covered below by truncating this key instead, which exercises the
  // identical `substantial` branch. RSA-4096 was measured by hand at 2370B declared / 1768B visible.
  const realPem = (type) => {
    const { privateKey } = generateKeyPairSync('rsa', {
      modulusLength: 2048,
      publicKeyEncoding: { type: 'spki', format: 'pem' },
      privateKeyEncoding: { type, format: 'pem' },
    });
    const at = privateKey.indexOf(PEM_MARKER_TAIL);
    return privateKey.slice(at + PEM_MARKER_TAIL.length);   // the tail: wrapped body + END marker
  };
  const realEcPem = () => {
    const { privateKey } = generateKeyPairSync('ec', {
      namedCurve: 'prime256v1',
      publicKeyEncoding: { type: 'spki', format: 'pem' },
      privateKeyEncoding: { type: 'sec1', format: 'pem' },
    });
    const at = privateKey.indexOf(PEM_MARKER_TAIL);
    return privateKey.slice(at + PEM_MARKER_TAIL.length);
  };
  const REAL_PKCS1 = realPem('pkcs1');
  const REAL_PKCS8 = realPem('pkcs8');
  // As the sweep would see it when the key is larger than the window: body cut, END never reached.
  const REAL_TRUNCATED = REAL_PKCS1.slice(0, 900);
  // Re-encode a real key the way a JSON artifact carries it.
  const esc = (s, seq) => s.replace(/\r?\n/g, seq);

  report('marker', 'a real PEM body (newline then a long base64 run) is key material',
    classifyMarkerHit('\n' + REAL_PKCS1), 'key-material');
  report('marker', 'CRLF form is key material too',
    classifyMarkerHit('\r\n' + REAL_PKCS1), 'key-material');

  // ⭐ THE REGRESSION. Its absence is why this self-test passed 23/23 while the sweep was blind to the
  //    one encoding that matters. A key written into JSON carries a LITERAL backslash-n, and JSON is
  //    what project.json and every agent transcript are. Built with fromCharCode(92) deliberately: a
  //    backslash-sensitive fixture written as a source escape is one editor away from silently
  //    becoming a real newline and testing nothing.
  report('marker', 'THE REGRESSION: a JSON-escaped real body is key material, not prose',
    classifyMarkerHit(esc(REAL_PKCS1, BS + 'n')), 'key-material');
  report('marker', 'JSON-escaped CRLF form too',
    classifyMarkerHit(BS + 'r' + esc(REAL_PKCS1, BS + 'n')), 'key-material');
  // A real EC key body is ~10x shorter than RSA, so this is the fixture that stops the rule being
  // accidentally RSA-shaped. It passes because it DECODES COMPLETELY, not because it is long — which is
  // the difference between the current rule and the length threshold it replaced.
  // ⚠️ THIS FIXTURE USED TO READ "a SHORT body still counts when a closing END marker proves it
  // complete", asserting B64_FRAGMENT plus an END marker. That is now correctly BENIGN: an END marker
  // after a 46-byte fragment proves nothing, and treating it as proof was one of the two disjoint
  // triggers that let RSA-4096 through. The intent was right and the mechanism was wrong.
  report('marker', 'a real EC key is key material despite a body an order of magnitude shorter than RSA',
    classifyMarkerHit(realEcPem()), 'key-material');
  report('marker', 'a 46-byte fragment followed by an END marker is NOT proof of a key',
    classifyMarkerHit(BS + 'n' + B64_FRAGMENT + BS + 'n-----END EC PRIVATE KEY-----'), 'benign');
  report('marker', 'PKCS#8 is key material too — the rule must not be PKCS#1-shaped',
    classifyMarkerHit(REAL_PKCS8), 'key-material');

  // ⭐ THE CASE THE PREVIOUS RULE COULD NOT SEE AT ALL. A key larger than the capture window arrives
  //    with its body cut and its END marker never reached — which is exactly RSA-4096 (measured by hand:
  //    declares 2370B, only 1768B visible). The old rule had two triggers and this shape defeated both:
  //    no END in the window, and a newline-wrapped body so the run threshold saw only 64 chars.
  //    It passes now because a valid SEQUENCE header plus enough payload is sufficient — which is what
  //    makes detection window-INDEPENDENT rather than merely window-widened.
  report('marker', 'a key TRUNCATED by the capture window is still key material',
    classifyMarkerHit(REAL_TRUNCATED), 'key-material');
  report('marker', 'and the truncated form survives JSON escaping too',
    classifyMarkerHit(esc(REAL_TRUNCATED, BS + 'n')), 'key-material');

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

  // ⭐ EVERY SERIALIZATION LAYER A LEAKED KEY CAN ARRIVE IN. Found by all THREE review gates
  //    independently (codex security auditor, claude code reviewer, codex cross-model adversary) on the
  //    commit that added single-escape handling — the first fix covered one layer and left five working
  //    encodings benign. Their absence here is why the previous self-test passed while the detector was
  //    still blind, which is the same failure this whole file is about.
  //    ⚠️ The last one is the sharpest: an escaped body WITH a closing END marker was still benign,
  //    because the base64 run never starts, so the regex fails before `hasEnd` is ever consulted.
  //    Normalisation is a PRECONDITION for both branches of the rule, not one input among them.
  for (const [label, prefix] of [
    ['JSON unicode escape', BS + 'u000a'],
    ['JSON unicode CRLF', BS + 'u000d' + BS + 'u000a'],
    ['JSON unicode, uppercase hex (also valid JSON)', BS + 'u000A'],
    ['double-escaped, i.e. JSON nested inside JSON', BS + BS + 'n'],
    ['percent-encoded, i.e. a key pasted into a URL or an HTTP log', '%0A'],
  ]) {
    report('marker', `a real body behind a ${label} is key material`,
      classifyMarkerHit(esc(REAL_PKCS1, prefix)), 'key-material');
  }
  report('marker', 'THE ONE THE END-MARKER BRANCH COULD NOT RESCUE: escaped body plus a closing END',
    classifyMarkerHit(BS + 'u000a' + REAL_PKCS1 + BS + 'u000a-----END RSA PRIVATE KEY-----'),
    'key-material');

  // ⭐ RFC 1421 ARMOR HEADERS. An encrypted PEM puts Proc-Type/DEK-Info between the marker and the
  //    body, so the matcher saw a header where it wanted base64 and called a complete key benign.
  //    Found by the cross-model adversary gate. The passphrase is a separate secret; a key file in a
  //    swept root is still a finding.
  report('marker', 'an encrypted PEM with Proc-Type/DEK-Info armor headers is still key material',
    classifyMarkerHit('\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,1B2C3D4E\n\n' + REAL_PKCS1),
    'key-material');
  report('marker', 'armor headers in the JSON-escaped form too',
    classifyMarkerHit(BS + 'nProc-Type: 4,ENCRYPTED' + BS + 'n' + esc(REAL_PKCS1, BS + 'n')),
    'key-material');
  report('marker', 'a header-shaped line with NO body behind it is still benign',
    classifyMarkerHit('\nProc-Type: 4,ENCRYPTED\n\nnot a key at all'), 'benign');

  // --- markerTailsIn. Guards the shadowing regression that WIDENING the capture window introduced:
  //     a benign marker must never hide a real key that follows it inside the same rg match.
  const BEGIN = '-----BEGIN RSA PRIVATE KEY-----';
  report('tails', 'one marker yields one tail',
    markerTailsIn(BEGIN + '\nabc'), ['\nabc']);
  report('tails', 'THE SHADOWING REGRESSION: a benign marker does not hide a real key behind it',
    markerTailsIn(BEGIN + ' prose about keys, at length. ' + BEGIN + '\n' + REAL_PKCS1)
      .map(classifyMarkerHit),
    ['benign', 'key-material']);
  report('tails', 'each tail stops at the next BEGIN, so one key body is never read as another\'s',
    markerTailsIn(BEGIN + '\n' + B64_FRAGMENT + BEGIN + '\nxyz'),
    ['\n' + B64_FRAGMENT, '\nxyz']);
  report('tails', 'an END marker does not open a tail of its own (it also contains "PRIVATE KEY-----")',
    markerTailsIn(BEGIN + '\nbody\n-----END RSA PRIVATE KEY-----\ntrailing').length, 1);
  report('tails', 'a CERTIFICATE block cannot claim a later private key\'s body',
    markerTailsIn('-----BEGIN CERTIFICATE-----\nzzz\n' + BEGIN + '\n' + REAL_PKCS1)
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
      { path: 'C:\\repo\\.claude\\project.json', after: '\n' + REAL_PKCS1 },
    ]),
    { ok: false, keyMaterialPaths: ['C:\\repo\\.claude\\project.json'], benignCount: 1 });
  report('markerVerdict', 'THE difference from verdictForLocations: key material in the AUTHORIZED '
    + 'store still fails, because that store holds ciphertext',
    verdictForMarkerHits([{ path: AUTH_STORE, after: '\r\n' + REAL_PKCS1 }]),
    { ok: false, keyMaterialPaths: [AUTH_STORE], benignCount: 0 });
  report('markerVerdict', 'no hits at all is a PASS (a machine that never registered an App)',
    verdictForMarkerHits([]), { ok: true, keyMaterialPaths: [], benignCount: 0 });
  // ⭐ THE REGRESSION AT THE VERDICT LEVEL, not just the classifier level. This is the shape the sweep
  //    reported as "plaintext PEM: NONE" for four sessions: a key in a project.json, JSON-encoded.
  //    Asserting it here as well as on classifyMarkerHit is deliberate — the earlier defect was a
  //    correct classifier with no caller, so proving the CLASSIFIER works has already once been
  //    insufficient to prove the AUDIT works.
  report('markerVerdict', 'THE REGRESSION: a JSON-encoded key in a project.json is a FAIL',
    verdictForMarkerHits([{ path: 'C:\\repo\\.claude\\project.json', after: esc(REAL_PKCS1, BS + 'n') }]),
    { ok: false, keyMaterialPaths: ['C:\\repo\\.claude\\project.json'], benignCount: 0 });
  report('markerVerdict', 'the transcript fragment that lives in multiterminal.db stays a PASS, so the '
    + 'audit does not fail on the session that runs it',
    verdictForMarkerHits([{ path: 'multiterminal.db', after: BS + 'n' + B64_FRAGMENT }]),
    { ok: true, keyMaterialPaths: [], benignCount: 1 });
  report('markerVerdict', 'the same file twice is reported once',
    verdictForMarkerHits([
      { path: 'dup.json', after: '\n' + REAL_PKCS1 },
      { path: 'dup.json', after: '\n' + REAL_PKCS1 },
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
// (a decodable key header plus enough payload) are unobservable inside 64 bytes. 2400 is
// sized to contain a full RSA-2048 PKCS#1 body (~1600 base64 chars plus line breaks) AND its END
// marker, so a complete key is always classifiable from one match. Cost is bounded: a few hundred KB
// of rg output across the whole sweep, against maxBuffer 1<<28.
const PEM_MARKER_TAIL = 'PRIVATE KEY-----';
const PEM_MARKER_RE = String.raw`-----BEGIN [A-Z ]{0,24}PRIVATE KEY-----[\s\S]{0,2400}`;

// How many PEM markers exist under `roots`, counted WITHOUT a trailing window.
//
// ⚠️ WHY A SECOND PASS EXISTS AT ALL, AND WHY IT IS A COVERAGE CHECK RATHER THAN A TIDY-UP.
// PEM_MARKER_RE carries a greedy 2400-byte tail and rg matches are NON-OVERLAPPING, so a match can
// absorb the leading bytes of the NEXT marker and rg then resumes scanning past it — that marker never
// matches again, and markerTailsIn cannot recover it either because its own tail lies outside the
// window. Measured by the debugger gate: a benign marker followed by 2360 bytes of filler and then a
// full RSA-2048 key produced exactly ONE hit, classified benign; the real key produced NO HIT AT ALL.
// Widening the window from 64 to 2400 bytes made that far likelier, since one match now spans 2400
// bytes of transcript.
// ⇒ This is trap (5) in miniature — auditing less than claimed while still printing PASS — so the
//   answer is the same as trap (5)'s: make the shortfall a hard FAILURE rather than a silent skip.
//   A marker the sweep never examined is indistinguishable from a marker it cleared, and only one of
//   those is safe to report as NONE.
// Making the quantifier lazy does NOT fix it: a lazy `{0,2400}` matches zero characters.
//
// ⚠️ MEASURED LIMIT OF THIS CHECK, recorded because an unstated limit is how the defects above survived.
// It compares TOTALS, not identities, so it detects a NET shortfall. Measured on this machine:
//     standalone markers 262 | examined at window 2400: 270 | at 800: 259 | at 200: 262 | at 64: 252
// The check is satisfied at the shipped 2400 and FIRES at 800 and 64, so it is falsifiable rather than
// decorative. But note the surplus at 2400: markerTailsIn can emit a tail for a marker that appears as
// CONTENT inside another match's window, so counts can exceed the standalone total. That makes the check
// one-directional and safe — it never false-alarms — but it also means N duplicate emissions could in
// principle mask N genuine straddles and net to zero. Comparing byte OFFSETS rather than counts would
// close that; it is not done here because the shortfall case is the one that loses a key, and a surplus
// only inflates a number that is already labelled "examined".
function countMarkers(roots) {
  const existing = roots.filter(r => fs.existsSync(r));
  if (!existing.length) return 0;
  try {
    const out = execFileSync(rgPath(),
      ['-a', '-uu', '-o', '--count-matches', '--no-filename',
       '-e', String.raw`-----BEGIN [A-Z ]{0,24}PRIVATE KEY-----`, '--', ...existing],
      { encoding: 'utf8', maxBuffer: 1 << 28 });
    return out.split(/\r?\n/).reduce((n, line) => n + (parseInt(line, 10) || 0), 0);
  } catch (e) {
    if (e.status === 1) return 0;            // rg: no matches
    throw new Error(`ripgrep failed counting markers (status ${e.status}): ${e.stderr || e.message}`);
  }
}

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

// COVERAGE CHECK, and it is not decoration: every marker that exists must have been EXAMINED. See
// countMarkers() for the mechanism by which one can go missing. An unexamined marker is
// indistinguishable in the output from a cleared one, and only one of those justifies printing NONE.
const markerTotal = countMarkers(sweepPaths);
if (markerTotal > markers.length) {
  problems.push(`plaintext PEM: COVERAGE SHORTFALL — ${markerTotal} PEM marker(s) exist under the sweep`
    + `\n      roots but only ${markers.length} were classified. The missing ${markerTotal - markers.length}`
    + ' straddled a capture-window boundary, so'
    + '\n      rg never re-matched them and their contents were never examined. This is reported as a'
    + '\n      FAILURE rather than a note because an unexamined marker reads exactly like a cleared one,'
    + '\n      and "plaintext PEM: NONE" would then be a claim about coverage this run did not have.'
    + '\n      Fix by widening PEM_MARKER_RE or by matching markers and tails in separate passes.');
}

if (markerVerdict.ok) {
  console.log(`  ok   plaintext PEM: NONE — ${markerVerdict.benignCount} marker hit(s) examined, all`
    + ' classified benign (this audit script, fixtures and transcripts quoting the marker)');
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
