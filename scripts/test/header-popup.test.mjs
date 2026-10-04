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
