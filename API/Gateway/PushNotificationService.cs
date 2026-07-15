using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Web Push (VAPID) sender for the MultiRemote phone gateway (task ca6c5344, item [7]).
    /// Ported from the standalone MultiRemote's PushNotificationService. VAPID keys + the
    /// push subscription list live in push-config.json under the resolved per-install data
    /// dir (<see cref="GatewayPaths"/>) — NOT the CWD the standalone used. Copy the existing
    /// push-config.json into that dir to keep Apple/Web Push subscriptions + VAPID identity
    /// alive across the fold-in; otherwise fresh keys are generated and phones must re-subscribe.
    /// </summary>
    public class PushNotificationService
    {
        private readonly string _configPath;
        private readonly string _vapidSubject;
        // Stored as SubscriptionData (not WebPush.PushSubscription) so each entry carries its
        // DeviceId — needed to dedup ghost subscriptions by device (task ca6c5344, item [11],
        // Finding 3). The WebPush.PushSubscription is built on demand at send time.
        private readonly List<SubscriptionData> _subscriptions = new List<SubscriptionData>();
        private readonly object _subsLock = new object();
        private readonly object _saveLock = new object();
        private VapidDetails _vapidDetails;
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(ILogger<PushNotificationService> logger, IConfiguration config)
        {
            _logger = logger;
            _configPath = Path.Combine(GatewayPaths.DataDir(config), "push-config.json");
            // VapidSubject resolves Multi-Connect settings-first → appsettings fallback (task 642c14e3).
            // VAPID KEY generation stays automatic (LoadOrGenerateVapid) — only the subject is configurable.
            // The default must NOT be a localhost-domain mailto: Apple validates the JWT sub claim and
            // rejects every send with 403 BadJwtToken for localhost-style domains (task 8fc66298 item [4]
            // live probe: real mailto domains and https URLs pass, mailto:*@localhost* fails). The old
            // default "mailto:admin@localhost" silently broke ALL push on Apple devices for any install
            // that never configured a subject.
            _vapidSubject = MultiConnectConfig.Resolve(
                MultiTerminal.Services.SettingsService.Default.GetMultiConnectVapidSubject(),
                config.GetValue<string>("MultiRemote:VapidSubject")) ?? "https://github.com/peterparker57/MultiTerminal";
            if (_vapidSubject.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Configured VAPID subject '{Subject}' contains a localhost domain — Apple rejects these with 403 BadJwtToken and push will not deliver. Set a real mailto: address or https: URL in Multi-Connect / MultiRemote:VapidSubject.",
                    _vapidSubject);
            }
            LoadOrGenerateVapid();
            LoadSubscriptions();
        }

        public string PublicKey => _vapidDetails?.PublicKey ?? "";

        /// <summary>Current number of stored push subscriptions (thread-safe).</summary>
        public int SubscriptionCount
        {
            get { lock (_subsLock) { return _subscriptions.Count; } }
        }

        private void LoadOrGenerateVapid()
        {
            var fileExisted = File.Exists(_configPath);
            if (fileExisted)
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<PushConfig>(json);
                    if (cfg != null && !string.IsNullOrEmpty(cfg.PublicKey))
                    {
                        _vapidDetails = new VapidDetails(_vapidSubject, cfg.PublicKey, cfg.PrivateKey);
                        _logger.LogInformation("Loaded VAPID keys from {Path}", _configPath);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load VAPID config from {Path}", _configPath);
                }

                // The file existed but was unusable (corrupt JSON or missing PublicKey). Do NOT
                // silently overwrite it — it may hold recoverable subscriptions. Back it up first
                // (pipeline Run-1 debugger/cross-model finding: a clobber strands every phone).
                try
                {
                    var backup = _configPath + ".corrupt-" + DateTime.UtcNow.Ticks + ".bak";
                    File.Copy(_configPath, backup, overwrite: false);
                    _logger.LogWarning(
                        "push-config.json at {Path} was unusable; backed up to {Backup} before regenerating VAPID keys. Existing phone subscriptions are now invalid — devices must re-subscribe.",
                        _configPath, backup);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not back up the unusable push-config.json before regenerating");
                }
            }
            else
            {
                // No config at all. On an UPGRADE this means the standalone's push-config.json was
                // not migrated, so a fresh VAPID identity is about to be minted and existing phones
                // will silently stop receiving push until they re-subscribe (pipeline Run-1 finding).
                _logger.LogWarning(
                    "No push-config.json at {Path} — generating a FRESH VAPID identity. If this is an upgrade from the standalone MultiRemote, copy its push-config.json here FIRST, otherwise every phone must re-subscribe.",
                    _configPath);
            }

            var keys = VapidHelper.GenerateVapidKeys();
            _vapidDetails = new VapidDetails(_vapidSubject, keys.PublicKey, keys.PrivateKey);

            var newCfg = new PushConfig
            {
                PublicKey = keys.PublicKey,
                PrivateKey = keys.PrivateKey,
                Subscriptions = new List<SubscriptionData>(),
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(newCfg, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Generated new VAPID keys, saved to {Path}", _configPath);
        }

        private void LoadSubscriptions()
        {
            if (!File.Exists(_configPath))
                return;
            try
            {
                var json = File.ReadAllText(_configPath);
                var cfg = JsonSerializer.Deserialize<PushConfig>(json);
                if (cfg?.Subscriptions != null)
                {
                    lock (_subsLock)
                    {
                        foreach (var sub in cfg.Subscriptions)
                            _subscriptions.Add(sub);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load push subscriptions from {Path}", _configPath);
            }
        }

        public void AddSubscription(string endpoint, string p256dh, string auth, string deviceId = null)
        {
            int prunedGhosts = 0;
            lock (_subsLock)
            {
                // Dedup-by-device (item [11], Finding 3): Apple 201-accepts dead subscriptions
                // forever, so a re-subscribing device mints a NEW endpoint while the old ghost
                // lingers (inflating successCount and masking the live device). When the client
                // supplies a stable deviceId, drop any prior subscription for that same device
                // before adding the fresh one. Older clients that don't send a deviceId fall back
                // to endpoint-only dedup (unchanged behaviour).
                if (!string.IsNullOrWhiteSpace(deviceId))
                    prunedGhosts = _subscriptions.RemoveAll(s => s.DeviceId == deviceId && s.Endpoint != endpoint);

                if (_subscriptions.Any(s => s.Endpoint == endpoint))
                {
                    // Endpoint already known. Refresh its keys/deviceId in case they changed, but
                    // don't add a duplicate.
                    var existing = _subscriptions.First(s => s.Endpoint == endpoint);
                    existing.P256dh = p256dh;
                    existing.Auth = auth;
                    if (!string.IsNullOrWhiteSpace(deviceId))
                        existing.DeviceId = deviceId;
                }
                else
                {
                    _subscriptions.Add(new SubscriptionData
                    {
                        Endpoint = endpoint,
                        P256dh = p256dh,
                        Auth = auth,
                        DeviceId = string.IsNullOrWhiteSpace(deviceId) ? "" : deviceId,
                    });
                }
            }
            SaveSubscriptions();
            if (prunedGhosts > 0)
                _logger.LogInformation("Pruned {Count} ghost subscription(s) for device {DeviceId} before re-subscribe", prunedGhosts, deviceId);
            _logger.LogInformation("Push subscription added: {Endpoint}", endpoint.Substring(0, Math.Min(50, endpoint.Length)));
        }

        private void SaveSubscriptions()
        {
            lock (_saveLock)
            {
                try
                {
                    List<SubscriptionData> snapshot;
                    lock (_subsLock)
                    {
                        // Deep-copy (not ToList of references) so a concurrent in-place key refresh in
                        // AddSubscription can't persist a torn p256dh/auth pair to disk (pipeline Run-5).
                        snapshot = _subscriptions.Select(s => new SubscriptionData
                        {
                            Endpoint = s.Endpoint,
                            P256dh = s.P256dh,
                            Auth = s.Auth,
                            DeviceId = s.DeviceId,
                        }).ToList();
                    }

                    var json = File.Exists(_configPath) ? File.ReadAllText(_configPath) : "{}";
                    var cfg = JsonSerializer.Deserialize<PushConfig>(json) ?? new PushConfig();
                    cfg.Subscriptions = snapshot;
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to save subscriptions: {Error}", ex.Message);
                }
            }
        }

        public async Task SendToAll(string title, string body, string notificationType = null, string agentName = null)
        {
            await SendToAllWithResult(title, body, notificationType, agentName);
        }

        public async Task<PushSendResult> SendToAllWithResult(string title, string body, string notificationType = null, string agentName = null)
        {
            if (_vapidDetails == null)
                return new PushSendResult { Error = "No VAPID keys", SubscriptionCount = 0 };

            // Snapshot by VALUE inside the lock: SubscriptionData is a mutable reference type and a
            // concurrent AddSubscription can refresh an existing entry's P256dh/Auth in place. Reading
            // the live object outside the lock (in the await send loop) could observe a torn key pair
            // (new p256dh + old auth) and fail an otherwise-healthy device (pipeline Run-5 debugger).
            // We copy the three key strings; the original SubscriptionData ref is retained only so the
            // 410-Gone removal below can target the exact instance.
            List<(string Endpoint, string P256dh, string Auth, SubscriptionData Ref)> snapshot;
            lock (_subsLock)
            {
                if (_subscriptions.Count == 0)
                    return new PushSendResult { Error = "No subscriptions", SubscriptionCount = 0 };
                snapshot = _subscriptions.Select(s => (s.Endpoint, s.P256dh, s.Auth, s)).ToList();
            }

            using var client = new WebPushClient();
            var payload = JsonSerializer.Serialize(new { title, body, notification_type = notificationType, agent_name = agentName });
            var results = new List<object>();

            // Count ACTUAL delivery outcomes — a non-zero subscription count does NOT mean any
            // device received the push (subs can all error / be pruned as 410 Gone). Callers
            // derive "delivered" from SuccessCount, not the subscription count (pipeline Run-2
            // cross-model finding: false-success logs/responses on stale-subscription failures).
            int successCount = 0;
            int errorCount = 0;

            var expired = new List<SubscriptionData>();
            foreach (var sub in snapshot)
            {
                // Per-send timeout: a single stale/slow subscription must not stall the whole
                // sequential send loop up to WebPushClient's ~100s HttpClient default and blow the
                // caller's forward budget (task ca6c5344 item [11]). Cancel the in-flight request at
                // 10s and record it as a delivery error so success/error accounting stays accurate.
                using var sendCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await client.SendNotificationAsync(new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth), payload, _vapidDetails, sendCts.Token);
                    successCount++;
                    results.Add(new { endpoint = sub.Endpoint.Substring(0, Math.Min(60, sub.Endpoint.Length)), status = "sent" });
                }
                catch (WebPushException ex)
                {
                    errorCount++;
                    results.Add(new { endpoint = sub.Endpoint.Substring(0, Math.Min(60, sub.Endpoint.Length)), status = "error", code = ex.StatusCode.ToString(), message = ex.Message });
                    // Prune dead subscriptions: 410 Gone is the spec signal, but Apple also reports
                    // permanently-invalid tokens as 400 {"reason":"BadDeviceToken"} (task 8fc66298
                    // item [4] live probe — a stale sub that never received a successful send).
                    // Match on the reason string, NOT bare 400: a generic 400 could be OUR payload
                    // bug, and pruning healthy devices on our own defect would silently unsubscribe
                    // everyone.
                    if (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                        (ex.StatusCode == System.Net.HttpStatusCode.BadRequest && ex.Message.Contains("BadDeviceToken", StringComparison.Ordinal)))
                        expired.Add(sub.Ref);
                }
                catch (OperationCanceledException)
                {
                    // Hit the 10s per-send cap — treat as a (recoverable) delivery failure, not a
                    // 410 Gone, so the subscription is kept for the next attempt rather than pruned.
                    errorCount++;
                    results.Add(new { endpoint = sub.Endpoint.Substring(0, Math.Min(60, sub.Endpoint.Length)), status = "error", code = "timeout", message = "send exceeded 10s" });
                }
                catch (Exception ex)
                {
                    errorCount++;
                    results.Add(new { endpoint = sub.Endpoint.Substring(0, Math.Min(60, sub.Endpoint.Length)), status = "error", code = "unknown", message = ex.Message });
                }
            }

            if (expired.Count > 0)
            {
                lock (_subsLock)
                {
                    foreach (var sub in expired)
                        _subscriptions.Remove(sub);
                }
                SaveSubscriptions();
            }

            int count;
            lock (_subsLock)
            {
                count = _subscriptions.Count;
            }
            return new PushSendResult
            {
                SubscriptionCount = count,
                SuccessCount = successCount,
                ErrorCount = errorCount,
                Results = results,
            };
        }
    }

    /// <summary>
    /// Outcome of a push send. <see cref="Delivered"/> is true only when at least one device
    /// actually accepted the push — callers must branch on this, not on subscription count,
    /// to avoid claiming success when every send failed (task ca6c5344, pipeline Run-2).
    /// </summary>
    public class PushSendResult
    {
        public int SubscriptionCount { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public string Error { get; set; }
        public List<object> Results { get; set; } = new List<object>();

        public bool Delivered => SuccessCount > 0;
    }

    public class PushConfig
    {
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public List<SubscriptionData> Subscriptions { get; set; } = new List<SubscriptionData>();
    }

    public class SubscriptionData
    {
        public string Endpoint { get; set; } = "";
        public string P256dh { get; set; } = "";
        public string Auth { get; set; } = "";

        /// <summary>
        /// Stable per-install identifier sent by the PWA (localStorage). Lets the server replace
        /// a device's prior (ghost) subscription on re-subscribe instead of accumulating dead ones
        /// (task ca6c5344, item [11], Finding 3). Empty for older clients that predate the field.
        /// LIMITATIONS: (1) pre-existing subs with an empty DeviceId are never collapsed by dedup —
        /// they clear only on 410 Gone. (2) The id is client-minted and not bound to the auth session,
        /// so two installs that share a cloned localStorage (e.g. browser-profile sync) collide on one
        /// DeviceId and a subscribe from one would evict the other's live subscription. Acceptable for a
        /// single-owner tool; binding DeviceId to the session is the follow-up hardening.
        /// </summary>
        public string DeviceId { get; set; } = "";
    }
}
