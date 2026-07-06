// Pins the delete_project id-shape validation (ticket ec97c446 H1 — path
// traversal). Runs with the built-in runner, no deps:  node --test mcp/test/
//
// This test extracts the ACTUAL PROJECT_ID_RE literal from the shipped
// mcp/index.js and asserts against it, so the shape contract is pinned to the
// real regex rather than a copy that could silently drift. Project ids are
// either an 8-char hex short id or a full canonical GUID; anything carrying a
// path metacharacter (/, \, ., %, ?, #) must be rejected before it can be
// interpolated into a REST path and normalized into a sibling-route request.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");
const m = src.match(/const PROJECT_ID_RE = (\/[^\n]*\/);/);
assert.ok(m, "PROJECT_ID_RE literal not found in mcp/index.js — did the validator move or get renamed?");
// Reconstruct the regex from its source literal (strip the surrounding slashes).
// No flags are used on PROJECT_ID_RE; this stays true if that remains the case.
const body = m[1].slice(1, -1);
const PROJECT_ID_RE = new RegExp(body);

test("accepts both real project-id shapes (positives)", () => {
  for (const id of [
    "2d9643b7", // 8-char hex short id
    "3138ca72-4462-4ffd-a955-4049a76ad6c0", // full canonical GUID
  ]) {
    assert.ok(PROJECT_ID_RE.test(id), `should accept ${id}`);
  }
});

test("rejects path-traversal and malformed ids (negatives)", () => {
  for (const id of [
    "../tasks/x", // the core traversal payload
    "%2e%2e", // percent-encoded dot-dot
    "3138ca72-4462-4ffd-a955-4049a76ad6c0/", // valid GUID with a trailing slash
    "../tasks/abc123",
    "2d9643b7/../tasks",
    "..%2ftasks",
    "", // empty
  ]) {
    assert.ok(!PROJECT_ID_RE.test(id), `should reject ${JSON.stringify(id)}`);
  }
});
