using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Gateway;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Loopback-only configuration surface for the Multi-Connect feature (task 642c14e3, item 4).
    ///
    /// GET  /api/multi-connect/config          — effective per-install config; secrets reported as
    ///                                            is-set booleans (NEVER raw) + the source each value
    ///                                            resolves from; carries a stable schemaVersion.
    /// POST /api/multi-connect/config          — validates then persists via SettingsService; cleared
    ///                                            fields are removed (never stored as ""); 400 on bad
    ///                                            input with no partial write.
    /// GET  /api/multi-connect/tailscale-status — typed Tailscale probe for the tab "Detect" button.
    ///
    /// SECURITY: this controller is mounted ONLY on the :5050 loopback REST host. It is deliberately
    /// EXCLUDED from <c>GatewayControllerFeatureProvider</c> so it is NOT reachable on the :5100 phone
    /// gateway (which is exposed over Tailscale). Every action additionally rejects a non-loopback
    /// remote IP, and POST adds an Origin/CSRF guard mirroring <see cref="RemoteModeController"/>.
    /// </summary>
    [ApiController]
    [Route("api/multi-connect")]
    public class MultiConnectController : ControllerBase
    {
        private readonly SettingsService _settings;

        public MultiConnectController(SettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>GET /api/multi-connect/config — effective config (secrets as is-set booleans).</summary>
        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            if (!IsLoopbackRequest())
                return StatusCode(403, new { error = "Loopback only" });

            // CSRF/exfil defense: the :5050 host serves AllowAnyOrigin CORS, so a malicious page in
            // the victim's loopback browser could otherwise fetch this and read hostname/username/
            // phoneUrl/port/secret-is-set. The shared guard refuses a browser cross-origin fetch
            // while local tooling / the setup skill (HttpClient, no Origin) passes.
            if (!CrossOriginBrowserGuard.IsLocalNonBrowserCaller(Request))
                return StatusCode(403, new { error = "Origin not allowed" });

            // Gateway port: settings int → appsettings MultiRemote:Port → default 5100.
            int? settingsPort = _settings.GetMultiConnectGatewayPort();
            string cfgPort = MultiConnectConfig.Appsettings[MultiConnectConfig.CfgPort];
            int effectivePort;
            string portSource;
            if (settingsPort.HasValue) { effectivePort = settingsPort.Value; portSource = "settings"; }
            else if (int.TryParse(cfgPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cp)) { effectivePort = cp; portSource = "appsettings"; }
            else { effectivePort = SettingsService.DefaultMultiConnectGatewayPort; portSource = "default"; }

            // Tailscale serve port: settings → default 443 (no appsettings fallback).
            int? settingsServePort = _settings.GetMultiConnectTailscaleServePort();
            int effectiveServePort = settingsServePort ?? SettingsService.DefaultMultiConnectTailscaleServePort;
            string servePortSource = settingsServePort.HasValue ? "settings" : "default";

            // Tailscale enabled: settings → default false.
            bool? tsEnabled = _settings.GetMultiConnectTailscaleEnabled();
            bool effectiveEnabled = tsEnabled ?? false;
            string enabledSource = tsEnabled.HasValue ? "settings" : "default";

            var hostname = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectTailscaleHostname(), null);
            var username = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectPhoneAuthUsername(), MultiConnectConfig.CfgAuthUsername);
            var vapid = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectVapidSubject(), MultiConnectConfig.CfgVapidSubject);
            var relayBase = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectRelayBaseUrl(), MultiConnectConfig.CfgRelayBaseUrl);

            var phonePassword = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectPhoneAuthPassword(), MultiConnectConfig.CfgAuthPassword);
            var notifSecret = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectNotificationSecret(), MultiConnectConfig.CfgNotificationSecret);
            var relayApiKey = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectRelayApiKey(), MultiConnectConfig.CfgRelayApiKey);

            string phoneUrl = null;
            if (!string.IsNullOrWhiteSpace(hostname.value))
            {
                phoneUrl = effectiveServePort == 443
                    ? $"https://{hostname.value}"
                    : $"https://{hostname.value}:{effectiveServePort}";
            }

            return Ok(new
            {
                schemaVersion = MultiConnectConfig.SchemaVersion,
                gatewayPort = new { value = effectivePort, source = portSource },
                tailscaleEnabled = new { value = effectiveEnabled, source = enabledSource },
                tailscaleHostname = new { value = hostname.value, source = hostname.source },
                tailscaleServePort = new { value = effectiveServePort, source = servePortSource },
                phoneAuthUsername = new { value = username.value, source = username.source },
                phoneAuthPassword = new { isSet = phonePassword.isSet, source = phonePassword.source },
                notificationSecret = new { isSet = notifSecret.isSet, source = notifSecret.source },
                vapidSubject = new { value = vapid.value, source = vapid.source },
                relayBaseUrl = new { value = relayBase.value, source = relayBase.source },
                relayApiKey = new { isSet = relayApiKey.isSet, source = relayApiKey.source },
                phoneUrl,
            });
        }

        /// <summary>POST /api/multi-connect/config — validate, then persist (Remove on cleared fields).</summary>
        [HttpPost("config")]
        public async Task<IActionResult> PostConfig([FromBody] MultiConnectConfigRequest request)
        {
            if (!IsLoopbackRequest())
                return StatusCode(403, new { error = "Loopback only" });

            // CSRF defense: reuse the shared guard (Origin + Sec-Fetch-Site). A browser cross-origin
            // POST is refused; curl / the local tab / the setup skill (no Origin) passes.
            if (!CrossOriginBrowserGuard.IsLocalNonBrowserCaller(Request))
                return StatusCode(403, new { error = "Origin not allowed" });

            if (request == null)
                return BadRequest(new { error = "Missing request body" });

            // --- Validate EVERYTHING before writing anything (no partial write on 400) ----------
            // Field semantics: null = leave unchanged; "" = clear (Remove); value = set.
            int? parsedGatewayPort = null;
            if (IsSetValue(request.GatewayPort))
            {
                if (!TryParsePort(request.GatewayPort, out int gp))
                    return BadRequest(new { error = "gatewayPort must be an integer between 1 and 65535" });
                parsedGatewayPort = gp;
            }

            int? parsedServePort = null;
            if (IsSetValue(request.TailscaleServePort))
            {
                if (!TryParsePort(request.TailscaleServePort, out int sp))
                    return BadRequest(new { error = "tailscaleServePort must be an integer between 1 and 65535" });
                parsedServePort = sp;
            }

            if (IsSetValue(request.VapidSubject) && !IsValidVapidSubject(request.VapidSubject))
                return BadRequest(new { error = "vapidSubject must be a mailto: address or an http(s) URL" });

            if (IsSetValue(request.RelayBaseUrl) && !IsAbsoluteHttpUrl(request.RelayBaseUrl))
                return BadRequest(new { error = "relayBaseUrl must be an absolute http(s) URL" });

            // Snapshot the effective restart-required fields (gateway port, NotificationSecret,
            // VapidSubject, relay BaseUrl) BEFORE persisting so we can tell whether one actually
            // changed and the running gateway needs re-applying. Non-restart fields (hostname,
            // serve port, enabled — the setup skill's normal POST) never enter this signature.
            var beforeRestart = RestartRelevantEffective();

            // --- Persist (single batched save). Cleared fields delete their key via the setters. --
            _settings.BeginBatch();
            try
            {
                if (request.GatewayPort != null)
                    _settings.SetMultiConnectGatewayPort(parsedGatewayPort); // null when "" → clears
                if (request.TailscaleServePort != null)
                    _settings.SetMultiConnectTailscaleServePort(parsedServePort);
                if (request.TailscaleEnabled.HasValue)
                    _settings.SetMultiConnectTailscaleEnabled(request.TailscaleEnabled.Value);
                if (request.TailscaleHostname != null)
                    _settings.SetMultiConnectTailscaleHostname(request.TailscaleHostname);
                if (request.PhoneAuthUsername != null)
                    _settings.SetMultiConnectPhoneAuthUsername(request.PhoneAuthUsername);
                if (request.VapidSubject != null)
                    _settings.SetMultiConnectVapidSubject(request.VapidSubject);
                if (request.RelayBaseUrl != null)
                    _settings.SetMultiConnectRelayBaseUrl(request.RelayBaseUrl);

                // Secrets: null = unchanged (so an unsent/masked field never clobbers the stored
                // secret), "" = clear, value = set (DPAPI-protected by the setter).
                if (request.PhoneAuthPassword != null)
                    _settings.SetMultiConnectPhoneAuthPassword(request.PhoneAuthPassword);
                if (request.NotificationSecret != null)
                    _settings.SetMultiConnectNotificationSecret(request.NotificationSecret);
                if (request.RelayApiKey != null)
                    _settings.SetMultiConnectRelayApiKey(request.RelayApiKey);
            }
            finally
            {
                _settings.EndBatch();
            }

            // If a restart-required field's effective value actually changed, the running gateway is
            // still on the OLD port/secret/subject/relay — persisting alone would return a success
            // view that doesn't match runtime. Re-apply via the same hook the Settings dialog uses so
            // the echoed config reflects APPLIED state, not just persisted state.
            var afterRestart = RestartRelevantEffective();
            if (!beforeRestart.Equals(afterRestart))
            {
                var restarter = MultiConnectConfig.GatewayRestarter;
                if (restarter == null)
                {
                    // Hook not wired in this host: settings are saved but the live gateway can't be
                    // re-applied in-process. Report applied=false rather than implying success.
                    return Ok(new
                    {
                        applied = false,
                        restartRequired = true,
                        error = "Settings persisted, but no gateway restart hook is wired in this host; restart MultiTerminal to apply gatewayPort/notificationSecret/vapidSubject/relayBaseUrl.",
                    });
                }

                try
                {
                    await restarter().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Settings are already persisted — a failed restart must NOT roll them back.
                    // Surface applied=false so the caller knows runtime is stale until a manual restart.
                    return Ok(new
                    {
                        applied = false,
                        restartRequired = true,
                        error = "Settings persisted, but the gateway restart failed: " + ex.Message + ". Restart MultiTerminal to apply.",
                    });
                }
                // Restart re-read settings and republished GatewayRuntimeConfig — GetConfig now
                // reflects the applied state.
            }

            // Echo back the new effective view so the caller can refresh without a second GET.
            return GetConfig();
        }

        /// <summary>
        /// Effective values of the four restart-required fields (gateway port, NotificationSecret,
        /// VapidSubject, relay BaseUrl), resolved settings-first → appsettings. Used to detect whether
        /// a POST actually changed one and the running gateway must be re-applied.
        /// </summary>
        private (int port, string secret, string vapid, string relayBase) RestartRelevantEffective()
        {
            int port;
            int? settingsPort = _settings.GetMultiConnectGatewayPort();
            if (settingsPort.HasValue)
                port = settingsPort.Value;
            else if (int.TryParse(MultiConnectConfig.Appsettings[MultiConnectConfig.CfgPort], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cp))
                port = cp;
            else
                port = SettingsService.DefaultMultiConnectGatewayPort;

            string secret = MultiConnectConfig.Resolve(_settings.GetMultiConnectNotificationSecret(), MultiConnectConfig.Appsettings[MultiConnectConfig.CfgNotificationSecret]) ?? "";
            string vapid = MultiConnectConfig.Resolve(_settings.GetMultiConnectVapidSubject(), MultiConnectConfig.Appsettings[MultiConnectConfig.CfgVapidSubject]) ?? "";
            string relayBase = MultiConnectConfig.Resolve(_settings.GetMultiConnectRelayBaseUrl(), MultiConnectConfig.Appsettings[MultiConnectConfig.CfgRelayBaseUrl]) ?? "";
            return (port, secret, vapid, relayBase);
        }

        /// <summary>GET /api/multi-connect/tailscale-status — typed Tailscale probe (never throws).</summary>
        [HttpGet("tailscale-status")]
        public async Task<IActionResult> GetTailscaleStatus()
        {
            if (!IsLoopbackRequest())
                return StatusCode(403, new { error = "Loopback only" });

            // Same cross-origin browser guard as GET /config — this leaks the tailscale hostname
            // and backend state, which a malicious loopback-origin page must not be able to read.
            if (!CrossOriginBrowserGuard.IsLocalNonBrowserCaller(Request))
                return StatusCode(403, new { error = "Origin not allowed" });

            var status = await TailscaleService.GetStatusAsync().ConfigureAwait(false);
            return Ok(new
            {
                installed = status.Installed,
                running = status.Running,
                backendState = status.BackendState,
                hostname = status.Hostname,
                error = status.Error,
            });
        }

        // ----------------------------- helpers -----------------------------

        private bool IsLoopbackRequest()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            // Null = in-process / no remote endpoint → treat as loopback. Otherwise require loopback.
            return ip == null || IPAddress.IsLoopback(ip);
        }

        private static bool IsSetValue(string value) => !string.IsNullOrWhiteSpace(value);

        private static bool TryParsePort(string raw, out int port)
        {
            port = 0;
            if (!int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                return false;
            if (p < 1 || p > 65535)
                return false;
            port = p;
            return true;
        }

        private static bool IsValidVapidSubject(string value)
        {
            string v = value.Trim();
            if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return v.Length > "mailto:".Length;
            return IsAbsoluteHttpUrl(v);
        }

        private static bool IsAbsoluteHttpUrl(string value)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
                return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
    }

    /// <summary>
    /// POST body for /api/multi-connect/config. Per-field semantics: <c>null</c> (key omitted or
    /// JSON null) = leave unchanged; empty string = clear the stored value; any other value = set it.
    /// This lets the tab send an untouched/masked secret as null so it never overwrites the stored
    /// secret with a placeholder. <see cref="TailscaleEnabled"/> is a tri-state bool: null = unchanged.
    /// </summary>
    public class MultiConnectConfigRequest
    {
        public string GatewayPort { get; set; }
        public bool? TailscaleEnabled { get; set; }
        public string TailscaleHostname { get; set; }
        public string TailscaleServePort { get; set; }
        public string PhoneAuthUsername { get; set; }
        public string PhoneAuthPassword { get; set; }
        public string NotificationSecret { get; set; }
        public string VapidSubject { get; set; }
        public string RelayBaseUrl { get; set; }
        public string RelayApiKey { get; set; }
    }
}
