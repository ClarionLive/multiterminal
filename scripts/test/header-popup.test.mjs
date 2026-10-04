// header-popup.test.mjs — the Launch Project picker's list rules (task 4cac608c).
//
// Runs the REAL pickerSections/glyph functions, cut out of DashboardHeader/header-popup.html by
// brace matching (each cut asserted), in node:vm. What this cannot see: the popup window, focus,
// keyboard handling or layout; those need the running app.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';

const here = path.dirname(fileURLToPath(import.meta.url));
const PAGE = readFileSync(path.join(here, '..', '..', 'DashboardHeader', 'header-popup.html'), 'utf8');

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

function load() {
  const sandbox = { out: null };
  vm.runInNewContext([
    extract(PAGE, 'function recency('),
    extract(PAGE, 'function pickerSections('),
    extract(PAGE, 'function glyph('),
    'out = { pickerSections, glyph };',
  ].join('\n'), sandbox);
  // Arrays made inside the vm context have that context's Array prototype, which deepStrictEqual
  // rejects; round-trip them into this realm.
  const plain = (x) => JSON.parse(JSON.stringify(x));
  return {
    pickerSections: (...args) => plain(sandbox.out.pickerSections(...args)),
    glyph: (...args) => plain(sandbox.out.glyph(...args)),
  };
}

const P = (name, opts = {}) => ({ id: name, name, path: `H:\\p\\${name}`, isPinned: false, status: 'active', lastOpenedAt: null, ...opts });
const names = (section) => section.rows.map((r) => r.name);

test('pinned projects come first, then recent ones, newest first', () => {
  const { pickerSections } = load();
  const list = [
    P('Old', { lastOpenedAt: '2026-09-01 10:00:00' }),
    P('Pinned', { isPinned: true, lastOpenedAt: '2026-08-01 10:00:00' }),
    P('New', { lastOpenedAt: '2026-10-03 10:00:00' }),
  ];
  const sections = pickerSections(list, '', 8);
  assert.deepEqual(sections.map((s) => s.title), ['PINNED', 'RECENT']);
  assert.deepEqual(names(sections[0]), ['Pinned']);
  assert.deepEqual(names(sections[1]), ['New', 'Old']);
});

test('the recent list is capped; a search lists every match', () => {
  const { pickerSections } = load();
  const list = Array.from({ length: 12 }, (_, i) => P(`Proj${i}`, { lastOpenedAt: `2026-09-${String(i + 1).padStart(2, '0')} 10:00:00` }));
  assert.equal(pickerSections(list, '', 8)[0].rows.length, 8);
  const found = pickerSections(list, 'proj', 8);
  assert.deepEqual(found.map((s) => s.title), ['MATCHES']);
  assert.equal(found[0].rows.length, 12);
});

test('search matches the name or the path, ignoring case', () => {
  const { pickerSections } = load();
  const list = [P('Alpha'), P('Beta', { path: 'H:\\Work\\GAMMA-tools' })];
  assert.deepEqual(names(pickerSections(list, 'ALP', 8)[0]), ['Alpha']);
  assert.deepEqual(names(pickerSections(list, 'gamma', 8)[0]), ['Beta']);
  assert.equal(pickerSections(list, 'zzz', 8)[0].rows.length, 0);
});

test('archived projects are hidden until searched for', () => {
  const { pickerSections } = load();
  const list = [P('Live'), P('Shelved', { status: 'archived' })];
  const all = pickerSections(list, '', 8).flatMap(names);
  assert.ok(!all.includes('Shelved'));
  assert.deepEqual(names(pickerSections(list, 'shel', 8)[0]), ['Shelved']);
});

test('an emoji icon shows as-is; anything else becomes the first letter', () => {
  const { glyph } = load();
  assert.equal(glyph({ name: 'x', icon: '\u{1F4E6}' }).text, '\u{1F4E6}');
  assert.equal(glyph({ name: 'clarion', icon: 'code' }).text, 'C');
  assert.equal(glyph({ name: 'zeta', icon: '' }).text, 'Z');
  assert.equal(glyph({ name: 'zeta', icon: 'code', iconColor: '#ff0000' }).color, '#ff0000');
});

// ---- Clear (x) button in the search (task 28343a65) ----
//
// Runs the REAL renderProjects/syncSearchClear/onSearchInput/clearSearch against a stub DOM of
// getElementById elements. What the stub CAN see: the x's `visible` class, what the rows are filled
// with, whether the search input is the same object afterwards (assigning the picker panel's
// innerHTML replaces its children, as in a real DOM), and that focus() was asked for. What it
// CANNOT see: CSS (that `visible` actually shows the x), the caret, real focus, or the popup window
// staying open; those rest on the x's cancelled mousedown and need the running app.

function stubElement(id) {
  const classes = new Set();
  return {
    id,
    value: '',
    innerHTML: '',
    focused: 0,
    classList: {
      contains: (c) => classes.has(c),
      toggle: (c, force) => {
        const on = force === undefined ? !classes.has(c) : !!force;
        if (on) classes.add(c); else classes.delete(c);
        return on;
      },
    },
    focus() { this.focused++; },
    querySelectorAll: () => [],
    getBoundingClientRect: () => ({ width: 400, height: 300 }),
  };
}

function bootPicker(list) {
  const els = {};
  const children = ['search', 'searchClear', 'rows'];
  for (const id of children) els[id] = stubElement(id);
  const panel = stubElement('projects');
  let panelHtml = '';
  Object.defineProperty(panel, 'innerHTML', {
    get: () => panelHtml,
    set: (v) => { panelHtml = v; for (const id of children) els[id] = stubElement(id); },
  });
  els.projects = panel;
  const document = { getElementById: (id) => els[id] || null };
  const sandbox = { document, out: null, posted: [] };
  vm.runInNewContext([
    "var view = 'projects';",
    'var requestId = 0;',
    'var focusIndex = -1;',
    "var currentProjectId = '';",
    'var projects = [];',
    'function post(msg) { posted.push(msg); }',
    extract(PAGE, 'var ICONS = {') + ';',
    extract(PAGE, 'function esc('),
    extract(PAGE, 'function svg('),
    extract(PAGE, 'function glyph('),
    extract(PAGE, 'function recency('),
    extract(PAGE, 'function pickerSections('),
    extract(PAGE, 'function renderProjects('),
    extract(PAGE, 'function postSize('),
    extract(PAGE, 'function buttons('),
    extract(PAGE, 'function paintFocus('),
    extract(PAGE, 'function syncSearchClear('),
    extract(PAGE, 'function onSearchInput('),
    extract(PAGE, 'function clearSearch('),
    'out = { onSearchInput, clearSearch, setProjects(l) { projects = l; } };',
  ].join('\n'), sandbox);
  sandbox.out.setProjects(list);
  sandbox.out.onSearchInput(); // the popup opens with an empty search
  return { api: sandbox.out, els };
}

// Typing, as the browser does it: the value changes, then the input listener runs.
function typeInto(api, els, text) {
  els.search.value = text;
  api.onSearchInput();
}

const PICKER = [
  P('Alpha', { lastOpenedAt: '2026-10-01 10:00:00' }),
  P('Beta', { lastOpenedAt: '2026-10-02 10:00:00' }),
  P('Gamma'),
];

test('the clear (x) button is hidden while the search is empty and shows once it has text (task 28343a65)', () => {
  const { api, els } = bootPicker(PICKER);
  assert.equal(els.searchClear.classList.contains('visible'), false, 'the x shows on an empty search');
  typeInto(api, els, 'a');
  assert.equal(els.searchClear.classList.contains('visible'), true, 'the x did not appear after typing');
  typeInto(api, els, '');
  assert.equal(els.searchClear.classList.contains('visible'), false, 'the x stayed after the search was emptied by typing');
});

test('the clear (x) button empties the search, lists every project again and keeps the same focused input (task 28343a65)', () => {
  const { api, els } = bootPicker(PICKER);
  const input = els.search;
  const clear = els.searchClear;
  typeInto(api, els, 'alp');
  assert.doesNotMatch(els.rows.innerHTML, />Beta</, 'precondition: the filter is applied');
  const focusedBefore = input.focused;

  api.clearSearch();

  assert.equal(els.search, input, 'clearing replaced the search input');
  assert.equal(input.value, '');
  for (const name of ['Alpha', 'Beta', 'Gamma']) {
    assert.match(els.rows.innerHTML, new RegExp(`>${name}<`), `"${name}" is missing after clearing`);
  }
  assert.doesNotMatch(els.rows.innerHTML, /MATCHES/, 'the list is still in search mode after clearing');
  assert.ok(input.focused > focusedBefore, 'clearing did not put focus back in the search');
  assert.equal(clear.classList.contains('visible'), false, 'the x is still showing on an empty search');
});
