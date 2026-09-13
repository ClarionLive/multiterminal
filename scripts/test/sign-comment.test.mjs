// sign-comment.test.mjs — falsifiable facts for scripts/lib/sign-comment.mjs and the shim wiring
// (task b42b1883, item 7).
//
// THE OBSERVATION POINT MATTERS. The integration tests read the arguments the CHILD PROCESS actually
// received, not the array the shim computed. Asserting on the shim's own return value would prove
// the rewrite happened in JavaScript and prove nothing about what crosses the process boundary —
// which on Windows is where a non-ASCII signature would be lost if it were going to be lost.
//
// WHAT IS DELIBERATELY NOT CLAIMED. There is no test asserting that every published comment is
// signed, because that is false: `gh api`, an editor-driven body and a body piped on stdin all
// publish unsigned by design. `gh api is left alone` and the two "runs unsigned" tests pin those
// holes OPEN on purpose, so a later change that quietly "fixes" them has to argue with a test
// instead of slipping through.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  signBody,
  signatureFor,
  findAuthoringSubcommand,
  findFlag,
  isSigningEnabled,
  transformArgs,
} from '../lib/sign-comment.mjs';

const SHIM = fileURLToPath(new URL('../gh-multiterminal.mjs', import.meta.url));

// WHY THE STAND-IN IS AN --import HOOK AND NOT A SCRIPT ARGUMENT.
//
// The obvious harness — point MULTITERMINAL_REAL_GH at node and pass a fake-gh script as the first
// argument — SHIFTS EVERY ARGUMENT BY ONE, so the child's argv is `[fake-gh.mjs, issue, comment…]`
// rather than `[issue, comment…]`. That is invisible when testing environment variables and exit
// codes (which is why item 5's suite can do it), but it is fatal here: the subcommand this feature
// keys off is no longer in first position, so NOTHING would ever be signed and the tests would be
// measuring an artefact of the harness.
//
// Windows cannot execute a bare script, so the stand-in must still be node. Instead of occupying an
// argument slot, it is loaded through NODE_OPTIONS=--import: the hook runs BEFORE node resolves its
// main module, prints the argv, and exits — so node never gets as far as complaining that `issue` is
// not a file, and process.argv.slice(1) is exactly the argument vector gh would have received.
//
// The hook is handed to the shim and the child alike (they share one environment block), so it
// distinguishes them the only way available: the shim's argv[1] is a real file on disk, the fake
// gh's is a subcommand word that is not.
const STAND_IN = (() => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mt-gh-sign-test-'));
  const file = path.join(dir, 'stand-in-gh.mjs');
  fs.writeFileSync(file, `
    import path from 'node:path';
    const first = process.argv[1];
    // Keyed on the extension, NOT on existsSync: node resolves the subcommand against the cwd, and
    // the api subcommand then resolves onto this repository own API directory, which exists. A gh
    // subcommand is never a .mjs file, so this cannot collide.
    const weAreTheShim = Boolean(first) && path.basename(first).endsWith('.mjs');
    if (!weAreTheShim) {
      // node resolves argv[1] against the cwd before anything can observe it, so the subcommand
      // arrives as an absolute path. Only the leaf is the token gh was handed. Arguments from
      // argv[2] onward are untouched, which is where every assertion about the body lives anyway.
      console.log('ARGS=' + JSON.stringify([path.basename(first), ...process.argv.slice(2)]));
      process.exit(0);
    }
  `, 'utf8');
  return file;
})();

/** Run the shim with no broker reachable, so minting fails and only the argv path is exercised. */
function runShim(args, env = {}) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [SHIM, ...args], {
      env: {
        ...process.env,
        NODE_OPTIONS: `--import ${pathToFileURL(STAND_IN).href}`,
        MULTITERMINAL_REAL_GH: process.execPath,
        MULTITERMINAL_LAUNCH_NONCE: '',
        GH_TOKEN: '',
        GITHUB_TOKEN: '',
        // Unreachable on purpose: this suite is about arguments, not tokens.
        MT_API_URL: 'http://127.0.0.1:1',
        MULTITERMINAL_NAME: 'Alice',
        ...env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d) => { stdout += d; });
    child.stderr.on('data', (d) => { stderr += d; });
    child.on('exit', (code) => {
      const line = stdout.split('\n').find((l) => l.startsWith('ARGS='));
      resolve({ code, stderr, args: line ? JSON.parse(line.slice(5)) : null });
    });
  });
}

/** The child now receives exactly what gh would, so no adjustment is needed. */
function ghArgs(args) {
  return args;
}

// ---------------------------------------------------------------------------
// The signature itself
// ---------------------------------------------------------------------------

test('signature is an em dash and the agent name', () => {
  assert.equal(signatureFor('Alice'), '— Alice');
});

test('no agent name means no signature rather than an empty one', () => {
  assert.equal(signatureFor(''), null);
  assert.equal(signatureFor(undefined), null);
  assert.equal(signatureFor('   '), null);
});

test('signBody appends the signature as its own paragraph', () => {
  assert.equal(signBody('Looks fixed to me.', 'Alice'), 'Looks fixed to me.\n\n— Alice\n');
});

test('signing is idempotent: re-signing the same body changes nothing', () => {
  const once = signBody('Looks fixed to me.', 'Alice');
  assert.equal(signBody(once, 'Alice'), once);
});

test('a body signed by ANOTHER agent still gets ours appended', () => {
  // The point of the feature is per-agent attribution. Suppressing our signature because some other
  // name is already there would publish Alice's post under Charlie's name.
  const relayed = signBody('Charlie wrote this.', 'Charlie');
  const posted = signBody(relayed, 'Alice');
  assert.ok(posted.includes('— Charlie'));
  assert.ok(posted.trimEnd().endsWith('— Alice'));
});

test('trailing whitespace does not defeat the idempotency check', () => {
  assert.equal(signBody('Hi\n\n— Alice\n\n\n', 'Alice'), 'Hi\n\n— Alice\n\n\n');
});

// ---------------------------------------------------------------------------
// Which commands are touched
// ---------------------------------------------------------------------------

test('recognizes the authoring subcommands', () => {
  assert.deepEqual(findAuthoringSubcommand(['issue', 'comment', '29']), ['issue', 'comment']);
  assert.deepEqual(findAuthoringSubcommand(['pr', 'comment', '7']), ['pr', 'comment']);
  assert.deepEqual(findAuthoringSubcommand(['pr', 'review', '--approve']), ['pr', 'review']);
});

test('flags before the positional arguments do not hide the subcommand', () => {
  assert.deepEqual(
    findAuthoringSubcommand(['issue', 'comment', '--body', 'text', '29']),
    ['issue', 'comment'],
  );
});

test('gh api is NOT an authoring subcommand — this hole is deliberate', () => {
  assert.equal(findAuthoringSubcommand(['api', 'repos/o/r/issues/1/comments', '-f', 'body=hi']), null);
});

test('unrelated read commands are not authoring subcommands', () => {
  assert.equal(findAuthoringSubcommand(['issue', 'list']), null);
  assert.equal(findAuthoringSubcommand(['auth', 'status']), null);
  assert.equal(findAuthoringSubcommand(['--version']), null);
});

// ---------------------------------------------------------------------------
// Flag parsing
// ---------------------------------------------------------------------------

test('finds --body in both spellings', () => {
  assert.equal(findFlag(['--body', 'hi'], ['--body', '-b']).value, 'hi');
  assert.equal(findFlag(['--body=hi'], ['--body', '-b']).value, 'hi');
  assert.equal(findFlag(['-b', 'hi'], ['--body', '-b']).value, 'hi');
});

test('--body-file is never mistaken for --body', () => {
  assert.equal(findFlag(['--body-file', 'x.md'], ['--body', '-b']), null);
});

// ---------------------------------------------------------------------------
// The opt-out
// ---------------------------------------------------------------------------

test('signing is on by default and off only for recognized falsey values', () => {
  assert.equal(isSigningEnabled({}), true);
  for (const v of ['0', 'false', 'off', 'no', 'disabled', 'OFF']) {
    assert.equal(isSigningEnabled({ MULTITERMINAL_GH_SIGN: v }), false, v);
  }
  for (const v of ['1', 'true', 'on', 'yes', 'enabled']) {
    assert.equal(isSigningEnabled({ MULTITERMINAL_GH_SIGN: v }), true, v);
  }
});

test('an unrecognized opt-out value stays ON and says so', () => {
  const warnings = [];
  assert.equal(isSigningEnabled({ MULTITERMINAL_GH_SIGN: 'maybe' }, (m) => warnings.push(m)), true);
  assert.equal(warnings.length, 1);
});

// ---------------------------------------------------------------------------
// transformArgs — the failure modes all fall through UNCHANGED
// ---------------------------------------------------------------------------

test('no agent name leaves the arguments untouched', () => {
  const argv = ['issue', 'comment', '29', '--body', 'hi'];
  const out = transformArgs(argv, '', {});
  assert.deepEqual(out.args, argv);
  assert.equal(out.signed, false);
});

test('a body read from stdin is left alone and warned about', () => {
  const warnings = [];
  const argv = ['issue', 'comment', '29', '--body-file', '-'];
  const out = transformArgs(argv, 'Alice', { warn: (m) => warnings.push(m) });
  assert.deepEqual(out.args, argv);
  assert.match(warnings.join('\n'), /UNSIGNED/);
});

test('an unreadable body file is left for gh to report, not pre-empted', () => {
  const warnings = [];
  const argv = ['issue', 'comment', '29', '--body-file', 'no-such-file.md'];
  const out = transformArgs(argv, 'Alice', {
    readFileText: () => { const e = new Error('nope'); e.code = 'ENOENT'; throw e; },
    warn: (m) => warnings.push(m),
  });
  assert.deepEqual(out.args, argv);
  assert.equal(out.signed, false);
});

test('no body flag at all is left alone and warned about', () => {
  const warnings = [];
  const argv = ['issue', 'comment', '29'];
  const out = transformArgs(argv, 'Alice', { warn: (m) => warnings.push(m) });
  assert.deepEqual(out.args, argv);
  assert.match(warnings.join('\n'), /UNSIGNED/);
});

test('the agent\'s own body file is copied, never edited in place', () => {
  const written = [];
  const argv = ['issue', 'comment', '29', '--body-file', 'original.md'];
  const out = transformArgs(argv, 'Alice', {
    readFileText: () => 'from a file',
    makeTempDir: () => '/tmp/fake',
    writeTempText: (dir, name, text) => { written.push({ dir, name, text }); return `${dir}/${name}`; },
  });

  assert.equal(out.args[4], '/tmp/fake/signed-body.md');
  assert.notEqual(out.args[4], 'original.md');
  assert.equal(written.length, 1);
  assert.ok(written[0].text.trimEnd().endsWith('— Alice'));
});

test('the input array is never mutated', () => {
  const argv = ['issue', 'comment', '29', '--body', 'hi'];
  const copy = [...argv];
  transformArgs(argv, 'Alice', {});
  assert.deepEqual(argv, copy);
});

// ---------------------------------------------------------------------------
// Integration — what the CHILD actually received
// ---------------------------------------------------------------------------

test('the child receives a signed body, em dash intact across the process boundary', async () => {
  const r = await runShim(ghArgs(['issue', 'comment', '29', '--body', 'Looks fixed.']));
  assert.deepEqual(r.args, ['issue', 'comment', '29', '--body', 'Looks fixed.\n\n— Alice\n']);
});

test('the child receives --body=value rewritten in the same spelling', async () => {
  const r = await runShim(ghArgs(['pr', 'comment', '7', '--body=Ship it.']));
  assert.equal(r.args[3], '--body=Ship it.\n\n— Alice\n');
});

test('the signature names the terminal that ran the command, not a fixed string', async () => {
  const r = await runShim(ghArgs(['issue', 'comment', '29', '-b', 'hi']), { MULTITERMINAL_NAME: 'Charlie' });
  assert.equal(r.args[4], 'hi\n\n— Charlie\n');
});

test('gh api reaches the child completely untouched', async () => {
  const raw = ['api', 'repos/o/r/issues/1/comments', '-f', 'body=hi'];
  const r = await runShim(ghArgs(raw));
  assert.deepEqual(r.args, raw);
});

test('a read command reaches the child untouched', async () => {
  const raw = ['issue', 'list', '--limit', '5'];
  const r = await runShim(ghArgs(raw));
  assert.deepEqual(r.args, raw);
});

test('MULTITERMINAL_GH_SIGN=0 passes the body through unsigned', async () => {
  const raw = ['issue', 'comment', '29', '--body', 'Looks fixed.'];
  const r = await runShim(ghArgs(raw), { MULTITERMINAL_GH_SIGN: '0' });
  assert.deepEqual(r.args, raw);
});

test('a real body file is signed into a temp copy and the original is left on disk untouched', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mt-gh-sign-body-'));
  const original = path.join(dir, 'body.md');
  fs.writeFileSync(original, 'From a file.', 'utf8');

  const r = await runShim(ghArgs(['issue', 'comment', '29', '--body-file', original]));

  assert.notEqual(r.args[4], original, 'argv must point at the signed copy, not the original');
  assert.equal(fs.readFileSync(original, 'utf8'), 'From a file.', 'the original must be untouched');
});

test('signing never costs a working gh: the exit code still comes from the child', async () => {
  // A body flag with no value is the kind of malformed command gh itself must reject. The shim has
  // to hand it over rather than fail first.
  const r = await runShim(ghArgs(['issue', 'comment', '29', '--body']));
  assert.equal(r.code, 0, 'the stand-in ran, so the shim did not bail out before spawning');
  assert.deepEqual(r.args, ['issue', 'comment', '29', '--body']);
});

// ---------------------------------------------------------------------------
// WHERE THE REPO FLAG SITS — the shape that shipped unsigned for seven sessions
// ---------------------------------------------------------------------------
//
// MEASURED LIVE 2026-09-13 on ClarionLive/multiterminal#31, before the fix, by posting three real
// comments and reading them back through the API:
//
//   gh issue comment 31 --repo o/r --body ...   ->  posted WITH "— Alice"
//   gh --repo o/r issue comment 31 --body ...   ->  posted WITHOUT it
//   gh -R     o/r issue comment 31 --body ...   ->  posted WITHOUT it
//
// All three authored correctly as clarionlive-agent[bot]. IDENTITY was never the casualty;
// ATTRIBUTION was — silently, with a zero exit code. One shared bot identity with the per-agent
// signature missing is the exact state item 7 exists to prevent, so "it still posted fine" is the
// most dangerous possible reading of that result.
//
// Root cause was findAuthoringSubcommand taking the first two NON-FLAG tokens as the subcommand,
// on the documented premise that a flag value can never precede the subcommand. gh is cobra-based
// and accepts --repo/-R ahead of the subcommand path, so the tokens collected were
// ['owner/name', 'issue'] — a pair that matches nothing, hence "not-an-authoring-subcommand".
//
// ⚠️ WHY THESE ARE transformArgs FACTS AND NOT CHILD-ARGV ONES, STATED SO NOBODY "UPGRADES" THEM.
// The integration harness above points MULTITERMINAL_REAL_GH at node itself. node parses its own
// options first and rejects a leading --repo or -R outright:
//
//     C:\Program Files\nodejs\node.exe: bad option: --repo     (exit 9, verified 2026-09-13)
//
// so the stand-in hook never runs and these shapes CANNOT be expressed against that harness. That is
// a limitation of the observation point, not a reason to believe the shapes are covered elsewhere.
// The end-to-end proof for them is the live GitHub run recorded on checklist item 19 — these facts
// are the regression guard that keeps it from silently rotting, nothing more. If you later give the
// suite a stand-in that is not node, move these up rather than leaving two half-proofs.

for (const [label, argv] of [
  ['--repo before the subcommand', ['--repo', 'o/r', 'issue', 'comment', '31', '--body', 'hi']],
  ['-R before the subcommand', ['-R', 'o/r', 'issue', 'comment', '31', '--body', 'hi']],
  ['--repo=o/r attached, before the subcommand', ['--repo=o/r', 'issue', 'comment', '31', '--body', 'hi']],
  ['the canonical ordering still works', ['issue', 'comment', '31', '--repo', 'o/r', '--body', 'hi']],
]) {
  test(`signs an issue comment when ${label}`, () => {
    const r = transformArgs(argv, 'Alice');
    assert.equal(r.signed, true, `${label}: must be recognised as an authoring command`);
    assert.equal(r.args[r.args.indexOf('--body') + 1], 'hi\n\n— Alice\n');
  });
}

// The defect lives in the SHARED matcher, so every entry in AUTHORING_SUBCOMMANDS inherited it.
// Only issue comment and issue create were measured live; these cover the rest by construction so
// the untested ones are not left resting on the assumption that they behave the same.
for (const [label, argv] of [
  ['pr comment', ['-R', 'o/r', 'pr', 'comment', '7', '--body', 'hi']],
  ['pr create', ['--repo', 'o/r', 'pr', 'create', '--title', 't', '--body', 'hi']],
  ['pr review', ['-R', 'o/r', 'pr', 'review', '7', '--body', 'hi']],
  ['issue create', ['--repo', 'o/r', 'issue', 'create', '--title', 't', '--body', 'hi']],
]) {
  test(`${label} is signed too when the repo flag comes first`, () => {
    const r = transformArgs(argv, 'Alice');
    assert.equal(r.signed, true, `${label}: the matcher is shared, so this shape must work too`);
    assert.equal(r.args[r.args.indexOf('--body') + 1], 'hi\n\n— Alice\n');
  });
}

// Scanning for a matching ADJACENT PAIR anywhere in argv is a wider net than reading the first two
// words, so the widening has to be shown not to catch things it should not. A --body value is ONE
// token however many words it holds, which is why no body text can forge a subcommand; the risk is
// only from separately-passed adjacent tokens, and that is what these pin.
for (const [label, argv] of [
  ['gh api stays out, deliberately', ['api', 'repos/o/r/issues/31/comments']],
  ['flag values that spell a subcommand', ['issue', 'list', '--label', 'issue', '--label', 'comment']],
  ['a body that merely contains the words', ['issue', 'list', '--search', 'issue comment']],
  ['a read-only subcommand', ['issue', 'view', '31', '--repo', 'o/r']],
]) {
  test(`does NOT sign: ${label}`, () => {
    assert.equal(transformArgs(argv, 'Alice').signed, false);
  });
}
