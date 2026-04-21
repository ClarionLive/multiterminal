# build-installer.ps1
# Publishes MultiTerminal + MCP Gateway, then compiles the Inno Setup installer.
#
# Usage:
#   .\build-installer.ps1                 # Full build (publish + installer)
#   .\build-installer.ps1 -SkipPublish    # Installer only (reuse last publish)
#   .\build-installer.ps1 -Verbose        # Show detailed build output

param(
    [switch]$SkipPublish,
    [switch]$Verbose
)

$ErrorActionPreference = 'Stop'

# --- Paths ---
$RepoRoot       = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # ClarionPowerShell
$MultiTermDir   = Join-Path $RepoRoot 'MultiTerminal'
$GatewayDir     = Join-Path $RepoRoot 'McpGateway'
$InstallerDir   = Join-Path $MultiTermDir 'installer'
$IssFile        = Join-Path $InstallerDir 'MultiTerminal.iss'

$MTPublishDir   = Join-Path $MultiTermDir 'bin\Release\net8.0-windows\win-x64\publish'
$GWPublishDir   = Join-Path $GatewayDir   'bin\publish\win-x64'

# --- Find Inno Setup ---
$InnoCompiler = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

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

# --- Compile installer ---
Write-Step "Compiling Inno Setup installer..."
if (-not $InnoCompiler) {
    Write-Warn "Inno Setup 6 not found. Publish complete - run ISCC.exe manually:"
    Write-Host "    ISCC.exe `"$IssFile`""
    exit 0
}

Push-Location $InstallerDir
try {
    & $InnoCompiler $IssFile
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
