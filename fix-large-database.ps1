# Fix large database issue

$configDir = Join-Path $env:APPDATA "MultiTerminal"
$sessionsDb = Join-Path $configDir "sessions.db"
$backupName = "sessions.db.backup-$(Get-Date -Format 'yyyy-MM-dd-HHmmss')"
$backupPath = Join-Path $configDir $backupName

Write-Host ""
Write-Host "========================================="
Write-Host "  Fix Large Database File"
Write-Host "========================================="
Write-Host ""

if (Test-Path $sessionsDb) {
    $size = (Get-Item $sessionsDb).Length
    $sizeMB = [math]::Round($size/1MB, 2)

    Write-Host "Found sessions.db:"
    Write-Host "  Size: $sizeMB MB"
    Write-Host ""

    if ($sizeMB -gt 100) {
        Write-Host "WARNING: Database is unusually large (>100 MB)"
        Write-Host "This may be causing startup hang."
        Write-Host ""
        Write-Host "Creating backup..."
        Move-Item $sessionsDb $backupPath -Force
        Write-Host "[SUCCESS] Renamed to: $backupName"
        Write-Host ""
        Write-Host "MultiTerminal will create a fresh sessions.db on next startup."
        Write-Host ""
        Write-Host "If you need to restore the old database:"
        Write-Host "  1. Close MultiTerminal"
        Write-Host "  2. Delete the new sessions.db"
        Write-Host "  3. Rename $backupName back to sessions.db"
        Write-Host ""
    } else {
        Write-Host "Database size looks normal. Not renaming."
        Write-Host ""
    }
} else {
    Write-Host "sessions.db not found - will be created on startup."
    Write-Host ""
}

Write-Host "Next steps:"
Write-Host "  1. Try starting MultiTerminal"
Write-Host "  2. It should start normally with a fresh database"
Write-Host ""
