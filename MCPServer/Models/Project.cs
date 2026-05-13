using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a project that groups related tasks.
    /// </summary>
    public class Project
    {
        /// <summary>
        /// Unique project identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Project name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Project description.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Absolute path to the project folder (optional).
        /// Links this kanban project to a file-based project with .claude/project.json
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Terminal name that created this project.
        /// </summary>
        public string CreatedBy { get; set; }

        /// <summary>
        /// When the project was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the project was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result of creating a project.
    /// </summary>
    public class CreateProjectResult
    {
        public bool Success { get; set; }
        public string ProjectId { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// The rich app-level Project (with all enhanced columns: TeamLead, DefaultTerminal,
        /// ProjectType, etc.) populated when CreateProject succeeds. Used by callers that
        /// need the full Project object for downstream work (e.g., MainForm terminal launch
        /// reads DefaultTerminal/TeamLead from it). Null when Success=false.
        /// </summary>
        public MultiTerminal.Models.Project CreatedFileProject { get; set; }
    }

    /// <summary>
    /// Result of updating a project.
    /// </summary>
    public class UpdateProjectResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of deleting a project.
    /// </summary>
    public class DeleteProjectResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of getting a project.
    /// </summary>
    public class GetProjectResult
    {
        public bool Success { get; set; }
        public ProjectInfo Project { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of listing projects.
    /// </summary>
    public class ProjectListResult
    {
        public bool Success { get; set; }
        public System.Collections.Generic.List<ProjectInfo> Projects { get; set; }
        public int Count { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Project info for API responses.
    /// </summary>
    public class ProjectInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public int TaskCount { get; set; }
    }
}
