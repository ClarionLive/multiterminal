# Fix corrupted MultiTerminal layout

$layoutPath = Join-Path $env:APPDATA "MultiTerminal\layout.xml"

Write-Host ""
Write-Host "========================================="
Write-Host "  MultiTerminal Layout Fix"
Write-Host "========================================="
Write-Host ""

if (Test-Path $layoutPath) {
    Write-Host "Found corrupted layout file:"
    Write-Host "  $layoutPath"
    Write-Host ""

    # Show file info
    $file = Get-Item $layoutPath
    Write-Host "File info:"
    Write-Host "  Size: $($file.Length) bytes"
    Write-Host "  Last Modified: $($file.LastWriteTime)"
    Write-Host ""

    # Delete the file
    Remove-Item $layoutPath -Force
    Write-Host "[SUCCESS] Deleted corrupted layout file!"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Start MultiTerminal"
    Write-Host "  2. A new default layout will be created"
    Write-Host "  3. All panels should display correctly"
    Write-Host ""
} else {
    Write-Host "[INFO] Layout file not found at expected location."
    Write-Host "  Expected: $layoutPath"
    Write-Host ""
    Write-Host "Checking for other config files..."
    $configDir = Join-Path $env:APPDATA "MultiTerminal"
    if (Test-Path $configDir) {
        Write-Host ""
        Write-Host "Found MultiTerminal config directory:"
        Get-ChildItem $configDir | ForEach-Object {
            Write-Host "  - $($_.Name) ($($_.Length) bytes)"
        }
    } else {
        Write-Host "  MultiTerminal config directory does not exist yet."
    }
    Write-Host ""
}
