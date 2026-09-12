// git-credential-helper.test.mjs — falsifiable facts for scripts/git-credential-multiterminal.mjs
// (task b42b1883, item 4).
//
// Each test below fails when the guard it covers is removed from the helper — the house rule
// inherited from c9285d2a: revert the fix and watch the test fail, or the test is worth nothing.
//
// THIS MAPPING WAS MEASURED, NOT PREDICTED. Five mutations were applied one at a time, the suite run
// against each, and the failing set recorded (2026-09-11):
//
//   host check -> `return true`      -> "refuses a non-GitHub host", "never contacts MT at all",
//                                       and "accepts github.com:443 but not another port".
//                                       The third is correct collateral: it exercises the same
//                                       function directly. My first write-up of this header claimed
//                                       "nothing else does" and was wrong.
//   https check -> removed           -> "refuses http, even for github.com" ALONE. The port test does
//                                       NOT move, because every case in it already specifies https —
//                                       one guard, one falsifier.
//   empty nonce accepted             -> "stays silent when the terminal has no launch nonce" alone.
//   `store`/`erase` answered as get  -> "store and erase are silent no-ops" alone.
//   token written to stderr          -> "never writes the token to stderr" alone.
//
// No mutation escaped. Note the ceiling recorded on c9285d2a: falsification proves a test
// DISTINGUISHES the guard, not that the guard is RIGHT, and every fixture here tells a one-actor
// story — none of them can catch a defect whose trigger is a second party.
//
// The tests spawn the real helper as a child process against a fake MT, because the protocol
// contract IS the process boundary: stdout framing, exit code and stdin parsing cannot be observed
// by importing a function. The two pure helpers are also imported directly, so a parsing bug is
// reported as a parsing bug rather than as a mysterious empty response.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import http from 'node:http';
import { fileURLToPath } from 'node:url';

import { parseCredentialRequest, isGitHubRequest } from '../git-credential-multiterminal.mjs';

const HELPER = fileURLToPath(new URL('../git-credential-multiterminal.mjs', import.meta.url));

/** A token that is obviously fake but distinctive enough to grep for in stdout AND stderr. */
const FAKE_TOKEN = 'ghs_FAKE_TOKEN_FOR_TESTS_0123456789';

/**
 * A stand-in for MultiTerminal's mint endpoint. Records every request it receives so a test can
 * assert on the ABSENCE of one — "the helper never asked" is a different, stronger claim than "the
 * helper printed nothing".
 */
function startFakeBroker({ status = 200, body = { token: FAKE_TOKEN, terminal: 'TestAgent' } } = {}) {
  const received = [];

  const server = http.createServer((req, res) => {
    let raw = '';
    req.on('data', (c) => { raw += c; });
    req.on('end', () => {
      received.push({
        url: req.url,
        method: req.method,
        nonce: req.headers['x-multiterminal-launch-nonce'] ?? null,
        body: raw,
      });
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(body));
    });
  });

  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      resolve({
        url: `http://127.0.0.1:${port}`,
        received,
        close: () => new Promise((done) => server.close(done)),
      });
    });
  });
}

/** Run the helper exactly as git would: one argument, the request on stdin, output on stdout. */
function runHelper({ operation = 'get', stdin = '', env = {} }) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [HELPER, operation], {
      env: {
        // A bare env keeps a developer's real MULTITERMINAL_LAUNCH_NONCE from leaking into a test
        // and making the no-nonce case pass for the wrong reason.
        PATH: process.env.PATH,
        SystemRoot: process.env.SystemRoot,
        ...env,
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (c) => { stdout += c; });
    child.stderr.on('data', (c) => { stderr += c; });
    child.on('close', (code) => resolve({ stdout, stderr, code }));

    child.stdin.write(stdin);
    child.stdin.end();
  });
}

const githubRequest = 'protocol=https\nhost=github.com\n\n';

test('hands git a minted token for github.com over https', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, code } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(code, 0);
    assert.equal(stdout, `username=x-access-token\npassword=${FAKE_TOKEN}\n\n`);

    // The credential is useless to git without the trailing blank line that ends the response.
    assert.ok(stdout.endsWith('\n\n'), 'response must be terminated by a blank line');

    assert.equal(broker.received.length, 1);
    assert.equal(broker.received[0].method, 'POST');
    assert.equal(broker.received[0].url, '/api/github/token');
    assert.equal(broker.received[0].nonce, 'nonce-abc', 'the nonce travels in the header, not the body');
    assert.ok(!broker.received[0].body.includes('nonce-abc'), 'the nonce must not be in the body');
  } finally {
    await broker.close();
  }
});

test('refuses a non-GitHub host — the exfiltration guard', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, code } = await runHelper({
      stdin: 'protocol=https\nhost=evil.example.com\n\n',
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(stdout, '', 'a token must never be offered to another host');
    assert.equal(code, 0, 'silence, not an error — git falls through to its other helpers');
  } finally {
    await broker.close();
  }
});

test('never contacts MT at all for a non-GitHub host', async () => {
  // Stronger than "printed nothing": a helper that minted first and filtered afterwards would still
  // have created a live credential at GitHub for a request that had no business asking.
  const broker = await startFakeBroker();
  try {
    await runHelper({
      stdin: 'protocol=https\nhost=evil.example.com\n\n',
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(broker.received.length, 0, 'no token should ever be minted for a foreign host');
  } finally {
    await broker.close();
  }
});

test('refuses http, even for github.com', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout } = await runHelper({
      stdin: 'protocol=http\nhost=github.com\n\n',
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(stdout, '', 'a bearer credential must not be handed to a cleartext connection');
    assert.equal(broker.received.length, 0);
  } finally {
    await broker.close();
  }
});

test('stays silent when the terminal has no launch nonce', async () => {
  // The adopted-terminal case: a plain shell that registered itself was never launched by MT and
  // holds no nonce. It cannot mint, and that is correct — not a bug to be fixed by loosening the gate.
  const broker = await startFakeBroker();
  try {
    const { stdout, code } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url },
    });

    assert.equal(stdout, '');
    assert.equal(code, 0);
    assert.equal(broker.received.length, 0, 'no nonce means no request, not an empty-nonce request');
  } finally {
    await broker.close();
  }
});

test('falls through silently when MT refuses the nonce (401)', async () => {
  const broker = await startFakeBroker({ status: 401, body: { error: 'bad nonce' } });
  try {
    const { stdout, code } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'wrong' },
    });

    assert.equal(stdout, '', 'a partial answer would make git try an empty password');
    assert.equal(code, 0, 'a non-zero exit makes git treat the helper as broken');
  } finally {
    await broker.close();
  }
});

test('falls through silently when MT cannot mint (503)', async () => {
  // ⚠️ This WAS "the expected state until the Owner completes item 3" and is not any more. The App is
  // registered, so a 503 now means something is genuinely wrong. What stays true is the part this
  // test asserts: git keeps working and simply gets no credential from us. The noise now goes to
  // stderr instead (see the attribution test below), where git cannot be confused by it.
  const broker = await startFakeBroker({ status: 503, body: { error: 'No GitHub App is configured' } });
  try {
    const { stdout, code } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(stdout, '');
    assert.equal(code, 0);
  } finally {
    await broker.close();
  }
});

test('falls through silently when MultiTerminal is not running at all', async () => {
  // Port 1 is reserved and nothing listens there, so this exercises the connection-refused path
  // without depending on a server that is deliberately down.
  const { stdout, code } = await runHelper({
    stdin: githubRequest,
    env: { MT_API_URL: 'http://127.0.0.1:1', MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
  });

  assert.equal(stdout, '');
  assert.equal(code, 0);
});

test('store and erase are silent no-ops', async () => {
  const broker = await startFakeBroker();
  try {
    for (const operation of ['store', 'erase']) {
      const { stdout, code } = await runHelper({
        operation,
        stdin: `${githubRequest}password=${FAKE_TOKEN}\n\n`,
        env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
      });

      assert.equal(stdout, '', `${operation} must produce no output`);
      assert.equal(code, 0);
    }

    assert.equal(broker.received.length, 0, 'nothing is stored, so nothing is minted or erased');
  } finally {
    await broker.close();
  }
});

// ─── Silent to git, audible to the human (item 11, corrected by item 8) ───────────────────
//
// Every test above asserts stdout is empty, and that is the contract WITH GIT: say nothing, exit 0.
//
// ⚠️ THIS COMMENT USED TO SAY the helpers git falls back to "hold the Owner's own credentials — so
// falling through does not fail the push, it succeeds under their name". That is FALSE, and the same
// false sentence had been copied into the source file it describes. MEASURED in an MT-launched
// terminal (item 8): the effective helper list for github.com is
//     manager / "" / !gh auth git-credential / "" / !node git-credential-multiterminal.mjs
// and git treats an empty value as "reset the list". Item 6's clear-then-set is the second reset, so
// OURS IS THE ONLY HELPER LEFT — deliberately, because leaving the Owner's helper in the list would
// have agents pushing as the Owner while appearing to work.
//
// So declining does not misattribute the push; it leaves git with nothing and the push PROMPTS or
// fails. Both deserve a warning, but they call for opposite next actions, which is why the wording
// is asserted here rather than left to drift.
test('a failed mint warns that git has no other helper to fall back to', async () => {
  const broker = await startFakeBroker({ status: 503, body: { error: 'no installation' } });
  try {
    const { stdout, stderr, code } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    // Unchanged where it matters to git.
    assert.equal(stdout, '', 'stderr diagnostics must not turn into a partial answer on stdout');
    assert.equal(code, 0, 'a non-zero exit makes git treat the helper as broken');

    assert.match(stderr, /ONLY credential helper/i, 'say that nothing else will answer');
    assert.match(stderr, /NOT a push that silently went out/i,
      'and rule out the failure mode a reader would otherwise assume');
  } finally {
    await broker.close();
  }
});

// ─── The deliberate Owner escape hatch reaches git too (item 8) ───────────────────────────
//
// One variable governs identity for BOTH tools. A hatch that covered `gh issue comment` but silently
// left `git push` acting as the bot would be more confusing than no hatch at all.
test('the hatch declines to mint, loudly, and names the way through', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr, code } = await runHelper({
      stdin: githubRequest,
      env: {
        MT_API_URL: broker.url,
        MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc',
        MULTITERMINAL_GH_AS_OWNER: '1',
      },
    });

    assert.equal(stdout, '', 'the hatch must hand git no bot credential');
    assert.equal(code, 0);
    assert.equal(broker.received.length, 0, 'and must not mint one either');
    assert.match(stderr, /acting as the OWNER/i);
    assert.match(stderr, /git -c credential/i, 'a hatch that only says "no" is not a way through');
  } finally {
    await broker.close();
  }
});

test('THE accident test: a misspelled hatch value still mints the bot token for git', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr } = await runHelper({
      stdin: githubRequest,
      env: {
        MT_API_URL: broker.url,
        MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc',
        MULTITERMINAL_GH_AS_OWNER: 'flase',
      },
    });

    assert.match(stdout, /^username=x-access-token\n/, 'a typo must leave you as the bot');
    assert.equal(broker.received.length, 1);
    assert.match(stderr, /not recognized/i);
  } finally {
    await broker.close();
  }
});

test('a non-GitHub host stays completely silent, warning included', async () => {
  // The warning belongs to "we could not act as the bot for a request that was ours". A push to some
  // other host was never ours to answer, and warning there would train the reader to ignore it.
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr } = await runHelper({
      stdin: 'protocol=https\nhost=gitlab.com\n\n',
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.equal(stdout, '');
    assert.equal(stderr, '', 'a host we never claimed must produce no diagnostics at all');
  } finally {
    await broker.close();
  }
});

test('never writes the token to stderr', async () => {
  const broker = await startFakeBroker();
  try {
    const { stdout, stderr } = await runHelper({
      stdin: githubRequest,
      env: { MT_API_URL: broker.url, MULTITERMINAL_LAUNCH_NONCE: 'nonce-abc' },
    });

    assert.ok(stdout.includes(FAKE_TOKEN), 'precondition: the token did reach stdout');
    assert.ok(!stderr.includes(FAKE_TOKEN), 'diagnostics must never carry the credential');
    assert.ok(!stderr.includes('nonce-abc'), 'diagnostics must never carry the nonce either');
  } finally {
    await broker.close();
  }
});

test('accepts github.com:443 but not another port', () => {
  assert.equal(isGitHubRequest({ protocol: 'https', host: 'github.com' }), true);
  assert.equal(isGitHubRequest({ protocol: 'https', host: 'github.com:443' }), true);
  assert.equal(isGitHubRequest({ protocol: 'https', host: 'GitHub.COM' }), true, 'hosts are case-insensitive');

  assert.equal(isGitHubRequest({ protocol: 'https', host: 'github.com:8443' }), false);
  assert.equal(isGitHubRequest({ protocol: 'https', host: 'github.com.evil.test' }), false);
  assert.equal(isGitHubRequest({ protocol: 'https', host: 'notgithub.com' }), false);
  assert.equal(isGitHubRequest({ protocol: 'https', host: '' }), false);
  assert.equal(isGitHubRequest({}), false);
});

test('parses git request blocks, ignoring junk rather than throwing', () => {
  assert.deepEqual(
    parseCredentialRequest('protocol=https\nhost=github.com\npath=Owner/repo.git\n\n'),
    { protocol: 'https', host: 'github.com', path: 'Owner/repo.git' },
  );

  // A value containing '=' must survive intact: git sends wwwauth[] headers and paths that carry it.
  assert.equal(parseCredentialRequest('url=https://x/y?a=b\n').url, 'https://x/y?a=b');

  // Lines with no '=', and a leading '=' with no key, are ignored — an unparseable request has to
  // degrade to "cannot help", never to a crash that git reports as a failed operation.
  assert.deepEqual(parseCredentialRequest('garbage\n=novalue\n\n'), {});
  assert.deepEqual(parseCredentialRequest(''), {});
  assert.deepEqual(parseCredentialRequest(null), {});

  // CRLF is what git sends on Windows, which is the platform this ships on.
  assert.deepEqual(parseCredentialRequest('protocol=https\r\nhost=github.com\r\n\r\n'),
    { protocol: 'https', host: 'github.com' });
});
