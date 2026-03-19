# Diagnose MultiTerminal startup issues

Write-Host ""
Write-Host "========================================="
Write-Host "  MultiTerminal Startup Diagnosis"
Write-Host "========================================="
Write-Host ""

# Check config directories
$roaming = Join-Path $env:APPDATA "MultiTerminal"
$local = Join-Path $env:LOCALAPPDATA "MultiTerminal"

Write-Host "1. Checking configuration directories..."
Write-Host ""

if (Test-Path $roaming) {
    Write-Host "AppData\Roaming\MultiTerminal:"
    Get-ChildItem $roaming -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  - $($_.Name) ($($_.Length) bytes, Modified: $($_.LastWriteTime))"
    }
} else {
    Write-Host "AppData\Roaming\MultiTerminal: NOT FOUND"
}

Write-Host ""

if (Test-Path $local) {
    Write-Host "AppData\Local\MultiTerminal:"
    Get-ChildItem $local -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  - $($_.Name) ($($_.Length) bytes, Modified: $($_.LastWriteTime))"
    }
} else {
    Write-Host "AppData\Local\MultiTerminal: NOT FOUND"
}

Write-Host ""
Write-Host "2. Checking for database file..."
Write-Host ""

# Check for SQLite database
$dbPath = Join-Path $roaming "multiterminal.db"
if (Test-Path $dbPath) {
    $db = Get-Item $dbPath
    Write-Host "Found database: $dbPath"
    Write-Host "  Size: $($db.Length) bytes"
    Write-Host "  Last Modified: $($db.LastWriteTime)"

    # Check if database is locked
    try {
        $stream = [System.IO.File]::Open($dbPath, 'Open', 'Read', 'None')
        $stream.Close()
        Write-Host "  Status: OK (not locked)"
    } catch {
        Write-Host "  Status: LOCKED or CORRUPTED"
        Write-Host "  Error: $($_.Exception.Message)"
    }
} else {
    Write-Host "Database not found at: $dbPath"
}

Write-Host ""
Write-Host "3. Checking for running processes..."
Write-Host ""

$processes = Get-Process -Name "MultiTerminal" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "MultiTerminal processes running:"
    $processes | ForEach-Object {
        $status = if ($_.Responding) { "Responding" } else { "NOT RESPONDING" }
        Write-Host "  - PID: $($_.Id), Status: $status"
    }
} else {
    Write-Host "No MultiTerminal processes running."
}

Write-Host ""
Write-Host "4. Checking MCP server (Node.js)..."
Write-Host ""

$nodeProcesses = Get-Process -Name "node" -ErrorAction SilentlyContinue
if ($nodeProcesses) {
    Write-Host "Node.js processes running: $($nodeProcesses.Count)"
    $nodeProcesses | Select-Object -First 3 | ForEach-Object {
        Write-Host "  - PID: $($_.Id), Memory: $([math]::Round($_.WorkingSet64/1MB, 2)) MB"
    }
} else {
    Write-Host "No Node.js processes running."
}

Write-Host ""
