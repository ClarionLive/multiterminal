/**
 * MultiTerminal Post-Uninstall Script
 *
 * Runs during uninstall to clean up Claude Code integration:
 * 1. Removes CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS env var from settings.json
 * 2. Removes MultiTerminal MCP server files from AppData
 * 3. Removes MCP server entries from ~/.claude.json — present only when the
 *    opt-in claude\globalreg component was selected at install time (GH#2);
 *    a no-op otherwise, so this script is safe in both modes
 * 4. Removes optional MCP servers from gateway database
 * 5. Removes the plugin marketplace directory (default profile — unchanged), and for a shared
 *    plugin location (ticket c8ee0eec, GH#23): de-registers the marketplace from every profile in
 *    HKCU\Software\MultiTerminal\PluginRegisteredProfiles, deletes the shared directory only when
 *    sharedDirRemovalDecision allows it (created flag AND a matching .mt-installer-owned marker
 *    AND not a reparse point), and removes the four registry values
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
// 5b. Shared plugin location (ticket c8ee0eec, GH#23).
//     Order matters: de-register first (so no profile is left
//     declaring a marketplace whose directory is gone), then delete
//     the directory, then the registry values that described it.
//     Every step is best-effort and logged; nothing fails uninstall.
// ============================================================
function removeSharedPluginLocation() {
    let reg;
    try {
        reg = require('./plugin-registration');
    } catch (e) {
        console.warn(`  Warning: plugin-registration.js not found (${e.message}); shared plugin cleanup skipped.`);
        return;
    }

    // One `reg export` read for every value (see readRegValues: non-ASCII safe).
    const values = reg.readRegValues();
    const sharedRoot = typeof values[reg.REG_PATH] === 'string' ? values[reg.REG_PATH] : '';
    const created = values[reg.REG_CREATED];
    const ownerToken = values[reg.REG_TOKEN];
    // Same filter as post-install's planner: the default profile is never touched here.
    const plan = reg.planPostInstall({
        mode: 'default', requested: [], userProfileDir,
        previous: reg.parseProfileList(values[reg.REG_PROFILES] || '', {}),
    });

    if (!sharedRoot && plan.remove.length === 0) {
        console.log('  No shared plugin location or registered profiles recorded.');
    }
    for (const dir of plan.skippedDefault) {
        console.log(`  Skipped ${dir}: the default Claude Code profile is not de-registered here.`);
    }

    for (const dir of plan.remove) {
        // A dir that no longer exists is never run: claude would create a new profile there.
        const skip = reg.profileDirPreflight({ action: 'remove', dir, exists: fs.existsSync(dir) });
        if (skip) {
            console.warn(`  Not de-registering ${dir}: ${skip.message}`);
            continue;
        }
        const r = reg.runMarketplace('remove', { configDir: dir, userProfileDir });
        const clean = r.ok || r.failureCode === 'not_configured';
        console.log(`  ${clean ? 'De-registered' : 'Warning: FAILED to de-register'} marketplace from ${dir}${r.message ? `: ${r.message}` : ''}`);
    }

    if (sharedRoot) {
        const root = reg.cleanDir(sharedRoot);
        const decision = reg.sharedDirRemovalDecision({
            sharedRoot: root, createdFlag: created, userProfileDir, ownerToken,
            ...reg.inspectSharedDir(root),
        });
        if (decision.remove) {
            // Known gap: the checks above and this delete are not atomic (check-then-delete race),
            // so the folder could be swapped between inspection and removal. Deferred to 4fb14422.
            safeRmdir(root);
        } else {
            console.log(`  Kept shared plugin directory ${root}: ${decision.reason}`);
        }
    }

    for (const name of [reg.REG_PATH, reg.REG_CREATED, reg.REG_PROFILES, reg.REG_TOKEN]) {
        if (values[name] !== undefined && !reg.deleteRegValue(name)) {
            console.warn(`  Warning: could not delete ${reg.REG_KEY}\\${name}`);
        }
    }
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

    console.log('Removing shared plugin location and profile registrations...');
    removeSharedPluginLocation();

    console.log('Cleaning up runtime config backup...');
    cleanupRuntimeConfigBackup();

    console.log('');
    console.log('Claude Code integration cleaned up.');
    process.exit(0);
} catch (err) {
    console.error('Uninstall cleanup error:', err.message);
    process.exit(0);
}
