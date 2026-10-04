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
// so nothing here checks the icon column's fixed width.
//
// Task 28343a65 (the clear (x) button): the stub records the x's `visible` class and counts focus()
// calls on the box. That shows the clear path ASKS for focus; it cannot show that focus or the caret
// actually stay in the box (that rests on the x's cancelled mousedown), nor that `visible` hides
// anything (that is CSS). Those need the running app.

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
    focused: 0, // how many times focus() was called
    renders: [], // every innerHTML assignment, in order
    classList: makeClassList(),
    get innerHTML() { return html; },
    set innerHTML(v) { html = v; children = new Map(); this.renders.push(v); },
    querySelector(sel) {
      if (!children.has(sel)) children.set(sel, makeElement(sel));
      return children.get(sel);
    },
    addEventListener() {},
    focus() { this.focused++; },
    remove() { this.removed = true; },
  };
}

function makeClassList() {
  const set = new Set();
  return {
    contains: (c) => set.has(c),
    add: (c) => { set.add(c); },
    remove: (c) => { set.delete(c); },
    toggle: (c, force) => {
      const on = force === undefined ? !set.has(c) : !!force;
      if (on) set.add(c); else set.delete(c);
      return on;
    },
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
    'let _projectSelectorPendingOpen = false;',
    // The host's 'projectList' reply, as written in handleHostMessage's switch.
    'function onProjectListReply(data) { switch (1) { default: ' + extract(PANEL, "case 'projectList': {") + ' } }',
    extract(PANEL, 'function escapeHtml('),
    extract(PANEL, 'function escapeJs('),
    extract(PANEL, 'function showProjectSelectorPopup('),
    extract(PANEL, 'function renderProjectSelectorList('),
    extract(PANEL, 'const PROJECT_ICON_NAMES = {') + ';',
    extract(PANEL, 'function projectIconGlyph('),
    extract(PANEL, 'function onProjectSearchInput('),
    extract(PANEL, 'function syncProjectSearchClear('),
    extract(PANEL, 'function clearProjectSearch('),
    extract(PANEL, 'function closeProjectSelector('),
    'out = { showProjectSelectorPopup, onProjectSearchInput, clearProjectSearch, projectIconGlyph, PROJECT_ICON_NAMES, onProjectListReply,',
    '        setProjects(list) { cachedProjectList = list; },',
    '        setPendingOpen(v) { _projectSelectorPendingOpen = v; } };',
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

test('typing keeps the same overlay and search box (replacing the box is what reset the caret)', () => {
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

test('a projectList reply while open renders the list once, already filtered by the typed text', () => {
  const { api, document } = boot([]);
  api.setPendingOpen(true); // opened while loading, as openProjectSelector does with an empty cache
  api.showProjectSelectorPopup();
  const overlay = document.querySelector('.picker-overlay.project-selector');
  overlay.querySelector('.project-selector-search').value = 'tra';
  const list = overlay.querySelector('.picker-list');
  const before = list.renders.length;

  api.onProjectListReply(JSON.stringify({ projects: PROJECTS, currentProjectId: '' }));

  const after = list.renders.slice(before);
  assert.equal(after.length, 1, `expected one render on reply, got ${after.length}`);
  assert.match(after[0], /TravelRemote/);
  assert.doesNotMatch(after[0], /TestB/, 'the reply rendered an unfiltered list');
});

test('a projectList reply opens the popup only when one was pending and none is open', () => {
  const pending = boot([]);
  pending.api.setPendingOpen(true);
  pending.api.onProjectListReply(JSON.stringify({ projects: PROJECTS }));
  assert.ok(pending.document.querySelector('.picker-overlay.project-selector'), 'a pending open did not open');

  const idle = boot([]);
  idle.api.onProjectListReply(JSON.stringify({ projects: PROJECTS }));
  assert.equal(idle.document.querySelector('.picker-overlay.project-selector'), null, 'opened with nothing pending');
});

// Typing, as the browser does it: the box's value changes, then its oninput runs.
function type(api, box, text) {
  box.value = text;
  api.onProjectSearchInput(text);
}

test('the clear (x) button is hidden while the box is empty and shows once it has text (task 28343a65)', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const overlay = document.querySelector('.picker-overlay.project-selector');
  const box = overlay.querySelector('.project-selector-search');
  const clear = overlay.querySelector('.search-clear');

  assert.equal(clear.classList.contains('visible'), false, 'the x shows on an empty box');
  type(api, box, 't');
  assert.equal(clear.classList.contains('visible'), true, 'the x did not appear after typing');
  type(api, box, '');
  assert.equal(clear.classList.contains('visible'), false, 'the x stayed after the box was emptied by typing');
});

test('the clear (x) button empties the box, lists every project again and keeps the same focused box (task 28343a65)', () => {
  const { api, document } = boot(PROJECTS);
  api.showProjectSelectorPopup();
  const overlay = document.querySelector('.picker-overlay.project-selector');
  const box = overlay.querySelector('.project-selector-search');
  const clear = overlay.querySelector('.search-clear');
  const list = () => document.querySelector('.picker-overlay.project-selector').querySelector('.picker-list').innerHTML;

  type(api, box, 'tra');
  assert.doesNotMatch(list(), /TestB/, 'precondition: the filter is applied');
  const focusedBefore = box.focused;

  api.clearProjectSearch();

  assert.equal(box.value, '');
  for (const name of ['TestB', 'TravelRemote', 'Clarion Addin Registry']) {
    assert.match(list(), new RegExp(name), `"${name}" is missing after clearing`);
  }
  const now = document.querySelector('.picker-overlay.project-selector');
  assert.equal(now, overlay, 'clearing rebuilt the overlay');
  assert.equal(now.querySelector('.project-selector-search'), box, 'clearing replaced the search box');
  assert.ok(box.focused > focusedBefore, 'clearing did not put focus back in the box');
  assert.equal(clear.classList.contains('visible'), false, 'the x is still showing on an empty box');
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
    for (const spelling of [name, name.toUpperCase(), ` ${name} `]) {
      assert.equal(sandbox.out({ icon: spelling, name: 'X' }), api.projectIconGlyph(spelling), `start screen disagrees on "${spelling}"`);
    }
  }
});
