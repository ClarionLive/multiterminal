<#
.SYNOPSIS
    Creates a throwaway git repository fixture for stress-testing the HUD Git tab
    (MultiTerminal kanban task fb718102, item [14]).

.DESCRIPTION
    Produces a temp-directory git repo with:
      - <CommitCount> commits in history (default 1000), each touching one file
      - <UntrackedFileCount> untracked files in the working tree (default 100)
      - <ModifiedFileCount> uncommitted modifications (default 25)
    so the working-changes panel + commits log + branches panel can be exercised
    against realistic-stress-scale data.

    The fixture is fast to generate (single-author commits, minimal content) but
    realistic in shape: a forward-only history with non-trivial commit messages
    and a varied working-tree state.

.PARAMETER Path
    Where to create the fixture. Default: $env:TEMP\mt-stress-fixture
    Will be wiped clean if it already exists (with confirmation prompt).

.PARAMETER CommitCount
    Total commits to generate. Default: 1000.

.PARAMETER UntrackedFileCount
    Untracked files to leave in the working tree. Default: 100.

.PARAMETER ModifiedFileCount
    Tracked files with uncommitted modifications. Default: 25.

.PARAMETER Force
    Skip the wipe-confirmation prompt.

.EXAMPLE
    .\stress-fixture-git-tab.ps1
    Generates the fixture at the default path with default counts.

.EXAMPLE
    .\stress-fixture-git-tab.ps1 -Path C:\stress-fixture -CommitCount 5000 -Force
    Bigger fixture, custom path, no prompt.

.NOTES
    After running, point MultiTerminal at the fixture directory as a project
    and follow docs/git-tab-stress-test-plan.md to verify no UI hangs and no
    excessive memory growth.
#>

[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:TEMP 'mt-stress-fixture'),
    [int]$CommitCount = 1000,
    [int]$UntrackedFileCount = 100,
    [int]$ModifiedFileCount = 25,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Wipe-IfExists {
    param([string]$Target)
    if (Test-Path -LiteralPath $Target) {
        if (-not $Force) {
            $answer = Read-Host "Path '$Target' exists. Wipe and recreate? (y/N)"
            if ($answer -notmatch '^[Yy]') {
                Write-Host "Aborted."
                exit 1
            }
        }
        Write-Host "Wiping $Target ..."
        Remove-Item -LiteralPath $Target -Recurse -Force
    }
}

# --- Pre-flight ---
$gitExe = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitExe) {
    Write-Error "git is not on PATH. Install git or add it to PATH."
    exit 2
}

Wipe-IfExists $Path
New-Item -ItemType Directory -Path $Path -Force | Out-Null
Push-Location $Path

try {
    # --- Init ---
    Write-Host "Initialising repo at $Path ..."
    git init -q -b master
    git config user.email 'stress-fixture@multiterminal.local'
    git config user.name  'Stress Fixture'

    # --- Commit history ---
    # Single round-robin file pool keeps each commit's diff small (~1 line) so
    # the loop is fast even at 5000+ commits. The shape of the history is what
    # matters for the stress test, not byte volume.
    $poolSize = [Math]::Max(8, [int]($CommitCount / 50))
    Write-Host "Generating $CommitCount commits across a $poolSize-file pool ..."
    for ($i = 0; $i -lt $poolSize; $i++) {
        Set-Content -Path "src-$i.txt" -Value "// initial line for src-$i"
    }
    git add -A | Out-Null
    git commit -q -m "Initial commit (pool of $poolSize files)"

    $start = Get-Date
    for ($i = 1; $i -lt $CommitCount; $i++) {
        $idx = $i % $poolSize
        $file = "src-$idx.txt"
        Add-Content -Path $file -Value "// commit $i touched $file"
        git add $file | Out-Null
        git commit -q -m "stress: commit $i — touch $file"
        if ($i % 100 -eq 0) {
            $elapsed = (Get-Date) - $start
            $rate = if ($elapsed.TotalSeconds -gt 0) { [int]($i / $elapsed.TotalSeconds) } else { 0 }
            Write-Host ("  {0} / {1} commits ({2}/s)" -f $i, $CommitCount, $rate)
        }
    }
    Write-Host "Committed $CommitCount entries."

    # --- Modified-but-uncommitted files (M) ---
    Write-Host "Adding $ModifiedFileCount uncommitted modifications ..."
    for ($i = 0; $i -lt $ModifiedFileCount; $i++) {
        $idx = $i % $poolSize
        $file = "src-$idx.txt"
        Add-Content -Path $file -Value "// uncommitted edit $i"
    }

    # --- Untracked files (U) ---
    Write-Host "Generating $UntrackedFileCount untracked files ..."
    New-Item -ItemType Directory -Path 'untracked' -Force | Out-Null
    for ($i = 0; $i -lt $UntrackedFileCount; $i++) {
        $name = 'untracked/file-{0:D4}.txt' -f $i
        Set-Content -Path $name -Value "untracked stress-fixture file $i`r`nLine 2`r`nLine 3"
    }

    # --- Summary ---
    $statusCount = (git status --porcelain | Measure-Object).Count
    $logCount    = (git rev-list --count HEAD)
    Write-Host ""
    Write-Host "=== Fixture ready ==="
    Write-Host "  Path           : $Path"
    Write-Host "  Total commits  : $logCount"
    Write-Host "  Working-tree   : $statusCount entries (modifications + untracked)"
    Write-Host "  Branches       : $(git branch | Measure-Object | Select-Object -ExpandProperty Count) local"
    Write-Host ""
    Write-Host "Next: register this directory as a project in MultiTerminal and"
    Write-Host "follow docs/git-tab-stress-test-plan.md."
}
finally {
    Pop-Location
}
