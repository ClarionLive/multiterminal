using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Threading;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite storage for the C# code graph. Schema, CRUD, and low-level data access
    /// for symbols, relationships, and projects. Adapted from Frank's Clarion CodeGraph.
    /// Shares the SQLite connection from TaskDatabase (same multiterminal.db, same WAL handle).
    /// </summary>
    public class CodeGraphDatabase
    {
        private readonly SQLiteConnection _connection;
        private readonly object _syncLock = new object();

        public CodeGraphDatabase(TaskDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            _connection = db.Connection;
            CreateSchema();
        }

        private void CreateSchema()
        {
            const string sql = @"
                CREATE TABLE IF NOT EXISTS cg_projects (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    csproj_path TEXT,
                    output_type TEXT,
                    sln_path TEXT
                );

                CREATE TABLE IF NOT EXISTS cg_project_dependencies (
                    project_id INTEGER REFERENCES cg_projects(id),
                    depends_on_id INTEGER REFERENCES cg_projects(id),
                    PRIMARY KEY (project_id, depends_on_id)
                );

                CREATE TABLE IF NOT EXISTS cg_symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    line_number INTEGER,
                    project_id INTEGER REFERENCES cg_projects(id),
                    params TEXT,
                    return_type TEXT,
                    parent_name TEXT,
                    member_of TEXT,
                    scope TEXT,
                    accessibility TEXT,
                    is_static INTEGER DEFAULT 0,
                    is_async INTEGER DEFAULT 0,
                    is_abstract INTEGER DEFAULT 0,
                    generic_params TEXT,
                    source_preview TEXT
                );

                CREATE TABLE IF NOT EXISTS cg_relationships (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_id INTEGER REFERENCES cg_symbols(id),
                    to_id INTEGER REFERENCES cg_symbols(id),
                    type TEXT NOT NULL,
                    file_path TEXT,
                    line_number INTEGER
                );

                CREATE TABLE IF NOT EXISTS cg_index_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_cg_sym_name ON cg_symbols(name);
                CREATE INDEX IF NOT EXISTS idx_cg_sym_type ON cg_symbols(type);
                CREATE INDEX IF NOT EXISTS idx_cg_sym_file ON cg_symbols(file_path);
                CREATE INDEX IF NOT EXISTS idx_cg_sym_project ON cg_symbols(project_id);
                CREATE INDEX IF NOT EXISTS idx_cg_sym_member ON cg_symbols(member_of);
                CREATE INDEX IF NOT EXISTS idx_cg_rel_from ON cg_relationships(from_id);
                CREATE INDEX IF NOT EXISTS idx_cg_rel_to ON cg_relationships(to_id);
                CREATE INDEX IF NOT EXISTS idx_cg_rel_type ON cg_relationships(type);
            ";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.ExecuteNonQuery();
        }

        // --- Projects ---

        public int InsertProject(string name, string csprojPath, string outputType = null, string slnPath = null)
        {
            const string sql = @"INSERT INTO cg_projects (name, csproj_path, output_type, sln_path)
                                 VALUES (@name, @csproj, @output, @sln);
                                 SELECT last_insert_rowid();";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@csproj", (object)csprojPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@output", (object)outputType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sln", (object)slnPath ?? DBNull.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public int FindProjectIdByName(string name)
        {
            const string sql = "SELECT id FROM cg_projects WHERE name = @name COLLATE NOCASE LIMIT 1";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@name", name);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : -1;
        }

        public void InsertProjectDependency(int projectId, int dependsOnId)
        {
            const string sql = "INSERT OR IGNORE INTO cg_project_dependencies (project_id, depends_on_id) VALUES (@pid, @did)";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@did", dependsOnId);
            cmd.ExecuteNonQuery();
        }

        // --- Symbols ---

        public long InsertSymbol(CodeSymbol symbol)
        {
            const string sql = @"INSERT INTO cg_symbols
                (name, type, file_path, line_number, project_id, params, return_type,
                 parent_name, member_of, scope, accessibility, is_static, is_async, is_abstract,
                 generic_params, source_preview)
                VALUES
                (@name, @type, @file, @line, @proj, @params, @ret,
                 @parent, @member, @scope, @access, @static, @async, @abstract,
                 @generic, @preview);
                SELECT last_insert_rowid();";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@name", symbol.Name);
            cmd.Parameters.AddWithValue("@type", symbol.Type);
            cmd.Parameters.AddWithValue("@file", symbol.FilePath);
            cmd.Parameters.AddWithValue("@line", symbol.LineNumber);
            cmd.Parameters.AddWithValue("@proj", symbol.ProjectId);
            cmd.Parameters.AddWithValue("@params", (object)symbol.Params ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ret", (object)symbol.ReturnType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@parent", (object)symbol.ParentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@member", (object)symbol.MemberOf ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@scope", (object)symbol.Scope ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@access", (object)symbol.Accessibility ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@static", symbol.IsStatic ? 1 : 0);
            cmd.Parameters.AddWithValue("@async", symbol.IsAsync ? 1 : 0);
            cmd.Parameters.AddWithValue("@abstract", symbol.IsAbstract ? 1 : 0);
            cmd.Parameters.AddWithValue("@generic", (object)symbol.GenericParams ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@preview", (object)symbol.SourcePreview ?? DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public long FindSymbolId(string name, int projectId)
        {
            const string sql = "SELECT id FROM cg_symbols WHERE name = @name AND project_id = @proj LIMIT 1";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@proj", projectId);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt64(result) : -1;
        }

        public long FindSymbolIdByName(string name)
        {
            const string sql = @"SELECT s.id FROM cg_symbols s
                                 LEFT JOIN cg_relationships r ON s.id = r.from_id OR s.id = r.to_id
                                 WHERE s.name = @name COLLATE NOCASE
                                 ORDER BY CASE WHEN r.id IS NOT NULL THEN 0 ELSE 1 END
                                 LIMIT 1";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@name", name);
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt64(result) : -1;
        }

        /// <summary>
        /// Load all symbols into a dictionary for fast in-memory relationship resolution.
        /// Key: "FullyQualifiedName" or "SimpleName", Value: symbol id.
        /// </summary>
        public Dictionary<string, long> LoadSymbolLookup()
        {
            var lookup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT id, name, member_of FROM cg_symbols";
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string name = reader.GetString(1);
                string memberOf = reader.IsDBNull(2) ? null : reader.GetString(2);

                // Store fully qualified: "ClassName.MethodName"
                if (!string.IsNullOrEmpty(memberOf))
                {
                    string qualified = memberOf + "." + name;
                    lookup[qualified] = id;
                }
                // Store simple name (last writer wins for ambiguous names)
                lookup[name] = id;
            }
            return lookup;
        }

        // --- Relationships ---

        public void InsertRelationship(CodeRelationship rel)
        {
            const string sql = @"INSERT INTO cg_relationships (from_id, to_id, type, file_path, line_number)
                                 VALUES (@from, @to, @type, @file, @line)";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@from", rel.FromId);
            cmd.Parameters.AddWithValue("@to", rel.ToId);
            cmd.Parameters.AddWithValue("@type", rel.Type);
            cmd.Parameters.AddWithValue("@file", (object)rel.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@line", rel.LineNumber);
            cmd.ExecuteNonQuery();
        }

        public void ClearProjectRelationships(int projectId)
        {
            lock (_syncLock)
            {
                const string sql = @"DELETE FROM cg_relationships WHERE from_id IN (SELECT id FROM cg_symbols WHERE project_id = @pid)";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@pid", projectId);
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearProject(int projectId, bool deleteProjectRow = false)
        {
            lock (_syncLock)
            {
                const string sqlRels = @"DELETE FROM cg_relationships WHERE from_id IN (SELECT id FROM cg_symbols WHERE project_id = @pid)
                                         OR to_id IN (SELECT id FROM cg_symbols WHERE project_id = @pid)";
                using (var cmd = new SQLiteCommand(sqlRels, _connection))
                {
                    cmd.Parameters.AddWithValue("@pid", projectId);
                    cmd.ExecuteNonQuery();
                }

                const string sqlSyms = "DELETE FROM cg_symbols WHERE project_id = @pid";
                using (var cmd = new SQLiteCommand(sqlSyms, _connection))
                {
                    cmd.Parameters.AddWithValue("@pid", projectId);
                    cmd.ExecuteNonQuery();
                }

                if (deleteProjectRow)
                {
                    using (var cmd = new SQLiteCommand("DELETE FROM cg_project_dependencies WHERE project_id = @pid", _connection))
                    {
                        cmd.Parameters.AddWithValue("@pid", projectId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SQLiteCommand("DELETE FROM cg_projects WHERE id = @pid", _connection))
                    {
                        cmd.Parameters.AddWithValue("@pid", projectId);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public void ClearAll()
        {
            lock (_syncLock)
            {
                const string sql = @"
                    DELETE FROM cg_relationships;
                    DELETE FROM cg_symbols;
                    DELETE FROM cg_project_dependencies;
                    DELETE FROM cg_projects;
                    DELETE FROM cg_index_metadata;";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        // --- Metadata ---

        public void SetMetadata(string key, string value)
        {
            const string sql = "INSERT OR REPLACE INTO cg_index_metadata (key, value) VALUES (@key, @val)";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", value);
            cmd.ExecuteNonQuery();
        }

        public string GetMetadata(string key)
        {
            const string sql = "SELECT value FROM cg_index_metadata WHERE key = @key";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result != null ? result.ToString() : null;
        }

        // Per-project last_indexed timestamp, keyed by the STABLE project name rather than the
        // autoincrement id. The indexer mints a fresh id every reindex (ClearProject deleteProjectRow
        // + InsertProject), so an id-keyed metadata row would orphan on each pass and leak forever;
        // name-keying makes the row idempotent under INSERT OR REPLACE. One owner of the key format
        // so the indexer (writer) and CodeGraphWatcher (reader) can't drift.
        private static string ProjectLastIndexedKey(string projectName) => "project:" + projectName + ":last_indexed";

        public void SetProjectLastIndexed(string projectName, string isoUtcTimestamp)
            => SetMetadata(ProjectLastIndexedKey(projectName), isoUtcTimestamp);

        public string GetProjectLastIndexed(string projectName)
            => GetMetadata(ProjectLastIndexedKey(projectName));

        // --- Transactions ---

        public SQLiteTransaction BeginTransaction()
        {
            Monitor.Enter(_syncLock);
            try
            {
                return _connection.BeginTransaction();
            }
            catch
            {
                Monitor.Exit(_syncLock);
                throw;
            }
        }

        /// <summary>Call after committing or rolling back a transaction started via BeginTransaction.</summary>
        public void EndTransaction()
        {
            Monitor.Exit(_syncLock);
        }

        // --- Raw Query ---

        public DataTable ExecuteQuery(string sql, Dictionary<string, object> parameters = null)
        {
            lock (_syncLock)
            {
                // CA2100: internal helper — all callers (CodeGraphQuery.cs) pass hardcoded SQL literals and route user values through the parameters dictionary.
                #pragma warning disable CA2100
                using var cmd = new SQLiteCommand(sql, _connection);
                #pragma warning restore CA2100
                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                        cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                }
                var dt = new DataTable();
                using var adapter = new SQLiteDataAdapter(cmd);
                adapter.Fill(dt);
                return dt;
            }
        }
    }
}
