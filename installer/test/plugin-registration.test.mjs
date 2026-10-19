// Unit tests for installer/plugin-registration.js (ticket c8ee0eec, GitHub #23).
// Run: node --test installer/test/*.test.mjs
//
// Pins the decisions the installer scripts make, without a registry, a claude CLI or a real
// Claude Code profile: which config dir gets CLAUDE_CONFIG_DIR (never the default one), what an
// install/upgrade adds and removes, what stays recorded for uninstall, and when uninstall may
// delete the shared directory (only when the installer created it). The impure edges are driven
// through an injected spawn that records its calls, so no test can reach reg.exe or claude.
//
// Run by CI: the "installer node tests" step of .github/workflows/ci.yml runs installer/test/*.test.mjs.
//
// FALSIFIED (2026-10-09) by a script that, per mutation, asserted the find string occurred exactly
// once and was gone afterwards, ran this file, and restored plugin-registration.js byte-for-byte.
// Expected red sets were written down before the run, and matched exactly:
//   - childEnvFor's `if (!isDefaultConfigDir(...))` -> `if (true)`: red = "default profile dir never
//     gets CLAUDE_CONFIG_DIR..." and "runMarketplace passes the per-profile env..."; 11 others green.
//   - sharedDirRemovalDecision's `if (createdFlag !== 1) return` -> `if (false) return`: red = "the
//     created flag gates deletion..." only.
// (Those two runs predate the review fixes; the suite then had 13 facts.)
//   - 2026-10-09 review fix B, same method, 22 facts: deleting sharedDirRemovalDecision's
//     `if (want !== have) return` line (1 occurrence before, 0 after): expected red = exactly
//     "ownership marker content that does not match PluginDirOwnerToken -> keep"; matched, 21 green.
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
    assert.deepEqual(reg.planPostInstall({ mode: 'default', requested: [WORK_CFG], previous: [WORK_CFG] }), { add: [], remove: [WORK_CFG], skippedDefault: [] });
    assert.equal(reg.planPostInstall({ mode: 'skip', requested: [WORK_CFG], previous: [WORK_CFG] }), null);
});

test('the default profile is never added or removed, in any spelling, and is reported as skipped', () => {
    const spellings = [DEFAULT_CFG, DEFAULT_CFG + '\\', 'c:/users/test user/.CLAUDE/', 'C:\\USERS\\Test User\\.claude'];
    for (const d of spellings) {
        const plan = reg.planPostInstall({ mode: 'shared', requested: [d, WORK_CFG], previous: [], userProfileDir: USERPROFILE });
        assert.deepEqual(plan.add, [WORK_CFG], `default added for ${d}`);
        assert.equal(plan.skippedDefault.length, 1, `not reported as skipped for ${d}`);
    }
    // A previously recorded default entry is not de-registered either (nor re-recorded).
    const plan = reg.planPostInstall({ mode: 'default', requested: [], previous: [DEFAULT_CFG, WORK_CFG], userProfileDir: USERPROFILE });
    assert.deepEqual(plan.remove, [WORK_CFG]);
    assert.deepEqual(plan.skippedDefault, [DEFAULT_CFG]);
});

test('preflight: a missing profile dir is never run; for a remove it counts as already clean', () => {
    assert.equal(reg.profileDirPreflight({ action: 'remove', dir: WORK_CFG, exists: true }), null);
    assert.equal(reg.profileDirPreflight({ action: 'add', dir: WORK_CFG, exists: true, rootProblem: null }), null);
    assert.equal(reg.profileDirPreflight({ action: 'remove', dir: WORK_CFG, exists: false }).failureCode, 'not_configured');
    assert.equal(reg.profileDirPreflight({ action: 'add', dir: WORK_CFG, exists: false }).failureCode, 'missing_dir');
    assert.equal(reg.profileDirPreflight({ action: 'add', dir: WORK_CFG, exists: true, rootProblem: 'bad root' }).failureCode, 'invalid_input');
    assert.equal(reg.profileDirPreflight({ action: 'remove', dir: 'relative', exists: true }).failureCode, 'invalid_input');
    // ...and the not_configured result drops the dir from the recorded list.
    const plan = { add: [], remove: [WORK_CFG] };
    const results = { [reg.dirKey(WORK_CFG)]: { ok: false, ...reg.profileDirPreflight({ action: 'remove', dir: WORK_CFG, exists: false }) } };
    assert.deepEqual(reg.registeredAfterInstall({ plan, previous: [WORK_CFG], results }), []);
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

// The shared "same folder" definition. MultiTerminal.iss CleanPluginPath (folds '/', ExpandFileName
// on absolute paths, drops the trailing '\', compared with CompareText) must agree with this case.
test('sameDir: forward slashes and .. segments name the same folder; a different folder does not', () => {
    assert.equal(reg.sameDir('C:/A/Y', 'C:\\A\\B\\..\\Y'), true);
    assert.equal(reg.sameDir('C:/A/Y/', 'c:\\a\\.\\y'), true);
    assert.equal(reg.sameDir('C:/A/Y', 'C:\\A\\B\\..\\Z'), false);
    assert.equal(reg.sameDir('C:/A/Y', 'C:\\A\\B\\Y'), false);
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

// Every input that allows deletion; each test below breaks exactly one.
const TOKEN = '{3F2504E0-4F89-11D3-9A0C-0305E82C3301}';
const OWNED = {
    sharedRoot: SHARED, createdFlag: 1, userProfileDir: USERPROFILE, hasManifest: true,
    markerToken: TOKEN, ownerToken: TOKEN, isReparsePoint: false, realPath: SHARED,
};

test('the created flag gates deletion of the shared directory', () => {
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, createdFlag: 1 }).remove, true);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, createdFlag: 0 }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, createdFlag: null }).remove, false);
});

test('all ownership checks pass -> delete (realpath may differ only in case / trailing slash; tokens are trimmed)', () => {
    const d = reg.sharedDirRemovalDecision({ ...OWNED, realPath: SHARED.toUpperCase() + '\\', markerToken: ' ' + TOKEN + '\r\n' });
    assert.equal(d.remove, true, d.reason);
});

test('ownership marker content that does not match PluginDirOwnerToken -> keep', () => {
    const d = reg.sharedDirRemovalDecision({ ...OWNED, markerToken: '{00000000-0000-0000-0000-000000000000}' });
    assert.deepEqual([d.remove, d.reason], [false, 'kept: ownership marker does not match PluginDirOwnerToken']);
    // Ordinal: a token differing only in case is a different token.
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, markerToken: TOKEN.toLowerCase() }).remove, false);
});

test('marker missing, or no token in HKCU (a Phase-1 install) -> keep: no ownership marker', () => {
    for (const over of [{ markerToken: null }, { markerToken: '' }, { ownerToken: undefined }, { ownerToken: '  ' }]) {
        const d = reg.sharedDirRemovalDecision({ ...OWNED, ...over });
        assert.deepEqual([d.remove, d.reason], [false, 'kept: no ownership marker'], JSON.stringify(over));
    }
});

test('a reparse point, an unknown reparse state, or a realpath elsewhere -> keep', () => {
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, isReparsePoint: true }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, isReparsePoint: null }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, realPath: 'C:\\Program Files\\Something\\multiterminal-marketplace' }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, realPath: null }).remove, false);
});

test('inspectSharedDir: a junction reads as reparse; a failed probe reads as the keep answer', () => {
    const files = { [SHARED + '\\.mt-installer-owned']: '\uFEFF' + TOKEN + '\r\n' };
    const fake = (opts) => ({
        existsSync: p => p === SHARED + '\\.claude-plugin\\marketplace.json',
        readFileSync: p => { if (p in files) return files[p]; throw new Error('ENOENT'); },
        lstatSync: () => ({ isSymbolicLink: () => opts.link, isDirectory: () => !opts.link }),
        realpathSync: { native: () => { if (opts.realThrows) throw new Error('x'); return opts.real; } },
    });
    const plain = reg.inspectSharedDir(SHARED, fake({ link: false, real: SHARED }));
    assert.deepEqual(plain, { hasManifest: true, markerToken: TOKEN, isReparsePoint: false, realPath: SHARED });
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, ...plain }).remove, true);
    const junction = reg.inspectSharedDir(SHARED, fake({ link: true, real: 'D:\\elsewhere' }));
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, ...junction }).remove, false);
    const broken = reg.inspectSharedDir(SHARED, fake({ link: false, realThrows: true }));
    assert.equal(broken.realPath, null);
    assert.equal(reg.sharedDirRemovalDecision({ ...OWNED, ...broken }).remove, false);
});

test('even when created, uninstall refuses an unrecognised folder, a drive root, or the default-profile location', () => {
    // realPath follows sharedRoot so each case is refused by the check it names, not by realpath.
    const at = root => ({ ...OWNED, sharedRoot: root, realPath: root });
    assert.equal(reg.sharedDirRemovalDecision({ ...at(SHARED), hasManifest: false }).remove, false);
    assert.equal(reg.sharedDirRemovalDecision(at('C:\\')).remove, false);
    assert.equal(reg.sharedDirRemovalDecision(at('relative')).remove, false);
    assert.equal(reg.sharedDirRemovalDecision(at(DEFAULT_CFG + '\\plugins\\marketplaces\\multiterminal-marketplace\\')).remove, false);
    assert.equal(reg.sharedDirRemovalDecision(at(null)).remove, false);
});

// ---------------------------------------------------------------- registry read (reg export)

const NON_ASCII_ROOT = 'C:\\Users\\José Müller\\AppData\\Local\\插件\\multiterminal-marketplace';

/** Escapes a value the way `reg export` writes a REG_SZ. */
const regEsc = v => v.replace(/\\/g, '\\\\').replace(/"/g, '\\"');

/** A reg export file body (format measured on this machine against HKCU\Console and HKCU\Environment). */
function exportText(values, extra = '') {
    const lines = ['Windows Registry Editor Version 5.00', '', '[HKEY_CURRENT_USER\\Software\\MultiTerminal]'];
    for (const [k, v] of Object.entries(values)) {
        lines.push(typeof v === 'number' ? `"${k}"=dword:${v.toString(16).padStart(8, '0')}` : `"${k}"="${regEsc(v)}"`);
    }
    return lines.join('\r\n') + '\r\n' + extra;
}

/** A spawn that plays reg.exe: writes `body` as UTF-16LE with a BOM to the export target. */
function fakeRegExport(body, calls) {
    return (cmd, args) => {
        calls && calls.push({ cmd, args });
        if (body === null) return { status: 1, stdout: '', stderr: 'ERROR: The system was unable to find the specified registry key or value.' };
        require('fs').writeFileSync(args[2], Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(body, 'utf16le')]));
        return { status: 0, stdout: 'The operation completed successfully.', stderr: '' };
    };
}

test('non-ASCII paths (é, ü, CJK) round-trip through reg export; the temp file is removed', () => {
    const calls = [];
    const v = reg.readRegValues(fakeRegExport(exportText({ PluginMarketplacePath: NON_ASCII_ROOT, PluginDirCreatedByInstaller: 1 }), calls));
    assert.equal(v.PluginMarketplacePath, NON_ASCII_ROOT);
    assert.equal(v.PluginDirCreatedByInstaller, 1);
    assert.equal(calls[0].cmd, 'reg');
    assert.deepEqual([calls[0].args[0], calls[0].args[1], calls[0].args[3]], ['export', reg.REG_KEY, '/y']);
    assert.equal(require('fs').existsSync(calls[0].args[2]), false, 'temp export file left behind');
    assert.equal(require('fs').existsSync(require('path').dirname(calls[0].args[2])), false, 'temp dir left behind');
});

test('reg export parser: dwords, escaped backslashes and quotes, empty strings, absence, subkeys ignored', () => {
    const text = exportText({ PluginMarketplacePath: 'C:\\a b\\m "q"', PluginDirCreatedByInstaller: 0xffffffff, PluginRegisteredProfiles: '' },
        '\r\n[HKEY_CURRENT_USER\\Software\\MultiTerminal\\Sub]\r\n"PluginDirOwnerToken"="from-a-subkey"\r\n"Hex"=hex(2):25,00,\\\r\n  00,00\r\n');
    const v = reg.parseRegExport('\uFEFF' + text, reg.REG_EXPORT_SECTION);
    assert.equal(v.PluginMarketplacePath, 'C:\\a b\\m "q"');
    assert.equal(v.PluginDirCreatedByInstaller, 0xffffffff);
    assert.equal(v.PluginRegisteredProfiles, '');
    assert.equal(v.PluginDirOwnerToken, undefined, 'a subkey value leaked into the key');
    assert.equal(v.Missing, undefined);
    assert.equal(reg.unescapeRegString('C:\\\\x\\\\y'), 'C:\\x\\y');
});

test('a missing key (reg export exits nonzero) reads every value as absent', () => {
    assert.deepEqual(reg.readRegValues(fakeRegExport(null)), {});
    assert.equal(reg.readReg(reg.REG_PATH, fakeRegExport(null)), undefined);
    assert.equal(reg.readReg(reg.REG_PATH, () => ({ error: new Error('ENOENT') })), undefined);
});

// ---------------------------------------------------------------- parsers

test('CLI --json result: last JSON line wins; not_configured is surfaced', () => {
    const r = reg.parseCliResult('noise\n{"command":"marketplace-remove","outcome":"failed","failureCode":"not_configured","message":"nf"}\n', 1);
    assert.deepEqual([r.ok, r.failureCode], [false, 'not_configured']);
    assert.equal(reg.parseCliResult('{"outcome":"ok"}', 0).ok, true);
    assert.equal(reg.parseCliResult('{"outcome":"ok"}', 1).ok, false, 'non-zero exit is never ok');
});
