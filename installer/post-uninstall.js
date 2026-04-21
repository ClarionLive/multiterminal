/**
 * MultiTerminal Post-Uninstall Script
 *
 * Runs during uninstall to clean up Claude Code integration:
 * 1. Removes CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS env var from settings.json
 * 2. Removes MultiTerminal MCP server files from AppData
 * 3. Removes MCP server entries from ~/.claude.json
 * 4. Removes optional MCP servers from gateway database
 * 5. Removes the plugin marketplace directory
 * 6. Restores runtimeconfig.json if framework-dependent backup exists
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
// 1. Remove CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS env from settings.json
// ============================================================
function removeExperimentalAgentTeamsEnv() {
    const settingsPath = path.join(claudeGlobalDir, 'settings.json');
    if (!fs.existsSync(settingsPath)) return;

    try {
        const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf-8'));
        if (settings.env && settings.env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS !== undefined) {
            delete settings.env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS;
            if (Object.keys(settings.env).length === 0) {
                delete settings.env;
            }
            fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2), 'utf-8');
            console.log('  Removed CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS from settings.json');
        }
    } catch (e) {
        console.warn(`  Warning: Could not update settings.json: ${e.message}`);
    }
}

// ============================================================
// 2. Remove MCP server files from AppData
// ============================================================
function removeMcpServerFiles() {
    safeRmdir(path.join(appDataDir, 'multiterminal', 'mcp'));
}

// ============================================================
// 3. Remove MCP server entries from ~/.claude.json
// ============================================================
function removeMcpConfig() {
    // Clean up legacy .mcp.json if it exists (from older installs)
    safeDelete(path.join(appDataDir, 'multiterminal', '.mcp.json'));

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
// 4. Remove optional MCP servers from gateway database
// ============================================================
function removeGatewayMcps() {
    const gatewayDbPath = path.join(appDataDir, 'multiterminal', 'gateway', 'gateway.db');
    if (fs.existsSync(gatewayDbPath)) {
        const mcpNames = ['mssql', 'sqlite', 'windows-build-runner', 'windowssnapit', 'everything-search'];
        try {
            const { execSync } = require('child_process');
            const names = mcpNames.map(n => `'${n}'`).join(',');
            execSync(`sqlite3 "${gatewayDbPath}" "DELETE FROM servers WHERE name IN (${names});"`, { stdio: 'inherit' });
            console.log('  Removed optional MCP servers from gateway database');
        } catch (e) {
            console.warn(`  Warning: Could not clean up gateway database: ${e.message}`);
        }
    }

    safeDelete(path.join(appDataDir, 'multiterminal', 'gateway', 'gateway-defaults.json'));
}

// ============================================================
// 5. Remove the plugin marketplace directory
// ============================================================
function removePluginMarketplace() {
    const marketplaceDir = path.join(claudeGlobalDir, 'plugins', 'marketplaces', 'multiterminal-marketplace');
    safeRmdir(marketplaceDir);
}

// ============================================================
// 6. Clean up runtimeconfig backup
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

    console.log('Removing agent-teams env var from settings...');
    removeExperimentalAgentTeamsEnv();

    console.log('Removing MCP server files...');
    removeMcpServerFiles();

    console.log('Removing MCP configuration...');
    removeMcpConfig();

    console.log('Removing optional MCP servers from gateway...');
    removeGatewayMcps();

    console.log('Removing plugin marketplace...');
    removePluginMarketplace();

    console.log('Cleaning up runtime config backup...');
    cleanupRuntimeConfigBackup();

    console.log('');
    console.log('Claude Code integration cleaned up.');
    process.exit(0);
} catch (err) {
    console.error('Uninstall cleanup error:', err.message);
    process.exit(0);
}
