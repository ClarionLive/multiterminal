using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// Incrementally sync sessions from a Claude project folder.
        /// Skips already-imported sessions for fast re-sync.
        /// </summary>
        [HttpPost("sync")]
        public IActionResult SyncSessions([FromBody] SyncSessionsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ClaudeProjectPath))
                return BadRequest(new { error = "claudeProjectPath is required" });

            // Guard against path traversal
            string allowedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            string fullPath = Path.GetFullPath(request.ClaudeProjectPath);
            if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "claudeProjectPath must be within the Claude projects directory" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var result = service.SyncNewSessions(
                request.ClaudeProjectPath,
                request.AgentName ?? "Unknown",
                request.TaskId ?? "__unlinked__"
            );

            return Ok(new
            {
                success = true,
                imported = result.Imported,
                skipped = result.Skipped,
                failed = result.Failed,
                total = result.Total
            });
        }

        /// <summary>
        /// On-demand sync for a project by filesystem path (resolves to Claude project folder).
        /// Called by /clear handler to ensure the previous session's lineage is imported
        /// before the new session queries for it.
        /// </summary>
        [HttpPost("sync-project")]
        public IActionResult SyncProject([FromBody] SyncProjectRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ProjectPath))
                return BadRequest(new { error = "projectPath is required" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            string claudeFolder = SessionLineageService.GetClaudeProjectFolder(request.ProjectPath);
            if (claudeFolder == null)
                return NotFound(new { error = "Claude project folder not found for the given path" });

            var result = service.SyncNewSessions(claudeFolder, request.TerminalName ?? "Unknown");
            return Ok(new
            {
                success = true,
                imported = result.Imported,
                skipped = result.Skipped,
                failed = result.Failed,
                total = result.Total
            });
        }

        /// <summary>
        /// Returns the most recent session for a project folder.
        /// If the session has no cached summary, also returns the last 10 assistant messages
        /// so the caller can generate a summary lazily.
        /// </summary>
        [HttpGet("latest")]
        public IActionResult GetLatestSession(
            [FromQuery] string projectPath,
            [FromQuery] string agentName = null,
            [FromQuery] string excludeSessionId = null,
            [FromQuery] int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return BadRequest(new { error = "projectPath is required" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var session = service.GetMostRecentSessionForProject(projectPath, agentName, excludeSessionId, skip);
            if (session == null)
                return NotFound(new { error = "No session found for the given project path" });

            // Only fetch recent messages when there is no cached summary
            IList<object> recentMessages = null;
            if (string.IsNullOrEmpty(session.Summary))
            {
                var msgs = service.GetRecentSessionMessages(session.SessionId, limit: 10);
                recentMessages = msgs.Cast<object>().ToList();
            }

            return Ok(new
            {
                session,
                summary = session.Summary,
                recentMessages
            });
        }

        /// <summary>
        /// Returns sessions that have no cached summary for a given project path.
        /// Used by agents to batch-generate summaries at session start.
        /// </summary>
        [HttpGet("unsummarized")]
        public IActionResult GetUnsummarizedSessions(
            [FromQuery] string projectPath,
            [FromQuery] int limit = 10)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return BadRequest(new { error = "projectPath is required" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var sessions = service.GetUnsummarizedSessions(projectPath, limit);
            return Ok(new
            {
                sessions,
                count = sessions.Count
            });
        }

        /// <summary>
        /// Saves a generated summary to a session lineage record.
        /// </summary>
        [HttpPut("{sessionId}/summary")]
        public IActionResult UpdateSessionSummary(string sessionId, [FromBody] UpdateSessionSummaryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Summary))
                return BadRequest(new { error = "summary is required" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            int rows = service.UpdateSessionSummary(sessionId, request.Summary);
            if (rows == 0)
                return NotFound(new { error = $"Session '{sessionId}' not found" });

            return Ok(new { success = true });
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

        /// <summary>
        /// Backfills heuristic summaries. Without ?regenerate=true, only fills null/empty summaries.
        /// With ?regenerate=true, also overwrites existing summaries that match known-junk patterns
        /// (tool-result echoes, hook markers, trace prefixes).
        /// </summary>
        [HttpPost("backfill-summaries")]
        public IActionResult BackfillSummaries([FromQuery] bool regenerate = false)
        {
            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            int updated = service.BackfillSummaries(regenerate);
            return Ok(new { success = true, updated, regenerate });
        }

        /// <summary>
        /// Registers a new session as 'open' in the lifecycle pipeline.
        /// Also closes any previous 'open' sessions for the same agent+project.
        /// Call this at session start before any JSONL processing.
        /// </summary>
        [HttpPost("register")]
        public IActionResult RegisterSession([FromBody] RegisterSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.SessionId))
                return BadRequest(new { error = "sessionId is required" });
            if (string.IsNullOrWhiteSpace(request?.AgentName))
                return BadRequest(new { error = "agentName is required" });

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var record = service.RegisterSession(request.SessionId, request.AgentName, request.ProjectPath);
            if (record == null)
                return StatusCode(500, new { error = "Failed to register session" });

            return Ok(new
            {
                success = true,
                sessionId = record.SessionId,
                processingStatus = record.ProcessingStatus,
                message = "Session registered as 'open'. Previous sessions for this agent closed."
            });
        }

        /// <summary>
        /// Drives a session through the processing pipeline to 'complete'.
        /// Checks the current processing_status and runs remaining steps on demand.
        /// Returns the fully processed session record with summary.
        /// </summary>
        [HttpPost("{sessionId}/ensure-ready")]
        public IActionResult EnsureSessionReady(string sessionId)
        {
            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var record = service.EnsureSessionReady(sessionId, _broker.SessionMemoryDb);
            if (record == null)
                return NotFound(new { error = $"Session '{sessionId}' not found" });

            return Ok(new
            {
                session = record,
                summary = record.Summary,
                processingStatus = record.ProcessingStatus
            });
        }
    }

    // Request models

    public class SyncSessionsRequest
    {
        public string ClaudeProjectPath { get; set; }
        public string AgentName { get; set; }
        public string TaskId { get; set; }
    }

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

    public class UpdateSessionSummaryRequest
    {
        public string Summary { get; set; }
    }

    public class SyncProjectRequest
    {
        public string ProjectPath { get; set; }
        public string TerminalName { get; set; }
    }

    public class RegisterSessionRequest
    {
        public string SessionId { get; set; }
        public string AgentName { get; set; }
        public string ProjectPath { get; set; }
    }
}
