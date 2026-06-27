# Adding presence / motion detection to MultiTerminal — end-to-end setup guide

This is the **start-here** walkthrough for giving MultiTerminal presence-aware notification
routing: a real mmWave sensor that detects whether you're at your desk and automatically switches
notifications between your desktop (present) and your phone (away). It's the concrete, reproducible
version of the bring-up done in task `9f9c3141` — written so another dev on another machine can
follow the same path.

> Companion docs (read alongside this one):
> - [presence-routing.md](presence-routing.md) — the MQTT topic/payload contract + phone-identity (IRK) theory
> - [presence-mosquitto-setup.md](presence-mosquitto-setup.md) — broker decision + security model
> - [msr2-presence.yaml](msr2-presence.yaml) — the actual ESPHome firmware config (working, verified)
> - [secrets.yaml.example](secrets.yaml.example) — secrets template
> - [presence-smoke-test.ps1](presence-smoke-test.ps1) — headless verification script

## What you get

```
Sensor (ESP32 + mmWave) ──MQTT──► MT PresenceAdapter ──► state machine ──► SetRemoteMode(bool)
   desk/occupancy ON|OFF            (subscribe, debounce,    (At desk / Away)   │
   status online|offline (LWT)       graceful degradation)                      ▼
                                                              MT's Local/Remote "pill" flips, and
                                                              EVERY phone-push path (permission
                                                              prompts, agent messages, task/error
                                                              notifications) auto-routes to the
                                                              phone while you're away, desktop while
                                                              you're present. No human or agent in
                                                              the loop — it's fully self-driving.
```

## Hardware used here

- **Apollo Automation MSR-2** multisensor (USB-C). Internally an **ESP32-C3** + **HLK-LD2410B**
  24 GHz mmWave radar. mmWave (not PIR) is the point: it detects you sitting *still*.
- Any ESP32 + LD2410 board works with minor pin changes — see [Portability](#portability).

## Prerequisites

| Need | Notes |
|------|-------|
| Python 3.x | For ESPHome. |
| ESPHome | `pip install esphome` (see Step 1 — there's a Windows gotcha). |
| Mosquitto | The MQTT broker. `winget install EclipseFoundation.Mosquitto`. |
| MultiTerminal | With the presence adapter (Services/Presence/*) — already in the build. |
| The sensor on USB | For first flash. After that, OTA over Wi-Fi works too. |

---

## Step 1 — Install ESPHome

```powershell
pip install esphome
esphome version   # confirm it runs
```

> ⚠️ **Windows Defender false-positive (we hit this).** Defender may quarantine ESPHome's `zeroconf`
> dependency wheel mid-install (it's an mDNS library and trips a heuristic), zeroing the file so pip
> fails with `OSError [Errno 22]` or a SHA-256 mismatch. If that happens:
> 1. Confirm it: `Get-MpThreatDetection` will list the `zeroconf-*.whl` under quarantine.
> 2. Add a **temporary, scoped** Defender exclusion (admin) for your pip working dir + the install's
>    `site-packages\zeroconf`, install, then narrow/remove it.
> This is environment-specific — many machines won't hit it.

> ⚠️ **Space in your Windows username (we hit this).** PlatformIO (ESPHome's build backend) **fails
> on whitespace in paths** and on the MSYS/Git-Bash environment. Two consequences:
> - Run every `esphome` command from **native PowerShell or cmd**, NOT Git Bash/WSL/MSYS.
> - If your user folder has a space (e.g. `C:\Users\John Hickey`), point PlatformIO at a space-free
>   core dir and use a space-free temp dir. The bulletproof fix is a venv on a short path:
>   ```powershell
>   python -m venv C:\ev\venv
>   C:\ev\venv\Scripts\python -m pip install esphome
>   $env:PLATFORMIO_CORE_DIR = "C:\ev\pio"   # space-free
>   $env:TMP = "C:\ev\t"; $env:TEMP = "C:\ev\t"
>   ```

---

## Step 2 — Stand up the MQTT broker (Mosquitto)

Install Mosquitto, then give it **two** listeners: loopback for MT, and your LAN IP so the Wi-Fi
sensor can reach it. Edit `C:\Program Files\Mosquitto\mosquitto.conf` (admin):

```conf
listener 1883 127.0.0.1          # MT's in-process adapter connects here
listener 1883 192.168.x.x        # YOUR LAN IP — the sensor publishes here
allow_anonymous true
```

```powershell
Restart-Service mosquitto
```

> ⚠️ **Loopback-only is the default and will silently break the sensor.** Out of the box Mosquitto
> 2.x listens only on `127.0.0.1`/`::1`. MT works (loopback), but the sensor's TCP connect will
> **time out** (`select() timeout` in the device log). You must add the LAN listener.

> ⚠️ **Windows Firewall blocks inbound 1883 by default (we hit this).** Even with the LAN listener,
> the sensor can't connect until you allow the port. Scope it tight (admin):
> ```powershell
> New-NetFirewallRule -DisplayName "Mosquitto MQTT 1883" -Direction Inbound -Protocol TCP `
>   -LocalPort 1883 -LocalAddress 192.168.x.x -RemoteAddress 192.168.0.0/16 -Action Allow
> ```

> 🔒 **Security:** an anonymous LAN-reachable broker means anything on your LAN can publish presence
> topics (and thus flip your routing). For a single-user home LAN that's the accepted v1 posture.
> To harden: keep it loopback-only if the sensor can reach MT another way, or add
> `allow_anonymous false` + a `password_file`. Full model in
> [presence-mosquitto-setup.md §6](presence-mosquitto-setup.md).

---

## Step 3 — Configure the firmware secrets

Copy [secrets.yaml.example](secrets.yaml.example) to `secrets.yaml` next to
[msr2-presence.yaml](msr2-presence.yaml) and fill in:

```yaml
wifi_ssid: "Your 2.4GHz SSID"     # ESP32 is 2.4GHz ONLY — a 5GHz-only SSID won't connect
wifi_password: "..."
mqtt_broker: "192.168.x.x"        # the LAN IP of the box running Mosquitto
```

`secrets.yaml` is git-ignored. The firmware config itself ([msr2-presence.yaml](msr2-presence.yaml))
is committed and documented inline — key hardware facts: **ESP32-C3**, LD2410 on **UART GPIO21(tx)/
GPIO20(rx) @ 256000**, logger over **USB-Serial-JTAG** (frees the UART pins), publishing the flat
`mt/presence/*` topics.

---

## Step 4 — Build & flash

Find the sensor's COM port (it enumerates as an Espressif USB-Serial-JTAG device, VID `0x303A`):

```powershell
Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match 'COM\d+' }
```

Then, **from PowerShell** (see the Step 1 MSYS/space warnings):

```powershell
$env:PLATFORMIO_CORE_DIR = "C:\ev\pio"; $env:TMP = "C:\ev\t"; $env:TEMP = "C:\ev\t"
esphome run msr2-presence.yaml --device COM5      # compile + flash; first build downloads the toolchain (slow)
```

> First compile pulls the whole ESP-IDF toolchain (5–15 min). Subsequent builds are seconds.
> Flashing the focused config **replaces** the Apollo factory firmware (other onboard sensors go
> away) — it's fully reversible by reflashing Apollo's official config later.

---

## Step 5 — Enable presence in MultiTerminal

Add to `%APPDATA%\MultiTerminal\settings.txt` **while MT is stopped** (the running app rewrites this
file from memory, so a live edit gets clobbered):

```
presence.enabled=1
```

Optionally add a single-line `presence.config={...}` JSON for broker host/port/topic-prefix/phones;
if omitted it defaults to `127.0.0.1:1883`, prefix `mt/presence`, no phones (pure mmWave desk/away).
Restart MT. The debug log (category **Presence**) should show:

```
Presence adapter starting — broker 127.0.0.1:1883, prefix 'mt/presence', 0 phone(s)...
Connected + subscribed to 'mt/presence/#'.
```

---

## Step 6 — Verify

**Headless smoke test** (simulates the sensor, asserts MT's gate flips):
```powershell
pwsh -File docs\presence\presence-smoke-test.ps1
```

**Confirm the real sensor is publishing** (subscribe to the broker):
```powershell
& "C:\Program Files\Mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "mt/presence/#" -v
# expect: mt/presence/status online   and   mt/presence/desk/occupancy ON|OFF
```

**Watch the routing flag flip** as you move:
```powershell
curl http://localhost:5050/api/remote-mode      # {"remote_mode":false} at desk, true when away
```
You'll also see MT's status-bar **Local/Remote pill** flip, and any phone push will route
accordingly. Expect **~10–15 s** to switch (LD2410 absence timeout + MT debounce + walk time) — a
deliberate cushion so brief movements don't thrash your routing. Lower the LD2410 `timeout` and MT
`debounceSeconds` if you want it snappier.

**Phone push** (optional): your phone must be subscribed to MT's push gateway first. Then a
notification sent while you're away (`remote_mode=true`) routes to the phone; at the desk it's
suppressed. Quick test of the gated path:
```powershell
# at desk -> suppressed; away -> phone push. Add ?forcePush=true to bypass the gate for a smoke test.
curl -X POST "http://localhost:5050/api/notifications" -H "Content-Type: application/json" `
  --data '{"notification_type":"message","title":"MT","message":"presence test","agent_name":"You"}'
```
Valid `notification_type`s: `task_complete, ready_for_testing, escalation, helper_request,
agent_stopped, permission_request, error, message, inbox`.

> ⚠️ **Publish-on-change alone isn't enough (we hit this).** If the firmware only published occupancy
> on state *change*, the initial reading (sent before MQTT connected, or while you sit still) never
> reaches MT and it stays dormant. The shipped config fixes this with an **on-connect republish + a
> 10 s interval republish** (retained). Keep those if you adapt the config.

---

## Step 7 — Calibrate the desk zone

The LD2410 detects up to ~6 m by default — i.e. your whole room counts as "present." To make only
your desk count (so getting up and moving to a couch reads as *away*), cap the detection distance.
The config sets this at boot via the LD2410 `max_move_distance_gate` / `max_still_distance_gate`
numbers (each gate ≈ 0.75 m; gate 3 ≈ 2.25 m):

```yaml
# in esphome: on_boot (already in msr2-presence.yaml)
- number.set: { id: max_move_gate,  value: 3 }   # ~2.25m
- number.set: { id: max_still_gate, value: 3 }
- number.set: { id: radar_timeout,  value: 5 }   # absence delay (s); lower = snappier "away"
```

**How to pick the cap:** temporarily flip the `radar_*_distance` sensors to `internal: false`,
subscribe to `mt/presence/sensor/#`, and read the distance (cm) at your desk vs. wherever your
"away" spot is. In our setup: desk ≤ 210 cm, couch ≥ 290 cm — a clean gap, so gate 3 (225 cm) splits
them perfectly. Set the cap between the two clusters, reflash, re-test, then set the sensors back to
`internal: true` to silence the MQTT noise.

---

## Troubleshooting — the gotchas we hit, in order

| Symptom | Cause | Fix |
|---|---|---|
| `pip install esphome` fails, `Errno 22` / SHA mismatch on `zeroconf` | Defender quarantining the wheel | Scoped temporary Defender exclusion (Step 1) |
| Compile: `Detected a whitespace character in project paths` | Space in Windows username → PlatformIO | `PLATFORMIO_CORE_DIR` + venv on a space-free path |
| Compile: `MSys/Mingw is not supported` | Ran esphome from Git Bash/MSYS | Run from native PowerShell/cmd |
| Sensor joins Wi-Fi but MQTT `select() timeout` | Broker loopback-only **and/or** firewall blocks 1883 | LAN listener (Step 2) + inbound firewall rule |
| `status online` arrives but no `desk/occupancy`, routing stays "present" | Publish-on-change only; initial publish lost | on-connect + interval republish (in shipped config) |
| Couch / across-the-room reads as "present" | LD2410 default ~6 m range | Cap detection gate to the desk zone (Step 7) |
| Phone push `successCount:0` once, then works on retry | Apple Web Push throttling a rapid burst | Space out sends; retry. Subs aren't pruned unless 410 Gone |
| `Unknown notification_type` (HTTP 400) | Type not in the allow-list | Use one of the valid types listed in Step 6 |

---

## Portability

**Same for everyone (universal):** install ESPHome → Mosquitto with a LAN listener → firewall allow
1883 → flash firmware → `presence.enabled=1` in MT → verify → calibrate. The MQTT contract and MT
side are sensor-agnostic.

**Differs by setup:**
- **Different board/sensor:** change `esp32: variant:` and the LD2410 **UART pins** to match your
  board (we pulled the MSR-2's from Apollo's official config). The topic-publishing automations stay
  the same.
- **Defender / space-in-path gotchas:** environment-specific — you may not hit them.
- **Broker IP, COM port, Wi-Fi SSID, desk-zone distance:** always your own values.

## Extending

- **Phone proximity (BLE):** add `ble_presence`/`ble_rssi` per phone (commented section at the bottom
  of [msr2-presence.yaml](msr2-presence.yaml)), publishing `mt/presence/ble/<id>/presence` + `/rssi`,
  and register the phones in `presence.config`. iOS/Android randomize their BLE MAC, so match by
  **IRK** — see [presence-routing.md §7](presence-routing.md). Needs a one-time IRK capture
  (Linux/BlueZ or Home Assistant).
- **Presence-triggered agent actions:** the broker fires a `RemoteModeChanged` event on every flip —
  the natural hook to trigger automation (e.g. "summarize what finished while away when the user
  returns"). Not wired yet; a clean next feature.
- **Physical indicator (LED):** drive an RGB LED off the sensor's GPIO header mirroring the state
  (green=present, off/red=away). The schema reserves `mt/presence/led/state` for this.
