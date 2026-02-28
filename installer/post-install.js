/**
 * MultiTerminal Post-Install Script
 *
 * Runs after Inno Setup copies files. Handles:
 * 1. Merging hooks into Claude Code's global settings.json
 * 2. Generating project-level .claude/mcp.json
 * 3. Generating project-level .claude/project.json
 *
 * Usage: node post-install.js <installDir> <appDataDir> <userProfileDir>
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const installDir = process.argv[2];
const appDataDir = process.argv[3];
const userProfileDir = process.argv[4];

if (!installDir || !appDataDir || !userProfileDir) {
    console.error('Usage: node post-install.js <installDir> <appDataDir> <userProfileDir>');
    process.exit(1);
}

const claudeGlobalDir = path.join(userProfileDir, '.claude');
const claudeProjectDir = path.join(installDir, '.claude');
const hooksDir = path.join(claudeGlobalDir, 'hooks');

// Ensure directories exist
function ensureDir(dir) {
    if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
    }
}

// Generate a GUID
function generateGuid() {
    return crypto.randomUUID();
}

// ============================================================
// 1. Merge hooks into global settings.json
// ============================================================
function mergeHooksIntoSettings() {
    const settingsPath = path.join(claudeGlobalDir, 'settings.json');

    // Read existing settings or start fresh
    let settings = {};
    if (fs.existsSync(settingsPath)) {
        try {
            // Back up existing settings
            const backupPath = settingsPath + '.pre-multiterminal.bak';
            fs.copyFileSync(settingsPath, backupPath);
            settings = JSON.parse(fs.readFileSync(settingsPath, 'utf-8'));
        } catch (e) {
            console.warn('Warning: Could not parse existing settings.json, creating new one');
            settings = {};
        }
    }

    // Build hook paths using the actual user profile directory
    const poolContextPath = path.join(hooksDir, 'pool-context.js');
    const profileStatusPath = path.join(hooksDir, 'profile-status-hook.js');
    const activityHookPath = path.join(hooksDir, 'activity-hook.js');

    // Define MultiTerminal hooks
    const multiTerminalHooks = {
        "SessionStart": [
            {
                "matcher": "startup|resume",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${poolContextPath}"`
                    },
                    {
                        "type": "command",
                        "command": `node "${profileStatusPath}"`,
                        "async": true
                    }
                ]
            }
        ],
        "SessionEnd": [
            {
                "matcher": "",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${profileStatusPath}"`,
                        "async": false
                    }
                ]
            }
        ],
        "PreToolUse": [
            {
                "matcher": "Edit|Write|Bash|Task",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${activityHookPath}"`,
                        "async": true
                    }
                ]
            }
        ],
        "PostToolUse": [
            {
                "matcher": "Edit|Write|Bash|Task",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${activityHookPath}"`,
                        "async": true
                    }
                ]
            }
        ],
        "PostToolUseFailure": [
            {
                "matcher": "",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${activityHookPath}"`,
                        "async": true
                    }
                ]
            }
        ],
        "SubagentStart": [
            {
                "matcher": "",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${activityHookPath}"`,
                        "async": true
                    }
                ]
            }
        ],
        "SubagentStop": [
            {
                "matcher": "",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${activityHookPath}"`,
                        "async": true
                    }
                ]
            }
        ]
    };

    // Merge hooks - replace any existing hook events with our versions
    if (!settings.hooks) {
        settings.hooks = {};
    }

    for (const [event, hookArray] of Object.entries(multiTerminalHooks)) {
        settings.hooks[event] = hookArray;
    }

    // Write updated settings
    ensureDir(claudeGlobalDir);
    fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
    console.log(`Updated Claude Code settings: ${settingsPath}`);
}

// ============================================================
// 2. Register MCP server in global ~/.claude.json
// ============================================================
function registerMcpServer() {
    const claudeJsonPath = path.join(userProfileDir, '.claude.json');
    const mcpServerPath = path.join(appDataDir, 'multiterminal', 'mcp', 'index.js');

    // Read existing .claude.json or start fresh
    let claudeConfig = {};
    if (fs.existsSync(claudeJsonPath)) {
        try {
            claudeConfig = JSON.parse(fs.readFileSync(claudeJsonPath, 'utf-8'));
        } catch (e) {
            console.warn('Warning: Could not parse existing .claude.json, skipping MCP registration');
            return;
        }
    }

    // Ensure mcpServers key exists
    if (!claudeConfig.mcpServers) {
        claudeConfig.mcpServers = {};
    }

    // Add or update multiterminal MCP server entry
    claudeConfig.mcpServers.multiterminal = {
        "type": "stdio",
        "command": "node",
        "args": [mcpServerPath],
        "env": {}
    };

    fs.writeFileSync(claudeJsonPath, JSON.stringify(claudeConfig, null, 2), 'utf-8');
    console.log(`Registered MCP server in: ${claudeJsonPath}`);
}

// ============================================================
// 3. Generate project-level .claude/project.json
// ============================================================
function generateProjectConfig() {
    const now = new Date().toISOString();

    const projectConfig = {
        "id": generateGuid(),
        "name": "MultiTerminal",
        "description": "Multi-agent coordination system for Claude Code",
        "changeLog": "",
        "createdAt": now,
        "lastOpenedAt": now,
        "isPinned": false,
        "prompts": [],
        "hooks": {
            "SessionStart": [
                {
                    "matcher": "startup|resume",
                    "hooks": [
                        {
                            "type": "command",
                            "command": "powershell -NoProfile -Command \"$n=[Environment]::GetEnvironmentVariable('MULTITERMINAL_NAME'); $d=[Environment]::GetEnvironmentVariable('MULTITERMINAL_DOC_ID'); if($n -and $d){ Write-Output ''; Write-Output '=== MULTITERMINAL AUTO-REGISTRATION ==='; Write-Output \\\"Agent Name: $n\\\"; Write-Output \\\"Doc ID: $d\\\"; Write-Output 'STATUS: Ready for registration'; Write-Output '======================================'; Write-Output '' } else { Write-Output 'MultiTerminal: No pre-registration (env vars not set)' }\""
                        },
                        {
                            "type": "command",
                            "command": "powershell -NoProfile -Command \"if([Environment]::GetEnvironmentVariable('MULTITERMINAL_SPAWNER')){ Write-Output ''; Write-Output 'TEAM COMMUNICATION REMINDER'; Write-Output ''; Write-Output 'When working with teammates:'; Write-Output '  ALWAYS use send_message() to communicate'; Write-Output '  Do NOT just put responses in your context'; Write-Output '  Your teammates CANNOT see your internal thoughts'; Write-Output '  Every response to a message MUST be sent via MCP tools'; Write-Output '' }\""
                        }
                    ]
                }
            ]
        }
    };

    ensureDir(claudeProjectDir);
    const projectJsonPath = path.join(claudeProjectDir, 'project.json');
    fs.writeFileSync(projectJsonPath, JSON.stringify(projectConfig, null, 2), 'utf-8');
    console.log(`Generated project config: ${projectJsonPath}`);
}

// ============================================================
// Run all steps
// ============================================================
try {
    console.log('MultiTerminal post-install configuration...');
    console.log(`  Install dir:    ${installDir}`);
    console.log(`  AppData dir:    ${appDataDir}`);
    console.log(`  User profile:   ${userProfileDir}`);
    console.log('');

    mergeHooksIntoSettings();
    registerMcpServer();
    generateProjectConfig();

    console.log('');
    console.log('Claude Code integration configured successfully.');
    process.exit(0);
} catch (err) {
    console.error('Post-install error:', err.message);
    // Don't fail the installer over config issues
    process.exit(0);
}
