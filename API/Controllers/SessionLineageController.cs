using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST endpoints for session lineage — importing session transcripts,
    /// querying session chains, and full-text searching across session messages.
    /// </summary>
    [ApiController]
    [Route("api/session-lineage")]
    public class SessionLineageController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public SessionLineageController(MessageBroker broker)
        {
            _broker = broker;
        }

        // Centralized service accessor — returns null when the service hasn't been wired yet.
        private SessionLineageService GetService() => _broker.SessionLineageService;

        private IActionResult ServiceUnavailable()
            => StatusCode(503, new { error = "SessionLineageService is not available" });

        /// <summary>
        /// Import a Claude Code session JSONL file and link it to a kanban task.
        /// </summary>
        [HttpPost("import")]
        public IActionResult ImportSession([FromBody] ImportSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SessionFilePath))
                return BadRequest(new { error = "sessionFilePath is required" });

            if (string.IsNullOrWhiteSpace(request.TaskId))
                return BadRequest(new { error = "taskId is required" });

            if (string.IsNullOrWhiteSpace(request.AgentName))
                return BadRequest(new { error = "agentName is required" });

            // Guard against path traversal — only allow files within the user's .claude/projects directory
            string allowedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            string fullPath = Path.GetFullPath(request.SessionFilePath);
            if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "sessionFilePath must be within the Claude projects directory" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var result = service.ImportSession(
                request.SessionFilePath,
                request.TaskId,
                request.AgentName,
                request.ParentSessionId,
                request.SessionType
            );

            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new
            {
                success = true,
                sessionId = result.SessionId,
                messageCount = result.MessageCount
            });
        }

        /// <summary>
        /// Get all session lineage records for a task, newest first.
        /// </summary>
        [HttpGet("task/{taskId}/sessions")]
        public IActionResult GetSessionsByTask(string taskId)
        {
            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var sessions = service.GetSessionsByTask(taskId);
            return Ok(sessions);
        }

        /// <summary>
        /// Get the full lineage chain for a session, ordered root to leaf.
        /// Walks parent_session_id links to reconstruct agent cycling history.
        /// </summary>
        [HttpGet("{sessionId}/chain")]
        public IActionResult GetSessionChain(string sessionId)
        {
            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var chain = service.GetSessionLineage(sessionId);
            return Ok(chain);
        }

        /// <summary>
        /// Full-text search across session messages.
        /// Uses SQLite FTS5 when available, falls back to LIKE search.
        /// </summary>
        [HttpGet("search")]
        public IActionResult SearchSessionHistory(
            [FromQuery] string taskId,
            [FromQuery] string query,
            [FromQuery] string role,
            [FromQuery] string agentName,
            [FromQuery] int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(query))
                return BadRequest(new { error = "At least one of taskId or query is required" });

            if (limit <= 0 || limit > 1000) limit = 1000;

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var results = service.SearchSessionMessages(
                taskId: taskId,
                query: query,
                role: role,
                agentName: agentName,
                limit: limit
            );

            return Ok(new
            {
                results,
                totalCount = results.Count
            });
        }
    }

    // Request models

    public class ImportSessionRequest
    {
        public string SessionFilePath { get; set; }
        public string TaskId { get; set; }
        public string AgentName { get; set; }
        /// <summary>Semantic type label (e.g., "coding", "review", "testing"). Optional.</summary>
        public string SessionType { get; set; }
        /// <summary>Parent session GUID for lineage chaining. Optional.</summary>
        public string ParentSessionId { get; set; }
    }
}
