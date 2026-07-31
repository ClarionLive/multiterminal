#!/usr/bin/env node
// verify-no-secret-serialization.mjs — falsifiable guard that MultiTerminal's HTTP surface never
// hands out a stored credential (task ea7d9cf9).
//
// WHY THIS EXISTS. Three endpoints used to serialize a raw source-control PAT into their response
// body: GET /api/source-accounts/{id}/token, GET /api/projects/{projectId}/source-account, and
// GET /api/owner-profile/github-token. They were added speculatively by f1e586b "for push" and NO
// consumer was ever written — MultiTerminal performs no authenticated git or GitHub write anywhere,
// and the one legitimate reader (SourceControlValidator, behind the account editor's "Test" button)
// calls SourceControlAccountService.GetToken IN-PROCESS, not over HTTP.
//
// The protections in front of them did not cover the actual threat. The loopback gate refuses remote
// callers but cannot distinguish MT's own agents from arbitrary local code, and the CORS allowlist
// (f9697aac) constrains browsers only — a plain HttpClient is subject to neither. So any local
// process running as the user could read every stored PAT, and every fetch wrote the secret into an
// agent's session JSONL on disk, which is then imported and indexed. That is not hypothetical: it is
// how a live ClarionLive token leaked and had to be rotated.
//
// ea7d9cf9 therefore REMOVED the dispensing rather than authenticating it — there was nothing to
// authenticate for. This script is what stops it coming back. The credential getters still exist
// (the Test button needs them), so nothing but a census prevents a future edit from serializing one
// again — which is exactly how it shipped the first time. Same "enumerate, don't prose" dialect as
// verify-writegate.mjs / verify-taskdb-gate.mjs / verify-logging.mjs.
//
// THREE falsifiable checks (each has --self-test negative fixtures):
//
//   (1) NO CREDENTIAL GETTER IN THE API LAYER — no file under API/ may call a credential getter
//       (`.GetToken(`, `.GetGitHubToken(`). There is no legitimate reason for the HTTP layer to hold
//       a plaintext secret: a caller that needs authenticated work done should have the SERVICE do
//       it. This is the broad check — it fails long before the value reaches a response object.
//
//   (2) NO SECRET-NAMED RESPONSE MEMBER — no anonymous-object member named token/secret/password/
//       pat/apiKey/accessToken/privateKey may reach an HTTP response in an API/ file. Check (1)
//       catches the value arriving from a known getter; this catches it arriving from ANYWHERE else
//       (a field, a parameter, a future service with a differently-named accessor). Three member
//       forms are covered, because C# has three ways to put a secret in a response object:
//         (a) assignment      `token = _accountService.GetToken(account.Id)`  — one shape that shipped
//         (b) SHORTHAND       `return Ok(new { token });`                     — the OTHER shape that
//             shipped, on the deleted GET /api/source-accounts/{id}/token. C# projects the member
//             name from the identifier, so there is no `=` anywhere and an assignment-only regex is
//             blind to it. `new { account.Token }` is the same hazard via a dotted path.
//         (c) direct return   `return Ok(token);`                             — no object at all.
//       Form (b) is not hypothetical and is why this check is not merely an `=` scan: the census's
//       own falsification pass (task ea7d9cf9, item ③) restored the real deleted endpoint and found
//       that only check (1) fired. Combined with a non-getter source — `var token = _someCache[id];
//       return Ok(new { token });` — BOTH checks would have passed a genuine leak.
//
//   (3) CREDENTIAL-BEARING MODELS STAY SECRET-FREE — Models/SourceControlAccount.cs and
//       Models/OwnerProfile.cs may expose only PRESENCE booleans (HasToken / HasGitHubToken), never a
//       raw secret property. This is load-bearing and non-obvious: GET /api/source-accounts returns
//       whole SourceControlAccount objects, so adding a `public string Token` to the model would
//       start leaking through an endpoint nobody edited, past checks (1) and (2) alike.
//
// KNOWN LIMITATION — READ BEFORE TRUSTING A GREEN RUN. Scope is the API/ tree plus the two named
// models. A green run means "the HTTP layer does not read or serialize a known credential, and the
// two credential-bearing models expose only booleans". It does NOT prove no secret can reach a
// response by some other route — e.g. a Services/ type that serializes itself, a secret embedded in
// an error message or log line, or a credential under a name this script does not know. Check (2)'s
// name list is a DENYLIST, and a denylist is only as good as its entries: a member named
// `credentials` or `ghp`, or a typed DTO whose property carries the secret under an unlisted name,
// would pass. Do not read PASS as "no secret can ever escape".
//
// The review pipeline for ea7d9cf9 proved this is not a theoretical caveat. It found FOUR fail-open
// paths in the first cut of this script, every one of which let a REAL leak through a green run:
// a getter split across lines (check 1 matched per line), an `@$"` string that silently masked the
// rest of the file, any responder other than Ok()/Results.* (Json, StatusCode, BadRequest…), and a
// dictionary key. Each is now covered and fixtured. The correct inference is NOT "it is airtight
// now" — it is that a name/shape-matching census needs adversarial probing to stay honest, so
// probe it again whenever a new credential or response shape appears.
//
// Usage:
//   node scripts/verify-no-secret-serialization.mjs             # exit 1 on any violation
//   node scripts/verify-no-secret-serialization.mjs --self-test # prove the checks falsify
//
// Need authenticated work done from an endpoint? Have the SERVICE perform the operation and return
// its RESULT. Do not return the credential. Exemptions live in ALLOWED_* below — a NAMED, reviewable
// edit to THIS file, never an in-code sentinel a controller can quietly apply to itself.

import fs from 'fs';
import path from 'path';

const doSelfTest = process.argv.slice(2).includes('--self-test');

const REPO_ROOT = path.join(
  path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');

// Credential accessors. A call through a receiver (`_accountService.GetToken(`) — the bare method
// DECLARATION in the service itself has no receiver and is not matched, which is why the services
// that legitimately own these are not flagged by scoping alone.
// Matched over the WHOLE masked source, not line by line: `\s*` spans newlines, so the fluent form
//     _accountService
//         .GetToken(id)
// is caught. Line-anchored matching missed it, and `API/` already contains 46 lines starting with
// `.`, so that is house style rather than an exotic shape. Found by the ea7d9cf9 review pipeline —
// with a non-secret member name (`value = ...`) check (2) stays silent too, so ONE NEWLINE used to
// take the whole census green over a real leak.
const CRED_GETTER_RE = /\b[A-Za-z_]\w*\s*\??\s*\.\s*(GetToken|GetGitHubToken)\s*\(/g;

// Secret-named members, matched ONLY inside the argument list of an HTTP response call (see
// findSecretMembers). Scoping to the response call is what makes this precise rather than noisy: an
// earlier draft anchored to start-of-line and flagged three innocent sites in the first real run —
// two `out`-parameter assignments (`apiKey = configured;` in GatewayServiceEndpoints.TryGetApiKey)
// and a typed config initializer written to a local FILE (`PrivateKey = keys.PrivateKey` in
// PushNotificationService, where persisting the VAPID key is required or every phone re-subscribes).
// None of those reach a response body. All three are now negative-control fixtures in the self-test.
const SECRET_MEMBER_RE =
  /\b(token|secret|password|pat|apiKey|accessToken|privateKey|clientSecret)\s*=(?!=)/gi;

// The same names as bare words, for the shorthand and direct-return forms (2b/2c), which have no
// `=` for SECRET_MEMBER_RE to anchor on. Lowercased; membership is tested case-insensitively.
const SECRET_NAMES = new Set(['token', 'secret', 'password', 'pat', 'apikey', 'accesstoken',
  'privatekey', 'clientsecret']);

// An expression that is JUST an identifier or a dotted/null-conditional path — i.e. something C#
// can project into a shorthand member name. `new { token }` yields a member named `token`;
// `new { account.Token }` and `new { account?.Token }` both yield `Token`. Anything with a call,
// operator, or literal in it is NOT shorthand (`valid = Validate(token)` has an `=` and is form 2a;
// a bare `Validate(token)` cannot be an anonymous-object member at all — C# requires a name).
const MEMBER_PATH_RE = /^[A-Za-z_]\w*(?:\s*\??\.\s*[A-Za-z_]\w*)*$/;

// `cts.Token` is a CancellationToken read, not a credential. Without this, the idiomatic ASP.NET
// overload `await context.Response.WriteAsJsonAsync(payload, cts.Token)` was FLAGGED — a false
// positive in a BUILD-FAILING census, which is worse than a gap because it blocks legitimate work.
// Deliberately narrow: it keys on the RECEIVER looking like a cancellation source, so `account.Token`
// (a genuine leak shape) still flags. Found by the ea7d9cf9 review pipeline; the fixture that was
// supposed to cover this passed vacuously because its call was not a response call at all.
const CANCELLATION_SOURCE_PATH_RE =
  /(?:^|\.)\s*(?:\w*[Cc]ts|\w*[Tt]okenSource|\w*[Cc]ancellation\w*)\s*\??\.\s*Token$/;

// Secret member name inside a dictionary-style initializer, where the name lives in a STRING
// literal — `Ok(new Dictionary<string,string> { ["token"] = pat })` serializes as {"token":"…"}.
// Scanned against the strings-PRESERVING masking, since default masking blanks exactly the text
// that carries the name. Flagged by two independent gates in the ea7d9cf9 review pipeline.
const DICT_KEY_SECRET_RE =
  /\[\s*"(token|secret|password|pat|apiKey|accessToken|privateKey|clientSecret)"\s*\]\s*=(?!=)/gi;

// Object-initializer bodies: `new {`, `new Foo {`, `new Dictionary<string, object> {`, and
// `new Foo(args) {`. Stops at the opening brace; the caller takes the balanced body from there.
const NEW_INITIALIZER_RE = /\bnew\b[^{};]*\{/g;

// HTTP response idioms used across this codebase's controllers (`return Ok(new { ... })`) and the
// gateway's minimal-API handlers (`Results.Ok(...)`, `Results.Json(...)`).
// Every ControllerBase responder that SERIALIZES its argument, not just the ones API/ happens to
// use today (it is `return Ok(` at 207 sites). Deriving the list from current usage was a mistake:
// `return Json(new { token });` and `return StatusCode(200, new { token });` are ordinary and
// serialize identically, and REINTRODUCTION — by definition code that does not exist yet — is the
// whole threat model. Found by the ea7d9cf9 review pipeline.
const RESPONSE_CALL_RE =
  /\b(?:Results\s*\.\s*(?:Ok|Json|Content|Text|Created|Accepted)|Ok|Created|CreatedAtAction|CreatedAtRoute|Accepted|AcceptedAtAction|AcceptedAtRoute|Json|Content|BadRequest|NotFound|Conflict|UnprocessableEntity|UnauthorizedObjectResult|StatusCode|WriteAsJsonAsync|new\s+JsonResult|new\s+ObjectResult|new\s+ContentResult)\s*\(/g;

// Property declarations that would put a raw secret on a serialized model. `HasToken` is fine —
// the boundary is a `string`-typed secret, not the presence flag.
// Allows the modifier and nullability forms C# actually uses — `public string? Token`,
// `public required string Token`, and a bare field — all of which serialize identically and all of
// which the original `public\s+string\s+` form missed.
const SECRET_PROPERTY_RE =
  /\bpublic\s+(?:required\s+|virtual\s+|override\s+|readonly\s+|init\s+)*string\s*\??\s+(Token|Password|Secret|Pat|ApiKey|AccessToken|PrivateKey|ClientSecret)\b/i;

const API_DIR = 'API';
const GUARDED_MODELS = ['Models/SourceControlAccount.cs', 'Models/OwnerProfile.cs'];

// ── NAMED EXEMPTIONS ──────────────────────────────────────────────────────────────────────────────
// Empty by design: after ea7d9cf9 the API layer has ZERO credential-getter call sites, so there is
// nothing to waive. An entry here means "this HTTP-layer code legitimately touches a plaintext
// credential", which should be approximately never — prefer moving the operation into the service.
// Keys are `relPath::identifier`; every entry must state why.
const ALLOWED_CRED_GETTERS = new Map();
const ALLOWED_SECRET_MEMBERS = new Map();

// Build artifacts and nested worktrees. NOTE: MultiTerminal.Tests is already out of scope because
// scanFiles only walks API/ — the entry below is belt-and-braces for a future widening of the scan
// root, not the thing currently excluding the test project. It would matter then: the ea7d9cf9
// regression tests deliberately name the forbidden shapes in order to assert their ABSENCE, so
// scanning them would make the census fail on its own proof.
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.claude', 'staged', 'Deploy',
  'packages', 'TestResults', '.vs', 'MultiTerminal.Tests']);

// Blank the CONTENTS of comments and string/char literals (preserving length and newlines) so
// detection sees CODE only. Same masker as verify-writegate.mjs / verify-taskdb-gate.mjs. This is
// load-bearing here, not decorative: both controllers now carry long remarks EXPLAINING the removed
// token endpoints, and a naive grep would flag the very comments documenting the fix.
// `keepStrings` preserves string CONTENTS while still blanking comments. Needed by the
// dictionary-key check, whose member name lives INSIDE a literal (`["token"] = pat`) and is
// therefore invisible to the default masking. Length and newlines are preserved in both modes, so
// offsets computed on one masking are valid in the other.
function maskCodeOnly(src, keepStrings = false) {
  let out = '';
  let state = 'code'; // code | line | block | str | verq | chr
  const lit = c => (keepStrings ? c : (c === '\n' ? '\n' : c === '\r' ? '\r' : ' '));
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    const c2 = i + 1 < src.length ? src[i + 1] : '';
    if (state === 'code') {
      if (c === '/' && c2 === '/') { state = 'line'; out += '  '; i++; continue; }
      if (c === '/' && c2 === '*') { state = 'block'; out += '  '; i++; continue; }
      // Verbatim-interpolated `@$"` (legal since C# 8) must enter the VERBATIM state. Missing it
      // sent `@$"C:\temp\"` into the escaped-string state, where the trailing `\"` reads as an
      // escape, the literal never closes, and EVERY subsequent line masks to spaces — silently
      // blinding all three checks for the rest of the file. `$@"` already worked because `@"` is
      // still adjacent. Found by the ea7d9cf9 review pipeline.
      if (c === '@' && c2 === '$' && src[i + 2] === '"') {
        state = 'verq'; out += keepStrings ? '@$"' : '   '; i += 2; continue;
      }
      if (c === '@' && c2 === '"') { state = 'verq'; out += keepStrings ? '@"' : '  '; i++; continue; }
      if (c === '"') { state = 'str'; out += keepStrings ? '"' : ' '; continue; }
      if (c === '\'') { state = 'chr'; out += keepStrings ? '\'' : ' '; continue; }
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
      if (c === '\\') { out += keepStrings ? c + c2 : '  '; i++; continue; }
      if (c === '"') { state = 'code'; out += keepStrings ? '"' : ' '; continue; }
      out += lit(c); continue;
    }
    if (state === 'verq') {
      if (c === '"' && c2 === '"') { out += keepStrings ? '""' : '  '; i++; continue; }
      if (c === '"') { state = 'code'; out += keepStrings ? '"' : ' '; continue; }
      out += lit(c); continue;
    }
    if (state === 'chr') {
      if (c === '\\') { out += keepStrings ? c + c2 : '  '; i++; continue; }
      if (c === '\'') { state = 'code'; out += keepStrings ? '\'' : ' '; continue; }
      out += lit(c); continue;
    }
  }
  return out;
}

// (1) credential-getter call sites. Pure over `lines` so the self-test needs no disk access.
function findCredGetters(lines) {
  const src = maskCodeOnly(lines.join('\n'));
  const hits = [];
  const lineOf = idx => src.slice(0, idx).split('\n').length;
  CRED_GETTER_RE.lastIndex = 0;
  let m;
  while ((m = CRED_GETTER_RE.exec(src)) !== null) {
    hits.push({ line: lineOf(m.index), name: m[1] });
  }
  return hits;
}

// Given the offset of a call's opening '(', return the offset just past its matching ')'.
// Returns -1 when unbalanced (truncated file / parse confusion), which the caller treats as
// "scan to end of file" rather than silently skipping — a census must not go quiet on weird input.
function matchingParen(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    if (src[i] === '(') depth++;
    else if (src[i] === ')') {
      depth--;
      if (depth === 0) return i + 1;
    }
  }
  return -1;
}

// Offset just past the '}' matching the '{' at openIdx. -1 when unbalanced, same contract as
// matchingParen: the caller scans to end-of-input rather than going quiet.
function matchingBrace(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}') {
      depth--;
      if (depth === 0) return i + 1;
    }
  }
  return -1;
}

// Split on commas at nesting depth 0, keeping each piece's offset within `s`. Used for both an
// initializer's member list and a call's argument list, where a naive split would cut through
// nested calls and objects (`new { a = F(x, y), token }` is TWO members, not three).
function splitTopLevel(s) {
  const out = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '(' || c === '{' || c === '[') depth++;
    else if (c === ')' || c === '}' || c === ']') depth--;
    else if (c === ',' && depth === 0) { out.push({ text: s.slice(start, i), offset: start }); start = i + 1; }
  }
  out.push({ text: s.slice(start), offset: start });
  return out;
}

// The member name a shorthand expression would project, or null when it is not a bare path.
function shorthandMemberName(expr) {
  const trimmed = expr.trim();
  if (!MEMBER_PATH_RE.test(trimmed)) return null;
  if (CANCELLATION_SOURCE_PATH_RE.test(trimmed)) return null; // cts.Token is not a credential
  const segments = trimmed.split('.').map(seg => seg.replace(/\?\s*$/, '').trim());
  return segments[segments.length - 1];
}

// True when a top-level piece carries an assignment (form 2a), so the shorthand pass must skip it
// and leave it to SECRET_MEMBER_RE. `==`, `=>`, `<=`, `>=`, `!=` are comparisons/lambdas, not
// assignments — mis-classifying `token == expected` as an assignment would resurrect a fixed bug.
function hasTopLevelAssignment(s) {
  let depth = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '(' || c === '{' || c === '[') depth++;
    else if (c === ')' || c === '}' || c === ']') depth--;
    else if (c === '=' && depth === 0) {
      const prev = i > 0 ? s[i - 1] : '';
      const next = i + 1 < s.length ? s[i + 1] : '';
      if (next === '=' || next === '>' || prev === '=' || prev === '!' || prev === '<' || prev === '>') continue;
      return true;
    }
  }
  return false;
}

// (2) secret-named members inside an HTTP RESPONSE call's arguments.
// Scoped deliberately: a secret assigned to an out-param, a local, or a typed object destined for
// disk is NOT a serialization leak, and flagging those made the check noise rather than a guard.
function findSecretMembers(lines) {
  const joined = lines.join('\n');
  const src = maskCodeOnly(joined);
  // Same length and newlines as `src`, but string CONTENTS survive — the dictionary-key form (2d)
  // carries its member name inside a literal, so it is invisible in `src`.
  const srcWithStrings = maskCodeOnly(joined, true);
  const hits = [];
  const seen = new Set();

  // Line number for a char offset, computed once per hit (files here are small).
  const lineOf = idx => src.slice(0, idx).split('\n').length;

  RESPONSE_CALL_RE.lastIndex = 0;
  let call;
  while ((call = RESPONSE_CALL_RE.exec(src)) !== null) {
    const openIdx = call.index + call[0].length - 1;
    const end = matchingParen(src, openIdx);
    const span = src.slice(openIdx, end === -1 ? src.length : end);

    // (2a) assignment form — `token = ...`.
    SECRET_MEMBER_RE.lastIndex = 0;
    let m;
    while ((m = SECRET_MEMBER_RE.exec(span)) !== null) {
      const abs = openIdx + m.index;
      if (seen.has(abs)) continue; // nested/overlapping response calls must not double-report
      seen.add(abs);
      hits.push({ line: lineOf(abs), name: m[1] });
    }

    // (2b) shorthand form — `new { token }` / `new { account.Token }`. Every object initializer
    // inside the response call is examined, so a secret nested in an inner `new { ... }` is caught
    // too (the outer member is `account = new { ... }`, which form 2a sees as a non-secret name).
    NEW_INITIALIZER_RE.lastIndex = 0;
    let init;
    while ((init = NEW_INITIALIZER_RE.exec(span)) !== null) {
      const braceIdx = init.index + init[0].length - 1;
      const braceEnd = matchingBrace(span, braceIdx);
      const body = span.slice(braceIdx + 1, braceEnd === -1 ? span.length : braceEnd - 1);
      for (const piece of splitTopLevel(body)) {
        if (hasTopLevelAssignment(piece.text)) continue; // form 2a's territory
        const name = shorthandMemberName(piece.text);
        if (!name || !SECRET_NAMES.has(name.toLowerCase())) continue;
        const abs = openIdx + braceIdx + 1 + piece.offset
          + (piece.text.length - piece.text.trimStart().length);
        if (seen.has(abs)) continue;
        seen.add(abs);
        hits.push({ line: lineOf(abs), name });
      }
    }

    // (2c) direct-return form — `return Ok(token);` with no wrapping object at all.
    for (const arg of splitTopLevel(span.slice(1, end === -1 ? span.length : span.length - 1))) {
      const name = shorthandMemberName(arg.text);
      if (!name || !SECRET_NAMES.has(name.toLowerCase())) continue;
      const abs = openIdx + 1 + arg.offset + (arg.text.length - arg.text.trimStart().length);
      if (seen.has(abs)) continue;
      seen.add(abs);
      hits.push({ line: lineOf(abs), name });
    }

    // (2d) dictionary-key form — `Ok(new Dictionary<string,string> { ["token"] = pat })`. Scanned
    // over the strings-preserving masking at the SAME offsets (both maskings preserve length).
    const spanWithStrings = srcWithStrings.slice(openIdx, end === -1 ? src.length : end);
    DICT_KEY_SECRET_RE.lastIndex = 0;
    let dict;
    while ((dict = DICT_KEY_SECRET_RE.exec(spanWithStrings)) !== null) {
      const abs = openIdx + dict.index;
      if (seen.has(abs)) continue;
      seen.add(abs);
      hits.push({ line: lineOf(abs), name: dict[1] });
    }
  }
  return hits.sort((a, b) => a.line - b.line);
}

// (3) raw-secret properties on a serialized model.
function findSecretProperties(lines) {
  const hits = [];
  const masked = maskCodeOnly(lines.join('\n')).split('\n');
  for (let i = 0; i < masked.length; i++) {
    const m = masked[i].match(SECRET_PROPERTY_RE);
    if (m) hits.push({ line: i + 1, name: m[1] });
  }
  return hits;
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
  let ran = 0;
  const report = (label, name, got, exp) => {
    const ok = got === exp;
    allOk = allOk && ok;
    ran++;
    console.log(`  ${ok ? 'ok  ' : 'FAIL'} [${label}] ${name} → ${got ? 'FLAGGED' : 'passed'} (expected ${exp ? 'FLAGGED' : 'passed'})`);
  };

  const getterCases = [
    { name: 'THE regression: controller calls _accountService.GetToken(...)', exp: true,
      code: '            var token = _accountService.GetToken(account.Id);' },
    { name: 'THE other regression: _ownerProfileService.GetGitHubToken()', exp: true,
      code: '            var token = _ownerProfileService.GetGitHubToken();' },
    { name: 'inline in a response object (the exact removed shape)', exp: true,
      code: '                token = _accountService.GetToken(account.Id)' },
    { name: 'a comment describing the removed call does NOT flag (masking is load-bearing here)', exp: false,
      code: '            // calls SourceControlAccountService.GetToken in-process rather than over HTTP' },
    { name: 'an XML doc <see cref> to the getter does not flag', exp: false,
      code: '        /// <see cref="SourceControlAccountService.GetToken"/> in-process' },
    { name: 'a string literal naming the getter does not flag', exp: false,
      code: '            _log.Info("GetToken(id) is service-only");' },
    { name: 'negative-control: an unrelated service call', exp: false,
      code: '            var account = _accountService.Get(id);' },
    { name: 'negative-control: HasToken presence flag is not a getter', exp: false,
      code: '                hasToken = account.HasToken' },
    // ---- FLUENT LINE-SPLIT. The blocking find of the ea7d9cf9 review pipeline: check (1) matched
    //      per LINE, so one newline hid a real getter. `API/` has 46 lines starting with `.`, so
    //      this is house style. With a non-secret member name check (2) is silent too, which made
    //      the combination pass the ENTIRE census over a genuine leak (reproduced on a probe
    //      controller: 57 files scanned, exit 0).
    { name: 'THE blocking regression: getter split across lines, fluent style', exp: true,
      code: '            return Ok(new { value = _accountService\n                .GetToken(id) });' },
    { name: 'fluent split with a comment between receiver and dot', exp: true,
      code: '            var t = _accountService   // resolve lazily\n                .GetToken(id);' },
    { name: 'null-conditional getter call', exp: true,
      code: '            var t = _accountService?.GetToken(id);' },
    // ---- `@$"` VERBATIM-INTERPOLATED MASKING. Missing this state sent the rest of the file into a
    //      never-closing string literal, masking it all to spaces — a silent, TOTAL blinding of all
    //      three checks. Zero `@$"` in the repo today, which is exactly why it needs a fixture.
    { name: 'a leak AFTER an @$" string with a trailing backslash is still seen', exp: true,
      code: '            var p = @$"C:\\temp\\";\n            var t = _accountService.GetToken(id);' },
    { name: 'negative-control: the @$" string contents themselves do not flag', exp: false,
      code: '            var p = @$"call _accountService.GetToken(id) here";' },
  ];
  for (const c of getterCases) report('getter', c.name, findCredGetters([c.code]).length > 0, c.exp);

  const memberCases = [
    { name: 'THE regression: the exact removed shape, a PAT member in an Ok(new { ... })', exp: true,
      code: '            return Ok(new\n            {\n                username = account.Username,\n                hasToken = account.HasToken,\n                token = _accountService.GetToken(account.Id)\n            });' },
    { name: 'a secret arriving from a NON-getter source still flags (check 1 would miss it)', exp: true,
      code: '            return Ok(new { token = _someFutureCache[id] });' },
    { name: 'self-assigned member `token = token`', exp: true,
      code: '            return Ok(new { token = token });' },
    // ---- SHORTHAND (form 2b). These were the census's own blind spot until item ③'s falsification
    //      pass restored the real deleted endpoint and found only check (1) firing. The fixture that
    //      used to sit here CLAIMED to cover `Ok(new { token })` but actually tested
    //      `Ok(new { token = token })` — a form the shipped code never used — so 31/31 green was
    //      measured against a paraphrase of the leak rather than the leak.
    { name: 'THE second removed shape, verbatim: the deleted {id}/token endpoint ended `Ok(new { token })`', exp: true,
      code: '            var token = _accountService.GetToken(id);\n            return Ok(new { token });' },
    { name: 'the leak that would have passed BOTH checks: shorthand from a non-getter source', exp: true,
      code: '            var token = _someFutureCache[id];\n            return Ok(new { token });' },
    { name: 'shorthand via a dotted path projects the last segment as the member name', exp: true,
      code: '            return Ok(new { account.Token });' },
    { name: 'shorthand via a null-conditional path', exp: true,
      code: '            return Ok(new { account?.PrivateKey });' },
    { name: 'shorthand alongside innocent members still flags', exp: true,
      code: '            return Ok(new { username, hasToken, token });' },
    { name: 'shorthand nested in an inner initializer still flags', exp: true,
      code: '            return Ok(new { account = new { id, apiKey } });' },
    { name: 'direct return of a bare secret with no wrapping object (form 2c)', exp: true,
      code: '            return Ok(token);' },
    { name: 'direct return of a secret via a dotted path', exp: true,
      code: '            return Ok(account.Token);' },
    { name: 'negative-control: innocent shorthand members', exp: false,
      code: '            return Ok(new { username, hasToken, provider });' },
    { name: 'real site: ListAccounts `Ok(new { count = accounts.Count, accounts })`', exp: false,
      code: '            return Ok(new { count = accounts.Count, accounts });' },
    { name: 'negative-control: a secret used only INSIDE an expression, under a non-secret name', exp: false,
      code: '            return Ok(new { valid = Validate(token) });' },
    { name: 'negative-control: tokenTotal shorthand is a metric, not a credential', exp: false,
      code: '            return Ok(new { tokenTotal });' },
    { name: 'negative-control: a trailing CancellationToken argument is not a response member', exp: false,
      code: '            await context.Response.WriteAsJsonAsync(payload, cancellationToken);' },
    { name: 'negative-control: shorthand in a NON-response initializer is out of scope', exp: false,
      code: '            var cfg = new { token };' },
    { name: 'minimal-API form: Results.Ok', exp: true,
      code: '                return Results.Ok(new { apiKey = configured });' },
    { name: 'Results.Json form', exp: true,
      code: '                return Results.Json(new { password = p });' },
    { name: 'WriteAsJsonAsync form', exp: true,
      code: '            await context.Response.WriteAsJsonAsync(new { clientSecret = s });' },
    { name: 'a secret nested deeper inside the response object still flags', exp: true,
      code: '            return Ok(new { account = new { id, privateKey = k } });' },
    { name: 'negative-control: hasToken presence flag in a response is exactly what SHOULD be there', exp: false,
      code: '            return Ok(new { username = account.Username, hasToken = account.HasToken });' },
    // ---- The three REAL sites the first draft of this check wrongly flagged. Each is a genuine
    //      piece of this codebase, kept here so the precision fix cannot silently regress.
    { name: 'real site (was a false positive): `out` param assignment in TryGetApiKey', exp: false,
      code: '            if (string.IsNullOrWhiteSpace(configured))\n            {\n                apiKey = string.Empty;\n                error = "not configured";\n                return false;\n            }\n            apiKey = configured;' },
    { name: 'real site (was a false positive): VAPID PrivateKey in a typed config written to FILE', exp: false,
      code: '            var newCfg = new PushConfig\n            {\n                PublicKey = keys.PublicKey,\n                PrivateKey = keys.PrivateKey,\n            };\n            File.WriteAllText(_configPath, JsonSerializer.Serialize(newCfg));' },
    { name: 'negative-control: `cts.Token` property read (no assignment)', exp: false,
      code: '                        cancellationToken, timeoutCts.Token);' },
    // ---- CANCELLATION TOKENS. The fixture that used to sit here called `_host.StopAsync(...)`,
    //      which is NOT a response call, so it passed VACUOUSLY while the real ASP.NET overload
    //      below was a false positive. A false positive in a build-failing census is worse than a
    //      gap — it blocks legitimate work. Both shapes are now asserted against real response calls.
    { name: 'THE false positive: idiomatic WriteAsJsonAsync(payload, cts.Token) must NOT flag', exp: false,
      code: '            await context.Response.WriteAsJsonAsync(payload, cts.Token);' },
    { name: 'negative-control: linked/timeout cancellation sources in a response call', exp: false,
      code: '            await context.Response.WriteAsJsonAsync(payload, linkedCts.Token);' },
    { name: 'negative-control: a named CancellationTokenSource in a response call', exp: false,
      code: '            return Ok(await _svc.RunAsync(cancellationTokenSource.Token));' },
    { name: 'but a genuine `account.Token` direct return still flags (exclusion is receiver-keyed)', exp: true,
      code: '            return Ok(account.Token);' },
    // ---- RESPONDERS BEYOND Ok(). Derived from current usage originally, which missed every other
    //      ControllerBase responder. Reintroduction is by definition code that does not exist yet.
    { name: 'THE second blocking shape: Json(new { token }) was invisible', exp: true,
      code: '            return Json(new { token });' },
    { name: 'StatusCode(200, new { token })', exp: true,
      code: '            return StatusCode(200, new { token });' },
    { name: 'BadRequest(new { token })', exp: true,
      code: '            return BadRequest(new { token });' },
    { name: 'NotFound(new { token })', exp: true,
      code: '            return NotFound(new { token });' },
    { name: 'new JsonResult(new { token })', exp: true,
      code: '            return new JsonResult(new { token });' },
    // ---- DICTIONARY-KEY FORM. Member name lives in a string literal, which the default masking
    //      blanks. Flagged independently by the security and adversary gates.
    { name: 'dictionary-key form: Ok(new Dictionary<string,string> { ["token"] = pat })', exp: true,
      code: '            return Ok(new Dictionary<string, string> { ["token"] = pat });' },
    { name: 'dictionary-key form with apiKey', exp: true,
      code: '            return Results.Json(new Dictionary<string, string> { ["apiKey"] = k });' },
    { name: 'negative-control: innocent dictionary key', exp: false,
      code: '            return Ok(new Dictionary<string, string> { ["username"] = u });' },
    { name: 'negative-control: a secret-named dictionary key OUTSIDE a response call', exp: false,
      code: '            var cache = new Dictionary<string, string> { ["token"] = pat };' },
    { name: 'negative-control: private field assignment `_token = `', exp: false,
      code: '            _token = GenerateToken();' },
    { name: 'negative-control: `var token = ` local outside any response call', exp: false,
      code: '            var token = Compute();' },
    { name: 'negative-control: equality comparison `token == x` is not an assignment', exp: false,
      code: '            if (token == expected) return Ok(new { ok = true });' },
    { name: 'a commented-out secret member does not flag', exp: false,
      code: '            return Ok(new {\n                // token = _accountService.GetToken(id)\n                hasToken = true });' },
    { name: 'negative-control: tokenTotal metric member in a response', exp: false,
      code: '            return Ok(new { tokenTotal = meter.Tokens });' },
  ];
  for (const c of memberCases) report('member', c.name, findSecretMembers(c.code.split('\n')).length > 0, c.exp);

  const propCases = [
    { name: 'THE model regression: `public string Token` would leak via whole-object serialization', exp: true,
      code: '        public string Token { get; set; }' },
    { name: 'public string Password flags', exp: true,
      code: '        public string Password { get; set; }' },
    { name: 'negative-control: the HasToken presence boolean is exactly what SHOULD be there', exp: false,
      code: '        public bool HasToken { get; set; }' },
    { name: 'negative-control: HasGitHubToken boolean', exp: false,
      code: '        public bool HasGitHubToken { get; set; }' },
    { name: 'nullable form `public string? Token` flags', exp: true,
      code: '        public string? Token { get; set; }' },
    { name: '`public required string Token` flags', exp: true,
      code: '        public required string Token { get; set; }' },
    { name: 'a bare public field flags', exp: true,
      code: '        public string AccessToken;' },
    { name: 'negative-control: an ordinary string property', exp: false,
      code: '        public string Username { get; set; }' },
    { name: 'negative-control: an ordinary nullable string property', exp: false,
      code: '        public string? Username { get; set; }' },
    { name: 'a commented-out secret property does not flag', exp: false,
      code: '        // public string Token { get; set; }' },
  ];
  for (const c of propCases) report('model', c.name, findSecretProperties([c.code]).length > 0, c.exp);

  console.log(allOk
    ? `\nSELF-TEST PASSED (${ran}/${ran}) — credential-getter, response-member and model-property `
      + 'checks all provably reject the bad shapes (including fluent line-splits, non-Ok responders '
      + 'and dictionary keys), and none of them fire on comments, doc refs, cancellation-source '
      + '.Token reads inside real response calls, or the HasToken presence flags.'
    : `\nSELF-TEST FAILED (${ran} fixtures ran) — a check does not falsify correctly.`);
  process.exit(allOk ? 0 : 1);
}

if (doSelfTest) selfTest();

// -------------------------------------------------------------------------------------
const problems = [];
let apiFilesScanned = 0;
let getterSites = 0;
let memberSites = 0;

const apiRoot = path.join(REPO_ROOT, API_DIR);
if (!fs.existsSync(apiRoot)) {
  problems.push(`${API_DIR}/ NOT FOUND — this census scopes to the HTTP layer; if it moved, update `
    + 'API_DIR (and reconsider whether the no-dispensing guarantee still has a home).');
}

for (const abs of scanFiles(apiRoot)) {
  const rel = path.relative(REPO_ROOT, abs).replace(/\\/g, '/');
  const lines = fs.readFileSync(abs, 'utf8').split(/\r?\n/);
  apiFilesScanned++;

  for (const hit of findCredGetters(lines)) {
    const key = `${rel}::${hit.name}`;
    if (ALLOWED_CRED_GETTERS.has(key)) { getterSites++; continue; }
    problems.push(`${rel}:${hit.line} CREDENTIAL GETTER — calls ${hit.name}() in the HTTP layer. The `
      + 'API must never hold a plaintext credential: have the SERVICE perform the authenticated '
      + 'operation and return its result instead (task ea7d9cf9).');
  }

  for (const hit of findSecretMembers(lines)) {
    const key = `${rel}::${hit.name}`;
    if (ALLOWED_SECRET_MEMBERS.has(key)) { memberSites++; continue; }
    problems.push(`${rel}:${hit.line} SECRET RESPONSE MEMBER — serializes a member named '${hit.name}' `
      + '(assigned, C# shorthand, or returned directly). '
      + 'This is the exact shape ea7d9cf9 removed (a PAT serialized into a response body, which then '
      + "landed in agents' session transcripts on disk). Return a RESULT, not a credential.");
  }
}

// (3) the guarded models. A missing file must FAIL rather than silently skip — otherwise a rename
// quietly retires the check, which is how a guarantee rots into decoration.
for (const rel of GUARDED_MODELS) {
  const abs = path.join(REPO_ROOT, rel);
  if (!fs.existsSync(abs)) {
    problems.push(`${rel} NOT FOUND — it is on the GUARDED_MODELS list because it is returned whole `
      + 'by an endpoint. If it was renamed or removed, update the list in the same commit.');
    continue;
  }
  const lines = fs.readFileSync(abs, 'utf8').split(/\r?\n/);
  for (const hit of findSecretProperties(lines)) {
    problems.push(`${rel}:${hit.line} SECRET MODEL PROPERTY — declares a raw '${hit.name}' property. `
      + 'This model is serialized WHOLE by an endpoint (GET /api/source-accounts returns account '
      + 'objects), so this would leak through a controller nobody edited. Keep presence booleans '
      + '(HasToken) and store the secret in Credential Manager only.');
  }
}

if (problems.length) {
  console.log(`\nFAIL (${problems.length}):`);
  for (const p of problems) console.log(`  - ${p}`);
  process.exit(1);
}

console.log('No-secret-serialization census (task ea7d9cf9):');
console.log(`  ok  API/ files scanned: ${apiFilesScanned} — 0 credential-getter call sites, `
  + '0 secret-named response members');
for (const rel of GUARDED_MODELS) console.log(`  ok  ${rel} — presence booleans only, no raw secret property`);
if (getterSites || memberSites) {
  console.log(`\n  named exemptions in use: ${getterSites} getter, ${memberSites} member`);
  for (const [key, why] of ALLOWED_CRED_GETTERS) console.log(`    - ${key}: ${why}`);
  for (const [key, why] of ALLOWED_SECRET_MEMBERS) console.log(`    - ${key}: ${why}`);
} else {
  console.log('\n  named exemptions: NONE — the API layer touches no plaintext credential at all.');
}
console.log('\nSCOPE (a green run does NOT mean "no secret can ever escape"): this census covers the '
  + `${API_DIR}/ tree and ${GUARDED_MODELS.length} named models. It does not prove a secret cannot reach a `
  + 'response by another route — a Services/ type that serializes itself, a credential in an error '
  + "message or log line, or one named outside check (2)'s denylist (e.g. `credentials`). Widen the "
  + 'lists when a new credential shape appears.');
console.log('\nPASS — the HTTP layer reads no credential, serializes no secret-named member, and the '
  + 'credential-bearing models expose presence booleans only.');
