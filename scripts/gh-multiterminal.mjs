#!/usr/bin/env node
// gh-multiterminal.mjs — runs the real `gh` with a freshly minted bot token in ITS environment only
// (task b42b1883, item 5).
//
// WHY THIS EXISTS, AND WHY IT IS NOT OPTIONAL POLISH. `gh` does NOT use git's credential helper: it
// authenticates from GH_TOKEN or its own stored config. So item 4 covers `git push` and nothing else,
// while the motivating use case of this whole ticket — an agent commenting on an issue as
// clarionlive-agent[bot] rather than as the Owner — goes through `gh issue comment`. Without this
// shim the ticket's headline scenario stays broken.
//
// WHY A WRAPPER PROCESS RATHER THAN AN EXPORTED VARIABLE. Setting GH_TOKEN in the terminal's own
// environment would put a live credential in a place every child process, every `env` dump and every
// crash report can read, and it would expire in an hour with no way to refresh a running shell. Here
// the token exists only in the environment block handed to ONE child, for the life of ONE command.
//
// FOUR RULES THAT ARE LOAD-BEARING — each has a test that fails alone when it is removed:
//
//   (1) A FAILED MINT STILL RUNS gh. No App configured (the correct state until item 3 is done), MT
//       down, no launch nonce in an adopted terminal: in every case the real `gh` runs with the
//       environment it would have had anyway. A shim that refuses to run on failure would break every
//       `gh` invocation in the terminal the moment MultiTerminal hiccupped.
//
//   (2) AN EXISTING GH_TOKEN IS NEVER OVERWRITTEN. If the environment already carries one, someone
//       chose it deliberately — that is the Owner escape hatch the plan insists must be explicit
//       rather than silently overridden. We say so on stderr and change nothing.
//
//   (3) THE CHILD'S EXIT CODE IS OURS. `gh` is scripted against; swallowing a non-zero exit would
//       turn a failed comment into an apparent success for every caller that checks.
//
//   (4) WE NEVER EXEC OURSELVES. This shim is meant to be found on PATH under the name `gh`, so a
//       naive PATH search finds the shim again and recurses until the process table gives out. Any
//       candidate inside the shim's own directory is skipped, and if resolution still lands on this
//       file we refuse loudly instead of looping.
//
//   (5) SIGNING NEVER BREAKS THE COMMAND. Item 7 rewrites the body of comment-authoring subcommands
//       to carry the agent's name, because one App is one identity and the bot marker alone cannot
//       say WHICH agent spoke. Every failure mode of that rewrite — no name, unknown subcommand,
//       unreadable file, body on stdin — falls through to running gh with the ORIGINAL arguments.
//       An unsigned comment is a small loss; a mangled one is a real one.
//
// The token is never logged: stderr diagnostics name only the shape of a failure, and stdout belongs
// entirely to `gh`.

import process from 'node:process';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { pathToFileURL, fileURLToPath } from 'node:url';

import { mintInstallationToken } from './lib/mint-github-token.mjs';
import { transformArgs, isSigningEnabled } from './lib/sign-comment.mjs';

const MINT_TIMEOUT_MS = 5000;
const MT_API_URL = process.env.MT_API_URL || 'http://localhost:5050';

function warn(message) {
  process.stderr.write(`[gh-multiterminal] ${message}\n`);
}

/**
 * Find the real `gh`.
 *
 * `override` (MULTITERMINAL_REAL_GH) wins when set — that is how the terminal launch wiring (item 6)
 * can name the exact executable instead of trusting PATH order, and how the tests point at a stand-in.
 *
 * Otherwise walk PATH, skipping anything inside `shimDir`, because that directory is where this shim
 * is installed under the name `gh` and finding it again is rule (4).
 *
 * @param {object} options
 * @param {string|null} options.override    Explicit path to the real gh, or null.
 * @param {string} options.pathValue        The PATH environment value.
 * @param {string} options.shimDir          Directory holding the shim; every candidate in it is skipped.
 * @param {string[]} [options.extensions]   Executable suffixes to try (Windows PATHEXT-style).
 * @param {(p: string) => boolean} [options.exists]  Injectable for tests.
 * @returns {string|null}
 */
export function resolveRealGh({
  override,
  pathValue,
  shimDir,
  extraSkipDirs = [],
  extensions = ['.exe', '.cmd', ''],
  exists = (p) => { try { return fs.statSync(p).isFile(); } catch { return false; } },
}) {
  if (override) return override;

  // TWO directories must be skipped, and missing the second one is the recursion bug in practice:
  // this .mjs lives in the repo's scripts/ folder, but what is actually ON PATH under the name `gh`
  // is a launcher in MultiTerminal's shims directory. Skipping only our own folder would find that
  // launcher, which runs this file again, forever. MT passes the shims directory in
  // MULTITERMINAL_GH_SHIM_DIR precisely so we can exclude it.
  const skip = new Set(
    [shimDir, ...extraSkipDirs]
      .filter(Boolean)
      .map((d) => path.resolve(d).toLowerCase()),
  );

  for (const dir of String(pathValue ?? '').split(path.delimiter)) {
    if (!dir) continue;
    if (skip.has(path.resolve(dir).toLowerCase())) continue;

    for (const ext of extensions) {
      const candidate = path.join(dir, `gh${ext}`);
      if (exists(candidate)) return candidate;
    }
  }

  return null;
}

/**
 * The environment the child gets. Separate and pure so the "never overwrite" rule is testable
 * without spawning anything.
 *
 * Note GITHUB_TOKEN is checked too: `gh` falls back to it, so a token there is just as deliberate a
 * choice as one in GH_TOKEN, and overriding it would be the same silent hijack rule (2) forbids.
 */
export function buildChildEnv(parentEnv, token) {
  if (!token) return { ...parentEnv };
  if (parentEnv.GH_TOKEN || parentEnv.GITHUB_TOKEN) return { ...parentEnv };
  return { ...parentEnv, GH_TOKEN: token };
}

/** True when the environment already carries a token someone chose on purpose. */
export function hasExplicitToken(parentEnv) {
  return Boolean(parentEnv.GH_TOKEN || parentEnv.GITHUB_TOKEN);
}

async function main() {
  const shimPath = fileURLToPath(import.meta.url);
  const realGh = resolveRealGh({
    override: process.env.MULTITERMINAL_REAL_GH || null,
    pathValue: process.env.PATH,
    shimDir: path.dirname(shimPath),
    extraSkipDirs: [process.env.MULTITERMINAL_GH_SHIM_DIR],
  });

  if (!realGh) {
    warn('Could not find the real gh on PATH. Install GitHub CLI, or set MULTITERMINAL_REAL_GH.');
    process.exit(127);
  }

  if (path.resolve(realGh).toLowerCase() === path.resolve(shimPath).toLowerCase()) {
    // Rule (4). Refusing beats recursing: a loop here would spawn processes until something breaks,
    // and the cause would be invisible in the output.
    warn('Refusing to run: gh resolved to this shim, which would recurse. Check PATH order.');
    process.exit(127);
  }

  let token = null;
  if (hasExplicitToken(process.env)) {
    // Rule (2). Loud, not silent — the plan requires the Owner escape hatch to be visible.
    warn('GH_TOKEN/GITHUB_TOKEN is already set, so gh will use it instead of MultiTerminal\'s bot identity.');
  } else {
    token = await mintInstallationToken({
      nonce: process.env.MULTITERMINAL_LAUNCH_NONCE,
      installationId: process.env.MULTITERMINAL_GITHUB_INSTALLATION_ID || null,
      apiUrl: MT_API_URL,
      timeoutMs: MINT_TIMEOUT_MS,
      warn,
    });

    // Rule (1): no token is not an error. gh runs exactly as it would have without this shim.
    if (!token) warn('Running gh without a MultiTerminal token.');
  }

  // Rule (5). Signing is deliberately the LAST thing before the spawn: the token path above must be
  // unaffected by it, and any throw here would cost the agent a working gh for the sake of a
  // signature, so the whole rewrite is wrapped rather than trusted.
  let childArgs = process.argv.slice(2);
  let signedTempDir = null;
  if (isSigningEnabled(process.env, warn)) {
    try {
      const rewritten = transformArgs(childArgs, process.env.MULTITERMINAL_NAME, { warn });
      childArgs = rewritten.args;
      signedTempDir = rewritten.tempDir;
    } catch (err) {
      warn(`Could not sign the comment body (${err?.message ?? 'unknown'}); sending it unsigned.`);
    }
  }

  const cleanup = () => {
    if (!signedTempDir) return;
    try {
      fs.rmSync(signedTempDir, { recursive: true, force: true });
    } catch {
      // A leftover file in the OS temp directory is not worth failing or warning over; it holds a
      // comment body, not a credential.
    }
    signedTempDir = null;
  };

  const child = spawn(realGh, childArgs, {
    env: buildChildEnv(process.env, token),
    stdio: 'inherit',
  });

  child.on('error', (err) => {
    cleanup();
    warn(`Could not start gh: ${err?.message ?? 'unknown error'}`);
    process.exit(127);
  });

  // Rule (3). A killed gh must not look like a clean exit to whatever is scripting this. Node
  // reports the signal by NAME, not by number, so the shell convention of 128+signum is not
  // available here without a lookup table that would be wrong on Windows anyway — a killed child
  // becomes a plain failure, with the signal named on stderr so the cause is not lost.
  child.on('exit', (code, signal) => {
    cleanup();
    if (signal) {
      warn(`gh was terminated by ${signal}.`);
      process.exit(1);
    }
    process.exit(code ?? 1);
  });
}

const invokedDirectly = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;

if (invokedDirectly) {
  main().catch((err) => {
    warn(`Unexpected failure: ${err?.message ?? 'unknown'}`);
    process.exit(1);
  });
}
