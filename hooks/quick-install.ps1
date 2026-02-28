$HooksDir = Join-Path $env:USERPROFILE ".claude\hooks"
$HooksJsonPath = Join-Path $HooksDir "hooks.json"

if (-not (Test-Path $HooksDir)) {
    New-Item -ItemType Directory -Path $HooksDir -Force | Out-Null
}

$hooks = @{
    TeammateIdle = @{
        command = "node"
        args = @("H:\DevLaptop\ClarionPowerShell\MultiTerminal\hooks\test-teammate-idle.js")
    }
    TaskCompleted = @{
        command = "node"
        args = @("H:\DevLaptop\ClarionPowerShell\MultiTerminal\hooks\test-task-completed.js")
    }
}

$hooks | ConvertTo-Json -Depth 10 | Out-File -FilePath $HooksJsonPath -Encoding UTF8 -Force

Write-Host "Test hooks installed to: $HooksJsonPath"
