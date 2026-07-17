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
            => Problem(detail: "SessionLineageService is not available", statusCode: 503);

        /// <summary>
        /// Import a Claude Code session JSONL file and link it to a kanban task.
        /// </summary>
        [HttpPost("import")]
        public IActionResult ImportSession([FromBody] ImportSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SessionFilePath))
                return Problem(detail: "sessionFilePath is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(request.TaskId))
                return Problem(detail: "taskId is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(request.AgentName))
                return Problem(detail: "agentName is required", statusCode: 400);

            // Guard against path traversal — only allow files within the user's .claude/projects directory
            string allowedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            string fullPath = Path.GetFullPath(request.SessionFilePath);
            if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                return Problem(detail: "sessionFilePath must be within the Claude projects directory", statusCode: 400);

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
                return Problem(detail: result.Error, statusCode: 400);

            return Ok(new
            {
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
                return Problem(detail: "claudeProjectPath is required", statusCode: 400);

            // Guard against path traversal
            string allowedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            string fullPath = Path.GetFullPath(request.ClaudeProjectPath);
            if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                return Problem(detail: "claudeProjectPath must be within the Claude projects directory", statusCode: 400);

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
                return Problem(detail: "projectPath is required", statusCode: 400);

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            string claudeFolder = SessionLineageService.GetClaudeProjectFolder(request.ProjectPath);
            if (claudeFolder == null)
                return Problem(detail: "Claude project folder not found for the given path", statusCode: 404);

            var result = service.SyncNewSessions(claudeFolder, request.TerminalName ?? "Unknown");
            return Ok(new
            {
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
                return Problem(detail: "projectPath is required", statusCode: 400);

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var session = service.GetMostRecentSessionForProject(projectPath, agentName, excludeSessionId, skip);
            if (session == null)
                return Problem(detail: "No session found for the given project path", statusCode: 404);

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
                return Problem(detail: "projectPath is required", statusCode: 400);

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
                return Problem(detail: "summary is required", statusCode: 400);

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            int rows = service.UpdateSessionSummary(sessionId, request.Summary);
            if (rows == 0)
                return Problem(detail: $"Session '{sessionId}' not found", statusCode: 404);

            return Ok();
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
                return Problem(detail: "At least one of taskId or query is required", statusCode: 400);

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
            return Ok(new { updated, regenerate });
        }

        /// <summary>
        /// Registers a new session as 'open' in the lifecycle pipeline.
        /// Also closes any previous 'open' sessions for the same agent+project.
        /// Call this at session start before any JSONL processing.
        /// </summary>
        [HttpPost("register")]
        public async System.Threading.Tasks.Task<IActionResult> RegisterSession([FromBody] RegisterSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.SessionId))
                return Problem(detail: "sessionId is required", statusCode: 400);
            if (string.IsNullOrWhiteSpace(request?.AgentName))
                return Problem(detail: "agentName is required", statusCode: 400);

            var service = GetService();
            if (service == null)
                return ServiceUnavailable();

            var record = service.RegisterSession(request.SessionId, request.AgentName, request.ProjectPath);
            if (record == null)
                return Problem(detail: "Failed to register session", statusCode: 500);

            // Task 94356803: surface the registering project's unresolved janitor
            // findings (pending merges, stranded worktree dirs) at session start, so
            // the next agent booting on an affected project sees them even if the
            // inbox alert scrolled by. Best-effort and null when the folder doesn't
            // resolve to a registered project — never blocks registration.
            object janitorFindings = await BuildJanitorFindingsAsync(request.ProjectPath).ConfigureAwait(false);

            return Ok(new
            {
                sessionId = record.SessionId,
                processingStatus = record.ProcessingStatus,
                message = "Session registered as 'open'. Previous sessions for this agent closed.",
                janitorFindings,
            });
        }

        /// <summary>
        /// Build the session-start janitor-findings block for the project containing
        /// <paramref name="projectPath"/> (containment resolution, same helper as the
        /// statusline poll — a worktree subfolder resolves to its parent project).
        /// Returns null when the path is empty, unregistered, or the janitor is absent;
        /// otherwise an object with the /stranded-style explicit <c>status</c> so a
        /// failed scan ("unavailable"/"partial") is never mistaken for "no findings".
        /// </summary>
        private async System.Threading.Tasks.Task<object> BuildJanitorFindingsAsync(string projectPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectPath)) return null;

                var janitor = _broker?.WorktreeJanitor;
                if (janitor == null) return null;

                var registered = _broker.ProjectService?.GetAllRegisteredProjects();
                var entry = MultiTerminal.Services.ProjectPathResolver.ResolveByContainment(registered, projectPath);
                if (entry == null) return null; // unregistered folder — nothing to scope findings to

                // Explicit null-Path guard (pipeline Run-1 debugger LOW): the resolver
                // only matches path-bearing entries today, but a resolved entry without
                // a Path cannot scope stranded dirs — treat like unregistered rather
                // than silently building a bare-"\" prefix that matches nothing.
                if (string.IsNullOrWhiteSpace(entry.Path)) return null;

                // Pending merges scoped to this project via each finding's task.ProjectId.
                var mergeScan = await janitor.ScanPendingMergesAsync(id => _broker.TryGetProjectPathForTask(id)).ConfigureAwait(false);
                var pendingMerges = new System.Collections.Generic.List<object>();
                foreach (var pm in mergeScan.Items)
                {
                    var task = _broker.GetTask(pm.TaskId);
                    if (!string.Equals(task?.ProjectId, entry.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    pendingMerges.Add(new
                    {
                        taskId = pm.TaskId,
                        taskTitle = task?.Title,
                        branchName = pm.BranchName,
                        repoRoot = pm.RepoRoot,
                    });
                }

                // Stranded dirs scoped by path containment under the project root
                // (segment-bounded so a sibling name-prefix doesn't match).
                var strandedScan = await janitor.ScanStrandedDirsAsync().ConfigureAwait(false);
                string rootPrefix = entry.Path.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
                var strandedDirs = new System.Collections.Generic.List<string>();
                foreach (var dir in strandedScan.Dirs)
                {
                    if (!string.IsNullOrEmpty(dir)
                        && dir.Replace('/', System.IO.Path.DirectorySeparatorChar)
                              .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        strandedDirs.Add(dir);
                    }
                }

                // Per-PROJECT partiality (pipeline Run-1 debugger MEDIUM): the scans are
                // system-wide, so their global Complete flags would let an unrelated
                // project's failure mislabel THIS fully-scanned project as partial on
                // every register. Attribute each skip: a skipped merge record counts only
                // when its task belongs to this project (or can't be attributed at all —
                // an honest unknown degrades everyone); a skipped stranded group counts
                // only when its repo root IS this project's root.
                bool mergePartial = false;
                if (!mergeScan.Complete)
                {
                    // Listing-level failures leave no per-record ids — unattributable.
                    if (mergeScan.SkippedRecords > mergeScan.SkippedTaskIds.Count) mergePartial = true;
                    foreach (var skippedId in mergeScan.SkippedTaskIds)
                    {
                        if (mergePartial) break;
                        MultiTerminal.MCPServer.Models.KanbanTask skippedTask = null;
                        try { skippedTask = _broker.GetTask(skippedId); } catch { /* treat as unattributable */ }
                        if (skippedTask == null || string.IsNullOrEmpty(skippedTask.ProjectId)
                            || string.Equals(skippedTask.ProjectId, entry.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            mergePartial = true;
                        }
                    }
                }

                bool strandedPartial = false;
                if (!strandedScan.Complete)
                {
                    string projectRoot = entry.Path.TrimEnd('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar);
                    if (strandedScan.SkippedGroups > strandedScan.SkippedGroupRepoRoots.Count) strandedPartial = true;
                    foreach (var skippedRoot in strandedScan.SkippedGroupRepoRoots)
                    {
                        if (strandedPartial) break;
                        if (string.IsNullOrEmpty(skippedRoot))
                        {
                            strandedPartial = true;
                            continue;
                        }

                        // Task e85eba13: the skipped entry may be a worktree PARENT
                        // dir rather than a repo root (an underivable-layout skip
                        // carries the only path it has). Segment-bounded containment
                        // — equal to the project root, or under it — attributes both
                        // shapes; bare equality missed the parent-dir shape entirely.
                        string skipped = skippedRoot.Replace('/', System.IO.Path.DirectorySeparatorChar).TrimEnd('\\');
                        if (string.Equals(skipped, projectRoot, StringComparison.OrdinalIgnoreCase)
                            || skipped.StartsWith(projectRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            strandedPartial = true;
                        }
                    }
                }

                bool complete = !mergePartial && !strandedPartial;
                if (complete && pendingMerges.Count == 0 && strandedDirs.Count == 0)
                    return null; // clean bill of health — keep the register response lean

                return new
                {
                    status = complete ? "ok" : "partial",
                    projectId = entry.Id,
                    projectName = entry.Name,
                    pendingMerges,
                    strandedDirs,
                };
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Warning("SessionLineageController", $"janitor findings enrichment failed: {ex.Message}");
                return new { status = "unavailable" };
            }
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
                return Problem(detail: $"Session '{sessionId}' not found", statusCode: 404);

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
