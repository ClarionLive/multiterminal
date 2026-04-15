using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/notifications")]
    public class NotificationsController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly TaskDatabase _taskDb;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        // Rate limiting: sliding window of timestamps per minute
        private static readonly ConcurrentQueue<DateTime> _rateLimitWindow = new();
        private const int MaxNotificationsPerMinute = 100;

        public NotificationsController(MessageBroker broker, TaskDatabase taskDb)
        {
            _broker = broker;
            _taskDb = taskDb;
        }

        /// <summary>
        /// POST /api/notifications — Receive a Claude Code runtime notification from the hook.
        /// Stores in DB, fires broker event, and forwards to ClaudeRemote for phone push.
        /// </summary>
        [HttpPost]
        public IActionResult PostNotification([FromBody] NotificationRequest request, [FromQuery] bool skipPush = false)
        {
            if (string.IsNullOrWhiteSpace(request.NotificationType))
                return BadRequest(new { error = "notification_type is required" });

            // Input length validation
            if (request.NotificationType?.Length > 100)
                return BadRequest(new { error = "notification_type exceeds 100 characters" });
            if (request.Title?.Length > 500)
                return BadRequest(new { error = "title exceeds 500 characters" });
            if (request.Message?.Length > 10000)
                return BadRequest(new { error = "message exceeds 10000 characters" });
            if (request.SessionId?.Length > 100)
                return BadRequest(new { error = "session_id exceeds 100 characters" });
            if (request.AgentName?.Length > 200)
                return BadRequest(new { error = "agent_name exceeds 200 characters" });
            if (request.Cwd?.Length > 1000)
                return BadRequest(new { error = "cwd exceeds 1000 characters" });

            // Rate limiting: sliding window, 100 per minute (locked for atomic check-and-enqueue)
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-1);
            lock (_rateLimitWindow)
            {
                while (_rateLimitWindow.TryPeek(out var oldest) && oldest < cutoff)
                    _rateLimitWindow.TryDequeue(out _);
                if (_rateLimitWindow.Count >= MaxNotificationsPerMinute)
                    return StatusCode(429, new { error = "Rate limit exceeded (100 notifications/minute)" });
                _rateLimitWindow.Enqueue(now);
            }

            string id = _broker.RecordNotification(
                request.NotificationType,
                request.Title ?? request.NotificationType,
                request.Message,
                request.SessionId,
                request.AgentName,
                request.Cwd);

            // Fire-and-forget forward to ClaudeRemote for phone push (skip if caller already pushed)
            if (!skipPush)
                _ = ForwardToClaudeRemoteAsync(request, id);

            return Ok(new { success = true, id });
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
            var notifications = _taskDb.GetNotificationEvents(limit, unreadOnly);
            return Ok(notifications);
        }

        /// <summary>
        /// POST /api/notifications/{id}/read — Mark a notification as read.
        /// </summary>
        [HttpPost("{id}/read")]
        public IActionResult MarkRead(string id)
        {
            _taskDb.MarkNotificationRead(id);
            return Ok(new { success = true });
        }

        /// <summary>
        /// GET /api/notifications/unread-count — Get count of unread notifications.
        /// </summary>
        [HttpGet("unread-count")]
        public IActionResult GetUnreadCount()
        {
            int count = _taskDb.GetUnreadNotificationCount();
            return Ok(new { count });
        }

        private async System.Threading.Tasks.Task ForwardToClaudeRemoteAsync(NotificationRequest request, string id)
        {
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
                    cwd = request.Cwd,
                    created_at = DateTime.UtcNow.ToString("o")
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // ClaudeRemote runs on port 5100
                await _httpClient.PostAsync("http://localhost:5100/api/notifications/runtime", content);
            }
            catch (Exception ex)
            {
                // ClaudeRemote may not be running — log at debug level for diagnostics
                _broker.DebugLogService?.Info("NotificationsController",
                    $"ClaudeRemote forward failed for notification {id}: {ex.Message}");
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
    }
}
