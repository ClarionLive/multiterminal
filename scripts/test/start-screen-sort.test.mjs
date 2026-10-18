// start-screen-sort.test.mjs — Browse All's multi-column sort (ticket e20e2ce0 item 8).
//
// Runs the REAL parseSortSpec / formatSortSpec / nextSortKeys / sortValue / compareBySortKeys, cut out
// of StartScreen/start-screen.html by brace matching (each cut asserted), in node:vm. What this cannot
// see: the header clicks themselves, the Shift key, the order badges, sticky columns or the WebView2
// round trip that stores the sort; those need the running app.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(here, '..', '..');
const PAGE = readFileSync(path.join(root, 'StartScreen', 'start-screen.html'), 'utf8');
const SETTINGS = readFileSync(path.join(root, 'Services', 'SettingsService.cs'), 'utf8');

function extract(source, header) {
  const start = source.indexOf(header);
  assert.ok(start >= 0, `could not find "${header}"`);
  const open = source.indexOf('{', start + header.length - 1);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) {
      const text = source.slice(start, i + 1);
      assert.ok(text.length > 40, `extraction of "${header}" looks truncated`);
      return text;
    }
  }
  assert.fail(`unbalanced braces after "${header}"`);
}

// One `var NAME = ...;` line from the page, so the test uses the page's own constants.
function varLine(name) {
  const m = PAGE.match(new RegExp(`^\\s*var ${name} = [^\\n]*;`, 'm'));
  assert.ok(m, `could not find "var ${name}"`);
  return m[0];
}

// The multi-line `var SORT_PRESETS = [ ... ];` array literal from the page.
function presetsBlock() {
  const start = PAGE.indexOf('var SORT_PRESETS = [');
  assert.ok(start >= 0, 'could not find "var SORT_PRESETS = ["');
  const end = PAGE.indexOf('];', start);
  assert.ok(end > start, 'unterminated SORT_PRESETS');
  const text = PAGE.slice(start, end + 2);
  assert.ok((text.match(/spec:/g) || []).length >= 5, 'SORT_PRESETS extraction looks truncated');
  return text;
}

function load() {
  const sandbox = { out: null };
  vm.runInNewContext([
    varLine('SORT_COLUMNS'),
    varLine('MAX_SORT_KEYS'),
    varLine('DEFAULT_SORT'),
    presetsBlock(),
    extract(PAGE, 'function normalizeStatus('),
    extract(PAGE, 'function parseSortSpec('),
    extract(PAGE, 'function formatSortSpec('),
    extract(PAGE, 'function nextSortKeys('),
    extract(PAGE, 'function sortValue('),
    extract(PAGE, 'function compareBySortKeys('),
    extract(PAGE, 'function browseLayout('),
    extract(PAGE, 'function sortPresetFor('),
    'out = { parseSortSpec, formatSortSpec, nextSortKeys, compareBySortKeys, browseLayout, sortPresetFor, SORT_PRESETS, SORT_COLUMNS, DEFAULT_SORT };',
  ].join('\n'), sandbox);
  // Arrays made inside the vm context have that context's Array prototype, which deepStrictEqual
  // rejects; round-trip them into this realm.
  const plain = (x) => JSON.parse(JSON.stringify(x));
  const o = sandbox.out;
  return {
    parse: (s) => plain(o.parseSortSpec(s)),
    format: (k) => o.formatSortSpec(k),
    next: (k, c, add) => plain(o.nextSortKeys(k, c, add)),
    sort: (list, keys) => list.slice().sort((a, b) => o.compareBySortKeys(a, b, keys)).map((p) => p.name),
    columns: plain(o.SORT_COLUMNS),
    defaultSpec: o.DEFAULT_SORT,
    layout: (view) => o.browseLayout(view),
    presetFor: (keys) => o.sortPresetFor(keys),
    presets: plain(o.SORT_PRESETS),
  };
}

const P = (name, opts = {}) => ({ name, isPinned: false, status: 'active', lastOpenedAt: null, createdAt: null, ...opts });
const LIST = [
  P('Bravo', { lastOpenedAt: '2026-10-10 09:00:00' }),
  P('Alpha', { isPinned: true, lastOpenedAt: '2026-09-01 09:00:00' }),
  P('Delta', { isPinned: true, lastOpenedAt: '2026-10-15 09:00:00' }),
  P('Charlie', { lastOpenedAt: '2026-10-16 09:00:00' }),
];

test('with nothing stored the list is pinned first, then by name', () => {
  const s = load();
  assert.equal(s.defaultSpec, 'pin:asc,name:asc');
  assert.deepEqual(s.parse(''), [{ col: 'pin', dir: 'asc' }, { col: 'name', dir: 'asc' }]);
  assert.deepEqual(s.sort(LIST, s.parse('')), ['Alpha', 'Delta', 'Bravo', 'Charlie']);
});

// Falsified: making nextSortKeys ignore `additive` (always the plain-click branch) turned this red at
// the second assertion, because Shift-click on Last Opened replaced the Pinned key instead of adding.
test('Shift-click adds a secondary key: Pinned then Last Opened, newest first', () => {
  const s = load();
  let keys = s.next([], 'pin', false);
  assert.deepEqual(keys, [{ col: 'pin', dir: 'asc' }]);
  keys = s.next(keys, 'opened', true);
  assert.deepEqual(keys, [{ col: 'pin', dir: 'asc' }, { col: 'opened', dir: 'asc' }]);
  keys = s.next(keys, 'opened', true);   // a second Shift-click flips that key, it does not re-add it
  assert.deepEqual(keys, [{ col: 'pin', dir: 'asc' }, { col: 'opened', dir: 'desc' }]);
  assert.deepEqual(s.sort(LIST, keys), ['Delta', 'Alpha', 'Charlie', 'Bravo']);
  assert.equal(s.format(keys), 'pin:asc,opened:desc');
});

test('a plain click sorts by that column alone, and a second plain click flips it', () => {
  const s = load();
  let keys = s.next([{ col: 'pin', dir: 'asc' }, { col: 'opened', dir: 'desc' }], 'name', false);
  assert.deepEqual(keys, [{ col: 'name', dir: 'asc' }]);
  keys = s.next(keys, 'name', false);
  assert.deepEqual(keys, [{ col: 'name', dir: 'desc' }]);
  // A plain click on a column that is one of several keys starts over with it alone.
  assert.deepEqual(s.next([{ col: 'pin', dir: 'asc' }, { col: 'name', dir: 'asc' }], 'pin', false), [{ col: 'pin', dir: 'asc' }]);
});

test('no more than three keys are kept', () => {
  const s = load();
  let keys = s.parse('pin:asc,opened:desc,type:asc');
  keys = s.next(keys, 'lead', true);
  assert.deepEqual(keys.map((k) => k.col), ['pin', 'opened', 'type']);
});

test('a stored spec is cleaned: unknown columns and repeats dropped, odd directions read as asc', () => {
  const s = load();
  assert.deepEqual(s.parse('bogus:desc,opened:DESC,opened:asc,name:sideways'),
    [{ col: 'opened', dir: 'desc' }, { col: 'name', dir: 'asc' }]);
  assert.deepEqual(s.parse('bogus'), s.parse(''));
});

// The page sends the spec and SettingsService.NormalizeStartScreenSort keeps only the columns in its
// StartScreenSortColumns. If the two lists drift, a column the page can sort by is silently dropped on
// the next restart. This reads the C# set literal (scoped to that one declaration, extraction asserted).
// Falsified: deleting "lead" from the C# set turned this red ("expected 8 columns ... found 7").
test('the page and SettingsService agree on the sortable columns', () => {
  const s = load();
  const decl = extract(SETTINGS, 'StartScreenSortColumns = new HashSet<string>(StringComparer.Ordinal)');
  const csharp = [...decl.matchAll(/"([a-z]+)"/g)].map((m) => m[1]);
  assert.equal(csharp.length, 8, `expected 8 columns in the C# set, found ${csharp.length}`);
  assert.deepEqual([...csharp].sort(), [...s.columns].sort());
});

// The Owner's "one interface" (item 8, live test on c7af159): with the Home view set to cards, Browse All
// shows cards too, and only 'cards' means cards. Falsified: making browseLayout always return 'list' (the
// pre-fix behaviour, where Browse All was always the table) turned this red at the first assertion.
test('Browse All follows the Home view preference', () => {
  const s = load();
  assert.equal(s.layout('cards'), 'cards');
  assert.equal(s.layout('list'), 'list');
  assert.equal(s.layout(undefined), 'list');
  assert.equal(s.layout('Cards'), 'list');   // the page only ever holds the normalized lowercase value
});

// The card layout's Sort dropdown reads and writes the SAME saved spec as the list headers: the default
// selects "Pinned, then name", a Shift-click sort with no preset shows as custom, and every preset is a
// spec the page (and so SettingsService) keeps unchanged. Falsified: making sortPresetFor always return
// 'custom' turned this red at the first assertion.
test('the card layout sort dropdown maps the saved sort to its presets', () => {
  const s = load();
  assert.equal(s.presetFor(s.parse('')), 'pin:asc,name:asc');
  assert.equal(s.presetFor(s.parse('PIN:asc, opened:DESC')), 'pin:asc,opened:desc');
  assert.equal(s.presetFor(s.parse('pin:asc,type:desc,lead:asc')), 'custom');
  for (const p of s.presets) {
    assert.equal(s.format(s.parse(p.spec)), p.spec, `preset "${p.label}" is not a clean spec`);
  }
});
