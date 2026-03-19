#!/usr/bin/env node
/**
 * PreToolUse hook that intercepts Task tool calls and redirects
 * general-purpose subagent spawns through AgentProcess (piped I/O).
 *
 * This makes team agents VISIBLE in the Agent Panel conversation UI
 * instead of running as invisible native subagents.
 *
 * Only intercepts subagent_type: "general-purpose" (team coding agents).
 * Quick utility tasks (Explore, Plan, Bash, haiku) pass through natively.
 *
 * Flow:
 * 1. Task tool call detected with subagent_type = "general-purpose"
 * 2. Hook calls POST /api/spawn/agent to spawn via AgentProcess
 * 3. Hook exits with code 2 to block the native Task tool
 * 4. Agent sees message explaining the subagent was spawned visibly
 */
const http = require('http');
const fs = require('fs');
const path = require('path');
const os = require('os');

const API_PORT = 5050;
const API_TIMEOUT = 15000; // Agent spawn can take a few seconds
const DEBUG_LOG = path.join(os.tmpdir(), 'mt-task-hook-debug.log');

// Subagent types that should be redirected to AgentProcess
// Empty = all agents run natively (results flow back via Task tool, Agent Panel reads JSONL for display)
const REDIRECT_TYPES = [];

const PENDING_DESC_FILE = path.join(os.tmpdir(), 'mt-pending-agent-descriptions.json');
const TEAM_SPAWNER_FILE = path.join(os.tmpdir(), 'mt-team-spawners.json');

/**
 * Write task description to a pending file so subagent-office-hook.js
 * can pick it up on SubagentStart and store it in the tracking entry.
 * Used for non-redirected agents (Explore, Plan) where the description
 * is only available in the PreToolUse hook.
 */
function writePendingDescription(description, name, subagentType) {
  try {
    let pending = [];
    if (fs.existsSync(PENDING_DESC_FILE)) {
      pending = JSON.parse(fs.readFileSync(PENDING_DESC_FILE, 'utf8'));
      if (!Array.isArray(pending)) pending = [];
    }
    pending.push({ description, name, subagentType, timestamp: Date.now() });
    fs.writeFileSync(PENDING_DESC_FILE, JSON.stringify(pending), 'utf8');
    log(`PENDING DESC written: "${description}" (${subagentType})`);
  } catch (e) {
    log(`PENDING DESC write error: ${e.message}`);
  }
}

/**
 * Write team→spawner mapping so TeamWatcherService can route
 * agent panels to the correct terminal.
 */
function writeTeamSpawner(teamName, spawnerName) {
  try {
    let mapping = {};
    if (fs.existsSync(TEAM_SPAWNER_FILE)) {
      mapping = JSON.parse(fs.readFileSync(TEAM_SPAWNER_FILE, 'utf8'));
      if (typeof mapping !== 'object' || Array.isArray(mapping)) mapping = {};
    }
    mapping[teamName] = { spawnerName, timestamp: Date.now() };
    const tempPath = TEAM_SPAWNER_FILE + '.tmp';
    fs.writeFileSync(tempPath, JSON.stringify(mapping, null, 2), 'utf8');
    fs.renameSync(tempPath, TEAM_SPAWNER_FILE);
    log(`TEAM SPAWNER written: ${teamName} → ${spawnerName}`);
  } catch (e) {
    log(`TEAM SPAWNER write error: ${e.message}`);
  }
}

function log(msg) {
  const timestamp = new Date().toISOString();
  fs.appendFileSync(DEBUG_LOG, `${timestamp} ${msg}\n`);
}

function callApi(apiPath, method, body) {
  return new Promise((resolve) => {
    const options = {
      hostname: 'localhost',
      port: API_PORT,
      path: apiPath,
      method: method,
      headers: { 'Content-Type': 'application/json' },
      timeout: API_TIMEOUT
    };

    const req = http.request(options, (res) => {
      let data = '';
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => {
        try {
          resolve({ ok: res.statusCode === 200, status: res.statusCode, data: JSON.parse(data) });
        } catch (e) {
          resolve({ ok: res.statusCode === 200, status: res.statusCode, data: {} });
        }
      });
    });
    req.on('error', (err) => {
      log(`API ERROR: ${err.message}`);
      resolve({ ok: false, status: 0, data: {} });
    });
    req.on('timeout', () => {
      req.destroy();
      log('API TIMEOUT');
      resolve({ ok: false, status: 0, data: {} });
    });

    if (body) {
      req.write(JSON.stringify(body));
    }
    req.end();
  });
}

async function main() {
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch (err) {
    log(`PARSE ERROR: ${err.message}`);
    // Can't parse — let the tool proceed
    process.exit(0);
    return;
  }

  const toolName = hookData.tool_name;
  log(`HOOK FIRED: tool=${toolName}`);

  // Only intercept Task tool calls
  if (toolName !== 'Task') {
    process.exit(0);
    return;
  }

  // Parse tool input
  const toolInput = hookData.tool_input || hookData.input || {};
  const subagentType = toolInput.subagent_type || '';
  const agentName = toolInput.name || toolInput.description || 'Agent';
  const prompt = toolInput.prompt || '';
  const spawnerName = process.env.MULTITERMINAL_NAME || 'Claude';

  log(`TASK TOOL: subagent_type=${subagentType}, name=${agentName}, team_name=${toolInput.team_name || '(none)'}, prompt=${prompt.substring(0, 100)}...`);

  // Let native team agent spawns pass through (team_name = native agent team)
  if (toolInput.team_name) {
    log(`PASS-THROUGH: Native team agent spawn (team_name=${toolInput.team_name})`);
    // Write description so TeamWatcherService can pick it up via tracking
    writePendingDescription(toolInput.description || '', toolInput.name || '', subagentType || 'general-purpose');
    // Write team→spawner mapping so TeamWatcherService knows which terminal owns this team
    writeTeamSpawner(toolInput.team_name, spawnerName);
    process.exit(0);
    return;
  }

  // Only redirect general-purpose agents
  if (!REDIRECT_TYPES.includes(subagentType)) {
    log(`PASS-THROUGH: ${subagentType} is not redirected`);
    // Write description to pending file so subagent-office-hook can pick it up
    writePendingDescription(toolInput.description || '', toolInput.name || '', subagentType);
    process.exit(0);
    return;
  }

  // Spawn via AgentProcess REST API
  log(`REDIRECTING to AgentProcess: ${agentName}`);
  const result = await callApi('/api/spawn/agent', 'POST', {
    agentName: agentName,
    initialPrompt: prompt,
    spawnerName: spawnerName,
    taskDescription: toolInput.description || '',
    subagentType: subagentType
  });

  if (result.ok) {
    log(`SPAWN SUCCESS: ${agentName} (PID: ${result.data.processId})`);
    // Output message that the agent will see
    // Exit code 2 = block tool; Claude Code reads the reason from stderr
    const msg = `Spawned visible agent "${agentName}" via AgentProcess (PID: ${result.data.processId}). The conversation is live in the Agent Panel — the user can observe and interact. Do NOT retry this Task call.`;
    process.stderr.write(msg);
    process.exit(2);
  } else {
    // Spawn failed — let the native Task tool proceed as fallback
    log(`SPAWN FAILED: ${JSON.stringify(result.data)} — falling through to native Task`);
    process.exit(0);
  }
}

main().catch((err) => {
  log(`HOOK ERROR: ${err.message}`);
  // On error, let the tool proceed
  process.exit(0);
});
