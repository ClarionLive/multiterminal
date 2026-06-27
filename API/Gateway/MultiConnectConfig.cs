using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MultiTerminal.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Settings-first → appsettings-fallback config resolver for the Multi-Connect feature
    /// (task 642c14e3). Written FRESH — there is no pre-existing settings-first wiring in the
    /// tree; every gateway consumer (port, auth, NotificationSecret, VapidSubject, relay
    /// BaseUrl/ApiKey) read <see cref="IConfiguration"/> directly. This routes them through a
    /// single rule so a per-install value in settings.txt wins, otherwise appsettings(.Local).json
    /// is used.
    ///
    /// CRITICAL (Devils-Advocate HIGH): the fallthrough test is null-OR-WHITESPACE, never just
    /// null. <c>SettingsService.Set(k, null)</c> historically stored "" and <c>Get</c> returned ""
    /// (not null); a naive <c>settings ?? config</c> would let an empty string SHADOW a real
    /// appsettings value and blank a working install. The setters in <see cref="SettingsService"/>
    /// also Remove() cleared keys so blanks never persist in the first place — this resolver is the
    /// second line of defence.
    /// </summary>
    public static class MultiConnectConfig
    {
        /// <summary>Bumped when the GET/POST /api/multi-connect/config JSON contract changes.</summary>
        public const string SchemaVersion = "1.0";

        // appsettings config keys these per-install settings fall back to.
        public const string CfgPort = "MultiRemote:Port";
        public const string CfgAuthUsername = "MultiRemote:Auth:Username";
        public const string CfgAuthPassword = "MultiRemote:Auth:Password";
        public const string CfgNotificationSecret = "MultiRemote:NotificationSecret";
        public const string CfgVapidSubject = "MultiRemote:VapidSubject";
        public const string CfgRelayBaseUrl = "MultiRemote:PermissionRelay:BaseUrl";
        public const string CfgRelayApiKey = "MultiRemote:PermissionRelay:ApiKey";

        /// <summary>
        /// Optional hook the tab/controller calls to re-apply restart-required fields (gateway
        /// port, VapidSubject, NotificationSecret, relay BaseUrl) without a full app relaunch.
        /// MainForm assigns this at startup (task 642c14e3, item 2). Null until wired.
        /// </summary>
        public static Func<Task> GatewayRestarter { get; set; }

        /// <summary>
        /// The core rule: returns <paramref name="settingsValue"/> when it is non-null and not
        /// whitespace, otherwise <paramref name="configFallback"/> (which may itself be null).
        /// </summary>
        public static string Resolve(string settingsValue, string configFallback)
            => string.IsNullOrWhiteSpace(settingsValue) ? configFallback : settingsValue;

        // ---- Shared appsettings fallback layer (for the :5050 controller) -------------------
        // The :5050 REST host does NOT load appsettings.Local.json, where the real per-install
        // phone secrets live. Build a dedicated layer here — appsettings.json + appsettings.Local.json
        // from the exe output dir — so GET /api/multi-connect/config reports the SAME effective
        // value + source the gateway resolves. Cached; refreshable for tests.
        private static readonly object _cfgLock = new object();
        private static IConfiguration _appsettings;

        /// <summary>
        /// appsettings.json + appsettings.Local.json (optional) loaded from the exe output dir.
        /// This mirrors the gateway's fallback sources for the MultiRemote keys.
        /// </summary>
        public static IConfiguration Appsettings
        {
            get
            {
                if (_appsettings != null) return _appsettings;
                lock (_cfgLock)
                {
                    if (_appsettings == null)
                    {
                        _appsettings = new ConfigurationBuilder()
                            .SetBasePath(AppContext.BaseDirectory)
                            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
                            .Build();
                    }
                    return _appsettings;
                }
            }
        }

        /// <summary>
        /// Resolves a settings value against the shared appsettings layer and reports the source:
        /// "settings" (per-install value wins), "appsettings" (fallback used), or "unset" (neither).
        /// Used by GET /config so the tab can show the user where the effective value comes from and
        /// never edit into a shadow.
        /// </summary>
        public static (string value, string source) ResolveWithSource(string settingsValue, string configKey)
        {
            if (!string.IsNullOrWhiteSpace(settingsValue))
                return (settingsValue, "settings");
            var cfg = string.IsNullOrEmpty(configKey) ? null : Appsettings[configKey];
            if (!string.IsNullOrWhiteSpace(cfg))
                return (cfg, "appsettings");
            return (null, "unset");
        }

        /// <summary>Reports whether a secret is set in settings or (as a fallback) in appsettings.</summary>
        public static (bool isSet, string source) ResolveSecretSource(bool settingsHasSecret, string configKey)
        {
            if (settingsHasSecret) return (true, "settings");
            var cfg = string.IsNullOrEmpty(configKey) ? null : Appsettings[configKey];
            if (!string.IsNullOrWhiteSpace(cfg)) return (true, "appsettings");
            return (false, "unset");
        }
    }
}
