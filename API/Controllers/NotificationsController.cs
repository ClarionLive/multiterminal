using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/notifications")]
    public class NotificationsController : ControllerBase
    {
        private readonly MessageBroker _broker;
        // 30s (was 3s): the forward to the in-process gateway awaits the REAL synchronous Web Push
        // — GatewayNotificationEndpoints awaits PushNotificationService.SendToAllWithResult, which
        // does sequential outbound VAPID round-trips per subscription. A live delivery routinely
        // exceeds 3s, so the old budget cancelled mid-flight and the forcePush MCP path reported
        // "gateway-unreachable" / successCount=0 even though the phone actually received the push
        // (task ca6c5344 item [11] testing). The per-send 10s timeout in PushNotificationService
        // caps any single stale subscription so this overall budget stays meaningful.
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        // Rate limiting: sliding window of timestamps per minute
        private static readonly ConcurrentQueue<DateTime> _rateLimitWindow = new();
        private const int MaxNotificationsPerMinute = 100;

        public NotificationsController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// POST /api/notifications — Receive a Claude Code runtime notification from the hook.
        /// Stores in DB, fires broker event, and forwards to the in-process gateway for phone push.
        /// </summary>
        /// <param name="skipPush">When true, only record history — do not forward (caller already pushed).</param>
        /// <param name="forcePush">
        /// When true (explicit pushes, e.g. the MCP send_push_notification tool), bypass the
        /// remote-mode gate AND await the forward so the response carries the real delivery result
        /// (push_result + delivered). This lets the MCP tool route through MT — the gateway port and
        /// X-MT-Secret are resolved here in-process (item [11], Findings 1+2) — instead of POSTing to
        /// a hardcoded :5100 with no secret, which 403'd whenever NotificationSecret was set.
        /// </param>
        [HttpPost]
        public async System.Threading.Tasks.Task<IActionResult> PostNotification([FromBody] NotificationRequest request, [FromQuery] bool skipPush = false, [FromQuery] bool forcePush = false)
        {
            if (string.IsNullOrWhiteSpace(request.NotificationType))
                return Problem(detail: "notification_type is required", statusCode: 400);

            // Input length validation
            if (request.NotificationType?.Length > 100)
                return Problem(detail: "notification_type exceeds 100 characters", statusCode: 400);
            if (request.Title?.Length > 500)
                return Problem(detail: "title exceeds 500 characters", statusCode: 400);
            if (request.Message?.Length > 10000)
                return Problem(detail: "message exceeds 10000 characters", statusCode: 400);
            if (request.SessionId?.Length > 100)
                return Problem(detail: "session_id exceeds 100 characters", statusCode: 400);
            if (request.AgentName?.Length > 200)
                return Problem(detail: "agent_name exceeds 200 characters", statusCode: 400);
            if (request.Cwd?.Length > 1000)
                return Problem(detail: "cwd exceeds 1000 characters", statusCode: 400);
            if (request.ProjectName?.Length > 200)
                return Problem(detail: "project_name exceeds 200 characters", statusCode: 400);

            // Rate limiting: sliding window, 100 per minute (locked for atomic check-and-enqueue)
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-1);
            lock (_rateLimitWindow)
            {
                while (_rateLimitWindow.TryPeek(out var oldest) && oldest < cutoff)
                    _rateLimitWindow.TryDequeue(out _);
                if (_rateLimitWindow.Count >= MaxNotificationsPerMinute)
                    return Problem(detail: "Rate limit exceeded (100 notifications/minute)", statusCode: 429);
                _rateLimitWindow.Enqueue(now);
            }

            string id = _broker.RecordNotification(
                request.NotificationType,
                request.Title ?? request.NotificationType,
                request.Message,
                request.SessionId,
                request.AgentName,
                request.Cwd);

            // Explicit push: await the forward (bypassing the remote-mode gate) and return the
            // real delivery result so the caller can report accurate success, not a bare HTTP 200.
            if (forcePush)
            {
                var outcome = await ForwardToGatewayAsync(request, id, bypassRemoteModeGate: true);
                return Ok(new
                {
                    id,
                    forwarded = outcome.Forwarded,
                    delivered = outcome.Delivered,
                    reason = outcome.Reason,
                    push_result = outcome.PushResult,
                });
            }

            // Fire-and-forget forward to the gateway for phone push (skip if caller already pushed).
            if (!skipPush)
                _ = ForwardToGatewayAsync(request, id, bypassRemoteModeGate: false);

            return Ok(new { id });
        }

        /// <summary>
        /// GET /api/notifications — Query notification history.
        /// </summary>
        [HttpGet]
        public IActionResult GetNotifications(
            [FromQuery] int limit = 50,
            [FromQuery] bool unreadOnly = false)
        {
            if (limit < 1) limit = 50;
            if (limit > 500) limit = 500;
            var notifications = _broker.GetNotificationEvents(limit, unreadOnly);
            return Ok(notifications);
        }

        /// <summary>
        /// POST /api/notifications/{id}/read — Mark a notification as read.
        /// </summary>
        [HttpPost("{id}/read")]
        public IActionResult MarkRead(string id)
        {
            _broker.MarkNotificationRead(id);
            return Ok();
        }

        /// <summary>
        /// GET /api/notifications/unread-count — Get count of unread notifications.
        /// </summary>
        [HttpGet("unread-count")]
        public IActionResult GetUnreadCount()
        {
            int count = _broker.GetUnreadNotificationCount();
            return Ok(new { count });
        }

        /// <summary>Outcome of forwarding a notification to the in-process phone gateway.</summary>
        private sealed class ForwardOutcome
        {
            /// <summary>True once the gateway accepted the POST (HTTP 2xx).</summary>
            public bool Forwarded { get; set; }

            /// <summary>True only when the gateway reports at least one device actually accepted the push.</summary>
            public bool Delivered { get; set; }

            /// <summary>Why it wasn't forwarded/delivered: remote-mode-off (hook path only), type-disabled-by-user, not-delivered, gateway-error:NNN, gateway-unreachable.</summary>
            public string Reason { get; set; }

            /// <summary>Aggregate-only delivery counts (subscriptionCount/successCount/errorCount/delivered/error) echoed for diagnostics — never the per-endpoint list.</summary>
            public object PushResult { get; set; }
        }

        private static int GetInt(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

        private async System.Threading.Tasks.Task<ForwardOutcome> ForwardToGatewayAsync(NotificationRequest request, string id, bool bypassRemoteModeGate)
        {
            var outcome = new ForwardOutcome();

            // remoteMode gate — no phone pushes when the user is at the desk. Explicit pushes
            // (forcePush) bypass it: the agent asked to notify the phone regardless of presence.
            if (!bypassRemoteModeGate && !_broker.IsRemoteMode)
            {
                outcome.Reason = "remote-mode-off";
                return outcome;
            }

            try
            {
                var payload = new
                {
                    id,
                    notification_type = request.NotificationType,
                    title = request.Title ?? request.NotificationType,
                    message = request.Message,
                    session_id = request.SessionId,
                    agent_name = request.AgentName,
                    project_name = request.ProjectName,
                    cwd = request.Cwd,
                    created_at = DateTime.UtcNow.ToString("o")
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                // The MultiRemote phone gateway now runs IN THIS PROCESS (task ca6c5344). Forward
                // to its configured loopback port and, when a NotificationSecret is set, attach the
                // matching X-MT-Secret header — otherwise enabling the secret would 403 every push
                // (pipeline Run-1 cross-model HIGH). GatewayRuntimeConfig defaults keep the old
                // :5100 / no-secret behaviour when the gateway never started.
                var gatewayUrl = $"http://localhost:{MultiTerminal.API.Gateway.GatewayRuntimeConfig.Port}/api/notifications/runtime";
                using var forwardRequest = new HttpRequestMessage(HttpMethod.Post, gatewayUrl) { Content = content };
                var notifSecret = MultiTerminal.API.Gateway.GatewayRuntimeConfig.NotificationSecret;
                if (!string.IsNullOrEmpty(notifSecret))
                    forwardRequest.Headers.TryAddWithoutValidation("X-MT-Secret", notifSecret);

                var response = await _httpClient.SendAsync(forwardRequest);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    outcome.Reason = $"gateway-error:{(int)response.StatusCode}";
                    _broker.DebugLogService?.Warning("NotificationsController",
                        $"Gateway rejected notification {id} with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                    return outcome;
                }

                outcome.Forwarded = true;

                // Parse the gateway's delivery result so callers don't treat a bare 200 as success.
                // Body shapes: { pushed:bool, push_result:{successCount,subscriptionCount,errorCount,...} }
                //          or  { suppressed:true, notification_type } when the user disabled this type.
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    // User toggled this notification type off — a deliberate suppression, NOT a failure.
                    // Distinct reason so the MCP tool doesn't report "not delivered" over an intended mute
                    // (pipeline Run-5 cross-model HIGH: false-negative would trigger agent retries/escalation).
                    if (root.TryGetProperty("suppressed", out var supEl) && supEl.ValueKind == JsonValueKind.True)
                    {
                        outcome.Reason = "type-disabled-by-user";
                        return outcome;
                    }

                    if (root.TryGetProperty("pushed", out var pushedEl) && pushedEl.ValueKind == JsonValueKind.True)
                        outcome.Delivered = true;

                    // Echo ONLY the aggregate counts, never the per-endpoint Results list — push endpoint
                    // URLs are a sender capability and the :5050 caller doesn't need them (pipeline Run-5
                    // security MEDIUM). The MCP tool consumes subscriptionCount/successCount/errorCount only.
                    if (root.TryGetProperty("push_result", out var prEl) && prEl.ValueKind == JsonValueKind.Object)
                    {
                        outcome.PushResult = new
                        {
                            subscriptionCount = GetInt(prEl, "subscriptionCount"),
                            successCount = GetInt(prEl, "successCount"),
                            errorCount = GetInt(prEl, "errorCount"),
                            delivered = prEl.TryGetProperty("delivered", out var dEl) && dEl.ValueKind == JsonValueKind.True,
                            error = prEl.TryGetProperty("error", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null,
                        };
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON 2xx body — treat as forwarded but delivery-unknown.
                }

                if (!outcome.Delivered && string.IsNullOrEmpty(outcome.Reason))
                    outcome.Reason = "not-delivered";

                return outcome;
            }
            catch (Exception ex)
            {
                // Gateway may not be running — log at debug level for diagnostics.
                outcome.Reason = "gateway-unreachable";
                _broker.DebugLogService?.Info("NotificationsController",
                    $"Gateway forward failed for notification {id}: {ex.Message}");
                return outcome;
            }
        }
    }

    public class NotificationRequest
    {
        [JsonPropertyName("notification_type")]
        public string NotificationType { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }
        [JsonPropertyName("agent_name")]
        public string AgentName { get; set; }
        public string Cwd { get; set; }

        // Optional project name used by the gateway to build the phone notification title
        // ("{agent} — {project}"). Carried through so the MCP tool's project_name isn't dropped.
        [JsonPropertyName("project_name")]
        public string ProjectName { get; set; }
    }
}
