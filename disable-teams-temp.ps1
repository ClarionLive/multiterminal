# Temporarily disable Claude Code Teams configuration

Write-Host ""
Write-Host "========================================="
Write-Host "  Temporarily Disable Teams Config"
Write-Host "========================================="
Write-Host ""

$teamsDir = Join-Path $env:USERPROFILE ".claude\teams"
$teamsBackup = Join-Path $env:USERPROFILE ".claude\teams.backup"

if (Test-Path $teamsDir) {
    Write-Host "Renaming teams directory to teams.backup..."
    if (Test-Path $teamsBackup) {
        Write-Host "Removing old backup..."
        Remove-Item $teamsBackup -Recurse -Force
    }
    Rename-Item $teamsDir $teamsBackup
    Write-Host "[SUCCESS] Teams directory disabled"
    Write-Host ""
    Write-Host "To re-enable later:"
    Write-Host "  Rename $teamsBackup back to $teamsDir"
} else {
    Write-Host "Teams directory not found, nothing to disable"
}

Write-Host ""
Write-Host "Now try starting MultiTerminal"
Write-Host ""
