# Verify deployment
$dest = "H:\DevLaptop\ClarionPowerShell\Deploy"

Write-Host ""
Write-Host "==================================="
Write-Host "  MultiTerminal Deployment Status"
Write-Host "==================================="
Write-Host ""

# Check executable
$exe = Get-Item "$dest\MultiTerminal.exe"
Write-Host "Main Executable:"
Write-Host "  Name: $($exe.Name)"
Write-Host "  Size: $($exe.Length) bytes ($([math]::Round($exe.Length/1KB, 2)) KB)"
Write-Host "  Last Modified: $($exe.LastWriteTime)"
Write-Host ""

# Count files
$fileCount = (Get-ChildItem $dest -Recurse -File).Count
Write-Host "Total Files: $fileCount"
Write-Host ""

# List directories
Write-Host "Deployed Directories:"
Get-ChildItem $dest -Directory | ForEach-Object {
    $dirFileCount = (Get-ChildItem $_.FullName -Recurse -File).Count
    Write-Host "  - $($_.Name) ($dirFileCount files)"
}
Write-Host ""

# Check key DLLs
Write-Host "Key Dependencies:"
$keyDlls = @(
    "MultiTerminal.dll",
    "WeifenLuo.WinFormsUI.Docking.dll",
    "Microsoft.Web.WebView2.Core.dll",
    "ModelContextProtocol.dll",
    "System.Data.SQLite.dll"
)
foreach ($dll in $keyDlls) {
    if (Test-Path "$dest\$dll") {
        $size = (Get-Item "$dest\$dll").Length
        $sizeKB = [math]::Round($size/1KB, 2)
        Write-Host "  [OK] $dll ($sizeKB KB)"
    } else {
        Write-Host "  [MISSING] $dll"
    }
}
Write-Host ""
Write-Host "Deployment Location: $dest"
Write-Host ""
