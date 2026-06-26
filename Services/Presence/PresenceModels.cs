using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTerminal.Services.Presence
{
    /// <summary>
    /// The three presence states the machine models. v1 routing collapses Nearby → AtDesk
    /// (desktop-only) per the Owner decision; the schema/contract is in docs/presence-routing.md.
    /// </summary>
    public enum PresenceState
    {
        /// <summary>mmWave reports someone at the desk zone. Routes to desktop.</summary>
        AtDesk,

        /// <summary>mmWave absent but a registered phone's BLE is still in range. v1: routes as AtDesk.</summary>
        Nearby,

        /// <summary>mmWave absent and no registered phone in range (or BLE degraded). Routes to phone push.</summary>
        Away,
    }

    /// <summary>One registered phone in the presence config (item [4] identity layer).</summary>
    public sealed class PhoneConfig
    {
        /// <summary>Stable slug matching the MQTT topic segment, e.g. "johns-pixel".</summary>
        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = "";

        /// <summary>Human-friendly label for UI/logs.</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>Calibrated per-device RSSI threshold in dBm; readings ≥ this count as in-range.</summary>
        [JsonPropertyName("rssiThreshold")]
        public int RssiThreshold { get; set; } = -75;

        /// <summary>"ios" | "android" — informational; the routing is OS-agnostic.</summary>
        [JsonPropertyName("os")]
        public string Os { get; set; } = "";
    }

    /// <summary>MQTT connection parameters for the presence adapter (all config-driven).</summary>
    public sealed class PresenceMqttConfig
    {
        /// <summary>Broker host. Default loopback for a dedicated local Mosquitto.</summary>
        [JsonPropertyName("host")]
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Broker TCP port.</summary>
        [JsonPropertyName("port")]
        public int Port { get; set; } = 1883;

        /// <summary>Optional username; null/empty connects anonymously.</summary>
        [JsonPropertyName("username")]
        public string Username { get; set; }

        /// <summary>Optional password.</summary>
        [JsonPropertyName("password")]
        public string Password { get; set; }

        /// <summary>Topic prefix the sensor publishes under (default "mt/presence").</summary>
        [JsonPropertyName("topicPrefix")]
        public string TopicPrefix { get; set; } = "mt/presence";

        /// <summary>Subscription QoS (0/1/2; default 1 at-least-once).</summary>
        [JsonPropertyName("qos")]
        public int Qos { get; set; } = 1;
    }

    /// <summary>
    /// Presence feature config. Persisted as COMPACT (single-line) JSON under the SettingsService
    /// key <see cref="SettingKey"/> — SettingsService stores key=value lines and forbids newlines
    /// in values, so this is NEVER serialized indented. <see cref="EnabledKey"/> ("1"/"0") gates
    /// whether the adapter runs at all (default off, so it never hijacks the manual remoteMode pill
    /// until the Owner opts in and calibrates).
    /// </summary>
    public sealed class PresenceConfig
    {
        /// <summary>SettingsService key holding the compact JSON config blob.</summary>
        public const string SettingKey = "presence.config";

        /// <summary>SettingsService key gating the adapter ("1" = on).</summary>
        public const string EnabledKey = "presence.enabled";

        /// <summary>MQTT connection parameters.</summary>
        [JsonPropertyName("mqtt")]
        public PresenceMqttConfig Mqtt { get; set; } = new PresenceMqttConfig();

        /// <summary>Hysteresis window (seconds) a desired state must persist before it commits.</summary>
        [JsonPropertyName("debounceSeconds")]
        public double DebounceSeconds { get; set; } = 5;

        /// <summary>RSSI/presence age (seconds) beyond which a phone signal is "stale".</summary>
        [JsonPropertyName("bleStaleSeconds")]
        public double BleStaleSeconds { get; set; } = 30;

        /// <summary>Registered phones tracked for the Nearby distinction.</summary>
        [JsonPropertyName("phones")]
        public List<PhoneConfig> Phones { get; set; } = new List<PhoneConfig>();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        /// <summary>Serialize to compact single-line JSON safe for SettingsService storage.</summary>
        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

        /// <summary>Parse from JSON; returns defaults (never throws) on null/corrupt input.</summary>
        public static PresenceConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new PresenceConfig();
            try
            {
                return JsonSerializer.Deserialize<PresenceConfig>(json, JsonOpts) ?? new PresenceConfig();
            }
            catch (JsonException)
            {
                return new PresenceConfig();
            }
        }

        /// <summary>
        /// Load the config from SettingsService (or defaults) and clamp to sane ranges so a
        /// corrupt/hostile blob can't disable debounce or set an absurd staleness window.
        /// </summary>
        public static PresenceConfig Load(SettingsService settings)
        {
            var cfg = FromJson(settings?.Get(SettingKey));
            if (cfg.Mqtt == null) cfg.Mqtt = new PresenceMqttConfig();
            if (cfg.Phones == null) cfg.Phones = new List<PhoneConfig>();
            if (cfg.DebounceSeconds < 0) cfg.DebounceSeconds = 0;
            if (cfg.DebounceSeconds > 300) cfg.DebounceSeconds = 300;
            if (cfg.BleStaleSeconds < 1) cfg.BleStaleSeconds = 1;
            if (cfg.BleStaleSeconds > 3600) cfg.BleStaleSeconds = 3600;
            return cfg;
        }

        /// <summary>True when the adapter is enabled (presence.enabled == "1").</summary>
        public static bool IsEnabled(SettingsService settings) => settings?.Get(EnabledKey) == "1";
    }

    /// <summary>A committed presence transition, carried by <see cref="PresenceStateMachine.StateChanged"/>.</summary>
    public sealed class PresenceTransition
    {
        /// <summary>State before the transition.</summary>
        public PresenceState OldState { get; }

        /// <summary>State after the transition.</summary>
        public PresenceState NewState { get; }

        /// <summary>True if BLE was degraded (stale/unavailable) when this committed.</summary>
        public bool Degraded { get; }

        /// <summary>Creates a transition record.</summary>
        public PresenceTransition(PresenceState oldState, PresenceState newState, bool degraded)
        {
            OldState = oldState;
            NewState = newState;
            Degraded = degraded;
        }
    }

    /// <summary>
    /// Maps a presence state to MT's binary remoteMode gate (MessageBroker.SetRemoteMode).
    /// remoteMode == true means "push to phone" (away from desk). v1 collapses Nearby → AtDesk
    /// per the Owner decision; switching to true 3-state routing later is a change to THIS method only.
    /// </summary>
    public static class PresenceRouting
    {
        /// <summary>Returns the remoteMode value (true = phone push) for a presence state.</summary>
        public static bool ToRemoteMode(PresenceState state)
        {
            switch (state)
            {
                case PresenceState.Away:
                    return true;            // away from desk → phone push
                case PresenceState.AtDesk:
                case PresenceState.Nearby:  // v1: Nearby behaves as At desk (desktop only)
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The single seam the presence adapter uses to drive MT's binary desk/away gate. MessageBroker
    /// already exposes <c>IsRemoteMode</c>/<c>SetRemoteMode</c> and implements this interface, so the
    /// adapter depends on the small contract instead of the 5k-line broker — which keeps the routing
    /// path unit-testable with a fake sink (no DB/broker construction in tests).
    /// </summary>
    public interface IRemoteModeSink
    {
        /// <summary>Current remoteMode value (true = phone-push mode).</summary>
        bool IsRemoteMode { get; }

        /// <summary>Set remoteMode (true = away/phone push, false = at desk/desktop). Idempotent.</summary>
        void SetRemoteMode(bool enabled);
    }

    /// <summary>
    /// Phone identity layer (item [4]) abstracting iOS + Android behind one OS-agnostic lookup. The
    /// router never cares which OS a phone is — registration is config-driven (deviceId + per-device
    /// RSSI threshold), and this normalizes the deviceId so a topic slug ("Johns_Pixel") resolves to
    /// the configured phone ("johns-pixel") despite case/separator drift.
    /// </summary>
    public sealed class PhoneRegistry
    {
        private readonly Dictionary<string, PhoneConfig> _byId;

        /// <summary>Builds a registry from configured phones, canonicalizing each DeviceId.</summary>
        public PhoneRegistry(IEnumerable<PhoneConfig> phones)
        {
            _byId = new Dictionary<string, PhoneConfig>(StringComparer.Ordinal);
            if (phones == null)
                return;
            foreach (var p in phones)
            {
                if (p == null)
                    continue;
                var id = NormalizeId(p.DeviceId);
                if (string.IsNullOrEmpty(id))
                    continue;
                p.DeviceId = id;     // canonicalize in place so downstream lookups match
                _byId[id] = p;       // last registration wins on duplicate ids
            }
        }

        /// <summary>Number of registered phones.</summary>
        public int Count => _byId.Count;

        /// <summary>The registered phones (canonical DeviceId).</summary>
        public IEnumerable<PhoneConfig> Phones => _byId.Values;

        /// <summary>Resolve a raw (topic) deviceId to its registered config.</summary>
        public bool TryResolve(string rawDeviceId, out PhoneConfig phone) =>
            _byId.TryGetValue(NormalizeId(rawDeviceId), out phone);

        /// <summary>True if the raw deviceId resolves to a registered phone.</summary>
        public bool IsRegistered(string rawDeviceId) => _byId.ContainsKey(NormalizeId(rawDeviceId));

        /// <summary>
        /// Canonical slug for a deviceId: trim, lowercase, map space/underscore/slash to '-',
        /// drop other illegal chars, collapse repeat hyphens, trim leading/trailing hyphens.
        /// </summary>
        public static string NormalizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var s = raw.Trim().ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            char prev = '\0';
            foreach (var ch in s)
            {
                char c;
                if (ch == ' ' || ch == '_' || ch == '/')
                    c = '-';
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')
                    c = ch;
                else
                    continue;
                if (c == '-' && prev == '-')
                    continue;
                sb.Append(c);
                prev = c;
            }
            return sb.ToString().Trim('-');
        }
    }

    /// <summary>
    /// Config-driven registration + persistence for the presence feature (item [4]). Loads/saves the
    /// <see cref="PresenceConfig"/> compact-JSON blob via SettingsService and offers add/remove/enable
    /// helpers so a phone can be registered with a stable id + calibrated RSSI threshold from any
    /// surface (settings UI, REST, or a console) without each caller re-implementing the JSON round-trip.
    /// </summary>
    public static class PresenceConfigStore
    {
        /// <summary>Load the current config (clamped defaults; never throws).</summary>
        public static PresenceConfig Load(SettingsService settings) => PresenceConfig.Load(settings);

        /// <summary>Persist the config as compact JSON under the SettingsService key.</summary>
        public static void Save(SettingsService settings, PresenceConfig config)
        {
            if (settings == null || config == null)
                return;
            settings.Set(PresenceConfig.SettingKey, config.ToJson());
        }

        /// <summary>Enable/disable the adapter (persisted; takes effect on next adapter Start).</summary>
        public static void SetEnabled(SettingsService settings, bool enabled) =>
            settings?.Set(PresenceConfig.EnabledKey, enabled ? "1" : "0");

        /// <summary>
        /// Register (or update) a phone by stable id with a calibrated RSSI threshold. Returns the
        /// stored entry. The deviceId is canonicalized; a matching existing entry is updated in place.
        /// </summary>
        public static PhoneConfig RegisterPhone(SettingsService settings, string deviceId, string label, int rssiThreshold, string os)
        {
            var id = PhoneRegistry.NormalizeId(deviceId);
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("deviceId is required", nameof(deviceId));

            var cfg = Load(settings);
            var existing = cfg.Phones.Find(p => PhoneRegistry.NormalizeId(p.DeviceId) == id);
            if (existing == null)
            {
                existing = new PhoneConfig { DeviceId = id };
                cfg.Phones.Add(existing);
            }

            existing.DeviceId = id;
            if (label != null) existing.Label = label;
            // Clamp to a sane dBm range (BLE RSSI is negative; 0 = touching, -100 ≈ out of range).
            existing.RssiThreshold = Math.Max(-120, Math.Min(0, rssiThreshold));
            if (os != null) existing.Os = os;

            Save(settings, cfg);
            return existing;
        }

        /// <summary>Remove a registered phone by id. Returns true if one was removed.</summary>
        public static bool UnregisterPhone(SettingsService settings, string deviceId)
        {
            var id = PhoneRegistry.NormalizeId(deviceId);
            if (string.IsNullOrEmpty(id))
                return false;
            var cfg = Load(settings);
            int removed = cfg.Phones.RemoveAll(p => PhoneRegistry.NormalizeId(p.DeviceId) == id);
            if (removed > 0)
                Save(settings, cfg);
            return removed > 0;
        }
    }
}
