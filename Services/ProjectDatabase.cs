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

        /// <summary>
        /// The open SQLite connection to multiterminal.db. Exposed so callers that need to
        /// read sibling tables in the same file (e.g. source_control_accounts) can reuse it
        /// rather than opening a second connection — mirrors TaskDatabase.Connection.
        /// </summary>
        public SQLiteConnection Connection => _connection;

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
            string folder = Path.GetDirectoryName(_databasePath);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Version = 3,
                JournalMode = SQLiteJournalModeEnum.Wal,
                Pooling = true
            }.ToString();

            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            CreateSchema();

            // Run migrations for schema changes (additive, non-destructive)
            MigrateAddNewProjectColumns();
            MigrateAddAssociationTables();

            // Drop the mcp_registry table — gateway is now the source of truth for server catalog
            MigrateDropMcpRegistry();
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
                ("source_control_account_id", "TEXT")
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
            ";

            using var command = new SQLiteCommand(createSql, _connection);
            command.ExecuteNonQuery();
        }

        #region Project Operations (MCPServer.Models.Project — used by MessageBroker)

        /// <summary>
        /// Get a project by ID. Returns the lightweight MCPServer.Models.Project used by MessageBroker.
        /// </summary>
        public MultiTerminal.MCPServer.Models.Project GetProject(string projectId)
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

        /// <summary>
        /// Get all projects. Returns lightweight MCPServer.Models.Project list used by MessageBroker.
        /// </summary>
        public List<MultiTerminal.MCPServer.Models.Project> GetAllProjects()
        {
            var projects = new List<MultiTerminal.MCPServer.Models.Project>();

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

            return projects;
        }

        /// <summary>
        /// Save a project (insert or update) using the lightweight MCPServer.Models.Project.
        /// Used by MessageBroker.
        /// </summary>
        public void SaveProject(MultiTerminal.MCPServer.Models.Project project)
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
            command.Parameters.AddWithValue("@path", (object)project.Path ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdBy", (object)project.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", project.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Update a project's name and description.
        /// </summary>
        /// <returns>True if a project was updated, false if project not found.</returns>
        public bool UpdateProject(string projectId, string name, string description)
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

        // Whitelist of project columns that are safe to update via UpdateProjectField.
        // Boolean fields are stored as INTEGER 0/1 in SQLite.
        private static readonly HashSet<string> _allowedProjectFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "description", "path", "source_path", "deploy_path", "build_output_path",
            "build_command", "deploy_command", "launch_command", "project_type", "current_version",
            "change_log", "icon", "icon_color", "git_repo_url", "git_default_branch", "git_auto_commit",
            "is_pinned", "status", "created_by", "last_opened_at", "team_lead", "default_terminal",
            "source_control_account_id"
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
        };

        // Fields that map to INTEGER 0/1 booleans in the database.
        private static readonly HashSet<string> _booleanProjectFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "git_auto_commit", "is_pinned"
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
            else
            {
                paramValue = (object)value ?? DBNull.Value;
            }

            // Safe to interpolate fieldName here because it passed the whitelist check above
            string sql = $"UPDATE projects SET {fieldName} = @value, updated_at = @updatedAt WHERE id = @id";

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

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete a project by ID.
        /// </summary>
        public bool DeleteProject(string projectId)
        {
            const string sql = "DELETE FROM projects WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", projectId);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Get task count for a project.
        /// </summary>
        public int GetProjectTaskCount(string projectId)
        {
            const string sql = "SELECT COUNT(*) FROM tasks WHERE project_id = @projectId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);

            var result = command.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : 0;
        }

        /// <summary>
        /// Delete all projects (for testing/reset).
        /// </summary>
        public void DeleteAllProjects()
        {
            const string sql = "DELETE FROM projects";

            using var command = new SQLiteCommand(sql, _connection);
            command.ExecuteNonQuery();
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
            const string sql = @"
                SELECT id, name, description, path, created_by, created_at, updated_at,
                       source_path, deploy_path, build_output_path, build_command, deploy_command,
                       launch_command, project_type, current_version, change_log, is_pinned,
                       icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                       team_lead, default_terminal, source_control_account_id
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

        /// <summary>
        /// Get all projects as rich model records ordered by pinned first, then created_at desc.
        /// </summary>
        public List<MultiTerminal.Models.Project> GetAllRichProjects()
        {
            var projects = new List<MultiTerminal.Models.Project>();

            const string sql = @"
                SELECT id, name, description, path, created_by, created_at, updated_at,
                       source_path, deploy_path, build_output_path, build_command, deploy_command,
                       launch_command, project_type, current_version, change_log, is_pinned,
                       icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                       team_lead, default_terminal, source_control_account_id
                FROM projects
                ORDER BY is_pinned DESC, created_at DESC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                projects.Add(ReadRichProject(reader));
            }

            return projects;
        }

        /// <summary>
        /// Save (upsert) the full rich project record including all new enhanced columns.
        /// Used by ProjectContextService and ProjectJsonMigrationService.
        /// </summary>
        public void SaveRichProject(MultiTerminal.Models.Project project)
        {
            // COALESCE preserves existing non-null values when the incoming project has nulls.
            // This prevents code paths that load from project.json (which lacks SQLite-only
            // fields like team_lead, icon, project_type, etc.) from wiping those values.
            const string sql = @"
                INSERT INTO projects (id, name, description, path, created_by, created_at, updated_at,
                       source_path, deploy_path, build_output_path, build_command, deploy_command,
                       launch_command, project_type, current_version, change_log, is_pinned,
                       icon, icon_color, last_opened_at, git_repo_url, git_default_branch, git_auto_commit,
                       team_lead, default_terminal, source_control_account_id)
                VALUES (@id, @name, @description, @path, @createdBy, @createdAt, @updatedAt,
                       @sourcePath, @deployPath, @buildOutputPath, @buildCommand, @deployCommand,
                       @launchCommand, @projectType, @currentVersion, @changeLog, @isPinned,
                       @icon, @iconColor, @lastOpenedAt, @gitRepoUrl, @gitDefaultBranch, @gitAutoCommit,
                       @teamLead, @defaultTerminal, @sourceControlAccountId)
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
                    source_control_account_id = COALESCE(@sourceControlAccountId, projects.source_control_account_id)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", project.Id);
            command.Parameters.AddWithValue("@name", project.Name);
            command.Parameters.AddWithValue("@description", (object)project.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@path", (object)project.Path ?? DBNull.Value);
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

            command.ExecuteNonQuery();
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
                SourceControlAccountId = reader.IsDBNull(25) ? null : reader.GetString(25)
            };
        }

        /// <summary>
        /// Get all team member profiles that have is_team_lead = 1.
        /// Returns a list of lightweight profile summaries (id, displayName, avatarUrl)
        /// for populating the team lead dropdown in the Project Panel.
        /// ProjectDatabase shares the same multiterminal.db file as TaskDatabase, so it can
        /// query team_member_profiles directly.
        /// </summary>
        public List<(string Id, string DisplayName, string AvatarUrl)> GetTeamLeadProfiles()
        {
            var result = new List<(string Id, string DisplayName, string AvatarUrl)>();

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

            return result;
        }

        /// <summary>
        /// Get all team member profiles as lightweight summaries for picker UIs.
        /// Returns (id, displayName, role, preferredModel) for each profile.
        /// </summary>
        public List<(string Id, string DisplayName, string Role, string PreferredModel)> GetAllProfileSummaries()
        {
            var result = new List<(string Id, string DisplayName, string Role, string PreferredModel)>();

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

            return agents;
        }

        /// <summary>
        /// Save (upsert) a project agent. Updates role and preferred_model if already present.
        /// </summary>
        public void SaveProjectAgent(MultiTerminal.Models.ProjectAgent agent)
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

        /// <summary>
        /// Delete an agent from a project.
        /// </summary>
        public bool DeleteProjectAgent(string projectId, string agentName)
        {
            const string sql = "DELETE FROM project_agents WHERE project_id = @projectId AND agent_name = @agentName";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);
            command.Parameters.AddWithValue("@agentName", agentName);
            return command.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Project MCP Servers

        /// <summary>
        /// Get all MCP servers for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectMcpServer> GetProjectMcpServers(string projectId)
        {
            var servers = new List<MultiTerminal.Models.ProjectMcpServer>();

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

            return servers;
        }

        /// <summary>
        /// Save (upsert) an MCP server for a project.
        /// </summary>
        public void SaveProjectMcpServer(MultiTerminal.Models.ProjectMcpServer server)
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

        /// <summary>
        /// Update only the is_enabled flag on an existing MCP server row by its id.
        /// </summary>
        public bool UpdateMcpServerEnabled(int id, bool isEnabled)
        {
            const string sql = "UPDATE project_mcp_servers SET is_enabled = @isEnabled WHERE id = @id";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete an MCP server from a project.
        /// </summary>
        public bool DeleteProjectMcpServer(string projectId, string serverName)
        {
            const string sql = "DELETE FROM project_mcp_servers WHERE project_id = @projectId AND server_name = @serverName";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);
            command.Parameters.AddWithValue("@serverName", serverName);
            return command.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Project Specialist Agents

        /// <summary>
        /// Get all specialist agents for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectSpecialistAgent> GetProjectSpecialistAgents(string projectId)
        {
            var specialists = new List<MultiTerminal.Models.ProjectSpecialistAgent>();

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

            return specialists;
        }

        /// <summary>
        /// Save (upsert) a specialist agent for a project.
        /// </summary>
        public void SaveProjectSpecialistAgent(MultiTerminal.Models.ProjectSpecialistAgent specialist)
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

        /// <summary>
        /// Update only the is_enabled flag on an existing specialist agent row by its id.
        /// </summary>
        public bool UpdateSpecialistAgentEnabled(int id, bool isEnabled)
        {
            const string sql = "UPDATE project_specialist_agents SET is_enabled = @isEnabled WHERE id = @id";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete a specialist agent from a project.
        /// </summary>
        public bool DeleteProjectSpecialistAgent(string projectId, string agentType)
        {
            const string sql = "DELETE FROM project_specialist_agents WHERE project_id = @projectId AND agent_type = @agentType";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);
            command.Parameters.AddWithValue("@agentType", agentType);
            return command.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Project Paths

        /// <summary>
        /// Get all paths for a project.
        /// </summary>
        public List<MultiTerminal.Models.ProjectPath> GetProjectPaths(string projectId)
        {
            var paths = new List<MultiTerminal.Models.ProjectPath>();

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

            return paths;
        }

        /// <summary>
        /// Save a project path (insert or update by id).
        /// </summary>
        public int SaveProjectPath(MultiTerminal.Models.ProjectPath path)
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

        /// <summary>
        /// Delete a project path by id.
        /// </summary>
        public bool DeleteProjectPath(int pathId)
        {
            const string sql = "DELETE FROM project_paths WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", pathId);
            return command.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Project Prompts

        /// <summary>
        /// Get all prompts for a project, ordered by display_order.
        /// </summary>
        public List<MultiTerminal.Models.ProjectPromptEntry> GetProjectPrompts(string projectId)
        {
            var prompts = new List<MultiTerminal.Models.ProjectPromptEntry>();

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

            return prompts;
        }

        /// <summary>
        /// Save a project prompt (insert or update by id).
        /// </summary>
        public int SaveProjectPrompt(MultiTerminal.Models.ProjectPromptEntry prompt)
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

        /// <summary>
        /// Delete a project prompt by id.
        /// </summary>
        public bool DeleteProjectPrompt(int promptId)
        {
            const string sql = "DELETE FROM project_prompts WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", promptId);
            return command.ExecuteNonQuery() > 0;
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

            const string sql = @"
                SELECT server_name FROM project_mcp_servers
                WHERE project_id = @projectId AND is_enabled = 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
                result.Add(reader.GetString(0));

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

            return skills;
        }

        /// <summary>
        /// Save (upsert) a skill for a project.
        /// </summary>
        public void SaveProjectSkill(MultiTerminal.Models.ProjectSkill skill)
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

        /// <summary>
        /// Update only the is_enabled flag on an existing skill row by its id.
        /// </summary>
        public bool UpdateSkillEnabled(int id, bool isEnabled)
        {
            const string sql = "UPDATE project_skills SET is_enabled = @isEnabled WHERE id = @id";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@isEnabled", isEnabled ? 1 : 0);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete a skill from a project.
        /// </summary>
        public bool DeleteProjectSkill(string projectId, string skillName)
        {
            const string sql = "DELETE FROM project_skills WHERE project_id = @projectId AND skill_name = @skillName";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@projectId", projectId);
            command.Parameters.AddWithValue("@skillName", skillName);
            return command.ExecuteNonQuery() > 0;
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
