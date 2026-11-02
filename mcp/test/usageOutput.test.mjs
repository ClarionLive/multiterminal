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

test("missing 7-day usage is labelled unknown rather than zero, and missing 5-hour usage is left out", () => {
  assert.equal(formatClaudeUsage({}), "Claude usage — 7d: unknown");
  assert.doesNotMatch(formatClaudeUsage({}), /0%/);
});

test("a plan with no 5-hour limit shows only the 7-day window", () => {
  assert.equal(
    formatClaudeUsage({ fiveHourPercent: null, sevenDayPercent: 87, sevenDayResetIn: "1d 9h" }),
    "Claude usage — 7d: 87% (resets in 1d 9h)",
  );
});

test("a real 0% 5-hour figure is still shown", () => {
  assert.equal(
    formatClaudeUsage({ fiveHourPercent: 0, sevenDayPercent: 87 }),
    "Claude usage — 5h: 0%  7d: 87%",
  );
});

test("Claude usage includes reset times when known", () => {
  assert.equal(
    formatClaudeUsage({ fiveHourPercent: 42, fiveHourResetIn: "2h 15m", sevenDayPercent: 67, sevenDayResetIn: "3d 4h" }),
    "Claude usage — 5h: 42% (resets in 2h 15m)  7d: 67% (resets in 3d 4h)",
  );
});
