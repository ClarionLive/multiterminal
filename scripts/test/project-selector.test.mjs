// project-selector.test.mjs — behavioural facts for the Project pane's Switch Project popup
// (task 66f529bd).
//
// These run the REAL popup functions from ProjectPanel/panel.html in a node:vm sandbox against a
// stub DOM. The page's inline script is too large and side-effectful to boot whole, so the named
// declarations are cut out by brace matching; extraction is asserted, so a rename fails loudly
// instead of leaving the facts vacuous.
//
// What the stub DOM CAN see: whether the overlay and its search box are the same objects after a
// keystroke (the defect: a rebuilt box put the caret at position 0, so "tes" came out as "set"),
// and what the list is filled with. What it CANNOT see: the caret itself, focus, layout, or CSS,
// so the icon column's fixed width is checked live, not here.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';

const here = path.dirname(fileURLToPath(import.meta.url));
const PANEL = readFileSync(path.join(here, '..', '..', 'ProjectPanel', 'panel.html'), 'utf8');
const START = readFileSync(path.join(here, '..', '..', 'StartScreen', 'start-screen.html'), 'utf8');

// Cuts `function name(...) {...}` or `const name = {...};` out of a page by brace matching.
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

// A stub element: querySelector returns one stable child per selector until innerHTML is
// assigned, which (like the real DOM) replaces every child with a new object.
function makeElement(tag) {
  let children = new Map();
  let html = '';
  return {
    tag,
    className: '',
    value: '',
    removed: false,
    get innerHTML() { return html; },
    set innerHTML(v) { html = v; children = new Map(); },
    querySelector(sel) {
      if (!children.has(sel)) children.set(sel, makeElement(sel));
      return children.get(sel);
    },
    addEventListener() {},
    focus() {},
    remove() { this.removed = true; },
  };
}

function boot(projects) {
  const body = [];
  const document = {
    createElement: makeElement,
    body: { appendChild(el) { body.push(el); } },
    querySelector(sel) {
      if (sel !== '.picker-overlay.project-selector') return null;
      return body.filter((el) => !el.removed && el.className === 'picker-overlay project-selector').pop() || null;
    },
  };
  const sandbox = { document, setTimeout: () => 0, sendToHost: () => {}, out: null };
  const code = [
    'let cachedProjectList = [];',
    "let cachedCurrentProjectId = '';",
    'let _projectListLoading = false;',
    extract(PANEL, 'function escapeHtml('),
    extract(PANEL, 'function escapeJs('),
    extract(PANEL, 'function showProjectSelectorPopup('),
    extract(PANEL, 'function renderProjectSelectorList('),
    extract(PANEL, 'const PROJECT_ICON_NAMES = {') + ';',
    extract(PANEL, 'function projectIconGlyph('),
    extract(PANEL, 'function onProjectSearchInput('),
    extract(PANEL, 'function closeProjectSelector('),
    'out = { showProjectSelectorPopup, onProjectSearchInput, projectIconGlyph, PROJECT_ICON_NAMES,',
    '        setProjects(list) { cachedProjectList = list; } };',
  ].join('\n');
  vm.runInNewContext(code, sandbox);
  sandbox.out.setProjects(projects);
  return { api: sandbox.out, document };
}

const PROJECTS = [
  { id: 'a', name: 'TestB', description: '', path: 'H:\\p\\TestB', icon: '' },
  { id: 'b', name: 'TravelRemote', description: 'remote desktop', path: 'H:\\p\\TravelRemote', icon: 'smartphone' },
  { id: 'c', name: 'Clarion Addin Registry', description: 'publisher list', path: 'H:\\p\\Reg', icon: 'package' },
];

test('typing keeps the same overlay and search box, so the caret is never reset', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const overlay = document.querySelector('.picker-overlay.project-selector');
  const box = overlay.querySelector('.project-selector-search');

  for (const typed of ['t', 'te', 'tes']) {
    api.onProjectSearchInput(typed);
    const now = document.querySelector('.picker-overlay.project-selector');
    assert.equal(now, overlay, `the overlay was rebuilt after typing "${typed}"`);
    assert.equal(now.querySelector('.project-selector-search'), box, `the search box was replaced after typing "${typed}"`);
  }
});

test('the list filters on each keystroke, case-insensitively', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const list = () => document.querySelector('.picker-overlay.project-selector').querySelector('.picker-list').innerHTML;

  assert.match(list(), /TestB/);
  assert.match(list(), /TravelRemote/);
  api.onProjectSearchInput('TES');
  assert.match(list(), /TestB/);
  assert.doesNotMatch(list(), /TravelRemote/);
  api.onProjectSearchInput('zzz');
  assert.match(list(), /No matching projects/);
});

test('a refresh while open (the projectList reply) re-renders the list but keeps the box', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const overlay = document.querySelector('.picker-overlay.project-selector');
  const box = overlay.querySelector('.project-selector-search');
  api.showProjectSelectorPopup('tra');
  assert.equal(overlay.querySelector('.project-selector-search'), box);
  assert.match(overlay.querySelector('.picker-list').innerHTML, /TravelRemote/);
  assert.doesNotMatch(overlay.querySelector('.picker-list').innerHTML, /TestB/);
});

test('icon names become emoji; unknown or empty names get the folder; emoji pass through', () => {
  const { api } = boot([]);
  assert.equal(api.projectIconGlyph('package'), '\u{1F4E6}');
  assert.equal(api.projectIconGlyph('extension'), '\u{1F9E9}');
  assert.equal(api.projectIconGlyph('smartphone'), '\u{1F4F1}');
  assert.equal(api.projectIconGlyph('Code'), '{}');
  assert.equal(api.projectIconGlyph('no-such-icon'), '\u{1F4C1}');
  assert.equal(api.projectIconGlyph(''), '\u{1F4C1}');
  assert.equal(api.projectIconGlyph(undefined), '\u{1F4C1}');
  assert.equal(api.projectIconGlyph('\u{1F680}'), '\u{1F680}');
});

test('the list shows the mapped emoji, never the icon name as text', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const html = document.querySelector('.picker-overlay.project-selector').querySelector('.picker-list').innerHTML;
  assert.match(html, /project-selector-icon[^>]*>\u{1F4E6}</u);
  assert.doesNotMatch(html, />package</);
  assert.doesNotMatch(html, />smartphone</);
});

test('the start screen maps every icon name the Project pane knows to the same emoji', () => {
  const { api } = boot([]);
  const sandbox = { out: null };
  vm.runInNewContext(extract(START, 'function iconLabel(') + '\nout = iconLabel;', sandbox);
  const names = Object.keys(api.PROJECT_ICON_NAMES);
  assert.ok(names.length >= 13, `expected the full name map, got ${names.length}`);
  for (const name of names) {
    assert.equal(sandbox.out({ icon: name, name: 'X' }), api.PROJECT_ICON_NAMES[name], `start screen disagrees on "${name}"`);
  }
});
