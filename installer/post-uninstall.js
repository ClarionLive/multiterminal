/**
 * MultiTerminal Post-Uninstall Script
 *
 * Runs during uninstall to clean up Claude Code integration:
 * 1. Removes MultiTerminal hooks from global settings.json
 * 2. Removes hook script files
 * 3. Removes skill folders
 * 4. Removes MCP server files from AppData
 * 5. Removes MCP server entries from ~/.claude.json (user-level config)
 * 6. Removes optional MCP servers from gateway database
 * 7. Restores runtimeconfig.json if framework-dependent backup exists
 *
 * Usage: node post-uninstall.js <installDir> <appDataDir> <userProfileDir>
 */

const fs = require('fs');
const path = require('path');

const installDir = process.argv[2];
const appDataDir = process.argv[3];
const userProfileDir = process.argv[4];

if (!installDir || !appDataDir || !userProfileDir) {
    console.error('Usage: node post-uninstall.js <installDir> <appDataDir> <userProfileDir>');
    process.exit(1);
}

const claudeGlobalDir = path.join(userProfileDir, '.claude');
const hooksDir = path.join(claudeGlobalDir, 'hooks');
const skillsDir = path.join(claudeGlobalDir, 'skills');

function safeDelete(filePath) {
    try {
        if (fs.existsSync(filePath)) {
            fs.unlinkSync(filePath);
            console.log(`  Deleted: ${filePath}`);
        }
    } catch (e) {
        console.warn(`  Warning: Could not delete ${filePath}: ${e.message}`);
    }
}

function safeRmdir(dirPath) {
    try {
        if (fs.existsSync(dirPath)) {
            fs.rmSync(dirPath, { recursive: true, force: true });
            console.log(`  Removed: ${dirPath}`);
        }
    } catch (e) {
        console.warn(`  Warning: Could not remove ${dirPath}: ${e.message}`);
    }
}

// ============================================================
// 1. Remove MultiTerminal hooks from settings.json
// ============================================================
function removeHooksFromSettings() {
    const settingsPath = path.join(claudeGlobalDir, 'settings.json');

    if (!fs.existsSync(settingsPath)) {
        console.log('  No settings.json found, skipping hook removal.');
        return;
    }

    try {
        const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf-8'));

        if (settings.hooks) {
            // Hook files installed by MultiTerminal
            const ourHookFiles = [
                'pool-context.js',
                'profile-status-hook.js',
                'activity-hook.js',
                'session-status-hook.js',
                'session-import-hook.js'
            ];

            for (const [event, hookGroups] of Object.entries(settings.hooks)) {
                if (Array.isArray(hookGroups)) {
                    settings.hooks[event] = hookGroups.filter(group => {
                        if (!group.hooks || !Array.isArray(group.hooks)) return true;
                        return group.hooks.some(hook => {
                            if (!hook.command) return true;
                            return !ourHookFiles.some(f => hook.command.includes(f));
                        });
                    });

                    if (settings.hooks[event].length === 0) {
                        delete settings.hooks[event];
                    }
                }
            }

            if (Object.keys(settings.hooks).length === 0) {
                delete settings.hooks;
            }

            fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
            console.log('  Updated settings.json: removed MultiTerminal hooks');
        }
    } catch (e) {
        console.warn(`  Warning: Could not update settings.json: ${e.message}`);
    }
}

// ============================================================
// 2. Remove global hook script files
// ============================================================
function removeHookFiles() {
    safeDelete(path.join(hooksDir, 'session-status-hook.js'));
    safeDelete(path.join(hooksDir, 'activity-hook.js'));
    safeDelete(path.join(hooksDir, 'pool-context.js'));
    safeDelete(path.join(hooksDir, 'profile-status-hook.js'));
    safeDelete(path.join(hooksDir, 'session-import-hook.js'));
}

// ============================================================
// 3. Remove skill folders
// ============================================================
function removeSkills() {
    safeRmdir(path.join(skillsDir, 'kanban-task'));
    safeRmdir(path.join(skillsDir, 'multiterminal-addproject'));
    safeRmdir(path.join(skillsDir, 'profile'));
    safeRmdir(path.join(skillsDir, 'new-project'));
    safeRmdir(path.join(skillsDir, 'project-management'));
}

// ============================================================
// 4. Remove MCP server files from AppData
// ============================================================
function removeMcpServerFiles() {
    safeRmdir(path.join(appDataDir, 'multiterminal', 'mcp'));
}

// ============================================================
// 5. Remove MCP server entries from ~/.claude.json
// ============================================================
function removeMcpConfig() {
    // Clean up legacy .mcp.json if it exists (from older installs)
    safeDelete(path.join(appDataDir, 'multiterminal', '.mcp.json'));

    // Remove MCP entries from ~/.claude.json (the actual config location)
    const claudeJsonPath = path.join(userProfileDir, '.claude.json');
    if (!fs.existsSync(claudeJsonPath)) return;

    try {
        const claudeConfig = JSON.parse(fs.readFileSync(claudeJsonPath, 'utf-8'));
        let changed = false;

        if (claudeConfig.mcpServers) {
            if (claudeConfig.mcpServers.multiterminal) {
                delete claudeConfig.mcpServers.multiterminal;
                changed = true;
            }
            if (claudeConfig.mcpServers['mcp-gateway']) {
                delete claudeConfig.mcpServers['mcp-gateway'];
                changed = true;
            }
        }

        if (changed) {
            fs.writeFileSync(claudeJsonPath, JSON.stringify(claudeConfig, null, 2), 'utf-8');
            console.log('  Removed MCP server entries from ~/.claude.json');
        }
    } catch (e) {
        console.warn(`  Warning: Could not update .claude.json: ${e.message}`);
    }
}

// ============================================================
// 6. Remove optional MCP servers from gateway database
// ============================================================
function removeGatewayMcps() {
    const gatewayDbPath = path.join(appDataDir, 'multiterminal', 'gateway', 'gateway.db');
    if (!fs.existsSync(gatewayDbPath)) return;

    const mcpNames = ['mssql', 'sqlite', 'windows-build-runner', 'windowssnapit', 'everything-search'];

    try {
        const { execSync } = require('child_process');
        const names = mcpNames.map(n => `'${n}'`).join(',');
        execSync(`sqlite3 "${gatewayDbPath}" "DELETE FROM servers WHERE name IN (${names});"`, { stdio: 'inherit' });
        console.log('  Removed optional MCP servers from gateway database');
    } catch (e) {
        console.warn(`  Warning: Could not clean up gateway database: ${e.message}`);
    }

    // Remove the defaults file
    safeDelete(path.join(appDataDir, 'multiterminal', 'gateway', 'gateway-defaults.json'));
}

// ============================================================
// 7. Clean up runtimeconfig backup
// ============================================================
function cleanupRuntimeConfigBackup() {
    safeDelete(path.join(installDir, 'MultiTerminal.runtimeconfig.json.self-contained.bak'));
}

// ============================================================
// Run all cleanup steps
// ============================================================
try {
    console.log('MultiTerminal uninstall cleanup...');
    console.log('');

    console.log('Removing Claude Code hooks from settings...');
    removeHooksFromSettings();

    console.log('Removing hook script files...');
    removeHookFiles();

    console.log('Removing skills...');
    removeSkills();

    console.log('Removing MCP server files...');
    removeMcpServerFiles();

    console.log('Removing MCP configuration...');
    removeMcpConfig();

    console.log('Removing optional MCP servers from gateway...');
    removeGatewayMcps();

    console.log('Cleaning up runtime config backup...');
    cleanupRuntimeConfigBackup();

    console.log('');
    console.log('Claude Code integration cleaned up.');
    process.exit(0);
} catch (err) {
    console.error('Uninstall cleanup error:', err.message);
    // Don't fail the uninstaller
    process.exit(0);
}
