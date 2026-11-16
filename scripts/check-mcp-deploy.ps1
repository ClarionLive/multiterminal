#requires -Version 5.1
# Decides whether deploy.ps1 may replace the live MCP server with the staged one
# (ticket 9ce606e0). Runs BEFORE deploy touches anything, so a refusal leaves
# both Deploy and the live server exactly as they were.
#
# Exit codes (deploy.ps1 keys on them):
#   0  sync: the staged server is an upgrade, a fresh install, or -Force was given
#   1  refuse: the staged server would be a downgrade
#   2  nothing to do: the live index.js already equals the staged one
#   3  no staged server: the build predates MCP staging; deploy binaries only
#
# A downgrade is either of:
#   * the live server offers a tool the staged one does not (agents would lose it)
#   * both copies carry a stamp, and the live commit is not an ancestor of the
#     staged commit (the staged build does not contain what is live now)
# Provenance that cannot be checked (no stamp, commit unknown to this repo) is a
# warning, not a refusal: the tool check still applies.

param(
    [Parameter(Mandatory = $true)][string]$StagedDir,
    [Parameter(Mandatory = $true)][string]$LiveDir,
    # Defaults to this script's repo. Set in the body: Windows PowerShell 5.1
    # leaves $PSScriptRoot empty while parameter defaults are evaluated.
    [string]$RepoDir,
    [switch]$Force
)

$ErrorActionPreference = "Continue"
if ([string]::IsNullOrWhiteSpace($RepoDir)) { $RepoDir = Split-Path -Parent $PSScriptRoot }
. (Join-Path $PSScriptRoot "mcp-server-info.ps1")

function Refuse([string[]]$Lines) {
    foreach ($l in $Lines) { Write-Host "MCP: $l" -ForegroundColor Red }
    if ($Force) {
        Write-Host "MCP: -ForceMcp given: syncing anyway." -ForegroundColor Yellow
        exit 0
    }
    Write-Host "MCP: refusing. Nothing has been changed. Re-run with -SkipMcpSync to deploy the binaries only, or -ForceMcp to install this MCP server anyway." -ForegroundColor Yellow
    exit 1
}

$stagedIndex = Join-Path $StagedDir "index.js"
$liveIndex = Join-Path $LiveDir "index.js"

if (-not (Test-Path -LiteralPath $stagedIndex)) {
    Write-Host "MCP: WARNING: the staged build has no MCP server ($StagedDir). It predates MCP staging, so the live MCP server stays as it is. Rebuild to stage one." -ForegroundColor Yellow
    exit 3
}

$stagedNames = Get-McpToolNames $stagedIndex
if ($null -eq $stagedNames) {
    Refuse @("cannot read the tool list from the staged index.js ($stagedIndex).")
}

if (-not (Test-Path -LiteralPath $liveIndex)) {
    Write-Host "MCP: no live MCP server at $LiveDir; installing the staged one ($($stagedNames.Count) tools)."
    exit 0
}

if ((Get-McpIndexHash $stagedIndex) -eq (Get-McpIndexHash $liveIndex)) {
    Write-Host "MCP: the live MCP server already matches the staged one; nothing to sync."
    exit 2
}

$liveNames = Get-McpToolNames $liveIndex
if ($null -eq $liveNames) {
    Write-Host "MCP: WARNING: cannot read the tool list from the live index.js; skipping the lost-tool check." -ForegroundColor Yellow
}
else {
    $lost = @($liveNames | Where-Object { $stagedNames -notcontains $_ })
    if ($lost.Count -gt 0) {
        Refuse @(
            "the staged MCP server has $($stagedNames.Count) tools; the live one has $($liveNames.Count).",
            "Agents would lose: $($lost -join ', ')")
    }
}

$stagedStamp = Read-McpStamp $StagedDir
$liveStamp = Read-McpStamp $LiveDir
$stagedCommit = if ($stagedStamp) { [string]$stagedStamp.commit } else { "" }
$liveCommit = if ($liveStamp) { [string]$liveStamp.commit } else { "" }

if ([string]::IsNullOrEmpty($stagedCommit) -or [string]::IsNullOrEmpty($liveCommit)) {
    Write-Host "MCP: WARNING: provenance unknown (staged commit '$stagedCommit', live commit '$liveCommit'); only the tool check applied." -ForegroundColor Yellow
}
elseif ($stagedCommit -ne $liveCommit) {
    & git -C $RepoDir merge-base --is-ancestor $liveCommit $stagedCommit 2>$null
    $code = $LASTEXITCODE
    if ($code -eq 1) {
        Refuse @(
            "the live MCP server came from $liveCommit, which the staged build ($stagedCommit) does not contain.",
            "Installing it would undo whatever $liveCommit added.")
    }
    elseif ($code -ne 0) {
        Write-Host "MCP: WARNING: cannot check commit ancestry in $RepoDir (live $liveCommit, staged $stagedCommit); only the tool check applied." -ForegroundColor Yellow
    }
}

$dirtyNote = if ($stagedStamp -and $stagedStamp.dirty) { " (built with uncommitted mcp/ changes)" } else { "" }
Write-Host "MCP: syncing the staged MCP server ($($stagedNames.Count) tools, commit $stagedCommit$dirtyNote) to $LiveDir."
exit 0
