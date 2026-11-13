using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>What one database rewrite of a Move Project changed (ticket 6f879683).</summary>
    public sealed class RelocationDbResult
    {
        /// <summary>True when the transaction committed.</summary>
        public bool Succeeded { get; init; }

        /// <summary>Why it did not commit; null on success.</summary>
        public string Error { get; init; }

        /// <summary>Rows changed per "table.column".</summary>
        public IReadOnlyDictionary<string, int> RowsChanged { get; init; } = new Dictionary<string, int>();

        /// <summary>Total rows changed.</summary>
        public int Total => RowsChanged.Values.Sum();
    }

    /// <summary>
    /// The database half of Move Project (ticket 6f879683): repoints every stored path under the project's
    /// old folder to the new one, in ONE transaction on its own connection to multiterminal.db, so a failure
    /// leaves every table as it was.
    ///
    /// <para>Matching is separator-bounded and accepts either separator (<see cref="RelocationPaths"/>). Each
    /// path keeps its own separator style. A column a database does not have yet (a table created by a
    /// later feature, an older install) is skipped, not an error.</para>
    ///
    /// <para>Notes are keyed by the exact path string (<c>project_notes</c>, <c>project_note_tabs</c>), so
    /// they are rebased like any other path; if the new folder already has notes the UNIQUE constraint
    /// fails and the whole rewrite is rolled back. <see cref="FindConflicts"/> reports that case before
    /// anything moves.</para>
    /// </summary>
    public static class ProjectRelocationStore
    {
        /// <summary>Every column that stores a path under a project's folder.</summary>
        internal static readonly (string Table, string Column)[] ProjectFolderColumns =
        {
            ("projects", "path"),
            ("projects", "source_path"),
            ("projects", "deploy_path"),
            ("projects", "build_output_path"),
            ("project_paths", "path_value"),
            ("session_lineage", "project_path"),
            ("session_chunks", "project_path"),
            ("session_chunks_index", "project_path"),
            ("task_worktrees", "worktree_path"),
            ("task_file_links", "file_path"),
            ("code_digests", "file_path"),
            ("project_notes", "project_path"),
            ("project_note_tabs", "project_path"),
        };

        /// <summary>Columns that store a path inside Claude Code's per-project history folder.</summary>
        internal static readonly (string Table, string Column)[] ClaudeHistoryColumns =
        {
            ("session_lineage", "session_file_path"),
        };

        /// <summary>Tables keyed by a path whose UNIQUE constraint can collide after a rewrite.</summary>
        private static readonly (string Table, string Column)[] KeyedColumns =
        {
            ("project_notes", "project_path"),
            ("project_note_tabs", "project_path"),
        };

        /// <summary>
        /// Problems that would make the rewrite fail: rows already stored under the new folder in a table
        /// keyed by path. Empty when there are none.
        /// </summary>
        public static IReadOnlyList<string> FindConflicts(SQLiteConnection connection, string newRoot)
        {
            var problems = new List<string>();
            foreach (var (table, column) in KeyedColumns)
            {
                if (!HasColumn(connection, table, column)) continue;
                int count = SelectUnder(connection, table, column, newRoot).Count;
                if (count > 0)
                    problems.Add($"MT already has {count} note(s) stored for '{newRoot}'. Remove them or choose another folder.");
            }

            return problems.Distinct().ToList();
        }

        /// <summary>
        /// Repoints every path under <paramref name="oldRoot"/> to <paramref name="newRoot"/>, rewrites the
        /// Claude history folder prefixes in <paramref name="claudeFolderMoves"/>, and records
        /// <paramref name="oldRoot"/> in the project's previous folders. All in one transaction.
        /// </summary>
        public static RelocationDbResult Apply(
            SQLiteConnection connection,
            string projectId,
            string oldRoot,
            string newRoot,
            IEnumerable<(string OldFolder, string NewFolder)> claudeFolderMoves)
        {
            var changed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var gate = SqliteWriteGate.EnterWrite("ProjectRelocationStore.Apply", projectId);
            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var (table, column) in ProjectFolderColumns)
                    Add(changed, table, column, Rebase(connection, transaction, table, column, oldRoot, newRoot));

                foreach (var (oldFolder, newFolder) in claudeFolderMoves ?? Enumerable.Empty<(string, string)>())
                {
                    foreach (var (table, column) in ClaudeHistoryColumns)
                        Add(changed, table, column, Rebase(connection, transaction, table, column, oldFolder, newFolder));
                }

                AddPreviousPath(connection, transaction, projectId, oldRoot);
                transaction.Commit();
                return new RelocationDbResult { Succeeded = true, RowsChanged = changed };
            }
            catch (SQLiteException ex)
            {
                transaction.Rollback();
                string why = ex.ResultCode == SQLiteErrorCode.Constraint
                    ? "Some of the project's records already exist under the new folder (" + ex.Message + ")."
                    : ex.Message;
                return new RelocationDbResult { Succeeded = false, Error = why };
            }
        }

        /// <summary>The project's previous folders, oldest first. Empty when it has never moved.</summary>
        public static IReadOnlyList<string> GetPreviousPaths(SQLiteConnection connection, string projectId)
        {
            if (!HasColumn(connection, "projects", "previous_paths")) return Array.Empty<string>();
            using var command = new SQLiteCommand("SELECT previous_paths FROM projects WHERE id = @id", connection);
            command.Parameters.AddWithValue("@id", projectId);
            return ParsePreviousPaths(command.ExecuteScalar() as string);
        }

        /// <summary>Splits the stored newline-separated list, dropping blanks.</summary>
        internal static IReadOnlyList<string> ParsePreviousPaths(string stored) =>
            string.IsNullOrWhiteSpace(stored)
                ? Array.Empty<string>()
                : stored.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        private static void AddPreviousPath(SQLiteConnection connection, SQLiteTransaction transaction, string projectId, string oldRoot)
        {
            if (!HasColumn(connection, "projects", "previous_paths")) return;
            var list = GetPreviousPaths(connection, projectId).ToList();
            string normalized = RelocationPaths.Normalize(oldRoot);
            if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase)) list.Add(normalized);

            using var command = new SQLiteCommand("UPDATE projects SET previous_paths = @p WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("@p", string.Join("\n", list));
            command.Parameters.AddWithValue("@id", projectId);
            command.ExecuteNonQuery();
        }

        private static void Add(Dictionary<string, int> changed, string table, string column, int count)
        {
            if (count <= 0) return;
            string key = table + "." + column;
            changed[key] = (changed.TryGetValue(key, out int prior) ? prior : 0) + count;
        }

        private static int Rebase(SQLiteConnection connection, SQLiteTransaction transaction, string table, string column, string oldRoot, string newRoot)
        {
            if (!HasColumn(connection, table, column)) return 0;
            int count = 0;
            foreach (var (rowId, value) in SelectUnder(connection, table, column, oldRoot, transaction))
            {
                string rebased = RelocationPaths.Rebase(value, oldRoot, newRoot);
                if (rebased == null || string.Equals(rebased, value, StringComparison.Ordinal)) continue;

                // CA2100: table/column come from the fixed lists above; values are parameters.
#pragma warning disable CA2100
                using var update = new SQLiteCommand($"UPDATE {table} SET {column} = @v WHERE rowid = @r", connection, transaction);
#pragma warning restore CA2100
                update.Parameters.AddWithValue("@v", rebased);
                update.Parameters.AddWithValue("@r", rowId);
                count += update.ExecuteNonQuery();
            }

            return count;
        }

        /// <summary>
        /// Rows whose value is <paramref name="root"/> or under it. The SQL narrows by a case-folded
        /// prefix (either separator); <see cref="RelocationPaths.IsSameOrUnder"/> makes the final call.
        /// </summary>
        private static List<(long RowId, string Value)> SelectUnder(SQLiteConnection connection, string table, string column, string root, SQLiteTransaction transaction = null)
        {
            var rows = new List<(long, string)>();
            string r = root.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            if (r.Length == 0) return rows;

            // CA2100: table/column come from the fixed lists in this class; values are parameters.
#pragma warning disable CA2100
            using var command = new SQLiteCommand(
                $"SELECT rowid, {column} FROM {table} WHERE {column} IS NOT NULL AND (" +
                $"lower(replace({column}, '/', '\\')) = @root OR substr(lower(replace({column}, '/', '\\')), 1, @n) = @prefix)",
                connection, transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("@root", r);
            command.Parameters.AddWithValue("@prefix", r + "\\");
            command.Parameters.AddWithValue("@n", r.Length + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string value = reader.GetString(1);
                if (RelocationPaths.IsSameOrUnder(value, root)) rows.Add((reader.GetInt64(0), value));
            }

            return rows;
        }

        private static bool HasColumn(SQLiteConnection connection, string table, string column)
        {
            // CA2100: table comes from the fixed lists in this class.
#pragma warning disable CA2100
            using var command = new SQLiteCommand($"PRAGMA table_info({table})", connection);
#pragma warning restore CA2100
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
    }
}
