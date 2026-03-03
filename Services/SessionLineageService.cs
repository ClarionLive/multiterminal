using System;
using System.Collections.Generic;
using System.IO;
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
        ///   Path like C:\Users\John\.claude\projects\H--DevLaptop-...\
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
            string sessionId = Path.GetFileNameWithoutExtension(jsonlPath);

            string parentSessionId = overrideParentSessionId;
            string startedAt = null;
            string endedAt = null;
            bool inSubagentsDir = jsonlPath.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar) ||
                                   jsonlPath.Contains('/' + "subagents" + '/');
            string sessionType = overrideSessionType ?? (inSubagentsDir ? "subagent" : "terminal");
            int messageIndex = 0;

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
                        if (root.TryGetProperty("sessionId", out var sidEl))
                        {
                            string embedded = sidEl.GetString();
                            if (!string.IsNullOrEmpty(embedded))
                                sessionId = embedded;
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

            result.Lineage = new SessionLineageRecord
            {
                SessionId = sessionId,
                ParentSessionId = parentSessionId,
                TaskId = taskId,
                AgentName = defaultAgentName,
                SessionType = sessionType,
                SessionFilePath = jsonlPath,
                StartedAt = startedAt,
                EndedAt = endedAt,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };

            return result;
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
