#!/usr/bin/env node
/**
 * Test hook for TeammateIdle event (Claude Code v2.1.33+)
 * Purpose: Discover payload structure and behavior
 */

const fs = require('fs');
const path = require('path');

// Log directory
const logDir = path.join(process.env.APPDATA || '', 'multiterminal');
const logFile = path.join(logDir, 'hook-test-teammate-idle.log');

// Ensure log directory exists
if (!fs.existsSync(logDir)) {
  fs.mkdirSync(logDir, { recursive: true });
}

try {
  const timestamp = new Date().toISOString();

  // Capture all available data
  const hookData = process.argv[2] ? JSON.parse(process.argv[2]) : null;
  const stdin = process.stdin.isTTY ? null : 'stdin available';

  // Capture relevant environment variables
  const envVars = Object.keys(process.env)
    .filter(k =>
      k.startsWith('CLAUDE_') ||
      k.startsWith('MULTITERMINAL_') ||
      k.startsWith('TEAMMATE_') ||
      k === 'AGENT_ID' ||
      k === 'AGENT_NAME'
    )
    .reduce((acc, k) => ({ ...acc, [k]: process.env[k] }), {});

  const logEntry = {
    timestamp,
    event: 'TeammateIdle',
    hookData,
    stdin,
    envVars,
    allArgs: process.argv,
    workingDir: process.cwd()
  };

  const logLine = JSON.stringify(logEntry, null, 2) + '\n' + '='.repeat(80) + '\n';
  fs.appendFileSync(logFile, logLine);

  // Also log to console for immediate feedback
  console.error(`[TeammateIdle Hook] Logged to ${logFile}`);
  console.error(`Data: ${JSON.stringify(hookData)}`);
} catch (err) {
  const errorLog = `ERROR at ${new Date().toISOString()}: ${err.message}\n${err.stack}\n` + '='.repeat(80) + '\n';
  fs.appendFileSync(logFile, errorLog);
  console.error(`[TeammateIdle Hook] ERROR: ${err.message}`);
}

// Always exit successfully (hooks must not break Claude)
process.exit(0);
