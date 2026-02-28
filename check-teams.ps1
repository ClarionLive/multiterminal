# Check Teams configuration

$teamsDir = Join-Path $env:USERPROFILE ".claude\teams"

Write-Host "Teams Configuration:"
Write-Host "===================="
Write-Host ""

Get-ChildItem $teamsDir -Recurse | ForEach-Object {
    if (-not $_.PSIsContainer) {
        Write-Host "File: $($_.FullName)"
        Write-Host "Size: $($_.Length) bytes"
        Write-Host "Modified: $($_.LastWriteTime)"
        Write-Host "Content:"
        Write-Host "--------"
        Get-Content $_.FullName
        Write-Host ""
        Write-Host "=========="
        Write-Host ""
    }
}
