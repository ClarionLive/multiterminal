# Deploy script for MultiTerminal
# Builds Release, then copies from staged/ to the Deploy folder.
# The csproj CopyToStaged target mirrors Release output into staged/ after build.
param(
    [switch]$IncludePdb = $false,  # Include debug symbols (.pdb files)
    [switch]$SkipBuild = $false    # Skip the build step (deploy existing staged/ output)
)

$source = "staged"
$dest = "H:\DevLaptop\ClarionPowerShell\Deploy"

Write-Host "=== MultiTerminal Deployment ===" -ForegroundColor Cyan
Write-Host "Source: $source"
Write-Host "Destination: $dest"
if ($IncludePdb) {
    Write-Host "Mode: Including debug symbols (.pdb)" -ForegroundColor Yellow
} else {
    Write-Host "Mode: Excluding debug symbols (.pdb)" -ForegroundColor Green
}
Write-Host ""

# Build Release (incremental — fast if nothing changed).
# The csproj CopyToStaged AfterTargets=Build target auto-mirrors output into staged/.
if (-not $SkipBuild) {
    Write-Host "Building Release configuration (Rebuild)..." -ForegroundColor Cyan
    dotnet build MultiTerminal.csproj -c Release -t:Rebuild
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
    Write-Host ""
}

# Check if staged folder exists and has files
if (-not (Test-Path $source)) {
    Write-Host "ERROR: Staged folder not found: $source" -ForegroundColor Red
    Write-Host "Run without -SkipBuild so the build populates staged/." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path "$source\MultiTerminal.exe")) {
    Write-Host "ERROR: MultiTerminal.exe not found in staged folder!" -ForegroundColor Red
    Write-Host "Run without -SkipBuild so the build populates staged/." -ForegroundColor Yellow
    exit 1
}

# Remove old deployment (preserve .claude folder)
if (Test-Path $dest) {
    Write-Host "Removing old deployment (preserving .claude folder)..." -ForegroundColor Yellow
    Get-ChildItem $dest -Exclude ".claude" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Copy new files
Write-Host "Copying files..." -ForegroundColor Green
if ($IncludePdb) {
    Copy-Item -Path "$source\*" -Destination $dest -Recurse -Force
} else {
    Copy-Item -Path "$source\*" -Destination $dest -Recurse -Force -Exclude "*.pdb"
}

# Copy .claude/agents to deployment
$agentsSource = ".claude\agents"
$agentsDest = "$dest\.claude\agents"
if (Test-Path $agentsSource) {
    Write-Host "Syncing .claude\agents..." -ForegroundColor Green
    if (-not (Test-Path $agentsDest)) {
        New-Item -ItemType Directory -Path $agentsDest -Force | Out-Null
    }
    Copy-Item -Path "$agentsSource\*" -Destination $agentsDest -Recurse -Force
    $agentCount = (Get-ChildItem $agentsDest -File).Count
    Write-Host "  Copied $agentCount agent(s) to deployment" -ForegroundColor Green
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

    # Count files
    $fileCount = (Get-ChildItem $dest -Recurse -File).Count
    Write-Host "Total files deployed: $fileCount" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "You can now restart MultiTerminal from: $dest" -ForegroundColor Green
} else {
    Write-Host "ERROR: MultiTerminal.exe not found in deployment!" -ForegroundColor Red
    exit 1
}
