# build-installer.ps1
# Publishes MultiTerminal + MCP Gateway, then compiles the Inno Setup installer.
#
# Usage:
#   .\build-installer.ps1                 # Full build (publish + installer)
#   .\build-installer.ps1 -SkipPublish    # Installer only (reuse last publish)
#   .\build-installer.ps1 -Verbose        # Show detailed build output
#   .\build-installer.ps1 -PluginRef v3.0.1            # Bundle the plugin from a tag/branch/sha (default: main)
#   .\build-installer.ps1 -PluginSource <local clone>  # Offline: bundle a clean local marketplace clone's HEAD
#   .\build-installer.ps1 -StageOnly      # Stage, validate and stamp the plugin only, then exit
#   .\build-installer.ps1 -CheckOnly [-PublishDir <folder>]  # Only check that the .iss ships everything
#                                         # in a publish folder (default: the normal publish output)
#
# The Claude Code plugin marketplace is staged from GitHub (ClarionLive/multiterminal-marketplace),
# never from this machine's live plugin folder: 3.0.0 shipped a local branch that was 20 commits
# ahead of GitHub and missing a same-day fix (task 4dc459e4).

param(
    [switch]$SkipPublish,
    [switch]$Verbose,
    [string]$PluginRef = 'main',
    [string]$PluginSource,
    [switch]$StageOnly,
    [switch]$CheckOnly,
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

# --- Paths ---
$RepoRoot       = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # parent of the MultiTerminal checkout
$MultiTermDir   = Split-Path -Parent $PSScriptRoot                       # this checkout, whatever its folder name
# The MCP Gateway is a SEPARATE source tree (not part of this repo). By default it
# is expected as a sibling folder named McpGateway; on machines that don't have the
# gateway source, point MCPGATEWAY_DIR at wherever the checkout lives.
$GatewayDir     = if ($env:MCPGATEWAY_DIR) { $env:MCPGATEWAY_DIR } else { Join-Path $RepoRoot 'McpGateway' }
$InstallerDir   = Join-Path $MultiTermDir 'installer'
$IssFile        = Join-Path $InstallerDir 'MultiTerminal.iss'

$MTPublishDir   = if ($PublishDir) { $PublishDir } else { Join-Path $MultiTermDir 'bin\Release\net10.0-windows\win-x64\publish' }
# Publish the Gateway to a dedicated installer-staging dir, NOT bin\publish\win-x64 —
# live McpGateway.exe processes (one per running MT terminal) are launched from that
# dir and hold locks on its DLLs, so publishing there fails with MSB3027 whenever MT
# is running. The .iss McpGatewayPublishDir points here too (task df1f521f).
$GWPublishDir   = Join-Path $GatewayDir   'bin\publish-installer\win-x64'

# --- Find Inno Setup ---
$InnoCompiler = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# --- Publish coverage guard (task 9ad3a95c) ---
# The .iss lists publish files and folders one by one, so a folder the build adds is silently
# left out of every install: 3.0.0 shipped without AttentionPanel\ (empty Attention panel) and
# scripts\ (no statusline.js, so the header context read "--%"). deploy.ps1 copies the whole
# build, which is why no dev machine ever saw it. This check fails the installer build when a
# top-level file or folder of the publish output is neither matched by a {#PublishDir} Source
# line nor listed below with the reason it is left out.
$PublishExclusions = [ordered]@{
    'docs'    = 'shipped from the source tree (DocsDir) as the optional "docs" component; same files'
    '*.pdb'   = 'debug symbols, not needed at run time'
    '*.lib'   = 'onnxruntime import libraries, used only when linking native code'
}

# Returns the top-level entries of $Dir that the .iss does not ship and $PublishExclusions does
# not exclude, as "folder\" or "file" strings. Runtime files count as shipped: their Source
# lines exist (Check: ShouldInstallRuntimeFiles decides per machine whether they are copied).
function Get-UnshippedPublishEntries([string]$Dir, [string]$Iss) {
    $fileRules = @()
    $dirRules = @{}
    foreach ($line in Get-Content -LiteralPath $Iss) {
        if ($line -notmatch '^\s*Source:\s*"\{#PublishDir\}\\([^"]+)"') { continue }
        $rel = $Matches[1]
        if ($rel.Contains('\')) {
            $dirRules[$rel.Split('\')[0].ToLowerInvariant()] = $true
            continue
        }
        $excludes = @()
        if ($line -match 'Excludes:\s*"([^"]+)"') { $excludes = $Matches[1].Split(',') | ForEach-Object { $_.Trim() } }
        $fileRules += [pscustomobject]@{ Pattern = $rel; Excludes = $excludes }
    }

    $unshipped = @()
    foreach ($entry in Get-ChildItem -LiteralPath $Dir -Force) {
        $name = $entry.Name
        if (@($PublishExclusions.Keys | Where-Object { $name -like $_ }).Count -gt 0) { continue }
        if ($entry.PSIsContainer) {
            if (-not $dirRules.ContainsKey($name.ToLowerInvariant())) { $unshipped += "$name\" }
            continue
        }
        $shipped = $fileRules | Where-Object {
            $name -like $_.Pattern -and -not (@($_.Excludes | Where-Object { $name -like $_ }).Count -gt 0)
        }
        if (-not $shipped) { $unshipped += $name }
    }
    return $unshipped
}

function Assert-PublishCoverage([string]$Dir, [string]$Iss) {
    if (-not (Test-Path -LiteralPath $Dir)) { throw "Publish output not found at $Dir" }
    $missing = @(Get-UnshippedPublishEntries -Dir $Dir -Iss $Iss)
    if ($missing.Count -gt 0) {
        throw ("The installer would leave out $($missing.Count) publish entr$(if ($missing.Count -eq 1) { 'y' } else { 'ies' }): " +
               ($missing -join ', ') + ". Add a {#PublishDir} Source line to MultiTerminal.iss, or add the " +
               "entry to `$PublishExclusions in build-installer.ps1 with the reason it is not needed.")
    }
    Write-Ok "Publish coverage OK: every top-level entry of $Dir is shipped or excluded with a reason"
}

if ($CheckOnly) {
    Write-Step "Checking publish coverage (-CheckOnly)..."
    Assert-PublishCoverage -Dir $MTPublishDir -Iss $IssFile
    exit 0
}

# --- Plugin marketplace staging (task 4dc459e4) ---
$PluginRepo       = 'ClarionLive/multiterminal-marketplace'
$PluginRepoUrl    = "https://github.com/$PluginRepo"
$PluginStagingDir = Join-Path $InstallerDir 'Output\plugin-staging'
$PluginStampName  = '.mt-plugin-source.json'

# Runs git and returns its combined output as strings. Windows PowerShell 5.1 turns native
# stderr into a terminating error under 'Stop', and git writes progress there, so the
# preference is relaxed for the call and the exit code decides instead.
function Invoke-Git {
    param([string[]]$GitArgs, [switch]$AllowFail)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -c core.longpaths=true @GitArgs 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    $script:GitExitCode = $code
    if ($code -ne 0 -and -not $AllowFail) {
        throw "git $($GitArgs -join ' ') failed (exit code $code):`n$($out -join "`n")"
    }
    return $out
}

# Deletes a folder tree. rd handles the \\?\ prefix, so a vendored node_modules path longer
# than MAX_PATH (and git's read-only pack files) cannot leave half a tree behind.
function Remove-Tree($path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    & cmd /c rd /s /q "\\?\$path"
    if (Test-Path -LiteralPath $path) { throw "Could not delete $path" }
}

# Stages the plugin marketplace into $PluginStagingDir at an exact commit, validates it,
# removes .git and writes the stamp file MT reads to know which commit is installed.
# Returns @{ Ref; Commit; Dir }.
function Invoke-PluginStaging {
    param([string]$Ref, [string]$Source)

    Write-Step "Staging plugin marketplace..."
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git is required to stage the plugin marketplace" }

    if ($Source) {
        # Offline build from a local clone: it must be clean, a clone of the real repo, and its
        # HEAD must be on an origin branch, so nothing unpushed can ship.
        if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) { throw "-PluginSource '$Source' is not a git checkout" }
        $Source = (Resolve-Path -LiteralPath $Source).Path
        $originUrl = (Invoke-Git @('-C', $Source, 'remote', 'get-url', 'origin') -AllowFail) -join ''
        if ($originUrl -notmatch '[/:]ClarionLive/multiterminal-marketplace(\.git)?/?$') {
            throw "-PluginSource '$Source': origin is '$originUrl', not $PluginRepoUrl"
        }
        $dirty = @(Invoke-Git @('-C', $Source, 'status', '--porcelain'))
        if (@($dirty | Where-Object { $_ }).Count -gt 0) {
            throw "-PluginSource '$Source' has uncommitted changes; commit and push them, or bundle from GitHub:`n$($dirty -join "`n")"
        }
        $commit = ((Invoke-Git @('-C', $Source, 'rev-parse', 'HEAD')) -join '').Trim()
        # Remote-tracking refs are as fresh as the clone's last fetch; a commit found there was on origin then.
        $onOrigin = @(Invoke-Git @('-C', $Source, 'branch', '-r', '--contains', $commit) |
            ForEach-Object { $_.Trim() } | Where-Object { $_ -like 'origin/*' -and $_ -notlike 'origin/HEAD*' })
        if ($onOrigin.Count -eq 0) {
            throw "-PluginSource '$Source': HEAD $commit is on no origin branch (unpushed?). Push it, or bundle from GitHub."
        }
        $Ref = if ($onOrigin -contains "origin/$Ref") { $Ref } else { $onOrigin[0] -replace '^origin/', '' }
        $fetchFrom = $Source
        $fetchRef  = $commit
    } else {
        $fetchFrom = "$PluginRepoUrl.git"
        $fetchRef  = $Ref
    }

    Remove-Tree $PluginStagingDir
    New-Item -ItemType Directory -Path $PluginStagingDir -Force | Out-Null

    # init + fetch (rather than clone --branch) also accepts a bare commit sha as the ref.
    Invoke-Git @('-C', $PluginStagingDir, 'init', '-q') | Out-Null
    $fetch = Invoke-Git @('-C', $PluginStagingDir, 'fetch', '-q', '--depth', '1', '--no-tags', $fetchFrom, $fetchRef) -AllowFail
    if ($script:GitExitCode -ne 0) {
        throw "Plugin ref '$fetchRef' does not resolve on $fetchFrom (no such branch, tag or commit):`n$($fetch -join "`n")"
    }
    Invoke-Git @('-C', $PluginStagingDir, 'checkout', '-q', '--detach', 'FETCH_HEAD') | Out-Null
    $staged = ((Invoke-Git @('-C', $PluginStagingDir, 'rev-parse', 'HEAD')) -join '').Trim()
    if ($staged -notmatch '^[0-9a-f]{40}$') { throw "Could not resolve the staged plugin commit (got '$staged')" }
    if ($Source -and $staged -ne $commit) { throw "Staged $staged but -PluginSource HEAD is $commit" }

    Assert-PluginStaging $PluginStagingDir

    Remove-Tree (Join-Path $PluginStagingDir '.git')
    if (Test-Path -LiteralPath (Join-Path $PluginStagingDir '.git')) { throw ".git is still present in $PluginStagingDir" }

    # The stamp's shape is a contract with MT's plugin updater (PluginUpdateService): keep the
    # field names and order. UTF-8 without BOM, which Windows PowerShell's Set-Content cannot write.
    $stamp = [ordered]@{
        repo      = $PluginRepo
        ref       = $Ref
        commit    = $staged
        stampedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        stampedBy = 'installer'
    }
    [System.IO.File]::WriteAllText((Join-Path $PluginStagingDir $PluginStampName),
        ($stamp | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

    Write-Ok "Plugin: $PluginRepo @ $Ref = $staged"
    Write-Ok "Staged to $PluginStagingDir"
    return @{ Ref = $Ref; Commit = $staged; Dir = $PluginStagingDir }
}

# Refuses a staged tree that is not the marketplace: the installer would otherwise ship
# whatever the ref happened to contain.
function Assert-PluginStaging($dir) {
    foreach ($rel in @('.claude-plugin\marketplace.json', 'plugins\multiterminal\.claude-plugin\plugin.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $rel))) {
            throw "Staged plugin marketplace is missing $rel ($dir). Wrong ref or wrong repository?"
        }
    }
}

$plugin = Invoke-PluginStaging -Ref $PluginRef -Source $PluginSource
if ($StageOnly) {
    Write-Step "Plugin staged (-StageOnly): skipping publish and installer"
    exit 0
}

# --- Publish MultiTerminal ---
if (-not $SkipPublish) {
    Write-Step "Publishing MultiTerminal..."
    $mtArgs = @(
        'publish'
        '-c', 'Release'
        '-r', 'win-x64'
        '--self-contained', 'true'
        '-o', $MTPublishDir
    )
    if (-not $Verbose) { $mtArgs += '-v'; $mtArgs += 'quiet' }

    Push-Location $MultiTermDir
    try {
        & dotnet @mtArgs
        if ($LASTEXITCODE -ne 0) { throw "MultiTerminal publish failed (exit code $LASTEXITCODE)" }
        Write-Ok "Published to $MTPublishDir"
    } finally { Pop-Location }

    # --- Publish MCP Gateway ---
    Write-Step "Publishing MCP Gateway..."
    if (-not (Test-Path $GatewayDir)) {
        throw ("MCP Gateway source not found at '$GatewayDir'. The gateway is a separate " +
               "source tree that is not part of this repo. Set the MCPGATEWAY_DIR env var " +
               "to its checkout location, or clone it as a sibling folder named 'McpGateway'.")
    }
    $gwArgs = @(
        'publish'
        '-c', 'Release'
        '-r', 'win-x64'
        '--no-self-contained'
        '-o', $GWPublishDir
    )
    if (-not $Verbose) { $gwArgs += '-v'; $gwArgs += 'quiet' }

    Push-Location $GatewayDir
    try {
        & dotnet @gwArgs
        if ($LASTEXITCODE -ne 0) { throw "MCP Gateway publish failed (exit code $LASTEXITCODE)" }
        Write-Ok "Published to $GWPublishDir"
    } finally { Pop-Location }
} else {
    Write-Step "Skipping publish (reusing existing output)"
    if (-not (Test-Path $MTPublishDir)) { throw "MultiTerminal publish output not found at $MTPublishDir" }
    if (-not (Test-Path $GWPublishDir)) { throw "MCP Gateway publish output not found at $GWPublishDir" }
    Write-Ok "Using existing publish output"
}

# --- Validate publish output ---
Write-Step "Validating publish output..."
$mtExe = Join-Path $MTPublishDir 'MultiTerminal.exe'
$gwExe = Join-Path $GWPublishDir 'McpGateway.exe'

if (-not (Test-Path $mtExe)) { throw "Missing: $mtExe" }
if (-not (Test-Path $gwExe)) { throw "Missing: $gwExe" }

$mtSize = [math]::Round((Get-Item $mtExe).Length / 1MB, 1)
$gwSize = [math]::Round((Get-Item $gwExe).Length / 1MB, 1)
$mtCount = (Get-ChildItem $MTPublishDir -Recurse -File).Count
$gwCount = (Get-ChildItem $GWPublishDir -Recurse -File).Count

Write-Ok "MultiTerminal: $mtCount files, exe=$mtSize MB"
Write-Ok "MCP Gateway:   $gwCount files, exe=$gwSize MB"

Write-Step "Checking publish coverage..."
Assert-PublishCoverage -Dir $MTPublishDir -Iss $IssFile

# --- Compile installer ---
Write-Step "Compiling Inno Setup installer..."
if (-not $InnoCompiler) {
    Write-Warn "Inno Setup 6 not found. Publish complete - run ISCC.exe manually:"
    Write-Host "    ISCC.exe /DPluginMarketplaceDir=`"$($plugin.Dir)`" `"$IssFile`""
    exit 0
}

Push-Location $InstallerDir
try {
    # The define overrides the .iss default (the build machine's live plugin folder).
    & $InnoCompiler "/DPluginMarketplaceDir=$($plugin.Dir)" $IssFile
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)" }
} finally { Pop-Location }

# --- Done ---
$outputDir = Join-Path $InstallerDir 'Output'
$installer = Get-ChildItem $outputDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Step "Build complete!"
if ($installer) {
    $instSize = [math]::Round($installer.Length / 1MB, 1)
    Write-Ok "Installer: $($installer.FullName) ($instSize MB)"
}
Write-Ok "Plugin:    $PluginRepo @ $($plugin.Ref) = $($plugin.Commit)"
