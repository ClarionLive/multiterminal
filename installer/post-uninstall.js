/**
 * MultiTerminal Post-Uninstall Script
 *
 * Runs during uninstall to clean up Claude Code integration:
 * 1. Removes MultiTerminal hooks from global settings.json
 * 2. Removes hook script files
 * 3. Removes skill folders
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

// Safe delete - ignores errors if file doesn't exist
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

// Safe remove directory recursively
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
            // Remove all hook events that reference our hook files
            const ourHookFiles = [
                'pool-context.js',
                'profile-status-hook.js',
                'activity-hook.js',
                'session-status-hook.js'
            ];

            for (const [event, hookGroups] of Object.entries(settings.hooks)) {
                if (Array.isArray(hookGroups)) {
                    // Filter out hook groups that only contain our hooks
                    settings.hooks[event] = hookGroups.filter(group => {
                        if (!group.hooks || !Array.isArray(group.hooks)) return true;
                        // Keep the group if any hook does NOT reference our files
                        return group.hooks.some(hook => {
                            if (!hook.command) return true;
                            return !ourHookFiles.some(f => hook.command.includes(f));
                        });
                    });

                    // Remove the event entirely if no hook groups remain
                    if (settings.hooks[event].length === 0) {
                        delete settings.hooks[event];
                    }
                }
            }

            // Remove hooks key entirely if empty
            if (Object.keys(settings.hooks).length === 0) {
                delete settings.hooks;
            }

            fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
            console.log(`  Updated settings.json: removed MultiTerminal hooks`);
        }
    } catch (e) {
        console.warn(`  Warning: Could not update settings.json: ${e.message}`);
    }
}

// ============================================================
// 2. Remove hook script files
// ============================================================
function removeHookFiles() {
    safeDelete(path.join(hooksDir, 'session-status-hook.js'));
    safeDelete(path.join(hooksDir, 'activity-hook.js'));
    safeDelete(path.join(hooksDir, 'pool-context.js'));
    safeDelete(path.join(hooksDir, 'profile-status-hook.js'));
}

// ============================================================
// 3. Remove skill folders
// ============================================================
function removeSkills() {
    safeRmdir(path.join(skillsDir, 'kanban-task'));
    safeRmdir(path.join(skillsDir, 'multiterminal-addproject'));
}

// ============================================================
// 4. Remove MCP server files from AppData
// ============================================================
function removeMcpServerFiles() {
    safeRmdir(path.join(appDataDir, 'multiterminal', 'mcp'));
}

// ============================================================
// 5. Remove MCP server registration from ~/.claude.json
// ============================================================
function unregisterMcpServer() {
    const claudeJsonPath = path.join(userProfileDir, '.claude.json');

    if (!fs.existsSync(claudeJsonPath)) {
        console.log('  No .claude.json found, skipping MCP unregistration.');
        return;
    }

    try {
        const claudeConfig = JSON.parse(fs.readFileSync(claudeJsonPath, 'utf-8'));

        if (claudeConfig.mcpServers && claudeConfig.mcpServers.multiterminal) {
            delete claudeConfig.mcpServers.multiterminal;
            fs.writeFileSync(claudeJsonPath, JSON.stringify(claudeConfig, null, 2), 'utf-8');
            console.log('  Removed multiterminal MCP server from .claude.json');
        }
    } catch (e) {
        console.warn(`  Warning: Could not update .claude.json: ${e.message}`);
    }
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

    console.log('Unregistering MCP server from Claude Code...');
    unregisterMcpServer();

    console.log('');
    console.log('Claude Code integration cleaned up.');
    process.exit(0);
} catch (err) {
    console.error('Uninstall cleanup error:', err.message);
    // Don't fail the uninstaller
    process.exit(0);
}
