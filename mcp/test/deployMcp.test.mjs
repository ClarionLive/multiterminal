// Ticket 9ce606e0: deploy installs the MCP server the build staged, and refuses
// a downgrade before touching anything. These drive the real scripts:
// sync-mcp-server.ps1 in stage and deploy mode, and check-mcp-deploy.ps1.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const skip = process.platform !== "win32";
const checkScript = path.join(repoRoot, "scripts", "check-mcp-deploy.ps1");
const syncScript = path.join(repoRoot, "scripts", "sync-mcp-server.ps1");

function cleanEnv(extra = {}) {
  const env = { ...process.env };
  for (const k of ["CI", "SMS_FAIL_ON_ERROR", "SMS_STAGE_ONLY", "SMS_SOURCE_DIR", "SMS_DEST_DIR"]) delete env[k];
  return { ...env, ...extra };
}

function ps(script, args, env = cleanEnv()) {
  return spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, ...args], {
    env,
    encoding: "utf8",
  });
}

function check(staged, live, extra = []) {
  const r = ps(checkScript, ["-StagedDir", staged, "-LiveDir", live, ...extra]);
  return { code: r.status, out: r.stdout + r.stderr };
}

// A server copy: the repo's mcp/ (no node_modules), optionally edited and stamped.
function serverCopy(dir, { edit, stamp } = {}) {
  mkdirSync(dir, { recursive: true });
  for (const name of ["index.js", "package.json", "package-lock.json", "README.md"]) {
    cpSync(path.join(repoRoot, "mcp", name), path.join(dir, name));
  }
  cpSync(path.join(repoRoot, "mcp", "lib"), path.join(dir, "lib"), { recursive: true });
  if (edit) {
    const p = path.join(dir, "index.js");
    writeFileSync(p, edit(readFileSync(p, "utf8")));
  }
  if (stamp) writeFileSync(path.join(dir, ".mcp-stamp.json"), JSON.stringify(stamp));
  return dir;
}

function withTemp(fn) {
  const root = mkdtempSync(path.join(os.tmpdir(), "mt-mcp-deploy-"));
  try {
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// Renaming a tool in the LIVE copy gives it a tool the staged copy lacks.
const renameTool = (src) => src.replace('name: "list_tasks"', 'name: "list_tasks_retired"');
const touch = (src) => src + "\n// edited\n";

test("stage mode copies the server and a stamp from a worktree path, without npm", { skip }, () =>
  withTemp((root) => {
    const source = serverCopy(path.join(root, ".claude", "worktrees", "abc", "mcp"));
    const staged = path.join(root, "staged", "mcp-server");
    mkdirSync(path.join(staged, "lib"), { recursive: true });
    writeFileSync(path.join(staged, "lib", "deleted-upstream.mjs"), "stale");

    const r = ps(syncScript, [], cleanEnv({ SMS_SOURCE_DIR: source, SMS_DEST_DIR: staged, SMS_STAGE_ONLY: "true" }));
    assert.equal(r.status, 0, r.stdout + r.stderr);
    assert.doesNotMatch(r.stdout, /MCPSYNC SKIP/, "the worktree guard must not block staging");
    assert.equal(readFileSync(path.join(staged, "index.js"), "utf8"), readFileSync(path.join(source, "index.js"), "utf8"));
    assert.equal(existsSync(path.join(staged, "node_modules")), false, "staging must not run npm");
    assert.equal(existsSync(path.join(staged, "lib", "deleted-upstream.mjs")), false, "a stale lib file must not survive staging");

    const stamp = JSON.parse(readFileSync(path.join(staged, ".mcp-stamp.json"), "utf8").replace(/^﻿/, ""));
    assert.ok(stamp.toolCount > 50, `toolCount ${stamp.toolCount}`);
    assert.equal(stamp.toolNames.length, stamp.toolCount);
    assert.ok(stamp.toolNames.includes("list_tasks"));
    assert.equal(stamp.commit, "", "a folder outside git has no commit, never a guessed one");
  }));

test("stage mode without an explicit dest stages nothing (never the live APPDATA copy)", { skip }, () =>
  withTemp((root) => {
    const source = serverCopy(path.join(root, "src"));
    const fakeAppData = path.join(root, "appdata");
    const r = ps(syncScript, [], cleanEnv({ SMS_SOURCE_DIR: source, SMS_STAGE_ONLY: "true", APPDATA: fakeAppData }));
    assert.equal(r.status, 0);
    assert.match(r.stdout, /needs an explicit dest/);
    assert.equal(existsSync(path.join(fakeAppData, "multiterminal", "mcp")), false);
  }));

test("preflight: no staged server means deploy binaries only (exit 3)", { skip }, () =>
  withTemp((root) => {
    const live = serverCopy(path.join(root, "live"));
    assert.equal(check(path.join(root, "staged-missing"), live).code, 3);
  }));

test("preflight: no live server is a fresh install (exit 0)", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"));
    assert.equal(check(staged, path.join(root, "live-missing")).code, 0);
  }));

test("preflight: identical index.js has nothing to sync (exit 2)", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"));
    const live = serverCopy(path.join(root, "live"));
    assert.equal(check(staged, live).code, 2);
  }));

test("preflight: refuses when agents would lose a tool, and -Force overrides", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"));
    const live = serverCopy(path.join(root, "live"), { edit: renameTool });
    const refused = check(staged, live);
    assert.equal(refused.code, 1, refused.out);
    assert.match(refused.out, /list_tasks_retired/);
    assert.match(refused.out, /Nothing has been changed/);
    assert.equal(check(staged, live, ["-Force"]).code, 0);
  }));

test("preflight: unknown provenance with the same tools is allowed with a warning", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"), { edit: touch });
    const live = serverCopy(path.join(root, "live"));
    const r = check(staged, live);
    assert.equal(r.code, 0, r.out);
    assert.match(r.out, /provenance unknown/);
  }));

test("preflight: refuses a staged commit that does not contain the live commit", { skip }, () =>
  withTemp((root) => {
    const repo = path.join(root, "repo");
    mkdirSync(repo);
    const git = (...a) => execFileSync("git", ["-C", repo, ...a], { encoding: "utf8" }).trim();
    git("init", "-q");
    git("config", "user.email", "t@example.invalid");
    git("config", "user.name", "t");
    git("commit", "-q", "--allow-empty", "-m", "base");
    const base = git("rev-parse", "HEAD");
    git("commit", "-q", "--allow-empty", "-m", "live");
    const liveCommit = git("rev-parse", "HEAD");
    git("checkout", "-q", "-b", "side", base);
    git("commit", "-q", "--allow-empty", "-m", "side");
    const sideCommit = git("rev-parse", "HEAD");
    git("checkout", "-q", "-b", "after", liveCommit);
    git("commit", "-q", "--allow-empty", "-m", "after");
    const afterCommit = git("rev-parse", "HEAD");

    const live = serverCopy(path.join(root, "live"), { stamp: { commit: liveCommit } });
    const diverged = serverCopy(path.join(root, "diverged"), { edit: touch, stamp: { commit: sideCommit } });
    const descendant = serverCopy(path.join(root, "descendant"), { edit: touch, stamp: { commit: afterCommit } });

    const refused = check(diverged, live, ["-RepoDir", repo]);
    assert.equal(refused.code, 1, refused.out);
    assert.match(refused.out, /does not contain/);
    const allowed = check(descendant, live, ["-RepoDir", repo]);
    assert.equal(allowed.code, 0, allowed.out);
  }));

test("deploy mode installs the staged server and passes its stamp through", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"), { stamp: { commit: "abc123", toolCount: 1, toolNames: ["x"] } });
    const live = path.join(root, "live");
    // An existing install with node_modules and the same lockfile: no npm run needed.
    mkdirSync(path.join(live, "node_modules"), { recursive: true });
    cpSync(path.join(staged, "package-lock.json"), path.join(live, "package-lock.json"));
    cpSync(path.join(staged, "package.json"), path.join(live, "package.json"));

    const r = ps(syncScript, ["-SourceDir", staged, "-DestDir", live, "-DeployMode"]);
    assert.equal(r.status, 0, r.stdout + r.stderr);
    assert.doesNotMatch(r.stdout, /running npm/, "deploy mode must not force a reinstall when the lockfile is unchanged");
    assert.equal(readFileSync(path.join(live, "index.js"), "utf8"), readFileSync(path.join(staged, "index.js"), "utf8"));
    const stamp = JSON.parse(readFileSync(path.join(live, ".mcp-stamp.json"), "utf8").replace(/^﻿/, ""));
    assert.equal(stamp.commit, "abc123");
  }));

test("preflight: a lib-only change is not 'already current' (Codex Run 1)", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"));
    const live = serverCopy(path.join(root, "live"));
    writeFileSync(path.join(live, "lib", "task-formatters.mjs"), "// an older library\n");
    const r = check(staged, live);
    assert.equal(r.code, 0, r.out);
    const lockOnly = serverCopy(path.join(root, "live2"));
    writeFileSync(path.join(lockOnly, "package-lock.json"), "{}\n");
    assert.equal(check(staged, lockOnly).code, 0, "a lockfile-only difference must sync too");
  }));

test("preflight: refuses a staged copy that no longer matches its own stamp (Codex Run 1)", { skip }, () =>
  withTemp((root) => {
    const source = serverCopy(path.join(root, "src"));
    const staged = path.join(root, "staged");
    const r = ps(syncScript, [], cleanEnv({ SMS_SOURCE_DIR: source, SMS_DEST_DIR: staged, SMS_STAGE_ONLY: "true" }));
    assert.equal(r.status, 0, r.stdout + r.stderr);
    const live = serverCopy(path.join(root, "live"), { edit: touch });
    assert.equal(check(staged, live).code, 0, "an intact staged copy is accepted");
    writeFileSync(path.join(staged, "lib", "context-formatters.mjs"), "// half-written by a concurrent build\n");
    const refused = check(staged, live);
    assert.equal(refused.code, 1, refused.out);
    assert.match(refused.out, /does not match its own stamp/);
  }));

test("preflight: refuses when both stamps name a commit git cannot place (Codex Run 1)", { skip }, () =>
  withTemp((root) => {
    const repo = path.join(root, "repo");
    mkdirSync(repo);
    execFileSync("git", ["-C", repo, "init", "-q"]);
    const live = serverCopy(path.join(root, "live"), { stamp: { commit: "1".repeat(40) } });
    const staged = serverCopy(path.join(root, "staged"), { edit: touch, stamp: { commit: "2".repeat(40) } });
    const r = check(staged, live, ["-RepoDir", repo]);
    assert.equal(r.code, 1, r.out);
    assert.match(r.out, /cannot check/);
  }));

test("the PowerShell tool count matches the server's own matcher", { skip }, () =>
  withTemp((root) => {
    const src = readFileSync(path.join(repoRoot, "mcp", "index.js"), "utf8");
    const region = src.slice(
      src.indexOf("server.setRequestHandler(ListToolsRequestSchema"),
      src.indexOf("server.setRequestHandler(CallToolRequestSchema"),
    );
    const nodeNames = [...new Set([...region.matchAll(/\bname:\s*"([^"]+)"/g)].map((m) => m[1]))].sort();
    const staged = path.join(root, "staged");
    const r = ps(syncScript, [], cleanEnv({ SMS_SOURCE_DIR: serverCopy(path.join(root, "src")), SMS_DEST_DIR: staged, SMS_STAGE_ONLY: "true" }));
    assert.equal(r.status, 0, r.stdout + r.stderr);
    const stamp = JSON.parse(readFileSync(path.join(staged, ".mcp-stamp.json"), "utf8").replace(/^\uFEFF/, ""));
    assert.deepEqual([...stamp.toolNames].sort(), nodeNames);
  }));

test("deploy mode removes a live lib/ module the source no longer has (debugger Run 1)", { skip }, () =>
  withTemp((root) => {
    const staged = serverCopy(path.join(root, "staged"));
    const live = serverCopy(path.join(root, "live"));
    mkdirSync(path.join(live, "node_modules"), { recursive: true });
    writeFileSync(path.join(live, "lib", "retired.mjs"), "export {};\n");
    writeFileSync(path.join(live, "tasks.db"), "data");
    const r = ps(syncScript, ["-SourceDir", staged, "-DestDir", live, "-DeployMode"]);
    assert.equal(r.status, 0, r.stdout + r.stderr);
    assert.equal(existsSync(path.join(live, "lib", "retired.mjs")), false);
    assert.equal(existsSync(path.join(live, "lib", "task-formatters.mjs")), true);
    assert.equal(existsSync(path.join(live, "tasks.db")), true, "live data must survive");
    assert.equal(existsSync(path.join(live, "node_modules")), true);
  }));
