using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MultiTerminal.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// The MT→phone push receiver + notification history/settings (task ca6c5344, item [7]).
    /// Ported from the standalone MultiRemote's NotificationEndpoints. MT's own
    /// NotificationsController (on :5050) fire-and-forwards runtime notifications to
    /// /api/notifications/runtime here; once folded in-process that POST is a same-process
    /// loopback call. /api/notifications/runtime is public (allowlisted) but protected by an
    /// optional X-MT-Secret shared secret (MultiRemote:NotificationSecret). The per-type
    /// toggle file lives in the resolved data dir (<see cref="GatewayPaths"/>).
    /// </summary>
    public static class GatewayNotificationEndpoints
    {
        private static readonly List<NotificationRecord> _history = new List<NotificationRecord>();
        private static readonly object _lock = new object();
        private const int MaxHistory = 200;

        private static readonly ConcurrentDictionary<string, bool> _toggles = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _togglesSaveLock = new object();
        private static string _togglesPath;

        private static readonly HashSet<string> ValidTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "task_complete", "ready_for_testing", "escalation", "helper_request",
            "agent_stopped", "permission_request", "error", "message", "inbox",
        };

        public static void MapMultiRemoteNotificationEndpoints(this WebApplication app)
        {
            _togglesPath = Path.Combine(GatewayPaths.DataDir(app.Configuration), "notification-toggles.json");
            LoadToggles();

            // Map-time read is ONLY for the startup warning. The shared secret is resolved
            // settings-first → appsettings fallback and published on GatewayRuntimeConfig by
            // MultiRemoteGatewayHost.StartAsync; it is re-read per-REQUEST below so this RECEIVER
            // validates the SAME value the SENDER (NotificationsController, via GatewayRuntimeConfig)
            // attaches. Capturing the map-time appsettings value alone would 403 every push once
            // settings.txt overrides the secret (task 642c14e3, pipeline Run-1 split-brain).
            var startupSecret = ResolveNotificationSecret(app.Configuration);
            if (string.IsNullOrEmpty(startupSecret))
            {
                app.Logger.LogWarning("MultiRemote:NotificationSecret is not configured — /api/notifications/runtime is restricted to LOOPBACK callers only (remote/tailnet POSTs are rejected). Set a secret in the Multi-Connect tab or appsettings.Local.json to allow authenticated remote callers.");
            }

            app.MapPost("/api/notifications/runtime", async (HttpContext context, PushNotificationService push, ILogger<PushNotificationService> logger) =>
            {
                // Resolve per-request so a secret set after startup (Multi-Connect tab → gateway
                // restart republishes GatewayRuntimeConfig) is honoured without leaving this
                // receiver stuck on the stale map-time value.
                var notificationSecret = ResolveNotificationSecret(app.Configuration);
                if (!string.IsNullOrEmpty(notificationSecret))
                {
                    var providedSecret = context.Request.Headers["X-MT-Secret"].FirstOrDefault() ?? "";
                    // Constant-time compare so the loopback secret can't be recovered via a timing
                    // oracle (pipeline Run-5 security). FixedTimeEquals is length-safe (returns false
                    // for differing lengths without leaking the length via early-out).
                    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(providedSecret),
                            System.Text.Encoding.UTF8.GetBytes(notificationSecret)))
                        return Results.Json(new { error = "Unauthorized" }, statusCode: 403);
                }
                else
                {
                    // No secret configured → this route would otherwise be fully unauthenticated, yet
                    // it sits on the no-session PublicPaths allowlist AND is reachable on the
                    // Tailscale-exposed :5100 gateway — so any tailnet peer could inject runtime
                    // notifications / spam push (pipeline Run-2 HIGH). The ONLY legitimate no-secret
                    // caller is MT's own NotificationsController forwarding to localhost:<port>
                    // (loopback). Restrict the unauthenticated path to loopback; a null remote means
                    // an in-process call and is allowed.
                    var remoteIp = context.Connection.RemoteIpAddress;
                    if (remoteIp != null && !System.Net.IPAddress.IsLoopback(remoteIp))
                        return Results.Json(new { error = "Unauthorized" }, statusCode: 403);
                }

                RuntimeNotification req;
                try
                {
                    req = await context.Request.ReadFromJsonAsync<RuntimeNotification>();
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON" });
                }

                if (req == null || string.IsNullOrEmpty(req.NotificationType))
                    return Results.BadRequest(new { error = "notification_type is required" });

                if (!ValidTypes.Contains(req.NotificationType))
                    return Results.BadRequest(new { error = $"Unknown notification_type: {req.NotificationType}" });

                if (_toggles.TryGetValue(req.NotificationType, out var enabled) && !enabled)
                {
                    logger.LogInformation("Notification suppressed (type {Type} is disabled)", req.NotificationType);
                    return Results.Ok(new { suppressed = true, notification_type = req.NotificationType });
                }

                var (title, body) = MapNotification(req);

                var record = new NotificationRecord
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    NotificationType = req.NotificationType,
                    Title = title,
                    Body = body,
                    AgentName = req.AgentName,
                    SessionId = req.SessionId,
                    ReceivedAt = DateTime.UtcNow,
                };

                lock (_lock)
                {
                    _history.Insert(0, record);
                    if (_history.Count > MaxHistory)
                        _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
                }

                var result = await push.SendToAllWithResult(title, body, req.NotificationType, req.AgentName);

                // Report ACTUAL delivery, not just "we tried" (pipeline Run-2 cross-model finding:
                // pushed:true / "Notification pushed" even when every send failed or there were no
                // subscriptions hid delivery breakage behind the now-fixed forward bridge).
                if (result.Delivered)
                {
                    logger.LogInformation("Notification pushed: [{Type}] {Title} ({Ok} device(s))", req.NotificationType, title, result.SuccessCount);
                }
                else
                {
                    logger.LogWarning("Notification NOT delivered: [{Type}] {Title} ({Detail})", req.NotificationType, title,
                        result.Error ?? $"{result.ErrorCount} send(s) failed, 0 delivered");
                }

                return Results.Ok(new { pushed = result.Delivered, notification_type = req.NotificationType, title, body, push_result = result });
            });

            app.MapGet("/api/notifications", (int? limit) =>
            {
                var count = Math.Min(limit ?? 50, MaxHistory);
                List<NotificationRecord> items;
                lock (_lock)
                {
                    items = _history.Take(count).ToList();
                }
                return Results.Ok(items);
            });

            app.MapGet("/api/notifications/settings", () =>
            {
                var types = new[] { "task_complete", "ready_for_testing", "escalation", "helper_request", "agent_stopped", "permission_request", "error" };
                var settings = types.Select(t => new
                {
                    type = t,
                    label = FormatTypeLabel(t),
                    enabled = !_toggles.TryGetValue(t, out var v) || v,
                });
                return Results.Ok(settings);
            });

            app.MapPut("/api/notifications/settings", async (HttpContext context) =>
            {
                var req = await context.Request.ReadFromJsonAsync<ToggleRequest>();
                if (req == null || string.IsNullOrEmpty(req.Type))
                    return Results.BadRequest(new { error = "type is required" });
                if (!ValidTypes.Contains(req.Type))
                    return Results.BadRequest(new { error = $"Unknown notification type: {req.Type}" });

                _toggles[req.Type] = req.Enabled;
                SaveToggles();
                return Results.Ok(new { type = req.Type, enabled = req.Enabled });
            });

            app.MapDelete("/api/notifications", () =>
            {
                lock (_lock)
                {
                    _history.Clear();
                }
                return Results.Ok(new { cleared = true });
            });
        }

        /// <summary>
        /// Settings-first → appsettings-fallback resolve of the X-MT-Secret shared secret. Prefers
        /// the value MultiRemoteGatewayHost.StartAsync published on <see cref="GatewayRuntimeConfig"/>
        /// (the exact value the sender uses), falling back to the resolver/appsettings only when that
        /// is empty (e.g. a host that mapped these endpoints without populating the runtime config).
        /// </summary>
        private static string ResolveNotificationSecret(IConfiguration configuration)
        {
            var runtime = GatewayRuntimeConfig.NotificationSecret;
            if (!string.IsNullOrEmpty(runtime))
                return runtime;

            return MultiConnectConfig.Resolve(
                SettingsService.Default.GetMultiConnectNotificationSecret(),
                configuration.GetValue<string>("MultiRemote:NotificationSecret")) ?? "";
        }

        private static void LoadToggles()
        {
            if (_togglesPath == null || !File.Exists(_togglesPath))
                return;
            try
            {
                var json = File.ReadAllText(_togglesPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (data != null)
                {
                    foreach (var kvp in data)
                        _toggles[kvp.Key] = kvp.Value;
                }
            }
            catch
            {
            }
        }

        private static void SaveToggles()
        {
            if (_togglesPath == null)
                return;
            lock (_togglesSaveLock)
            {
                try
                {
                    var data = new Dictionary<string, bool>(_toggles);
                    File.WriteAllText(_togglesPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                }
            }
        }

        private static (string title, string body) MapNotification(RuntimeNotification n)
        {
            var agent = n.AgentName ?? "Agent";
            var title = !string.IsNullOrEmpty(n.ProjectName)
                ? $"{agent} — {n.ProjectName}"
                : agent;

            var body = n.NotificationType?.ToLowerInvariant() switch
            {
                "task_complete" => n.Message ?? "Finished a task",
                "ready_for_testing" => n.Message ?? "Has work ready for your review",
                "escalation" => n.Message ?? "Needs your help",
                "helper_request" => n.Message ?? "Is requesting assistance",
                "agent_stopped" => n.Message ?? "Has stopped",
                "permission_request" => n.Message ?? "Needs approval",
                "error" => n.Message ?? "Encountered an error",
                _ => n.Message ?? "Notification",
            };

            return (title, body);
        }

        private static string FormatTypeLabel(string type) => type switch
        {
            "task_complete" => "Task Complete",
            "ready_for_testing" => "Ready for Testing",
            "escalation" => "Escalations",
            "helper_request" => "Helper Requests",
            "agent_stopped" => "Agent Stopped",
            "permission_request" => "Permission Requests",
            "error" => "Errors",
            _ => type,
        };
    }

    public record RuntimeNotification
    {
        [JsonPropertyName("notification_type")]
        public string NotificationType { get; init; }

        [JsonPropertyName("title")]
        public string Title { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; init; }

        [JsonPropertyName("agent_name")]
        public string AgentName { get; init; }

        [JsonPropertyName("project_name")]
        public string ProjectName { get; init; }

        [JsonPropertyName("cwd")]
        public string Cwd { get; init; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; init; }
    }

    public class NotificationRecord
    {
        public string Id { get; set; } = "";
        public string NotificationType { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string AgentName { get; set; }
        public string SessionId { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    public record ToggleRequest
    {
        public string Type { get; init; }
        public bool Enabled { get; init; }
    }
}
