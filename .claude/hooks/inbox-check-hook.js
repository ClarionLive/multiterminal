#!/usr/bin/env node
/**
 * inbox-check-hook.js
 *
 * Claude Code hook that checks for pending messages in a file-based inbox.
 * Designed for PostToolUse and Stop hooks to deliver inter-terminal messages
 * to Claude Code agents without ConPTY injection.
 *
 * Inbox file: %APPDATA%/multiterminal/inbox/{MULTITERMINAL_NAME}.json
 * Format: JSON array of [{id, sender, content, timestamp}]
 *
 * Behavior:
 *   - No inbox file → exit 0 silently (fast path, < 30ms)
 *   - Inbox exists → read, delete, format messages for Claude
 *   - Stop hooks → JSON with decision:"block" to keep Claude processing
 *   - Other hooks → plain text stdout as additional context
 *   - Any error → exit 0 silently (never block Claude)
 */
const fs = require('fs');
const path = require('path');

async function main() {
  const name = process.env.MULTITERMINAL_NAME;
  if (!name) {
    process.exit(0);
    return;
  }

  const inboxPath = path.join(process.env.APPDATA || '', 'multiterminal', 'inbox', name + '.json');

  // Fast path: no inbox file means no messages
  if (!fs.existsSync(inboxPath)) {
    process.exit(0);
    return;
  }



  // Read and delete inbox file (atomic: read then unlink)
  let raw;
  try {
    raw = fs.readFileSync(inboxPath, 'utf8');
    fs.unlinkSync(inboxPath);
  } catch (e) {
    // File may have been consumed by another process
    process.exit(0);
    return;
  }

  // Parse messages
  let messages;
  try {
    messages = JSON.parse(raw);
  } catch (e) {
    process.exit(0);
    return;
  }

  if (!Array.isArray(messages) || messages.length === 0) {
    process.exit(0);
    return;
  }

  // Format messages
  const lines = ['## Incoming Messages'];
  for (const msg of messages) {
    const sender = msg.Sender || msg.sender;
    const content = msg.Content || msg.content;
    if (msg && sender && content) {
      lines.push(`[${sender}]: ${content}`);
    }
  }

  if (lines.length === 1) {
    // No valid messages after filtering
    process.exit(0);
    return;
  }

  const formatted = lines.join('\n');

  // Hook type passed as command-line argument (avoids slow stdin reading)
  const hookType = process.argv[2] || '';

  // Output based on hook type
  if (hookType === 'Stop' || hookType === 'SubagentStop') {
    // Block stopping so Claude processes the messages
    const output = {
      decision: 'block',
      reason: formatted
    };
    console.log(JSON.stringify(output));
  } else {
    // For PostToolUse, PreToolUse, etc. — plain text becomes context
    console.log(formatted);
  }
}

main().catch(() => {
  process.exit(0);
});
