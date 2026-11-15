// respond_to_stale (ticket 28845a5f): the stale-task reminder named this tool for years while no tool
// or route existed. Pins that the tool posts to the route the C# controller serves, and that the
// tool's enum is exactly the controller's accepted list, so the two cannot drift apart again.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const indexSrc = readFileSync(path.join(here, "..", "index.js"), "utf8");
const controllerSrc = readFileSync(path.join(here, "..", "..", "API", "Controllers", "TasksController.cs"), "utf8");

function toolDef(name) {
  const start = indexSrc.indexOf(`name: "${name}",`);
  assert.ok(start > 0, `tool definition for ${name} not found`);
  return indexSrc.slice(start, indexSrc.indexOf("\n      },\n", start));
}

function handler(name) {
  const start = indexSrc.indexOf(`      case "${name}": {`);
  assert.ok(start > 0, `handler for ${name} not found`);
  return indexSrc.slice(start, indexSrc.indexOf("\n      }\n", start));
}

test("respond_to_stale posts to the stale-response route", () => {
  const body = handler("respond_to_stale");
  assert.match(body, /\/api\/tasks\/\$\{seg\(args\.taskId\)\}\/stale-response`, "POST"/);
  assert.match(controllerSrc, /\[HttpPost\("\{taskId\}\/stale-response"\)\]/);
});

test("the tool's answers are exactly the ones the route accepts", () => {
  const enumMatch = toolDef("respond_to_stale").match(/enum:\s*\[([^\]]+)\]/);
  assert.ok(enumMatch, "enum not found in the tool definition");
  const toolValues = [...enumMatch[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]);

  const allowedMatch = controllerSrc.match(/Allowed\s*=\s*new\[\]\s*\{([^}]+)\}/);
  assert.ok(allowedMatch, "StaleResponseRequest.Allowed not found in TasksController.cs");
  const routeValues = [...allowedMatch[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]);

  assert.deepEqual(toolValues, routeValues);
  assert.equal(toolValues.length, 3);
});
