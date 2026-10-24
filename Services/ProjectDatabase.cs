using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
// Use fully-qualified names to distinguish two Project classes:
//   MultiTerminal.MCPServer.Models.Project  — lightweight kanban-grouping model used by MessageBroker
//   MultiTerminal.Models.Project            — rich file-based project model with all new fields

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting Projects.
    /// Uses the same database as TaskDatabase at %APPDATA%\multiterminal\multiterminal.db
    ///
    /// Supports two Project models:
    ///  - MCPServer.Models.Project  (lightweight, used by MessageBroker cache)
    ///  - Models.Project            (rich model, used by ProjectContextService / new features)
    /// </summary>
    public class ProjectDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

        // G9: serializes access to the shared _connection THROUGH THIS CLASS'S METHODS. After the G8
        // ProjectService unification this one ProjectDatabase is reachable concurrently from the UI
        // thread, Kestrel REST threads (CreateProject), gateway threads, and the CodeGraphWatcher timer
        // — concurrent ExecuteReader/ExecuteNonQuery on one System.Data.SQLite connection throws
        // 'database is locked'/busy. Mirrors TaskDatabase._dbLock: every public connection-touching
        // method below wraps its body in lock (_dbLock). Private reader helpers stay lock-free (they
        // run inside a locked block); the migration/schema methods run once in the ctor before any
        // concurrency. NOTE: this does NOT GLOBALLY serialize the connection — callers that borrow the
        // exposed Connection property (below) run commands the lock never sees (same escape hatch as
        // TaskDatabase). Closing that hole is a separate cross-service-serialization concern.
        private readonly object _dbLock = new object();

        // The Connection property was DELETED in ticket bb2b0104 (same close-out as TaskDatabase.Connection).
        // Callers that used to borrow this handle to read sibling tables (e.g. source_control_accounts via
        // SourceControlAccountService) now open their OWN connection through MultiterminalDb.Open(). Invariant:
        // one owner per connection; no class touches another's handle.

        /// <summary>
        /// Creates a new ProjectDatabase instance.
        /// </summary>
        public ProjectDatabase()
        {
            _databasePath = TaskDatabase.GetDatabasePath();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            // Open through the one factory (bb2b0104 condition 2) — same WAL / pooling / busy_timeout
            // as every other owner. ProjectDatabase owns this connection and serializes its own access
            // via _dbLock; no other class touches this handle (the Connection escape hatch was deleted).
            _connection = MultiterminalDb.Open();

            CreateSchema();

            // Run migrations for schema changes (additive, non-destructive)
            MigrateAddNewProjectColumns();
            MigrateAddAssociationTables();

            // Drop the mcp_registry table — gateway is now the source of truth for server catalog
            MigrateDropMcpRegistry();

            // One-time repair: heal any projects.path that points at a git-worktree directory
            // (written before the canonical-path write-guard existed). Task 19d0d867.
            MigrateRepairOrphanedWorktreePaths();
        }

        /// <summary>
        /// One-time, idempotent repair (task 19d0d867): rewrite any <c>projects.path</c> that points
        /// at a git-worktree directory back to its stable repo root. A worktree path is ephemeral —
        /// once the worktree is pruned the project becomes invisible to <see cref="CodeGraphWatcher"/>
        /// and every other path-dependent feature (the watcher's <c>Directory.Exists</c> gate skipped
        /// it silently). The write-guard (<see cref="CanonicalizeProjectPath"/>) stops new worktree
        /// paths from being persisted; this sweep heals rows written before that guard existed.
        ///
        /// <para>Idempotent: re-running finds nothing once paths are canonical. Only rewrites when a
        /// stable repo root is resolvable (validated to contain <c>.git</c>); a worktree whose repo
        /// root is also gone is left untouched and logged rather than guessed at.</para>
        /// </summary>
        private void MigrateRepairOrphanedWorktreePaths()
        {
            try
            {
                var rows = new List<(string Id, string Name, string Path, string SourcePath)>();
                using (var sel = new SQLiteCommand(
                    "SELECT id, name, path, source_path FROM projects WHERE path IS NOT NULL AND path <> ''", _connection))
                using (var r = sel.ExecuteReader())
                {
                    while (r.Read())
                        rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetString(3)));
                }
                if (rows.Count == 0) return;

                int repaired = 0;
                foreach (var row in rows)
                {
                    // CanonicalizeProjectPath returns the input unchanged for non-worktree paths and
                    // for worktree paths with no resolvable target (it logs the latter as a warning).
                    string target = CanonicalizeProjectPath(row.Path, row.SourcePath, row.Id, row.Name);
                    if (string.Equals(target, row.Path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var upd = new SQLiteCommand(
                        "UPDATE projects SET path = @p, updated_at = @u WHERE id = @id", _connection);
                    upd.Parameters.AddWithValue("@p", target);
                    upd.Parameters.AddWithValue("@u", DateTime.UtcNow);
                    upd.Parameters.AddWithValue("@id", row.Id);
                    upd.ExecuteNonQuery();
                    repaired++;
                }

                if (repaired > 0)
                    System.Diagnostics.Debug.WriteLine(
                        $"[ProjectDatabase] Worktree-path migration: repaired {repaired} of {rows.Count} project path(s).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectDatabase] MigrateRepairOrphanedWorktreePaths failed: {ex.Message}");
            }
        }

        private void CreateSchema()
        {
            const string schema = @"
                -- Projects table
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    path TEXT,
                    created_by TEXT,
                    created_at DATETIME NOT NULL,
                    updated_at DATETIME NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(name);
                CREATE INDEX IF NOT EXISTS idx_projects_created_by ON projects(created_by);
            ";

            using var command = new SQLiteCommand(schema, _connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Migration to add enhanced project columns to the projects table.
        /// Adds: source_path, deploy_path, build_output_path, build_command, deploy_command,
        /// launch_command, project_type, current_version, change_log, is_pinned, icon, icon_color,
        /// last_opened_at, git_repo_url, git_default_branch, git_auto_commit
        /// </summary>
        private void MigrateAddNewProjectColumns()
        {
            const string checkSql = "PRAGMA table_info(projects)";
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var newColumns = new List<(string name, string definition)>
            {
                // 'path' is created by ProjectDatabase's own CREATE TABLE, but TaskDatabase
                // (constructed FIRST — MessageBroker builds _taskDb before _projectDb) also does
                // CREATE TABLE IF NOT EXISTS projects with a 6-column subset that OMITS 'path'.
                // On a clean install TaskDatabase wins the IF-NOT-EXISTS race, so ProjectDatabase's
                // CREATE no-ops and 'path' is never created — then GetAllRichProjects' SELECT of
                // 'path' throws "no such column", the Home screen's project push is swallowed, and
                // the skeleton cards hang forever. Adding it to this idempotent list heals every
                // fresh/already-broken DB on next launch (no-op where 'path' already exists). task df1f521f
                ("path",               "TEXT"),
                ("source_path",        "TEXT"),
                ("deploy_path",        "TEXT"),
                ("build_output_path",  "TEXT"),
                ("build_command",      "TEXT"),
                ("deploy_command",     "TEXT"),
                ("launch_command",     "TEXT"),
                ("project_type",       "TEXT"),
                ("current_version",    "TEXT"),
                ("change_log",         "TEXT"),
                ("is_pinned",          "INTEGER DEFAULT 0"),
                ("icon",               "TEXT"),
                ("icon_color",         "TEXT"),
                ("last_opened_at",     "DATETIME"),
                ("git_repo_url",       "TEXT"),
                ("git_default_branch", "TEXT"),
                ("git_auto_commit",    "INTEGER DEFAULT 0"),
                ("team_lead",          "TEXT"),
                ("default_terminal",   "TEXT NOT NULL DEFAULT 'claude-code'"),
                ("source_control_account_id", "TEXT"),
                ("status",             "TEXT DEFAULT 'active'"),
                // No DEFAULT: NULL reads as off, so every existing project keeps today's startup
                // (Owner decision, GitHub #34 / task e0fa9d90). Project.QuietStart explains why null.
                ("quiet_start",        "INTEGER"),
                // Claude Code config profile NAME (ticket c8ee0eec, GitHub #21). NULL = the implicit
                // Default profile, so every existing project keeps launching as it does today.
                ("claude_profile",     "TEXT"),
                // No DEFAULT: NULL reads as 'ask' (task ec2ed636), so every existing project keeps
                // prompting. Project.ParseLaunchMode maps NULL/unknown to Ask.
                ("launch_mode",        "TEXT"),
                // GitHub watch (task 92eeeb47). No DEFAULTs: NULL reads as off, so no existing
                // project starts calling GitHub. SQLite-only, like launch_mode.
                ("github_watch_mode",     "TEXT"),
                ("github_check_on_start", "INTEGER"),
                ("github_check_interval", "TEXT")
            };

            foreach (var (colName, colDef) in newColumns)
            {
                if (!existingColumns.Contains(colName))
                {
                    string alterSql = $"ALTER TABLE projects ADD COLUMN {colName} {colDef}";
                    // CA2100: colName/colDef come from the hardcoded newColumns tuple list above — no user input reaches this SQL.
                    #pragma warning disable CA2100
                    using var alterCommand = new SQLiteCommand(alterSql, _connection);
                    #pragma warning restore CA2100
                    alterCommand.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Migration to create the 6 association tables for project-related entities.
        /// All tables use foreign keys referencing projects.id with ON DELETE CASCADE.
        /// </summary>
        private void MigrateAddAssociationTables()
        {
            const string createSql = @"
                -- Agents assigned to a project
                CREATE TABLE IF NOT EXISTS project_agents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    agent_name TEXT NOT NULL,
                    role TEXT,
                    preferred_model TEXT,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                    UNIQUE(project_id, agent_name)
                );
                CREATE INDEX IF NOT EXISTS idx_project_agents_project ON project_agents(project_id);

                -- MCP servers configured for a project
                CREATE TABLE IF NOT EXISTS project_mcp_servers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    server_name TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                    UNIQUE(project_id, server_name)
                );
                CREATE INDEX IF NOT EXISTS idx_project_mcp_servers_project ON project_mcp_servers(project_id);

                -- Specialist agents for a project (devils-advocate, verifier, etc.)
                CREATE TABLE IF NOT EXISTS project_specialist_agents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    agent_type TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    custom_prompt TEXT,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                    UNIQUE(project_id, agent_type)
                );
                CREATE INDEX IF NOT EXISTS idx_project_specialist_agents_project ON project_specialist_agents(project_id);

                -- Important paths for a project
                CREATE TABLE IF NOT EXISTS project_paths (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    path_type TEXT NOT NULL,
                    path_value TEXT NOT NULL,
                    description TEXT,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_project_paths_project ON project_paths(project_id);

                -- Prompts/instructions for a project
                CREATE TABLE IF NOT EXISTS project_prompts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    prompt_type TEXT NOT NULL,
                    prompt_text TEXT NOT NULL,
                    display_order INTEGER DEFAULT 0,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_project_prompts_project ON project_prompts(project_id);

                -- Skills enabled for a project
                CREATE TABLE IF NOT EXISTS project_skills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    skill_name TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                    UNIQUE(project_id, skill_name)
                );
                CREATE INDEX IF NOT EXISTS idx_project_skills_project ON project_skills(project_id);

                -- GitHub watch (task 92eeeb47): per project and repo, the newest issue/PR number
                -- already accounted for. GitHub numbers issues and PRs from one rising sequence, so
                -- anything above the baseline is new.
                CREATE TABLE IF NOT EXISTS github_watch_state (
                    project_id TEXT NOT NULL,
                    repo TEXT NOT NULL,
                    -- NULL until a check SUCCEEDS: a failed first check must not invent a baseline
                    -- (0 would make the whole backlog new; pipeline Run 1).
                    baseline_number INTEGER,
                    last_checked_at DATETIME,
                    last_success_at DATETIME,
                    last_error TEXT,
                    PRIMARY KEY (project_id, repo)
                );

                -- One row per issue/PR the watcher reported; the INSERT OR IGNORE on it is what
                -- makes a card at most once, even if two checks overlap.
                CREATE TABLE IF NOT EXISTS github_watch_seen (
                    project_id TEXT NOT NULL,
                    repo TEXT NOT NULL,
                    number INTEGER NOT NULL,
                    is_pr INTEGER NOT NULL DEFAULT 0,
                    task_id TEXT,
                    seen_at DATETIME NOT NULL,
                    PRIMARY KEY (project_id, repo, number)
                );
            ";

            // A github_watch_state from an unreleased build of task 92eeeb47 has a NOT NULL baseline and
            // no last_success_at, and CREATE TABLE IF NOT EXISTS would keep it. Nothing in it is worth
            // keeping (it only holds baselines), so drop it and let the CREATE below make it again.
            using (var probe = new SQLiteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'github_watch_state' " +
                "AND NOT EXISTS (SELECT 1 FROM pragma_table_info('github_watch_state') WHERE name = 'last_success_at')", _connection))
            {
                if (Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    using var drop = new SQLiteCommand("DROP TABLE github_watch_state", _connection);
                    drop.ExecuteNonQuery();
                }
            }

            using var command = new SQLiteCommand(createSql, _connection);
            command.ExecuteNonQuery();
        }

        #region Project Operations (MCPServer.Models.Project — used by MessageBroker)

        /// <summary>
        /// Get a project by ID. Returns the lightweight MCPServer.Models.Project used by MessageBroker.
        /// </summary>
        public MultiTerminal.MCPServer.Models.Project GetProject(string projectId)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, name, description, path, created_by, created_at, updated_at
                    FROM projects
                    WHERE id = @id
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", projectId);
                using var reader = command.ExecuteReader();

                if (reader.Read())
                {
                    return ReadProject(reader);
                }

                return null;
            }
        }

        /// <summary>
        /// Get all projects. Returns lightweight MCPServer.Models.Project list used by MessageBroker.
        /// </summary>
        public List<MultiTerminal.MCPServer.Models.Project> GetAllProjects()
        {
            var projects = new List<MultiTerminal.MCPServer.Models.Project>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, name, description, path, created_by, created_at, updated_at
                    FROM projects
                    ORDER BY created_at DESC
                ";

                using var command = new SQLiteCommand(sql, _connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    projects.Add(ReadProject(reader));
                }
            }

            return projects;
        }

        /// <summary>
        /// Save a project (insert or update) using the lightweight MCPServer.Models.Project.
        /// Used by MessageBroker.
        /// </summary>
        public void SaveProject(MultiTerminal.MCPServer.Models.Project project)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO projects (id, name, description, path, created_by, created_at, updated_at)
                    VALUES (@id, @name, @description, @path, @createdBy, @createdAt, @updatedAt)
                    ON CONFLICT(id) DO UPDATE SET
                        name = @name,
                        description = @description,
                        path = @path,
                        updated_at = @updatedAt
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", project.Id);
                command.Parameters.AddWithValue("@name", project.Name);
                command.Parameters.AddWithValue("@description", (object)project.Description ?? DBNull.Value);
                // 5-column model carries no source_path; repo-root derivation is the only signal.
                command.Parameters.AddWithValue("@path",
                    (object)CanonicalizeProjectPath(project.Path, null, project.Id, project.Name) ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdBy", (object)project.CreatedBy ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdAt", project.CreatedAt);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Update a project's name and description.
        /// </summary>
        /// <returns>True if a project was updated, false if project not found.</returns>
        public bool UpdateProject(string projectId, string name, string description)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    UPDATE projects
                    SET name = COALESCE(@name, name),
                        description = COALESCE(@description, description),
                        updated_at = @updatedAt
                    WHERE id = @id
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", projectId);
                command.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);
                command.Parameters.AddWithValue("@description", (object)description ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

                return command.ExecuteNonQuery() > 0;
            }
        }

        // Whitelist of project columns that are safe to update via UpdateProjectField.
        // Boolean fields are stored as INTEGER 0/1 in SQLite.
        private static readonly HashSet<string> _allowedProjectFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "description", "path", "source_path", "deploy_path", "build_output_path",
            "build_command", "deploy_command", "launch_command", "project_type", "current_version",
            "change_log", "icon", "icon_color", "git_repo_url", "git_default_branch", "git_auto_commit",
            "is_pinned", "status", "created_by", "last_opened_at", "team_lead", "default_terminal",
            "source_control_account_id", "quiet_start", "claude_profile",
            "github_watch_mode", "github_check_on_start", "github_check_interval"
        };

        // Map camelCase JS field names to snake_case SQLite column names.
        // JS sends data-field attributes in camelCase; SQLite columns are snake_case.
        private static readonly Dictionary<string, string> _fieldNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "name", ["description"] = "description", ["path"] = "path",
            ["sourcePath"] = "source_path", ["deployPath"] = "deploy_path",
            ["buildOutputPath"] = "build_output_path", ["buildCommand"] = "build_command",
            ["deployCommand"] = "deploy_command", ["launchCommand"] = "launch_command",
            ["projectType"] = "project_type", ["currentVersion"] = "current_version",
            ["changeLog"] = "change_log", ["icon"] = "icon", ["iconColor"] = "icon_color",
            ["gitRepoUrl"] = "git_repo_url", ["gitDefaultBranch"] = "git_default_branch",
            ["gitAutoCommit"] = "git_auto_commit", ["isPinned"] = "is_pinned",
            ["status"] = "status", ["createdBy"] = "created_by", ["lastOpenedAt"] = "last_opened_at",
            ["teamLead"] = "team_lead", ["defaultTerminal"] = "default_terminal",
            ["sourceControlAccountId"] = "source_control_account_id",
            ["quietStart"] = "quiet_start",
            ["claudeProfile"] = "claude_profile",
            ["gitHubWatchMode"] = "github_watch_mode",
            ["gitHubCheckOnStart"] = "github_check_on_start",
            ["gitHubCheckInterval"] = "github_check_interval",
        };

        // Fields that map to INTEGER 0/1 booleans in the database.
        private static readonly HashSet<string> _booleanProjectFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "git_auto_commit", "is_pinned", "quiet_start", "github_check_on_start"
        };

        /// <summary>
        /// Update a single project field by column name.
        /// Uses a whitelist to prevent SQL injection via field name.
        /// Boolean fields ("git_auto_commit", "is_pinned") accept "true"/"false" strings
        /// or "1"/"0" and convert to INTEGER automatically.
        /// </summary>
        /// <returns>True if updated, false if project not found or field not allowed.</returns>
        public bool UpdateProjectField(string projectId, string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(fieldName))
                return false;

            // Normalize camelCase from JS to snake_case for SQLite
            if (_fieldNameMap.TryGetValue(fieldName, out var mapped))
                fieldName = mapped;

            // Enforce column whitelist — fieldName is embedded directly in SQL so it MUST be validated
            if (!_allowedProjectFields.Contains(fieldName))
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectDatabase] UpdateProjectField rejected disallowed field: {fieldName}");
                return false;
            }

            // Determine the parameter value — booleans are stored as 0/1 INTEGER
            object paramValue;
            if (_booleanProjectFields.Contains(fieldName))
            {
                bool boolVal = value == "true" || value == "1";
                paramValue = boolVal ? 1 : 0;
            }
            else if (string.Equals(fieldName, "path", StringComparison.OrdinalIgnoreCase))
            {
                // Guard the canonical path column: a worktree path must never be persisted here
                // (task 19d0d867). Load the row's existing source_path so a csproj-in-subfolder
                // project canonicalizes to its real subfolder (within the repo) instead of the bare,
                // unwatchable repo root. projectName is unavailable here; the id traces the correction.
                string existingSourcePath = ReadProjectSourcePath(projectId);
                paramValue = (object)CanonicalizeProjectPath(value, existingSourcePath, projectId, projectId) ?? DBNull.Value;
            }
            else if (string.Equals(fieldName, "claude_profile", StringComparison.OrdinalIgnoreCase))
            {
                // Blank or "Default" clears the column, the same as SetClaudeProfile.
                paramValue = (object)NormalizeClaudeProfileName(value) ?? DBNull.Value;
            }
            else
            {
                paramValue = (object)value ?? DBNull.Value;
            }

            // Safe to interpolate fieldName here because it passed the whitelist check above
            string sql = $"UPDATE projects SET {fieldName} = @value, updated_at = @updatedAt WHERE id = @id";

            lock (_dbLock)
            {
                bool watchField = fieldName == "github_watch_mode" || fieldName == "git_repo_url";
                string oldValue = watchField ? ReadProjectText(projectId, fieldName) : null;

                // CA2100 / CA3001: fieldName is whitelist-validated against _allowedProjectFields above (rejected if not present);
                // all user-supplied values (projectId, paramValue) flow through SQLiteParameter.
                #pragma warning disable CA2100
                #pragma warning disable CA3001
                using var command = new SQLiteCommand(sql, _connection);
                #pragma warning restore CA3001
                #pragma warning restore CA2100
                command.Parameters.AddWithValue("@id", projectId);
                command.Parameters.AddWithValue("@value", paramValue);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

                bool updated = command.ExecuteNonQuery() > 0;
                // Here, not in one caller: the form, PATCH /api/projects/{id} and the MCP update_project
                // tool all write through this method (task 92eeeb47, pipeline Run 2).
                if (updated && watchField && ShouldResetGitHubWatch(fieldName, oldValue, value))
                    ResetGitHubWatchState(projectId);
                return updated;
            }
        }

        /// <summary>
        /// Whether a write to a GitHub watch field starts the watch from a fresh baseline (task 92eeeb47):
        /// watching turned on from Off, or the Repository URL now names another repository. A baseline
        /// left from before covers a gap nobody watched, and everything created in it would become
        /// cards. The URL is compared as the parsed <c>owner/name</c>, so writing the same repository
        /// another way (ssh form, a trailing .git) keeps the baseline. Pure.
        /// </summary>
        internal static bool ShouldResetGitHubWatch(string column, string oldValue, string newValue)
        {
            if (column == "github_watch_mode")
                return MultiTerminal.Models.GitHubWatchSettings.ParseMode(oldValue) == MultiTerminal.Models.GitHubWatchMode.Off
                    && MultiTerminal.Models.GitHubWatchSettings.ParseMode(newValue) != MultiTerminal.Models.GitHubWatchMode.Off;
            if (column == "git_repo_url")
                return !string.Equals(GitHubRepoRef.TryParse(oldValue)?.FullName, GitHubRepoRef.TryParse(newValue)?.FullName,
                    StringComparison.OrdinalIgnoreCase);
            return false;
        }

        /// <summary>A project's stored GitHub watch mode or Repository URL, or null.</summary>
        private string ReadProjectText(string projectId, string column)
        {
            // Fixed statements, not an interpolated column: nothing a caller passes reaches the SQL text.
            string sql = column switch
            {
                "github_watch_mode" => "SELECT github_watch_mode FROM projects WHERE id = @id",
                "git_repo_url" => "SELECT git_repo_url FROM projects WHERE id = @id",
                _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a GitHub watch column."),
            };
            // Re-entrant: both callers already hold _dbLock.
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", projectId);
                var result = command.ExecuteScalar();
                return result == null || result is DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Delete a project by ID.
        /// </summary>
        public bool DeleteProject(string projectId)
        {
            // One transaction: the child rows, then the project row (task 9f95ab0c Run 4). The child tables
            // declare ON DELETE CASCADE, but SQLite enforces foreign keys only with PRAGMA foreign_keys=ON,
            // which MultiterminalDb.Open never sets, so the cascade never ran and every delete left orphans.
            // Deleted explicitly rather than enabling foreign keys globally: existing data may already hold
            // dangling references that enforcement would start rejecting elsewhere.
            // GATE BEFORE _dbLock, the SqliteWriteGate lock-ordering rule.
            using var writeGate = SqliteWriteGate.EnterWrite("ProjectDatabase.DeleteProject", projectId);
            lock (_dbLock)
            {
                using var tx = _connection.BeginTransaction();
                foreach (var table in ProjectChildTables)
                {
#pragma warning disable CA2100 // table names come from the constant ProjectChildTables list; the id is a parameter
                    using var child = new SQLiteCommand($"DELETE FROM {table} WHERE project_id = @id", _connection, tx);
#pragma warning restore CA2100
                    child.Parameters.AddWithValue("@id", projectId);
                    child.ExecuteNonQuery();
                }

                using var command = new SQLiteCommand("DELETE FROM projects WHERE id = @id", _connection, tx);
                command.Parameters.AddWithValue("@id", projectId);
                bool deleted = command.ExecuteNonQuery() > 0;
                tx.Commit();
                return deleted;
            }
        }

        /// <summary>
        /// Runs <paramref name="body"/> as one transaction on this class's connection: every write the body
        /// makes through this class's public methods commits together or not at all (ticket e20e2ce0, so a
        /// project settings save cannot leave half its lists written). _dbLock is held throughout and is
        /// re-entrant, so those methods run inside it unchanged; other threads wait for the commit.
        /// The body must not touch another connection owner: that owner's write would wait on this
        /// transaction's file lock while this thread waits on it.
        /// </summary>
        public void RunInTransaction(string operation, string projectId, Action body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            // GATE BEFORE _dbLock, the SqliteWriteGate lock-ordering rule.
            using var writeGate = SqliteWriteGate.EnterWrite(operation, projectId);
            lock (_dbLock)
            {
                using var tx = _connection.BeginTransaction();
                body();
                tx.Commit();
            }
        }

        /// <summary>Every table whose rows belong to a project (project_id REFERENCES projects(id)).</summary>
        internal static readonly string[] ProjectChildTables =
        {
            "project_agents",
            "project_mcp_servers",
            "project_specialist_agents",
            "project_paths",
            "project_prompts",
            "project_skills",
        };

        /// <summary>
        /// Get task count for a project.
        /// </summary>
        public int GetProjectTaskCount(string projectId)
        {
            lock (_dbLock)
            {
                const string sql = "SELECT COUNT(*) FROM tasks WHERE project_id = @projectId";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);

                var result = command.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        /// <summary>
        /// Delete all projects (for testing/reset).
        /// </summary>
        public void DeleteAllProjects()
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM projects";

                using var command = new SQLiteCommand(sql, _connection);
                command.ExecuteNonQuery();
            }
        }

        private MultiTerminal.MCPServer.Models.Project ReadProject(SQLiteDataReader reader)
        {
            return new MultiTerminal.MCPServer.Models.Project
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Path = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetDateTime(5),
                UpdatedAt = reader.GetDateTime(6)
            };
        }

        #endregion

        #region Rich Project Operations (Models.Project — used by ProjectContextService)

        /// <summary>
        /// Get the full rich project record including all new enhanced columns.
        /// Returns MultiTerminal.Models.Project used by ProjectContextService.
        /// </summary>
        public MultiTerminal.Models.Project GetRichProject(string projectId)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, name, description, path, created_by, created_at, updated_at,
                           source_path, deploy_path, build_output_path, build_command, deploy_command,
                           launch_command, project_type, current_version, change_log, is_pinned,
                           icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                           team_lead, default_terminal, source_control_account_id, status, quiet_start,
                           claude_profile, launch_mode,
                           github_watch_mode, github_check_on_start, github_check_interval
                    FROM projects
                    WHERE id = @id
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", projectId);
                using var reader = command.ExecuteReader();

                if (reader.Read())
                {
                    return ReadRichProject(reader);
                }

                return null;
            }
        }

        /// <summary>
        /// Get all projects as rich model records ordered by pinned first, then created_at desc.
        /// </summary>
        public List<MultiTerminal.Models.Project> GetAllRichProjects()
        {
            var projects = new List<MultiTerminal.Models.Project>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, name, description, path, created_by, created_at, updated_at,
                           source_path, deploy_path, build_output_path, build_command, deploy_command,
                           launch_command, project_type, current_version, change_log, is_pinned,
                           icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                           team_lead, default_terminal, source_control_account_id, status, quiet_start,
                           claude_profile, launch_mode,
                           github_watch_mode, github_check_on_start, github_check_interval
                    FROM projects
                    ORDER BY is_pinned DESC, created_at DESC
                ";

                using var command = new SQLiteCommand(sql, _connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    projects.Add(ReadRichProject(reader));
                }
            }

            return projects;
        }

        /// <summary>
        /// Save (upsert) the full rich project record including all new enhanced columns.
        /// Used by ProjectContextService and ProjectJsonMigrationService.
        /// </summary>
        /// <summary>
        /// Guards the canonical <c>projects.path</c> column: when <paramref name="path"/> is a
        /// git-worktree directory (current <c>.../.claude/worktrees/&lt;id&gt;</c> or a legacy
        /// layout), rewrite it to a STABLE path — preferring <paramref name="sourcePath"/>, else the
        /// derived repo root (see <see cref="WorktreeLayout.TryResolveStableProjectPath"/>). A
        /// worktree path is ephemeral — once the worktree is pruned (e.g. on task completion) the
        /// project silently becomes invisible to every path-dependent feature (CodeGraphWatcher's
        /// <c>Directory.Exists</c> eligibility gate, etc.). The canonical path must always be the
        /// durable project root; the active worktree is tracked separately by the broker
        /// (<c>get_active_worktree</c>). A correction is logged (not silent) so the original
        /// injecting caller can be traced. Task 19d0d867.
        /// </summary>
        /// <summary>
        /// Reads a project's stored <c>source_path</c> by id (null if unset/not found). Used by the
        /// single-field <see cref="UpdateProjectField"/> path-guard so a worktree path can be
        /// canonicalized to the project's real subfolder, not just the bare repo root. Task 19d0d867.
        /// </summary>
        private string ReadProjectSourcePath(string projectId)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand("SELECT source_path FROM projects WHERE id = @id", _connection);
                command.Parameters.AddWithValue("@id", projectId);
                var result = command.ExecuteScalar();
                return (result == null || result == DBNull.Value) ? null : (string)result;
            }
        }

        private static string CanonicalizeProjectPath(string path, string sourcePath, string projectId, string projectName)
        {
            if (WorktreeLayout.TryResolveStableProjectPath(path, sourcePath, out var stable))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ProjectDatabase] Canonicalized worktree path for project '{projectName}' ({projectId}): '{path}' -> '{stable}'");
                return stable;
            }

            // A worktree path we couldn't resolve to a stable target — surface it rather than
            // persisting a vanishing path silently.
            if (WorktreeLayout.LooksLikeWorktreePath(path))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ProjectDatabase] WARNING: project '{projectName}' ({projectId}) path is a worktree path with no resolvable stable root; persisting as-is: '{path}'");
            }
            return path;
        }

        public void SaveRichProject(MultiTerminal.Models.Project project)
        {
            // COALESCE preserves existing non-null values when the incoming project has nulls.
            // This prevents code paths that load from project.json (which lacks SQLite-only
            // fields like team_lead, icon, project_type, etc.) from wiping those values.
            // source_control_account_id is ALSO COALESCE-ed: several callers re-save via the
            // SaveProject -> SaveRichProject wrapper from a project.json that doesn't carry the
            // binding (ChangelogService task-done, VersioningService bump), so a null here must
            // NOT erase the SQLite binding. The deliberate "(None)"/clear is applied out-of-band by
            // UpdateProjectField (ProjectSettingsService, ticket e20e2ce0; SetSourceControlAccount
            // does the same but lost its last caller with EditProjectDialog), never via this UPSERT.
            // claude_profile is stricter still: it is written on INSERT only and the ON CONFLICT
            // branch leaves it alone. project.json DOES carry the profile name, so a COALESCE would
            // let a stale file value replace a profile changed since (the re-savers above), and a
            // terminal would then launch under the wrong account with nothing said. An existing
            // row's profile changes only through SetClaudeProfile / UpdateProjectField.
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO projects (id, name, description, path, created_by, created_at, updated_at,
                           source_path, deploy_path, build_output_path, build_command, deploy_command,
                           launch_command, project_type, current_version, change_log, is_pinned,
                           icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                           team_lead, default_terminal, source_control_account_id, status, quiet_start,
                           claude_profile)
                    VALUES (@id, @name, @description, @path, @createdBy, @createdAt, @updatedAt,
                           @sourcePath, @deployPath, @buildOutputPath, @buildCommand, @deployCommand,
                           @launchCommand, @projectType, @currentVersion, @changeLog, @isPinned,
                           @icon, @iconColor, @lastOpenedAt, @gitRepoUrl, @gitDefaultBranch, @gitAutoCommit,
                           @teamLead, @defaultTerminal, @sourceControlAccountId, @status, @quietStart,
                           @claudeProfile)
                    ON CONFLICT(id) DO UPDATE SET
                        name = COALESCE(@name, projects.name),
                        description = COALESCE(@description, projects.description),
                        path = COALESCE(@path, projects.path),
                        updated_at = @updatedAt,
                        source_path = COALESCE(@sourcePath, projects.source_path),
                        deploy_path = COALESCE(@deployPath, projects.deploy_path),
                        build_output_path = COALESCE(@buildOutputPath, projects.build_output_path),
                        build_command = COALESCE(@buildCommand, projects.build_command),
                        deploy_command = COALESCE(@deployCommand, projects.deploy_command),
                        launch_command = COALESCE(@launchCommand, projects.launch_command),
                        project_type = COALESCE(@projectType, projects.project_type),
                        current_version = COALESCE(@currentVersion, projects.current_version),
                        change_log = COALESCE(@changeLog, projects.change_log),
                        is_pinned = @isPinned,
                        icon = COALESCE(@icon, projects.icon),
                        icon_color = COALESCE(@iconColor, projects.icon_color),
                        last_opened_at = COALESCE(@lastOpenedAt, projects.last_opened_at),
                        git_repo_url = COALESCE(@gitRepoUrl, projects.git_repo_url),
                        git_default_branch = COALESCE(@gitDefaultBranch, projects.git_default_branch),
                        git_auto_commit = @gitAutoCommit,
                        team_lead = COALESCE(@teamLead, projects.team_lead),
                        default_terminal = COALESCE(@defaultTerminal, projects.default_terminal),
                        source_control_account_id = COALESCE(@sourceControlAccountId, projects.source_control_account_id),
                        status = COALESCE(@status, projects.status),
                        quiet_start = COALESCE(@quietStart, projects.quiet_start)
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", project.Id);
                command.Parameters.AddWithValue("@name", project.Name);
                command.Parameters.AddWithValue("@description", (object)project.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("@path",
                    (object)CanonicalizeProjectPath(project.Path, project.SourcePath, project.Id, project.Name) ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdBy", (object)project.CreatedBy ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdAt", project.CreatedAt == default ? DateTime.UtcNow : project.CreatedAt);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@sourcePath", (object)project.SourcePath ?? DBNull.Value);
                command.Parameters.AddWithValue("@deployPath", (object)project.DeployPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@buildOutputPath", (object)project.BuildOutputPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@buildCommand", (object)project.BuildCommand ?? DBNull.Value);
                command.Parameters.AddWithValue("@deployCommand", (object)project.DeployCommand ?? DBNull.Value);
                command.Parameters.AddWithValue("@launchCommand", (object)project.LaunchCommand ?? DBNull.Value);
                command.Parameters.AddWithValue("@projectType", (object)project.ProjectType ?? DBNull.Value);
                command.Parameters.AddWithValue("@currentVersion", (object)project.CurrentVersion ?? DBNull.Value);
                command.Parameters.AddWithValue("@changeLog", (object)project.ChangeLog ?? DBNull.Value);
                command.Parameters.AddWithValue("@isPinned", project.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("@icon", (object)project.Icon ?? DBNull.Value);
                command.Parameters.AddWithValue("@iconColor", (object)project.IconColor ?? DBNull.Value);
                command.Parameters.AddWithValue("@lastOpenedAt", project.LastOpenedAt == default ? (object)DBNull.Value : project.LastOpenedAt);
                command.Parameters.AddWithValue("@gitRepoUrl", (object)project.GitRepoUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("@gitDefaultBranch", (object)project.GitDefaultBranch ?? DBNull.Value);
                command.Parameters.AddWithValue("@gitAutoCommit", project.GitAutoCommit ? 1 : 0);
                command.Parameters.AddWithValue("@teamLead", (object)project.TeamLead ?? DBNull.Value);
                // Normalize on write so an unrecognized string can never persist — callers
                // always see a canonical value on read.
                command.Parameters.AddWithValue("@defaultTerminal",
                    (object)MultiTerminal.Models.TerminalKindHelper.Normalize(project.DefaultTerminal) ?? DBNull.Value);
                command.Parameters.AddWithValue("@sourceControlAccountId", (object)project.SourceControlAccountId ?? DBNull.Value);
                // NULL when the caller didn't set a status (e.g. a project.json re-save) so the
                // COALESCE preserves an existing Parked/Archived — mirrors team_lead. When set,
                // normalize to the lowercased canonical form so an unrecognized string can never
                // persist; readers re-normalize to the capitalized display form.
                command.Parameters.AddWithValue("@status",
                    string.IsNullOrWhiteSpace(project.Status)
                        ? (object)DBNull.Value
                        : MultiTerminal.Models.Project.NormalizeStatus(project.Status).ToLowerInvariant());
                // NULL when unset so the COALESCE keeps the stored value (see Project.QuietStart).
                command.Parameters.AddWithValue("@quietStart",
                    project.QuietStart.HasValue ? (object)(project.QuietStart.Value ? 1 : 0) : DBNull.Value);
                // Used by the INSERT only; the ON CONFLICT branch never mentions claude_profile.
                command.Parameters.AddWithValue("@claudeProfile",
                    (object)NormalizeClaudeProfileName(project.ClaudeProfile) ?? DBNull.Value);

                // A null URL keeps the stored one (COALESCE), so only a non-null one can repoint the
                // watch; the same reset rule as UpdateProjectField (task 92eeeb47, pipeline Run 2).
                string oldRepoUrl = project.GitRepoUrl != null ? ReadProjectText(project.Id, "git_repo_url") : null;

                command.ExecuteNonQuery();

                if (project.GitRepoUrl != null && ShouldResetGitHubWatch("git_repo_url", oldRepoUrl, project.GitRepoUrl))
                    ResetGitHubWatchState(project.Id);
            }
        }

        /// <summary>
        /// Updates ONLY <c>last_opened_at</c> for a project (93ad8184). Deliberately NOT the
        /// SaveRichProject UPSERT: the recency stamp is written best-effort from a background
        /// thread, and a deferred full-row snapshot could resurrect a deleted project (the
        /// UPSERT inserts) or revert a concurrent edit (the snapshot's non-null fields win the
        /// per-column COALESCE). A plain single-column UPDATE can do neither — it touches no
        /// other field and affects zero rows if the project was deleted in the interim.
        /// Monotonic: an out-of-order deferred stamp (two fire-and-forget opens completing in
        /// reverse) can never move the timestamp backwards.
        /// </summary>
        public void UpdateLastOpened(string projectId, DateTime lastOpenedAt)
        {
            if (string.IsNullOrWhiteSpace(projectId)) return;
            lock (_dbLock)
            {
                const string sql = @"UPDATE projects SET last_opened_at = @lastOpenedAt
                                     WHERE id = @id AND (last_opened_at IS NULL OR last_opened_at < @lastOpenedAt)";
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@lastOpenedAt", lastOpenedAt);
                command.Parameters.AddWithValue("@id", projectId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Sets (or clears) a project's source control account binding directly, out-of-band
        /// from the COALESCE-ing SaveRichProject UPSERT. A null/empty accountId CLEARS the
        /// binding ("(None)"). This is the only SaveRichProject-independent path that can erase
        /// the binding, so a stale project.json re-save can never wipe it accidentally.
        /// </summary>
        /// <returns>True if a row was updated, false if the project was not found.</returns>
        public bool SetSourceControlAccount(string projectId, string accountId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return false;

            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "UPDATE projects SET source_control_account_id = @accountId, updated_at = @updatedAt WHERE id = @id",
                    _connection);
                command.Parameters.AddWithValue("@accountId",
                    string.IsNullOrEmpty(accountId) ? (object)DBNull.Value : accountId);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@id", projectId);

                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Sets a project's quiet-start flag (GitHub #34, task e0fa9d90) out-of-band from the
        /// COALESCE-ing SaveRichProject UPSERT, for the same reason as
        /// <see cref="SetSourceControlAccount"/>: an explicit "off" must be writable, and a
        /// project.json re-save must not be able to reset it.
        /// </summary>
        /// <returns>True if a row was updated, false if the project was not found.</returns>
        public bool SetQuietStart(string projectId, bool quietStart)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return false;

            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "UPDATE projects SET quiet_start = @quietStart, updated_at = @updatedAt WHERE id = @id",
                    _connection);
                command.Parameters.AddWithValue("@quietStart", quietStart ? 1 : 0);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@id", projectId);

                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Sets a project's Claude Code config profile by NAME (ticket c8ee0eec, GitHub #21), or clears
        /// it: null, blank or "Default" stores NULL, which reads as the implicit Default profile. This
        /// and <see cref="UpdateProjectField"/> are the only writers for an existing row; the
        /// SaveRichProject UPSERT deliberately leaves the column alone on update.
        /// </summary>
        /// <returns>True if a row was updated, false if the project was not found.</returns>
        public bool SetClaudeProfile(string projectId, string profileName)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return false;

            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "UPDATE projects SET claude_profile = @claudeProfile, updated_at = @updatedAt WHERE id = @id",
                    _connection);
                command.Parameters.AddWithValue("@claudeProfile",
                    (object)NormalizeClaudeProfileName(profileName) ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@id", projectId);

                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// The stored form of a profile name. A forwarder: the rule itself is
        /// <see cref="MultiTerminal.Models.ClaudeProfile.NormalizeName"/>.
        /// </summary>
        internal static string NormalizeClaudeProfileName(string profileName) =>
            MultiTerminal.Models.ClaudeProfile.NormalizeName(profileName);

        /// <summary>One project's watch state for one repo (task 92eeeb47).</summary>
        public sealed class GitHubWatchStateRow
        {
            /// <summary>Newest number accounted for; null = never checked for this project.</summary>
            public int? Baseline { get; set; }

            /// <summary>Last attempt, successful or not.</summary>
            public DateTime? LastCheckedUtc { get; set; }

            /// <summary>Last check that worked; the interval is counted from this (pipeline Run 1).</summary>
            public DateTime? LastSuccessUtc { get; set; }

            /// <summary>The last attempt's error; null when it worked.</summary>
            public string LastError { get; set; }
        }

        /// <summary>
        /// The GitHub watch state for a project's repo (task 92eeeb47). It survives a restart, so a
        /// daily check is not reset by one.
        /// </summary>
        public GitHubWatchStateRow GetGitHubWatchState(string projectId, string repo)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "SELECT baseline_number, last_checked_at, last_success_at, last_error FROM github_watch_state WHERE project_id = @p AND repo = @r",
                    _connection);
                command.Parameters.AddWithValue("@p", projectId);
                command.Parameters.AddWithValue("@r", repo);
                using var reader = command.ExecuteReader();
                var row = new GitHubWatchStateRow();
                if (!reader.Read()) return row;
                row.Baseline = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
                // ToUniversalTime, not SpecifyKind: System.Data.SQLite hands the stored UTC value back
                // as LOCAL wall time (TaskDatabase reads its DATETIMEs the same way; pipeline Run 1).
                row.LastCheckedUtc = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1).ToUniversalTime();
                row.LastSuccessUtc = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2).ToUniversalTime();
                row.LastError = reader.IsDBNull(3) ? null : reader.GetString(3);
                return row;
            }
        }

        /// <summary>
        /// Forgets a project's watch baselines (task 92eeeb47, pipeline Run 1), so the next check is a
        /// baseline-only check again. Called when watching is turned back on or the repository URL
        /// changes: a baseline left from before covers a gap nobody watched, and would turn everything
        /// created in it into cards. Claims are kept, so an item already reported is never re-reported.
        /// </summary>
        public void ResetGitHubWatchState(string projectId)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand("DELETE FROM github_watch_state WHERE project_id = @p", _connection);
                command.Parameters.AddWithValue("@p", projectId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Releases a claim whose card could not be made, so the next check offers the item again
        /// (pipeline Run 1). Only an unlinked claim is released.
        /// </summary>
        public void ReleaseGitHubItemClaim(string projectId, string repo, int number)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "DELETE FROM github_watch_seen WHERE project_id = @p AND repo = @r AND number = @n AND task_id IS NULL", _connection);
                command.Parameters.AddWithValue("@p", projectId);
                command.Parameters.AddWithValue("@r", repo);
                command.Parameters.AddWithValue("@n", number);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Records a check. <paramref name="baseline"/> is the newest number now accounted for, or
        /// null when this check learned nothing (it failed): a null never creates or replaces one, so
        /// a failed FIRST check leaves the repo un-baselined and the next good check is still a
        /// baseline-only check (pipeline Run 1). A baseline only ever rises. The outcome (null error =
        /// it worked) is kept for the form; <c>last_success_at</c> moves only on success, or when
        /// <paramref name="fetched"/> says GitHub answered and only a card failed: the interval then
        /// keeps its pace and the card is tried at the next check, instead of every retry delay
        /// (pipeline Run 2).
        /// </summary>
        public void RecordGitHubWatchCheck(string projectId, string repo, int? baseline, string error, bool fetched = false)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(@"
                    INSERT INTO github_watch_state (project_id, repo, baseline_number, last_checked_at, last_success_at, last_error)
                    VALUES (@p, @r, @b, @at, CASE WHEN @err IS NULL OR @fetched = 1 THEN @at END, @err)
                    ON CONFLICT(project_id, repo) DO UPDATE SET
                        baseline_number = CASE
                            WHEN excluded.baseline_number IS NULL THEN github_watch_state.baseline_number
                            WHEN github_watch_state.baseline_number IS NULL THEN excluded.baseline_number
                            ELSE MAX(github_watch_state.baseline_number, excluded.baseline_number) END,
                        last_checked_at = excluded.last_checked_at,
                        last_success_at = COALESCE(excluded.last_success_at, github_watch_state.last_success_at),
                        last_error = excluded.last_error", _connection);
                command.Parameters.AddWithValue("@p", projectId);
                command.Parameters.AddWithValue("@r", repo);
                command.Parameters.AddWithValue("@b", baseline.HasValue ? (object)baseline.Value : DBNull.Value);
                command.Parameters.AddWithValue("@at", DateTime.UtcNow);
                command.Parameters.AddWithValue("@err", (object)error ?? DBNull.Value);
                command.Parameters.AddWithValue("@fetched", fetched ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Claims an issue/PR for reporting: true the first time, false if it was claimed before. The
        /// one place that decides "new", so overlapping checks cannot both report one item.
        /// </summary>
        public bool TryClaimGitHubItem(string projectId, string repo, int number, bool isPullRequest)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(@"
                    INSERT OR IGNORE INTO github_watch_seen (project_id, repo, number, is_pr, seen_at)
                    VALUES (@p, @r, @n, @pr, @at)", _connection);
                command.Parameters.AddWithValue("@p", projectId);
                command.Parameters.AddWithValue("@r", repo);
                command.Parameters.AddWithValue("@n", number);
                command.Parameters.AddWithValue("@pr", isPullRequest ? 1 : 0);
                command.Parameters.AddWithValue("@at", DateTime.UtcNow);
                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>Links a claimed issue/PR to the board card made for it.</summary>
        public void SetGitHubItemTask(string projectId, string repo, int number, string taskId)
        {
            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "UPDATE github_watch_seen SET task_id = @t WHERE project_id = @p AND repo = @r AND number = @n", _connection);
                command.Parameters.AddWithValue("@t", taskId);
                command.Parameters.AddWithValue("@p", projectId);
                command.Parameters.AddWithValue("@r", repo);
                command.Parameters.AddWithValue("@n", number);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Sets a project's launch mode (task ec2ed636): ask / new / continue. The only writer of
        /// <c>launch_mode</c>; the SaveRichProject UPSERT does not name the column, so a
        /// project.json re-save can never reset it.
        /// <para>Ask is written as the string "ask", not NULL. Both read as Ask today, but an
        /// explicit "ask" records that the user CHOSE it, so a future change of the default for
        /// unset (NULL) projects cannot silently override that choice.</para>
        /// </summary>
        /// <returns>True if a row was updated, false if the project was not found.</returns>
        public bool SetLaunchMode(string projectId, MultiTerminal.Models.ProjectLaunchMode mode)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return false;

            lock (_dbLock)
            {
                using var command = new SQLiteCommand(
                    "UPDATE projects SET launch_mode = @launchMode, updated_at = @updatedAt WHERE id = @id",
                    _connection);
                command.Parameters.AddWithValue("@launchMode", MultiTerminal.Models.Project.LaunchModeToStorage(mode));
                command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
                command.Parameters.AddWithValue("@id", projectId);

                return command.ExecuteNonQuery() > 0;
            }
        }

        private MultiTerminal.Models.Project ReadRichProject(SQLiteDataReader reader)
        {
            return new MultiTerminal.Models.Project
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Path = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetDateTime(5),
                UpdatedAt = reader.GetDateTime(6),
                SourcePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                DeployPath = reader.IsDBNull(8) ? null : reader.GetString(8),
                BuildOutputPath = reader.IsDBNull(9) ? null : reader.GetString(9),
                BuildCommand = reader.IsDBNull(10) ? null : reader.GetString(10),
                DeployCommand = reader.IsDBNull(11) ? null : reader.GetString(11),
                LaunchCommand = reader.IsDBNull(12) ? null : reader.GetString(12),
                ProjectType = reader.IsDBNull(13) ? null : reader.GetString(13),
                CurrentVersion = reader.IsDBNull(14) ? "0.1.0" : reader.GetString(14),
                ChangeLog = reader.IsDBNull(15) ? null : reader.GetString(15),
                IsPinned = !reader.IsDBNull(16) && reader.GetInt32(16) == 1,
                Icon = reader.IsDBNull(17) ? null : reader.GetString(17),
                IconColor = reader.IsDBNull(18) ? null : reader.GetString(18),
                LastOpenedAt = reader.IsDBNull(19) ? default : reader.GetDateTime(19),
                GitRepoUrl = reader.IsDBNull(20) ? null : reader.GetString(20),
                GitDefaultBranch = reader.IsDBNull(21) ? null : reader.GetString(21),
                GitAutoCommit = !reader.IsDBNull(22) && reader.GetInt32(22) == 1,
                TeamLead = reader.IsDBNull(23) ? null : reader.GetString(23),
                DefaultTerminal = MultiTerminal.Models.TerminalKindHelper.Normalize(
                    reader.IsDBNull(24) ? null : reader.GetString(24)),
                SourceControlAccountId = reader.IsDBNull(25) ? null : reader.GetString(25),
                Status = MultiTerminal.Models.Project.NormalizeStatus(
                    reader.IsDBNull(26) ? null : reader.GetString(26)),
                QuietStart = reader.IsDBNull(27) ? (bool?)null : reader.GetInt32(27) == 1,
                ClaudeProfile = reader.IsDBNull(28) ? null : reader.GetString(28),
                LaunchMode = MultiTerminal.Models.Project.ParseLaunchMode(
                    reader.IsDBNull(29) ? null : reader.GetString(29)),
                GitHubWatchMode = MultiTerminal.Models.GitHubWatchSettings.ParseMode(
                    reader.IsDBNull(30) ? null : reader.GetString(30)),
                GitHubCheckOnStart = !reader.IsDBNull(31) && reader.GetInt32(31) == 1,
                GitHubCheckInterval = MultiTerminal.Models.GitHubWatchSettings.ParseInterval(
                    reader.IsDBNull(32) ? null : reader.GetString(32))
            };
        }

        /// <summary>
        /// Get all team member profiles that have is_team_lead = 1.
        /// Returns a list of lightweight profile summaries (id, displayName, avatarUrl)
        /// for populating a team lead dropdown.
        /// ProjectDatabase shares the same multiterminal.db file as TaskDatabase, so it can
        /// query team_member_profiles directly.
        /// </summary>
        public List<(string Id, string DisplayName, string AvatarUrl)> GetTeamLeadProfiles()
        {
            var result = new List<(string Id, string DisplayName, string AvatarUrl)>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, display_name, avatar_url
                    FROM team_member_profiles
                    WHERE is_team_lead = 1
                    ORDER BY COALESCE(display_name, id) ASC
                ";

                using var command = new SQLiteCommand(sql, _connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    string displayName = reader.IsDBNull(1) ? id : reader.GetString(1);
                    string avatarUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                    result.Add((id, displayName, avatarUrl));
                }
            }

            return result;
        }

        /// <summary>
        /// Get all team member profiles as lightweight summaries for picker UIs.
        /// Returns (id, displayName, role, preferredModel) for each profile.
        /// </summary>
        public List<(string Id, string DisplayName, string Role, string PreferredModel)> GetAllProfileSummaries()
        {
            var result = new List<(string Id, string DisplayName, string Role, string PreferredModel)>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, display_name, role, preferred_model
                    FROM team_member_profiles
                    ORDER BY COALESCE(display_name, id) ASC
                ";

                using var command = new SQLiteCommand(sql, _connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    string displayName = reader.IsDBNull(1) ? id : reader.GetString(1);
                    string role = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string preferredModel = reader.IsDBNull(3) ? null : reader.GetString(3);
                    result.Add((id, displayName, role, preferredModel));
                }
            }

            return result;
        }

        #endregion

        #region Project Agents

        /// <summary>
        /// Get all agents for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectAgent> GetProjectAgents(string projectId)
        {
            var agents = new List<MultiTerminal.Models.ProjectAgent>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, agent_name, role, preferred_model
                    FROM project_agents
                    WHERE project_id = @projectId
                    ORDER BY agent_name
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    agents.Add(new MultiTerminal.Models.ProjectAgent
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        AgentName = reader.GetString(2),
                        Role = reader.IsDBNull(3) ? null : reader.GetString(3),
                        PreferredModel = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            return agents;
        }

        /// <summary>
        /// Save (upsert) a project agent. Updates role and preferred_model if already present.
        /// </summary>
        public void SaveProjectAgent(MultiTerminal.Models.ProjectAgent agent)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO project_agents (project_id, agent_name, role, preferred_model)
                    VALUES (@projectId, @agentName, @role, @preferredModel)
                    ON CONFLICT(project_id, agent_name) DO UPDATE SET
                        role = @role,
                        preferred_model = @preferredModel
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", agent.ProjectId);
                command.Parameters.AddWithValue("@agentName", agent.AgentName);
                command.Parameters.AddWithValue("@role", (object)agent.Role ?? DBNull.Value);
                command.Parameters.AddWithValue("@preferredModel", (object)agent.PreferredModel ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Delete an agent from a project.
        /// </summary>
        public bool DeleteProjectAgent(string projectId, string agentName)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_agents WHERE project_id = @projectId AND agent_name = @agentName";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                command.Parameters.AddWithValue("@agentName", agentName);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        #region Project MCP Servers

        /// <summary>
        /// Get all MCP servers for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectMcpServer> GetProjectMcpServers(string projectId)
        {
            var servers = new List<MultiTerminal.Models.ProjectMcpServer>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, server_name, is_enabled
                    FROM project_mcp_servers
                    WHERE project_id = @projectId
                    ORDER BY server_name
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    servers.Add(new MultiTerminal.Models.ProjectMcpServer
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        ServerName = reader.GetString(2),
                        IsEnabled = reader.GetInt32(3) == 1
                    });
                }
            }

            return servers;
        }

        /// <summary>
        /// Save (upsert) an MCP server for a project.
        /// </summary>
        public void SaveProjectMcpServer(MultiTerminal.Models.ProjectMcpServer server)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO project_mcp_servers (project_id, server_name, is_enabled)
                    VALUES (@projectId, @serverName, @isEnabled)
                    ON CONFLICT(project_id, server_name) DO UPDATE SET
                        is_enabled = @isEnabled
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", server.ProjectId);
                command.Parameters.AddWithValue("@serverName", server.ServerName);
                command.Parameters.AddWithValue("@isEnabled", server.IsEnabled ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Update only the is_enabled flag on an existing MCP server row by its id.
        /// </summary>
        public bool UpdateMcpServerEnabled(int id, bool isEnabled)
        {
            lock (_dbLock)
            {
                const string sql = "UPDATE project_mcp_servers SET is_enabled = @isEnabled WHERE id = @id";
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Delete an MCP server from a project.
        /// </summary>
        public bool DeleteProjectMcpServer(string projectId, string serverName)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_mcp_servers WHERE project_id = @projectId AND server_name = @serverName";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                command.Parameters.AddWithValue("@serverName", serverName);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        #region Project Specialist Agents

        /// <summary>
        /// Get all specialist agents for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectSpecialistAgent> GetProjectSpecialistAgents(string projectId)
        {
            var specialists = new List<MultiTerminal.Models.ProjectSpecialistAgent>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, agent_type, is_enabled, custom_prompt
                    FROM project_specialist_agents
                    WHERE project_id = @projectId
                    ORDER BY agent_type
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    specialists.Add(new MultiTerminal.Models.ProjectSpecialistAgent
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        AgentType = reader.GetString(2),
                        IsEnabled = reader.GetInt32(3) == 1,
                        CustomPrompt = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            return specialists;
        }

        /// <summary>
        /// Save (upsert) a specialist agent for a project.
        /// </summary>
        public void SaveProjectSpecialistAgent(MultiTerminal.Models.ProjectSpecialistAgent specialist)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO project_specialist_agents (project_id, agent_type, is_enabled, custom_prompt)
                    VALUES (@projectId, @agentType, @isEnabled, @customPrompt)
                    ON CONFLICT(project_id, agent_type) DO UPDATE SET
                        is_enabled = @isEnabled,
                        custom_prompt = @customPrompt
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", specialist.ProjectId);
                command.Parameters.AddWithValue("@agentType", specialist.AgentType);
                command.Parameters.AddWithValue("@isEnabled", specialist.IsEnabled ? 1 : 0);
                command.Parameters.AddWithValue("@customPrompt", (object)specialist.CustomPrompt ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Update only the is_enabled flag on an existing specialist agent row by its id.
        /// </summary>
        public bool UpdateSpecialistAgentEnabled(int id, bool isEnabled)
        {
            lock (_dbLock)
            {
                const string sql = "UPDATE project_specialist_agents SET is_enabled = @isEnabled WHERE id = @id";
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Delete a specialist agent from a project.
        /// </summary>
        public bool DeleteProjectSpecialistAgent(string projectId, string agentType)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_specialist_agents WHERE project_id = @projectId AND agent_type = @agentType";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                command.Parameters.AddWithValue("@agentType", agentType);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        #region Project Paths

        /// <summary>
        /// Get all paths for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectPath> GetProjectPaths(string projectId)
        {
            var paths = new List<MultiTerminal.Models.ProjectPath>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, path_type, path_value, description
                    FROM project_paths
                    WHERE project_id = @projectId
                    ORDER BY path_type
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    paths.Add(new MultiTerminal.Models.ProjectPath
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        PathType = reader.GetString(2),
                        PathValue = reader.GetString(3),
                        Description = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            return paths;
        }

        /// <summary>
        /// Save a project path (insert or update by id).
        /// </summary>
        public int SaveProjectPath(MultiTerminal.Models.ProjectPath path)
        {
            lock (_dbLock)
            {
                if (path.Id > 0)
                {
                    const string updateSql = @"
                        UPDATE project_paths
                        SET path_type = @pathType, path_value = @pathValue, description = @description
                        WHERE id = @id AND project_id = @projectId
                    ";
                    using var updateCommand = new SQLiteCommand(updateSql, _connection);
                    updateCommand.Parameters.AddWithValue("@id", path.Id);
                    updateCommand.Parameters.AddWithValue("@projectId", path.ProjectId);
                    updateCommand.Parameters.AddWithValue("@pathType", path.PathType);
                    updateCommand.Parameters.AddWithValue("@pathValue", path.PathValue);
                    updateCommand.Parameters.AddWithValue("@description", (object)path.Description ?? DBNull.Value);
                    updateCommand.ExecuteNonQuery();
                    return path.Id;
                }
                else
                {
                    const string insertSql = @"
                        INSERT INTO project_paths (project_id, path_type, path_value, description)
                        VALUES (@projectId, @pathType, @pathValue, @description);
                        SELECT last_insert_rowid();
                    ";
                    using var insertCommand = new SQLiteCommand(insertSql, _connection);
                    insertCommand.Parameters.AddWithValue("@projectId", path.ProjectId);
                    insertCommand.Parameters.AddWithValue("@pathType", path.PathType);
                    insertCommand.Parameters.AddWithValue("@pathValue", path.PathValue);
                    insertCommand.Parameters.AddWithValue("@description", (object)path.Description ?? DBNull.Value);
                    return Convert.ToInt32(insertCommand.ExecuteScalar());
                }
            }
        }

        /// <summary>
        /// Delete a project path by id.
        /// </summary>
        public bool DeleteProjectPath(int pathId)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_paths WHERE id = @id";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", pathId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        #region Project Prompts

        /// <summary>
        /// Get all prompts for a project, ordered by display_order.
        /// </summary>
        public List<MultiTerminal.Models.ProjectPromptEntry> GetProjectPrompts(string projectId)
        {
            var prompts = new List<MultiTerminal.Models.ProjectPromptEntry>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, prompt_type, prompt_text, display_order
                    FROM project_prompts
                    WHERE project_id = @projectId
                    ORDER BY display_order, id
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    prompts.Add(new MultiTerminal.Models.ProjectPromptEntry
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        PromptType = reader.GetString(2),
                        PromptText = reader.GetString(3),
                        DisplayOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
                    });
                }
            }

            return prompts;
        }

        /// <summary>
        /// Save a project prompt (insert or update by id).
        /// </summary>
        public int SaveProjectPrompt(MultiTerminal.Models.ProjectPromptEntry prompt)
        {
            lock (_dbLock)
            {
                if (prompt.Id > 0)
                {
                    const string updateSql = @"
                        UPDATE project_prompts
                        SET prompt_type = @promptType, prompt_text = @promptText, display_order = @displayOrder
                        WHERE id = @id AND project_id = @projectId
                    ";
                    using var updateCommand = new SQLiteCommand(updateSql, _connection);
                    updateCommand.Parameters.AddWithValue("@id", prompt.Id);
                    updateCommand.Parameters.AddWithValue("@projectId", prompt.ProjectId);
                    updateCommand.Parameters.AddWithValue("@promptType", prompt.PromptType);
                    updateCommand.Parameters.AddWithValue("@promptText", prompt.PromptText);
                    updateCommand.Parameters.AddWithValue("@displayOrder", prompt.DisplayOrder);
                    updateCommand.ExecuteNonQuery();
                    return prompt.Id;
                }
                else
                {
                    const string insertSql = @"
                        INSERT INTO project_prompts (project_id, prompt_type, prompt_text, display_order)
                        VALUES (@projectId, @promptType, @promptText, @displayOrder);
                        SELECT last_insert_rowid();
                    ";
                    using var insertCommand = new SQLiteCommand(insertSql, _connection);
                    insertCommand.Parameters.AddWithValue("@projectId", prompt.ProjectId);
                    insertCommand.Parameters.AddWithValue("@promptType", prompt.PromptType);
                    insertCommand.Parameters.AddWithValue("@promptText", prompt.PromptText);
                    insertCommand.Parameters.AddWithValue("@displayOrder", prompt.DisplayOrder);
                    return Convert.ToInt32(insertCommand.ExecuteScalar());
                }
            }
        }

        /// <summary>
        /// Delete a project prompt by id.
        /// </summary>
        public bool DeleteProjectPrompt(int promptId)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_prompts WHERE id = @id";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", promptId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        #region MCP Registry (removed — gateway is source of truth)

        /// <summary>
        /// Migration: drops the mcp_registry table if it still exists from a previous version.
        /// The gateway database (gateway.db) is now the authoritative server catalog.
        /// project_mcp_servers (per-project enablement preferences) is kept intact.
        /// </summary>
        private void MigrateDropMcpRegistry()
        {
            // Drop indexes first (SQLite auto-drops them with the table, but be explicit)
            using (var cmd = new SQLiteCommand("DROP INDEX IF EXISTS idx_mcp_registry_server_name", _connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand("DROP INDEX IF EXISTS idx_mcp_registry_tier", _connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand("DROP TABLE IF EXISTS mcp_registry", _connection))
                cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Get the set of server_names explicitly enabled in project_mcp_servers for a project.
        /// Used by GatewayIntegrationService.SyncProjectProfile to determine which gateway servers
        /// to include in the project's gateway profile.
        /// </summary>
        public HashSet<string> GetEnabledMcpServerNamesForProject(string projectId)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT server_name FROM project_mcp_servers
                    WHERE project_id = @projectId AND is_enabled = 1
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    result.Add(reader.GetString(0));
            }

            return result;
        }

        #endregion

        #region Project Skills

        /// <summary>
        /// Get all skills for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectSkill> GetProjectSkills(string projectId)
        {
            var skills = new List<MultiTerminal.Models.ProjectSkill>();

            lock (_dbLock)
            {
                const string sql = @"
                    SELECT id, project_id, skill_name, is_enabled
                    FROM project_skills
                    WHERE project_id = @projectId
                    ORDER BY skill_name
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    skills.Add(new MultiTerminal.Models.ProjectSkill
                    {
                        Id = reader.GetInt32(0),
                        ProjectId = reader.GetString(1),
                        SkillName = reader.GetString(2),
                        IsEnabled = reader.GetInt32(3) == 1
                    });
                }
            }

            return skills;
        }

        /// <summary>
        /// Save (upsert) a skill for a project.
        /// </summary>
        public void SaveProjectSkill(MultiTerminal.Models.ProjectSkill skill)
        {
            lock (_dbLock)
            {
                const string sql = @"
                    INSERT INTO project_skills (project_id, skill_name, is_enabled)
                    VALUES (@projectId, @skillName, @isEnabled)
                    ON CONFLICT(project_id, skill_name) DO UPDATE SET
                        is_enabled = @isEnabled
                ";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", skill.ProjectId);
                command.Parameters.AddWithValue("@skillName", skill.SkillName);
                command.Parameters.AddWithValue("@isEnabled", skill.IsEnabled ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Update only the is_enabled flag on an existing skill row by its id.
        /// </summary>
        public bool UpdateSkillEnabled(int id, bool isEnabled)
        {
            lock (_dbLock)
            {
                const string sql = "UPDATE project_skills SET is_enabled = @isEnabled WHERE id = @id";
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Delete a skill from a project.
        /// </summary>
        public bool DeleteProjectSkill(string projectId, string skillName)
        {
            lock (_dbLock)
            {
                const string sql = "DELETE FROM project_skills WHERE project_id = @projectId AND skill_name = @skillName";

                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@projectId", projectId);
                command.Parameters.AddWithValue("@skillName", skillName);
                return command.ExecuteNonQuery() > 0;
            }
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            _isDisposed = true;
        }
    }
}
