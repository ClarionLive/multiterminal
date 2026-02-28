# ============================================================================
# Terminal Jitter Diagnostic Script
# ============================================================================
# This script diagnoses why Claude Code might be experiencing terminal jitter
# by checking for DEC Mode 2026 (Synchronized Output) support.
#
# Claude Code already uses DEC 2026 when available, so jitter indicates:
# 1. Not running in Windows Terminal (using old PowerShell console)
# 2. Windows Terminal version < 1.23 (DEC 2026 added in v1.23)
# 3. Terminal query failing (ConPTY or other issue)
# ============================================================================

Write-Host "`n🔍 Claude Code Terminal Jitter Diagnostic`n" -ForegroundColor Cyan
Write-Host "=" * 70 -ForegroundColor Gray

# ============================================================================
# CHECK 1: Windows Terminal Detection
# ============================================================================
Write-Host "`n[1] Checking if running in Windows Terminal..." -ForegroundColor Yellow

$wtSession = $env:WT_SESSION
if ($wtSession) {
    Write-Host "    ✅ PASS: Running in Windows Terminal" -ForegroundColor Green
    Write-Host "       Session ID: $wtSession" -ForegroundColor Gray
    $inWindowsTerminal = $true
} else {
    Write-Host "    ❌ FAIL: NOT running in Windows Terminal" -ForegroundColor Red
    Write-Host "       You're likely in the old PowerShell console (conhost.exe)" -ForegroundColor Red
    Write-Host "       The old console does NOT support DEC Mode 2026!" -ForegroundColor Red
    $inWindowsTerminal = $false
}

# ============================================================================
# CHECK 2: Parent Process Name
# ============================================================================
Write-Host "`n[2] Checking parent terminal process..." -ForegroundColor Yellow

try {
    $parentProcess = (Get-Process -Id $PID).Parent
    $parentName = $parentProcess.ProcessName
    Write-Host "    Parent Process: $parentName" -ForegroundColor Gray

    if ($parentName -eq "WindowsTerminal") {
        Write-Host "    ✅ PASS: Windows Terminal is the parent process" -ForegroundColor Green
    } elseif ($parentName -match "conhost|powershell|cmd") {
        Write-Host "    ❌ FAIL: Old console detected ($parentName)" -ForegroundColor Red
        Write-Host "       This terminal does NOT support DEC Mode 2026!" -ForegroundColor Red
    } else {
        Write-Host "    ⚠️  WARN: Unknown parent process: $parentName" -ForegroundColor Yellow
    }
} catch {
    Write-Host "    ⚠️  WARN: Could not determine parent process" -ForegroundColor Yellow
}

# ============================================================================
# CHECK 3: Windows Terminal Version
# ============================================================================
Write-Host "`n[3] Checking Windows Terminal version..." -ForegroundColor Yellow

if ($inWindowsTerminal) {
    try {
        $wtVersion = & wt.exe --version 2>&1
        if ($wtVersion -match "(\d+\.\d+\.\d+)") {
            $versionNumber = $matches[1]
            Write-Host "    Version: $versionNumber" -ForegroundColor Gray

            # Parse version (need v1.23+)
            $parts = $versionNumber.Split('.')
            $major = [int]$parts[0]
            $minor = [int]$parts[1]

            if ($major -gt 1 -or ($major -eq 1 -and $minor -ge 23)) {
                Write-Host "    ✅ PASS: Version $versionNumber supports DEC Mode 2026" -ForegroundColor Green
            } else {
                Write-Host "    ❌ FAIL: Version $versionNumber is too old (need v1.23+)" -ForegroundColor Red
                Write-Host "       DEC Mode 2026 was added in v1.23.2372.0" -ForegroundColor Red
            }
        } else {
            Write-Host "    ⚠️  WARN: Could not parse version: $wtVersion" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "    ⚠️  WARN: Could not run 'wt.exe --version'" -ForegroundColor Yellow
        Write-Host "       Error: $_" -ForegroundColor Gray
    }
} else {
    Write-Host "    ⏭️  SKIP: Not in Windows Terminal" -ForegroundColor Gray
}

# ============================================================================
# CHECK 4: DEC Mode 2026 Query Test
# ============================================================================
Write-Host "`n[4] Testing DEC Mode 2026 query response..." -ForegroundColor Yellow
Write-Host "    Sending query: \x1b[?2026`$p" -ForegroundColor Gray

# Note: This test is informational only. The actual query happens at the
# terminal level and responses are captured by the terminal emulator.
# We can send the query but capturing the response requires terminal-specific
# handling that PowerShell doesn't natively support.

Write-Host "    ℹ️  INFO: Query sent (response handling requires terminal-level capture)" -ForegroundColor Cyan
Write-Host "       Expected response if supported: \x1b[?2026;1`$y" -ForegroundColor Gray
Write-Host "       Expected response if NOT supported: \x1b[?2026;0`$y" -ForegroundColor Gray

# Send the query (terminal will respond, but we can't easily capture it in PowerShell)
Write-Host -NoNewline "`e[?2026`$p"

# ============================================================================
# RECOMMENDATIONS
# ============================================================================
Write-Host "`n" + ("=" * 70) -ForegroundColor Gray
Write-Host "`n📋 RECOMMENDATIONS:`n" -ForegroundColor Cyan

if (-not $inWindowsTerminal) {
    Write-Host "🚨 CRITICAL: You're not using Windows Terminal!" -ForegroundColor Red
    Write-Host ""
    Write-Host "To fix Claude Code jitter, you MUST switch to a terminal with DEC Mode 2026 support:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "OPTION 1: Windows Terminal (Recommended)" -ForegroundColor Green
    Write-Host "  • Already installed on Windows 11" -ForegroundColor Gray
    Write-Host "  • Free download for Windows 10: https://aka.ms/terminal" -ForegroundColor Gray
    Write-Host "  • Native performance, DEC 2026 support in v1.23+" -ForegroundColor Gray
    Write-Host "  • Launch: Press Win+X, select 'Windows Terminal'" -ForegroundColor Gray
    Write-Host ""
    Write-Host "OPTION 2: WezTerm (Alternative)" -ForegroundColor Green
    Write-Host "  • Download: https://wezfurlong.org/wezterm/installation.html" -ForegroundColor Gray
    Write-Host "  • Cross-platform, GPU-accelerated, Rust-based" -ForegroundColor Gray
    Write-Host "  • Built-in DEC 2026 support" -ForegroundColor Gray
    Write-Host ""
    Write-Host "After switching terminals, run Claude Code again. Jitter should be GONE! ✨" -ForegroundColor Green
} else {
    Write-Host "✅ Good news: You're using Windows Terminal!" -ForegroundColor Green
    Write-Host ""
    Write-Host "If you're still experiencing jitter:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "1. Update Windows Terminal to latest version:" -ForegroundColor Gray
    Write-Host "   • Open Microsoft Store" -ForegroundColor Gray
    Write-Host "   • Search 'Windows Terminal'" -ForegroundColor Gray
    Write-Host "   • Click 'Update' if available" -ForegroundColor Gray
    Write-Host "   • Need v1.23+ for DEC Mode 2026 support" -ForegroundColor Gray
    Write-Host ""
    Write-Host "2. Restart Windows Terminal after updating" -ForegroundColor Gray
    Write-Host ""
    Write-Host "3. Verify Claude Code is sending DEC 2026 codes:" -ForegroundColor Gray
    Write-Host "   • Claude Code automatically detects and uses DEC 2026" -ForegroundColor Gray
    Write-Host "   • If still jittering, check Claude Code version (update if old)" -ForegroundColor Gray
}

Write-Host "`n" + ("=" * 70) -ForegroundColor Gray
Write-Host "`n📚 Technical Details:`n" -ForegroundColor Cyan
Write-Host "Problem: Claude Code generates 4,000-6,700 scroll events/second" -ForegroundColor Gray
Write-Host "Solution: DEC Mode 2026 batches these into single atomic frame updates" -ForegroundColor Gray
Write-Host "Result: Zero flicker/jitter on supported terminals" -ForegroundColor Gray
Write-Host ""
Write-Host "Escape Codes:" -ForegroundColor Gray
Write-Host "  • Enable batching: \x1b[?2026h" -ForegroundColor Gray
Write-Host "  • Render batch:    \x1b[?2026l" -ForegroundColor Gray
Write-Host "  • Query support:   \x1b[?2026`$p" -ForegroundColor Gray
Write-Host ""
Write-Host "References:" -ForegroundColor Gray
Write-Host "  • GitHub Issue #9935: https://github.com/anthropics/claude-code/issues/9935" -ForegroundColor Gray
Write-Host "  • Windows Terminal PR #18826: DEC 2026 implementation" -ForegroundColor Gray
Write-Host "  • DEC 2026 Spec: https://gist.github.com/christianparpart/d8a62cc1ab659194337d73e399004036" -ForegroundColor Gray

Write-Host "`n" + ("=" * 70) -ForegroundColor Gray
Write-Host ""
