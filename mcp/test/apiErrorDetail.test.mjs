// apiCall error-detail relay (task c9285d2a, checklist item 0)
//
// WHY THIS FILE EXISTS
// apiCall threw `API error: 400 Bad Request` and never read the response body, so every
// explanation the API took the trouble to write was discarded at the single funnel all ~91 MCP
// tools share. It surfaced on gate (4): the broker refuses a duplicate name with a precise,
// actionable message, and the caller saw none of it. Given only "400 Bad Request", an agent
// reasoned — correctly, from what it was given — that its REQUEST was malformed, invented a
// docId, retried, was refused again, and reported SUCCESS to the Owner (it had "verified" by
// finding the name in the roster; that row belonged to the incumbent). Observed live 2026-09-07,
// broker log `duplicate-name-reject ... rowHeldBy=livePid => refused` twice.
//
// A status code cannot distinguish "you sent nonsense" from "you may not have that name". The
// body always could. These tests pin that the body's reason reaches the caller, and that reading
// it can never itself become the failure.
//
// APPROACH
// Follows the house pattern of consistency.test.mjs / pathEncoding.test.mjs / pushRatio.test.mjs:
// read mcp/index.js as TEXT and never import it (importing pulls in the MCP SDK and starts the
// whole server). Both blocks are extracted from the real source and executed, so these tests
// exercise the shipping code rather than a retyped copy. Extractions are asserted non-vacuous,
// and negative fixtures prove the assertions still falsify — a test that passes on an empty
// slice is worse than no test.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

/** Slice [startMarker, endMarker) out of the real source, or throw loudly. A silent miss would
 *  make every assertion below vacuously true, which is the failure mode this ticket keeps hitting. */
function slice(startMarker, endMarker, label) {
  const a = src.indexOf(startMarker);
  assert.ok(a !== -1, `${label}: start marker not found in mcp/index.js — did the code move or get renamed?`);
  const b = src.indexOf(endMarker, a + startMarker.length);
  assert.ok(b !== -1, `${label}: end marker not found in mcp/index.js — did the code move or get renamed?`);
  return src.slice(a, b);
}

// ─── Extract the real readErrorDetail + clampDetail ─────────────────────────────────────────
const detailBlock = slice(
  "const MAX_ERROR_DETAIL_CHARS",
  "async function apiCall(",
  "readErrorDetail block",
);
assert.ok(detailBlock.includes("async function readErrorDetail"), "extraction lost readErrorDetail");
assert.ok(detailBlock.includes("function clampDetail"), "extraction lost clampDetail");
assert.ok(detailBlock.includes("JSON.parse"), "extraction lost the JSON branch — slice is too small");

const { readErrorDetail, MAX_ERROR_DETAIL_CHARS } = new Function(
  `${detailBlock}; return { readErrorDetail, clampDetail, MAX_ERROR_DETAIL_CHARS };`,
)();

// ─── Extract the real error-composition block from apiCall ──────────────────────────────────
const composeBlock = slice(
  "if (!response.ok) {",
  "const text = await response.text();",
  "apiCall !response.ok block",
);
assert.ok(composeBlock.includes("readErrorDetail(response)"), "extraction lost the detail lookup");
assert.ok(composeBlock.includes("apiErr.status"), "extraction lost the status assignment");

/** Run the REAL composition block with an injected readErrorDetail, and return the thrown error. */
async function composeError(response, detailFn) {
  const run = new Function(
    "response",
    "readErrorDetail",
    `return (async () => { ${composeBlock} })();`,
  );
  try {
    await run(response, detailFn);
  } catch (err) {
    return err;
  }
  return null;
}

/** A minimal Response stand-in. `body` may be a string, or a function that throws. */
function fakeResponse(status, statusText, body) {
  return {
    ok: false,
    status,
    statusText,
    text: async () => (typeof body === "function" ? body() : body),
  };
}

// The message the broker actually returns for a gate-(4) refusal, verbatim from
// MessageBroker.cs. This is the sentence that was being destroyed.
const GATE4 =
  "The name 'Lynn' is already in use by a connected terminal. Pick a different name, or " +
  "disconnect the terminal currently holding it. (If this IS your own terminal's channel " +
  "server, it is running a build older than the one that echoes the launch nonce — restart " +
  "the terminal.)";

const problemDetails = (detail) =>
  JSON.stringify({ type: "about:blank", title: "Bad Request", status: 400, detail, traceId: "x" });

// ─── readErrorDetail: shape handling ────────────────────────────────────────────────────────

test("ProblemDetails detail is what gets relayed (the 252-site shape)", async () => {
  const got = await readErrorDetail(fakeResponse(400, "Bad Request", problemDetails(GATE4)));
  assert.equal(got, GATE4);
});

test("detail wins over title — relaying title would say 'Bad Request' and drop the reason", async () => {
  const got = await readErrorDetail(fakeResponse(400, "Bad Request", problemDetails(GATE4)));
  assert.notEqual(got, "Bad Request");
  assert.ok(got.includes("already in use by a connected terminal"));
});

test("title is still used when there is no detail (better than nothing)", async () => {
  const body = JSON.stringify({ title: "Conflict", status: 409 });
  assert.equal(await readErrorDetail(fakeResponse(409, "Conflict", body)), "Conflict");
});

test("the one { error } endpoint is handled too, not just the dominant shape", async () => {
  const body = JSON.stringify({ error: "ppid must be a positive process id." });
  assert.equal(
    await readErrorDetail(fakeResponse(400, "Bad Request", body)),
    "ppid must be a positive process id.",
  );
});

test("short non-JSON text is relayed", async () => {
  assert.equal(await readErrorDetail(fakeResponse(500, "Server Error", "upstream unavailable")), "upstream unavailable");
});

// ─── readErrorDetail: refusing to relay noise ───────────────────────────────────────────────

test("an HTML error page is dropped, not truncated into the conversation", async () => {
  const html = "<!DOCTYPE html><html><head><title>500</title></head><body>...</body></html>";
  assert.equal(await readErrorDetail(fakeResponse(500, "Server Error", html)), null);
});

test("an over-long plain-text body is dropped rather than half-relayed", async () => {
  const huge = "x".repeat(MAX_ERROR_DETAIL_CHARS + 1);
  assert.equal(await readErrorDetail(fakeResponse(500, "Server Error", huge)), null);
});

test("a long but REAL detail is clamped, not dropped — it is still the reason", async () => {
  const long = "y".repeat(MAX_ERROR_DETAIL_CHARS + 200);
  const got = await readErrorDetail(fakeResponse(400, "Bad Request", problemDetails(long)));
  assert.ok(got, "a JSON detail must survive length, unlike an unstructured body");
  assert.ok(got.length <= MAX_ERROR_DETAIL_CHARS + 1, `clamped to ${got.length}`);
  assert.ok(got.endsWith("…"), "clamping should be visible, not silent truncation");
});

test("empty body yields null rather than an empty separator", async () => {
  assert.equal(await readErrorDetail(fakeResponse(400, "Bad Request", "")), null);
});

test("JSON object with no usable field yields null", async () => {
  assert.equal(await readErrorDetail(fakeResponse(400, "Bad Request", JSON.stringify({ traceId: "x" }))), null);
});

// ─── readErrorDetail: the never-throws contract ─────────────────────────────────────────────
// This runs on a path that is ALREADY failing. An exception here would replace a real
// "the name is held by a live terminal" with a parse error — strictly worse than the bare
// status line it exists to improve on.

test("never throws when the body cannot be read at all", async () => {
  const exploding = fakeResponse(400, "Bad Request", () => { throw new Error("body already consumed"); });
  assert.equal(await readErrorDetail(exploding), null);
});

test("never throws on malformed JSON", async () => {
  assert.equal(await readErrorDetail(fakeResponse(400, "Bad Request", '{"detail": "trunca')), null);
});

test("never throws on a JSON null body", async () => {
  assert.equal(await readErrorDetail(fakeResponse(400, "Bad Request", "null")), null);
});

// ─── The composition: what the caller actually receives ─────────────────────────────────────

test("the thrown Error carries the server's reason, not just the status line", async () => {
  const err = await composeError(fakeResponse(400, "Bad Request", null), async () => GATE4);
  assert.ok(err, "the block must throw");
  assert.ok(err.message.includes("400 Bad Request"), "status line is still present");
  assert.ok(
    err.message.includes("already in use by a connected terminal"),
    `reason missing from: ${err.message}`,
  );
});

test("status and detail are preserved as properties for programmatic branching", async () => {
  const err = await composeError(fakeResponse(400, "Bad Request", null), async () => GATE4);
  assert.equal(err.status, 400);
  assert.equal(err.detail, GATE4);
});

test("with no detail available the message is exactly the old status line (no dangling dash)", async () => {
  const err = await composeError(fakeResponse(404, "Not Found", null), async () => null);
  assert.equal(err.message, "API error: 404 Not Found");
  assert.equal(err.detail, undefined);
});

// ─── Negative fixtures: prove these assertions still falsify ────────────────────────────────
// Every one of the above would pass against a broken implementation if the extraction silently
// missed. These reproduce the ACTUAL pre-fix behaviour and assert the damage is visible, so the
// suite distinguishes fixed from broken rather than merely being green.

test("negative fixture: the pre-fix status-only throw reproduces the unreadable refusal", () => {
  // Verbatim shape of the code this ticket replaced.
  const response = { status: 400, statusText: "Bad Request" };
  const apiErr = new Error(`API error: ${response.status} ${response.statusText}`);
  apiErr.status = response.status;

  assert.equal(apiErr.message, "API error: 400 Bad Request");
  assert.ok(
    !apiErr.message.includes("already in use"),
    "pre-fix message must NOT carry the reason — that is the defect",
  );
  assert.equal(apiErr.detail, undefined);
  // And this is precisely what an agent was left to infer from: a status that reads as
  // "your request was malformed" when the truth was "you may not have that name".
});

test("negative fixture: preferring title over detail reproduces relaying 'Bad Request'", () => {
  const parsed = JSON.parse(problemDetails(GATE4));
  const wrong = parsed.title ?? parsed.detail ?? parsed.error;   // the inverted precedence
  assert.equal(wrong, "Bad Request", "inverted precedence yields the useless generic label");

  const right = parsed.detail ?? parsed.error ?? parsed.title;   // what the code actually does
  assert.equal(right, GATE4);
  assert.notEqual(right, wrong, "the ordering is load-bearing, not cosmetic");
});

test("negative fixture: a throwing body would propagate without the try/catch", async () => {
  // Proves the never-throws contract is doing work rather than being decorative.
  const bare = async (response) => {
    const text = await response.text();          // no try/catch — the pre-guard shape
    return text ? JSON.parse(text) : null;
  };
  const exploding = fakeResponse(400, "Bad Request", () => { throw new Error("body already consumed"); });
  await assert.rejects(() => bare(exploding), /body already consumed/);
  // ...whereas the real one absorbs it:
  assert.equal(await readErrorDetail(exploding), null);
});
