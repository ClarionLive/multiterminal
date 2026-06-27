# Presence-aware notification routing — MQTT signal schema

This is the **contract** between the physical sensor (Apollo MSR-2 running ESPHome) and the
MultiTerminal *Presence Adapter*. The adapter subscribes to these topics on an MQTT broker,
normalizes them into internal presence events, and feeds a state machine that drives MT's
existing desk-vs-away gate (`MessageBroker.SetRemoteMode(bool)`).

> Task: `9f9c3141` — Presence-aware notification routing (mmWave + phone BLE → desktop/phone switch).
> Item [0] defines this schema. Items [1]–[6] consume it; item [10] flashes the matching ESPHome YAML.

---

## 1. Transport

- **Protocol:** MQTT 3.1.1 (plain TCP, default port `1883`; TLS optional later).
- **Broker:** a **dedicated Mosquitto** instance (Owner decision — no Home Assistant in the loop).
  The MT adapter is broker-agnostic: host / port / username / password / topic-prefix are all
  config-driven (see [§5](#5-mt-adapter-config)), so a future swap to an HA Mosquitto add-on or a
  shared broker is a config change only.
- **Direction:** sensors **publish**; MT **subscribes**. MT never publishes presence signals
  (it may publish the optional LED command topic, [§4](#4-optional-led-indicator-stretch-item-12)).
- **QoS:** `1` (at-least-once) for all subscriptions. Presence is idempotent — a duplicate
  "occupancy ON" is harmless because the state machine debounces.

---

## 2. Topic namespace

All topics live under a configurable prefix. **Default prefix: `mt/presence`.** Every topic below
is shown relative to that prefix.

| Signal | Topic (`<prefix>/…`) | Payload | Retained | Role |
|---|---|---|---|---|
| Desk occupancy (mmWave) | `desk/occupancy` | `ON` \| `OFF` | **yes** | PRIMARY gate |
| Per-phone BLE RSSI | `ble/<deviceId>/rssi` | int dBm as string, e.g. `-67` | no | SECONDARY (Nearby vs Away) |
| Per-phone BLE presence | `ble/<deviceId>/presence` | `ON` \| `OFF` | **yes** | SECONDARY (in-range vs gone) |
| Sensor availability (LWT) | `status` | `online` \| `offline` | **yes** | health / graceful degradation |

`<deviceId>` is a **stable slug** that must exactly match an entry in the MT phone registry
(see [§5](#5-mt-adapter-config)) — e.g. `johns-iphone`, `johns-pixel`. Use lowercase, hyphenated,
no spaces.

### Why these choices

- **`ON`/`OFF` strings, not JSON.** This is ESPHome's native default payload for `binary_sensor`
  states, so the MSR-2 YAML needs no custom templating. The adapter parses case-insensitively and
  also accepts `1`/`0`, `true`/`false` as aliases ([§3](#3-payload-parsing-rules)).
- **Occupancy + presence are retained; RSSI is not.** Retained means MT gets the *current* state
  the instant it connects/reconnects — no waiting for the next change. RSSI changes constantly and
  carries no useful "last value on reconnect," so it is published unretained to avoid stale reads.
- **`status` is the MQTT Last Will (LWT).** The sensor registers a will of `offline` on
  `<prefix>/status`; on a clean connect it publishes `online` (retained). If the sensor drops, the
  broker delivers `offline` automatically. MT uses this for **graceful degradation** (item [3]):
  when BLE alone is stale the state machine collapses to mmWave-only (desk vs away); when the sensor
  is fully **offline** the retained mmWave value is also distrusted and routing fails safe to **Away
  (phone push)** — a dead sensor's last `occupancy=ON` must not pin you "at desk" forever.

---

## 3. Payload parsing rules

The adapter normalizes loosely so the ESPHome side can stay simple:

| Field | Accepted (case-insensitive) | Normalized to |
|---|---|---|
| occupancy / presence | `ON`, `1`, `true`, `yes` | `true` |
| occupancy / presence | `OFF`, `0`, `false`, `no` | `false` |
| availability | `online`, `1`, `true` | online |
| availability | `offline`, `0`, `false` | offline |
| rssi | any string parseable as an integer (e.g. `-67`, `-70`) | `int` dBm; unparseable → ignored + logged |

**Staleness:** the adapter timestamps every RSSI message. If no RSSI for a registered device has
arrived within the configured `bleStaleSeconds` (default `30`), that device is treated as
**stale** and excluded from the Nearby decision — same effect as `presence=OFF` for that device.
If *all* registered devices are stale (or `status=offline`), BLE is considered unavailable and the
machine degrades to mmWave-only.

---

## 4. Optional LED indicator (stretch, item [12])

If the MSR-2 GPIO header drives an RGB strip, MT *may* publish a desired-color command so the LED
mirrors the presence state. This is the only topic MT publishes to.

| Topic (`<prefix>/…`) | Payload | Retained | Meaning |
|---|---|---|---|
| `led/state` | `at_desk` \| `nearby` \| `away` \| `off` | yes | physical presence indicator |

Suggested mapping: `at_desk` → green, `nearby` → amber, `away` → off/red. The ESPHome YAML owns
the actual color + the **300 mA @ 5 V** budget; MT only emits the abstract state.

---

## 5. MT adapter config

Lives in MT settings (exact storage TBD in item [1]). Shape:

```jsonc
{
  "presence": {
    "mqtt": {
      "host": "127.0.0.1",
      "port": 1883,
      "username": null,
      "password": null,
      "topicPrefix": "mt/presence",
      "qos": 1
    },
    "debounceSeconds": 5,        // hysteresis on state transitions (item [2])
    "bleStaleSeconds": 30,       // RSSI age beyond which a device is "stale" (item [3])
    "phones": [
      { "deviceId": "johns-pixel",  "label": "John's Pixel",  "rssiThreshold": -75, "os": "android" },
      { "deviceId": "johns-iphone", "label": "John's iPhone", "rssiThreshold": -75, "os": "ios" }
    ]
  }
}
```

- A phone is **in range** when its latest (non-stale) RSSI ≥ `rssiThreshold` **or** its
  `presence` topic is `ON`. `rssiThreshold` is calibrated per device (item [10]).
- `debounceSeconds` and `bleStaleSeconds` are global; thresholds are per device.

---

## 6. Example ESPHome wiring (target for item [10])

Illustrative only — final YAML + calibration happen in item [10] with the real MSR-2.

```yaml
mqtt:
  broker: 127.0.0.1
  port: 1883
  topic_prefix: mt/presence        # → mt/presence/...
  birth_message:
    topic: mt/presence/status
    payload: online
    retain: true
  will_message:
    topic: mt/presence/status
    payload: offline
    retain: true

binary_sensor:
  - platform: ld2410                # MSR-2 mmWave radar
    has_target:
      name: "Desk occupancy"
      # ESPHome publishes ON/OFF to: mt/presence/binary_sensor/desk-occupancy/state
      # Use an mqtt: state_topic override or on_state→mqtt.publish to hit mt/presence/desk/occupancy
```

> Note: ESPHome's auto-generated component topics (`<prefix>/binary_sensor/<name>/state`) do **not**
> match the flat schema in §2. The YAML must override `state_topic` (or use an explicit
> `mqtt.publish` action) so the published topics are exactly `mt/presence/desk/occupancy`,
> `mt/presence/ble/<deviceId>/rssi`, etc. This is captured as a calibration detail for item [10].

---

## 7. Phone identity & defeating MAC randomization (item [4]/[5])

The adapter tracks phones by a **stable `deviceId` slug**, never by MAC — because iOS (and modern
Android) rotate their BLE MAC roughly every 15 minutes, so a MAC-based tracker loses the phone on
every rotation. The ESPHome side is responsible for resolving the rotating address back to a stable
identity and publishing under the phone's `deviceId`. Whichever method you pick, the output is the
same two topics from §2: `ble/<deviceId>/presence` (ON/OFF) and optionally `ble/<deviceId>/rssi`.

### iOS — use an IRK (Identity Resolving Key). PRIMARY, works with NO Home Assistant.

iOS advertises a **Resolvable Private Address** derived from a per-device IRK. ESPHome can match the
rotating address back to the phone using that key — no HA, no app kept in the foreground:

```yaml
esp32_ble_tracker:
binary_sensor:
  - platform: ble_presence
    irk: 1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d   # the phone's 16-byte IRK (hex)
    name: "Johns iPhone present"
    # Override the auto topic so it lands on the flat schema:
    # state_topic: mt/presence/ble/johns-iphone/presence
```

**Getting the IRK (one-time):**
- Pair the iPhone to a Linux box running BlueZ, then read the IRK from
  `/var/lib/bluetooth/<adapter>/<device>/info` under `[IdentityResolvingKey] Key=…`, **or**
- If you later run Home Assistant, the *Private BLE Device* integration extracts and shows the IRK, **or**
- Use any BlueZ `bluetoothctl` pairing flow and dump the key.

The IRK does not rotate, so once captured the match is permanent. This is the recommended iOS path
for this no-HA setup.

> iBeacon alternative (NOT recommended for iOS): an iOS app can broadcast a fixed iBeacon UUID that
> ESPHome matches via `ibeacon_uuid:`, but iOS stops/changes beacon advertising when the app is
> backgrounded, so background presence is unreliable. Prefer IRK.

### Android — stable iBeacon broadcast (no HA), or IRK where supported.

Modern Android also randomizes its MAC, so don't track by MAC. Two options:
- **iBeacon broadcaster app** (e.g. a beacon-simulator app) emitting a fixed UUID; match in ESPHome
  with `platform: ble_presence` + `ibeacon_uuid:`. Android keeps broadcasting in the background more
  reliably than iOS.
- Some Android phones expose a resolvable address with an IRK too — same `irk:` approach as iOS if
  you can extract the key.

### Why this stays OS-agnostic downstream

The `PhoneRegistry` (Services/Presence) is the identity layer: each phone is registered once with a
canonical `deviceId` + a calibrated `rssiThreshold`, and the state machine resolves incoming
`ble/<deviceId>/…` topics through it. The `os` field in the config is **informational only** — the
router treats an iPhone and a Pixel identically once their `ble_presence`/`rssi` topics arrive. All
the OS-specific work lives in the ESPHome YAML (IRK vs iBeacon), not in MT.

## 8. Quick reference (copy/paste)

```
mt/presence/desk/occupancy          ON|OFF        retained   PRIMARY mmWave gate
mt/presence/ble/<deviceId>/rssi     "-67"         not-ret.   per-phone signal strength (dBm)
mt/presence/ble/<deviceId>/presence ON|OFF        retained   per-phone in-range flag
mt/presence/status                  online|offline retained  sensor LWT (degradation trigger)
mt/presence/led/state               at_desk|nearby|away|off  retained  (MT→sensor, stretch)
```
