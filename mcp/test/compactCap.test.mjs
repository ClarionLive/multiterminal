// compact_my_context byte cap (ticket c0ebcfd4).
//
// TerminalControl.InjectInputAsync splits injected text over 500 UTF-8 bytes into "[1/2] "-prefixed
// chunks submitted separately, so a long "/compact <focus>" became a plain message and /compact never
// ran (live-observed twice). capCompactCommand caps the whole command at 450 bytes.
//
// House pattern (apiErrorDetail.test.mjs, pushRatio.test.mjs): read mcp/index.js as TEXT, extract the
// real function and run it; never import index.js (that boots the server). Extraction is asserted
// non-vacuous.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

function slice(startMarker, endMarker, label) {
  const a = src.indexOf(startMarker);
  assert.ok(a !== -1, `${label}: start marker not found in mcp/index.js`);
  const b = src.indexOf(endMarker, a + startMarker.length);
  assert.ok(b !== -1, `${label}: end marker not found in mcp/index.js`);
  return src.slice(a, b);
}

const capBlock = slice("const COMPACT_MAX_BYTES", "// Format terminal list for display", "capCompactCommand block");
assert.ok(capBlock.includes("function capCompactCommand"), "extraction lost capCompactCommand");
const { capCompactCommand, COMPACT_MAX_BYTES } = new Function(
  `${capBlock}; return { capCompactCommand, COMPACT_MAX_BYTES };`,
)();

const bytes = (s) => Buffer.byteLength(s, "utf8");

test("the cap leaves headroom under the 500-byte inject chunk limit", () => {
  assert.ok(COMPACT_MAX_BYTES <= 450, `COMPACT_MAX_BYTES is ${COMPACT_MAX_BYTES}`);
});

test("no focus gives a plain /compact", () => {
  assert.deepEqual(capCompactCommand(""), { text: "/compact", trimmed: false, focusBytes: 0 });
});

test("a focus under the limit is unchanged and not flagged", () => {
  const focus = "keep the active task ID, modified files, and next step";
  const r = capCompactCommand(focus);
  assert.equal(r.text, `/compact ${focus}`);
  assert.equal(r.trimmed, false);
});

test("a command exactly at the limit is unchanged", () => {
  const focus = "a".repeat(COMPACT_MAX_BYTES - bytes("/compact "));
  const r = capCompactCommand(focus);
  assert.equal(bytes(r.text), COMPACT_MAX_BYTES);
  assert.equal(r.trimmed, false);
});

test("an ASCII focus over the limit is trimmed to <= 450 bytes and flagged", () => {
  const focus = "word ".repeat(300).trim();
  const r = capCompactCommand(focus);
  assert.ok(bytes(r.text) <= 450, `capped command is ${bytes(r.text)} bytes`);
  assert.ok(r.text.startsWith("/compact word"));
  assert.ok(r.text.endsWith("…"));
  assert.equal(r.trimmed, true);
  assert.equal(r.focusBytes, bytes(focus));
});

test("multi-byte characters are never split (é and astral emoji)", () => {
  for (const unit of ["é", "😀", "a😀é"]) {
    const focus = unit.repeat(400);
    const r = capCompactCommand(focus);
    assert.ok(bytes(r.text) <= 450, `${unit}: ${bytes(r.text)} bytes`);
    assert.equal(r.trimmed, true);
    // A split surrogate pair or UTF-8 sequence would not survive a round trip.
    assert.equal(Buffer.from(r.text, "utf8").toString("utf8"), r.text, `${unit}: text is not valid UTF-16/UTF-8`);
    assert.doesNotMatch(r.text, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/, `${unit}: lone surrogate`);
    const body = r.text.slice("/compact ".length, -1);
    assert.ok(focus.startsWith(body), `${unit}: kept text is not a prefix of the focus`);
  }
});

test("the handler submits the capped text and reports the trim", () => {
  const start = src.indexOf('      case "compact_my_context": {');
  assert.ok(start !== -1);
  const handler = src.slice(start, src.indexOf("\n      case ", start + 10));
  const code = handler.split(/\r?\n/).filter((l) => !/^\s*\/\//.test(l)).join("\n");
  assert.match(code, /= capCompactCommand\(focus\)/);
  assert.match(code, /\{ text: compactText \}/);
  assert.match(code, /it was trimmed to fit the 500-byte terminal input limit/);
  assert.match(code, /\$\{trimNote\}/);
});
