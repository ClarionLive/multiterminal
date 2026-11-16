#requires -Version 5.1
# Shared helpers describing an MCP server copy (ticket 9ce606e0). Dot-sourced by
# sync-mcp-server.ps1 (writes a stamp next to every copy it makes) and by
# check-mcp-deploy.ps1 (decides whether deploy may replace the live copy).
#
# A stamp (.mcp-stamp.json) records which commit a copy came from and which
# tools it offers, so deploy can tell an upgrade from a downgrade without
# guessing from file timestamps.

$script:McpStampFileName = ".mcp-stamp.json"

# The tool names an index.js offers. Same region and matcher as the server's
# own startup assert (assertToolDefHandlerConsistency in mcp/index.js) and the
# CI gate mcp/test/consistency.test.mjs: every `name: "..."` between the
# ListTools and CallTool handler registrations. Returns $null when the markers
# are missing (not an index.js this helper understands), never a guessed list.
function Get-McpToolNames([string]$IndexPath) {
    if (-not (Test-Path -LiteralPath $IndexPath)) { return $null }
    $src = [System.IO.File]::ReadAllText($IndexPath)
    $listIdx = $src.IndexOf('server.setRequestHandler(ListToolsRequestSchema')
    $callIdx = $src.IndexOf('server.setRequestHandler(CallToolRequestSchema')
    if ($listIdx -lt 0 -or $callIdx -le $listIdx) { return $null }
    $region = $src.Substring($listIdx, $callIdx - $listIdx)
    $names = foreach ($m in [regex]::Matches($region, '\bname:\s*"([^"]+)"')) { $m.Groups[1].Value }
    # The leading comma keeps an empty or one-name list an array: PowerShell
    # would otherwise unroll it to $null or a bare string on return.
    return ,@($names | Sort-Object -Unique)
}

# SHA-256 of index.js with line endings normalized, so a CRLF checkout and an
# LF copy of the same source compare equal.
function Get-McpIndexHash([string]$IndexPath) {
    if (-not (Test-Path -LiteralPath $IndexPath)) { return "" }
    $text = ([System.IO.File]::ReadAllText($IndexPath)).Replace("`r`n", "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        return ([System.BitConverter]::ToString($bytes)).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Invoke-GitQuiet([string]$Directory, [string[]]$GitArgs) {
    try {
        $out = & git -C $Directory @GitArgs 2>$null
        if ($LASTEXITCODE -ne 0) { return "" }
        return (($out | Out-String).Trim())
    }
    catch { return "" }
}

# Describe the copy at $SourceDir. Commit fields are empty when $SourceDir is
# not inside a git checkout (a plain folder copy), which callers treat as
# "provenance unknown", never as a match.
function New-McpStamp([string]$SourceDir) {
    $index = Join-Path $SourceDir "index.js"
    $names = Get-McpToolNames $index
    $commit = Invoke-GitQuiet $SourceDir @("rev-parse", "HEAD")
    $branch = ""
    $dirty = $false
    if ($commit) {
        $branch = Invoke-GitQuiet $SourceDir @("rev-parse", "--abbrev-ref", "HEAD")
        $dirty = -not [string]::IsNullOrEmpty((Invoke-GitQuiet $SourceDir @("status", "--porcelain", "--", ".")))
    }
    # Assigned rather than written as `if` expressions: an `if` returning @()
    # unrolls to $null, and the stamp must say [] for "no tools".
    $toolNames = [string[]]@()
    $toolCount = -1
    if ($null -ne $names) { $toolNames = [string[]]$names; $toolCount = $names.Count }
    return [ordered]@{
        commit      = $commit
        branch      = $branch
        dirty       = $dirty
        toolCount   = $toolCount
        toolNames   = $toolNames
        indexSha256 = Get-McpIndexHash $index
        createdAt   = (Get-Date).ToUniversalTime().ToString("o")
    }
}

function Read-McpStamp([string]$Dir) {
    $path = Join-Path $Dir $script:McpStampFileName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Write-McpStamp([string]$Dir, $Stamp) {
    $path = Join-Path $Dir $script:McpStampFileName
    $Stamp | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8
}
