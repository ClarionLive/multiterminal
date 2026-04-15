using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Result returned by ImportSession.
    /// </summary>
    public class ImportSessionResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string SessionId { get; set; }
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// Result of an incremental session sync operation.
    /// </summary>
    public class SyncResult
    {
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Total => Imported + Skipped + Failed;
    }

    /// <summary>
    /// Imports Claude Code session JSONL files into the session_lineage and session_messages
    /// tables in TaskDatabase. Tracks parent/child session relationships (lineage chains),
    /// extracts user/assistant messages with flattened content arrays, and exposes
    /// search through the database layer (FTS5 or LIKE fallback).
    /// </summary>
    public class SessionLineageService
    {
        private readonly TaskDatabase _db;

        public SessionLineageService(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Returns true if FTS5 full-text search is available in the SQLite build.
        /// </summary>
        public bool IsFts5Available => _db.IsFts5Available;

        #region Import

        /// <summary>
        /// Imports a single session JSONL file, links it to a task, and optionally
        /// sets a parent session ID and session type label.
        /// Returns an ImportSessionResult with Success flag, SessionId, and MessageCount.
        /// </summary>
        /// <param name="sessionFilePath">Absolute path to the .jsonl file.</param>
        /// <param name="taskId">Kanban task ID to link this session to.</param>
        /// <param name="agentName">Agent name to associate (e.g. terminal display name).</param>
        /// <param name="parentSessionId">Parent session's GUID for lineage chaining. Optional.</param>
        /// <param name="sessionType">Semantic type label (e.g. "coding", "review"). Optional.</param>
        public ImportSessionResult ImportSession(
            string sessionFilePath,
            string taskId,
            string agentName,
            string parentSessionId = null,
            string sessionType = null)
        {
            if (!File.Exists(sessionFilePath))
                return new ImportSessionResult { Success = false, Error = $"File not found: {sessionFilePath}" };

            try
            {
                var parsed = ParseJsonlFile(sessionFilePath, agentName, taskId, parentSessionId, sessionType);

                if (parsed.Lineage == null)
                    return new ImportSessionResult { Success = false, Error = "No user/assistant messages found in file" };

                // Auto-generate heuristic summary if none exists
                if (string.IsNullOrEmpty(parsed.Lineage.Summary))
                    parsed.Lineage.Summary = GenerateHeuristicSummaryFromMessages(parsed.Messages);

                // Persist lineage record (upsert by session_id)
                _db.SaveSessionLineage(parsed.Lineage);

                // Bulk-insert messages (delete+replace for idempotent re-import)
                if (parsed.Messages.Count > 0)
                    _db.SaveSessionMessages(parsed.Lineage.SessionId, parsed.Messages);

                return new ImportSessionResult
                {
                    Success = true,
                    SessionId = parsed.Lineage.SessionId,
                    MessageCount = parsed.Messages.Count
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionLineageService] ImportSession error: {ex}");
                return new ImportSessionResult { Success = false, Error = "Failed to import session. Check server logs for details." };
            }
        }

        /// <summary>
        /// Imports all JSONL files found under a Claude project folder (top-level + subagents).
        /// </summary>
        /// <param name="claudeProjectPath">
        ///   Path like C:\Users\&lt;username&gt;\.claude\projects\H--DevLaptop-...\
        ///   (the per-project folder inside .claude\projects\)
        /// </param>
        /// <param name="agentName">Default agent name for imported sessions.</param>
        /// <param name="taskId">Optional kanban task ID to link all sessions to.</param>
        /// <returns>Number of sessions successfully imported.</returns>
        public int ImportAllSessionsFromFolder(string claudeProjectPath, string agentName, string taskId = null)
        {
            if (!Directory.Exists(claudeProjectPath))
                return 0;

            int count = 0;

            // Top-level JSONL files — each file name is the session GUID
            foreach (var file in Directory.GetFiles(claudeProjectPath, "*.jsonl"))
            {
                try
                {
                    var r = ImportSession(file, taskId, agentName);
                    if (r.Success) count++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionLineageService] Failed to import {file}: {ex.Message}");
                }
            }

            // Subagent JSONL files inside <sessionId>/subagents/
            foreach (var sessionDir in Directory.GetDirectories(claudeProjectPath))
            {
                string subagentsDir = Path.Combine(sessionDir, "subagents");
                if (!Directory.Exists(subagentsDir))
                    continue;

                foreach (var file in Directory.GetFiles(subagentsDir, "*.jsonl"))
                {
                    try
                    {
                        var r = ImportSession(file, taskId, agentName, sessionType: "subagent");
                        if (r.Success) count++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionLineageService] Failed to import subagent {file}: {ex.Message}");
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Converts a project filesystem path to the corresponding .claude/projects/ folder path.
        /// Returns null if the folder doesn't exist.
        /// </summary>
        public static string GetClaudeProjectFolder(string projectPath)
        {
            string claudeProjectsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");

            if (!Directory.Exists(claudeProjectsRoot))
                return null;

            // Claude's folder name format: drive letter + path segments joined with dashes
            string normalized = projectPath.Replace("\\", "/").TrimEnd('/');
            string folderName = normalized.Replace(":", "-").Replace("/", "-");

            string claudeProjectPath = Path.Combine(claudeProjectsRoot, folderName);
            return Directory.Exists(claudeProjectPath) ? claudeProjectPath : null;
        }

        /// <summary>
        /// Incrementally syncs sessions from a Claude project folder, skipping those
        /// already imported. Much faster than ImportAllSessionsFromFolder for large folders.
        /// </summary>
        /// <param name="claudeProjectPath">Path to the .claude/projects/{project-folder}/ directory.</param>
        /// <param name="defaultAgentName">Agent name for sessions with no known agent.</param>
        /// <param name="defaultTaskId">Task ID for unlinked sessions. Defaults to "__unlinked__".</param>
        /// <returns>A SyncResult with counts of imported, skipped, and failed sessions.</returns>
        /// <summary>
        /// Threshold for considering a session file still actively being written.
        /// Files modified within this window are skipped to avoid importing partial data.
        /// </summary>
        private static readonly TimeSpan ActiveFileThreshold = TimeSpan.FromSeconds(120);

        public SyncResult SyncNewSessions(string claudeProjectPath, string defaultAgentName = "Unknown", string defaultTaskId = "__unlinked__")
        {
            var result = new SyncResult();

            if (!Directory.Exists(claudeProjectPath))
                return result;

            // Get already-imported session IDs in one query
            var importedIds = _db.GetImportedSessionIds();

            var now = DateTime.UtcNow;

            // Top-level JSONL files
            foreach (var file in Directory.GetFiles(claudeProjectPath, "*.jsonl"))
            {
                string sessionId = Path.GetFileNameWithoutExtension(file);
                if (importedIds.Contains(sessionId))
                {
                    result.Skipped++;
                    continue;
                }

                // Skip files modified recently — likely still being written by a live session.
                // Uses file-age heuristic instead of session_agent_map which is unreliable.
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if ((now - lastWrite) < ActiveFileThreshold)
                    {
                        result.Skipped++;
                        continue;
                    }
                }
                catch
                {
                    // If we can't read file time, try to import anyway
                }

                try
                {
                    // Look up agent name from hook-written mapping, fall back to default
                    string agentName = _db.GetSessionAgentName(sessionId) ?? defaultAgentName;

                    var r = ImportSession(file, defaultTaskId, agentName);
                    if (r.Success)
                        result.Imported++;
                    else
                        result.Failed++;
                }
                catch
                {
                    result.Failed++;
                }
            }

            // Subagent JSONL files inside sessionId/subagents/
            foreach (var sessionDir in Directory.GetDirectories(claudeProjectPath))
            {
                string subagentsDir = Path.Combine(sessionDir, "subagents");
                if (!Directory.Exists(subagentsDir))
                    continue;

                foreach (var file in Directory.GetFiles(subagentsDir, "*.jsonl"))
                {
                    string sessionId = Path.GetFileNameWithoutExtension(file);
                    if (importedIds.Contains(sessionId))
                    {
                        result.Skipped++;
                        continue;
                    }

                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if ((now - lastWrite) < ActiveFileThreshold)
                        {
                            result.Skipped++;
                            continue;
                        }
                    }
                    catch { }

                    try
                    {
                        // For subagents, use parent session's agent name if available
                        string parentId = Path.GetFileName(sessionDir);
                        string agentName = _db.GetSessionAgentName(parentId) ?? defaultAgentName;

                        var r = ImportSession(file, defaultTaskId, agentName, sessionType: "subagent");
                        if (r.Success)
                            result.Imported++;
                        else
                            result.Failed++;
                    }
                    catch
                    {
                        result.Failed++;
                    }
                }
            }

            return result;
        }

        #endregion

        #region Session Lifecycle

        /// <summary>
        /// Registers a new session as 'open' in the session_lineage table.
        /// Also closes any previous 'open' sessions for the same agent+project.
        /// Called at session start (before any JSONL processing) to establish the
        /// session as a known entity in the database.
        /// </summary>
        public SessionLineageRecord RegisterSession(string sessionId, string agentName, string projectPath)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(agentName))
                return null;

            // Close any prior 'open' sessions for this agent+project
            CloseAgentSessions(agentName, projectPath, excludeSessionId: sessionId);

            // Compute the expected JSONL file path
            string sessionFilePath = null;
            string claudeFolder = GetClaudeProjectFolder(projectPath);
            if (claudeFolder != null)
                sessionFilePath = Path.Combine(claudeFolder, $"{sessionId}.jsonl");

            var record = new SessionLineageRecord
            {
                SessionId = sessionId,
                AgentName = agentName,
                SessionType = "terminal",
                ProcessingStatus = "open",
                ProjectPath = projectPath,
                SessionFilePath = sessionFilePath,
                StartedAt = DateTime.UtcNow.ToString("O"),
                CreatedAt = DateTime.UtcNow.ToString("O")
            };

            _db.SaveSessionLineage(record);
            return record;
        }

        /// <summary>
        /// Closes all 'open' sessions for the given agent and project, optionally
        /// excluding a specific session (the current one). Sets processing_status='closed'
        /// and ended_at to now.
        /// </summary>
        public int CloseAgentSessions(string agentName, string projectPath, string excludeSessionId = null)
            => _db.CloseOpenSessions(agentName, projectPath, excludeSessionId);

        /// <summary>
        /// Drives a session through the processing pipeline to 'complete'.
        /// Checks the current processing_status and runs remaining steps:
        ///   closed → import messages → imported
        ///   imported → index chunks → indexed
        ///   indexed → generate summary → complete
        /// Returns the updated session record, or null if the session doesn't exist.
        /// Pass sessionMemoryDb to enable chunk indexing; if null, indexing is skipped.
        /// </summary>
        public SessionLineageRecord EnsureSessionReady(string sessionId, SessionMemoryDatabase sessionMemoryDb = null)
        {
            var record = _db.GetSessionById(sessionId);
            if (record == null) return null;

            // Already done
            if (record.ProcessingStatus == "complete") return record;

            // Step 1: Import messages (closed → imported)
            if (record.ProcessingStatus == "closed" || record.ProcessingStatus == "open")
            {
                // If status is still 'open', the session file should be closed by now
                // (the caller is a new session, so the previous one is definitely done)
                if (!string.IsNullOrEmpty(record.SessionFilePath) && File.Exists(record.SessionFilePath))
                {
                    try
                    {
                        var importResult = ImportSession(
                            record.SessionFilePath,
                            record.TaskId ?? "__unlinked__",
                            record.AgentName,
                            record.ParentSessionId,
                            record.SessionType);

                        if (importResult.Success)
                        {
                            _db.UpdateSessionProcessingStatus(sessionId, "imported");
                            record.ProcessingStatus = "imported";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionLifecycle] Import failed for {sessionId}: {ex.Message}");
                    }
                }
                else
                {
                    // No file path — can't import, but we can still try to summarize
                    // if messages were already imported by another path
                    var existingMessages = _db.GetRecentSessionMessages(sessionId, "assistant", 5);
                    if (existingMessages.Count > 0)
                    {
                        _db.UpdateSessionProcessingStatus(sessionId, "imported");
                        record.ProcessingStatus = "imported";
                    }
                }
            }

            // Step 2: Index chunks (imported → indexed)
            if (record.ProcessingStatus == "imported" && sessionMemoryDb != null)
            {
                if (!string.IsNullOrEmpty(record.SessionFilePath) && File.Exists(record.SessionFilePath))
                {
                    try
                    {
                        sessionMemoryDb.IndexSessionFile(
                            record.SessionFilePath,
                            record.AgentName,
                            record.ProjectPath);

                        _db.UpdateSessionProcessingStatus(sessionId, "indexed", "indexed_at");
                        record.ProcessingStatus = "indexed";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionLifecycle] Indexing failed for {sessionId}: {ex.Message}");
                        // Don't block — skip to summary generation
                        _db.UpdateSessionProcessingStatus(sessionId, "indexed", "indexed_at");
                        record.ProcessingStatus = "indexed";
                    }
                }
                else
                {
                    // No file — skip indexing
                    _db.UpdateSessionProcessingStatus(sessionId, "indexed", "indexed_at");
                    record.ProcessingStatus = "indexed";
                }
            }

            // Step 3: Generate summary (indexed → complete)
            if (record.ProcessingStatus == "indexed" || record.ProcessingStatus == "imported")
            {
                string summary = GenerateHeuristicSummary(sessionId);
                if (!string.IsNullOrEmpty(summary))
                {
                    _db.UpdateSessionSummary(sessionId, summary);
                    record.Summary = summary;
                }

                _db.UpdateSessionProcessingStatus(sessionId, "complete", "summarized_at");
                record.ProcessingStatus = "complete";
            }

            // Re-read to get latest state
            return _db.GetSessionById(sessionId);
        }

        #endregion

        #region Query

        /// <summary>
        /// Returns all sessions linked to a task, newest first.
        /// </summary>
        public List<SessionLineageRecord> GetSessionsByTask(string taskId)
            => _db.GetSessionsByTask(taskId);

        /// <summary>
        /// Walks the lineage chain for a session, returning the full ancestor list
        /// ordered oldest-first (root ancestor to given session).
        /// </summary>
        public List<SessionLineageRecord> GetSessionLineage(string sessionId)
            => _db.GetSessionLineage(sessionId);

        /// <summary>
        /// Returns the most recent session for the given project path by converting the
        /// project path to its .claude/projects/ folder and querying by file path prefix.
        /// Returns null if the project folder doesn't exist or no sessions are found.
        /// </summary>
        public SessionLineageRecord GetMostRecentSessionForProject(string projectPath, string agentName = null, string excludeSessionId = null, int skip = 0)
        {
            string claudeFolder = GetClaudeProjectFolder(projectPath);
            if (claudeFolder == null)
                return null;

            return _db.GetMostRecentSessionByFolder(claudeFolder, agentName, excludeSessionId, skip);
        }

        /// <summary>
        /// Updates the summary field on a session lineage record.
        /// Returns the number of rows affected.
        /// </summary>
        public int UpdateSessionSummary(string sessionId, string summary)
            => _db.UpdateSessionSummary(sessionId, summary);

        /// <summary>
        /// Returns sessions with no summary for a given project path.
        /// Excludes subagent sessions. Most recent first.
        /// </summary>
        public List<SessionLineageRecord> GetUnsummarizedSessions(string projectPath, int limit = 10)
        {
            var syncService = new SessionSyncService();
            string claudeFolder = syncService.GetClaudeProjectPath(projectPath);
            if (string.IsNullOrEmpty(claudeFolder))
                return new List<SessionLineageRecord>();

            return _db.GetUnsummarizedSessions(claudeFolder, limit);
        }

        /// <summary>
        /// Returns the last N assistant messages for a session, ordered most-recent-first.
        /// Provides raw material for lazy summary generation when no cached summary exists.
        /// </summary>
        public List<SessionMessageRecord> GetRecentSessionMessages(string sessionId, int limit = 20)
            => _db.GetRecentSessionMessages(sessionId, "assistant", limit);

        /// <summary>
        /// Returns sessions whose file path is within the given Claude project folder.
        /// Used by ProjectPanel to show sessions for a specific project.
        /// </summary>
        public List<SessionLineageRecord> GetSessionsByFolder(string claudeProjectFolder, int limit = 20)
            => _db.GetSessionsByFolder(claudeProjectFolder, limit);

        /// <summary>
        /// Returns all messages (user + assistant) for a given session, ordered by message_index.
        /// </summary>
        public List<SessionMessageRecord> GetSessionMessagesBySessionId(string sessionId, int limit = 500)
            => _db.GetSessionMessagesBySessionId(sessionId, limit);

        /// <summary>
        /// Searches session messages with optional filters for taskId, role, and agentName.
        /// Uses FTS5 if available, LIKE otherwise. When query is null/empty, returns
        /// all messages matching the other filters.
        /// </summary>
        /// <param name="taskId">Filter to sessions linked to this task. Optional.</param>
        /// <param name="query">Full-text search query. Pass null/empty to list without searching.</param>
        /// <param name="role">Filter to "user" or "assistant" messages. Optional.</param>
        /// <param name="agentName">Filter to messages from a specific agent. Optional.</param>
        /// <param name="limit">Maximum results to return.</param>
        public List<SessionMessageRecord> SearchSessionMessages(
            string taskId = null,
            string query = null,
            string role = null,
            string agentName = null,
            int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(query))
                return _db.GetSessionMessages(taskId: taskId, role: role, agentName: agentName, limit: limit);

            return _db.SearchSessionMessages(query, taskId: taskId, role: role, agentName: agentName, limit: limit);
        }

        #endregion

        #region Heuristic Summary Generation

        /// <summary>
        /// Generates a brief heuristic summary for a session from its stored messages.
        /// Returns null if the session has no useful messages.
        /// </summary>
        public string GenerateHeuristicSummary(string sessionId)
        {
            // Get all messages (capped) for this session
            var messages = _db.GetSessionMessagesBySessionId(sessionId, 200);
            if (messages == null || messages.Count == 0)
                return null;

            return BuildSummaryFromMessages(messages);
        }

        /// <summary>
        /// Generates a heuristic summary directly from parsed messages (no DB query).
        /// Used during import when messages haven't been committed yet or were just saved.
        /// </summary>
        public static string GenerateHeuristicSummaryFromMessages(List<SessionMessageRecord> messages)
        {
            if (messages == null || messages.Count == 0)
                return null;

            return BuildSummaryFromMessages(messages);
        }

        /// <summary>
        /// Backfills heuristic summaries for all sessions that have no summary.
        /// Only overwrites null/empty summaries — never replaces agent-generated ones.
        /// Returns the number of sessions updated.
        /// </summary>
        public int BackfillSummaries()
        {
            // Get all session IDs with no summary
            var unsummarized = _db.GetAllUnsummarizedSessionIds();
            int updated = 0;

            foreach (var sessionId in unsummarized)
            {
                try
                {
                    string summary = GenerateHeuristicSummary(sessionId);
                    if (!string.IsNullOrEmpty(summary))
                    {
                        _db.UpdateSessionSummary(sessionId, summary);
                        updated++;
                    }
                }
                catch
                {
                    // Skip failed sessions
                }
            }

            return updated;
        }

        /// <summary>
        /// Returns true if the text looks like a noise/boilerplate user message that
        /// should not be used as a session topic. Public wrapper for use by other components.
        /// </summary>
        public static bool IsNoisyUserMessagePublic(string content) => IsNoisyUserMessage(content);

        /// <summary>
        /// Returns true if the text looks like a noise/boilerplate user message that
        /// should not be used as a session topic.
        /// </summary>
        private static bool IsNoisyUserMessage(string content)
        {
            if (string.IsNullOrEmpty(content)) return true;
            string trimmed = content.Trim();
            if (trimmed.Length < 4) return true;

            // JSON payloads (hook responses, tool results, etc.)
            if (trimmed.StartsWith("{") && (trimmed.Contains("terminalId") || trimmed.Length > 50)) return true;

            string lower = trimmed.ToLowerInvariant();

            // Greetings and trivial
            if (lower == "hello" || lower == "hi" || lower == "hey" ||
                lower == "initializing..." || lower == "initializing" ||
                lower == "tool loaded." || lower == "tool loaded") return true;

            // Skill/command framework noise
            if (lower.StartsWith("<local-command") || lower.StartsWith("<command-name") ||
                lower.StartsWith("<command-message") || lower.StartsWith("<channel ") ||
                lower.StartsWith("<system-reminder") || lower.StartsWith("<user-prompt") ||
                lower.StartsWith("execute skill:") || lower.StartsWith("launching skill:") ||
                lower.StartsWith("base directory for this skill:") ||
                lower.StartsWith("active terminals:") || lower.StartsWith("no active terminals") ||
                lower.StartsWith("user has answered your questions:") ||
                lower.StartsWith("# session-start") || lower.StartsWith("# ")) return true;

            // Registration echoes like "Alice|6bb0bdf1" or "Alice|472d6198"
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]+\|[0-9a-f]{4,}$")) return true;

            // Raw env var echoes like "Alice|" or "|"
            if (trimmed.EndsWith("|") && trimmed.Length < 30) return true;

            return false;
        }

        private static string BuildSummaryFromMessages(List<SessionMessageRecord> messages)
        {
            // 1. Find the first meaningful user message (the "topic")
            string topic = null;
            foreach (var msg in messages)
            {
                if (msg.Role != "user") continue;
                if (IsNoisyUserMessage(msg.Content)) continue;
                topic = msg.Content.Trim();
                break;
            }

            // 1b. Fallback: if all user messages were noise, try the first meaningful assistant text
            if (string.IsNullOrEmpty(topic))
            {
                foreach (var msg in messages)
                {
                    if (msg.Role != "assistant" || !string.IsNullOrEmpty(msg.ToolName)) continue;
                    string content = msg.Content?.Trim();
                    if (string.IsNullOrEmpty(content) || content.Length < 10) continue;
                    // Skip assistant messages that are just tool stubs or framework noise
                    if (content.StartsWith("[tool:") || content.StartsWith("{")) continue;
                    topic = content;
                    break;
                }
            }

            // 2. Count tool usage
            int totalMessages = messages.Count;
            int edits = 0, writes = 0, reads = 0, builds = 0, searches = 0, skills = 0;
            var editedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.ToolName)) continue;
                switch (msg.ToolName)
                {
                    case "Edit":
                        edits++;
                        ExtractFileName(msg.Content, editedFiles);
                        break;
                    case "Write":
                        writes++;
                        ExtractFileName(msg.Content, editedFiles);
                        break;
                    case "Read":
                        reads++;
                        break;
                    case "Glob":
                    case "Grep":
                        searches++;
                        break;
                    case "Bash":
                        if (msg.Content != null && (msg.Content.Contains("dotnet build") || msg.Content.Contains("msbuild")))
                            builds++;
                        break;
                    case "Skill":
                        skills++;
                        break;
                    default:
                        if (msg.ToolName.Contains("build_project"))
                            builds++;
                        break;
                }
            }

            // 3. Build the summary
            var parts = new List<string>();

            // Topic line — clean and truncate
            if (!string.IsNullOrEmpty(topic))
            {
                string topicLine = topic.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (topicLine.Length > 120) topicLine = topicLine.Substring(0, 117) + "...";
                parts.Add(topicLine);
            }

            // Activity stats
            var stats = new List<string>();
            int fileChanges = edits + writes;
            if (fileChanges > 0)
            {
                string fileCount = editedFiles.Count > 0 ? $" across {editedFiles.Count} file{(editedFiles.Count == 1 ? "" : "s")}" : "";
                stats.Add($"{fileChanges} edit{(fileChanges == 1 ? "" : "s")}{fileCount}");
            }
            if (builds > 0) stats.Add($"{builds} build{(builds == 1 ? "" : "s")}");
            if (reads > 0) stats.Add($"{reads} read{(reads == 1 ? "" : "s")}");
            if (searches > 0) stats.Add($"{searches} search{(searches == 1 ? "" : "es")}");

            if (stats.Count > 0)
                parts.Add(string.Join(", ", stats));
            else if (totalMessages <= 4)
                parts.Add("Brief session");
            else
                parts.Add($"{totalMessages} messages");

            return parts.Count > 0 ? string.Join(" — ", parts) : null;
        }

        private static void ExtractFileName(string content, HashSet<string> files)
        {
            if (string.IsNullOrEmpty(content)) return;

            // Tool content often starts with a file path or contains "file_path" references.
            // Try to extract the first path-like segment.
            var lines = content.Split('\n', 3);
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                // Match common path patterns: absolute paths or relative paths with extensions
                if ((trimmed.Contains(":\\") || trimmed.Contains(":/") || trimmed.StartsWith("/")) &&
                    trimmed.Contains("."))
                {
                    // Clean up — take just the path portion
                    string path = trimmed.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(p => p.Contains(".") && (p.Contains("\\") || p.Contains("/")));
                    if (!string.IsNullOrEmpty(path))
                    {
                        try
                        {
                            files.Add(Path.GetFileName(path));
                        }
                        catch { }
                    }
                    return;
                }
            }
        }

        #endregion

        #region JSONL Parsing

        private struct ParseResult
        {
            public SessionLineageRecord Lineage;
            public List<SessionMessageRecord> Messages;
        }

        /// <summary>
        /// Reads a JSONL file and extracts lineage metadata + message records.
        /// Handles both string content and content-block arrays (text, tool_use, tool_result).
        /// </summary>
        private ParseResult ParseJsonlFile(
            string jsonlPath,
            string defaultAgentName,
            string taskId,
            string overrideParentSessionId,
            string overrideSessionType)
        {
            var result = new ParseResult { Messages = new List<SessionMessageRecord>() };

            // Derive sessionId from file name (Claude Code names files by session GUID)
            // IMPORTANT: The filename IS the canonical session ID. Do NOT override it with
            // the embedded sessionId from JSONL lines — subagent files embed the PARENT's
            // session ID, which would cause the subagent to overwrite the parent record.
            string sessionId = Path.GetFileNameWithoutExtension(jsonlPath);
            string canonicalSessionId = sessionId; // preserve filename-derived ID

            string parentSessionId = overrideParentSessionId;
            string startedAt = null;
            string endedAt = null;
            bool inSubagentsDir = jsonlPath.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar) ||
                                   jsonlPath.Contains('/' + "subagents" + '/');
            string sessionType = overrideSessionType ?? (inSubagentsDir ? "subagent" : "terminal");
            int messageIndex = 0;
            string detectedAgentName = null; // extracted from JSONL content

            try
            {
                // FileShare.ReadWrite allows reading files being actively written by a live Claude session
                using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        // Top-level metadata present on every line
                        // For subagent files, the embedded sessionId is the PARENT's ID — capture
                        // it as parentSessionId but keep the filename-derived ID as canonical.
                        // For top-level files, the embedded ID should match the filename.
                        if (root.TryGetProperty("sessionId", out var sidEl))
                        {
                            string embedded = sidEl.GetString();
                            if (!string.IsNullOrEmpty(embedded))
                            {
                                if (inSubagentsDir && embedded != canonicalSessionId)
                                {
                                    // Subagent file: embedded ID is the parent session
                                    if (parentSessionId == null)
                                        parentSessionId = embedded;
                                }
                                else
                                {
                                    sessionId = embedded;
                                }
                            }
                        }

                        // isSidechain=true means this is a subagent transcript
                        if (root.TryGetProperty("isSidechain", out var sideEl) &&
                            sideEl.ValueKind == JsonValueKind.True &&
                            overrideSessionType == null)
                        {
                            sessionType = "subagent";
                        }

                        // Track timestamp range for started_at / ended_at
                        string ts = null;
                        if (root.TryGetProperty("timestamp", out var tsEl))
                            ts = tsEl.GetString();

                        if (!string.IsNullOrEmpty(ts))
                        {
                            if (startedAt == null) startedAt = ts;
                            endedAt = ts;
                        }

                        // Only process lines that carry a user/assistant message
                        if (!root.TryGetProperty("message", out var msgEl) ||
                            msgEl.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!msgEl.TryGetProperty("role", out var roleEl))
                            continue;

                        string role = roleEl.GetString();
                        if (role != "user" && role != "assistant")
                            continue;

                        // Flatten content — plain string or array of content blocks
                        string content = null;
                        string toolName = null;

                        if (msgEl.TryGetProperty("content", out var contentEl))
                        {
                            if (contentEl.ValueKind == JsonValueKind.String)
                            {
                                content = contentEl.GetString();
                            }
                            else if (contentEl.ValueKind == JsonValueKind.Array)
                            {
                                content = FlattenContentArray(contentEl, out toolName);

                                // Try to detect agent name from register_terminal tool calls
                                if (detectedAgentName == null && role == "assistant")
                                {
                                    detectedAgentName = ExtractAgentNameFromContent(contentEl);
                                }
                            }
                        }

                        result.Messages.Add(new SessionMessageRecord
                        {
                            SessionId = sessionId,
                            TaskId = taskId,
                            AgentName = defaultAgentName,
                            MessageIndex = messageIndex++,
                            Role = role,
                            Content = content,
                            ToolName = toolName,
                            Timestamp = ts
                        });
                    }
                    catch
                    {
                        // Skip malformed lines — common in in-progress sessions
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionLineageService] Failed to read {jsonlPath}: {ex.Message}");
                return result;
            }

            if (messageIndex == 0)
                return result; // No messages — caller treats Lineage==null as "skip"

            // Use detected agent name from JSONL content if available
            string resolvedAgentName = detectedAgentName ?? defaultAgentName;

            // Backfill agent name on already-added messages
            if (detectedAgentName != null)
            {
                foreach (var msg in result.Messages)
                    msg.AgentName = resolvedAgentName;
            }

            result.Lineage = new SessionLineageRecord
            {
                SessionId = sessionId,
                ParentSessionId = parentSessionId,
                TaskId = taskId,
                AgentName = resolvedAgentName,
                SessionType = sessionType,
                SessionFilePath = jsonlPath,
                StartedAt = startedAt,
                EndedAt = endedAt,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };

            return result;
        }

        /// <summary>
        /// Extracts the agent name from a JSONL content array by looking for:
        /// 1. register_terminal tool calls with a "name" input
        /// 2. send_message / broadcast_message with a "fromTerminalId" input
        /// Returns null if no agent name could be detected.
        /// </summary>
        private static string ExtractAgentNameFromContent(JsonElement contentArray)
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "tool_use")
                    continue;

                if (!block.TryGetProperty("name", out var nameEl))
                    continue;

                string toolName = nameEl.GetString();
                if (toolName == null)
                    continue;

                if (!block.TryGetProperty("input", out var inputEl) || inputEl.ValueKind != JsonValueKind.Object)
                    continue;

                // register_terminal has a "name" field with the agent name
                if (toolName.Contains("register_terminal"))
                {
                    if (inputEl.TryGetProperty("name", out var regNameEl))
                    {
                        string regName = regNameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(regName))
                            return regName;
                    }
                }

                // send_message / broadcast_message have "fromTerminalId" with the agent name
                if (toolName.Contains("send_message") || toolName.Contains("broadcast_message"))
                {
                    if (inputEl.TryGetProperty("fromTerminalId", out var fromEl))
                    {
                        string fromName = fromEl.GetString();
                        if (!string.IsNullOrWhiteSpace(fromName))
                            return fromName;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Flattens a JSONL content array into a single string.
        /// Text blocks are joined with newlines.
        /// Tool-use blocks become "[tool: ToolName]" stubs.
        /// Tool-result content is extracted and truncated at 500 chars.
        /// </summary>
        private static string FlattenContentArray(JsonElement contentArray, out string firstToolName)
        {
            firstToolName = null;
            var parts = new List<string>();

            foreach (var block in contentArray.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockTypeEl))
                    continue;

                string blockType = blockTypeEl.GetString();

                switch (blockType)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var textEl))
                        {
                            string text = textEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                                parts.Add(text);
                        }
                        break;

                    case "tool_use":
                        string name = null;
                        if (block.TryGetProperty("name", out var nameEl))
                            name = nameEl.GetString();

                        if (firstToolName == null)
                            firstToolName = name;

                        if (!string.IsNullOrEmpty(name))
                            parts.Add($"[tool: {name}]");
                        break;

                    case "tool_result":
                        // Tool results sit in user messages as the reply to an assistant tool call
                        if (block.TryGetProperty("content", out var resultContent))
                        {
                            if (resultContent.ValueKind == JsonValueKind.String)
                            {
                                string txt = Truncate(resultContent.GetString(), 500);
                                if (!string.IsNullOrEmpty(txt))
                                    parts.Add(txt);
                            }
                            else if (resultContent.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var rb in resultContent.EnumerateArray())
                                {
                                    if (rb.TryGetProperty("text", out var rtEl))
                                    {
                                        string txt = Truncate(rtEl.GetString(), 500);
                                        if (!string.IsNullOrEmpty(txt))
                                            parts.Add(txt);
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "…";
        }

        #endregion
    }
}
