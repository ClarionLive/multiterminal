# Alice's concurrent write stress test
$stateFile = Join-Path $env:USERPROFILE '.claude\pool\state.jsonl'
$baseTs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

Write-Host "Alice: Starting rapid write test..."

1..50 | ForEach-Object {
    $msg = @{
        instance = 'Alice'
        action = 'LEARNED'
        topic = "Concurrent test msg $_"
        ts = $baseTs + $_
    } | ConvertTo-Json -Compress

    # Non-atomic append - this is what we're testing for corruption
    Add-Content -Path $stateFile -Value $msg
}

Write-Host "Alice: 50 messages written!"
