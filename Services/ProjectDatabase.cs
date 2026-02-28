using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting Projects.
    /// Uses the same database as TaskDatabase at %APPDATA%\multiterminal\tasks.db
    /// </summary>
    public class ProjectDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

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

        #region Project Operations

        /// <summary>
        /// Get a project by ID.
        /// </summary>
        public Project GetProject(string projectId)
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
        /// Get all projects.
        /// </summary>
        public List<Project> GetAllProjects()
        {
            var projects = new List<Project>();

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
        /// Save a project (insert or update).
        /// </summary>
        public void SaveProject(Project project)
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

        private Project ReadProject(SQLiteDataReader reader)
        {
            return new Project
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

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _isDisposed = true;
            }
        }
    }
}
