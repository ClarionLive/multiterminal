// send_push_notification delivery-report rendering (task 2f7280c2)
//
// WHY THIS FILE EXISTS
// The tool used to render `✅ Push delivered to ${ok}/${subs} device(s)` using
// push_result.subscriptionCount as the denominator. The gateway prunes dead subscriptions and
// only THEN reads _subscriptions.Count into that field, so failures are already gone from the
// number by the time the tool sees it. When every failure is prunable (the common stale-sub case)
// the denominator collapses to exactly successCount and ANY partial failure renders as a perfect
// N/N. A real 4-attempt/2-failed send displayed as "2/2" and caused a verifying agent to report
// that no failures had occurred (observed live on task 8fc66298 item [4]).
//
// The fix derives the denominator (`attempted = successCount + errorCount`) and guards the
// delivered branch against counts that cannot confirm delivery. BOTH are invariants held across a
// process boundary in C# — they are underivable from this JS, so nothing but a test stops a future
// refactor from "simplifying" the denominator back to subscriptionCount or loosening the guard.
// Three independent pipeline gates (verifier, code-reviewer, and the Codex cross-model adversary)
// each flagged that the guard shipped without one: "the guard is now itself unguarded."
//
// APPROACH
// Follows the house pattern of consistency.test.mjs / pathEncoding.test.mjs: read mcp/index.js as
// TEXT and never import it (importing would pull in the MCP SDK and the whole server). The render
// block is extracted from the real source and executed, so these tests exercise the shipping code
// rather than a retyped copy. The extraction is asserted non-vacuous, and negative fixtures prove
// the assertions still falsify — a test that passes on an empty slice is worse than no test.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const BLOCK_START = "const pr = (res && res.push_result) || {};";
const BLOCK_END = "⚠️ Notification recorded but NOT delivered to phone:";

// Slice the send_push_notification response-rendering block out of the real source. Throws loudly
// if the markers move — a silent miss would make every assertion below vacuously true.
function extractRenderBlock(source) {
  const i = source.indexOf(BLOCK_START);
  assert.ok(i >= 0, `render block start marker not found in mcp/index.js: ${BLOCK_START}`);
  const j = source.indexOf(BLOCK_END, i);
  assert.ok(j >= 0, `render block end marker not found after start: ${BLOCK_END}`);
  const k = source.indexOf("};", source.indexOf("}]", j)) + 2;
  assert.ok(k > j, "could not find the end of the not-delivered return statement");
  return source.slice(i, k);
}

const block = extractRenderBlock(src);

// The guard's copy is asserted in six places. Hoisting it declares the copy-coupling ONCE: these
// tests are deliberately coupled to the message text because the text IS the product — an agent
// reads it and decides whether to retry — but a copy edit should mean one update here, not six.
const UNCONFIRMED = /could NOT be confirmed/;

// Execute the extracted source with an injected scope. `args` only needs notification_type.
function renderWith(source, res, notificationType = "ready_for_testing") {
  const fn = new Function("res", "args", `${source}\nreturn undefined;`);
  return fn(res, { notification_type: notificationType })?.content?.[0]?.text;
}

const render = (res, notificationType) => renderWith(block, res, notificationType);

test("extraction is non-vacuous — the block carries the load-bearing tokens", () => {
  // If a refactor renames these, this file must fail rather than silently testing nothing.
  assert.match(block, /const attempted = ok \+ errs;/, "derived denominator missing");
  assert.match(block, /countsConfirmDelivery/, "delivered-branch guard missing");
  assert.match(block, /res\.delivered/, "delivered branch missing");
  assert.ok(block.length > 200, "extracted block suspiciously short");
});

test("denominator is what was ATTEMPTED, not the post-prune subscription count", () => {
  // task 8fc66298 item [4]'s real send: 4 attempted, 2 delivered, 2 dead ghosts pruned.
  // subscriptionCount is 2 *after* the prune — using it renders a flawless "2/2".
  const text = render({
    delivered: true,
    push_result: { subscriptionCount: 2, successCount: 2, errorCount: 2 },
  });
  assert.match(text, /2\/4 device\(s\)/, "denominator must be attempted (2+2), not post-prune count");
  assert.match(text, /2 failed/, "partial failure must be named, not silently dropped");
  assert.doesNotMatch(text, /2\/2/, "the pre-fix false-success ratio must not reappear");
});

test("a fully successful send is unchanged (no regression on the common path)", () => {
  const text = render({
    delivered: true,
    push_result: { subscriptionCount: 2, successCount: 2, errorCount: 0 },
  });
  assert.match(text, /✅ Push delivered to 2\/2 device\(s\)/);
  assert.doesNotMatch(text, /failed/, "must not append a failure note when nothing failed");
});

test("total failure still reports its counts once every sub is pruned away", () => {
  // Every sub died and was pruned, so subscriptionCount is 0. Gating the detail on that count
  // suppressed it in exactly the case it exists to explain.
  const text = render({
    delivered: false,
    push_result: { subscriptionCount: 0, successCount: 0, errorCount: 3 },
  });
  assert.match(text, /0 ok \/ 3 failed of 3 attempted/);
});

test("nothing attempted renders no count detail", () => {
  const text = render({
    delivered: false,
    push_result: { subscriptionCount: 0, successCount: 0, errorCount: 0, error: "No subscriptions" },
  });
  assert.match(text, /NOT delivered/);
  assert.doesNotMatch(text, /attempted/, "no counts to show when nothing was tried");
});

test("a user-muted notification type stays distinct from a delivery failure", () => {
  const text = render({ delivered: false, reason: "type-disabled-by-user", push_result: {} });
  assert.match(text, /disabled in MultiRemote/);
  assert.doesNotMatch(text, /NOT delivered to phone/, "a deliberate mute is not a failure");
});

// --- The guard: `delivered` is only meaningful alongside counts that can confirm it ---------------
// NotificationsController sets Delivered (from `pushed`) and PushResult in two INDEPENDENT ifs over
// a nullable PushResult, so "delivered => counts present" and "delivered => successCount > 0" are
// both held by belief at the point of consumption. Unreachable via the in-process gateway today,
// but reachable if a foreign listener occupies the gateway port, and one refactor away otherwise.

test("delivered with an absent push_result does not render a green 0/0", () => {
  const text = render({ delivered: true });
  assert.match(text, UNCONFIRMED);
  assert.doesNotMatch(text, /✅/, "must not claim success on counts we never received");
});

test("delivered with an empty push_result does not render a green 0/0", () => {
  const text = render({ delivered: true, push_result: {} });
  assert.match(text, UNCONFIRMED);
  assert.doesNotMatch(text, /✅/);
});

test("delivered with ZERO successes does not render a green ratio", () => {
  // "✅ Push delivered to 0/5 device(s) — 5 failed" asserts delivery no device received. Worse than
  // 0/0: it looks specific enough to believe.
  const text = render({ delivered: true, push_result: { successCount: 0, errorCount: 5 } });
  assert.match(text, UNCONFIRMED);
  assert.doesNotMatch(text, /✅/);
});

test("delivered with non-integer counts does not render a concatenated ratio", () => {
  // `ok + errs` on strings concatenates: "2" + "2" would render a nonsense "2/22".
  const text = render({ delivered: true, push_result: { successCount: "2", errorCount: "2" } });
  assert.match(text, UNCONFIRMED);
  assert.doesNotMatch(text, /2\/22/);
  // Raw values are quoted so the type drift is visible; a bare 2 would hide why it was rejected.
  assert.match(text, /successCount="2"/);
});

test("the guard does not fire on legitimate partial successes", () => {
  for (const [ok, errs, expected] of [[1, 0, "1/1"], [1, 5, "1/6"], [3, 7, "3/10"]]) {
    const text = render({ delivered: true, push_result: { successCount: ok, errorCount: errs } });
    assert.match(text, new RegExp(`✅ Push delivered to ${expected.replace("/", "\\/")} device\\(s\\)`));
    assert.doesNotMatch(text, UNCONFIRMED, `false positive on ${ok} ok / ${errs} failed`);
  }
});

test("the guard is delivered-only and leaves the not-delivered path alone", () => {
  const text = render({ delivered: false, push_result: { successCount: 0, errorCount: 0 } });
  assert.match(text, /NOT delivered/);
  assert.doesNotMatch(text, UNCONFIRMED);
});

// --- Negative fixtures: prove the assertions above still falsify ----------------------------------
// Same discipline as pathEncoding.test.mjs's "census FLAGS ..." cases. Each mutates the REAL block
// to reintroduce a bug this file exists to catch, and asserts the bug is observable — so if a
// future edit made these tests vacuous, these would fail first.
//
// Each fixture asserts its own mutation APPLIED. Without that, a fixture whose .replace() target
// has drifted out of the source silently becomes a no-op — it would then render the real (correct)
// code and, in at least one case, still pass while the regression it names is live. A fixture that
// cannot fail in the case it exists for is the same false-green this whole file is about.
function mutate(source, find, replaceWith, why) {
  const mutated = source.replace(find, replaceWith);
  assert.notEqual(mutated, source, `negative fixture is a NO-OP — ${why}. Update the fixture.`);
  return mutated;
}

test("negative fixture: reverting to the post-prune denominator reproduces the 2/2 lie", () => {
  let reverted = mutate(
    block,
    "const attempted = ok + errs;",
    "const attempted = pr.subscriptionCount ?? 0;",
    "the derived-denominator line was not found in the shipping source",
  );
  reverted = mutate(
    reverted,
    /&& attempted > 0 && ok > 0/,
    "&& true",
    "the guard conjuncts were not found in the shipping source",
  );
  const text = renderWith(reverted, {
    delivered: true,
    push_result: { subscriptionCount: 2, successCount: 2, errorCount: 2 },
  });
  assert.match(text, /2\/2 device\(s\)/, "the pre-fix bug must still be reproducible");
  assert.doesNotMatch(text, /2\/4/, "reverted code must not somehow still be correct");
});

test("negative fixture: dropping the ok>0 conjunct reopens the green 0/5", () => {
  const weakened = mutate(
    block,
    " && ok > 0",
    "",
    "the ok>0 conjunct is ALREADY ABSENT from the shipping guard — the regression this fixture " +
      "names may be live (see the 'delivered with ZERO successes' test, which is its primary gate)",
  );
  const text = renderWith(weakened, {
    delivered: true,
    push_result: { successCount: 0, errorCount: 5 },
  });
  assert.match(text, /✅ Push delivered to 0\/5 device\(s\)/, "the half-done guard must still be observable");
});
