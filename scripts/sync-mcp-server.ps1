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
    [string]$DestDir,
    # When set (or SMS_FAIL_ON_ERROR=true), a copy / verify / npm failure exits
    # non-zero so the caller (the Release StageMcpForInstaller target) fails the
    # build rather than silently shipping stale bytes. Off by default so the
    # Debug live-APPDATA sync stays non-fatal — but even non-fatal failures now
    # emit a loud, greppable warning (ticket ec97c446 H2: non-fatal != silent).
    [switch]$FailOnError
)

$ErrorActionPreference = "Continue"

if (-not $FailOnError -and $env:SMS_FAIL_ON_ERROR -eq "true") { $FailOnError = $true }

# --- CI skip (ticket 9fec5c5f) ---------------------------------------------
# On a CI runner (GitHub Actions sets CI=true) the AfterTargets=Build dev-live
# sync has no purpose: there is no live MultiTerminal install under %APPDATA% to
# keep fresh, and running `npm ci` into a throwaway %APPDATA% copy just burns
# runner minutes and floods every build log. Skip it -- but ONLY the non-fatal
# dev sync. The fatal installer-staging path (-FailOnError / SMS_FAIL_ON_ERROR,
# used by the Release StageMcpForInstaller target) must still run everywhere so
# a Release build on CI never ships an unstaged/stale server.
if (-not $FailOnError -and $env:CI -eq "true") {
    Write-Host "SyncMcpServer: skipped on CI (no live %APPDATA% install to sync; installer staging path is unaffected)."
    exit 0
}

# Accumulates across copy / verify / npm so a single end-of-run decision can fail
# the build under -FailOnError while a normal Debug sync only warns.
$script:hadError = $false

function Report-SyncFailure($message) {
    # One loud, greppable line so a stale deployment is never silent. "MCPSYNC
    # WARNING" is the grep anchor callers/CI can key on.
    Write-Host "MCPSYNC WARNING: $message" -ForegroundColor Red
    $script:hadError = $true
}

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

    # --- Decide whether a dependency (re)install is needed (BEFORE the copy) -----
    # Reinstall when: node_modules is missing, OR the lockfile is missing/changed
    # at the dest, OR (no-lock fallback) package.json changed. The lockfile check
    # is load-bearing: an EXISTING dest from the old pipeline can have
    # node_modules + an unchanged package.json yet deps that don't match the
    # committed lock (produced by an old floating `npm install`). Without keying
    # on the lock, npm ci would never run there and the installer would keep
    # shipping the non-reproducible deps this fix is meant to close
    # (ticket ec97c446 F2 re-review). All comparisons normalize CRLF/LF + trim so
    # a pure line-ending difference never triggers a spurious reinstall.
    $sourceLock = Join-Path $SourceDir "package-lock.json"
    $destLock   = Join-Path $DestDir "package-lock.json"
    $needsInstall = $false
    if (-not (Test-Path -LiteralPath $destModules)) {
        $needsInstall = $true
        Write-Host "SyncMcpServer: dest node_modules missing -- will install."
    }
    elseif ((Test-Path -LiteralPath $sourceLock) -and -not (Test-Path -LiteralPath $destLock)) {
        # Source ships a lock but the dest has none => existing deps were produced
        # without it (old floating install). Force a reproducible reinstall.
        $needsInstall = $true
        Write-Host "SyncMcpServer: dest lockfile missing (source has one) -- will install for reproducibility."
    }
    elseif ((Test-Path -LiteralPath $sourceLock) -and (Test-Path -LiteralPath $destLock)) {
        $srcLockTxt  = ([System.IO.File]::ReadAllText($sourceLock)).Replace("`r`n", "`n").Trim()
        $destLockTxt = ([System.IO.File]::ReadAllText($destLock)).Replace("`r`n", "`n").Trim()
        if ($srcLockTxt -ne $destLockTxt) {
            $needsInstall = $true
            Write-Host "SyncMcpServer: package-lock.json changed -- will install."
        }
    }
    if (-not $needsInstall -and (Test-Path -LiteralPath $sourcePkg) -and (Test-Path -LiteralPath $destPkg)) {
        # No-lock fallback: compare package.json content when there is no lockfile
        # to key on (so the sync still detects a dep change in a lock-less setup).
        $srcText  = ([System.IO.File]::ReadAllText($sourcePkg)).Replace("`r`n", "`n").Trim()
        $destText = ([System.IO.File]::ReadAllText($destPkg)).Replace("`r`n", "`n").Trim()
        if ($srcText -ne $destText) {
            $needsInstall = $true
            Write-Host "SyncMcpServer: package.json changed -- will install."
        }
    }
    # Installer/Release staging (-FailOnError) has NO runtime backstop, so always
    # reinstall from the committed lock -- this guarantees the shipped deps are
    # exactly the lockfile even if a dest's node_modules drifted from a matching
    # lock (ticket ec97c446 F2, belt-and-braces). Cheap: installers build rarely.
    if ($FailOnError -and -not $needsInstall) {
        $needsInstall = $true
        Write-Host "SyncMcpServer: installer staging (FailOnError) -- forcing reproducible reinstall from lock."
    }

    # --- Copy the git-tracked files only (never touch node_modules / *.db) -----
    # package-lock.json is included so `npm ci` can run reproducibly at the dest.
    $trackedFiles = @("index.js", "package.json", "package-lock.json", "README.md")
    foreach ($name in $trackedFiles) {
        $src = Join-Path $SourceDir $name
        if (Test-Path -LiteralPath $src) {
            try {
                Copy-Item -LiteralPath $src -Destination (Join-Path $DestDir $name) -Force -ErrorAction Stop
            }
            catch {
                Report-SyncFailure "APPDATA/dest copy of $name failed ($DestDir) -- deployed server is now STALE: $_"
            }
        }
    }
    if (-not $script:hadError) {
        Write-Host "SyncMcpServer: synced $($trackedFiles -join '/') -> $DestDir"
    }

    # --- Verify the copy actually landed: dest bytes must match source ----------
    # Catches a silent copy failure (dest read-only, disk full, AV lock) that a
    # non-throwing Copy-Item could otherwise mask. Content compare (normalized)
    # not Get-FileHash, for the same PS 5.1-host reason as the package.json check.
    foreach ($name in @("index.js", "package.json", "package-lock.json")) {
        $s = Join-Path $SourceDir $name
        $d = Join-Path $DestDir $name
        if (Test-Path -LiteralPath $s) {
            if (-not (Test-Path -LiteralPath $d)) {
                Report-SyncFailure "post-copy verify: $name missing at dest ($DestDir) -- deployed server is STALE."
            }
            else {
                $sTxt = ([System.IO.File]::ReadAllText($s)).Replace("`r`n", "`n")
                $dTxt = ([System.IO.File]::ReadAllText($d)).Replace("`r`n", "`n")
                if ($sTxt -ne $dTxt) {
                    Report-SyncFailure "post-copy verify: $name at dest does not match source ($DestDir) -- deployed server is STALE."
                }
            }
        }
    }

    # --- Conditional dependency install (production only) ----------------------
    # Prefer `npm ci` (reproducible, installs exactly the committed lockfile) when
    # a package-lock.json is present at the dest; fall back to `npm install` only
    # if there's no lock. --ignore-scripts blocks dependency lifecycle scripts so
    # a malicious/compromised transitive package can't get build-time code
    # execution on the build machine (ticket ec97c446 supply-chain hardening;
    # verified @modelcontextprotocol/sdk has no install/postinstall scripts). The
    # broad supply-chain audit is tracked in 3391b886.
    if ($needsInstall) {
        $npm = Get-Command npm -ErrorAction SilentlyContinue
        if ($null -eq $npm) {
            Report-SyncFailure "npm not on PATH; skipped dependency install. MCP server deps are STALE/missing in $DestDir."
        }
        else {
            $destLock = Join-Path $DestDir "package-lock.json"
            $useCi = Test-Path -LiteralPath $destLock
            $npmArgs = if ($useCi) {
                @("ci", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund")
            } else {
                @("install", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund")
            }
            Write-Host "SyncMcpServer: running npm $($npmArgs -join ' ') in $DestDir ..."
            Push-Location $DestDir
            try {
                & npm @npmArgs 2>&1 | ForEach-Object { Write-Host "  npm> $_" }
                if ($LASTEXITCODE -ne 0) {
                    Report-SyncFailure "npm $($npmArgs[0]) exited $LASTEXITCODE; deps are incomplete in $DestDir."
                }
            }
            finally { Pop-Location }
        }
    }

    if ($script:hadError) {
        if ($FailOnError) {
            Write-Host "SyncMcpServer: FAILED ($SourceDir -> $DestDir) -- see MCPSYNC WARNING line(s) above." -ForegroundColor Red
            exit 1
        }
        Write-Host "SyncMcpServer: completed WITH WARNINGS ($SourceDir -> $DestDir) -- deployed copy may be stale (see MCPSYNC WARNING above)." -ForegroundColor Yellow
    }
    else {
        Write-Host "SyncMcpServer: done ($SourceDir -> $DestDir)."
    }
}
catch {
    # Unexpected exception. Under -FailOnError this must fail the build; otherwise
    # stay non-fatal but LOUD (never silent -- ticket ec97c446 H2).
    Report-SyncFailure "unexpected error syncing to $DestDir : $_"
    if ($FailOnError) { exit 1 }
    exit 0
}
