# Presence routing — post-deploy smoke test (task 9f9c3141)
#
# Verifies the FULL in-app path headlessly: publishes mmWave occupancy over MQTT and confirms MT's
# remoteMode gate flips, read via GET /api/remote-mode (no need to watch the status-bar pill).
#
# PREREQUISITES:
#   1. The presence build is DEPLOYED and MT is running (the adapter only starts at MT launch).
#   2. presence.enabled=1 is set in %APPDATA%\MultiTerminal\settings.txt.
#      IMPORTANT: set that line while MT is STOPPED (e.g. during deploy) — the running app owns
#      settings.txt and rewrites it from memory, so a live edit gets clobbered. After the line is in
#      place, (re)start MT so the adapter picks it up.
#   3. Mosquitto installed (this script will start a loopback broker on the port if none is listening).
#
# USAGE:  pwsh -File docs\presence\presence-smoke-test.ps1
#         pwsh -File docs\presence\presence-smoke-test.ps1 -Port 1883 -DebounceWaitSec 7

param(
    [int]$Port = 1883,
    [string]$MtApi = "http://localhost:5050",
    [int]$DebounceWaitSec = 7,
    [string]$MosquittoDir = "C:\Program Files\mosquitto"
)

$ErrorActionPreference = "Stop"
$pub = Join-Path $MosquittoDir "mosquitto_pub.exe"
$brk = Join-Path $MosquittoDir "mosquitto.exe"
if (-not (Test-Path $pub)) { throw "mosquitto_pub.exe not found at $pub — install Mosquitto or pass -MosquittoDir." }

# Start a loopback broker if nothing is listening on $Port.
$startedBroker = $null
$listening = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    Write-Host "No broker on $Port — starting a temporary loopback Mosquitto..." -ForegroundColor Yellow
    $conf = Join-Path $env:TEMP "mt-presence-smoke.conf"
    "listener $Port 127.0.0.1`nallow_anonymous true" | Set-Content -Path $conf -Encoding ascii
    $startedBroker = Start-Process -FilePath $brk -ArgumentList @("-c", $conf) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1
} else {
    Write-Host "Using existing broker on port $Port." -ForegroundColor Green
}

function Pub([string]$topic, [string]$msg) {
    & $pub -h 127.0.0.1 -p $Port -t $topic -m $msg -q 1
    if ($LASTEXITCODE -ne 0) { throw "mosquitto_pub failed (exit $LASTEXITCODE) — is the broker up on $Port?" }
}

function Get-RemoteMode {
    try { return [bool](Invoke-RestMethod -Uri "$MtApi/api/remote-mode" -TimeoutSec 5).remote_mode }
    catch { throw "GET $MtApi/api/remote-mode failed: $($_.Exception.Message). Is MT running with the presence build?" }
}

$pass = $true
function Check([string]$label, [bool]$expected) {
    Start-Sleep -Seconds $DebounceWaitSec
    $actual = Get-RemoteMode
    $ok = ($actual -eq $expected)
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("{0,-42} expected remote_mode={1,-5} got={2,-5} {3}" -f $label, $expected, $actual, $(if ($ok) { "PASS" } else { "FAIL" })) -ForegroundColor $color
    if (-not $ok) { $script:pass = $false }
}

try {
    Write-Host "`n=== Presence smoke test (broker :$Port, MT $MtApi) ===`n"

    Write-Host "1. At desk (occupancy ON — also establishes routing readiness)"
    Pub "mt/presence/desk/occupancy" "ON"
    Check "   at desk -> desktop" $false

    Write-Host "2. Walk away (occupancy OFF, no phone registered -> Away)"
    Pub "mt/presence/desk/occupancy" "OFF"
    Check "   away -> phone push" $true

    Write-Host "3. Back at desk (occupancy ON)"
    Pub "mt/presence/desk/occupancy" "ON"
    Check "   back at desk -> desktop" $false

    Write-Host "4. Sensor dies while 'at desk' (LWT offline must distrust stale occupancy)"
    Pub "mt/presence/status" "offline"
    Check "   offline -> phone push (fail-safe)" $true

    # Clear the retained offline + restore so we don't leave the gate latched.
    Pub "mt/presence/status" "online"
    Pub "mt/presence/desk/occupancy" "ON"

    Write-Host ""
    if ($pass) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green }
    else { Write-Host "SOME CHECKS FAILED — see above. Check the MT debug log (category 'Presence')." -ForegroundColor Red }
}
finally {
    if ($startedBroker) {
        Write-Host "Stopping the temporary broker (pid $($startedBroker.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $startedBroker.Id -Force -ErrorAction SilentlyContinue
    }
}
