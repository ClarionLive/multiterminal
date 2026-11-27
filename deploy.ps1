# Deploy script for MultiTerminal
#
# Default flow: copy from the SHARED staged folder to the Deploy folder.
# Whoever built last wins.
#
# Machine-specific paths come from env vars so the same script works on every
# dev machine (the H:\DevLaptop\... defaults match John's laptop layout):
#   MULTITERMINAL_STAGED_PATH  -- staged build source (default H:\DevLaptop\ClarionPowerShell\staged)
#   MULTITERMINAL_DEPLOY_PATH  -- deploy destination  (default H:\DevLaptop\ClarionPowerShell\Deploy)
# Set them once per machine, e.g.:
#   setx MULTITERMINAL_STAGED_PATH "D:\CarpioAI\GitHub\multiterminal-staged"
#   setx MULTITERMINAL_DEPLOY_PATH "C:\Program Files\MultiTerminal"
# A Program Files destination requires running this script from an elevated shell.
# Use -Build to also Release-build from the current folder first (the old
# behaviour, useful when running from a checked-out branch you want to deploy).
#
# The csproj's CopyToStaged target mirrors Release output into the shared
# staged folder automatically after every build, and WriteBuildInfo stamps it
# with branch / commit / agent / timestamp so this script can tell you exactly
# what you're about to push to Deploy.
param(
    [switch]$Build = $false,        # Build Release here first, then deploy (old behaviour)
    [switch]$IncludePdb = $false,   # Include debug symbols (.pdb files)
    [switch]$Force = $false,        # Bypass staleness guard / missing-stamp guard
    [switch]$SkipBuild = $false,    # Kept for backward compatibility -- now a no-op (skip is default)
    [switch]$SkipMcpSync = $false,  # Deploy the binaries only; leave the live MCP server alone
    [switch]$ForceMcp = $false      # Install the staged MCP server even when it looks like a downgrade
)

$source = if ($env:MULTITERMINAL_STAGED_PATH) { $env:MULTITERMINAL_STAGED_PATH } else { "H:\DevLaptop\ClarionPowerShell\staged" }
$dest = if ($env:MULTITERMINAL_DEPLOY_PATH) { $env:MULTITERMINAL_DEPLOY_PATH } else { "H:\DevLaptop\ClarionPowerShell\Deploy" }

Write-Host "=== MultiTerminal Deployment ===" -ForegroundColor Cyan
Write-Host "Source: $source  (shared staged)"
Write-Host "Destination: $dest"
if ($IncludePdb) {
    Write-Host "Mode: Including debug symbols (.pdb)" -ForegroundColor Yellow
} else {
    Write-Host "Mode: Excluding debug symbols (.pdb)" -ForegroundColor Green
}
Write-Host ""

# Optional in-place build (opt-in). Without -Build we trust whatever was built
# elsewhere into the shared staged folder.
if ($Build) {
    Write-Host "Building Release in current folder (Rebuild)..." -ForegroundColor Cyan
    dotnet build MultiTerminal.csproj -c Release -t:Rebuild
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
    Write-Host ""
}

if ($SkipBuild) {
    Write-Host "(Note: -SkipBuild is now the default; flag has no effect.)" -ForegroundColor DarkGray
}

# Validate staged folder
if (-not (Test-Path $source)) {
    Write-Host "ERROR: Shared staged folder not found: $source" -ForegroundColor Red
    Write-Host "Build the project from any worktree first (or set MULTITERMINAL_STAGED_PATH)." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path "$source\MultiTerminal.exe")) {
    Write-Host "ERROR: MultiTerminal.exe not found in staged folder!" -ForegroundColor Red
    Write-Host "Build the project from any worktree first." -ForegroundColor Yellow
    exit 1
}

# Read + display build stamp so it's obvious what's about to ship
$stampPath = Join-Path $source ".build-info.json"
$stamp = $null
if (Test-Path $stampPath) {
    try {
        $stamp = Get-Content $stampPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Host "Build stamp:" -ForegroundColor Cyan
        Write-Host "  Agent:    $($stamp.agentName)"
        Write-Host "  Branch:   $($stamp.branch)"
        Write-Host "  Commit:   $($stamp.commit)"
        Write-Host "  Config:   $($stamp.configuration)"
        Write-Host "  Built at: $($stamp.timestamp)"
        Write-Host "  Source:   $($stamp.sourceDir)"
        Write-Host ""

        # NOTE: Release-config guard was dropped — agent build_project defaults to Debug
        # and this is a run-what-you-build local setup, so the guard caused friction
        # without value. Use `-Build` to explicitly Release-rebuild before deploy.
        if ($stamp.configuration -ne 'Release') {
            Write-Host "Note: deploying $($stamp.configuration) build (use -Build to Release-rebuild first)." -ForegroundColor DarkGray
        }

        # Staleness guard: warn if staged is >24h old, require -Force to proceed
        try {
            $builtAt = [DateTime]::Parse($stamp.timestamp)
            $ageHours = ((Get-Date) - $builtAt).TotalHours
            if ($ageHours -gt 24 -and -not $Force) {
                Write-Host "STALENESS WARNING: Staged build is $([math]::Round($ageHours, 1)) hours old." -ForegroundColor Yellow
                Write-Host "Re-run with -Force to deploy anyway, or rebuild fresh." -ForegroundColor Yellow
                exit 1
            }
        } catch {
            Write-Host "WARNING: could not parse stamp timestamp: $($stamp.timestamp)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "WARNING: failed to parse .build-info.json: $_" -ForegroundColor Yellow
        if (-not $Force) {
            Write-Host "Re-run with -Force to deploy anyway." -ForegroundColor Yellow
            exit 1
        }
    }
} else {
    Write-Host "WARNING: No .build-info.json in staged folder -- can't identify what's about to deploy." -ForegroundColor Yellow
    if (-not $Force) {
        Write-Host "Re-run with -Force to deploy anyway, or rebuild so the stamp is written." -ForegroundColor Yellow
        exit 1
    }
}

# Refuse to deploy over a running MultiTerminal.exe -- locked files would silently
# fail to overwrite (Remove-Item / Copy-Item swallow the error), leaving a
# mismatched mix of old .exe + new DLLs that crashes at next launch.
$mtRunning = Get-Process -Name MultiTerminal -ErrorAction SilentlyContinue
if ($mtRunning) {
    $pids = ($mtRunning | ForEach-Object { $_.Id }) -join ", "
    Write-Host "ERROR: MultiTerminal.exe is running (PID $pids). Exit it first, then re-run deploy." -ForegroundColor Red
    exit 1
}

# Verify the destination is writable BEFORE wiping anything -- an unelevated run
# against a Program Files install would otherwise fail the wipe with swallowed
# access-denied errors and leave a mismatched mix of old and new files.
if (Test-Path $dest) {
    $probe = Join-Path $dest ".deploy-write-probe"
    try {
        [System.IO.File]::WriteAllText($probe, "probe")
        Remove-Item $probe -Force
    } catch {
        Write-Host "ERROR: Destination is not writable: $dest" -ForegroundColor Red
        Write-Host "If it is under Program Files, re-run this script from an elevated shell." -ForegroundColor Yellow
        exit 1
    }
}

# MCP server preflight (ticket 9ce606e0). The build stages the MCP server that
# matches these binaries into staged\mcp-server; deploy installs it into the
# live copy every agent's MCP server runs from. Checked HERE, before the wipe,
# so a refusal leaves Deploy and the live server both untouched.
#
# The check and the install both read a private snapshot, never the shared
# staged folder itself: any worktree build may rewrite staged while this script
# runs, and the server installed must be the one that passed the check. The
# preflight also checks the snapshot against the payload hash in its stamp, so a
# copy taken mid-rewrite is refused rather than installed.
$mcpStaged = Join-Path $source "mcp-server"
$mcpSnapshot = ""
function Remove-McpSnapshot {
    if ($script:mcpSnapshot -and (Test-Path -LiteralPath $script:mcpSnapshot)) {
        Remove-Item -LiteralPath $script:mcpSnapshot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$mcpLive = if ($env:APPDATA) { Join-Path $env:APPDATA "multiterminal\mcp" } else { "" }
$mcpSync = $false
$mcpNote = ""
if ($SkipMcpSync) {
    $mcpNote = "skipped (-SkipMcpSync); the live MCP server was not changed"
    Write-Host "MCP: $mcpNote." -ForegroundColor Yellow
} elseif (-not $mcpLive) {
    $mcpNote = "skipped: APPDATA is not set"
    Write-Host "MCP: WARNING: $mcpNote." -ForegroundColor Yellow
} else {
    $mcpCheckDir = $mcpStaged
    if (Test-Path -LiteralPath $mcpStaged) {
        $mcpSnapshot = Join-Path ([System.IO.Path]::GetTempPath()) ("mt-deploy-mcp-" + [guid]::NewGuid().ToString("N"))
        try {
            Copy-Item -LiteralPath $mcpStaged -Destination $mcpSnapshot -Recurse -Force -ErrorAction Stop
        } catch {
            Remove-McpSnapshot
            Write-Host "ERROR: could not snapshot the staged MCP server ($mcpStaged): $_" -ForegroundColor Red
            Write-Host "Nothing has been changed. Re-run, or use -SkipMcpSync to deploy the binaries only." -ForegroundColor Yellow
            exit 1
        }
        $mcpCheckDir = $mcpSnapshot
    }
    $checkArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "scripts\check-mcp-deploy.ps1"),
                   "-StagedDir", $mcpCheckDir, "-LiveDir", $mcpLive, "-RepoDir", $PSScriptRoot)
    if ($ForceMcp) { $checkArgs += "-Force" }
    & powershell @checkArgs
    switch ($LASTEXITCODE) {
        0 { $mcpSync = $true }
        1 { Remove-McpSnapshot; exit 1 }
        2 { $mcpNote = "already current" }
        3 { $mcpNote = "NOT updated: the staged build has no MCP server" }
        default {
            Remove-McpSnapshot
            Write-Host "ERROR: the MCP preflight failed (exit $LASTEXITCODE). Nothing has been changed. Re-run with -SkipMcpSync to deploy the binaries only." -ForegroundColor Red
            exit 1
        }
    }
}
Write-Host ""

# Remove old deployment (preserve .claude folder + the gitignored local config
# override, plus installer-managed components that are NOT part of the staged
# build output: mcp-gateway\, mcps\, docs\, tools\ and the uninstaller. Wiping
# those on an installer-based machine would delete pieces this script cannot
# put back). appsettings.Local.json holds the phone-gateway secrets (auth creds,
# NotificationSecret, PermissionRelay ApiKey) and is NOT a build artifact, so it
# never exists in the staged folder. Wiping it here without re-copying would
# silently revert login to the committed changeme/changeme defaults and lock the
# gateway out (task ca6c5344 item [11]). Preserve it across the wipe.
if (Test-Path $dest) {
    Write-Host "Removing old deployment (preserving .claude, appsettings.Local.json, mcp-gateway, mcps, docs, tools, uninstaller)..." -ForegroundColor Yellow
    Get-ChildItem $dest -Exclude ".claude", "appsettings.Local.json", "mcp-gateway", "mcps", "docs", "tools", "unins*" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Copy new files. "mcp-server" is the staged MCP server, installed into APPDATA
# below rather than copied into Deploy. -Exclude matches that name at any depth,
# so no build output may use it for anything else.
Write-Host "Copying files..." -ForegroundColor Green
if ($IncludePdb) {
    Copy-Item -Path "$source\*" -Destination $dest -Recurse -Force -Exclude "mcp-server"
} else {
    Copy-Item -Path "$source\*" -Destination $dest -Recurse -Force -Exclude "*.pdb", "mcp-server"
}

# Install the staged MCP server (cleared by the preflight above). Running MCP
# servers keep the code they loaded; agents pick this one up when they restart.
$mcpFailed = $false
if ($mcpSync) {
    Write-Host "Syncing MCP server: $mcpStaged (checked snapshot) -> $mcpLive" -ForegroundColor Green
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "scripts\sync-mcp-server.ps1") -SourceDir $mcpSnapshot -DestDir $mcpLive -DeployMode
    if ($LASTEXITCODE -eq 0) {
        $mcpNote = "updated"
    } else {
        $mcpFailed = $true
        $mcpNote = "FAILED (exit $LASTEXITCODE); see the MCPSYNC WARNING lines above"
    }
}
Remove-McpSnapshot

# Sync .claude\agents -- prefer the source worktree's agents (matches the binaries
# we just copied); fall back to the current folder if no stamp is available.
$agentsSource = if ($stamp -and $stamp.sourceDir -and (Test-Path (Join-Path $stamp.sourceDir ".claude\agents"))) {
    Join-Path $stamp.sourceDir ".claude\agents"
} else {
    ".claude\agents"
}
$agentsDest = "$dest\.claude\agents"
if (Test-Path $agentsSource) {
    Write-Host "Syncing .claude\agents from: $agentsSource" -ForegroundColor Green
    if (-not (Test-Path $agentsDest)) {
        New-Item -ItemType Directory -Path $agentsDest -Force | Out-Null
    }
    Copy-Item -Path "$agentsSource\*" -Destination $agentsDest -Recurse -Force
    $agentCount = (Get-ChildItem $agentsDest -File).Count
    Write-Host "  Copied $agentCount agent(s) to deployment" -ForegroundColor Green
} else {
    Write-Host "(No .claude\agents folder found at $agentsSource -- skipping agents sync.)" -ForegroundColor DarkGray
}

# Verify deployment
Write-Host ""
Write-Host "=== Deployment Complete ===" -ForegroundColor Green
Write-Host ""

if (Test-Path "$dest\MultiTerminal.exe") {
    $exe = Get-Item "$dest\MultiTerminal.exe"
    Write-Host "MultiTerminal.exe:" -ForegroundColor Cyan
    Write-Host "  Size: $([math]::Round($exe.Length / 1MB, 2)) MB"
    Write-Host "  Last Modified: $($exe.LastWriteTime)"
    Write-Host ""

    $fileCount = (Get-ChildItem $dest -Recurse -File).Count
    Write-Host "Total files deployed: $fileCount" -ForegroundColor Cyan
    if ($stamp) {
        Write-Host "Deployed: $($stamp.agentName) / $($stamp.branch) / $($stamp.commit)" -ForegroundColor Cyan
    }
    Write-Host ""
    if ($mcpNote) {
        $mcpColor = if ($mcpFailed -or $mcpNote.StartsWith("NOT")) { "Red" } else { "Cyan" }
        Write-Host "MCP server: $mcpNote" -ForegroundColor $mcpColor
    }
    if ($mcpFailed) {
        Write-Host "The binaries are deployed but the MCP server is not. Fix the cause and re-run deploy." -ForegroundColor Red
        exit 1
    }
    Write-Host "You can now restart MultiTerminal from: $dest" -ForegroundColor Green
} else {
    Write-Host "ERROR: MultiTerminal.exe not found in deployment!" -ForegroundColor Red
    exit 1
}
