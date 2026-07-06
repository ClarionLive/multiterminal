using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API
{
    /// <summary>
    /// Simple REST API server for MultiTerminal (replaces MCP server)
    /// </summary>
    public class MultiTerminalRestServer : IDisposable
    {
        private IHost _host;
        private readonly MessageBroker _broker;
        private readonly PoolCoordinator _poolCoordinator;
        private readonly int _port;
        private bool _isRunning;
        private readonly object _lock = new object();
        private System.Threading.Timer _staleTaskTimer;
        private System.Threading.Timer _staleAgentTimer;
        private StaleTaskService _staleService;
        private SpawnService _spawnService;
        private TerminalStreamService _terminalStreamService;
        private CompanionProcessManager _companionProcessManager;
        // G8: MainForm's ProjectService instance, supplied so the REST DI shares the ONE instance the
        // CodeGraphWatcher subscribes to (instead of the container creating a second). Owned/disposed
        // by MainForm — the container does not dispose instances it didn't create.
        private readonly MultiTerminal.Services.ProjectService _externalProjectService;
        private MultiTerminal.Services.Presence.PresenceAdapter _presenceAdapter;
        private bool _isDisposed;

        /// <summary>
        /// The message broker for routing messages between terminals.
        /// </summary>
        public MessageBroker Broker => _broker;

        /// <summary>
        /// The spawn service for programmatic terminal spawning.
        /// </summary>
        public SpawnService SpawnService => _spawnService;

        /// <summary>
        /// The pool coordinator for multi-instance state management.
        /// </summary>
        public PoolCoordinator PoolCoordinator => _poolCoordinator;

        /// <summary>
        /// The terminal stream service for WebSocket terminal I/O.
        /// Available after StartAsync() completes.
        /// </summary>
        public TerminalStreamService TerminalStreamService => _terminalStreamService;

        /// <summary>
        /// The companion process manager for auto-starting external services.
        /// </summary>
        public CompanionProcessManager CompanionProcessManager => _companionProcessManager;

        /// <summary>
        /// The port the server is running on.
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Whether the server is currently running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// The URL for clients to connect to.
        /// </summary>
        public string Url => $"http://localhost:{_port}";

        /// <summary>
        /// Raised when the server starts.
        /// </summary>
        public event EventHandler ServerStarted;

        /// <summary>
        /// Raised when the server stops.
        /// </summary>
        public event EventHandler ServerStopped;

        /// <summary>
        /// Raised when an error occurs.
        /// </summary>
        public event EventHandler<Exception> ServerError;

        public MultiTerminalRestServer(int port, MultiTerminal.Services.ProjectService projectService)
        {
            _port = port;
            _broker = new MessageBroker();
            _poolCoordinator = new PoolCoordinator();
            _spawnService = new SpawnService();
            _companionProcessManager = new CompanionProcessManager();
            // Required (G8): the REST DI must share MainForm's single ProjectService instance so
            // project-creation events reach the CodeGraphWatcher. Fail fast rather than silently
            // letting DI construct a second, deaf instance.
            _externalProjectService = projectService
                ?? throw new ArgumentNullException(nameof(projectService),
                    "ProjectService must be supplied so the REST DI shares MainForm's single instance (G8).");
        }

        /// <summary>
        /// Start the REST API server.
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
                var builder = WebApplication.CreateBuilder();

                // Configure Kestrel
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, _port);
                });

                // Suppress ASP.NET Core logging noise
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

                // Add services
                builder.Services.AddSingleton(_broker);
                builder.Services.AddSingleton(_poolCoordinator);
                builder.Services.AddSingleton(_spawnService);
                builder.Services.AddSingleton<TerminalStreamService>();
                // SessionDatabase removed — sessions now stored in multiterminal.db via SessionLineageService
                builder.Services.AddSingleton<PlanDatabase>();
                builder.Services.AddSingleton<TaskDatabase>();
                builder.Services.AddSingleton<ActivityService>();
                builder.Services.AddSingleton<ActivityFeedService>();
                builder.Services.AddSingleton<SummaryService>();
                builder.Services.AddSingleton<StaleTaskService>();
                builder.Services.AddSingleton<ComplexityDetector>();
                // G8: register MainForm's existing ProjectService instance (not a DI-created second
                // one) so broker.ProjectService — and every REST controller / broker path that fires
                // ProjectUpdated etc. — lands on the SAME instance the CodeGraphWatcher subscribes to.
                // Registering an existing instance means the container does NOT own its disposal
                // (mirrors _broker/_companionProcessManager above), so MainForm keeps single ownership
                // and there's no double-dispose. The ctor guarantees _externalProjectService is non-null.
                builder.Services.AddSingleton(_externalProjectService);
                builder.Services.AddSingleton<MultiTerminal.Services.VersioningService>();
                builder.Services.AddSingleton<MultiTerminal.Services.ChangelogService>();
                builder.Services.AddSingleton<MultiTerminal.Services.ProjectDatabase>();
                builder.Services.AddSingleton<MultiTerminal.Services.ProjectContextService>();
                builder.Services.AddSingleton<MultiTerminal.Services.ProjectJsonMigrationService>();
                builder.Services.AddSingleton<MultiTerminal.Services.McpConfigService>();
                builder.Services.AddSingleton<MultiTerminal.Services.GatewayIntegrationService>();
                builder.Services.AddSingleton<MultiTerminal.Services.RipgrepService>();
                builder.Services.AddSingleton(SettingsService.Default);
                builder.Services.AddSingleton<PermissionRelayService>();
                builder.Services.AddSingleton(_companionProcessManager);
                builder.Services.AddSingleton(sp =>
                    new MultiTerminal.Services.OwnerProfileService(
                        sp.GetRequiredService<TaskDatabase>().Connection));
                builder.Services.AddSingleton(sp =>
                    new MultiTerminal.Services.SourceControlAccountService(
                        sp.GetRequiredService<TaskDatabase>().Connection));
                // Pipeline Run 5 finding (Codex security HIGH): the DI factory
                // previously constructed a SECOND BranchMetadataService instance
                // separate from the one MainForm wires onto broker.BranchMetadata
                // for the HUD path. Two instances → two private _lock objects →
                // HUD writes and REST writes don't serialize against the same
                // lock. Unify by returning the broker's instance so all callers
                // (REST controllers via DI + HUD via broker) share one lock.
                // The broker.BranchMetadata is wired by MainForm during startup
                // before the REST server begins accepting connections, so the
                // null-check throw is defensive against future ordering drift.
                builder.Services.AddSingleton<MultiTerminal.Services.BranchMetadataService>(sp =>
                {
                    var broker = sp.GetRequiredService<MultiTerminal.MCPServer.Services.MessageBroker>();
                    return broker.BranchMetadata
                        ?? throw new InvalidOperationException(
                            "BranchMetadataService not yet wired on MessageBroker — MainForm wire-up must precede REST controller resolution.");
                });

                // Add controllers for REST API (DebugController gets DebugLogService from MessageBroker directly)
                builder.Services.AddControllers()
                    .AddJsonOptions(options =>
                    {
                        // Preserve international characters (Dutch, emoji, etc.) as-is instead of \uXXXX escaping
                        options.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                    });

                // Global exception handling (Eval P2 item 1, task c522764d): give the :5050
                // surface a safety net so all controllers emit consistent RFC 7807 ProblemDetails
                // 500s (via IProblemDetailsService) instead of bare stack-trace 500s. The handler
                // logs the unhandled exception to DebugLogService, then returns false so the
                // framework writes the ProblemDetails body. Invoked by app.UseExceptionHandler() below.
                builder.Services.AddProblemDetails();
                builder.Services.AddExceptionHandler<RestApiExceptionHandler>();

                // CORS (Eval P2 item 3, task c522764d): replace AllowAnyOrigin with a two-tier
                // allowlist (see RestCorsOriginPolicy for the full rationale + retirement path f9697aac).
                //  - DEFAULT policy (all controllers): loopback origins ONLY, no "null". Blocks the
                //    drive-by remote-web-page CSRF/exfil READ threat AllowAnyOrigin left open —
                //    including a hostile sandboxed iframe whose opaque origin serializes to "null".
                //  - NAMED policy (FilePanelPolicyName): loopback + "null", applied via [EnableCors]
                //    to TaskReportsController only — the one controller the file:// tasks-panel.html
                //    actually fetches. Scoping "null" to that controller (PM ruling A) shrinks the
                //    interim null-origin read-window from the whole API to a single controller.
                // NOTE: intentionally NO AllowCredentials on either policy — the panels are
                // credential-less, and null-origin + credentials is a CORS footgun.
                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.SetIsOriginAllowed(RestCorsOriginPolicy.IsLoopbackOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod());

                    options.AddPolicy(RestCorsOriginPolicy.FilePanelPolicyName, policy =>
                        policy.SetIsOriginAllowed(RestCorsOriginPolicy.IsAllowedOrigin)
                              .AllowAnyHeader()
                              .AllowAnyMethod());
                });

                var app = builder.Build();

                // Exception handler must be first so it catches exceptions from all downstream
                // middleware and controllers (Eval P2 item 1, task c522764d). Produces RFC 7807
                // ProblemDetails 500s via the registered ProblemDetails service; RestApiExceptionHandler
                // logs the error to DebugLogService.
                app.UseExceptionHandler();

                // Enable WebSockets for terminal streaming
                app.UseWebSockets();

                // Enable CORS
                app.UseCors();

                // Wire up ActivityService to MessageBroker for auto-update hooks
                var activityService = app.Services.GetRequiredService<ActivityService>();
                _broker.ActivityService = activityService;

                // Wire up SummaryService to MessageBroker for auto-summary generation
                var summaryService = app.Services.GetRequiredService<SummaryService>();
                _broker.SummaryService = summaryService;

                // Wire up ComplexityDetector to MessageBroker for plan suggestions
                var complexityDetector = app.Services.GetRequiredService<ComplexityDetector>();
                _broker.ComplexityDetector = complexityDetector;

                // Wire up ChangelogService to MessageBroker for automatic changelog generation
                var changelogService = app.Services.GetRequiredService<MultiTerminal.Services.ChangelogService>();
                _broker.ChangelogService = changelogService;

                // Wire up ProjectService to MessageBroker for .claude/project.json management.
                // It's MainForm's instance (passed into this server's ctor and DI-registered above), so
                // use it directly rather than round-tripping through DI — broker.ProjectService ===
                // MainForm._projectService === the instance the CodeGraphWatcher subscribes to, so
                // every creation path now notifies the watcher.
                var projectService = _externalProjectService;
                _broker.ProjectService = projectService;

                // Wire up ActivityFeedService to MessageBroker so hook-generated events reach the UI
                var activityFeedService = app.Services.GetRequiredService<ActivityFeedService>();
                _broker.ActivityFeedService = activityFeedService;

                // Subscribe to ActivityFeedService.ActivityRecorded and forward to MessageBroker
                activityFeedService.ActivityRecorded += (sender, entry) =>
                {
                    try
                    {
                        var activityEvent = new MCPServer.Models.ActivityEvent
                        {
                            Id = entry.Id.ToString(),
                            Terminal = entry.Actor ?? "System",
                            Type = MapActivityTypeToEventType(entry.ActivityType),
                            Action = ExtractActionFromActivityType(entry.ActivityType),
                            Content = entry.Summary,
                            Timestamp = entry.Timestamp,
                            RelatedId = entry.PlanId ?? entry.PhaseId,
                            Details = entry.DetailsJson
                        };

                        _broker.RecordActivity(activityEvent, alreadyPersisted: true);
                        System.Diagnostics.Debug.WriteLine($"[ActivityFeed→UI] {entry.ActivityType}: {entry.Summary}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ActivityFeed→UI] Failed to forward event: {ex.Message}");
                    }
                };

                // Sync UI-created projects to MessageBroker/Database
                projectService.ProjectRegistered += (sender, args) =>
                {
                    try
                    {
                        var fileProject = args.Project;
                        var dbProject = new MCPServer.Models.Project
                        {
                            Id = fileProject.Id,
                            Name = fileProject.Name,
                            Description = fileProject.Description ?? "",
                            Path = fileProject.Path,
                            CreatedBy = "UI",
                            CreatedAt = fileProject.CreatedAt,
                            UpdatedAt = DateTime.UtcNow
                        };

                        if (_broker.GetProject(dbProject.Id).Success == false)
                        {
                            // This hook fires *after* ProjectService.RegisterProject wrote
                            // .claude/project.json. The broker must reuse that ID rather
                            // than reject the create as a duplicate — set allowReuseExisting.
                            _broker.CreateProject(dbProject.Name, dbProject.Description, "UI", dbProject.Path,
                                allowReuseExisting: true);
                            System.Diagnostics.Debug.WriteLine($"[ProjectSync] Synced UI project to database: {dbProject.Name} (ID: {dbProject.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProjectSync] Failed to sync project: {ex.Message}");
                    }
                };

                // LEARNED message persistence now handled by knowledge_entries in multiterminal.db

                // Get stale service for timer
                _staleService = app.Services.GetRequiredService<StaleTaskService>();

                // Expose TerminalStreamService for MainForm wiring
                _terminalStreamService = app.Services.GetRequiredService<TerminalStreamService>();

                // Start periodic stale task checker (runs every hour)
                // Note: Startup check removed to avoid blocking first API request
                _staleTaskTimer = new System.Threading.Timer(
                    CheckStaleTasksTimerCallback,
                    null,
                    TimeSpan.FromHours(1),
                    TimeSpan.FromHours(1)
                );
                System.Diagnostics.Debug.WriteLine("[API] Stale task timer started (hourly checks)");

                // Start periodic stale office agent cleanup (every 5 minutes)
                _staleAgentTimer = new System.Threading.Timer(
                    CheckStaleOfficeAgentsCallback,
                    null,
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(5)
                );
                System.Diagnostics.Debug.WriteLine("[API] Stale office agent timer started (5-minute checks)");

                // MCP registry seeding/sync removed — gateway is now source of truth for server selection.

                // Map REST API endpoints
                app.MapControllers();

                // Simple health check endpoint
                app.MapGet("/", () => "MultiTerminal REST API is running");
                app.MapGet("/health", () => new { status = "ok", port = _port });

                _host = app;

                await _host.StartAsync(cancellationToken);

                // Presence-aware notification routing (task 9f9c3141): ingest MSR-2 mmWave + phone
                // BLE signals over MQTT and drive the binary desk/away gate (broker.SetRemoteMode)
                // automatically. No-ops entirely unless presence.enabled == "1", so the manual
                // remoteMode pill is untouched until the Owner opts in + calibrates.
                try
                {
                    _presenceAdapter = new MultiTerminal.Services.Presence.PresenceAdapter(
                        _broker,
                        SettingsService.Default,
                        (level, message) =>
                        {
                            var dbg = _broker?.DebugLogService;
                            if (dbg == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Presence:{level}] {message}");
                                return;
                            }
                            switch (level)
                            {
                                case "error": dbg.Error("Presence", message); break;
                                case "warn": dbg.Warning("Presence", message); break;
                                default: dbg.Info("Presence", message); break;
                            }
                        });
                    _presenceAdapter.Start();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Presence adapter failed to start: {ex.Message}");
                }

                ServerStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _isRunning = false;
                }
                ServerError?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Stop the REST API server.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;
            }

            try
            {
                if (_host != null)
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeoutCts.Token);

                    try
                    {
                        await _host.StopAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[API] Shutdown timed out, forcing dispose");
                    }

                    _host.Dispose();
                    _host = null;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isRunning = false;
                }
                ServerStopped?.Invoke(this, EventArgs.Empty);
            }
        }

        private void CheckStaleTasksTimerCallback(object state)
        {
            if (_staleService == null || !_isRunning)
                return;

            try
            {
                var staleResults = _staleService.CheckAndFlagStaleTasks();
                if (staleResults.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Periodic check: Flagged {staleResults.Count} stale tasks");

                    foreach (var result in staleResults)
                    {
                        if (!string.IsNullOrEmpty(result.Assignee))
                        {
                            _broker?.NotifyStaleTask(
                                result.Assignee,
                                result.TaskId,
                                result.TaskTitle,
                                result.NewLevel,
                                result.DaysPaused);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] Periodic stale task check failed: {ex.Message}");
            }
        }

        private void CheckStaleOfficeAgentsCallback(object state)
        {
            if (!_isRunning) return;

            try
            {
                var removed = _broker?.CleanupStaleOfficeAgents(30);
                if (removed != null && removed.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Stale agent cleanup: Removed {removed.Count} ghost agent(s)");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] Stale agent cleanup failed: {ex.Message}");
            }
        }

        private static string MapActivityTypeToEventType(string activityType)
        {
            if (activityType == null) return "activity";

            if (activityType.StartsWith("BUILD_", StringComparison.OrdinalIgnoreCase))
                return "build";

            if (activityType.StartsWith("PLAN_", StringComparison.OrdinalIgnoreCase))
                return "plan";

            if (activityType.StartsWith("PHASE_", StringComparison.OrdinalIgnoreCase))
                return "plan";

            if (activityType.StartsWith("TOOL_", StringComparison.OrdinalIgnoreCase))
                return "activity";

            if (activityType.StartsWith("SUBAGENT_", StringComparison.OrdinalIgnoreCase))
                return "activity";

            return "activity";
        }

        private static string ExtractActionFromActivityType(string activityType)
        {
            if (string.IsNullOrEmpty(activityType)) return null;

            var parts = activityType.Split('_');
            if (parts.Length > 1)
            {
                return parts[parts.Length - 1].ToLowerInvariant();
            }

            return activityType.ToLowerInvariant();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _staleTaskTimer?.Dispose();
                _staleTaskTimer = null;
                _staleAgentTimer?.Dispose();
                _staleAgentTimer = null;
                _presenceAdapter?.Dispose();
                _presenceAdapter = null;
                StopAsync().GetAwaiter().GetResult();
                _broker?.Dispose();
                _poolCoordinator?.Dispose();
            }
            _isDisposed = true;
        }
    }
}
