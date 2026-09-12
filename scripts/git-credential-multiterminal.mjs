#!/usr/bin/env node
// git-credential-multiterminal.mjs — hands git a freshly minted GitHub App installation token,
// for the duration of one command, and stores nothing (task b42b1883, item 4).
//
// WHY THIS EXISTS. Agents currently push and comment under the Owner's own GitHub account, so
// nothing distinguishes the Owner's words from an agent's. The fix is a GitHub App identity whose
// private key never leaves MultiTerminal; what an agent may hold is a token that expires within the
// hour. This helper is how `git push` gets one.
//
// WHY A CREDENTIAL HELPER RATHER THAN AN ENVIRONMENT VARIABLE. Two reasons, and the second is fatal
// to the env-var design (recorded in the ticket's survey findings):
//   (1) Terminal environments are built from a PowerShell command line ConPtyTerminal composes, and
//       that line reaches the debug log, which agents read via the debug_logs MCP tool. The launch
//       nonce leak (task fd3437e6) is the precedent.
//   (2) Installation tokens expire in ~1h and an environment variable CANNOT be changed in a running
//       process from outside. Any terminal alive longer than an hour would hold a dead token with no
//       way to refresh. A helper mints per operation, so nothing is stored and nothing expires in
//       the wrong place.
//
// THE PROTOCOL (git-credential(1)): git runs us with one argument — get, store or erase — and writes
// `key=value` lines on stdin terminated by a blank line. For `get` we answer with `username=` and
// `password=` lines and a blank line. Writing NOTHING and exiting 0 is a valid "I cannot help", which
// leaves git to try the next helper or prompt; that is our behaviour on every failure, because a
// credential helper that errors out or hangs breaks every git operation in the terminal.
//
// FOUR THINGS THAT ARE LOAD-BEARING — each has a test that fails ALONE when it is removed:
//
//   (1) WE ANSWER ONLY FOR github.com OVER https. This is the security-critical one. git asks the
//       helper for whatever host the remote names, so a repository whose URL points at
//       evil.example.com would otherwise be handed a live GitHub token that can push code and
//       comment as the bot. The host check is not tidiness — without it this file is a token
//       exfiltration endpoint that any hostile remote URL can trigger.
//
//   (2) AN ABSENT NONCE MEANS SILENCE, NOT A MINT ATTEMPT. Rows legitimately have no nonce: an
//       adopted terminal (a plain shell that registered itself) was never launched by MT and has
//       none. Asking anyway would put a pointless 401 in MT's logs on every git operation in such a
//       terminal, and would invite "just make the endpoint accept an empty nonce" as a fix later.
//
//   (3) A FAILED MINT PRODUCES NO OUTPUT AND EXIT 0. 401 (not an MT terminal), 503 (no App
//       configured yet — the correct state until the Owner runs item 3), a timeout, a dead MT: all
//       are "cannot help". Emitting a partial answer would make git try an empty password; a
//       non-zero exit makes git treat the helper as broken.
//
//   (4) THE TOKEN IS NEVER LOGGED. Diagnostics go to stderr and name only the SHAPE of the failure.
//       stdout carries the credential and nothing else, because stdout IS the protocol channel —
//       a stray console.log there corrupts the response git parses.
//
// WHAT THIS DOES NOT COVER: `gh` does NOT use git's credential helper (it reads GH_TOKEN or its own
// config), so issue comments and PRs — the motivating use case — need the separate shim in item 5.
// This file alone would leave the ticket's headline scenario unfixed.

import process from 'node:process';
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

import {
  isOwnerHatchRequested,
  readGhActiveAccount,
  ownerHatchNotice,
} from './lib/owner-escape-hatch.mjs';

// One shared mint path with the gh shim (item 5). The nonce rule, the timeout and the
// "a token or nothing" contract live there so the two callers cannot drift apart.
import { mintInstallationToken } from './lib/mint-github-token.mjs';

/** The only host we will ever hand a token to. See load-bearing fact (1). */
const GITHUB_HOST = 'github.com';

/**
 * How long we will wait for MT before giving up. git blocks on the helper, so this cap is what stops
 * a wedged or busy MT from freezing every git command in the terminal. Short on purpose: the mint
 * endpoint is a loopback call that normally answers in milliseconds, and "no credential" degrades
 * gracefully while "git hangs" does not.
 */
const MINT_TIMEOUT_MS = 5000;

const MT_API_URL = process.env.MT_API_URL || 'http://localhost:5050';

/** Diagnostics only. Never include a token, and never write to stdout — stdout is the protocol. */
function warn(message) {
  process.stderr.write(`[git-credential-multiterminal] ${message}\n`);
}

function readStdin() {
  return new Promise((resolve) => {
    let buffer = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => { buffer += chunk; });
    process.stdin.on('end', () => resolve(buffer));
    process.stdin.on('error', () => resolve(buffer));
  });
}

/**
 * Parse git's `key=value` block. Later keys win, matching git's own behaviour, and a line without
 * `=` is ignored rather than throwing — an unparseable request must degrade to "cannot help".
 */
export function parseCredentialRequest(input) {
  const request = {};
  for (const line of String(input ?? '').split(/\r?\n/)) {
    if (!line) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    request[line.slice(0, eq)] = line.slice(eq + 1);
  }
  return request;
}

/**
 * Whether this request is one we may answer. Exported so the decision is testable on its own rather
 * than only through a spawned process.
 *
 * `host` may carry a port (git sends `host=github.com:443` for an explicit port). We strip ONLY the
 * standard https port: anything else is a different endpoint than the one we mean, and a token is
 * not something to hand over on a "close enough" match.
 */
export function isGitHubRequest(request) {
  const protocol = String(request?.protocol ?? '').toLowerCase();
  if (protocol !== 'https') return false;

  let host = String(request?.host ?? '').toLowerCase();
  if (host.endsWith(':443')) host = host.slice(0, -4);

  return host === GITHUB_HOST;
}

async function main() {
  // git passes the operation as the first argument. `store` and `erase` are deliberate no-ops: we
  // persist nothing, so there is nothing to save and nothing to forget. An unknown operation (a
  // future git verb) is treated the same way — silence is always a safe answer.
  if (process.argv[2] !== 'get') return;

  const request = parseCredentialRequest(await readStdin());

  if (!isGitHubRequest(request)) {
    // Not our host. Say nothing at all: git moves on to its other helpers, exactly as if this one
    // were not configured.
    return;
  }

  // ITEM 8: the Owner escape hatch, honoured here as well as in the gh shim so that ONE variable
  // governs identity across both tools. A hatch that covered `gh issue comment` but silently left
  // `git push` acting as the bot would be a worse kind of confusing than no hatch at all.
  //
  // We decline rather than minting. Note honestly what that does and does not achieve: per the
  // correction below, declining leaves git with no helper for github.com, so this makes the push
  // PROMPT rather than making it succeed as the Owner. That is the truthful ceiling of what this
  // file can do alone, and it is why the message names the concrete way through.
  //
  // ⚠️ NOT IMPLEMENTED ON PURPOSE — needs an Owner decision, not an agent's: this helper COULD
  // delegate to `gh auth git-credential` and hand git the Owner's own credential. That would make
  // the hatch work end to end for pushes, at the cost of routing a long-lived personal credential
  // through MultiTerminal's helper process — the precise thing the rest of this ticket removes.
  // Choosing that trade is the Owner's call, so it is written down rather than taken.
  if (isOwnerHatchRequested(process.env, warn)) {
    const account = readGhActiveAccount({
      env: process.env,
      readFileText: (p) => fs.readFileSync(p, 'utf8'),
    });
    for (const line of ownerHatchNotice(account, 'Commits pushed by this command')) warn(line);
    warn('This helper is declining, and MT configures no other helper for github.com, so git will'
      + ' prompt. To push as the Owner, re-run with:'
      + ' git -c credential.https://github.com.helper=\'!gh auth git-credential\' push');
    return;
  }

  const token = await mintInstallationToken({
    nonce: process.env.MULTITERMINAL_LAUNCH_NONCE,
    installationId: process.env.MULTITERMINAL_GITHUB_INSTALLATION_ID || null,
    apiUrl: MT_API_URL,
    timeoutMs: MINT_TIMEOUT_MS,
    warn,
  });

  // Every failure lands here identically, and every one of them means the same thing to git:
  // say nothing, exit 0.
  //
  // ⚠️ CORRECTED (item 8). This block used to say "git will fall back to its other credential
  // helpers ... it succeeds, under their name", and that is FALSE in the only situation where this
  // file ever runs. Measured, not reasoned: in an MT-launched terminal the effective helper list for
  // github.com is
  //     manager            (generic, from .gitconfig)
  //     ""                 (reset, from .gitconfig)
  //     !gh auth git-credential   (the Owner's helper, from .gitconfig)
  //     ""                 (reset, from item 6's GIT_CONFIG_* clear-then-set)
  //     !node this file    (ours)
  // and git treats an empty value as "reset the list to empty". So ours is the ONLY helper left —
  // which is exactly what item 6 intended, since leaving the Owner's helper in the list would have
  // agents pushing as the Owner while appearing to work. The consequence is that declining here
  // leaves git with NO helper for github.com: it prompts, and a non-interactive agent terminal fails.
  // It does not silently succeed as the Owner.
  //
  // That correction matters beyond tidiness: the old text told a reader their push had merely been
  // misattributed, when in fact it had not happened. Both are worth warning about, but they call for
  // opposite next actions.
  //
  // Silence remains the right answer TO GIT (a helper that errors makes git report failure for an
  // operation that might still succeed) and the wrong answer to the human. git ignores a helper's
  // stderr, so saying so cannot affect the operation.
  if (!token) {
    warn('No MultiTerminal bot token, and MT configures this as the ONLY credential helper for'
      + ' github.com — so git has nothing else to try.');
    warn('Expect an authentication prompt, or a failed push in a non-interactive terminal. This is'
      + ' a failure to authenticate, NOT a push that silently went out under someone else\'s name.');
    return;
  }

  // x-access-token is GitHub's username convention for an installation token. The blank line
  // terminates the response; without it git keeps reading.
  process.stdout.write(`username=x-access-token\npassword=${token}\n\n`);
}

// Run ONLY when git invoked this file directly. The pure helpers above are imported by the tests,
// and without this guard that import would start main(), which reads stdin — the test runner's
// stdin — and hangs waiting for a request that never comes.
const invokedDirectly = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;

if (invokedDirectly) {
  // Never let an unexpected throw reach git as a non-zero exit: a helper that "fails" makes git
  // report an error for an operation that could still have succeeded without us.
  main().catch((err) => {
    warn(`Unexpected failure: ${err?.message ?? 'unknown'}`);
    process.exit(0);
  });
}
