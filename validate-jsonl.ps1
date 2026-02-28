# Validate JSONL for corruption after concurrent writes
$stateFile = Join-Path $env:USERPROFILE '.claude\pool\state.jsonl'
$lines = Get-Content $stateFile
$totalLines = $lines.Count
$validJson = 0
$invalidJson = 0
$invalidLines = @()
$instanceCounts = @{}

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $obj = $line | ConvertFrom-Json
        $validJson++

        # Count by instance
        $inst = $obj.instance
        if ($instanceCounts.ContainsKey($inst)) {
            $instanceCounts[$inst]++
        } else {
            $instanceCounts[$inst] = 1
        }
    } catch {
        $invalidJson++
        $invalidLines += "Line $($i+1): $($line.Substring(0, [Math]::Min(80, $line.Length)))..."
    }
}

Write-Host "=== JSONL VALIDATION RESULTS ===" -ForegroundColor Cyan
Write-Host "Total lines: $totalLines"
Write-Host "Valid JSON: $validJson" -ForegroundColor Green
Write-Host "Invalid JSON: $invalidJson" -ForegroundColor $(if ($invalidJson -gt 0) { 'Red' } else { 'Green' })

Write-Host "`nMessages by instance:"
$instanceCounts.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Key): $($_.Value)"
}

if ($invalidLines.Count -gt 0) {
    Write-Host "`nCorrupted lines:" -ForegroundColor Red
    $invalidLines | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "`n✅ NO CORRUPTION DETECTED!" -ForegroundColor Green
}
