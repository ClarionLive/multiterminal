/**
 * pipeline-trigger-hook.js — PostToolUse hook for update_task_checklist
 *
 * After any checklist update, checks if ALL items are now in "testing" or "done".
 * If so, outputs a message telling the agent to auto-run the pipeline.
 *
 * This is a safety net for Option A (kanban-task skill auto-trigger).
 * Even if the skill logic is bypassed (e.g., agent updates checklist directly
 * without using /kanban-task), this hook catches it.
 *
 * Hook type: PostToolUse
 * Matcher: mcp__multiterminal__update_task_checklist
 */

const http = require('http');

// Read stdin (hook receives JSON with tool_name, tool_input, tool_output)
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => input += chunk);
process.stdin.on('end', async () => {
  try {
    const hookData = JSON.parse(input);
    const toolInput = hookData.tool_input || {};
    const taskId = toolInput.taskId;

    if (!taskId) {
      // No task ID — can't check, just exit silently
      process.exit(0);
    }

    // Query the REST API for full task detail
    const taskData = await getTaskDetail(taskId);
    if (!taskData) {
      process.exit(0);
    }

    // Parse checklist
    const checklist = parseChecklist(taskData);
    if (!checklist || checklist.length === 0) {
      process.exit(0);
    }

    // Check if ALL items are in "testing" or "done"
    const allTestingOrDone = checklist.every(item =>
      item.status === 'testing' || item.status === 'done'
    );

    // Check there's at least one "testing" item (not all done already)
    const hasTestingItems = checklist.some(item => item.status === 'testing');

    if (allTestingOrDone && hasTestingItems) {
      const testingCount = checklist.filter(i => i.status === 'testing').length;
      const doneCount = checklist.filter(i => i.status === 'done').length;

      console.log(`AUTO-PIPELINE TRIGGER: All ${checklist.length} checklist items are in testing (${testingCount}) or done (${doneCount}). ` +
        `No pending or coding items remain. ` +
        `You MUST run the pipeline now — invoke Skill(skill="pipeline") immediately. ` +
        `Do NOT ask the user for permission. The pipeline must pass before presenting items for manual testing.`);
    }

    process.exit(0);
  } catch (err) {
    // Hook errors should not block the agent — fail silently
    process.exit(0);
  }
});

/**
 * Fetch task detail from MultiTerminal REST API
 */
function getTaskDetail(taskId) {
  return new Promise((resolve) => {
    const options = {
      hostname: 'localhost',
      port: 5050,
      path: `/api/tasks/${taskId}`,
      method: 'GET',
      timeout: 3000
    };

    const req = http.request(options, (res) => {
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        try {
          resolve(JSON.parse(data));
        } catch {
          resolve(null);
        }
      });
    });

    req.on('error', () => resolve(null));
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.end();
  });
}

/**
 * Parse checklist from task data (handles both JSON string and array)
 */
function parseChecklist(taskData) {
  try {
    let checklist = taskData.checklist || taskData.checklist_json;
    if (typeof checklist === 'string') {
      checklist = JSON.parse(checklist);
    }
    if (Array.isArray(checklist)) {
      return checklist;
    }
    return null;
  } catch {
    return null;
  }
}
