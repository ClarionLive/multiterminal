// owner-escape-hatch.test.mjs — falsifiable facts for the deliberate Owner escape hatch
// (task b42b1883, item 8, agent-side half).
//
// THE ONE TEST THAT CARRIES THE ITEM. "never a silent fallback reachable by accident" is the plan's
// requirement, and the way a hatch like this gets reached by accident is a MISSPELLING. The
// neighbouring convention (sign-comment's isSigningEnabled) treats an unrecognized value as ON,
// which is right for signing and would be catastrophic here: MULTITERMINAL_GH_AS_OWNER=flase would
// publish an agent's words under a real person's name. `an unrecognized value does NOT open the
// hatch` is that requirement in executable form. If it ever goes red because someone "made the
// parsing consistent with signing", the consistency is the bug.
//
// The second non-obvious one is `names the ACTIVE account, not the first one in the users map`.
// gh's hosts.yml lists every account ever authenticated under `users:`, and only a scalar `user:`
// names the live one. A warning that confidently names the WRONG person is worse than one that
// names nobody, because it will be believed.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  OWNER_HATCH_ENV,
  isOwnerHatchRequested,
  readGhActiveAccount,
  ghHostsCandidates,
  parseActiveAccount,
  ownerHatchNotice,
} from '../lib/owner-escape-hatch.mjs';

// A real hosts.yml shape, reduced: two authenticated accounts, one of them active. Taken from the
// actual file on the dev machine (2026-09-12), where `peterparker57` sorts FIRST in the users map
// and `ClarionLive` is the active account — so a parser that grabbed the wrong one would name the
// admin-scoped account as the publisher.
const REAL_HOSTS_YML = `github.com:
    users:
        peterparker57:
            oauth_token: gho_REDACTED
        ClarionLive:
            oauth_token: gho_REDACTED
    git_protocol: https
    user: ClarionLive
`;

// ─── the accident-proofing ────────────────────────────────────────────────────────────────────

test('absent, empty and negative values all leave you as the bot', () => {
  for (const raw of [undefined, '', '0', 'false', 'off', 'no', 'disabled', 'FALSE', '  off  ']) {
    const env = raw === undefined ? {} : { [OWNER_HATCH_ENV]: raw };
    assert.equal(isOwnerHatchRequested(env), false, `${JSON.stringify(raw)} must not open the hatch`);
  }
});

test('every affirmative spelling opens the hatch', () => {
  for (const raw of ['1', 'true', 'on', 'yes', 'enabled', 'TRUE', ' Yes ']) {
    assert.equal(isOwnerHatchRequested({ [OWNER_HATCH_ENV]: raw }), true, `${raw} must open the hatch`);
  }
});

test('THE accident test: an unrecognized value does NOT open the hatch, and says so', () => {
  // This is the polarity that differs from isSigningEnabled ON PURPOSE. A typo must never be the
  // thing that publishes an agent's words under a person's name.
  const warnings = [];
  assert.equal(isOwnerHatchRequested({ [OWNER_HATCH_ENV]: 'flase' }, (m) => warnings.push(m)), false);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /not recognized/i);
  assert.match(warnings[0], /still the bot/i);
});

test('an unrecognized value names the spellings that would have worked', () => {
  const warnings = [];
  isOwnerHatchRequested({ [OWNER_HATCH_ENV]: 'owner' }, (m) => warnings.push(m));
  assert.match(warnings[0], /1\/true\/on\/yes\/enabled/);
});

test('a recognized value warns about nothing — the hatch is not nagware', () => {
  const warnings = [];
  isOwnerHatchRequested({ [OWNER_HATCH_ENV]: '1' }, (m) => warnings.push(m));
  isOwnerHatchRequested({ [OWNER_HATCH_ENV]: '0' }, (m) => warnings.push(m));
  assert.deepEqual(warnings, []);
});

// ─── naming the account ───────────────────────────────────────────────────────────────────────

test('names the ACTIVE account, not the first one in the users map', () => {
  assert.equal(parseActiveAccount(REAL_HOSTS_YML), 'ClarionLive');
});

test('a hosts.yml with no active account yields null rather than a guess', () => {
  assert.equal(parseActiveAccount('github.com:\n    users:\n        someone:\n'), null);
  assert.equal(parseActiveAccount(''), null);
  assert.equal(parseActiveAccount(undefined), null);
});

test('readGhActiveAccount reads the first candidate that exists', () => {
  const account = readGhActiveAccount({
    env: { APPDATA: 'C:/AppData' },
    readFileText: (p) => {
      assert.equal(p, 'C:/AppData/GitHub CLI/hosts.yml');
      return REAL_HOSTS_YML;
    },
  });
  assert.equal(account, 'ClarionLive');
});

test('an unreadable config degrades to null instead of throwing', () => {
  // This runs on the path to every hatched command. Throwing here would break the command for the
  // sake of decorating a warning.
  const account = readGhActiveAccount({
    env: { APPDATA: 'C:/AppData' },
    readFileText: () => { throw new Error('ENOENT'); },
  });
  assert.equal(account, null);
});

test('candidate list covers the Windows and XDG locations gh actually uses', () => {
  const c = ghHostsCandidates({ APPDATA: 'C:/A', HOME: '/h', GH_CONFIG_DIR: '/g' });
  assert.equal(c[0], '/g/hosts.yml', 'GH_CONFIG_DIR must win');
  assert.ok(c.includes('C:/A/GitHub CLI/hosts.yml'));
  assert.ok(c.includes('/h/.config/gh/hosts.yml'));
});

// ─── the notice ───────────────────────────────────────────────────────────────────────────────

test('the notice names the account when it is known', () => {
  const lines = ownerHatchNotice('ClarionLive').join('\n');
  assert.match(lines, /'ClarionLive'/);
  assert.match(lines, /a real person/);
  assert.match(lines, /not clarionlive-agent\[bot\]/);
});

test('the notice still states the consequence when the account is unknown', () => {
  // Degrading from naming to describing must not degrade into saying nothing about attribution —
  // that was the exact defect item 11 corrected in the sibling warning.
  const lines = ownerHatchNotice(null).join('\n');
  assert.match(lines, /whichever account gh is signed in to/);
  assert.match(lines, /attributed to/);
});

test('the notice always says how to get back to the bot', () => {
  for (const account of ['ClarionLive', null]) {
    assert.match(ownerHatchNotice(account).join('\n'), new RegExp(`Unset ${OWNER_HATCH_ENV}`));
  }
});

test('the caller can say what is about to happen in its own words', () => {
  const lines = ownerHatchNotice('ClarionLive', 'Commits pushed by this command').join('\n');
  assert.match(lines, /Commits pushed by this command will be attributed to/);
});
