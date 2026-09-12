// gh-shim.test.mjs — falsifiable facts for scripts/gh-multiterminal.mjs (task b42b1883, item 5).
//
// The integration tests point MULTITERMINAL_REAL_GH at node itself and pass a stand-in script as the
// first argument, so the shim spawns a real child process with a real environment block and a real
// exit code — the three things this shim exists to get right — without needing a gh binary installed.
// The stand-in reports whether it was handed a token, which is the observation that matters: asking
// the shim what it *intended* would prove nothing about what the child actually received.
//
// GUARD-TO-TEST MAPPING — MEASURED BY MUTATION, 2026-09-11, not predicted:
//
//   exit instead of running gh on a
//   failed mint                         -> all THREE "runs gh anyway" tests fail (no token, MT down,
//                                          no nonce). One guard, three situations that reach it.
//   swallow the child's exit code       -> "propagates the child exit code" alone.
//   drop the self-exec guard            -> "refuses to exec itself rather than recursing" alone.
//
//   THE EXISTING-TOKEN RULE HAS TWO INDEPENDENT LAYERS, which the mutation run exposed:
//     - main() skips minting entirely when hasExplicitToken() is true, and
//     - buildChildEnv() refuses to add a token when the environment already carries one.
//   Removing ONLY buildChildEnv's guard fails just its unit test — the integration test still passes,
//   because nothing was minted to override with. Only removing BOTH lets a bot token replace an
//   explicit one, and then "never overwrites a token the environment already had" fails too. That is
//   defence in depth rather than redundancy: the escape hatch survives a single careless edit.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { resolveRealGh, buildChildEnv, hasExplicitToken } from '../gh-multiterminal.mjs';

const SHIM = fileURLToPath(new URL('../gh-multiterminal.mjs', import.meta.url));
const FAKE_TOKEN = 'ghs_FAKE_SHIM_TOKEN_0123456789';

/**
 * A stand-in for gh. Prints what it was given so the test can assert on the CHILD's view, then exits
 * with whatever code the test asked for.
 */
const FAKE_GH = (() => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mt-gh-shim-'));
  const file = path.join(dir, 'fake-gh.mjs');
  fs.writeFileSync(file, `
    const token = process.env.GH_TOKEN ?? '(none)';
    console.log('GH_TOKEN=' + token);
    console.log('ARGS=' + JSON.stringify(process.argv.slice(2)));
    process.exit(Number(process.env.FAKE_GH_EXIT ?? 0));
  `);
  return file;
})();

function startFakeBroker({ status = 200, body = { token: FAKE_TOKEN, terminal: 'TestAgent' } } = {}) {
  const received = [];
  const server = http.createServer((req, res) => {
    // The body is not asserted on here (item 4's suite covers its shape), but it must be drained or
    // 'end' never fires and the shim waits out its timeout.
    req.resume();
    req.on('end', () => {
      received.push({ nonce: req.headers['x-multiterminal-launch-nonce'] ?? null });
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(body));
    });
  });

  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve({
      url: `http://127.0.0.1:${server.address().port}`,
      received,
      close: () => new Promise((done) => server.close(done)),
    }));
  });
}

/** Run the shim as a terminal would, with the fake gh standing in for the real one. */
function runShim({ args = ['issue', 'comment'], env = {}, realGh = process.execPath } = {}) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [SHIM, FAKE_GH, ...args], {
      env: {
        PATH: process.env.PATH,
        SystemRoot: process.env.SystemRoot,
        MULTITERMINAL_REAL_GH: realGh,
        ...env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (c) => { stdout += c; });
    child.stderr.on('data', (c) => { stderr += c; });
    child.on('close', (code) => resolve({ stdout, stderr, code }));
  });
}

// ─── The fallback says what it will DO, not only what failed (item 11) ─────────────────────
//
// Owner ruling 2026-09-12: warn and continue, do not refuse. The CONTINUE half was already true and
// is covered by the three "runs gh anyway" tests above — these do not re-prove it, they pin the WARN
// half, which was the part that was failing its job. The old text said "Running gh without a
// MultiTerminal token.": true, and silent about the thing a reader needs, which is that the next
// comment will carry their own name.

test('a failed mint warns about the ATTRIBUTION, not just the missing token', async () => {
  const broker = await startFakeBroker({ status: 503, body: { error: 'no installation' } });
  try {
    const { stdout, stderr, code } = await runShim({
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    // The consequence, in words someone who has never read this ticket can act on.
    assert.match(stderr, /attributed/i, 'the warning must name what happens, not only what failed');
    assert.match(stderr, /not to the bot/i, 'it must be explicit about which identity is NOT used');
    assert.match(stderr, /signed in to/i, 'and about where the other identity comes from');

    // Rule (1) is untouched: gh still ran, with no token, exactly as it would have without the shim.
    assert.match(stdout, /GH_TOKEN=\(none\)/);
    assert.equal(code, 0);
  } finally {
    await broker.close();
  }
});

test('the attribution warning also fires for a terminal that holds no nonce', async () => {
  // Reached through a completely different branch — mintInstallationToken refuses before any request
  // — so a warning added only to the HTTP-failure path would pass the test above and leave an adopted
  // terminal posting as the Owner with no notice at all.
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr } = await runShim({ env: { MT_API_URL: broker.url } });

    assert.equal(broker.received.length, 0, 'no nonce must still mean no request');
    assert.match(stderr, /attributed/i);
    assert.match(stderr, /not to the bot/i);
    assert.match(stdout, /GH_TOKEN=\(none\)/);
  } finally {
    await broker.close();
  }
});

test('the warning never names the identity it could not get a token for', async () => {
  // A diagnostic is not a place to start printing installation ids or nonces back at the user.
  const broker = await startFakeBroker({ status: 503, body: { error: 'no installation' } });
  try {
    const { stderr } = await runShim({
      env: {
        MT_API_URL: broker.url,
        MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc',
        MULTITERMINAL_GITHUB_INSTALLATION_ID: '161180702',
      },
    });

    assert.ok(!stderr.includes('nonce-abc'), 'the nonce must never reach stderr');
    assert.ok(!stderr.includes('161180702'), 'the installation id is not diagnostic, it is noise');
  } finally {
    await broker.close();
  }
});

test('hands the minted token to gh, and only to gh', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr, code } = await runShim({
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(code, 0);
    assert.ok(stdout.includes(`GH_TOKEN=${FAKE_TOKEN}`), 'the child must receive the token');
    assert.equal(broker.received[0].nonce, 'nonce-abc');

    // The shim's own output must never carry the credential.
    assert.ok(!stderr.includes(FAKE_TOKEN), 'the token must not appear in diagnostics');
  } finally {
    await broker.close();
  }
});

test('passes arguments through verbatim, including ones with spaces', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout } = await runShim({
      args: ['issue', 'comment', '42', '--body', 'two words'],
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.ok(
      stdout.includes(JSON.stringify(['issue', 'comment', '42', '--body', 'two words'])),
      'gh must see exactly the arguments the caller typed',
    );
  } finally {
    await broker.close();
  }
});

test('runs gh anyway when no token can be minted', async () => {
  // 503 is the CORRECT state until the Owner completes item 3, so this is the everyday path today.
  const broker = await startFakeBroker({ status: 503, body: { error: 'No GitHub App is configured' } });
  try {
    const { stdout, code } = await runShim({
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(code, 0, 'a mint failure must not break gh');
    assert.ok(stdout.includes('GH_TOKEN=(none)'), 'gh runs with the environment it would have had');
  } finally {
    await broker.close();
  }
});

test('runs gh anyway when MultiTerminal is not running', async () => {
  const { stdout, code } = await runShim({
    env: { MT_API_URL: 'http://127.0.0.1:1', MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
  });

  assert.equal(code, 0);
  assert.ok(stdout.includes('GH_TOKEN=(none)'));
});

test('runs gh anyway in a terminal with no launch nonce', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, code } = await runShim({ env: { MT_API_URL: broker.url } });

    assert.equal(code, 0);
    assert.ok(stdout.includes('GH_TOKEN=(none)'));
    assert.equal(broker.received.length, 0, 'no nonce means no mint request at all');
  } finally {
    await broker.close();
  }
});

test('never overwrites a token the environment already had', async () => {
  // The Owner escape hatch: a token set deliberately must survive, and the shim must say so rather
  // than silently swapping identities.
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr } = await runShim({
      env: {
        MT_API_URL: broker.url,
        MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc',
        GH_TOKEN: 'explicit-token-from-the-owner',
      },
    });

    assert.ok(stdout.includes('GH_TOKEN=explicit-token-from-the-owner'), 'the explicit token wins');
    assert.ok(!stdout.includes(FAKE_TOKEN), 'the bot token must not replace it');
    assert.equal(broker.received.length, 0, 'and nothing should be minted at all');
    assert.match(stderr, /already set/i, 'the override must be visible, not silent');
  } finally {
    await broker.close();
  }
});

test('propagates the child exit code', async () => {
  const broker = await startFakeBroker();
  try {
    const { code } = await runShim({
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc', FAKE_GH_EXIT: '42' },
    });

    assert.equal(code, 42, 'a failed gh must not look like success to a caller that checks');
  } finally {
    await broker.close();
  }
});

test('refuses to exec itself rather than recursing', async () => {
  const { stderr, code } = await runShim({ realGh: SHIM });

  assert.equal(code, 127);
  assert.match(stderr, /recurse/i);
});

test('reports a missing gh instead of failing silently', async () => {
  const child = spawn(process.execPath, [SHIM, 'issue'], {
    env: { PATH: '', SystemRoot: process.env.SystemRoot },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let stderr = '';
  child.stderr.on('data', (c) => { stderr += c; });
  const code = await new Promise((resolve) => child.on('close', resolve));

  assert.equal(code, 127);
  assert.match(stderr, /could not find the real gh/i);
});

test('resolveRealGh skips the shim directory and honours an override', () => {
  const shimDir = path.join('C:', 'mt', 'shims');
  const realDir = path.join('C:', 'Program Files', 'GitHub CLI');
  const pathValue = [shimDir, realDir].join(path.delimiter);
  const exists = (p) => p.toLowerCase().endsWith('gh.exe');

  // Without the skip, the shim in the first PATH entry would be chosen and recurse.
  assert.equal(
    resolveRealGh({ override: null, pathValue, shimDir, exists }),
    path.join(realDir, 'gh.exe'),
  );

  assert.equal(
    resolveRealGh({ override: 'D:\\explicit\\gh.exe', pathValue, shimDir, exists }),
    'D:\\explicit\\gh.exe',
    'an explicit override wins over any PATH search',
  );

  assert.equal(
    resolveRealGh({ override: null, pathValue: shimDir, shimDir, exists }),
    null,
    'when the only candidate is the shim itself, report nothing rather than recurse',
  );
});

test('skips the launcher directory MT puts first on PATH', () => {
  // THE REAL RECURSION CASE. This .mjs lives in scripts/, but what is on PATH under the name `gh` is
  // a launcher in MultiTerminal's shims directory — a different folder. Skipping only our own would
  // find that launcher, which runs this file again. MT passes it as MULTITERMINAL_GH_SHIM_DIR.
  const scriptsDir = path.join('C:', 'app', 'scripts');
  const launcherDir = path.join('C:', 'Users', 'x', 'AppData', 'Roaming', 'multiterminal', 'shims');
  const realDir = path.join('C:', 'Program Files', 'GitHub CLI');

  // Mirror the real layout: MT writes only gh.cmd into the launcher directory, and GitHub CLI
  // installs gh.exe. A predicate that claimed both existed everywhere would test a world that
  // cannot happen.
  const exists = (p) => {
    const lower = p.toLowerCase();
    if (lower.startsWith(launcherDir.toLowerCase())) return lower.endsWith('gh.cmd');
    if (lower.startsWith(realDir.toLowerCase())) return lower.endsWith('gh.exe');
    return false;
  };

  assert.equal(
    resolveRealGh({
      override: null,
      pathValue: [launcherDir, realDir].join(path.delimiter),
      shimDir: scriptsDir,
      extraSkipDirs: [launcherDir],
      exists,
    }),
    path.join(realDir, 'gh.exe'),
    'the real gh must win over the launcher that shadows it',
  );

  // Without the extra skip, the launcher is what gets found — the bug this guards.
  assert.equal(
    resolveRealGh({
      override: null,
      pathValue: [launcherDir, realDir].join(path.delimiter),
      shimDir: scriptsDir,
      exists,
    }),
    path.join(launcherDir, 'gh.cmd'),
    'demonstrates what happens without the skip: the launcher resolves to itself',
  );

  // A null/undefined entry must not blow up the resolver.
  assert.equal(
    resolveRealGh({
      override: null,
      pathValue: realDir,
      shimDir: scriptsDir,
      extraSkipDirs: [undefined, null],
      exists,
    }),
    path.join(realDir, 'gh.exe'),
  );
});

test('buildChildEnv adds the token only when the environment has none', () => {
  assert.equal(buildChildEnv({ A: '1' }, 'tok').GH_TOKEN, 'tok');
  assert.equal(buildChildEnv({ GH_TOKEN: 'mine' }, 'tok').GH_TOKEN, 'mine');
  assert.equal(buildChildEnv({ GITHUB_TOKEN: 'mine' }, 'tok').GH_TOKEN, undefined,
    'gh falls back to GITHUB_TOKEN, so that counts as a deliberate choice too');
  assert.equal(buildChildEnv({ A: '1' }, null).GH_TOKEN, undefined);

  // The parent environment must not be mutated: this process keeps running after the spawn.
  const parent = { A: '1' };
  buildChildEnv(parent, 'tok');
  assert.equal(parent.GH_TOKEN, undefined);

  assert.equal(hasExplicitToken({ GH_TOKEN: 'x' }), true);
  assert.equal(hasExplicitToken({ GITHUB_TOKEN: 'x' }), true);
  assert.equal(hasExplicitToken({}), false);
});
