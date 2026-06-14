using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// In-process host for the MultiRemote phone gateway (Phase 2).
    /// Runs as a SECOND, INDEPENDENT <see cref="WebApplication"/> on its own port
    /// (default :5100) so it keeps its own middleware pipeline (static files, and —
    /// in later items — session auth + forwarded headers) without contaminating
    /// MultiTerminal's unauthenticated :5050 API. See task ca6c5344 DECISIONS LOG D1.
    ///
    /// Item [1] scope: serve the static PWA (wwwroot) with the same cache policy the
    /// standalone MultiRemote used (no-store for HTML/JSON, 60s for assets) plus a
    /// /health probe and SPA fallback.
    ///
    /// Item [2] scope: cookie-session auth — ForwardedHeaders trusting loopback (so
    /// Tailscale serve's X-Forwarded-Proto=https yields Secure cookies), security
    /// headers, an in-memory session store, the <see cref="SessionAuthMiddleware"/>
    /// gate, and the login/logout/status endpoints (<see cref="AuthEndpoints"/>).
    ///
    /// Items [3]-[7] scope: the in-process replacements for the old :5050 proxy hops.
    /// MainForm hands this host MT's already-wired service singletons; the gateway
    /// then (a) mounts a whitelisted subset of MT's own controllers in-process
    /// (Tasks/Projects/Team/remote-mode/digest — identical responses, no HTTP hop)
    /// and (b) hand-maps the PWA's remapped/self-contained routes (terminals,
    /// messages, inbox, spawn, permissions, notifications, unfurl, push, terminal-WS)
    /// straight onto those same singletons.
    /// </summary>
    public class MultiRemoteGatewayHost : IDisposable
    {
        // Constructor-supplied fallback; the effective port is resolved from the
        // MultiRemote:Port config key in StartAsync (config wins so a Dev can change
        // the listen port without recompiling). Not readonly for that reason.
        private int _port;
        private readonly object _lock = new object();
        private WebApplication _app;
        private bool _isRunning;
        private bool _isDisposed;

        // MT service singletons handed in by MainForm (after :5050 + UI wiring is live).
        // The EXACT instances matter: SpawnService/TerminalStreamService carry callbacks
        // and a terminal resolver that MainForm bound to those specific objects.
        private readonly MessageBroker _broker;
        private readonly SpawnService _spawnService;
        private readonly TerminalStreamService _streamService;
        private readonly ProjectDatabase _projectDb;
        private readonly bool _servicesAvailable;

        // fa1101db R4 — handler bridging the shared broker's PermissionRelayPushRequested event to
        // THIS host's PushNotificationService. Held so Stop/Dispose can detach it: the broker
        // outlives this host, so a Stop→Start cycle must not leave a stale (or doubled) subscription.
        private EventHandler<PermissionRelayPushEventArgs> _relayPushHandler;

        /// <summary>
        /// The port the gateway listens on. Resolved from MultiRemote:Port (config wins)
        /// once StartAsync runs; until then it reflects the constructor fallback (5100).
        /// </summary>
        public int Port => _port;

        /// <summary>Whether the gateway host is currently running.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>The loopback URL the gateway serves on.</summary>
        public string Url => "http://localhost:" + _port;

        /// <summary>Raised when an unhandled error occurs during startup.</summary>
        public event EventHandler<Exception> ServerError;

        /// <summary>
        /// Construct the gateway host. <paramref name="broker"/> and the other service
        /// singletons should be MainForm's live instances (from <c>_mcpServer</c>); when
        /// they are null the gateway degrades gracefully to static-PWA + auth only (the
        /// in-process API is simply not mounted).
        /// </summary>
        public MultiRemoteGatewayHost(
            int port = 5100,
            MessageBroker broker = null,
            SpawnService spawnService = null,
            TerminalStreamService streamService = null,
            ProjectDatabase projectDb = null)
        {
            _port = port;
            _broker = broker;
            _spawnService = spawnService;
            _streamService = streamService;
            _projectDb = projectDb;
            _servicesAvailable = broker != null;
        }

        /// <summary>
        /// Start the gateway host. Safe to call once; subsequent calls no-op while running.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_isRunning)
                    return;
                _isRunning = true;
            }

            try
            {
                // Content root pinned to the exe output dir so wwwroot resolves regardless
                // of the process working directory (a WinForms host can launch from anywhere).
                var contentRoot = AppContext.BaseDirectory;
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = contentRoot,
                    WebRootPath = "wwwroot",
                });

                // Load the gitignored local override (real auth credentials live here, never
                // committed). appsettings.json ships changeme/changeme defaults; this file —
                // dropped next to the exe per install — overrides them. Item [8] formalizes
                // the key set + ownership; item [2] only needs the Auth section.
                builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

                // Gateway config section (port, auth credentials, cookie/session settings).
                var gatewayConfig = builder.Configuration.GetSection("MultiRemote");

                // Resolve the listen port from config (MultiRemote:Port) so a Dev can change
                // it without recompiling; the constructor value is only the fallback. Keep
                // tailscale serve + companion-processes.json aligned to this port (item [9]).
                _port = gatewayConfig.GetValue<int>("Port", _port);

                // Publish runtime config so MT's own NotificationsController can forward to the
                // right port AND attach X-MT-Secret (pipeline Run-1 cross-model HIGH: setting the
                // secret must not silently break MT→phone push). See GatewayRuntimeConfig.
                GatewayRuntimeConfig.Port = _port;
                GatewayRuntimeConfig.NotificationSecret = gatewayConfig.GetValue<string>("NotificationSecret") ?? "";

                // Own listener — independent of MT's :5050 Kestrel.
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, _port);
                });

                // Match MT's REST host: keep ASP.NET logging quiet.
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Warning);

                // Trust X-Forwarded-* from the loopback reverse proxy. Tailscale serve
                // terminates TLS and forwards to loopback with X-Forwarded-Proto=https,
                // so the request scheme becomes https and the SameAsRequest session cookie
                // below is issued Secure. (Per D2 the proxy is Tailscale-only — Caddy dropped.)
                builder.Services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    options.KnownProxies.Add(IPAddress.Loopback);
                    options.KnownProxies.Add(IPAddress.IPv6Loopback);
                });

                // In-memory cookie session for auth (ported from MultiRemote Program.cs:36-47).
                builder.Services.AddDistributedMemoryCache();
                builder.Services.AddSession(options =>
                {
                    var sessionTimeout = gatewayConfig.GetValue<int>("Auth:SessionTimeoutMinutes", 1440);
                    options.IdleTimeout = TimeSpan.FromMinutes(sessionTimeout);
                    options.Cookie.Name = gatewayConfig.GetValue<string>("Auth:CookieName") ?? "MultiRemoteSession";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.Cookie.IsEssential = true;
                });

                // --- In-process MT services (task ca6c5344, items [3]-[7]) ---------------
                // Hand MT's already-wired singletons to the gateway so its controllers and
                // hand-mapped handlers call them DIRECTLY (no HTTP hop to :5050). Reusing the
                // EXACT instances is load-bearing: SpawnService/TerminalStreamService carry UI
                // callbacks + a terminal resolver MainForm bound to those specific objects.
                // TaskDatabase + ProjectService are reached via the broker; ProjectContextService
                // and PermissionRelayService aren't held anywhere, so the DI container builds them.
                if (_servicesAvailable)
                {
                    builder.Services.AddSingleton(_broker);
                    builder.Services.AddSingleton(_broker.TaskDb);
                    builder.Services.AddSingleton(_broker.ProjectService);
                    builder.Services.AddSingleton(_projectDb);
                    builder.Services.AddSingleton(SettingsService.Default);
                    builder.Services.AddSingleton<ProjectContextService>();
                    builder.Services.AddSingleton<PermissionRelayService>();
                    if (_spawnService != null)
                        builder.Services.AddSingleton(_spawnService);
                    if (_streamService != null)
                        builder.Services.AddSingleton(_streamService);

                    // Mount a WHITELISTED subset of MT's own controllers in-process. Their
                    // native routes (api/tasks, api/projects, api/team, api/remote-mode,
                    // api/digest) are exactly what the PWA calls, so responses are byte-
                    // identical to :5050 with zero re-implementation. UnsafeRelaxedJsonEscaping
                    // matches :5050 (MultiTerminalRestServer.cs:175-180) so international
                    // characters/emoji aren't \uXXXX-escaped.
                    builder.Services.AddControllers()
                        .AddJsonOptions(o =>
                            o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
                        .ConfigureApplicationPartManager(apm =>
                        {
                            foreach (var existing in apm.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                                apm.FeatureProviders.Remove(existing);
                            apm.FeatureProviders.Add(new GatewayControllerFeatureProvider(
                                typeof(TasksController),
                                typeof(ProjectContextController),
                                typeof(TeamController),
                                typeof(RemoteModeController),
                                typeof(DigestController)));
                        });

                    // Same relaxed escaping for the hand-mapped minimal-API handlers
                    // (terminals/messages/inbox/spawn/permissions/notifications/unfurl/push).
                    builder.Services.ConfigureHttpJsonOptions(o =>
                        o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

                    // HttpClient for the off-box Cloudflare permission relay (item [5]) — the
                    // one remaining outbound hop (everything else is a direct service call).
                    // fa1101db R3 — read the relay base URL from the SAME source PermissionRelayService
                    // (the posting side) uses (SettingsService "permissionRelay.baseUrl") so the two
                    // can't drift; appsettings MultiRemote:PermissionRelay:BaseUrl stays a fallback.
                    var relayUrl = SettingsService.Default.Get("permissionRelay.baseUrl");
                    if (string.IsNullOrWhiteSpace(relayUrl))
                        relayUrl = gatewayConfig.GetValue<string>("PermissionRelay:BaseUrl");
                    if (string.IsNullOrWhiteSpace(relayUrl))
                        relayUrl = "https://mt-mcp-server.clarionlive.workers.dev";
                    builder.Services.AddHttpClient("PermissionRelay", c =>
                    {
                        c.BaseAddress = new Uri(relayUrl);
                        c.Timeout = TimeSpan.FromSeconds(15);
                    });

                    // Web Push pipeline (item [7]): VAPID sender + the in-process inbox→push
                    // monitor (reads MT's inbox directly instead of HTTP-polling :5050).
                    builder.Services.AddSingleton<PushNotificationService>();
                    builder.Services.AddHostedService<GatewayInboxMonitorService>();
                }

                _app = builder.Build();

                var webRoot = Path.Combine(contentRoot, "wwwroot");
                if (!Directory.Exists(webRoot))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[MultiRemoteGatewayHost] wwwroot not found at " + webRoot +
                        " — the PWA will 404 until the build copies it to output.");
                }

                // Forwarded headers FIRST so downstream middleware sees the real client
                // scheme/IP from the Tailscale loopback proxy (drives Secure cookies + the
                // per-IP login rate limiter).
                _app.UseForwardedHeaders();

                // Security headers (ported verbatim from MultiRemote Program.cs:108-115).
                _app.Use(async (context, next) =>
                {
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    await next();
                });

                // WebSockets before session/auth so the terminal-stream upgrade (item [6])
                // is handled; the WS handler does its own in-handler session auth check.
                _app.UseWebSockets();

                // Session before the auth gate so SessionAuthMiddleware can read it.
                _app.UseSession();

                // Auth gate: protects everything except the public paths (login page/api,
                // push key, health, sw.js) and the login static assets. Must run BEFORE the
                // default/static-file middleware so an unauthenticated "/" is redirected to
                // /login.html rather than served the app shell.
                _app.UseMiddleware<SessionAuthMiddleware>();

                // Serve index.html for "/" (and other default-document requests).
                _app.UseDefaultFiles(new DefaultFilesOptions
                {
                    DefaultFileNames = { "index.html" },
                });

                // Static assets with the cache policy ported verbatim from MultiRemote:
                // HTML/JSON must always revalidate (so a redeploy is picked up immediately),
                // everything else gets a short 60s public cache.
                _app.UseStaticFiles(new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        var name = ctx.File.Name;
                        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                            ctx.Context.Response.Headers["Pragma"] = "no-cache";
                        }
                        else
                        {
                            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=60";
                        }
                    },
                });

                // Auth endpoints (login/logout/status). /api/auth/login is public (see
                // SessionAuthMiddleware.PublicPaths); logout/status require a session.
                _app.MapAuthEndpoints(gatewayConfig);

                if (_servicesAvailable)
                {
                    // Mounted MT controllers — item [3] (api/tasks, api/projects, api/team)
                    // + item [5]/[8] (api/remote-mode, api/digest). These call MT's broker/
                    // services directly in-process; the PWA hits them with no :5050 hop.
                    _app.MapControllers();

                    // Item [4] — terminals/messages/inbox, hand-mapped onto the broker.
                    _app.MapMultiRemoteMessagingEndpoints();

                    // PWA verb-compat shims (e.g. PUT /api/tasks/{id}/status → PATCH handler).
                    _app.MapMultiRemoteCompatEndpoints();

                    // Item [5] — permissions (Cloudflare Worker relay), spawn, unfurl.
                    _app.MapMultiRemotePermissionsEndpoints();
                    _app.MapMultiRemoteSpawnEndpoints();
                    _app.MapMultiRemoteUnfurlEndpoints();

                    // Item [6] — interactive terminal console WebSocket (→ TerminalStreamService).
                    _app.MapMultiRemoteTerminalStream();

                    // Item [7] — Web Push subscribe/key/test + MT→phone notification
                    // receiver/history/settings.
                    _app.MapMultiRemotePushEndpoints();
                    _app.MapMultiRemoteNotificationEndpoints();

                    // fa1101db R4 — wake the phone with a web-push the moment the Cloudflare Worker
                    // confirms a permission-relay request was stored. PermissionRelayService raises
                    // PermissionRelayPushRequested on the SHARED broker (from the :5050 OR the gateway
                    // relay instance — same broker singleton); we resolve THIS host's
                    // PushNotificationService (the one holding the VAPID subscriptions) and fire it.
                    var pushForRelay = _app.Services.GetService<PushNotificationService>();
                    if (pushForRelay != null && _broker != null)
                    {
                        _relayPushHandler = (s, e) =>
                        {
                            // Offload + swallow: the broker invokes this on the relay's round-trip
                            // path, so a slow/failed send must never throw back or block it.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var result = await pushForRelay
                                        .SendToAllWithResult(e.Title, e.Body, e.RequestType, e.AgentName)
                                        .ConfigureAwait(false);
                                    System.Diagnostics.Debug.WriteLine(
                                        "[MultiRemoteGatewayHost] R4 relay push (" + e.RequestType +
                                        "): delivered=" + result.SuccessCount + "/" + result.SubscriptionCount);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        "[MultiRemoteGatewayHost] R4 relay push failed: " + ex.Message);
                                }
                            });
                        };
                        _broker.PermissionRelayPushRequested += _relayPushHandler;
                    }
                }

                // Liveness probe (kept public; the standalone gateway exposed the same).
                _app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

                // SPA fallback: unmatched non-file, non-API routes serve the app shell.
                // MUST be last so real endpoints (controllers, auth, future handlers) win.
                _app.MapFallbackToFile("index.html");

                await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _isRunning = false;
                }

                System.Diagnostics.Debug.WriteLine(
                    "[MultiRemoteGatewayHost] Failed to start on port " + _port + ": " + ex.Message);
                ServerError?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>Stop the gateway host gracefully.</summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            WebApplication app;
            lock (_lock)
            {
                if (!_isRunning)
                    return;
                _isRunning = false;
                app = _app;
            }

            // fa1101db R4 — detach the broker push bridge before the host stops; the broker
            // outlives this host, so leaving it attached would keep a dead PushNotificationService
            // wired (and a subsequent Start would add a second handler).
            if (_relayPushHandler != null && _broker != null)
            {
                _broker.PermissionRelayPushRequested -= _relayPushHandler;
                _relayPushHandler = null;
            }

            if (app != null)
            {
                try
                {
                    await app.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[MultiRemoteGatewayHost] Error stopping: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;
            if (disposing)
            {
                // fa1101db R4 — detach the broker push bridge (idempotent if StopAsync already did).
                if (_relayPushHandler != null && _broker != null)
                {
                    _broker.PermissionRelayPushRequested -= _relayPushHandler;
                    _relayPushHandler = null;
                }

                try
                {
                    if (_app != null)
                    {
                        // DisposeAsync is the supported teardown for WebApplication; block
                        // briefly on shutdown (process is exiting) mirroring MT's REST host.
                        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[MultiRemoteGatewayHost] Error disposing: " + ex.Message);
                }
            }

            _isDisposed = true;
        }
    }
}
