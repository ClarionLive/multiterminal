import { test } from "node:test";
import assert from "node:assert/strict";
import { formatTasks } from "../lib/task-formatters.mjs";
import { formatClaudeUsage } from "../lib/context-formatters.mjs";

const tasks = [{
  id: "30f64711-abcd",
  title: "Compact task output",
  description: "The full brief should only appear when requested.",
  status: "in_progress",
  priority: "high",
  assignee: "Bob",
}];

test("list_tasks is compact by default and includes every summary field", () => {
  const output = formatTasks(tasks);
  assert.equal(output, "Tasks:\n[30f64711] Compact task output | in_progress | high | Bob");
  assert.doesNotMatch(output, /full brief/);
});

test("list_tasks includeDescriptions restores description previews", () => {
  assert.match(formatTasks(tasks, true), /The full brief should only appear when requested\./);
});

test("missing Claude usage is labelled unknown rather than zero", () => {
  assert.equal(formatClaudeUsage({}), "Claude usage — 5h: unknown  7d: unknown");
  assert.doesNotMatch(formatClaudeUsage({}), /0%/);
});

test("Claude usage includes reset times when known", () => {
  assert.equal(
    formatClaudeUsage({ fiveHourPercent: 42, fiveHourResetIn: "2h 15m", sevenDayPercent: 67, sevenDayResetIn: "3d 4h" }),
    "Claude usage — 5h: 42% (resets in 2h 15m)  7d: 67% (resets in 3d 4h)",
  );
});
