#requires -Version 5.1
# Writes .build-info.json into the shared staged folder so deploy.ps1 can show
# the deployer exactly what they're about to push to Deploy\.
#
# Invoked from MultiTerminal.csproj's WriteBuildInfo MSBuild target after the
# build has mirrored output into the shared staged folder. Failures here must
# NOT fail the build -- the file is informational only.

param(
    # When invoked from MSBuild's WriteBuildInfo target, values arrive via
    # process EnvironmentVariables (WBI_STAGED_PATH / WBI_CONFIGURATION) so they
    # don't pass through the shell -- this avoids command injection via hostile
    # MULTITERMINAL_STAGED_PATH or Configuration values. When invoked directly
    # (e.g. by a developer testing the script), the -StagedPath / -Configuration
    # parameters work as a fallback.
    [string]$StagedPath,

    [string]$Configuration
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($StagedPath)   -and $env:WBI_STAGED_PATH)   { $StagedPath   = $env:WBI_STAGED_PATH }
if ([string]::IsNullOrWhiteSpace($Configuration) -and $env:WBI_CONFIGURATION) { $Configuration = $env:WBI_CONFIGURATION }
if ([string]::IsNullOrWhiteSpace($Configuration)) { $Configuration = "Unknown" }

if ([string]::IsNullOrWhiteSpace($StagedPath)) {
    Write-Host "WriteBuildInfo: no staged path provided (set WBI_STAGED_PATH or pass -StagedPath); skipping."
    exit 0
}

try {
    if (-not (Test-Path -LiteralPath $StagedPath)) {
        Write-Host "WriteBuildInfo: staged path does not exist, skipping: $StagedPath"
        exit 0
    }

    $branch = ""
    $commit = ""
    try { $branch = (& git rev-parse --abbrev-ref HEAD 2>$null).Trim() } catch { $branch = "unknown" }
    try { $commit = (& git rev-parse --short HEAD 2>$null).Trim() } catch { $commit = "unknown" }
    if ([string]::IsNullOrWhiteSpace($branch)) { $branch = "unknown" }
    if ([string]::IsNullOrWhiteSpace($commit)) { $commit = "unknown" }

    $agentName = if ($env:MULTITERMINAL_NAME) { $env:MULTITERMINAL_NAME } else { "unknown" }

    $info = [ordered]@{
        branch        = $branch
        commit        = $commit
        timestamp     = (Get-Date).ToString("o")
        agentName     = $agentName
        configuration = $Configuration
        sourceDir     = (Get-Location).Path
    }

    $jsonPath = Join-Path $StagedPath ".build-info.json"
    $info | ConvertTo-Json | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Write-Host "WriteBuildInfo: wrote $jsonPath  ($agentName / $branch / $commit)"
}
catch {
    Write-Host "WriteBuildInfo: non-fatal error: $_"
    exit 0
}
