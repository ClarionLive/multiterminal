using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// CRUD and search operations for knowledge_entries and code_digests tables.
    /// Shares the SQLite connection from TaskDatabase (same multiterminal.db file, same WAL handle).
    /// FTS5 full-text search is used when available; falls back to LIKE queries otherwise.
    /// </summary>
    public class KnowledgeDatabase
    {
        private readonly SQLiteConnection _connection;
        private readonly bool _fts5Available;

        /// <summary>
        /// Creates a new KnowledgeDatabase using the shared SQLite connection from TaskDatabase.
        /// This avoids opening a second WAL handle on the same file.
        /// </summary>
        public KnowledgeDatabase(TaskDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            _connection = db.Connection;
            _fts5Available = db.IsFts5Available;
        }

        #region Knowledge Entries

        /// <summary>
        /// Inserts a new knowledge entry and returns its auto-assigned integer ID.
        /// </summary>
        public int AddKnowledgeEntry(KnowledgeEntry entry)
        {
            const string sql = @"
                INSERT INTO knowledge_entries
                    (project_id, category, title, content, source_type, source_id,
                     source_agent, tags, confidence, superseded_by, query_hash, last_referenced, created_at, updated_at)
                VALUES
                    (@projectId, @category, @title, @content, @sourceType, @sourceId,
                     @sourceAgent, @tags, @confidence, @supersededBy, @queryHash, datetime('now'), datetime('now'), datetime('now'));
                SELECT last_insert_rowid();";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@projectId", (object)entry.ProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@category", entry.Category ?? "general");
            cmd.Parameters.AddWithValue("@title", entry.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("@content", entry.Content ?? string.Empty);
            cmd.Parameters.AddWithValue("@sourceType", entry.SourceType ?? "manual");
            cmd.Parameters.AddWithValue("@sourceId", (object)entry.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sourceAgent", (object)entry.SourceAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tags", (object)entry.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@confidence", entry.Confidence ?? "confirmed");
            cmd.Parameters.AddWithValue("@supersededBy", (object)entry.SupersededBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@queryHash", (object)entry.QueryHash ?? DBNull.Value);

            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Updates mutable fields on an existing knowledge entry from a string dictionary.
        /// Accepted keys: category, title, content, tags, confidence, superseded_by.
        /// Uses an allowlist to prevent SQL injection via dictionary keys.
        /// Returns the number of rows updated (0 = not found or no valid fields supplied).
        /// </summary>
        public int UpdateKnowledgeEntry(int id, Dictionary<string, string> fields)
        {
            if (fields == null || fields.Count == 0)
                return 0;

            // Allowlist of updatable columns — never interpolate raw dictionary keys into SQL
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "category", "title", "content", "tags", "confidence", "superseded_by", "query_hash"
            };

            var setClauses = new List<string> { "updated_at = datetime('now')" };
            var cmd = new SQLiteCommand(_connection);

            int paramIndex = 0;
            foreach (var kvp in fields)
            {
                if (!allowed.Contains(kvp.Key))
                    continue;

                // Use the allowlisted column name directly (safe — validated above)
                string col = kvp.Key.ToLowerInvariant();
                string paramName = $"@p{paramIndex++}";
                setClauses.Add($"{col} = {paramName}");
                cmd.Parameters.AddWithValue(paramName, (object)kvp.Value ?? DBNull.Value);
            }

            if (setClauses.Count == 1)
            {
                cmd.Dispose();
                return 0; // No valid fields provided
            }

            // CA2100: Column names in setClauses are whitelist-validated against `allowed` (line 85);
            // all user values flow through SQLiteParameter (@pN, @id).
#pragma warning disable CA2100
            cmd.CommandText = $"UPDATE knowledge_entries SET {string.Join(", ", setClauses)} WHERE id = @id";
#pragma warning restore CA2100
            cmd.Parameters.AddWithValue("@id", id);

            int rows = cmd.ExecuteNonQuery();
            cmd.Dispose();
            return rows;
        }

        /// <summary>
        /// Marks a knowledge entry as deprecated. If supersededById is provided the
        /// superseded_by foreign key is set so callers can follow the chain.
        /// Returns true if the row was found and updated.
        /// </summary>
        public bool DeprecateKnowledgeEntry(int id, int? supersededById = null)
        {
            const string sql = @"
                UPDATE knowledge_entries
                SET confidence = 'deprecated',
                    superseded_by = @supersededBy,
                    updated_at = datetime('now')
                WHERE id = @id";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@supersededBy", (object)supersededById ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Returns a single knowledge entry by ID, or null if not found.
        /// </summary>
        public KnowledgeEntry GetKnowledgeEntry(int id)
        {
            const string sql = @"
                SELECT id, project_id, category, title, content, source_type, source_id,
                       source_agent, tags, confidence, superseded_by, created_at, updated_at, query_hash,
                       last_referenced, reference_count
                FROM knowledge_entries
                WHERE id = @id";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }

        /// <summary>
        /// Returns all knowledge entries that originated from a specific source (e.g., all
        /// entries extracted from a given session or task).
        /// </summary>
        public List<KnowledgeEntry> GetKnowledgeBySource(string sourceType, string sourceId)
        {
            const string sql = @"
                SELECT id, project_id, category, title, content, source_type, source_id,
                       source_agent, tags, confidence, superseded_by, created_at, updated_at, query_hash,
                       last_referenced, reference_count
                FROM knowledge_entries
                WHERE source_type = @sourceType AND source_id = @sourceId
                ORDER BY created_at DESC";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@sourceType", sourceType ?? string.Empty);
            cmd.Parameters.AddWithValue("@sourceId", sourceId ?? string.Empty);

            return ReadEntries(cmd);
        }

        /// <summary>
        /// Searches knowledge entries using FTS5 if available, falling back to LIKE queries.
        /// All filter parameters are optional. Deprecated entries are excluded by default.
        /// </summary>
        /// <param name="query">Free-text search term. Pass null to list without searching.</param>
        /// <param name="category">Filter to a specific category (e.g. "pattern", "gotcha").</param>
        /// <param name="projectId">Restrict to a project (null returns global + project entries).</param>
        /// <param name="tags">Comma-separated tag filter (LIKE match on the tags column).</param>
        /// <param name="limit">Maximum number of results to return.</param>
        /// <param name="includeDeprecated">When true, deprecated entries are included.</param>
        public List<KnowledgeEntry> SearchKnowledge(
            string query,
            string category = null,
            string projectId = null,
            string tags = null,
            int limit = 20,
            bool includeDeprecated = false)
        {
            // Treat wildcard-only or whitespace-only queries as "list all" (no text filter)
            if (query != null && query.Trim().TrimStart('*').TrimEnd('*').Length == 0)
                query = null;

            if (!string.IsNullOrWhiteSpace(query) && _fts5Available)
            {
                // Guard: clamp query to prevent oversized FTS5 expressions
                if (query.Length > 500)
                    query = query.Substring(0, 500);

                try
                {
                    return ExecuteFts5KnowledgeSearch(query, category, projectId, tags, limit, includeDeprecated);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[KnowledgeDatabase] FTS5 query failed, falling back to LIKE: {ex.Message}");
                }
            }

            return ExecuteLikeKnowledgeSearch(query, category, projectId, tags, limit, includeDeprecated);
        }

        private List<KnowledgeEntry> ExecuteFts5KnowledgeSearch(
            string query,
            string category,
            string projectId,
            string tags,
            int limit,
            bool includeDeprecated)
        {
            var sql = @"
                SELECT ke.id, ke.project_id, ke.category, ke.title, ke.content, ke.source_type,
                       ke.source_id, ke.source_agent, ke.tags, ke.confidence, ke.superseded_by,
                       ke.created_at, ke.updated_at, ke.query_hash,
                       ke.last_referenced, ke.reference_count
                FROM knowledge_entries ke
                JOIN knowledge_entries_fts fts ON ke.id = fts.rowid
                WHERE knowledge_entries_fts MATCH @query";

            if (!includeDeprecated) sql += " AND ke.confidence != 'deprecated'";
            if (category != null)   sql += " AND ke.category = @category";
            if (projectId != null)  sql += " AND (ke.project_id = @projectId OR ke.project_id IS NULL)";
            if (tags != null)       sql += " AND ke.tags LIKE @tags";

            sql += $" ORDER BY ke.updated_at DESC LIMIT {limit}";

            // CA2100: SQL is composed from static literals + an int `limit`; all user values flow through SQLiteParameter.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            // Sanitize FTS5 query: strip double-quotes and wrap in phrase quotes to prevent
            // FTS5 expression injection (OR, NOT, NEAR operators). This forces literal matching.
            var sanitizedQuery = "\"" + query.Replace("\"", " ") + "\"";
            cmd.Parameters.AddWithValue("@query", sanitizedQuery);
            if (category != null)  cmd.Parameters.AddWithValue("@category", category);
            if (projectId != null) cmd.Parameters.AddWithValue("@projectId", projectId);
            if (tags != null)      cmd.Parameters.AddWithValue("@tags", $"%{tags}%");

            return ReadEntries(cmd);
        }

        private List<KnowledgeEntry> ExecuteLikeKnowledgeSearch(
            string query,
            string category,
            string projectId,
            string tags,
            int limit,
            bool includeDeprecated)
        {
            var sql = @"
                SELECT id, project_id, category, title, content, source_type, source_id,
                       source_agent, tags, confidence, superseded_by, created_at, updated_at, query_hash,
                       last_referenced, reference_count
                FROM knowledge_entries
                WHERE 1=1";

            if (!string.IsNullOrWhiteSpace(query)) sql += " AND (title LIKE @query OR content LIKE @query OR tags LIKE @query)";
            if (!includeDeprecated)                sql += " AND confidence != 'deprecated'";
            if (category != null)                  sql += " AND category = @category";
            if (projectId != null)                 sql += " AND (project_id = @projectId OR project_id IS NULL)";
            if (tags != null)                      sql += " AND tags LIKE @tags";

            sql += $" ORDER BY updated_at DESC LIMIT {limit}";

            // CA2100: SQL is composed from static literals + an int `limit`; all user values flow through SQLiteParameter.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("@query", $"%{query}%");
            if (category != null)  cmd.Parameters.AddWithValue("@category", category);
            if (projectId != null) cmd.Parameters.AddWithValue("@projectId", projectId);
            if (tags != null)      cmd.Parameters.AddWithValue("@tags", $"%{tags}%");

            return ReadEntries(cmd);
        }

        /// <summary>
        /// Bumps the last_referenced timestamp and increments reference_count for the given entry.
        /// Called when knowledge is queried or injected into agent context.
        /// </summary>
        public void BumpReference(int id)
        {
            const string sql = @"
                UPDATE knowledge_entries
                SET last_referenced = datetime('now'),
                    reference_count = reference_count + 1
                WHERE id = @id";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Bumps last_referenced and reference_count for multiple entries at once.
        /// Wraps in a transaction for atomicity and performance (single WAL commit).
        /// </summary>
        public void BumpReferences(IEnumerable<int> ids)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var id in ids)
                    BumpReference(id);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        #region Research Cache

        /// <summary>
        /// Looks up a cached research result by query hash. Returns the most recent
        /// non-deprecated entry with the given hash, or null if no cache hit.
        /// </summary>
        public KnowledgeEntry LookupResearchCache(string queryHash)
        {
            if (string.IsNullOrEmpty(queryHash)) return null;

            const string sql = @"
                SELECT id, project_id, category, title, content, source_type, source_id,
                       source_agent, tags, confidence, superseded_by, created_at, updated_at, query_hash,
                       last_referenced, reference_count
                FROM knowledge_entries
                WHERE query_hash = @queryHash
                  AND confidence != 'deprecated'
                ORDER BY updated_at DESC
                LIMIT 1";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@queryHash", queryHash);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }

        /// <summary>
        /// Checks if a query hash already exists in the knowledge base.
        /// </summary>
        public bool ResearchCacheExists(string queryHash)
        {
            if (string.IsNullOrEmpty(queryHash)) return false;

            const string sql = @"
                SELECT 1 FROM knowledge_entries
                WHERE query_hash = @queryHash AND confidence != 'deprecated'
                LIMIT 1";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@queryHash", queryHash);
            return cmd.ExecuteScalar() != null;
        }

        #endregion

        #endregion

        #region Code Digests

        /// <summary>
        /// Upserts a code digest for the given project + file path combination.
        /// Inserts on first call; replaces on subsequent calls when the hash changes.
        /// Returns the row ID of the inserted or updated digest.
        /// </summary>
        public int SaveCodeDigest(CodeDigest digest)
        {
            const string sql = @"
                INSERT INTO code_digests
                    (project_id, file_path, file_hash, purpose, key_classes, key_methods,
                     patterns, gotchas, dependencies, line_count, digest_model, created_at, updated_at)
                VALUES
                    (@projectId, @filePath, @fileHash, @purpose, @keyClasses, @keyMethods,
                     @patterns, @gotchas, @dependencies, @lineCount, @digestModel, datetime('now'), datetime('now'))
                ON CONFLICT(project_id, file_path) DO UPDATE SET
                    file_hash = excluded.file_hash,
                    purpose = excluded.purpose,
                    key_classes = excluded.key_classes,
                    key_methods = excluded.key_methods,
                    patterns = excluded.patterns,
                    gotchas = excluded.gotchas,
                    dependencies = excluded.dependencies,
                    line_count = excluded.line_count,
                    digest_model = excluded.digest_model,
                    updated_at = datetime('now');
                SELECT id FROM code_digests WHERE project_id = @projectId AND file_path = @filePath;";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@projectId", (object)digest.ProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@filePath", digest.FilePath ?? string.Empty);
            cmd.Parameters.AddWithValue("@fileHash", digest.FileHash ?? string.Empty);
            cmd.Parameters.AddWithValue("@purpose", (object)digest.Purpose ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@keyClasses", (object)digest.KeyClasses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@keyMethods", (object)digest.KeyMethods ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@patterns", (object)digest.Patterns ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@gotchas", (object)digest.Gotchas ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dependencies", (object)digest.Dependencies ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lineCount", digest.LineCount);
            cmd.Parameters.AddWithValue("@digestModel", (object)digest.DigestModel ?? DBNull.Value);

            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Returns the code digest for a specific project + file path, or null if none exists.
        /// </summary>
        public CodeDigest GetCodeDigest(string projectId, string filePath)
        {
            string sql;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                sql = @"
                    SELECT id, project_id, file_path, file_hash, purpose, key_classes, key_methods,
                           patterns, gotchas, dependencies, line_count, digest_model, created_at, updated_at
                    FROM code_digests
                    WHERE file_path = @filePath
                    LIMIT 1";
            }
            else
            {
                sql = @"
                    SELECT id, project_id, file_path, file_hash, purpose, key_classes, key_methods,
                           patterns, gotchas, dependencies, line_count, digest_model, created_at, updated_at
                    FROM code_digests
                    WHERE project_id = @projectId AND file_path = @filePath";
            }

            using var cmd = new SQLiteCommand(sql, _connection);
            if (!string.IsNullOrWhiteSpace(projectId))
                cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@filePath", filePath ?? string.Empty);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDigest(reader) : null;
        }

        /// <summary>
        /// Returns digests for the given project whose stored file_hash differs from the
        /// current hash provided in <paramref name="currentHashes"/>.
        /// Also returns digests for files that are no longer present in currentHashes
        /// (deleted files), so callers can remove orphaned digests.
        /// </summary>
        /// <param name="projectId">Project scope to query.</param>
        /// <param name="currentHashes">Map of filePath → currentHash for all tracked files.</param>
        public List<CodeDigest> GetStaleDigests(string projectId, Dictionary<string, string> currentHashes)
        {
            // Fetch all digests for the project, then filter in memory.
            // This avoids complex parameterized IN clauses and keeps the query simple.
            const string sql = @"
                SELECT id, project_id, file_path, file_hash, purpose, key_classes, key_methods,
                       patterns, gotchas, dependencies, line_count, digest_model, created_at, updated_at
                FROM code_digests
                WHERE project_id = @projectId";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@projectId", (object)projectId ?? DBNull.Value);

            var all = ReadDigests(cmd);
            var stale = new List<CodeDigest>();

            foreach (var d in all)
            {
                // Stale if: file no longer tracked OR hash has changed
                if (!currentHashes.TryGetValue(d.FilePath, out string currentHash) ||
                    !string.Equals(currentHash, d.FileHash, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(d);
                }
            }

            return stale;
        }

        /// <summary>
        /// Deletes a code digest for the given project + file path.
        /// Returns true if a row was deleted.
        /// </summary>
        public bool DeleteCodeDigest(string projectId, string filePath)
        {
            const string sql = @"
                DELETE FROM code_digests
                WHERE project_id = @projectId AND file_path = @filePath";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@projectId", (object)projectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@filePath", filePath ?? string.Empty);
            return cmd.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Private Helpers

        private static List<KnowledgeEntry> ReadEntries(SQLiteCommand cmd)
        {
            var results = new List<KnowledgeEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadEntry(reader));
            return results;
        }

        private static KnowledgeEntry ReadEntry(SQLiteDataReader reader)
        {
            return new KnowledgeEntry
            {
                Id           = reader.GetInt32(0),
                ProjectId    = reader.IsDBNull(1)  ? null : reader.GetString(1),
                Category     = reader.IsDBNull(2)  ? null : reader.GetString(2),
                Title        = reader.IsDBNull(3)  ? null : reader.GetString(3),
                Content      = reader.IsDBNull(4)  ? null : reader.GetString(4),
                SourceType   = reader.IsDBNull(5)  ? null : reader.GetString(5),
                SourceId     = reader.IsDBNull(6)  ? null : reader.GetString(6),
                SourceAgent  = reader.IsDBNull(7)  ? null : reader.GetString(7),
                Tags         = reader.IsDBNull(8)  ? null : reader.GetString(8),
                Confidence   = reader.IsDBNull(9)  ? null : reader.GetString(9),
                SupersededBy = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10),
                CreatedAt    = reader.IsDBNull(11) ? null : reader.GetString(11),
                UpdatedAt    = reader.IsDBNull(12) ? null : reader.GetString(12),
                // query_hash column added by migration — guard for older queries that may not select it
                QueryHash      = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null,
                // attention decay columns added by migration
                LastReferenced = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
                ReferenceCount = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetInt32(15) : 0
            };
        }

        private static List<CodeDigest> ReadDigests(SQLiteCommand cmd)
        {
            var results = new List<CodeDigest>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadDigest(reader));
            return results;
        }

        private static CodeDigest ReadDigest(SQLiteDataReader reader)
        {
            return new CodeDigest
            {
                Id           = reader.GetInt32(0),
                ProjectId    = reader.IsDBNull(1)  ? null : reader.GetString(1),
                FilePath     = reader.IsDBNull(2)  ? null : reader.GetString(2),
                FileHash     = reader.IsDBNull(3)  ? null : reader.GetString(3),
                Purpose      = reader.IsDBNull(4)  ? null : reader.GetString(4),
                KeyClasses   = reader.IsDBNull(5)  ? null : reader.GetString(5),
                KeyMethods   = reader.IsDBNull(6)  ? null : reader.GetString(6),
                Patterns     = reader.IsDBNull(7)  ? null : reader.GetString(7),
                Gotchas      = reader.IsDBNull(8)  ? null : reader.GetString(8),
                Dependencies = reader.IsDBNull(9)  ? null : reader.GetString(9),
                LineCount    = reader.IsDBNull(10) ? 0    : reader.GetInt32(10),
                DigestModel  = reader.IsDBNull(11) ? null : reader.GetString(11),
                CreatedAt    = reader.IsDBNull(12) ? null : reader.GetString(12),
                UpdatedAt    = reader.IsDBNull(13) ? null : reader.GetString(13)
            };
        }

        #endregion

        #region Decay-Ranked Injection

        /// <summary>
        /// Returns knowledge entries ranked by attention decay score.
        /// Score = (reference_count + 1) / (days_since_last_referenced + 1).
        /// Falls back to updated_at ordering if decay columns don't exist yet.
        /// </summary>
        public List<KnowledgeEntry> GetDecayRanked(int limit = 15)
        {
            // Check if decay columns exist (migration may not have run)
            bool hasDecay = false;
            using (var pragma = new SQLiteCommand("PRAGMA table_info(knowledge_entries)", _connection))
            using (var reader = pragma.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1) == "last_referenced")
                    {
                        hasDecay = true;
                        break;
                    }
                }
            }

            string sql;
            if (hasDecay)
            {
                sql = @"
                    SELECT id, project_id, category, title, content,
                           source_type, source_id, source_agent, tags, confidence,
                           superseded_by, created_at, updated_at, query_hash,
                           last_referenced, reference_count,
                           (COALESCE(reference_count, 0) + 1.0)
                           / (julianday('now') - julianday(COALESCE(last_referenced, updated_at)) + 1.0)
                           AS decay_score
                    FROM knowledge_entries
                    WHERE (superseded_by IS NULL OR superseded_by = 0)
                      AND confidence != 'deprecated'
                    ORDER BY decay_score DESC
                    LIMIT @limit";
            }
            else
            {
                sql = @"
                    SELECT id, project_id, category, title, content,
                           source_type, source_id, source_agent, tags, confidence,
                           superseded_by, created_at, updated_at, NULL, NULL, 0
                    FROM knowledge_entries
                    WHERE (superseded_by IS NULL OR superseded_by = 0)
                      AND confidence != 'deprecated'
                    ORDER BY updated_at DESC
                    LIMIT @limit";
            }

            var results = new List<KnowledgeEntry>();
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    results.Add(ReadEntry(reader));
            }

            return results;
        }

        #endregion
    }
}
