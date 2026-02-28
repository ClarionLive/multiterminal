using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Builds a complete ProjectContext object by joining all project-related tables.
    /// Designed to give agents a single "everything you need" object in one call.
    /// </summary>
    public class ProjectContextService
    {
        private readonly ProjectDatabase _projectDb;

        public ProjectContextService(ProjectDatabase projectDb)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
        }

        /// <summary>
        /// Builds a full context object for the given project id by joining all
        /// association tables (agents, mcp servers, specialist agents, paths, prompts, skills).
        /// Returns null if the project does not exist.
        /// </summary>
        public ProjectContext GetProjectContext(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return null;

            var project = _projectDb.GetRichProject(projectId);
            if (project == null)
                return null;

            return new ProjectContext
            {
                Project = project,
                Agents = _projectDb.GetProjectAgents(projectId),
                McpServers = _projectDb.GetProjectMcpServers(projectId),
                SpecialistAgents = _projectDb.GetProjectSpecialistAgents(projectId),
                Paths = _projectDb.GetProjectPaths(projectId),
                Prompts = _projectDb.GetProjectPrompts(projectId),
                Skills = _projectDb.GetProjectSkills(projectId)
            };
        }

        /// <summary>
        /// Returns context objects for all projects.
        /// Use sparingly — prefer GetProjectContext for a single project.
        /// </summary>
        public List<ProjectContext> GetAllProjectContexts()
        {
            var projects = _projectDb.GetAllRichProjects();
            var contexts = new List<ProjectContext>(projects.Count);

            foreach (var project in projects)
            {
                contexts.Add(new ProjectContext
                {
                    Project = project,
                    Agents = _projectDb.GetProjectAgents(project.Id),
                    McpServers = _projectDb.GetProjectMcpServers(project.Id),
                    SpecialistAgents = _projectDb.GetProjectSpecialistAgents(project.Id),
                    Paths = _projectDb.GetProjectPaths(project.Id),
                    Prompts = _projectDb.GetProjectPrompts(project.Id),
                    Skills = _projectDb.GetProjectSkills(project.Id)
                });
            }

            return contexts;
        }
    }

    /// <summary>
    /// Complete project context that joins all related tables.
    /// Intended as a single payload for agent consumption via GET /api/projects/{id}/context.
    /// </summary>
    public class ProjectContext
    {
        /// <summary>Core project record with all scalar fields.</summary>
        public Project Project { get; set; }

        /// <summary>Agents assigned to this project.</summary>
        public List<ProjectAgent> Agents { get; set; } = new List<ProjectAgent>();

        /// <summary>MCP servers configured for this project.</summary>
        public List<ProjectMcpServer> McpServers { get; set; } = new List<ProjectMcpServer>();

        /// <summary>Specialist agents (verifier, devils-advocate, etc.) for this project.</summary>
        public List<ProjectSpecialistAgent> SpecialistAgents { get; set; } = new List<ProjectSpecialistAgent>();

        /// <summary>Named filesystem paths for this project.</summary>
        public List<ProjectPath> Paths { get; set; } = new List<ProjectPath>();

        /// <summary>Stored prompts/instructions for this project.</summary>
        public List<ProjectPromptEntry> Prompts { get; set; } = new List<ProjectPromptEntry>();

        /// <summary>Skills enabled for this project.</summary>
        public List<ProjectSkill> Skills { get; set; } = new List<ProjectSkill>();
    }
}
