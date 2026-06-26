# Presence routing — MQTT broker setup (dedicated Mosquitto)

**Decision (Owner, task 9f9c3141):** stand up a **dedicated Mosquitto** broker rather than reuse an
existing one — there is no Home Assistant / shared broker in the loop. MT's Presence Adapter is
broker-agnostic (config-driven host/port/creds), so this can later be repointed at any broker by
changing `presence.config`.

This guide stands the broker up on the **same Windows machine as MT** (loopback), which is the
simplest secure default: nothing is exposed off-box.

> Companion docs: the topic/payload contract is in [presence-routing.md](presence-routing.md); the
> MSR-2 sensor config starter is in [presence/msr2-presence.yaml](presence/msr2-presence.yaml).

---

## 1. Install Mosquitto (Windows)

Any one of:

```powershell
winget install EclipseFoundation.Mosquitto
# or
choco install mosquitto
# or download the installer from https://mosquitto.org/download/
```

The installer typically lands it at `C:\Program Files\mosquitto\`, with `mosquitto.exe`,
`mosquitto_pub.exe`, and `mosquitto_sub.exe`.

---

## 2. Broker config

For a **loopback-only dev broker** (MT + the MSR-2 both reach it on the LAN), create
`mosquitto.conf`:

```conf
# --- mt-presence mosquitto.conf ---
persistence true
persistence_location C:\ProgramData\mosquitto\

# Loopback listener for MT (same machine).
listener 1883 127.0.0.1
allow_anonymous true

# LAN listener so the MSR-2 (on Wi-Fi) can publish. Comment out if the sensor
# runs on this same host or you bridge it differently.
listener 1883 0.0.0.0
```

> ⚠️ **Security:** `allow_anonymous true` + a `0.0.0.0` listener means anything on your LAN can
> publish presence topics (and thus flip your desk/away state). For a home LAN that's usually fine.
> To lock it down, drop the `0.0.0.0` listener (loopback only) **or** add auth:
>
> ```conf
> allow_anonymous false
> password_file C:\ProgramData\mosquitto\passwd
> ```
> Create the file with `mosquitto_passwd -c C:\ProgramData\mosquitto\passwd mt`, then put the same
> username/password into `presence.config` (`mqtt.username`/`mqtt.password`).

Run it (foreground, verbose — good for first bring-up):

```powershell
& "C:\Program Files\mosquitto\mosquitto.exe" -c .\mosquitto.conf -v
```

Or install it as a Windows service (`mosquitto install`) so it survives reboots.

---

## 3. Point MT at the broker + enable presence

MT reads two keys from `%APPDATA%\MultiTerminal\settings.txt` (flat `key=value`, **one line each**):

- `presence.enabled` — `1` turns the adapter on.
- `presence.config` — **compact** (single-line) JSON. **Optional** — if omitted, the adapter
  defaults to `127.0.0.1:1883`, prefix `mt/presence`, no phones (pure mmWave desk/away).

Minimal enable (mmWave-only, localhost broker):

```
presence.enabled=1
```

Full config with two phones (single line — do NOT wrap):

```
presence.config={"mqtt":{"host":"127.0.0.1","port":1883,"topicPrefix":"mt/presence","qos":1},"debounceSeconds":5,"bleStaleSeconds":30,"phones":[{"deviceId":"johns-iphone","label":"John's iPhone","rssiThreshold":-75,"os":"ios"},{"deviceId":"johns-pixel","label":"John's Pixel","rssiThreshold":-75,"os":"android"}]}
```

Restart MT (the adapter snapshots config + connects at REST-host startup). On connect you'll see in
the debug log (category **Presence**):

```
Presence adapter starting — broker 127.0.0.1:1883, prefix 'mt/presence', 2 phone(s).
Connected + subscribed to 'mt/presence/#'.
```

If presence is disabled you'll instead see: `presence.enabled != 1 — adapter not starting`.

---

## 4. Smoke test (no sensor needed)

With Mosquitto running and `presence.enabled=1`, simulate the sensor from another terminal and watch
MT's **Local/Remote** status-bar pill flip (and the Presence debug logs):

```powershell
$pub = "C:\Program Files\mosquitto\mosquitto_pub.exe"

# At the desk → pill should read Local (remoteMode off).
& $pub -t mt/presence/desk/occupancy -m ON

# Walk away, no phone configured/in-range → after debounce, pill flips Remote (phone push on).
& $pub -t mt/presence/desk/occupancy -m OFF

# With a phone registered, keep it "in range" to stay desktop (Nearby = At desk in v1):
& $pub -t mt/presence/ble/johns-pixel/rssi -m -55
& $pub -t mt/presence/desk/occupancy -m OFF      # stays Local (Nearby)
& $pub -t mt/presence/ble/johns-pixel/presence -m OFF   # phone gone → Remote (Away)
```

Watch the broker side too if you want to confirm publishes land:

```powershell
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -t "mt/presence/#" -v
```

Expected log trail on the MT side (Presence category): `Presence AtDesk → Away ⇒ remoteMode=True.`
etc. Because phone push paths already gate on `IsRemoteMode`, flipping to Remote re-enables phone
notifications exactly as the manual pill does today.

---

## 5. Going live with the real MSR-2

Once the broker is up and the smoke test passes, flash the MSR-2 (item [10]) using
[presence/msr2-presence.yaml](presence/msr2-presence.yaml) as a starting point — set your Wi-Fi +
broker IP, calibrate the desk-occupancy zone and per-phone RSSI thresholds, and confirm the sensor's
published topics match §2 of [presence-routing.md](presence-routing.md). Then run the end-to-end
handoff check (item [11]).

---

## 6. Security & ownership model

### Ownership: presence is authoritative when enabled (Owner decision)

When `presence.enabled=1`, **presence is the source of truth for remoteMode**. The manual
Local/Remote pill can still be toggled, but the adapter reconciles the gate against the current
presence state on **every evaluation (each ~1s tick + every sensor message)**, so a manual flip is
**re-corrected within about a second** — the pill is not a usable manual control while presence is
enabled. If you want manual control, set `presence.enabled=0` (and restart MT) so the adapter stops
driving the gate. The adapter logs `Presence is authoritative for remoteMode while enabled.` at
startup as a reminder.

If the sensor itself goes **offline** (its MQTT Last-Will fires `status=offline`), the adapter
distrusts the now-stale retained mmWave/BLE values and routes to **phone push** (fail-safe: don't
silently swallow notifications because a dead sensor's last reading said you were at the desk).

### MQTT trust boundary (the important one)

The adapter trusts **any** publisher on the broker: anyone who can publish to `mt/presence/#` can
flip your notification routing (desktop ↔ phone). The pipeline security gate rated this HIGH. For v1
the chosen posture is **guard + document + accept** for a single-user loopback/LAN setup:

- **Keep the broker loopback-only** (drop the `0.0.0.0` listener) whenever the sensor can reach MT
  another way, OR **require auth** (`allow_anonymous false` + `password_file` + per-topic ACLs) on any
  LAN-reachable broker.
- MT **warns loudly at startup** if the configured broker host is non-loopback AND no
  `mqtt.username` is set — that warning means the channel is spoofable; fix it before relying on it.
- **Cleartext transport (no TLS in v1):** the adapter connects over plain TCP MQTT. Even with
  `mqtt.username`/`mqtt.password`, a same-LAN attacker can passively sniff the credentials and replay
  spoofed (retained) presence messages. So for a LAN-reachable broker, auth alone is **not**
  sufficient hardening — the strongest v1 posture is a **loopback-only broker**. TLS support
  (`WithTlsOptions` + cert validation, required for non-loopback) is a deliberate post-v1 item; until
  then, treat any non-loopback broker as trusted-LAN-only.
- **Retained spoofing:** because `desk/occupancy`/`status`/`presence` are retained, a spoofed retained
  value persists across MT restarts. Clear a topic with `mosquitto_pub -r -n -t mt/presence/<topic>`.
- Hardening beyond v1 (not built): application-level signed/HMAC payloads so a bare broker publish
  can't drive routing. Revisit if this is ever exposed beyond a trusted single-user LAN.

### Other hardening in the adapter

- **Payload/topic size caps** (256 B / 512 chars) + **rate-limited** parse-failure logging, so a
  malicious publisher can't flood memory or the debug log with oversized/garbage messages.
- Unregistered `ble/<deviceId>` signals are ignored, bounding the in-memory signal map.

### Credentials at rest

`mqtt.password` is stored **in plaintext** in `%APPDATA%\MultiTerminal\settings.txt` (inside
`presence.config`). This is consistent with how MT already stores other secrets (`permissionRelay.apiKey`,
the VAPID private key in `push-config.json`). Protect that file with OS file permissions. Moving MT
secrets to DPAPI / Windows Credential Manager is a separate MT-wide hardening effort, not part of this
feature.
