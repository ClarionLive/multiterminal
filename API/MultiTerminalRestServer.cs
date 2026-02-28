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

        public MultiTerminalRestServer(int port = 5050)
        {
            _port = port;
            _broker = new MessageBroker();
            _poolCoordinator = new PoolCoordinator();
            _spawnService = new SpawnService();
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
                builder.Services.AddSingleton<SessionDatabase>();
                builder.Services.AddSingleton<PlanDatabase>();
                builder.Services.AddSingleton<TaskDatabase>();
                builder.Services.AddSingleton<ActivityService>();
                builder.Services.AddSingleton<ActivityFeedService>();
                builder.Services.AddSingleton<SummaryService>();
                builder.Services.AddSingleton<StaleTaskService>();
                builder.Services.AddSingleton<ComplexityDetector>();
                builder.Services.AddSingleton<MultiTerminal.Services.ProjectService>();
                builder.Services.AddSingleton<MultiTerminal.Services.VersioningService>();
                builder.Services.AddSingleton<MultiTerminal.Services.ChangelogService>();

                // Add controllers for REST API (DebugController gets DebugLogService from MessageBroker directly)
                builder.Services.AddControllers()
                    .AddJsonOptions(options =>
                    {
                        // Preserve international characters (Dutch, emoji, etc.) as-is instead of \uXXXX escaping
                        options.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                    });

                // Add CORS for local development
                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                });

                var app = builder.Build();

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

                // Wire up ProjectService to MessageBroker for .claude/project.json management
                var projectService = app.Services.GetRequiredService<MultiTerminal.Services.ProjectService>();
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

                        _broker.RecordActivity(activityEvent);
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
                            _broker.CreateProject(dbProject.Name, dbProject.Description, "UI", dbProject.Path);
                            System.Diagnostics.Debug.WriteLine($"[ProjectSync] Synced UI project to database: {dbProject.Name} (ID: {dbProject.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProjectSync] Failed to sync project: {ex.Message}");
                    }
                };

                // Wire up LEARNED message persistence
                var poolCoordinator = app.Services.GetRequiredService<PoolCoordinator>();
                var sessionDb = app.Services.GetRequiredService<SessionDatabase>();

                poolCoordinator.LearnedMessageRecorded += (sender, args) =>
                {
                    try
                    {
                        sessionDb.SaveLearnedMessage(
                            args.MessageId,
                            args.Instance,
                            args.Topic,
                            args.Summary,
                            args.Tags,
                            args.Timestamp);
                        System.Diagnostics.Debug.WriteLine($"[Memory] Persisted LEARNED: {args.Topic}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Memory] Failed to persist: {ex.Message}");
                    }
                };

                // Get stale service for timer
                _staleService = app.Services.GetRequiredService<StaleTaskService>();

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

                // Map REST API endpoints
                app.MapControllers();

                // Simple health check endpoint
                app.MapGet("/", () => "MultiTerminal REST API is running");
                app.MapGet("/health", () => new { status = "ok", port = _port });

                _host = app;

                await _host.StartAsync(cancellationToken);
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
            _staleTaskTimer?.Dispose();
            _staleTaskTimer = null;
            _staleAgentTimer?.Dispose();
            _staleAgentTimer = null;
            StopAsync().GetAwaiter().GetResult();
        }
    }
}
