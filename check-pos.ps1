$json = Get-Content 'H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\project.json' -Raw
Write-Output "Position 314: '$($json[314])'"
Write-Output "Context (305-325): '$($json.Substring(305, 20))'"
