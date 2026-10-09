/**
 * Shared plugin-marketplace registration helpers for post-install.js and post-uninstall.js
 * (ticket c8ee0eec, GitHub #23: install the marketplace once, in a shared location, and
 * register it into other Claude Code config profiles).
 *
 * Shipped next to both scripts (the .iss copies it to {tmp} for post-install and to {app} for
 * post-uninstall). Everything that decides WHAT to do is a pure function taking its inputs as
 * arguments, so installer/test/plugin-registration.test.mjs can pin it without a registry, a
 * CLI or a real profile. The impure edges (reg.exe, claude) take an injectable `spawn`.
 *
 * Registry contract (HKCU\Software\MultiTerminal), shared with Services/LaunchCommandBuilder.cs:
 *   PluginMarketplacePath       REG_SZ    shared MARKETPLACE ROOT; absent = default profile
 *   PluginDirCreatedByInstaller REG_DWORD 1 only if the installer created that directory
 *   PluginRegisteredProfiles    REG_SZ    ';'-separated config dirs the marketplace was added to
 * The .iss writes the first two; post-install.js writes the third (it is the only party that
 * knows which registrations succeeded), and post-uninstall.js removes all three.
 *
 * CLI behaviour these helpers rely on, measured against Claude Code 2.1.294 with a scratch
 * CLAUDE_CONFIG_DIR (2026-10-09), not assumed:
 *   - `claude plugin marketplace add <dir> --scope user --json` writes BOTH
 *     <config>/plugins/known_marketplaces.json and <config>/settings.json extraKnownMarketplaces,
 *     with source {source:"directory", path:<dir>}; it does not copy the directory.
 *   - Re-adding the same dir is a no-op success; adding a different dir under the same
 *     marketplace name re-points the entry in place (exit 0). So an upgrade that moves the
 *     shared root needs only `add`, never remove-then-add (which could drop a plugin the user
 *     installed from it).
 *   - `claude plugin marketplace remove multiterminal-marketplace --scope user --json` removes
 *     both entries and does NOT delete a directory source. Removing an absent entry exits 1
 *     with failureCode "not_configured", which for cleanup means "already clean".
 */

'use strict';

const path = require('path');
const os = require('os');

const REG_KEY = 'HKCU\\Software\\MultiTerminal';
const REG_PATH = 'PluginMarketplacePath';
const REG_CREATED = 'PluginDirCreatedByInstaller';
const REG_PROFILES = 'PluginRegisteredProfiles';
const MARKETPLACE_NAME = 'multiterminal-marketplace';

// ------------------------------------------------------------------
// Pure helpers
// ------------------------------------------------------------------

/** Expands %VAR% references from `env` (case-insensitive names); unknown ones are left as-is. */
function expandEnvVars(text, env) {
    const lookup = {};
    for (const k of Object.keys(env || {})) lookup[k.toUpperCase()] = env[k];
    return String(text).replace(/%([^%]+)%/g, (m, name) => {
        const v = lookup[name.toUpperCase()];
        return v === undefined ? m : v;
    });
}

/** Trims whitespace and quotes and drops trailing separators (keeping a bare drive root "C:\"). */
function cleanDir(dir) {
    let d = String(dir).trim().replace(/"/g, '').trim();
    while (d.length > 3 && /[\\/]$/.test(d)) d = d.slice(0, -1);
    return d;
}

/** Comparison key for a Windows directory: normalized, no trailing separator, lower-case. */
function dirKey(dir) {
    return cleanDir(path.win32.normalize(cleanDir(dir))).toLowerCase();
}

function sameDir(a, b) {
    return dirKey(a) === dirKey(b);
}

/** True for C:\x or \\server\share\x; false for relative, drive-relative or empty paths. */
function isAbsoluteWindowsDir(dir) {
    return /^[a-zA-Z]:\\/.test(dir) || /^\\\\[^\\]+\\[^\\]+/.test(dir);
}

/**
 * Parses the installer's ';'-separated profile list into unique cleaned directories, in input
 * order. %VARS% are expanded from `env`. Duplicates compare case-insensitively.
 */
function parseProfileList(text, env) {
    const out = [];
    const seen = new Set();
    for (const raw of String(text || '').split(';')) {
        const d = cleanDir(expandEnvVars(raw, env || {}));
        if (!d) continue;
        const k = dirKey(d);
        if (seen.has(k)) continue;
        seen.add(k);
        out.push(d);
    }
    return out;
}

/** The default Claude Code config dir: %USERPROFILE%\.claude. */
function defaultConfigDir(userProfileDir) {
    return path.win32.join(userProfileDir, '.claude');
}

function isDefaultConfigDir(dir, userProfileDir) {
    return sameDir(dir, defaultConfigDir(userProfileDir));
}

/**
 * Why `dir` cannot be passed to the claude CLI, or null when it can. The command runs through
 * cmd.exe, where a double quote would end the quoting and %...% would expand even inside quotes.
 */
function unsafeDirReason(dir) {
    if (!dir) return 'empty path';
    if (/["%\r\n\0]/.test(dir)) return 'path contains a character that cannot be passed safely (" or % or a control character)';
    if (!isAbsoluteWindowsDir(dir)) return 'not an absolute path';
    return null;
}

/**
 * The environment for ONE claude child process registering into `configDir`.
 *
 * Never mutates `baseEnv` (the installer's own environment is never changed). For the default
 * profile (%USERPROFILE%\.claude) CLAUDE_CONFIG_DIR is REMOVED rather than set: when it is set,
 * Claude Code reads .claude.json from inside that directory instead of %USERPROFILE%, so
 * pointing it at the default dir would move the user's state. It is removed (not just left
 * alone) so a CLAUDE_CONFIG_DIR inherited from the user's environment cannot redirect the
 * default-profile registration into some other profile. Windows env names are case-insensitive
 * but a copied object is not, so every casing is dropped.
 */
function childEnvFor(configDir, userProfileDir, baseEnv) {
    const env = {};
    for (const k of Object.keys(baseEnv || {})) {
        if (k.toUpperCase() !== 'CLAUDE_CONFIG_DIR') env[k] = baseEnv[k];
    }
    if (!isDefaultConfigDir(configDir, userProfileDir)) {
        env.CLAUDE_CONFIG_DIR = configDir;
    }
    return env;
}

/**
 * Post-install plan. `mode` is 'shared' | 'default' | 'skip' (plugin component not selected).
 *   shared:  add every requested dir; remove previously registered dirs no longer requested.
 *   default: add nothing; remove every previous registration (they point at a shared copy the
 *            user just moved away from).
 *   skip:    touch nothing, including the recorded list.
 * Returns { add: [dir], remove: [dir] } or null for skip.
 */
function planPostInstall({ mode, requested, previous }) {
    if (mode === 'skip') return null;
    const req = mode === 'shared' ? (requested || []) : [];
    const prev = previous || [];
    const remove = prev.filter(p => !req.some(r => sameDir(r, p)));
    return { add: req.slice(), remove };
}

/**
 * The PluginRegisteredProfiles list after post-install ran the plan. A dir stays recorded while
 * it may still hold an entry, so uninstall can clean it up:
 *   - an add that succeeded;
 *   - an add that failed for a dir that was ALREADY registered (its old entry is still there);
 *   - a remove that failed for any reason other than "not configured".
 * `results` maps dirKey(dir) -> { ok, failureCode }.
 */
function registeredAfterInstall({ plan, previous, results }) {
    const out = [];
    const prev = previous || [];
    const push = d => { if (!out.some(o => sameDir(o, d))) out.push(d); };
    for (const d of plan.add) {
        const r = results[dirKey(d)];
        if ((r && r.ok) || prev.some(p => sameDir(p, d))) push(d);
    }
    for (const d of plan.remove) {
        const r = results[dirKey(d)];
        if (r && !r.ok && r.failureCode !== 'not_configured') push(d);
    }
    return out;
}

/**
 * Whether uninstall may delete the shared marketplace root. Only when the installer created it
 * (createdFlag === 1), the path is absolute and not a drive root, it is not the default-profile
 * location (that one has its own unchanged cleanup), and it still looks like this marketplace
 * (`hasManifest`: <root>\.claude-plugin\marketplace.json exists). The manifest check stops a
 * corrupted or hand-edited registry value from turning into a recursive delete of some other
 * folder. Returns { remove: boolean, reason: string }.
 */
function sharedDirRemovalDecision({ sharedRoot, createdFlag, userProfileDir, hasManifest }) {
    if (!sharedRoot) return { remove: false, reason: 'no shared location recorded' };
    const root = cleanDir(sharedRoot);
    if (createdFlag !== 1) return { remove: false, reason: 'not created by the installer (PluginDirCreatedByInstaller != 1)' };
    if (!isAbsoluteWindowsDir(root) || root.length <= 3) return { remove: false, reason: 'not a safe absolute path' };
    const defaultRoot = path.win32.join(defaultConfigDir(userProfileDir), 'plugins', 'marketplaces', MARKETPLACE_NAME);
    if (sameDir(root, defaultRoot)) return { remove: false, reason: 'is the default-profile location' };
    if (!hasManifest) return { remove: false, reason: 'no .claude-plugin\\marketplace.json inside; not deleting an unrecognised folder' };
    return { remove: true, reason: 'created by the installer' };
}

/** Command line for `claude plugin marketplace add|remove`. Caller has checked unsafeDirReason. */
function marketplaceCommand(action, sharedRoot) {
    if (action === 'add') return `claude plugin marketplace add "${sharedRoot}" --scope user --json`;
    if (action === 'remove') return `claude plugin marketplace remove ${MARKETPLACE_NAME} --scope user --json`;
    throw new Error(`unknown action ${action}`);
}

/** Parses the CLI's --json result line (the last JSON line on stdout). */
function parseCliResult(stdout, status) {
    const lines = String(stdout || '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
    for (let i = lines.length - 1; i >= 0; i--) {
        if (!lines[i].startsWith('{')) continue;
        try {
            const j = JSON.parse(lines[i]);
            return { ok: status === 0 && j.outcome === 'ok', outcome: j.outcome, failureCode: j.failureCode, message: j.message };
        } catch { /* not the result line */ }
    }
    return { ok: status === 0, outcome: status === 0 ? 'ok' : 'failed', failureCode: undefined, message: '' };
}

/** Parses one value out of `reg query <key> /v <name>` output; null when absent. */
function parseRegQuery(stdout, name) {
    const re = new RegExp('^\\s+' + name + '\\s+(REG_[A-Z_]+)(?:\\s+(.*))?$', 'i');
    for (const line of String(stdout || '').split(/\r?\n/)) {
        const m = re.exec(line);
        if (!m) continue;
        const type = m[1].toUpperCase();
        const data = (m[2] || '').trim();
        if (type === 'REG_DWORD') return parseInt(data, 16);
        return data;
    }
    return null;
}

// ------------------------------------------------------------------
// Impure edges (spawn injectable for tests)
// ------------------------------------------------------------------

function defaultSpawn() {
    return require('child_process').spawnSync;
}

function readReg(name, spawn) {
    const r = (spawn || defaultSpawn())('reg', ['query', REG_KEY, '/v', name], { encoding: 'utf8', windowsHide: true });
    if (!r || r.status !== 0) return null;
    return parseRegQuery(r.stdout, name);
}

function writeRegString(name, value, spawn) {
    const r = (spawn || defaultSpawn())('reg', ['add', REG_KEY, '/v', name, '/t', 'REG_SZ', '/d', value, '/f'], { encoding: 'utf8', windowsHide: true });
    return !!r && r.status === 0;
}

function deleteRegValue(name, spawn) {
    const r = (spawn || defaultSpawn())('reg', ['delete', REG_KEY, '/v', name, '/f'], { encoding: 'utf8', windowsHide: true });
    return !!r && r.status === 0;
}

/**
 * Runs one `claude plugin marketplace` command for `configDir` with CLAUDE_CONFIG_DIR set in the
 * CHILD env only (see childEnvFor). cwd is the temp dir so no project's settings are in scope.
 */
function runMarketplace(action, { configDir, sharedRoot, userProfileDir, baseEnv, spawn }) {
    const cmd = marketplaceCommand(action, sharedRoot);
    const env = childEnvFor(configDir, userProfileDir, baseEnv || process.env);
    const r = (spawn || defaultSpawn())(cmd, [], {
        shell: true, env, cwd: os.tmpdir(), encoding: 'utf8', windowsHide: true, timeout: 120000,
    });
    if (!r || r.error) {
        return { ok: false, outcome: 'failed', failureCode: 'spawn_error', message: r && r.error ? r.error.message : 'no result' };
    }
    const res = parseCliResult(r.stdout, r.status);
    if (!res.message && r.stderr) res.message = String(r.stderr).trim();
    return res;
}

module.exports = {
    REG_KEY, REG_PATH, REG_CREATED, REG_PROFILES, MARKETPLACE_NAME,
    expandEnvVars, cleanDir, dirKey, sameDir, isAbsoluteWindowsDir, parseProfileList,
    defaultConfigDir, isDefaultConfigDir, unsafeDirReason, childEnvFor,
    planPostInstall, registeredAfterInstall, sharedDirRemovalDecision,
    marketplaceCommand, parseCliResult, parseRegQuery,
    readReg, writeRegString, deleteRegValue, runMarketplace,
};
