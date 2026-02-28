# Check for Claude Code configuration that might be causing hang

Write-Host ""
Write-Host "========================================="
Write-Host "  Claude Code Configuration Check"
Write-Host "========================================="
Write-Host ""

$claudeDir = Join-Path $env:USERPROFILE ".claude"

if (Test-Path $claudeDir) {
    Write-Host "Found .claude directory: $claudeDir"
    Write-Host ""

    # Check for hooks
    Write-Host "Checking for hooks..."
    $hooksDir = Join-Path $claudeDir "hooks"
    if (Test-Path $hooksDir) {
        Write-Host "Found hooks directory:"
        Get-ChildItem $hooksDir -File | ForEach-Object {
            Write-Host "  - $($_.Name) (Modified: $($_.LastWriteTime))"
        }
    } else {
        Write-Host "  No hooks directory found"
    }
    Write-Host ""

    # Check for teams
    Write-Host "Checking for teams configuration..."
    $teamsDir = Join-Path $claudeDir "teams"
    if (Test-Path $teamsDir) {
        Write-Host "Found teams directory:"
        Get-ChildItem $teamsDir -Recurse | ForEach-Object {
            if ($_.PSIsContainer) {
                Write-Host "  [DIR] $($_.Name)"
            } else {
                Write-Host "  - $($_.Name) ($($_.Length) bytes, Modified: $($_.LastWriteTime))"
            }
        }
    } else {
        Write-Host "  No teams directory found"
    }
    Write-Host ""

    # Check settings.json for hooks
    Write-Host "Checking settings.json for hook references..."
    $settingsFile = Join-Path $claudeDir "settings.json"
    if (Test-Path $settingsFile) {
        $settings = Get-Content $settingsFile -Raw
        if ($settings -match 'hook|team') {
            Write-Host "Found hook/team references in settings.json:"
            $settings | Select-String -Pattern 'hook|team' | ForEach-Object {
                Write-Host "  $($_.Line.Trim())"
            }
        } else {
            Write-Host "  No hook/team references found in settings.json"
        }
    } else {
        Write-Host "  settings.json not found"
    }
    Write-Host ""

    # Check for any recent files in .claude
    Write-Host "Recent files in .claude (last 24 hours):"
    $yesterday = (Get-Date).AddHours(-24)
    Get-ChildItem $claudeDir -Recurse -File | Where-Object { $_.LastWriteTime -gt $yesterday } | Select-Object -First 10 | ForEach-Object {
        $relativePath = $_.FullName.Replace($claudeDir, ".claude")
        Write-Host "  $relativePath (Modified: $($_.LastWriteTime))"
    }

} else {
    Write-Host ".claude directory not found at: $claudeDir"
}

Write-Host ""
Write-Host "========================================="
Write-Host ""
