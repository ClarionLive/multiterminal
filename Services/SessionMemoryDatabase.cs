using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartComponents.LocalEmbeddings;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Vector-embedded session memory. Reads Claude Code session JSONL files,
    /// filters noise, chunks text, embeds with SmartComponents.LocalEmbeddings (bge-micro-v2),
    /// stores in SQLite with FTS5 index, and provides hybrid search (FTS5 + vector similarity).
    ///
    /// Follows the KnowledgeDatabase pattern: shares the SQLite connection from TaskDatabase.
    /// </summary>
    public class SessionMemoryDatabase : IDisposable
    {
        private readonly SQLiteConnection _connection;
        private readonly bool _fts5Available;
        private LocalEmbedder _embedder;
        private readonly object _embedderLock = new object();
        private bool _disposed;

        // Embedding dimensions for bge-micro-v2
        private const int EmbeddingDimensions = 384;

        // Chunking parameters
        private const int MaxChunkChars = 2000;  // ~500 tokens at 4 chars/token
        private const int ChunkOverlapChars = 100;

        public SessionMemoryDatabase(TaskDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            _connection = db.Connection;
            _fts5Available = db.IsFts5Available;
            EnsureSchema();
            BackfillTerminalNames();
        }

        #region Schema

        private void EnsureSchema()
        {
            const string schema = @"
                CREATE TABLE IF NOT EXISTS session_chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    terminal_name TEXT,
                    project_path TEXT,
                    chunk_index INTEGER NOT NULL,
                    chunk_text TEXT NOT NULL,
                    embedding BLOB,
                    timestamp TEXT,
                    metadata TEXT,
                    created_at TEXT DEFAULT (datetime('now'))
                );

                CREATE INDEX IF NOT EXISTS idx_session_chunks_session ON session_chunks(session_id);
                CREATE INDEX IF NOT EXISTS idx_session_chunks_project ON session_chunks(project_path);
                CREATE INDEX IF NOT EXISTS idx_session_chunks_terminal ON session_chunks(terminal_name);

                CREATE TABLE IF NOT EXISTS session_chunks_index (
                    session_id TEXT PRIMARY KEY,
                    project_path TEXT,
                    terminal_name TEXT,
                    chunk_count INTEGER,
                    indexed_at TEXT DEFAULT (datetime('now')),
                    file_last_modified TEXT
                );

                -- Dedup table: tracks content hashes to avoid storing identical chunks
                -- across sessions (e.g., repeated skill definitions)
                CREATE TABLE IF NOT EXISTS session_chunk_hashes (
                    content_hash TEXT PRIMARY KEY,
                    first_session_id TEXT NOT NULL,
                    created_at TEXT DEFAULT (datetime('now'))
                );
            ";

            using var cmd = new SQLiteCommand(schema, _connection);
            cmd.ExecuteNonQuery();

            // FTS5 virtual table (may fail if FTS5 not compiled in)
            if (_fts5Available)
            {
                try
                {
                    const string fts5Sql = @"
                        CREATE VIRTUAL TABLE IF NOT EXISTS session_chunks_fts USING fts5(
                            chunk_text,
                            terminal_name,
                            content='session_chunks',
                            content_rowid='id',
                            tokenize='porter'
                        );

                        CREATE TRIGGER IF NOT EXISTS session_chunks_ai
                        AFTER INSERT ON session_chunks BEGIN
                            INSERT INTO session_chunks_fts(rowid, chunk_text, terminal_name)
                            VALUES (new.id, new.chunk_text, new.terminal_name);
                        END;

                        CREATE TRIGGER IF NOT EXISTS session_chunks_ad
                        AFTER DELETE ON session_chunks BEGIN
                            INSERT INTO session_chunks_fts(session_chunks_fts, rowid, chunk_text, terminal_name)
                            VALUES ('delete', old.id, old.chunk_text, old.terminal_name);
                        END;

                        CREATE TRIGGER IF NOT EXISTS session_chunks_au
                        AFTER UPDATE ON session_chunks BEGIN
                            INSERT INTO session_chunks_fts(session_chunks_fts, rowid, chunk_text, terminal_name)
                            VALUES ('delete', old.id, old.chunk_text, old.terminal_name);
                            INSERT INTO session_chunks_fts(rowid, chunk_text, terminal_name)
                            VALUES (new.id, new.chunk_text, new.terminal_name);
                        END;
                    ";
                    using var ftsCmd = new SQLiteCommand(fts5Sql, _connection);
                    ftsCmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionMemory] FTS5 setup failed: {ex.Message}");
                }
            }
        }

        #endregion

        #region Embedder

        private LocalEmbedder GetEmbedder()
        {
            if (_embedder != null) return _embedder;
            lock (_embedderLock)
            {
                if (_embedder == null)
                    _embedder = new LocalEmbedder();
                return _embedder;
            }
        }

        #endregion

        #region Indexing Pipeline

        /// <summary>
        /// Check if a session has already been indexed.
        /// </summary>
        public bool IsSessionIndexed(string sessionId)
        {
            const string sql = "SELECT COUNT(1) FROM session_chunks_index WHERE session_id = @sessionId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>
        /// Index a session JSONL file: read, filter, chunk, embed, store.
        /// Returns the number of chunks created, or 0 if already indexed or empty.
        /// </summary>
        public int IndexSessionFile(string jsonlPath, string terminalName = null, string projectPath = null)
        {
            // CA3003: jsonlPath reaches this sink only through sanitized flows — SessionMemoryController.
            // IndexSession canonicalizes with Path.GetFullPath and rejects anything outside %USERPROFILE%/
            // .claude, and IndexProjectSessions walks files enumerated from Directory.GetFiles under a
            // GetClaudeProjectFolder-anchored directory. No raw user path reaches here.
#pragma warning disable CA3003
            if (!File.Exists(jsonlPath)) return 0;
#pragma warning restore CA3003

            string sessionId = Path.GetFileNameWithoutExtension(jsonlPath);
            if (IsSessionIndexed(sessionId)) return 0;

            // Read and filter the transcript
            string filteredText = ReadAndFilterTranscript(jsonlPath);
            if (string.IsNullOrWhiteSpace(filteredText)) return 0;

            // Chunk the filtered text
            var chunks = ChunkText(filteredText);
            if (chunks.Count == 0) return 0;

            // Embed all chunks
            var embedder = GetEmbedder();
            var embeddings = new List<EmbeddingF32>(chunks.Count);
            foreach (var chunk in chunks)
            {
                embeddings.Add(embedder.Embed(chunk.Text));
            }

            // Write to SQLite in a transaction
            var fileLastModified = File.GetLastWriteTimeUtc(jsonlPath).ToString("o");

            using var transaction = _connection.BeginTransaction();
            try
            {
                int storedCount = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];

                    // Dedup: skip chunks with identical content already stored from another session
                    string contentHash = ComputeContentHash(chunk.Text);
                    if (IsChunkDuplicate(contentHash))
                    {
                        Debug.WriteLine($"[SessionMemory] Skipping duplicate chunk (hash={contentHash[..8]}...) in session {sessionId}");
                        continue;
                    }

                    byte[] embeddingBytes = EmbeddingToBytes(embeddings[i]);

                    const string insertSql = @"
                        INSERT INTO session_chunks
                            (session_id, terminal_name, project_path, chunk_index, chunk_text, embedding, timestamp, metadata)
                        VALUES
                            (@sessionId, @terminal, @project, @index, @text, @embedding, @timestamp, @metadata)";

                    using var cmd = new SQLiteCommand(insertSql, _connection);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    cmd.Parameters.AddWithValue("@terminal", (object)terminalName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@project", (object)projectPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@index", i);
                    cmd.Parameters.AddWithValue("@text", chunk.Text);
                    cmd.Parameters.AddWithValue("@embedding", embeddingBytes);
                    cmd.Parameters.AddWithValue("@timestamp", (object)chunk.Timestamp ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@metadata", (object)chunk.Metadata ?? DBNull.Value);
                    cmd.ExecuteNonQuery();

                    // Record hash so future sessions skip this content
                    RecordChunkHash(contentHash, sessionId);
                    storedCount++;
                }

                // Record in index tracker
                const string indexSql = @"
                    INSERT OR REPLACE INTO session_chunks_index
                        (session_id, project_path, terminal_name, chunk_count, indexed_at, file_last_modified)
                    VALUES
                        (@sessionId, @project, @terminal, @count, datetime('now'), @fileMod)";

                using var indexCmd = new SQLiteCommand(indexSql, _connection);
                indexCmd.Parameters.AddWithValue("@sessionId", sessionId);
                indexCmd.Parameters.AddWithValue("@project", (object)projectPath ?? DBNull.Value);
                indexCmd.Parameters.AddWithValue("@terminal", (object)terminalName ?? DBNull.Value);
                indexCmd.Parameters.AddWithValue("@count", chunks.Count);
                indexCmd.Parameters.AddWithValue("@fileMod", fileLastModified);
                indexCmd.ExecuteNonQuery();

                transaction.Commit();
                int skipped = chunks.Count - storedCount;
                Debug.WriteLine($"[SessionMemory] Indexed session {sessionId}: {storedCount} chunks stored, {skipped} duplicates skipped");
                return storedCount;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Debug.WriteLine($"[SessionMemory] Failed to index session {sessionId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Index all unindexed session files for a project folder.
        /// </summary>
        public int IndexProjectSessions(string projectPath, string terminalName = null)
        {
            string claudeFolder = SessionLineageService.GetClaudeProjectFolder(projectPath);
            if (claudeFolder == null) return 0;

            int totalChunks = 0;

            // Top-level JSONL files
            foreach (var file in Directory.GetFiles(claudeFolder, "*.jsonl"))
            {
                // Skip files modified in the last 2 minutes (likely active)
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if ((DateTime.UtcNow - lastWrite).TotalSeconds < 120) continue;
                }
                catch { continue; }

                totalChunks += IndexSessionFile(file, terminalName, projectPath);
            }

            // Subagent sessions
            foreach (var sessionDir in Directory.GetDirectories(claudeFolder))
            {
                string subagentsDir = Path.Combine(sessionDir, "subagents");
                // CA3003: subagentsDir is Path.Combine(sessionDir, "subagents") where sessionDir comes
                // from Directory.GetDirectories(claudeFolder), and claudeFolder is the result of
                // SessionLineageService.GetClaudeProjectFolder — always anchored to %USERPROFILE%/
                // .claude/projects with separators stripped from the folder-name segment.
#pragma warning disable CA3003
                if (!Directory.Exists(subagentsDir)) continue;
#pragma warning restore CA3003

                foreach (var file in Directory.GetFiles(subagentsDir, "*.jsonl"))
                {
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if ((DateTime.UtcNow - lastWrite).TotalSeconds < 120) continue;
                    }
                    catch { continue; }

                    totalChunks += IndexSessionFile(file, terminalName, projectPath);
                }
            }

            return totalChunks;
        }

        /// <summary>
        /// Find unindexed sessions by comparing file timestamps against the index.
        /// Used for crash recovery on startup.
        /// </summary>
        public List<string> FindUnindexedSessions(string projectPath)
        {
            var unindexed = new List<string>();
            string claudeFolder = SessionLineageService.GetClaudeProjectFolder(projectPath);
            if (claudeFolder == null) return unindexed;

            foreach (var file in Directory.GetFiles(claudeFolder, "*.jsonl"))
            {
                string sessionId = Path.GetFileNameWithoutExtension(file);

                // Skip active files
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if ((DateTime.UtcNow - lastWrite).TotalSeconds < 120) continue;
                }
                catch { continue; }

                if (!IsSessionIndexed(sessionId))
                    unindexed.Add(file);
            }

            return unindexed;
        }

        #endregion

        #region Transcript Reading & Filtering

        /// <summary>
        /// Read a Claude Code JSONL transcript and extract meaningful text,
        /// filtering out tool JSON bodies, permission prompts, and noise.
        /// </summary>
        private string ReadAndFilterTranscript(string jsonlPath)
        {
            var sb = new StringBuilder();

            try
            {
                using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        string extracted = ExtractTextFromJsonLine(line);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            sb.AppendLine(extracted);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed lines
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionMemory] Error reading transcript: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extract useful text from a single JSONL line.
        /// Keeps: user messages, assistant text, tool names + results summary.
        /// Filters: raw tool JSON bodies, permission prompts, progress events, system reminders.
        /// </summary>
        private string ExtractTextFromJsonLine(string jsonLine)
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return null;

            string type = typeProp.GetString();

            switch (type)
            {
                case "user":
                    return ExtractUserMessage(root);

                case "assistant":
                    return ExtractAssistantMessage(root);

                case "message":
                    return ExtractMessageObject(root);

                // Skip noise types
                case "progress":
                case "content_block_start":
                case "content_block_stop":
                case "content_block_delta":
                case "result":
                case "system":
                case "error":
                    return null;

                default:
                    return null;
            }
        }

        private string ExtractUserMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msg)) return null;

            if (msg.ValueKind == JsonValueKind.String)
            {
                string text = msg.GetString();
                // Skip system reminders and permission prompts
                if (text != null && (text.Contains("<system-reminder>") || text.Contains("permission")))
                    return null;
                return $"[User] {text}";
            }

            if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content))
            {
                return ExtractContentBlocks(content, "User");
            }

            return null;
        }

        private string ExtractAssistantMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msg)) return null;

            if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content))
            {
                return ExtractContentBlocks(content, "Assistant");
            }

            return null;
        }

        private string ExtractMessageObject(JsonElement root)
        {
            string role = "Unknown";
            if (root.TryGetProperty("role", out var roleProp))
                role = roleProp.GetString() == "assistant" ? "Assistant" : "User";

            if (root.TryGetProperty("content", out var content))
            {
                return ExtractContentBlocks(content, role);
            }

            return null;
        }

        private string ExtractContentBlocks(JsonElement content, string role)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                string text = content.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (text.Contains("<system-reminder>")) return null;
                return $"[{role}] {text}";
            }

            if (content.ValueKind != JsonValueKind.Array) return null;

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType)) continue;

                string bt = blockType.GetString();
                if (bt == "text" && block.TryGetProperty("text", out var textEl))
                {
                    string text = textEl.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.Contains("<system-reminder>")) continue;
                    sb.AppendLine($"[{role}] {text}");
                }
                else if (bt == "tool_use" && block.TryGetProperty("name", out var toolName))
                {
                    // Keep tool name but skip the full input JSON
                    sb.AppendLine($"[Tool: {toolName.GetString()}]");
                }
                else if (bt == "tool_result")
                {
                    // Keep a brief summary of tool results
                    if (block.TryGetProperty("content", out var resultContent))
                    {
                        string resultText = "";
                        if (resultContent.ValueKind == JsonValueKind.String)
                        {
                            resultText = resultContent.GetString();
                        }
                        else if (resultContent.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var rc in resultContent.EnumerateArray())
                            {
                                if (rc.TryGetProperty("text", out var rcText))
                                    resultText += rcText.GetString() + " ";
                            }
                        }

                        // Truncate long tool results
                        if (resultText.Length > 500)
                            resultText = resultText.Substring(0, 500) + "...";

                        if (!string.IsNullOrWhiteSpace(resultText))
                            sb.AppendLine($"[Result] {resultText}");
                    }
                }
            }

            string result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        #endregion

        #region Chunking

        private class TextChunk
        {
            public string Text { get; set; }
            public string Timestamp { get; set; }
            public string Metadata { get; set; }
        }

        /// <summary>
        /// Split filtered transcript text into chunks of ~500-1000 tokens.
        /// Breaks at paragraph/sentence boundaries with overlap for context continuity.
        /// </summary>
        private List<TextChunk> ChunkText(string text)
        {
            var chunks = new List<TextChunk>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;

            // Split into paragraphs first (double newline or role markers)
            var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            var currentChunk = new StringBuilder();
            int safetyLimit = 10000;
            int iterations = 0;

            foreach (var para in paragraphs)
            {
                if (++iterations > safetyLimit) break;

                if (currentChunk.Length + para.Length + 1 <= MaxChunkChars)
                {
                    if (currentChunk.Length > 0) currentChunk.AppendLine();
                    currentChunk.Append(para);
                }
                else
                {
                    // Current chunk is full — emit it
                    if (currentChunk.Length > 0)
                    {
                        chunks.Add(new TextChunk { Text = currentChunk.ToString().Trim() });
                    }

                    // If the paragraph itself is too large, split it further
                    if (para.Length > MaxChunkChars)
                    {
                        var subChunks = SplitLargeParagraph(para);
                        chunks.AddRange(subChunks);
                        currentChunk.Clear();
                    }
                    else
                    {
                        // Start new chunk with overlap from the end of the previous
                        currentChunk.Clear();
                        if (chunks.Count > 0)
                        {
                            string lastChunkText = chunks[chunks.Count - 1].Text;
                            if (lastChunkText.Length > ChunkOverlapChars)
                            {
                                string overlap = lastChunkText.Substring(lastChunkText.Length - ChunkOverlapChars);
                                currentChunk.Append(overlap);
                                currentChunk.AppendLine();
                            }
                        }
                        currentChunk.Append(para);
                    }
                }
            }

            // Don't forget the last chunk
            if (currentChunk.Length > 0)
            {
                chunks.Add(new TextChunk { Text = currentChunk.ToString().Trim() });
            }

            // Re-index
            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Metadata = JsonSerializer.Serialize(new { chunkIndex = i, totalChunks = chunks.Count });
            }

            return chunks;
        }

        private List<TextChunk> SplitLargeParagraph(string para)
        {
            var chunks = new List<TextChunk>();
            int pos = 0;
            int safety = 0;

            while (pos < para.Length && safety++ < 1000)
            {
                int end = Math.Min(pos + MaxChunkChars, para.Length);

                // Try to break at a sentence boundary
                if (end < para.Length)
                {
                    int sentenceBreak = para.LastIndexOf(". ", end, Math.Min(end - pos, 200));
                    if (sentenceBreak > pos)
                        end = sentenceBreak + 2;
                    else
                    {
                        // Try word boundary
                        int wordBreak = para.LastIndexOf(' ', end, Math.Min(end - pos, 100));
                        if (wordBreak > pos)
                            end = wordBreak + 1;
                    }
                }

                chunks.Add(new TextChunk { Text = para.Substring(pos, end - pos).Trim() });
                pos = end;
            }

            return chunks;
        }

        #endregion

        #region Search

        /// <summary>
        /// Hybrid search: combines FTS5 keyword matching with vector similarity.
        /// Returns top-K relevant chunks with scores.
        /// </summary>
        public List<SessionChunkResult> Search(string query, string projectPath = null, int topK = 10, string agentName = null)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<SessionChunkResult>();

            // Run both searches
            var ftsResults = SearchFts5(query, projectPath, topK * 2, agentName);
            var vectorResults = SearchVector(query, projectPath, topK * 2, agentName);

            // Merge with weighted scoring: 70% vector, 30% FTS5
            var merged = MergeResults(ftsResults, vectorResults, topK);

            return merged;
        }

        /// <summary>
        /// FTS5 keyword search with BM25 ranking.
        /// </summary>
        private List<SessionChunkResult> SearchFts5(string query, string projectPath, int limit, string agentName = null)
        {
            var results = new List<SessionChunkResult>();

            if (!_fts5Available) return SearchLike(query, projectPath, limit, agentName);

            try
            {
                // Split query into individual keywords and join with OR for flexible matching.
                // Previous approach wrapped in quotes for phrase-only matching, which failed
                // when the user's search terms didn't appear in exact order.
                string sanitizedQuery = SanitizeFts5Query(query);

                string sql = @"
                    SELECT sc.id, sc.session_id, sc.terminal_name, sc.project_path,
                           sc.chunk_index, sc.chunk_text, sc.timestamp, sc.metadata,
                           sc.created_at, rank
                    FROM session_chunks sc
                    JOIN session_chunks_fts fts ON sc.id = fts.rowid
                    WHERE session_chunks_fts MATCH @query";

                if (!string.IsNullOrEmpty(projectPath))
                    sql += " AND sc.project_path = @project";
                if (!string.IsNullOrEmpty(agentName))
                    sql += " AND sc.terminal_name = @agent";

                sql += " ORDER BY rank LIMIT @limit";

                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@query", sanitizedQuery);
                if (!string.IsNullOrEmpty(projectPath))
                    cmd.Parameters.AddWithValue("@project", projectPath);
                if (!string.IsNullOrEmpty(agentName))
                    cmd.Parameters.AddWithValue("@agent", agentName);
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var chunk = ReadChunkResult(reader, 0);
                    chunk.Score = reader.GetFloat(reader.GetOrdinal("rank")); // BM25 rank (negative)
                    results.Add(chunk);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionMemory] FTS5 search failed, falling back to LIKE: {ex.Message}");
                return SearchLike(query, projectPath, limit);
            }

            // Normalize FTS5 scores to 0-1 range
            if (results.Count > 0)
            {
                float maxScore = results.Max(r => Math.Abs(r.Score));
                if (maxScore > 0)
                {
                    foreach (var r in results)
                        r.Score = Math.Abs(r.Score) / maxScore; // BM25 rank is negative; more negative = better match → higher normalized score
                }
            }

            return results;
        }

        /// <summary>
        /// LIKE fallback when FTS5 is unavailable.
        /// Splits query into keywords and OR-matches, scoring by number of keyword hits.
        /// </summary>
        private List<SessionChunkResult> SearchLike(string query, string projectPath, int limit, string agentName = null)
        {
            var results = new List<SessionChunkResult>();

            // Extract keywords using same noise-word filtering as FTS5
            var keywords = ExtractSearchKeywords(query);
            if (keywords.Count == 0) return results;

            // Build OR'd LIKE conditions + a hit-count expression for scoring
            // e.g., (CASE WHEN chunk_text LIKE '%bug%' THEN 1 ELSE 0 END + CASE WHEN chunk_text LIKE '%fix%' THEN 1 ELSE 0 END) as hit_count
            var likeClauses = new List<string>();
            var hitCases = new List<string>();
            for (int i = 0; i < keywords.Count; i++)
            {
                string escaped = keywords[i].Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
                string paramName = $"@kw{i}";
                likeClauses.Add($"chunk_text LIKE {paramName} ESCAPE '\\'");
                hitCases.Add($"CASE WHEN chunk_text LIKE {paramName} ESCAPE '\\' THEN 1 ELSE 0 END");
            }

            string hitCountExpr = "(" + string.Join(" + ", hitCases) + ")";

            string sql = $@"
                SELECT id, session_id, terminal_name, project_path,
                       chunk_index, chunk_text, timestamp, metadata, created_at,
                       {hitCountExpr} as hit_count
                FROM session_chunks
                WHERE ({string.Join(" OR ", likeClauses)})";

            if (!string.IsNullOrEmpty(projectPath))
                sql += " AND project_path = @project";
            if (!string.IsNullOrEmpty(agentName))
                sql += " AND terminal_name = @agent";

            sql += " ORDER BY hit_count DESC, created_at DESC LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            for (int i = 0; i < keywords.Count; i++)
            {
                string escaped = keywords[i].Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
                cmd.Parameters.AddWithValue($"@kw{i}", $"%{escaped}%");
            }
            if (!string.IsNullOrEmpty(projectPath))
                cmd.Parameters.AddWithValue("@project", projectPath);
            if (!string.IsNullOrEmpty(agentName))
                cmd.Parameters.AddWithValue("@agent", agentName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var chunk = ReadChunkResult(reader, 0);
                int hitCount = reader.GetInt32(reader.GetOrdinal("hit_count"));
                chunk.Score = (float)hitCount / keywords.Count; // Normalize to 0-1
                results.Add(chunk);
            }

            return results;
        }

        /// <summary>
        /// Extract meaningful keywords from a search query, filtering noise words.
        /// Shared logic between LIKE fallback and FTS5 sanitization.
        /// </summary>
        private static List<string> ExtractSearchKeywords(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<string>();

            var cleaned = query.Replace("\"", " ").Replace("*", " ").Replace("(", " ")
                               .Replace(")", " ").Replace(":", " ").Replace("^", " ")
                               .Replace("'", " ").Replace("\u2019", " ");

            var noiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
                "in", "on", "at", "to", "for", "of", "with", "by", "from", "and",
                "or", "not", "no", "but", "if", "then", "than", "that", "this",
                "it", "its", "i", "we", "you", "they", "he", "she", "my", "your",
                "do", "did", "didn", "t", "don", "does", "has", "have", "had",
                "will", "would", "could", "should", "can", "may", "might",
                "what", "how", "when", "where", "which", "who", "why",
                "made", "worked", "changes", "session", "progress"
            };

            return cleaned.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && !noiseWords.Contains(w))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10) // Cap keywords to prevent huge SQL
                .ToList();
        }

        /// <summary>
        /// Vector similarity search using cosine distance.
        /// </summary>
        private List<SessionChunkResult> SearchVector(string query, string projectPath, int limit, string agentName = null)
        {
            var results = new List<SessionChunkResult>();

            try
            {
                var embedder = GetEmbedder();
                var queryEmbedding = embedder.Embed(query);

                // Load all embeddings for the project (or all if no project filter)
                string sql = @"
                    SELECT id, session_id, terminal_name, project_path,
                           chunk_index, chunk_text, timestamp, metadata, embedding, created_at
                    FROM session_chunks
                    WHERE embedding IS NOT NULL";

                if (!string.IsNullOrEmpty(projectPath))
                    sql += " AND project_path = @project";
                if (!string.IsNullOrEmpty(agentName))
                    sql += " AND terminal_name = @agent";

                using var cmd = new SQLiteCommand(sql, _connection);
                if (!string.IsNullOrEmpty(projectPath))
                    cmd.Parameters.AddWithValue("@project", projectPath);
                if (!string.IsNullOrEmpty(agentName))
                    cmd.Parameters.AddWithValue("@agent", agentName);

                var candidates = new List<(SessionChunkResult result, float[] embedding)>();

                // Count check for performance monitoring
                using var countCmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM session_chunks WHERE embedding IS NOT NULL" +
                    (!string.IsNullOrEmpty(projectPath) ? " AND project_path = @project" : "") +
                    (!string.IsNullOrEmpty(agentName) ? " AND terminal_name = @agent" : ""),
                    _connection);
                if (!string.IsNullOrEmpty(projectPath))
                    countCmd.Parameters.AddWithValue("@project", projectPath);
                if (!string.IsNullOrEmpty(agentName))
                    countCmd.Parameters.AddWithValue("@agent", agentName);
                int rowCount = Convert.ToInt32(countCmd.ExecuteScalar());
                if (rowCount > 5000)
                    Debug.WriteLine($"[SessionMemory] WARNING: Vector search loading {rowCount} embeddings — consider adding ANN index for better scaling");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    byte[] embBytes = reader["embedding"] as byte[];
                    if (embBytes == null || embBytes.Length != EmbeddingDimensions * sizeof(float))
                        continue;

                    float[] emb = BytesToEmbedding(embBytes);
                    var chunk = ReadChunkResult(reader, 0);
                    candidates.Add((chunk, emb));
                }

                // Compute cosine similarity for all candidates
                float[] queryVec = EmbeddingToFloatArray(queryEmbedding);

                foreach (var (chunk, emb) in candidates)
                {
                    chunk.Score = CosineSimilarity(queryVec, emb);
                }

                results = candidates
                    .Select(c => c.result)
                    .OrderByDescending(r => r.Score)
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionMemory] Vector search failed: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Merge FTS5 and vector results with weighted scoring + recency boost.
        /// Recent chunks (today) get up to +30% boost, decaying to zero over 7 days.
        /// </summary>
        private List<SessionChunkResult> MergeResults(
            List<SessionChunkResult> ftsResults,
            List<SessionChunkResult> vectorResults,
            int topK)
        {
            const float vectorWeight = 0.7f;
            const float ftsWeight = 0.3f;
            const float recencyBoost = 0.3f;     // max boost for today's chunks
            const double recencyDecayDays = 7.0;  // boost decays to zero over this many days

            var scoreMap = new Dictionary<int, SessionChunkResult>();

            foreach (var r in vectorResults)
            {
                scoreMap[r.Id] = new SessionChunkResult
                {
                    Id = r.Id,
                    SessionId = r.SessionId,
                    TerminalName = r.TerminalName,
                    ProjectPath = r.ProjectPath,
                    ChunkIndex = r.ChunkIndex,
                    ChunkText = r.ChunkText,
                    Timestamp = r.Timestamp,
                    Metadata = r.Metadata,
                    CreatedAt = r.CreatedAt,
                    Score = r.Score * vectorWeight
                };
            }

            foreach (var r in ftsResults)
            {
                if (scoreMap.TryGetValue(r.Id, out var existing))
                {
                    existing.Score += r.Score * ftsWeight;
                }
                else
                {
                    scoreMap[r.Id] = new SessionChunkResult
                    {
                        Id = r.Id,
                        SessionId = r.SessionId,
                        TerminalName = r.TerminalName,
                        ProjectPath = r.ProjectPath,
                        ChunkIndex = r.ChunkIndex,
                        ChunkText = r.ChunkText,
                        Timestamp = r.Timestamp,
                        Metadata = r.Metadata,
                        CreatedAt = r.CreatedAt,
                        Score = r.Score * ftsWeight
                    };
                }
            }

            // Apply recency boost: recent chunks get higher scores
            var now = DateTime.UtcNow;
            foreach (var r in scoreMap.Values)
            {
                if (DateTime.TryParse(r.CreatedAt, out var createdAt))
                {
                    double ageDays = (now - createdAt).TotalDays;
                    if (ageDays < recencyDecayDays)
                    {
                        float boost = recencyBoost * (float)(1.0 - ageDays / recencyDecayDays);
                        r.Score += boost;
                    }
                }
            }

            // Deduplicate results with near-identical content (e.g., skill definitions
            // loaded across multiple sessions). Keep only the highest-scoring instance.
            var dedupedResults = new List<SessionChunkResult>();
            var seenHashes = new HashSet<string>();
            foreach (var r in scoreMap.Values.OrderByDescending(r => r.Score))
            {
                string hash = ComputeContentHash(r.ChunkText ?? "");
                if (seenHashes.Add(hash))
                    dedupedResults.Add(r);
            }

            return dedupedResults
                .Take(topK)
                .ToList();
        }

        /// <summary>
        /// Sanitize a natural-language query for FTS5.
        /// Splits into keywords, removes noise words and punctuation, joins with OR
        /// so any keyword can match (not just exact phrases).
        /// </summary>
        private static string SanitizeFts5Query(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "\"\"";

            var keywords = ExtractSearchKeywords(query);
            if (keywords.Count == 0) return "\"" + query.Replace("\"", " ") + "\"";

            // Quote each keyword and join with OR for flexible matching
            return string.Join(" OR ", keywords.Select(w => "\"" + w + "\""));
        }

        /// <summary>Compute SHA256 hash of chunk content for deduplication.</summary>
        private static string ComputeContentHash(string text)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.Trim()));
            return Convert.ToHexString(hash);
        }

        /// <summary>Check if a chunk with this content hash already exists.</summary>
        private bool IsChunkDuplicate(string contentHash)
        {
            const string sql = "SELECT 1 FROM session_chunk_hashes WHERE content_hash = @hash LIMIT 1";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@hash", contentHash);
            return cmd.ExecuteScalar() != null;
        }

        /// <summary>Record a content hash to prevent future duplicates.</summary>
        private void RecordChunkHash(string contentHash, string sessionId)
        {
            const string sql = "INSERT OR IGNORE INTO session_chunk_hashes (content_hash, first_session_id) VALUES (@hash, @sid)";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@hash", contentHash);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.ExecuteNonQuery();
        }

        private SessionChunkResult ReadChunkResult(SQLiteDataReader reader, float defaultScore)
        {
            // Try to read created_at if the query includes it
            string createdAt = null;
            try { createdAt = reader["created_at"]?.ToString(); } catch { }

            return new SessionChunkResult
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                SessionId = reader["session_id"]?.ToString(),
                TerminalName = reader["terminal_name"]?.ToString(),
                ProjectPath = reader["project_path"]?.ToString(),
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
                ChunkText = reader["chunk_text"]?.ToString(),
                Timestamp = reader["timestamp"]?.ToString(),
                Metadata = reader["metadata"]?.ToString(),
                Score = defaultScore,
                CreatedAt = createdAt
            };
        }

        #endregion

        #region Terminal Name Backfill

        /// <summary>
        /// Backfill NULL terminal_name on session_chunks using two sources:
        /// 1. session_chunks_index (which may have terminal_name from later re-indexing)
        /// 2. session_agent_map (which maps session_id to agent_name from terminal registration)
        /// Runs once at startup — fast because it uses batch UPDATE with subqueries.
        /// </summary>
        public void BackfillTerminalNames()
        {
            try
            {
                // Count NULLs first to see if backfill is needed
                const string countSql = "SELECT COUNT(1) FROM session_chunks WHERE terminal_name IS NULL";
                using var countCmd = new SQLiteCommand(countSql, _connection);
                int nullCount = Convert.ToInt32(countCmd.ExecuteScalar());
                if (nullCount == 0) return;

                Debug.WriteLine($"[SessionMemory] Backfilling terminal_name for {nullCount} chunks...");

                int totalFixed = 0;

                // Source 1: session_chunks_index (same DB, has terminal_name for re-indexed sessions)
                const string fromIndexSql = @"
                    UPDATE session_chunks
                    SET terminal_name = (
                        SELECT sci.terminal_name
                        FROM session_chunks_index sci
                        WHERE sci.session_id = session_chunks.session_id
                          AND sci.terminal_name IS NOT NULL
                          AND sci.terminal_name != ''
                    )
                    WHERE terminal_name IS NULL
                      AND session_id IN (
                          SELECT session_id FROM session_chunks_index
                          WHERE terminal_name IS NOT NULL AND terminal_name != ''
                      )";
                using (var cmd = new SQLiteCommand(fromIndexSql, _connection))
                {
                    int fixed1 = cmd.ExecuteNonQuery();
                    totalFixed += fixed1;
                    if (fixed1 > 0) Debug.WriteLine($"[SessionMemory] Backfilled {fixed1} chunks from session_chunks_index");
                }

                // Source 2: session_agent_map (maps session_id → agent_name from terminal registration)
                const string fromMapSql = @"
                    UPDATE session_chunks
                    SET terminal_name = (
                        SELECT sam.agent_name
                        FROM session_agent_map sam
                        WHERE sam.session_id = session_chunks.session_id
                          AND sam.agent_name IS NOT NULL
                          AND sam.agent_name != ''
                    )
                    WHERE terminal_name IS NULL
                      AND session_id IN (
                          SELECT session_id FROM session_agent_map
                          WHERE agent_name IS NOT NULL AND agent_name != ''
                      )";
                using (var cmd = new SQLiteCommand(fromMapSql, _connection))
                {
                    int fixed2 = cmd.ExecuteNonQuery();
                    totalFixed += fixed2;
                    if (fixed2 > 0) Debug.WriteLine($"[SessionMemory] Backfilled {fixed2} chunks from session_agent_map");
                }

                // Also update session_chunks_index for consistency
                const string fixIndexSql = @"
                    UPDATE session_chunks_index
                    SET terminal_name = (
                        SELECT sam.agent_name
                        FROM session_agent_map sam
                        WHERE sam.session_id = session_chunks_index.session_id
                          AND sam.agent_name IS NOT NULL
                          AND sam.agent_name != ''
                    )
                    WHERE terminal_name IS NULL
                      AND session_id IN (
                          SELECT session_id FROM session_agent_map
                          WHERE agent_name IS NOT NULL AND agent_name != ''
                      )";
                using (var cmd = new SQLiteCommand(fixIndexSql, _connection))
                {
                    cmd.ExecuteNonQuery();
                }

                Debug.WriteLine($"[SessionMemory] Backfill complete: {totalFixed} chunks updated, {nullCount - totalFixed} remain NULL (no mapping found)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionMemory] Backfill failed (non-fatal): {ex.Message}");
            }
        }

        #endregion

        #region Stats

        /// <summary>
        /// Get indexing statistics.
        /// </summary>
        public SessionMemoryStats GetStats(string projectPath = null)
        {
            var stats = new SessionMemoryStats();

            string countSql = "SELECT COUNT(1) FROM session_chunks";
            string sessionSql = "SELECT COUNT(1) FROM session_chunks_index";

            if (!string.IsNullOrEmpty(projectPath))
            {
                countSql += " WHERE project_path = @project";
                sessionSql += " WHERE project_path = @project";
            }

            using (var cmd = new SQLiteCommand(countSql, _connection))
            {
                if (!string.IsNullOrEmpty(projectPath))
                    cmd.Parameters.AddWithValue("@project", projectPath);
                stats.TotalChunks = Convert.ToInt32(cmd.ExecuteScalar());
            }

            using (var cmd = new SQLiteCommand(sessionSql, _connection))
            {
                if (!string.IsNullOrEmpty(projectPath))
                    cmd.Parameters.AddWithValue("@project", projectPath);
                stats.IndexedSessions = Convert.ToInt32(cmd.ExecuteScalar());
            }

            return stats;
        }

        #endregion

        #region Helpers

        private static byte[] EmbeddingToBytes(EmbeddingF32 embedding)
        {
            var values = embedding.Values;
            byte[] bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] BytesToEmbedding(byte[] bytes)
        {
            float[] values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        private static float[] EmbeddingToFloatArray(EmbeddingF32 embedding)
        {
            return embedding.Values.ToArray();
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
            return denom > 0 ? dot / denom : 0;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _embedder?.Dispose();
                _embedder = null;
            }
        }

        #endregion
    }

    /// <summary>
    /// A single chunk result from session memory search.
    /// </summary>
    public class SessionChunkResult
    {
        public int Id { get; set; }
        public string SessionId { get; set; }
        public string TerminalName { get; set; }
        public string ProjectPath { get; set; }
        public int ChunkIndex { get; set; }
        public string ChunkText { get; set; }
        public string Timestamp { get; set; }
        public string Metadata { get; set; }
        public float Score { get; set; }
        public string CreatedAt { get; set; }
    }

    /// <summary>
    /// Statistics about the session memory index.
    /// </summary>
    public class SessionMemoryStats
    {
        public int TotalChunks { get; set; }
        public int IndexedSessions { get; set; }
    }
}
