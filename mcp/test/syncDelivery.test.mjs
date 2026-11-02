import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

function relativeImports(source) {
  const imports = [];
  const pattern = /(?:\bfrom\s+|\bimport\s+)["'](\.[^"']+)["']/g;
  for (const match of source.matchAll(pattern)) imports.push(match[1]);
  return imports;
}

function relativeFiles(root, current = "") {
  const directory = path.join(root, current);
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const relative = path.join(current, entry.name);
    if (entry.isDirectory()) files.push(...relativeFiles(root, relative));
    else files.push(relative);
  }
  return files.sort();
}

test("sync script delivers every relative index.js import", { skip: process.platform !== "win32" }, () => {
  const root = mkdtempSync(path.join(os.tmpdir(), "mt-mcp-sync-"));
  const source = path.join(root, "source");
  const dest = path.join(root, "dest");

  try {
    cpSync(path.join(repoRoot, "mcp"), source, { recursive: true });
    mkdirSync(path.join(dest, "node_modules"), { recursive: true });
    cpSync(path.join(source, "package.json"), path.join(dest, "package.json"));
    cpSync(path.join(source, "package-lock.json"), path.join(dest, "package-lock.json"));

    const syncEnv = { ...process.env, SMS_SOURCE_DIR: source, SMS_DEST_DIR: dest };
    delete syncEnv.CI;
    delete syncEnv.SMS_FAIL_ON_ERROR;
    execFileSync("powershell.exe", [
      "-NoProfile",
      "-ExecutionPolicy", "Bypass",
      "-File", path.join(repoRoot, "scripts", "sync-mcp-server.ps1"),
    ], {
      env: syncEnv,
      encoding: "utf8",
      stdio: "pipe",
    });

    const indexSource = readFileSync(path.join(source, "index.js"), "utf8");
    const imports = relativeImports(indexSource);
    assert.ok(imports.length > 0, "guard is vacuous: index.js has no relative imports");
    assert.deepEqual(
      relativeFiles(path.join(dest, "lib")),
      relativeFiles(path.join(source, "lib")),
      "dest/lib must contain exactly the source library file list",
    );
    assert.equal(existsSync(path.join(dest, "lib", "lib")), false, "sync must not create dest/lib/lib");
    for (const importPath of imports) {
      const relativePath = importPath.replace(/^\.\//, "");
      const delivered = path.join(dest, relativePath);
      assert.equal(
        readFileSync(delivered, "utf8"),
        readFileSync(path.join(source, relativePath), "utf8"),
        `${importPath} was not delivered byte-for-byte`,
      );
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
