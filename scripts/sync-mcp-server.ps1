#requires -Version 5.1
# Syncs the canonical repo MCP server (mcp/) to a destination copy.
#
# Part of ticket ec97c446 "MCP server source-of-truth fix": the git repo's
# mcp/ folder is the single source of truth. Historically %APPDATA%\multiterminal\mcp
# was hand-edited in place and drifted from git (deployed had delete_project the
# repo lacked; the repo had a cost-render $ fix the deployed copy had regressed).
# This script makes the repo push one-way to the live/shipped copies so that
# drift can never silently accumulate again.
#
# Invoked from MultiTerminal.csproj's SyncMcpServer MSBuild target after every
# build. Values arrive via process EnvironmentVariables (SMS_SOURCE_DIR /
# SMS_DEST_DIR) so they do NOT pass through the shell -- avoids command injection
# via a hostile path. When invoked directly (developer testing), the
# -SourceDir / -DestDir parameters work as a fallback; -DestDir defaults to the
# current user's %APPDATA%\multiterminal\mcp.
#
# Design invariants:
#   * ONE-WAY: repo -> dest. Never reads dest back into the repo.
#   * SELECTIVE: copies only the git-tracked files (index.js, package.json,
#     README.md). Deliberately does NOT mirror/delete -- dest\node_modules and
#     dest\*.db must survive (node_modules is gitignored; the DBs are live data).
#   * NEWER-TARGET GUARD: if dest\index.js is newer than source\index.js it may
#     hold an out-of-band edit; warn loudly before overwriting (repo still wins).
#   * CONDITIONAL npm install: only when package.json content changed or
#     dest\node_modules is missing.
#   * NON-FATAL: every failure path exits 0 so an error-checking build is never
#     broken by a sync hiccup. The runtime def<->handler assert (item 4) and the
#     verification step (item 6) are the loud backstops.

param(
    [string]$SourceDir,
    [string]$DestDir
)

$ErrorActionPreference = "Continue"

# --- Resolve source (repo mcp/) --------------------------------------------
if ([string]::IsNullOrWhiteSpace($SourceDir) -and $env:SMS_SOURCE_DIR) { $SourceDir = $env:SMS_SOURCE_DIR }
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    Write-Host "SyncMcpServer: no source dir provided (set SMS_SOURCE_DIR or pass -SourceDir); skipping."
    exit 0
}
if (-not (Test-Path -LiteralPath $SourceDir)) {
    Write-Host "SyncMcpServer: source dir does not exist, skipping: $SourceDir"
    exit 0
}
$sourceIndex = Join-Path $SourceDir "index.js"
if (-not (Test-Path -LiteralPath $sourceIndex)) {
    Write-Host "SyncMcpServer: no index.js in source, skipping: $SourceDir"
    exit 0
}

# --- Resolve dest (defaults to current user's APPDATA copy) -----------------
if ([string]::IsNullOrWhiteSpace($DestDir) -and $env:SMS_DEST_DIR) { $DestDir = $env:SMS_DEST_DIR }
if ([string]::IsNullOrWhiteSpace($DestDir)) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        Write-Host "SyncMcpServer: no dest dir and %APPDATA% is empty; skipping."
        exit 0
    }
    $DestDir = Join-Path $env:APPDATA "multiterminal\mcp"
}

try {
    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
        Write-Host "SyncMcpServer: created dest dir $DestDir"
    }

    $destIndex   = Join-Path $DestDir "index.js"
    $sourcePkg   = Join-Path $SourceDir "package.json"
    $destPkg     = Join-Path $DestDir "package.json"
    $destModules = Join-Path $DestDir "node_modules"

    # --- Newer-target guard: detect an out-of-band edit before we clobber it ---
    if (Test-Path -LiteralPath $destIndex) {
        $srcTime  = (Get-Item -LiteralPath $sourceIndex).LastWriteTimeUtc
        $destTime = (Get-Item -LiteralPath $destIndex).LastWriteTimeUtc
        if ($destTime -gt $srcTime) {
            Write-Host "SyncMcpServer: WARNING -- dest index.js is NEWER than the repo copy:" -ForegroundColor Yellow
            Write-Host "  dest:   $destIndex ($destTime UTC)" -ForegroundColor Yellow
            Write-Host "  source: $sourceIndex ($srcTime UTC)" -ForegroundColor Yellow
            Write-Host "  It may contain an out-of-band edit that is about to be OVERWRITTEN." -ForegroundColor Yellow
            Write-Host "  If that edit is intentional, move it into the repo mcp/ and commit -- the repo is canonical." -ForegroundColor Yellow
        }
    }

    # --- Decide whether npm install is needed (BEFORE we overwrite package.json) ---
    $needsInstall = $false
    if (-not (Test-Path -LiteralPath $destModules)) {
        $needsInstall = $true
        Write-Host "SyncMcpServer: dest node_modules missing -- will npm install."
    }
    elseif ((Test-Path -LiteralPath $sourcePkg) -and (Test-Path -LiteralPath $destPkg)) {
        # Content compare (not Get-FileHash -- that cmdlet is absent in some
        # minimal PS 5.1 hosts). Normalize line endings + trim so a pure CRLF/LF
        # difference (git checks out mcp/ CRLF; the dest copy may be LF) doesn't
        # trigger a spurious npm install. Only a real dependency edit should.
        $srcText  = ([System.IO.File]::ReadAllText($sourcePkg)).Replace("`r`n", "`n").Trim()
        $destText = ([System.IO.File]::ReadAllText($destPkg)).Replace("`r`n", "`n").Trim()
        if ($srcText -ne $destText) {
            $needsInstall = $true
            Write-Host "SyncMcpServer: package.json changed -- will npm install."
        }
    }

    # --- Copy the git-tracked files only (never touch node_modules / *.db) -----
    $trackedFiles = @("index.js", "package.json", "README.md")
    foreach ($name in $trackedFiles) {
        $src = Join-Path $SourceDir $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $DestDir $name) -Force
        }
    }
    Write-Host "SyncMcpServer: synced index.js/package.json/README.md -> $DestDir"

    # --- Conditional npm install (production deps only), non-fatal -------------
    if ($needsInstall) {
        $npm = Get-Command npm -ErrorAction SilentlyContinue
        if ($null -eq $npm) {
            Write-Host "SyncMcpServer: WARNING -- npm not on PATH; skipped install. MCP server deps may be stale in $DestDir." -ForegroundColor Yellow
        }
        else {
            Write-Host "SyncMcpServer: running npm install (omit dev) in $DestDir ..."
            Push-Location $DestDir
            try {
                & npm install --omit=dev --no-audit --no-fund 2>&1 | ForEach-Object { Write-Host "  npm> $_" }
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "SyncMcpServer: WARNING -- npm install exited $LASTEXITCODE; deps may be incomplete in $DestDir." -ForegroundColor Yellow
                }
            }
            finally { Pop-Location }
        }
    }

    Write-Host "SyncMcpServer: done ($SourceDir -> $DestDir)."
}
catch {
    Write-Host "SyncMcpServer: non-fatal error: $_" -ForegroundColor Yellow
    exit 0
}
