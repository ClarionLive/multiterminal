// Unit tests for installer/plugin-registration.js (ticket c8ee0eec, GitHub #23).
// Run: node --test installer/test/*.test.mjs
//
// Pins the decisions the installer scripts make, without a registry, a claude CLI or a real
// Claude Code profile: which config dir gets CLAUDE_CONFIG_DIR (never the default one), what an
// install/upgrade adds and removes, what stays recorded for uninstall, and when uninstall may
// delete the shared directory (only when the installer created it). The impure edges are driven
// through an injected spawn that records its calls, so no test can reach reg.exe or claude.
//
// NOT run by CI: .github/workflows/ci.yml globs only mcp/test/*.test.mjs.
//
// FALSIFIED (2026-10-09) by a script that, per mutation, asserted the find string occurred exactly
// once and was gone afterwards, ran this file, and restored plugin-registration.js byte-for-byte.
// Expected red sets were written down before the run, and matched exactly:
//   - childEnvFor's `if (!isDefaultConfigDir(...))` -> `if (true)`: red = "default profile dir never
//     gets CLAUDE_CONFIG_DIR..." and "runMarketplace passes the per-profile env..."; 11 others green.
//   - sharedDirRemovalDecision's `if (createdFlag !== 1) return` -> `if (false) return`: red = "the
//     created flag gates deletion..." only.
// The other facts were not individually falsified.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const reg = require('../plugin-registration.js');

const USERPROFILE = 'C:\\Users\\Test User';
const DEFAULT_CFG = 'C:\\Users\\Test User\\.claude';
const WORK_CFG = 'C:\\Users\\Test User\\.claude-work';
const SHARED = 'C:\\Users\\Test User\\AppData\\Local\\ClarionLive\\claude-plugins\\marketplaces\\multiterminal-marketplace';

// ---------------------------------------------------------------- childEnvFor

test('default profile dir never gets CLAUDE_CONFIG_DIR, in any spelling, and an inherited one is dropped', () => {
    const base = { PATH: 'x', Claude_Config_Dir: 'C:\\somewhere-else' };
    for (const spelling of [DEFAULT_CFG, DEFAULT_CFG + '\\', 'c:\\users\\test user\\.CLAUDE', '"' + DEFAULT_CFG + '"']) {
        const env = reg.childEnvFor(spelling, USERPROFILE, base);
        const keys = Object.keys(env).filter(k => k.toUpperCase() === 'CLAUDE_CONFIG_DIR');
        assert.deepEqual(keys, [], `CLAUDE_CONFIG_DIR present for ${spelling}`);
        assert.equal(env.PATH, 'x');
    }
    assert.equal(base.Claude_Config_Dir, 'C:\\somewhere-else', 'base env must not be mutated');
});

test('a non-default profile dir gets CLAUDE_CONFIG_DIR in the child env only', () => {
    const base = { PATH: 'x', CLAUDE_CONFIG_DIR: 'C:\\inherited' };
    const env = reg.childEnvFor(WORK_CFG, USERPROFILE, base);
    assert.equal(env.CLAUDE_CONFIG_DIR, WORK_CFG);
    assert.equal(Object.keys(env).filter(k => k.toUpperCase() === 'CLAUDE_CONFIG_DIR').length, 1);
    assert.equal(base.CLAUDE_CONFIG_DIR, 'C:\\inherited', 'base env must not be mutated');
});

test('runMarketplace passes the per-profile env to the child and leaves process.env alone', () => {
    const before = process.env.CLAUDE_CONFIG_DIR;
    const calls = [];
    const spawn = (cmd, args, opts) => {
        calls.push({ cmd, opts });
        return { status: 0, stdout: '{"command":"marketplace-add","outcome":"ok","message":"done"}\n', stderr: '' };
    };
    const a = reg.runMarketplace('add', { configDir: WORK_CFG, sharedRoot: SHARED, userProfileDir: USERPROFILE, baseEnv: { PATH: 'p' }, spawn });
    const b = reg.runMarketplace('add', { configDir: DEFAULT_CFG, sharedRoot: SHARED, userProfileDir: USERPROFILE, baseEnv: { PATH: 'p', CLAUDE_CONFIG_DIR: WORK_CFG }, spawn });
    assert.ok(a.ok && b.ok);
    assert.equal(calls[0].opts.env.CLAUDE_CONFIG_DIR, WORK_CFG);
    assert.equal('CLAUDE_CONFIG_DIR' in calls[1].opts.env, false, 'default profile ran with CLAUDE_CONFIG_DIR');
    assert.equal(calls[0].cmd, `claude plugin marketplace add "${SHARED}" --scope user --json`);
    assert.equal(process.env.CLAUDE_CONFIG_DIR, before);
});

test('remove targets the marketplace by name in user scope; a spawn error is a failure, not a throw', () => {
    assert.equal(reg.marketplaceCommand('remove', SHARED), 'claude plugin marketplace remove multiterminal-marketplace --scope user --json');
    const r = reg.runMarketplace('remove', { configDir: WORK_CFG, sharedRoot: SHARED, userProfileDir: USERPROFILE, baseEnv: {}, spawn: () => ({ error: new Error('ENOENT') }) });
    assert.equal(r.ok, false);
    assert.equal(r.failureCode, 'spawn_error');
});

// ---------------------------------------------------------------- planning

test('shared install adds every requested dir and removes previous ones no longer listed', () => {
    const plan = reg.planPostInstall({ mode: 'shared', requested: [WORK_CFG, 'D:\\p2'], previous: ['d:\\P2\\', 'D:\\old'] });
    assert.deepEqual(plan.add, [WORK_CFG, 'D:\\p2']);
    assert.deepEqual(plan.remove, ['D:\\old']);
});

test('default install adds nothing and removes every previous registration; skip touches nothing', () => {
    assert.deepEqual(reg.planPostInstall({ mode: 'default', requested: [WORK_CFG], previous: [WORK_CFG] }), { add: [], remove: [WORK_CFG] });
    assert.equal(reg.planPostInstall({ mode: 'skip', requested: [WORK_CFG], previous: [WORK_CFG] }), null);
});

test('recorded list keeps successful adds, already-registered failed adds, and failed removes', () => {
    const plan = { add: ['D:\\ok', 'D:\\failnew', 'D:\\failold'], remove: ['D:\\gone', 'D:\\stuck', 'D:\\absent'] };
    const results = {
        [reg.dirKey('D:\\ok')]: { ok: true },
        [reg.dirKey('D:\\failnew')]: { ok: false, failureCode: 'invalid_source' },
        [reg.dirKey('D:\\failold')]: { ok: false, failureCode: 'spawn_error' },
        [reg.dirKey('D:\\gone')]: { ok: true },
        [reg.dirKey('D:\\stuck')]: { ok: false, failureCode: 'spawn_error' },
        [reg.dirKey('D:\\absent')]: { ok: false, failureCode: 'not_configured' },
    };
    const out = reg.registeredAfterInstall({ plan, previous: ['D:\\failold', 'D:\\gone', 'D:\\stuck', 'D:\\absent'], results });
    assert.deepEqual(out, ['D:\\ok', 'D:\\failold', 'D:\\stuck']);
});

test('profile list: ; separated, %VARS% expanded, quotes/trailing slashes dropped, duplicates removed', () => {
    const list = reg.parseProfileList(' "%USERPROFILE%\\.claude-work\\" ;;C:\\Users\\Test User\\.CLAUDE-WORK; D:\\x\\ ', { UserProfile: USERPROFILE });
    assert.deepEqual(list, [WORK_CFG, 'D:\\x']);
    assert.deepEqual(reg.parseProfileList('', {}), []);
});

test('unsafe dirs are refused before reaching cmd.exe', () => {
    assert.equal(reg.unsafeDirReason(WORK_CFG), null);
    assert.equal(reg.unsafeDirReason('\\\\server\\share\\cfg'), null);
    assert.notEqual(reg.unsafeDirReason('relative\\dir'), null);
    assert.notEqual(reg.unsafeDirReason('C:\\a"&calc&"'), null);
    assert.notEqual(reg.unsafeDirReason('C:\\%TEMP%\\x'), null);
    assert.notEqual(reg.unsafeDirReason(''), null);
});

// ---------------------------------------------------------------- uninstall deletion gate

test('the created flag gates deletion of the shared directory', () => {
    const base = { sharedRoot: SHARED, userProfileDir: USERPROFILE, hasManifest: true };
    assert.equal(reg.sharedDirRemovalDecision({ ...base, createdFlag: 1 }).remove, true);
    assert.equal(reg.sharedDirRemovalDecision({ ...base, createdFlag: 0 }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...base, createdFlag: null }).remove, false);
});

test('even when created, uninstall refuses an unrecognised folder, a drive root, or the default-profile location', () => {
    const ok = { createdFlag: 1, userProfileDir: USERPROFILE, hasManifest: true };
    assert.equal(reg.sharedDirRemovalDecision({ ...ok, sharedRoot: SHARED, hasManifest: false }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...ok, sharedRoot: 'C:\\' }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...ok, sharedRoot: 'relative' }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...ok, sharedRoot: DEFAULT_CFG + '\\plugins\\marketplaces\\multiterminal-marketplace\\' }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...ok, sharedRoot: null }).remove, false);
});

// ---------------------------------------------------------------- parsers

test('reg query output parses strings, dwords, empty strings and absence', () => {
    const out = '\r\nHKEY_CURRENT_USER\\Software\\MultiTerminal\r\n    PluginMarketplacePath    REG_SZ    C:\\a b\\m    x\r\n    PluginDirCreatedByInstaller    REG_DWORD    0x1\r\n    PluginRegisteredProfiles    REG_SZ    \r\n';
    assert.equal(reg.parseRegQuery(out, 'PluginMarketplacePath'), 'C:\\a b\\m    x');
    assert.equal(reg.parseRegQuery(out, 'PluginDirCreatedByInstaller'), 1);
    assert.equal(reg.parseRegQuery(out, 'PluginRegisteredProfiles'), '');
    assert.equal(reg.parseRegQuery(out, 'Missing'), null);
});

test('CLI --json result: last JSON line wins; not_configured is surfaced', () => {
    const r = reg.parseCliResult('noise\n{"command":"marketplace-remove","outcome":"failed","failureCode":"not_configured","message":"nf"}\n', 1);
    assert.deepEqual([r.ok, r.failureCode], [false, 'not_configured']);
    assert.equal(reg.parseCliResult('{"outcome":"ok"}', 0).ok, true);
    assert.equal(reg.parseCliResult('{"outcome":"ok"}', 1).ok, false, 'non-zero exit is never ok');
});
