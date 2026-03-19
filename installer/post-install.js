/**
 * MultiTerminal Post-Install Script
 *
 * Runs after Inno Setup copies files. Handles:
 * 1. Merging global hooks into Claude Code's ~/.claude/settings.json
 *    (includes commentary-hook.js if ClaudeRemote is installed)
 * 2. Registering MCP servers in ~/.claude.json (user-level, the only location Claude Code reads)
 * 3. Registering optional MCP servers in the MCP Gateway database
 * 4. Generating project-level .claude/project.json
 * 5. Generating project-level .claude/settings.local.json (project hooks)
 *    (includes pipeline-trigger, active-context, and notification hooks)
 * 6. Generating companion-processes.json (if ClaudeRemote installed)
 * 7. Patching runtimeconfig.json for framework-dependent mode (if .NET 8 detected)
 *
 * Usage: node post-install.js <installDir> <appDataDir> <userProfileDir> [--framework-dependent|--self-contained] [--mcps=name1,name2,...]
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const installDir = process.argv[2];
const appDataDir = process.argv[3];
const userProfileDir = process.argv[4];
const dotnetFlag = process.argv[5] || '--self-contained';
const mcpsFlag = process.argv[6] || '--mcps=none';

if (!installDir || !appDataDir || !userProfileDir) {
    console.error('Usage: node post-install.js <installDir> <appDataDir> <userProfileDir> [--framework-dependent|--self-contained] [--mcps=name1,name2,...]');
    process.exit(1);
}

const isFrameworkDependent = (dotnetFlag === '--framework-dependent');
const selectedMcps = mcpsFlag.replace('--mcps=', '').split(',').filter(m => m && m !== 'none');
const claudeGlobalDir = path.join(userProfileDir, '.claude');
const claudeProjectDir = path.join(installDir, '.claude');
const hooksDir = path.join(claudeGlobalDir, 'hooks');

function ensureDir(dir) {
    if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
    }
}

function generateGuid() {
    return crypto.randomUUID();
}

// ============================================================
// 1. Merge global hooks into ~/.claude/settings.json
// ============================================================
function mergeHooksIntoSettings() {
    const settingsPath = path.join(claudeGlobalDir, 'settings.json');

    let settings = {};
    if (fs.existsSync(settingsPath)) {
        try {
            const backupPath = settingsPath + '.pre-multiterminal.bak';
            fs.copyFileSync(settingsPath, backupPath);
            settings = JSON.parse(fs.readFileSync(settingsPath, 'utf-8'));
        } catch (e) {
            console.warn('Warning: Could not parse existing settings.json, creating new one');
            settings = {};
        }
    }

    // Build hook paths using actual user profile directory
    const poolContextPath = path.join(hooksDir, 'pool-context.js');
    const profileStatusPath = path.join(hooksDir, 'profile-status-hook.js');
    const activityHookPath = path.join(hooksDir, 'activity-hook.js');
    const sessionImportPath = path.join(hooksDir, 'session-import-hook.js');
    const commentaryHookPath = path.join(hooksDir, 'commentary-hook.js');

    // Only register commentary hook if ClaudeRemote is installed
    const claudeRemoteExe = path.join(installDir, 'claude-remote', 'ClaudeRemote.exe');
    const hasClaudeRemote = fs.existsSync(claudeRemoteExe);

    // Define global MultiTerminal hooks
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
                    },
                    {
                        "type": "command",
                        "command": `node "${sessionImportPath}"`,
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
            },
            ...(hasClaudeRemote ? [{
                "matcher": "Edit|Write|Bash|Task|mcp__multiterminal",
                "hooks": [
                    {
                        "type": "command",
                        "command": `node "${commentaryHookPath}"`,
                        "async": true
                    }
                ]
            }] : [])
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

    if (!settings.hooks) {
        settings.hooks = {};
    }

    // Merge hooks: append MultiTerminal hooks to existing arrays instead of overwriting
    for (const [event, hookArray] of Object.entries(multiTerminalHooks)) {
        if (!settings.hooks[event]) {
            settings.hooks[event] = hookArray;
        } else {
            // Remove any existing MultiTerminal hooks (by checking command paths)
            // to avoid duplicates on re-install, then append ours
            const existingEntries = settings.hooks[event].filter(entry => {
                if (!entry.hooks) return true;
                // Keep entry if none of its hooks reference our hook files
                const isMultiTerminal = entry.hooks.some(h =>
                    h.command && (
                        h.command.includes('pool-context.js') ||
                        h.command.includes('profile-status-hook.js') ||
                        h.command.includes('activity-hook.js') ||
                        h.command.includes('session-import-hook.js') ||
                        h.command.includes('commentary-hook.js')
                    )
                );
                return !isMultiTerminal;
            });
            settings.hooks[event] = [...existingEntries, ...hookArray];
        }
    }

    // Enable experimental agent teams (required for Team Agents / AgentPanel)
    if (!settings.env) {
        settings.env = {};
    }
    settings.env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = "1";

    ensureDir(claudeGlobalDir);
    fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
    console.log(`Updated Claude Code settings: ${settingsPath}`);
}

// ============================================================
// 2. Register MCP servers in ~/.claude.json (user-level config)
//    Claude Code only reads user-level MCP servers from this file.
//    The old %APPDATA%\multiterminal\.mcp.json location is ignored.
// ============================================================
function registerMcpServers() {
    const claudeJsonPath = path.join(userProfileDir, '.claude.json');
    const mcpServerPath = path.join(appDataDir, 'multiterminal', 'mcp', 'index.js');
    const mcpGatewayPath = path.join(installDir, 'mcp-gateway', 'McpGateway.exe');

    // Use forward slashes for cross-platform compatibility
    const normalize = (p) => p.replace(/\\/g, '/');

    let claudeConfig = {};
    if (fs.existsSync(claudeJsonPath)) {
        try {
            // Back up before modifying
            const backupPath = claudeJsonPath + '.pre-multiterminal.bak';
            fs.copyFileSync(claudeJsonPath, backupPath);
            claudeConfig = JSON.parse(fs.readFileSync(claudeJsonPath, 'utf-8'));
        } catch (e) {
            console.error('Error: Could not parse ~/.claude.json. Aborting MCP registration.');
            console.error('  This file is critical — not safe to overwrite.');
            return;
        }
    } else {
        console.error('Error: ~/.claude.json not found. Claude Code must be installed first.');
        return;
    }

    if (!claudeConfig.mcpServers) {
        claudeConfig.mcpServers = {};
    }

    claudeConfig.mcpServers['mcp-gateway'] = {
        "type": "stdio",
        "command": normalize(mcpGatewayPath),
        "args": [],
        "env": {}
    };

    claudeConfig.mcpServers['multiterminal'] = {
        "type": "stdio",
        "command": "node",
        "args": [normalize(mcpServerPath)],
        "env": {}
    };

    fs.writeFileSync(claudeJsonPath, JSON.stringify(claudeConfig, null, 2), 'utf-8');
    console.log(`Registered MCP servers in: ${claudeJsonPath}`);

    // Clean up old .mcp.json if it exists (no longer used)
    const legacyMcpJson = path.join(appDataDir, 'multiterminal', '.mcp.json');
    if (fs.existsSync(legacyMcpJson)) {
        try {
            fs.unlinkSync(legacyMcpJson);
            console.log(`  Removed legacy config: ${legacyMcpJson}`);
        } catch (e) {
            // Not critical
        }
    }
}

// ============================================================
// 3. Register optional MCP servers in the gateway database
// ============================================================
function registerOptionalMcps() {
    if (selectedMcps.length === 0) {
        console.log('No optional MCP servers selected.');
        return;
    }

    const gatewayDbPath = path.join(appDataDir, 'multiterminal', 'gateway', 'gateway.db');
    const mcpsDir = path.join(installDir, 'mcps');
    const normalize = (p) => p.replace(/\\/g, '/');

    // MCP server definitions — entry points relative to install dir
    const mcpDefs = {
        'mssql': {
            display_name: 'MSSQL',
            description: 'Microsoft SQL Server query and schema tools',
            command: 'node',
            args: [normalize(path.join(mcpsDir, 'mssql', 'dist', 'index.js'))],
            env: {
                MSSQL_SERVER: '${MSSQL_SERVER}',
                MSSQL_PORT: '${MSSQL_PORT:-1433}',
                MSSQL_DATABASE: '${MSSQL_DATABASE}',
                MSSQL_USER: '${MSSQL_USER}',
                MSSQL_PASSWORD: '${MSSQL_PASSWORD}',
                MSSQL_ENCRYPT: '${MSSQL_ENCRYPT:-false}',
                AI_PROVIDER: 'claude'
            }
        },
        'sqlite': {
            display_name: 'SQLite',
            description: 'Local SQLite database query and management tools',
            command: 'node',
            args: [normalize(path.join(mcpsDir, 'sqlite', 'custom-sqlite-mcp-server.js'))],
            env: {}
        },
        'windows-build-runner': {
            display_name: 'Windows Build Runner',
            description: 'MSBuild and dotnet build execution tools',
            command: 'node',
            args: [normalize(path.join(mcpsDir, 'windows-build-runner', 'build', 'index.js'))],
            env: {}
        },
        'windowssnapit': {
            display_name: 'Windows SnapIt',
            description: 'Window screenshot capture tools',
            command: 'node',
            args: [normalize(path.join(mcpsDir, 'windowssnapit', 'index.js'))],
            env: {}
        },
        'everything-search': {
            display_name: 'Everything Search',
            description: 'Instant file search via voidtools Everything',
            command: 'node',
            args: [normalize(path.join(mcpsDir, 'everything-search', 'build', 'index.js'))],
            env: {}
        }
    };

    // Write gateway-defaults.json — the gateway auto-seeds from this file on next startup.
    // This avoids depending on sqlite3 CLI being available on the target machine.
    ensureDir(path.join(appDataDir, 'multiterminal', 'gateway'));

    const defaultsPath = path.join(appDataDir, 'multiterminal', 'gateway', 'gateway-defaults.json');
    const defaultsConfig = {};
    for (const mcpName of selectedMcps) {
        const def = mcpDefs[mcpName];
        if (!def) {
            console.warn(`  Unknown MCP: ${mcpName}, skipping`);
            continue;
        }
        defaultsConfig[mcpName] = {
            display_name: def.display_name,
            description: def.description,
            transport_type: 'stdio',
            command: def.command,
            args: def.args,
            env: def.env,
            enabled: true
        };
    }
    fs.writeFileSync(defaultsPath, JSON.stringify(defaultsConfig, null, 2), 'utf-8');
    console.log(`  Wrote gateway defaults (${selectedMcps.length} servers): ${defaultsPath}`);
    console.log('  Servers will be registered when the gateway starts.');
}

// ============================================================
// 4. Generate project-level .claude/project.json
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
// 5. Generate project-level .claude/settings.local.json
// ============================================================
function generateProjectSettings() {
    const projectHooksDir = path.join(installDir, '.claude', 'hooks');
    // Use forward slashes for Claude Code compatibility
    const hooksPath = (p) => path.join(projectHooksDir, p).replace(/\\/g, '/');

    const projectSettings = {
        "hooks": {
            "SessionStart": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('project-context-hook.js')}"`,
                            "timeout": 6
                        }
                    ]
                }
            ],
            "PreToolUse": [
                {
                    "matcher": "Task",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('task-to-agent-hook.js')}"`,
                            "timeout": 20
                        }
                    ]
                },
                {
                    "matcher": "Bash",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('safety-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                },
                {
                    "matcher": "Read",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('safety-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                },
                {
                    "matcher": "Write|Edit",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('safety-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                },
                {
                    "matcher": "mcp__sqlite__write_query|mcp__mssql__query",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('safety-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                }
            ],
            "PostToolUse": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('inbox-check-hook.js')}" PostToolUse`,
                            "timeout": 3
                        }
                    ]
                },
                {
                    "matcher": "mcp__windows-build-runner__build_project|mcp__multiterminal__build_project",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('active-context-hook.js')}"`,
                            "timeout": 10,
                            "async": true
                        }
                    ]
                },
                {
                    "matcher": "mcp__multiterminal__update_task_checklist",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('pipeline-trigger-hook.js')}"`,
                            "timeout": 15,
                            "async": true
                        },
                        {
                            "type": "command",
                            "command": `node "${hooksPath('active-context-hook.js')}"`,
                            "timeout": 10,
                            "async": true
                        }
                    ]
                },
                {
                    "matcher": "mcp__multiterminal__update_task_status|mcp__multiterminal__update_task_continuation",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('active-context-hook.js')}"`,
                            "timeout": 10,
                            "async": true
                        }
                    ]
                },
                {
                    "matcher": "mcp__multiterminal__update_task_checklist|mcp__multiterminal__update_task_status|mcp__multiterminal__create_task|mcp__multiterminal__claim_task|mcp__multiterminal__send_message|mcp__multiterminal__broadcast_message",
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('notification-hook.js')}"`,
                            "timeout": 5,
                            "async": true
                        }
                    ]
                }
            ],
            "Stop": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('inbox-check-hook.js')}" Stop`,
                            "timeout": 3
                        }
                    ]
                }
            ],
            "UserPromptSubmit": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('inbox-check-hook.js')}" UserPromptSubmit`,
                            "timeout": 3
                        }
                    ]
                }
            ],
            "SubagentStart": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('subagent-office-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                }
            ],
            "SubagentStop": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('subagent-office-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                },
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('inbox-check-hook.js')}" SubagentStop`,
                            "timeout": 3
                        }
                    ]
                }
            ],
            "TeammateIdle": [
                {
                    "hooks": [
                        {
                            "type": "command",
                            "command": `node "${hooksPath('subagent-office-hook.js')}"`,
                            "timeout": 5
                        }
                    ]
                }
            ]
        }
    };

    const settingsPath = path.join(claudeProjectDir, 'settings.local.json');
    fs.writeFileSync(settingsPath, JSON.stringify(projectSettings, null, 2), 'utf-8');
    console.log(`Generated project settings: ${settingsPath}`);
}

// ============================================================
// 6. Generate companion-processes.json (if ClaudeRemote installed)
// ============================================================
function generateCompanionProcesses() {
    const claudeRemoteExe = path.join(installDir, 'claude-remote', 'ClaudeRemote.exe');
    if (!fs.existsSync(claudeRemoteExe)) {
        console.log('ClaudeRemote not installed, skipping companion-processes.json.');
        return;
    }

    const companionDir = path.join(appDataDir, 'MultiTerminal');
    const companionPath = path.join(companionDir, 'companion-processes.json');

    // Don't overwrite existing file (preserve user customizations)
    if (fs.existsSync(companionPath)) {
        console.log(`Companion processes config already exists, preserving: ${companionPath}`);
        return;
    }

    const claudeRemoteDir = path.join(installDir, 'claude-remote');

    const companionConfig = {
        "Companions": [
            {
                "Name": "ClaudeRemote",
                "Command": claudeRemoteExe,
                "Args": "",
                "HealthUrl": "http://localhost:5100/health",
                "AutoStart": true,
                "StopOnExit": false
            }
        ]
    };

    ensureDir(companionDir);
    fs.writeFileSync(companionPath, JSON.stringify(companionConfig, null, 2), 'utf-8');
    console.log(`Generated companion processes config: ${companionPath}`);
}

// ============================================================
// 7. Patch runtimeconfig.json for framework-dependent mode
// ============================================================
function patchRuntimeConfig() {
    if (!isFrameworkDependent) {
        console.log('Self-contained mode: runtimeconfig.json unchanged.');
        return;
    }

    const rcPath = path.join(installDir, 'MultiTerminal.runtimeconfig.json');
    if (!fs.existsSync(rcPath)) {
        console.warn('Warning: runtimeconfig.json not found, skipping patch.');
        return;
    }

    try {
        const config = JSON.parse(fs.readFileSync(rcPath, 'utf-8'));

        if (config.runtimeOptions && config.runtimeOptions.includedFrameworks) {
            // Back up the original
            fs.copyFileSync(rcPath, rcPath + '.self-contained.bak');

            // Convert includedFrameworks to frameworks (framework-dependent resolution)
            // Use major.minor.0 as minimum version for roll-forward compatibility
            config.runtimeOptions.frameworks = config.runtimeOptions.includedFrameworks.map(fw => ({
                name: fw.name,
                version: fw.version.split('.').slice(0, 2).join('.') + '.0'
            }));
            delete config.runtimeOptions.includedFrameworks;

            fs.writeFileSync(rcPath, JSON.stringify(config, null, 2), 'utf-8');
            console.log('Patched runtimeconfig.json for framework-dependent mode.');
        } else {
            console.log('runtimeconfig.json already in framework-dependent mode.');
        }
    } catch (e) {
        console.warn(`Warning: Could not patch runtimeconfig.json: ${e.message}`);
    }
}

// ============================================================
// Run all steps
// ============================================================
try {
    console.log('MultiTerminal post-install configuration...');
    console.log(`  Install dir:    ${installDir}`);
    console.log(`  AppData dir:    ${appDataDir}`);
    console.log(`  User profile:   ${userProfileDir}`);
    console.log(`  .NET mode:      ${isFrameworkDependent ? 'framework-dependent' : 'self-contained'}`);
    console.log('');

    mergeHooksIntoSettings();
    registerMcpServers();
    registerOptionalMcps();
    generateProjectConfig();
    generateProjectSettings();
    generateCompanionProcesses();
    patchRuntimeConfig();

    console.log('');
    console.log('Claude Code integration configured successfully.');
    process.exit(0);
} catch (err) {
    console.error('Post-install error:', err.message);
    // Don't fail the installer over config issues
    process.exit(0);
}
