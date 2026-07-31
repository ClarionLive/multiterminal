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
//       pat/apiKey/accessToken/privateKey may be assigned in an API/ file. Check (1) catches the
//       value arriving from a known getter; this catches it arriving from ANYWHERE else (a field, a
//       parameter, a future service with a differently-named accessor). This is the exact shape that
//       shipped: `token = _accountService.GetToken(account.Id)` inside an `Ok(new { ... })`.
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
// name list is a denylist, and a denylist is only as good as its entries: a member named
// `credentials` or `ghp` would pass. Do not read PASS as "no secret can ever escape".
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
const CRED_GETTER_RE = /\b[A-Za-z_]\w*\s*\.\s*(GetToken|GetGitHubToken)\s*\(/;

// Secret-named members, matched ONLY inside the argument list of an HTTP response call (see
// findSecretMembers). Scoping to the response call is what makes this precise rather than noisy: an
// earlier draft anchored to start-of-line and flagged three innocent sites in the first real run —
// two `out`-parameter assignments (`apiKey = configured;` in GatewayServiceEndpoints.TryGetApiKey)
// and a typed config initializer written to a local FILE (`PrivateKey = keys.PrivateKey` in
// PushNotificationService, where persisting the VAPID key is required or every phone re-subscribes).
// None of those reach a response body. All three are now negative-control fixtures in the self-test.
const SECRET_MEMBER_RE =
  /\b(token|secret|password|pat|apiKey|accessToken|privateKey|clientSecret)\s*=(?!=)/gi;

// HTTP response idioms used across this codebase's controllers (`return Ok(new { ... })`) and the
// gateway's minimal-API handlers (`Results.Ok(...)`, `Results.Json(...)`).
const RESPONSE_CALL_RE =
  /\b(?:Results\s*\.\s*(?:Ok|Json|Content)|Ok|Created|CreatedAtAction|Accepted|WriteAsJsonAsync)\s*\(/g;

// Property declarations that would put a raw secret on a serialized model. `HasToken` is fine —
// the boundary is a `string`-typed secret, not the presence flag.
const SECRET_PROPERTY_RE =
  /\bpublic\s+string\s+(Token|Password|Secret|Pat|ApiKey|AccessToken|PrivateKey|ClientSecret)\b/i;

const API_DIR = 'API';
const GUARDED_MODELS = ['Models/SourceControlAccount.cs', 'Models/OwnerProfile.cs'];

// ── NAMED EXEMPTIONS ──────────────────────────────────────────────────────────────────────────────
// Empty by design: after ea7d9cf9 the API layer has ZERO credential-getter call sites, so there is
// nothing to waive. An entry here means "this HTTP-layer code legitimately touches a plaintext
// credential", which should be approximately never — prefer moving the operation into the service.
// Keys are `relPath::identifier`; every entry must state why.
const ALLOWED_CRED_GETTERS = new Map();
const ALLOWED_SECRET_MEMBERS = new Map();

// Build artifacts, the test project, and nested worktrees. MultiTerminal.Tests is skipped for the
// SAME reason verify-writegate.mjs skips it — and it matters here: the ea7d9cf9 regression tests
// deliberately name the forbidden shapes in order to assert their ABSENCE, so scanning them would
// make the census fail on its own proof.
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.claude', 'staged', 'Deploy',
  'packages', 'TestResults', '.vs', 'MultiTerminal.Tests']);

// Blank the CONTENTS of comments and string/char literals (preserving length and newlines) so
// detection sees CODE only. Same masker as verify-writegate.mjs / verify-taskdb-gate.mjs. This is
// load-bearing here, not decorative: both controllers now carry long remarks EXPLAINING the removed
// token endpoints, and a naive grep would flag the very comments documenting the fix.
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

// (1) credential-getter call sites. Pure over `lines` so the self-test needs no disk access.
function findCredGetters(lines) {
  const hits = [];
  const masked = maskCodeOnly(lines.join('\n')).split('\n');
  for (let i = 0; i < masked.length; i++) {
    const m = masked[i].match(CRED_GETTER_RE);
    if (m) hits.push({ line: i + 1, name: m[1] });
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

// (2) secret-named members inside an HTTP RESPONSE call's arguments.
// Scoped deliberately: a secret assigned to an out-param, a local, or a typed object destined for
// disk is NOT a serialization leak, and flagging those made the check noise rather than a guard.
function findSecretMembers(lines) {
  const src = maskCodeOnly(lines.join('\n'));
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

    SECRET_MEMBER_RE.lastIndex = 0;
    let m;
    while ((m = SECRET_MEMBER_RE.exec(span)) !== null) {
      const abs = openIdx + m.index;
      if (seen.has(abs)) continue; // nested/overlapping response calls must not double-report
      seen.add(abs);
      hits.push({ line: lineOf(abs), name: m[1] });
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
  ];
  for (const c of getterCases) report('getter', c.name, findCredGetters([c.code]).length > 0, c.exp);

  const memberCases = [
    { name: 'THE regression: the exact removed shape, a PAT member in an Ok(new { ... })', exp: true,
      code: '            return Ok(new\n            {\n                username = account.Username,\n                hasToken = account.HasToken,\n                token = _accountService.GetToken(account.Id)\n            });' },
    { name: 'a secret arriving from a NON-getter source still flags (check 1 would miss it)', exp: true,
      code: '            return Ok(new { token = _someFutureCache[id] });' },
    { name: 'the other removed shape: Ok(new { token })-style single member', exp: true,
      code: '            return Ok(new { token = token });' },
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
    { name: 'negative-control: a CancellationToken argument inside a response-shaped call', exp: false,
      code: '            await _host.StopAsync(linkedCts.Token);' },
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
    { name: 'negative-control: an ordinary string property', exp: false,
      code: '        public string Username { get; set; }' },
    { name: 'a commented-out secret property does not flag', exp: false,
      code: '        // public string Token { get; set; }' },
  ];
  for (const c of propCases) report('model', c.name, findSecretProperties([c.code]).length > 0, c.exp);

  console.log(allOk
    ? `\nSELF-TEST PASSED (${ran}/${ran}) — credential-getter, response-member and model-property `
      + 'checks all provably reject the bad shapes, and none of them fire on comments, doc refs, '
      + 'CancellationToken, or the HasToken presence flags.'
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
    problems.push(`${rel}:${hit.line} SECRET RESPONSE MEMBER — assigns a member named '${hit.name}'. `
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
