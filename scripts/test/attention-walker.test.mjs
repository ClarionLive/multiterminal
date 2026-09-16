// attention-walker.test.mjs — the walker's wardrobe on the Attention panel (task 84de66e7), plus the
// canvas DPI fix it depends on (task 5350a75e, item 1).
//
// These run the REAL drawing code out of AttentionPanel/attention-panel.html in a vm, against a
// recording canvas context, and assert on what it does. Nothing here matches source text, except
// reading the walker stage height out of the stylesheet, so the fit test measures against the
// height the panel actually ships.
//
// Why the fit test exists: the design study drew every costume on a 34px stage. The real stage was
// 18px, and measured, every costume except the roadster lost part of its head off the top of the
// card (the umbrella by 11px). Nothing looked wrong in the study, because the study was the wrong
// size. A drawing tweak that pushes a costume back above the stage fails here, not in front of the
// Owner.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HTML = fs.readFileSync(path.join(HERE, '..', '..', 'AttentionPanel', 'attention-panel.html'), 'utf8');

// ─── extraction ───────────────────────────────────────────────────────────────────────────────

const BEGIN = 'WALKER WARDROBE — begin';
const END = 'WALKER WARDROBE — end';

function wardrobeSource() {
  const b = HTML.indexOf(BEGIN);
  const e = HTML.indexOf(END);
  assert.ok(b > 0 && e > b, `wardrobe markers not found (begin=${b}, end=${e}); a rename would make every fact below vacuous`);
  // Start at the line after the marker's comment opener, stop at the start of the end marker's line.
  const start = HTML.indexOf('*/', b) + 2;
  const stop = HTML.lastIndexOf('\n', e);
  const src = HTML.slice(start, stop);
  assert.ok(src.length > 10000, `wardrobe extraction is suspiciously short (${src.length} chars)`);
  return src;
}

// A named top-level function, by brace matching from its declaration. The bodies extracted here
// contain no braces inside strings or comments, which the length/shape assertions would catch.
function functionSource(name) {
  const at = HTML.indexOf(`function ${name}(`);
  assert.ok(at > 0, `function ${name} not found`);
  let depth = 0;
  for (let i = HTML.indexOf('{', at); i < HTML.length; i++) {
    if (HTML[i] === '{') depth++;
    else if (HTML[i] === '}' && --depth === 0) {
      const src = HTML.slice(at, i + 1);
      assert.ok(src.length > 200, `extracted ${name} is suspiciously short (${src.length} chars)`);
      return src;
    }
  }
  throw new Error(`unbalanced braces in ${name}`);
}

// The walker stage height the panel ships, read from the stylesheet.
function walkerStageHeight() {
  const m = HTML.match(/body\[data-ambient="walker"\]\s*\.stage\s*\{\s*height:\s*(\d+)px/);
  assert.ok(m, 'walker stage height rule not found in the stylesheet');
  return Number(m[1]);
}

function load(extra = '', random = Math.random) {
  const math = Object.create(Math);
  math.random = random;
  const ctx = { Math: math, Object };
  vm.createContext(ctx);
  vm.runInContext(wardrobeSource() + '\n' + extra, ctx);
  return ctx;
}

// A 2D context that records the extent of everything drawn, honouring translate/rotate (the flip).
function recordingContext(lineWidth = 1.3) {
  const box = { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, calls: 0 };
  let m = [1, 0, 0, 1, 0, 0];
  const saved = [];
  const pad = lineWidth / 2;
  const point = (x, y, r = 0) => {
    const X = m[0] * x + m[2] * y + m[4];
    const Y = m[1] * x + m[3] * y + m[5];
    assert.ok(Number.isFinite(X) && Number.isFinite(Y), 'drew a non-finite coordinate');
    box.minX = Math.min(box.minX, X - r - pad); box.maxX = Math.max(box.maxX, X + r + pad);
    box.minY = Math.min(box.minY, Y - r - pad); box.maxY = Math.max(box.maxY, Y + r + pad);
    box.calls++;
  };
  return {
    box,
    beginPath() {}, stroke() {}, fill() {}, closePath() {},
    moveTo: point, lineTo: point,
    arc(x, y, r) { point(x, y, r); },
    save() { saved.push(m.slice()); },
    restore() { m = saved.pop(); },
    translate(x, y) { m = [m[0], m[1], m[2], m[3], m[4] + m[0] * x + m[2] * y, m[5] + m[1] * x + m[3] * y]; },
    rotate(a) {
      const c = Math.cos(a), s = Math.sin(a);
      m = [m[0] * c + m[2] * s, m[1] * c + m[3] * s, m[2] * c - m[0] * s, m[3] * c - m[1] * s, m[4], m[5]];
    },
  };
}

// ─── the wardrobe ─────────────────────────────────────────────────────────────────────────────

test('the wardrobe holds the costumes the Owner chose, and no stationary ones', () => {
  const ids = Object.keys(load().WARDROBE).sort();
  assert.deepEqual(ids, [
    'barrow', 'bike', 'board', 'boxes', 'brolly', 'dog', 'flip', 'jump',
    'penny', 'pogo', 'roadster', 'scooter', 'tandem', 'uni', 'walk',
  ]);
});

test('every costume stays inside the walker stage over a full motion cycle', () => {
  const h = walkerStageHeight();
  const ground = Math.round(h * 0.82);   // drawWalker's ground line
  const { WARDROBE } = load();
  const clipped = [];
  for (const id of Object.keys(WARDROBE)) {
    const g = recordingContext();
    // t advances ~0.042 per ms; 0..300 covers several full cycles of every phase in the wardrobe.
    for (let t = 0; t < 300; t += 0.05) WARDROBE[id](g, 120, ground, t, 1);
    assert.ok(g.box.calls > 0, `${id} drew nothing`);
    if (g.box.minY < 0 || g.box.maxY > h) {
      clipped.push(`${id}: y ${g.box.minY.toFixed(2)}..${g.box.maxY.toFixed(2)} on a ${h}px stage`);
    }
  }
  assert.deepEqual(clipped, [], 'costumes cut off by the stage edge');
});

test('the costume holds for a whole crossing and changes only when the crossing does', () => {
  const { costumeFor } = load();
  const w = 240, span = w - 20;
  const st = {};
  const byCrossing = new Map();
  // Frame-sized steps of t, as drawWalker advances it (dt ~16ms * 0.042).
  for (let t = 0; t < span * 40; t += 0.67) {
    const crossing = Math.floor(t / span);
    const costume = costumeFor(st, t, w);
    if (!byCrossing.has(crossing)) byCrossing.set(crossing, new Set());
    byCrossing.get(crossing).add(costume);
  }
  const unstable = [...byCrossing].filter(([, set]) => set.size !== 1).map(([c, set]) => `${c}: ${[...set]}`);
  assert.deepEqual(unstable, [], 'a crossing showed more than one costume (strobing)');
  assert.equal(byCrossing.size, 40, 'expected 40 crossings');
});

test('consecutive crossings never wear the same costume', () => {
  const { costumeFor } = load();
  const w = 240, span = w - 20;
  const st = {};
  let prev = null;
  for (let c = 0; c < 500; c++) {
    const costume = costumeFor(st, c * span + 1, w);
    assert.notEqual(costume, prev, `crossing ${c} repeated ${costume}`);
    prev = costume;
  }
});

test('every costume comes up, and the vertical group comes up about a third as often', () => {
  // A seeded generator, so this is a fixed sample rather than a flaky one.
  let seed = 84;
  const lcg = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
  const { pickCostume, WARDROBE } = load('', lcg);
  const counts = Object.fromEntries(Object.keys(WARDROBE).map((id) => [id, 0]));
  let prev;
  for (let i = 0; i < 60000; i++) { prev = pickCostume(prev); counts[prev]++; }

  for (const [id, n] of Object.entries(counts)) assert.ok(n > 0, `${id} never came up`);
  const quiet = ['walk', 'penny', 'bike', 'uni', 'tandem', 'roadster', 'board', 'scooter', 'barrow', 'brolly', 'boxes', 'dog'];
  const avgQuiet = quiet.reduce((s, id) => s + counts[id], 0) / quiet.length;
  for (const id of ['pogo', 'jump', 'flip']) {
    const ratio = counts[id] / avgQuiet;
    assert.ok(ratio > 0.25 && ratio < 0.45, `${id} came up at ${ratio.toFixed(2)}x a quiet costume, expected ~0.33`);
  }
});

// ─── wiring into drawWalker ───────────────────────────────────────────────────────────────────

function loadDrawWalker() {
  const stubs = `
    var alarm = "hand";
    function isBlocking(s) { return !!s.blocking; }
    function isAcked(s) { return false; }
    function stateColor(state) { return "#000"; }
  `;
  return load(stubs + '\n' + functionSource('drawWalker'));
}

test('a Working walker is drawn in a costume; Idle, waving and standing are not', () => {
  const { drawWalker } = loadDrawWalker();
  const h = walkerStageHeight();

  const working = { walk: 5 };
  drawWalker(working, { state: 'Working' }, recordingContext(), 240, h, 16);
  assert.ok(working.costume, 'Working did not pick a costume');

  for (const s of [{ state: 'Idle' }, { state: 'Blocked', blocking: true }, { state: 'Unknown' }]) {
    const st = { walk: 5 };
    const g = recordingContext();
    drawWalker(st, s, g, 240, h, 16);
    assert.equal(st.costume, undefined, `${s.state} picked a costume`);
    assert.ok(g.box.calls > 0, `${s.state} drew nothing`);
    assert.ok(g.box.minY >= 0, `${s.state} pose is clipped on the ${h}px stage (top ${g.box.minY.toFixed(2)})`);
  }
});

// ─── the DPI fix (5350a75e item 1) ────────────────────────────────────────────────────────────

test('fitCanvas does not reallocate the canvas on every frame at 125% scaling', () => {
  const ctx = { Math, window: { devicePixelRatio: 1.25 } };
  vm.createContext(ctx);
  vm.runInContext(functionSource('fitCanvas'), ctx);

  let writes = 0, width = 300, height = 150;
  const cv = {
    getBoundingClientRect: () => ({ width: 237, height: 26 }),   // 237 * 1.25 = 296.25
    get width() { return width; }, set width(v) { writes++; width = Math.trunc(v); },
    get height() { return height; }, set height(v) { writes++; height = Math.trunc(v); },
    getContext: () => ({ setTransform() {} }),
  };

  ctx.fitCanvas(cv);
  const afterFirst = writes;
  assert.ok(afterFirst > 0, 'the first fit should size the canvas');
  for (let frame = 0; frame < 10; frame++) ctx.fitCanvas(cv);
  assert.equal(writes, afterFirst, 'fitCanvas reassigned width/height on a frame where nothing changed');
});
