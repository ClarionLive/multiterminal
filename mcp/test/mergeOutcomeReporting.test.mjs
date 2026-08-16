// Falsifiable gate for task b88e7017 item ③ — the defect that hid a 7-week outage.
//
// THE BUG: `update_task_status` did `await apiCall(...)` and threw the response
// away, then answered a flat "✅ Task <id> status updated to: done". Marking a
// task done ALSO runs the post-prune auto-merge, and when that merge was refused
// (project git_default_branch said 'master' after the repo moved to 'main') the
// refusal went only to the activity feed. The worktree-pruning broadcast still
// fired, so every signal the caller could see read as success. Thirteen branches
// accumulated unmerged before anyone noticed.
//
// The fix is only meaningful if the handler KEEPS reporting the outcome, so this
// gate is structural over mcp/index.js source — the same
// extract-and-assert-against-the-real-source contract as consistency.test.mjs and
// pathEncoding.test.mjs (never imports index.js: it is a stdio server that would
// run forever, and the SDK import needs its own npm ci).
//
// A NEGATIVE FIXTURE at the bottom re-runs every assertion against a synthetic
// copy of the PRE-FIX handler and requires each to fail. Without that, these
// checks could silently become vacuous — a census that cannot fail is a census
// that proves nothing.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const indexPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "index.js");
const src = readFileSync(indexPath, "utf8");

const CASE_MARKER = 'case "update_task_status": {';

/** Slice the handler body: from its `case` marker to the start of the next `case "`. */
function extractHandler(source, marker) {
  const start = source.indexOf(marker);
  if (start === -1) return null;
  const rest = source.slice(start + marker.length);
  const nextCase = rest.search(/^ {6}case\s+"/m);
  return nextCase === -1 ? rest : rest.slice(0, nextCase);
}

/**
 * The three properties that together mean "a refused merge reaches the caller".
 * Returned as a list so the negative fixture can assert each one flips.
 */
function auditHandler(body) {
  return {
    // 1. The response is captured at all. The pre-fix form was a bare
    //    `await apiCall(` whose return value was discarded.
    capturesResponse: /=\s*await apiCall\(/.test(body),
    // 2. It reads the merge outcome the REST layer now returns.
    readsMergeOutcome: /mergeOutcome/.test(body),
    // 3. It branches on the attention flag — the specific case that was silent.
    //    Merely printing a message is not enough; a refusal must be distinguishable.
    surfacesNeedsAttention: /needsAttention/.test(body),
  };
}

test("the update_task_status handler still exists where the census expects it", () => {
  assert.ok(
    src.includes(CASE_MARKER),
    `pin failed: '${CASE_MARKER}' not found in mcp/index.js — the handler was renamed or reshaped, ` +
      "so this gate is no longer auditing what it thinks it is. Update the marker deliberately.",
  );
});

test("update_task_status reports the auto-merge outcome to its caller", () => {
  const body = extractHandler(src, CASE_MARKER);
  assert.ok(body, "could not extract the update_task_status handler body");

  const audit = auditHandler(body);

  assert.ok(
    audit.capturesResponse,
    "update_task_status discards the API response (bare `await apiCall(...)`). That is the exact pre-b88e7017 " +
      "shape: the merge outcome cannot be reported if the response is never captured.",
  );
  assert.ok(
    audit.readsMergeOutcome,
    "update_task_status never reads `mergeOutcome`, so a refused auto-merge is invisible to the agent that " +
      "marked the task done.",
  );
  assert.ok(
    audit.surfacesNeedsAttention,
    "update_task_status never branches on `needsAttention`. A refused merge must be distinguishable from a " +
      "benign skip — reporting both as prose is how 13 branches went unmerged unnoticed.",
  );
});

test("negative fixture: the pre-fix handler fails every assertion", () => {
  // Verbatim shape of the handler before b88e7017.
  const preFix = `
        await apiCall(\`/api/tasks/\${seg(args.taskId)}/status\`, "PATCH", {
          status: args.status,
          updatedBy: args.updatedBy,
        });
        return {
          content: [
            {
              type: "text",
              text: \`✅ Task \${args.taskId} status updated to: \${args.status}\`,
            },
          ],
        };
      }
`;

  const audit = auditHandler(preFix);

  assert.equal(audit.capturesResponse, false, "negative fixture is vacuous: the pre-fix handler must not capture the response");
  assert.equal(audit.readsMergeOutcome, false, "negative fixture is vacuous: the pre-fix handler must not read mergeOutcome");
  assert.equal(audit.surfacesNeedsAttention, false, "negative fixture is vacuous: the pre-fix handler must not surface needsAttention");
});
