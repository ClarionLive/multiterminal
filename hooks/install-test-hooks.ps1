#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs test hooks for TeammateIdle and TaskCompleted events
.DESCRIPTION
    Adds test hook configurations to ~/.claude/hooks/hooks.json
    for experimenting with Claude Code v2.1.33+ hooks
#>

$ErrorActionPreference = 'Stop'

Write-Host "=== Installing Test Hooks for Claude Code v2.1.33+ ===" -ForegroundColor Cyan
Write-Host ""

# Paths
$HooksDir = Join-Path $env:USERPROFILE ".claude\hooks"
$HooksJsonPath = Join-Path $HooksDir "hooks.json"
$ScriptDir = $PSScriptRoot

$TeammateIdleScript = Join-Path $ScriptDir "test-teammate-idle.js"
$TaskCompletedScript = Join-Path $ScriptDir "test-task-completed.js"

# Validate test scripts exist
if (-not (Test-Path $TeammateIdleScript)) {
    Write-Error "Test script not found: $TeammateIdleScript"
    exit 1
}

if (-not (Test-Path $TaskCompletedScript)) {
    Write-Error "Test script not found: $TaskCompletedScript"
    exit 1
}

Write-Host "Test scripts found" -ForegroundColor Green

# Create hooks directory if needed
if (-not (Test-Path $HooksDir)) {
    New-Item -ItemType Directory -Path $HooksDir -Force | Out-Null
    Write-Host "Created hooks directory: $HooksDir" -ForegroundColor Green
}

# Load or create hooks.json
$hooks = @{}
if (Test-Path $HooksJsonPath) {
    $hooksContent = Get-Content $HooksJsonPath -Raw
    $hooksObj = $hooksContent | ConvertFrom-Json
    # Convert PSCustomObject to hashtable
    $hooksObj.PSObject.Properties | ForEach-Object {
        $hooks[$_.Name] = $_.Value
    }
    Write-Host "Loaded existing hooks.json" -ForegroundColor Green
} else {
    Write-Host "Creating new hooks.json" -ForegroundColor Green
}

# Escape backslashes for JSON
$TeammateIdleScriptEscaped = $TeammateIdleScript.Replace('\', '\\')
$TaskCompletedScriptEscaped = $TaskCompletedScript.Replace('\', '\\')

# Add test hooks
$hooks['TeammateIdle'] = @{
    command = "node"
    args = @($TeammateIdleScriptEscaped)
}

$hooks['TaskCompleted'] = @{
    command = "node"
    args = @($TaskCompletedScriptEscaped)
}

# Save hooks.json
$hooksJson = $hooks | ConvertTo-Json -Depth 10
$hooksJson | Out-File -FilePath $HooksJsonPath -Encoding UTF8 -Force

Write-Host "Updated hooks.json with test hooks" -ForegroundColor Green
Write-Host ""

Write-Host "Test hooks installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Restart Claude Code sessions to load new hooks"
Write-Host "  2. Trigger TeammateIdle events (for example, let terminal go idle)"
Write-Host "  3. Trigger TaskCompleted events (for example, complete tasks using TaskUpdate)"
Write-Host "  4. Check logs in: $env:APPDATA\multiterminal\"
Write-Host "     - hook-test-teammate-idle.log"
Write-Host "     - hook-test-task-completed.log"
Write-Host ""
Write-Host "Current hooks configuration:" -ForegroundColor Yellow
Get-Content $HooksJsonPath
