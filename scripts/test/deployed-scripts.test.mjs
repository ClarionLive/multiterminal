// deployed-scripts.test.mjs — every module the bot-identity scripts import must be staged into a
// deployed MultiTerminal (task b42b1883, item 7).
//
// WHY THIS EXISTS AS A TEST RATHER THAN A NOTE. MultiTerminal.csproj lists these files ONE BY ONE.
// A new module under scripts/lib that the shim imports, but that nobody remembers to add there, is
// absent from the Deploy folder — and the symptom is not "comments are unsigned". It is an
// unresolved import that makes the shim exit non-zero, which means EVERY `gh` command in EVERY
// terminal fails, in the deployed build only, with nothing failing in the repo, in the tests, or in
// CI. Item 7 hit exactly this and caught it by reading the csproj; that catch should not depend on
// somebody thinking to look.
//
// This is the same lesson as the IZoomableTab conversion (task 0d72698a) recorded in
// .claude/rules/checklist-graph.md: when a per-item obligation is maintained as a hand-written list,
// prefer a check that fails when the list is incomplete over trusting everyone to update it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = fileURLToPath(new URL('../../', import.meta.url));
const SCRIPTS_DIR = path.join(REPO_ROOT, 'scripts');
const CSPROJ = path.join(REPO_ROOT, 'MultiTerminal.csproj');

/** The scripts MT hands to terminals; these are the roots of the dependency walk. */
const DEPLOYED_ENTRY_POINTS = [
  'gh-multiterminal.mjs',
  'git-credential-multiterminal.mjs',
];

/** Relative specifiers an entry point imports, resolved to repo-relative POSIX paths. */
function localImportsOf(file) {
  const source = fs.readFileSync(path.join(SCRIPTS_DIR, file), 'utf8');
  const found = [];
  for (const m of source.matchAll(/^\s*import\s[^'"]*from\s+['"](\.[^'"]+)['"]/gm)) {
    const resolved = path.resolve(SCRIPTS_DIR, path.dirname(file), m[1]);
    found.push(path.relative(REPO_ROOT, resolved).split(path.sep).join('/'));
  }
  return found;
}

test('the entry points actually import something, so a silent regex miss cannot pass this suite', () => {
  // Without this, a change to the import syntax would make localImportsOf return [] and every
  // assertion below would vacuously succeed.
  const all = DEPLOYED_ENTRY_POINTS.flatMap(localImportsOf);
  assert.ok(all.length >= 2, `expected local imports, found ${JSON.stringify(all)}`);
});

test('every module the deployed scripts import is itself on disk', () => {
  for (const entry of DEPLOYED_ENTRY_POINTS) {
    for (const dep of localImportsOf(entry)) {
      assert.ok(fs.existsSync(path.join(REPO_ROOT, dep)), `${entry} imports missing file ${dep}`);
    }
  }
});

test('every module the deployed scripts import is staged by MultiTerminal.csproj', () => {
  const csproj = fs.readFileSync(CSPROJ, 'utf8');

  for (const entry of DEPLOYED_ENTRY_POINTS) {
    const windowsPath = `scripts\\${entry}`;
    assert.ok(
      csproj.includes(`<Content Include="${windowsPath}">`),
      `MultiTerminal.csproj does not stage the entry point ${windowsPath}`,
    );

    for (const dep of localImportsOf(entry)) {
      const staged = dep.split('/').join('\\');
      assert.ok(
        csproj.includes(`<Content Include="${staged}">`),
        `MultiTerminal.csproj does not stage ${staged}, which ${entry} imports. A deployed `
        + 'MultiTerminal would fail to load the shim and every gh command would break.',
      );
    }
  }
});
