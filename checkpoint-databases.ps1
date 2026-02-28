# Checkpoint SQLite databases to merge WAL files

$configDir = Join-Path $env:APPDATA "MultiTerminal"

Write-Host ""
Write-Host "========================================="
Write-Host "  SQLite Database Maintenance"
Write-Host "========================================="
Write-Host ""

# Check if sqlite3.exe is available
$sqlite = "sqlite3.exe"
try {
    $null = & $sqlite --version 2>&1
    $sqliteAvailable = $true
} catch {
    $sqliteAvailable = $false
}

if ($sqliteAvailable) {
    Write-Host "SQLite3 found, performing maintenance..."
    Write-Host ""

    # Checkpoint tasks.db
    $tasksDb = Join-Path $configDir "tasks.db"
    if (Test-Path $tasksDb) {
        Write-Host "Checkpointing tasks.db..."
        & $sqlite $tasksDb "PRAGMA wal_checkpoint(TRUNCATE);" 2>&1 | Out-Null
        Write-Host "  [OK] tasks.db checkpointed"
    }

    # Checkpoint messages.db
    $messagesDb = Join-Path $configDir "messages.db"
    if (Test-Path $messagesDb) {
        Write-Host "Checkpointing messages.db..."
        & $sqlite $messagesDb "PRAGMA wal_checkpoint(TRUNCATE);" 2>&1 | Out-Null
        Write-Host "  [OK] messages.db checkpointed"
    }

    Write-Host ""
    Write-Host "Database maintenance complete!"
} else {
    Write-Host "SQLite3 not found in PATH."
    Write-Host "Skipping database checkpoint."
    Write-Host ""
    Write-Host "Alternative: Delete WAL files manually:"
    Write-Host "  - tasks.db-wal"
    Write-Host "  - tasks.db-shm"
    Write-Host "  - messages.db-wal"
    Write-Host "  - messages.db-shm"
    Write-Host ""
    Write-Host "The app will recreate them on next run."
}

Write-Host ""
