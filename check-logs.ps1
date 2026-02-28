# Check MultiTerminal logs

$configDir = Join-Path $env:APPDATA "MultiTerminal"

Write-Host ""
Write-Host "========================================="
Write-Host "  MultiTerminal Logs"
Write-Host "========================================="
Write-Host ""

# MCP startup log
$mcpLog = Join-Path $configDir "mcp-startup.log"
if (Test-Path $mcpLog) {
    Write-Host "MCP Startup Log:"
    Write-Host "----------------------------------------"
    Get-Content $mcpLog | Select-Object -Last 20
    Write-Host "----------------------------------------"
    Write-Host ""
}

# Vector search log (last few lines)
$vectorLog = Join-Path $configDir "vector-search.log"
if (Test-Path $vectorLog) {
    Write-Host "Vector Search Log (last 10 lines):"
    Write-Host "----------------------------------------"
    Get-Content $vectorLog | Select-Object -Last 10
    Write-Host "----------------------------------------"
    Write-Host ""
}

# Chunking log (last few lines)
$chunkLog = Join-Path $configDir "chunking.log"
if (Test-Path $chunkLog) {
    Write-Host "Chunking Log (last 10 lines):"
    Write-Host "----------------------------------------"
    Get-Content $chunkLog | Select-Object -Last 10
    Write-Host "----------------------------------------"
    Write-Host ""
}

# Check database WAL files
Write-Host "Database WAL Status:"
Write-Host "----------------------------------------"
$tasks_wal = Join-Path $configDir "tasks.db-wal"
$messages_wal = Join-Path $configDir "messages.db-wal"
$sessions_db = Join-Path $configDir "sessions.db"

if (Test-Path $tasks_wal) {
    $size = (Get-Item $tasks_wal).Length
    Write-Host "tasks.db-wal: $([math]::Round($size/1MB, 2)) MB"
}
if (Test-Path $messages_wal) {
    $size = (Get-Item $messages_wal).Length
    Write-Host "messages.db-wal: $([math]::Round($size/1MB, 2)) MB"
}
if (Test-Path $sessions_db) {
    $size = (Get-Item $sessions_db).Length
    Write-Host "sessions.db: $([math]::Round($size/1MB, 2)) MB (WARNING: Very large!)"
}
Write-Host "----------------------------------------"
Write-Host ""
