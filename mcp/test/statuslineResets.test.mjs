import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("../../scripts/statusline.js", import.meta.url));
const MINUTE = 60;
const nowSeconds = () => Math.floor(Date.now() / 1000);

// TEMP and TMP point at a scratch folder so this can never touch the live mt-statusline files.
function runStatusline(rateLimits) {
  const dir = mkdtempSync(path.join(tmpdir(), "statusline-test-"));
  try {
    const result = spawnSync(process.execPath, [script], {
      input: JSON.stringify({ rate_limits: rateLimits }),
      encoding: "utf8",
      env: {
        ...process.env,
        TEMP: dir,
        TMP: dir,
        TMPDIR: dir,
        MULTITERMINAL_NAME: "UsageTest",
        MULTITERMINAL_DOC_ID: "doc1",
      },
    });
    assert.equal(result.status, 0, result.stderr);
    return JSON.parse(readFileSync(path.join(dir, "mt-statusline-UsageTest-doc1.json"), "utf8"));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test("resets_at in Unix seconds gives the 7-day reset and pace", () => {
  // 2 days and a little over an hour ahead, so the floor still lands on 2d 1h.
  const remainingMinutes = 2 * 1440 + 90;
  const stats = runStatusline({
    seven_day: { used_percentage: 87, resets_at: nowSeconds() + remainingMinutes * MINUTE },
  });
  assert.equal(stats.quota7d, 87);
  assert.equal(stats.resetIn7d, "2d 1h");
  const expectedPace = Math.round(((10080 - remainingMinutes) * 100) / 10080 - 87);
  assert.ok(Math.abs(stats.pace7d - expectedPace) <= 1, `pace7d was ${stats.pace7d}, expected about ${expectedPace}`);
});

test("resets_at in Unix seconds gives the 5-hour reset", () => {
  const stats = runStatusline({
    five_hour: { used_percentage: 10, resets_at: nowSeconds() + 150 * MINUTE + 30 },
  });
  assert.equal(stats.quota5h, 10);
  assert.equal(stats.resetIn5h, "2h 30m");
});

test("an absent window stays null and does not affect the other", () => {
  const stats = runStatusline({
    seven_day: { used_percentage: 87, resets_at: nowSeconds() + 1440 * MINUTE + 30 },
  });
  assert.equal(stats.quota5h, null);
  assert.equal(stats.pace5h, null);
  assert.equal(stats.resetIn5h, null);
  assert.equal(stats.resetIn7d, "1d 0h");
});

test("a missing percentage stays null rather than 0", () => {
  const stats = runStatusline({ seven_day: { resets_at: nowSeconds() + 1440 * MINUTE + 30 } });
  assert.equal(stats.quota7d, null);
  assert.equal(stats.pace7d, null);
  assert.equal(stats.resetIn7d, "1d 0h");
});

test("an unreadable or past reset gives no countdown and no pace", () => {
  for (const resets_at of ["not a date", nowSeconds() - 60, null, undefined, 0]) {
    const stats = runStatusline({ seven_day: { used_percentage: 87, resets_at } });
    assert.equal(stats.quota7d, 87);
    assert.equal(stats.resetIn7d, null);
    assert.equal(stats.pace7d, null);
  }
});

test("an ISO resets_at is still accepted", () => {
  const stats = runStatusline({
    seven_day: {
      used_percentage: 50,
      resets_at: new Date(Date.now() + (1440 * MINUTE + 30) * 1000).toISOString(),
    },
  });
  assert.equal(stats.resetIn7d, "1d 0h");
});
