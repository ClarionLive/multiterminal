#!/usr/bin/env node
/**
 * Session Status Hook for Claude Code
 * Marks terminal profiles online/offline based on SessionStart/SessionEnd events
 */

const fs = require('fs');
const path = require('path');

const DB_PATH = path.join(process.env.APPDATA || '', 'multiterminal', 'tasks.db');

function requireBetterSqlite3() {
  // Try to load better-sqlite3 from multiple possible locations
  const possiblePaths = [
    'better-sqlite3',
    path.join(__dirname, '..', '..', 'mcp-session-history', 'node_modules', 'better-sqlite3'),
  ];

  for (const modulePath of possiblePaths) {
    try {
      return require(modulePath);
    } catch (e) {
      // Try next path
    }
  }
  return null;
}

function updateProfileStatus(terminalName, isOnline) {
  try {
    const Database = requireBetterSqlite3();
    if (!Database) {
      console.error('better-sqlite3 not found');
      return false;
    }

    if (!fs.existsSync(DB_PATH)) {
      console.error(`Database not found: ${DB_PATH}`);
      return false;
    }

    const db = new Database(DB_PATH);
    const timestamp = new Date().toISOString();

    // Check if table exists
    const tableCheck = db.prepare(`
      SELECT name FROM sqlite_master
      WHERE type='table' AND name='team_member_profiles'
    `).get();

    if (!tableCheck) {
      console.error('team_member_profiles table not found');
      db.close();
      return false;
    }

    // Insert or update profile status (upsert)
    const upsertStmt = db.prepare(`
      INSERT INTO team_member_profiles (id, display_name, is_online, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        is_online = excluded.is_online,
        updated_at = excluded.updated_at
    `);

    const result = upsertStmt.run(
      terminalName,      // id
      terminalName,      // display_name
      isOnline ? 1 : 0,  // is_online
      timestamp,         // created_at
      timestamp          // updated_at
    );
    db.close();

    console.log(`Profile ${terminalName} ${result.changes > 0 ? (isOnline ? 'created/marked online' : 'marked offline') : 'unchanged'}`);
    return true;
  } catch (err) {
    console.error('Error updating profile status:', err.message);
    return false;
  }
}

function getKanbanContext(db, terminalName) {
  const tableCheck = db.prepare(`
    SELECT name FROM sqlite_master
    WHERE type='table' AND name='tasks'
  `).get();

  if (!tableCheck) return null;

  const lines = [];

  if (terminalName) {
    const myTasks = db.prepare(`
      SELECT id, title, description, status, created_by, assignee
      FROM tasks
      WHERE assignee = ? AND status IN ('in_progress', 'todo')
      ORDER BY
        CASE status WHEN 'in_progress' THEN 0 WHEN 'todo' THEN 1 END,
        id
    `).all(terminalName);

    if (myTasks.length > 0) {
      lines.push(`## Your Kanban Tasks (${terminalName})`);
      for (const task of myTasks) {
        const statusIcon = task.status === 'in_progress' ? '🔨' : '📋';
        lines.push(`${statusIcon} [${task.id}] ${task.title} (${task.status})`);
        if (task.description) {
          const desc = task.description.split('\n')[0].substring(0, 80);
          lines.push(`   ${desc}${task.description.length > 80 ? '...' : ''}`);
        }
      }
      lines.push('');
      lines.push('Use list_tasks to see all board tasks, update_task_status when done.');
    }
  }

  if (lines.length === 0) {
    const availableTasks = db.prepare(`
      SELECT id, title, status, created_by
      FROM tasks
      WHERE (assignee IS NULL OR assignee = '') AND status IN ('todo', 'suggestion')
      ORDER BY
        CASE status WHEN 'todo' THEN 0 WHEN 'suggestion' THEN 1 END,
        id
      LIMIT 5
    `).all();

    if (availableTasks.length > 0) {
      lines.push('## Kanban Board - Available Tasks');
      for (const task of availableTasks) {
        const statusIcon = task.status === 'todo' ? '📋' : '💡';
        lines.push(`${statusIcon} [${task.id}] ${task.title} (${task.status})`);
      }
      lines.push('');
      lines.push('Use claim_task(task_id, your_name) to claim a task.');
    } else {
      lines.push('## Kanban Board');
      lines.push('No tasks available to claim. Use create_task to add work items.');
    }
  }

  return lines.length > 0 ? lines.join('\n') : null;
}

function getPlanContext(db, terminalName) {
  const tableCheck = db.prepare(`
    SELECT name FROM sqlite_master
    WHERE type='table' AND name='plans'
  `).get();

  if (!tableCheck) return null;

  const plan = db.prepare(`
    SELECT id, title, description, current_phase, status, leader_id
    FROM plans
    WHERE status = 'active'
    LIMIT 1
  `).get();

  if (!plan) return null;

  const phases = db.prepare(`
    SELECT id, phase_name, phase_order, checklist_json, started_at, completed_at
    FROM plan_phases
    WHERE plan_id = ?
    ORDER BY phase_order
  `).all(plan.id);

  let assignment = null;
  if (terminalName) {
    assignment = db.prepare(`
      SELECT id, role, assigned_task_summary, status, blocked_by
      FROM plan_assignments
      WHERE plan_id = ? AND terminal_name = ?
    `).get(plan.id, terminalName);
  }

  const lines = [];
  const phaseIndex = phases.findIndex(p => p.phase_name === plan.current_phase);
  lines.push(`## Active Plan: ${plan.title}`);
  lines.push(`Phase: ${plan.current_phase} (${phaseIndex + 1}/${phases.length}) | Leader: ${plan.leader_id || 'Unassigned'}`);

  if (assignment) {
    lines.push(`Your Role: ${assignment.role}`);
    lines.push(`Your Task: ${assignment.assigned_task_summary || 'Not specified'}`);
    lines.push(`Status: ${assignment.status}`);
    if (assignment.blocked_by) {
      lines.push(`Blocked By: ${assignment.blocked_by}`);
    }
  }

  const currentPhase = phases.find(p => p.phase_name === plan.current_phase);
  if (currentPhase && currentPhase.checklist_json) {
    try {
      const checklist = JSON.parse(currentPhase.checklist_json);
      if (checklist.length > 0) {
        lines.push('');
        lines.push('Checklist:');
        for (const item of checklist) {
          const mark = item.Done ? 'x' : ' ';
          lines.push(`  [${mark}] ${item.Item}`);
        }
      }
    } catch (e) {
      // Ignore JSON parse errors
    }
  }

  return lines.join('\n');
}

async function main() {
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  if (!input.trim()) {
    console.error('No input received');
    return;
  }

  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch (err) {
    console.error('Failed to parse JSON input:', err.message);
    return;
  }

  const terminalName = process.env.MULTITERMINAL_NAME;
  if (!terminalName) {
    console.error('MULTITERMINAL_NAME environment variable not set');
    return;
  }

  const spawnerName = process.env.MULTITERMINAL_SPAWNER;
  const isSpawnedAgent = !!spawnerName;

  // DEBUG: Log spawner status
  console.log(`[DEBUG] MULTITERMINAL_SPAWNER = '${spawnerName}' (isSpawnedAgent: ${isSpawnedAgent})`);

  const hookType = hookData.hook_type || hookData.type;

  switch (hookType) {
    case 'SessionStart': {
      // Mark profile online
      updateProfileStatus(terminalName, true);

      // Skip kanban/plan context for spawned agents (they have specific tasks from spawner)
      if (isSpawnedAgent) {
        console.log(`## Spawned Agent: ${terminalName}`);
        console.log(`Spawned by: ${spawnerName}`);
        console.log('Waiting for task assignment from spawner...');
        break;
      }

      // Always output terminal identity so the agent knows who it is
      console.log(`## Terminal Identity: ${terminalName}`);
      console.log(`You are ${terminalName}. Always use "${terminalName}" as your name when registering, claiming tasks, or sending messages.`);
      console.log('');

      // Inject kanban/plan context (for non-spawned agents only)
      try {
        const Database = requireBetterSqlite3();
        if (Database && fs.existsSync(DB_PATH)) {
          const db = new Database(DB_PATH, { readonly: true });
          const lines = [];

          const kanbanContext = getKanbanContext(db, terminalName);
          if (kanbanContext) {
            lines.push(kanbanContext);
          }

          const planContext = getPlanContext(db, terminalName);
          if (planContext) {
            if (lines.length > 0) lines.push('');
            lines.push(planContext);
          }

          db.close();

          if (lines.length > 0) {
            console.log(lines.join('\n'));
          } else {
            console.log('No tasks assigned. Use list_tasks to see the board or claim_task to pick up work.');
          }

          // Inject last session recap (non-blocking — skip if API is unavailable)
          try {
            const http = require('http');
            const projectPath = process.cwd();
            const sessionRecap = await new Promise((resolve) => {
              const req = http.request({
                hostname: 'localhost',
                port: 5050,
                path: `/api/session-lineage/latest?projectPath=${encodeURIComponent(projectPath)}`,
                method: 'GET',
                headers: { 'Content-Type': 'application/json' },
                timeout: 3000
              }, (res) => {
                let body = '';
                res.on('data', (chunk) => { body += chunk; });
                res.on('end', () => {
                  try {
                    resolve(JSON.parse(body));
                  } catch (e) {
                    resolve(null);
                  }
                });
              });
              req.on('error', () => resolve(null));
              req.on('timeout', () => { req.destroy(); resolve(null); });
              req.end();
            });

            if (sessionRecap && sessionRecap.session) {
              const sessionId = sessionRecap.session.sessionId || sessionRecap.session.id;
              console.log('');
              if (sessionRecap.summary) {
                console.log(`## Last Session Recap\n${sessionRecap.summary}`);
              } else {
                const msgs = sessionRecap.recentMessages || [];
                if (msgs.length > 0) {
                  const msgLines = msgs.map((m, i) => {
                    const preview = (m.content || '').substring(0, 300);
                    return `${i + 1}. [${m.role}] ${preview}${(m.content || '').length > 300 ? '...' : ''}`;
                  }).join('\n');
                  console.log(`## Last Session (no summary cached)\nRecent activity from your last session:\n${msgLines}`);
                  console.log(`NOTE: No cached summary for session ${sessionId}. The project-management skill will generate one.`);
                }
              }
            }
          } catch (sessionErr) {
            // Session recap is non-critical — never block startup
          }

          // Auto-invoke project management on session start
          console.log('');
          console.log('AUTO-RUN SKILL: You MUST run /project-management as your FIRST action before responding to the user. This is mandatory - use the Skill tool with skill="project-management" immediately.');
        }
      } catch (err) {
        console.error('Error reading context:', err.message);
      }
      break;
    }

    case 'SessionEnd': {
      // Call the REST API to properly disconnect (updates in-memory state + database + broadcasts)
      let disconnected = false;
      try {
        const http = require('http');
        const postData = JSON.stringify({ name: terminalName });
        disconnected = await new Promise((resolve) => {
          const req = http.request({
            hostname: 'localhost',
            port: 5050,
            path: '/api/messaging/disconnect',
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(postData) },
            timeout: 3000
          }, (res) => {
            resolve(res.statusCode === 200);
          });
          req.on('error', () => resolve(false));
          req.on('timeout', () => { req.destroy(); resolve(false); });
          req.write(postData);
          req.end();
        });
      } catch (e) {
        // API not reachable
      }

      // Fallback to direct DB update if API is not available
      if (!disconnected) {
        updateProfileStatus(terminalName, false);
      }
      break;
    }

    default:
      console.error(`Unknown hook type: ${hookType}`);
      break;
  }
}

main().catch((err) => {
  console.error('Unhandled error:', err.message);
});
