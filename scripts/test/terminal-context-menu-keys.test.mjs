// terminal-context-menu-keys.test.mjs — behavioural facts for the terminal menu's keyboard handling
// (task 11edbec4, follow-up to 94ae8f12 / GH #24).
//
// These run the REAL inline script from Terminal/terminal.html in a node:vm sandbox, with xterm.js,
// the DOM and the WebView2 bridge replaced by stubs, and assert what the page sends to the host for
// a given sequence of key and mouse events. That is a behavioural pin, not a text census: no
// comment, renamed variable or reworded string can satisfy or break it — only what the handler
// does. What the stubs CANNOT see: xterm's own key processing (a handler that returns true hands
// the key to xterm, which these facts treat as "reached the app"), Chromium's real event order, and
// the host. The host-side Esc path (WebViewTerminalRenderer.OnWebViewKeyDown) is not covered here
// or anywhere else; it needs a live WebView2.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';

const here = path.dirname(fileURLToPath(import.meta.url));
const HTML_PATH = path.join(here, '..', '..', 'Terminal', 'terminal.html');

function inlineScript() {
  const html = readFileSync(HTML_PATH, 'utf8');
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  assert.equal(scripts.length, 1, 'expected exactly one inline <script> in terminal.html');
  assert.ok(scripts[0].length > 5000, 'inline script extraction looks truncated');
  return scripts[0];
}

const SCREEN_RECT = { left: 10, top: 20, width: 800, height: 480 }; // 80x24 cells of 10x20

function makeTarget() {
  const listeners = [];
  return {
    listeners,
    addEventListener(type, fn, capture) {
      listeners.push({ type, fn, capture: capture === true || (capture && capture.capture === true) });
    },
    dispatch(type, ev) {
      ev.type = type;
      let stopped = false;
      ev.stopPropagation = () => { stopped = true; };
      ev.preventDefault = () => { ev.defaultPrevented = true; };
      for (const phase of [true, false]) {
        for (const l of listeners.filter((x) => x.type === type && x.capture === phase)) {
          if (stopped) return;
          l.fn(ev);
        }
      }
    },
  };
}

// Boots the page and returns a driver. `sent` collects every message posted to the host.
function boot() {
  const sent = [];
  const clock = { now: 1000 };
  let keyHandler = null;
  let hostMessage = null;
  let domReady = null;

  const container = makeTarget();
  container.getBoundingClientRect = () => ({ ...SCREEN_RECT });
  const screen = { getBoundingClientRect: () => ({ ...SCREEN_RECT }) };
  const win = makeTarget();

  const selection = { text: '' };
  class Terminal {
    constructor() {
      this.cols = 80;
      this.rows = 24;
      this.options = {};
      this.unicode = {};
      this.buffer = { active: { cursorX: 4, cursorY: 2, baseY: 0, viewportY: 0 } };
      this.parser = { registerOscHandler() {} };
    }
    loadAddon() {}
    open() {}
    attachCustomKeyEventHandler(h) { keyHandler = h; }
    onData() {}
    onResize() {}
    onTitleChange() {}
    hasSelection() { return selection.text.length > 0; }
    getSelection() { return selection.text; }
    clearSelection() { selection.text = ''; }
    focus() {}
    paste() {}
    write() {}
  }
  class Stub { fit() {} proposeDimensions() { return { cols: 80, rows: 24 }; } }

  win.chrome = {
    webview: {
      // JSON round trip, as WebView2 serialises postMessage (and it strips the vm realm).
      postMessage: (m) => sent.push(JSON.parse(JSON.stringify(m))),
      addEventListener: (type, fn) => { if (type === 'message') hostMessage = fn; },
    },
  };

  const context = {
    window: win,
    document: {
      readyState: 'loading',
      body: { style: {} },
      getElementById: (id) => (id === 'terminal-container' ? container : null),
      querySelector: (sel) => (sel.includes('xterm-screen') ? screen : null),
      addEventListener: (type, fn) => { if (type === 'DOMContentLoaded') domReady = fn; },
    },
    Terminal,
    FitAddon: { FitAddon: Stub },
    WebLinksAddon: { WebLinksAddon: Stub },
    Unicode11Addon: { Unicode11Addon: Stub },
    ResizeObserver: class { observe() {} },
    setInterval: () => 0,
    setTimeout: () => 0,
    clearTimeout: () => {},
    performance: { now: () => clock.now },
    console,
    btoa, atob, TextEncoder, TextDecoder, Uint8Array, Math, Date,
  };
  vm.createContext(context);
  vm.runInContext(inlineScript(), context, { filename: 'terminal.html' });
  assert.ok(domReady, 'page registered no DOMContentLoaded handler');
  domReady();
  assert.ok(keyHandler, 'page attached no xterm custom key handler');
  assert.deepEqual(sent.map((m) => m.type), ['ready'], 'boot sent something other than ready');
  sent.length = 0;

  const mods = (m = {}) => ({ shiftKey: !!m.shift, ctrlKey: !!m.ctrl, altKey: !!m.alt, metaKey: !!m.meta, repeat: !!m.repeat });

  return {
    sent,
    clock,
    selection,
    // One key event through the page as Chromium orders it: window capture listener (keydown
    // only) then xterm's textarea, where the custom handler runs first. Returns the handler's
    // verdict (false = swallowed, true = handed to xterm, i.e. to the app) and the event.
    key(type, keyName, m) {
      const ev = { key: keyName, ...mods(m), defaultPrevented: false };
      if (type === 'keydown') win.dispatch('keydown', ev);
      ev.type = type;
      ev.preventDefault = () => { ev.defaultPrevented = true; };
      ev.stopPropagation = () => {};
      return { verdict: keyHandler(ev), ev };
    },
    press(keyName, m) {
      const down = this.key('keydown', keyName, m);
      const up = this.key('keyup', keyName, m);
      return { down, up };
    },
    rightClick(m) {
      container.dispatch('mousedown', { button: 2, ...mods(m) });
      container.dispatch('mouseup', { button: 2, ...mods(m) });
      container.dispatch('contextmenu', { clientX: 300, clientY: 200, ...mods(m) });
    },
    // A contextmenu with no right mousedown before it: the browser's own keyboard contextmenu,
    // or a touch long-press.
    bareContextMenu(m) {
      container.dispatch('contextmenu', { clientX: 111, clientY: 222, ...mods(m) });
    },
    host(msg) { hostMessage({ data: msg }); },
    types() { return sent.filter((m) => m.type !== 'log').map((m) => m.type); },
    clear() { sent.length = 0; },
  };
}

const MENU_KEYS = [
  ['Shift+F10', 'F10', { shift: true }],
  ['Apps key', 'ContextMenu', {}],
  ['Shift+Apps key', 'ContextMenu', { shift: true }],
];

for (const [label, keyName, m] of MENU_KEYS) {
  test(`${label} opens the menu at the cursor cell, sends nothing else, and swallows keydown and keyup`, () => {
    const page = boot();
    page.selection.text = 'picked';
    const { down, up } = page.press(keyName, m);

    assert.equal(down.verdict, false, 'keydown reached xterm (the app)');
    assert.equal(up.verdict, false, 'keyup reached xterm (the app)');
    assert.equal(down.ev.defaultPrevented, true, 'keydown not default-prevented: the browser may raise its own contextmenu');
    assert.equal(up.ev.defaultPrevented, true, 'keyup not default-prevented: on Windows the browser raises keyboard contextmenu on keyup');
    assert.deepEqual(page.types(), ['contextmenu']);
    // Cursor at column 4, row 2 of an 80x24 grid of 10x20 cells starting at (10,20): just below
    // and right of the cell.
    assert.deepEqual(page.sent[0], { type: 'contextmenu', x: 60, y: 80, selectedText: 'picked' });
  });
}

test('an auto-repeated menu keydown does not open a second menu', () => {
  const page = boot();
  page.key('keydown', 'F10', { shift: true });
  const rep = page.key('keydown', 'F10', { shift: true, repeat: true });
  assert.equal(rep.verdict, false);
  assert.deepEqual(page.types(), ['contextmenu']);
});

test('F10 alone, and Shift+F10 with Ctrl or Alt, are left to xterm', () => {
  const page = boot();
  for (const m of [{}, { shift: true, ctrl: true }, { shift: true, alt: true }]) {
    assert.equal(page.key('keydown', 'F10', m).verdict, true, `F10 ${JSON.stringify(m)} was intercepted`);
  }
  assert.deepEqual(page.types(), []);
});

test('the browser\'s own contextmenu after a menu key opens no second menu and never pastes', () => {
  const page = boot();
  page.press('ContextMenu', {});
  page.bareContextMenu({});
  assert.deepEqual(page.types(), ['contextmenu']);
});

test('the dedupe is a window, not a lock: a later bare contextmenu with the menu closed still opens the menu (no paste)', () => {
  const page = boot();
  page.press('ContextMenu', {});
  page.host('contextMenu:1');
  page.host('contextMenu:0');
  page.clock.now += 5000;
  page.clear();
  page.bareContextMenu({});
  assert.deepEqual(page.types(), ['contextmenu']);
});

test('stale right-click flag: right-click paste, then Shift+F10, opens the menu and does not paste again', () => {
  const page = boot();
  page.rightClick({});
  assert.deepEqual(page.types(), ['terminalclick', 'paste']);
  page.clear();
  page.press('F10', { shift: true });
  assert.deepEqual(page.types(), ['contextmenu']);
});

test('Esc right after a keyboard open is swallowed and dismisses, even before the host says the menu is up', () => {
  const page = boot();
  page.press('F10', { shift: true });
  page.clear();
  // No "contextMenu:1" from the host yet.
  const down = page.key('keydown', 'Escape', {});
  assert.equal(down.verdict, false, 'Esc reached the app while the menu was being opened');
  assert.deepEqual(page.types(), ['dismissContextMenu']);
});

test('Esc dismissing the menu: its auto-repeats and keyup are swallowed too, the next Esc goes to the app', () => {
  const page = boot();
  page.rightClick({ shift: true });
  page.host('contextMenu:1');
  page.clear();

  assert.equal(page.key('keydown', 'Escape', {}).verdict, false);
  assert.equal(page.key('keydown', 'Escape', { repeat: true }).verdict, false, 'auto-repeat Esc reached the app');
  // The host closes the menu in reaction to the first keydown, while the key is still held.
  page.host('contextMenu:0');
  assert.equal(page.key('keydown', 'Escape', { repeat: true }).verdict, false, 'auto-repeat Esc after the menu closed reached the app');
  assert.equal(page.key('keyup', 'Escape', {}).verdict, false, 'Esc keyup reached the app');
  assert.deepEqual(page.types(), ['dismissContextMenu']);

  assert.equal(page.key('keydown', 'Escape', {}).verdict, true, 'a fresh Esc with no menu was swallowed');
});

test('with no menu, Esc goes to the app', () => {
  const page = boot();
  assert.equal(page.key('keydown', 'Escape', {}).verdict, true);
  assert.deepEqual(page.types(), []);
});
