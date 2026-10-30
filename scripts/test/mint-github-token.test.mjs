// mint-github-token.test.mjs — GH #68: "no App configured" is reported calmly, every other mint
// failure stays loud and carries MT's own explanation.
//
// POST /api/github/token answers 503 for BOTH "no App configured" (the normal state of an install that
// never registered one) and real failures (GitHubAppController.MintToken maps every
// InvalidOperationException to 503). The only thing telling them apart is the server's error text, so
// the last fact here pins that text against the C# that produces it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

import {
  mintInstallationToken,
  readableMintError,
  NOT_CONFIGURED_PREFIX,
  NOT_CONFIGURED_NOTICE,
} from '../lib/mint-github-token.mjs';

const NOT_CONFIGURED_ERROR = 'No GitHub App is configured (missing app id or private key). Register the App first.';

function startFakeBroker({ status, body }) {
  const server = http.createServer((req, res) => {
    req.resume();
    req.on('end', () => {
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(typeof body === 'string' ? body : JSON.stringify(body));
    });
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve({
      url: `http://127.0.0.1:${server.address().port}`,
      close: () => new Promise((done) => server.close(done)),
    }));
  });
}

async function mintAgainst({ status, body, onNotConfigured }) {
  const broker = await startFakeBroker({ status, body });
  const warnings = [];
  try {
    const token = await mintInstallationToken({
      nonce: 'nonce-abc',
      apiUrl: broker.url,
      warn: (m) => warnings.push(m),
      ...(onNotConfigured ? { onNotConfigured } : {}),
    });
    return { token, warnings };
  } finally {
    await broker.close();
  }
}

test('no App configured: one neutral line, never "refused"', async () => {
  const { token, warnings } = await mintAgainst({ status: 503, body: { error: NOT_CONFIGURED_ERROR } });

  assert.equal(token, null);
  assert.deepEqual(warnings, [NOT_CONFIGURED_NOTICE]);
  assert.doesNotMatch(NOT_CONFIGURED_NOTICE, /refused|503|token|mint|GitHub App/i,
    'the notice is for someone who never registered an App: no fault words, no implementation terms');
});

test('no App configured: a caller-supplied onNotConfigured replaces the default line', async () => {
  let called = 0;
  const { token, warnings } = await mintAgainst({
    status: 503,
    body: { error: NOT_CONFIGURED_ERROR },
    onNotConfigured: () => { called += 1; },
  });

  assert.equal(token, null);
  assert.equal(called, 1);
  assert.deepEqual(warnings, [], 'the caller words it; the library says nothing of its own');
});

test('a real 503 stays loud and carries the server\'s explanation', async () => {
  let called = 0;
  const { token, warnings } = await mintAgainst({
    status: 503,
    body: { error: 'No installation of the App could be resolved.' },
    onNotConfigured: () => { called += 1; },
  });

  assert.equal(token, null);
  assert.equal(called, 0, 'a real fault must not be reported as "not set up"');
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /refused to mint a token \(HTTP 503\)/);
  assert.match(warnings[0], /No installation of the App could be resolved\./);
});

test('the not-configured text on another status is still a fault', async () => {
  // The calm path is the controller's 503 for that one message, not the text alone.
  const { warnings } = await mintAgainst({ status: 500, body: { error: NOT_CONFIGURED_ERROR } });

  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /refused to mint a token \(HTTP 500\)/);
});

test('a failure with no readable body still reports the status', async () => {
  const { warnings } = await mintAgainst({ status: 503, body: '<html>proxy error</html>' });

  assert.deepEqual(warnings, ['MultiTerminal refused to mint a token (HTTP 503).']);
});

test('the server text is flattened to one printable line and capped', () => {
  const hostile = JSON.stringify({ error: `bad\u001b[31m\r\nthing‮${'x'.repeat(1000)}` });
  const shown = readableMintError(hostile);

  // eslint-disable-next-line no-control-regex
  assert.doesNotMatch(shown, /[\u0000-\u001f\u007f-\u009f‪-‮]/, 'no control or bidi characters');
  assert.ok(shown.startsWith('bad [31m thing'), `got ${JSON.stringify(shown.slice(0, 20))}`);
  assert.ok(shown.length <= 301, `capped, got ${shown.length}`);
  assert.equal(readableMintError('not json'), '');
  assert.equal(readableMintError(JSON.stringify({ message: 'x' })), '');
});

// The prefix the JS matches is written in C#, in a file this suite cannot run. Every literal thrown as
// InvalidOperationException that carries the "missing app id or private key" wording is the
// not-configured message; each must start with NOT_CONFIGURED_PREFIX, or a reworded C# message would
// silently turn every unconfigured install's notice back into "refused". Comments are stripped first so
// a comment quoting the old text cannot satisfy it, and at least one literal must be found so a rename
// cannot make it vacuous.
test('the C# "no App configured" message still starts with the prefix the JS matches', () => {
  const csPath = fileURLToPath(new URL('../../Services/GitHubAppTokenService.cs', import.meta.url));
  const code = fs.readFileSync(csPath, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/.*$/gm, '');

  const thrown = [...code.matchAll(/throw\s+new\s+InvalidOperationException\(\s*"((?:[^"\\]|\\.)*)"/g)]
    .map((m) => m[1]);
  assert.ok(thrown.length > 5, `extraction found only ${thrown.length} thrown literals; did the file move?`);

  const notConfigured = thrown.filter((s) => /missing app id or private key/i.test(s));
  assert.ok(notConfigured.length >= 1, 'no not-configured throw found: if it was reworded, update NOT_CONFIGURED_PREFIX with it');
  for (const message of notConfigured) {
    assert.ok(message.startsWith(NOT_CONFIGURED_PREFIX),
      `C# throws "${message}", which no longer starts with "${NOT_CONFIGURED_PREFIX}"`);
  }
});
