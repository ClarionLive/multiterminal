using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents an agent assigned to a project.
    /// </summary>
    public class ProjectAgent
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        public string AgentName { get; set; }
        public string Role { get; set; }
        public string PreferredModel { get; set; }

        /// <summary>
        /// Extra Claude Code flags for this agent's terminals only, added after the project's
        /// (<c>project_agents.extra_args</c>, ticket 5380b6fb); null for none. Decided by
        /// <see cref="Services.ClaudeExtraArgsPolicy"/>, which quotes it for the command line.
        /// </summary>
        public string ExtraArgs { get; set; }
    }

    /// <summary>
    /// Represents an MCP server configured for a project.
    /// </summary>
    public class ProjectMcpServer
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        public string ServerName { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Represents a specialist agent (devils-advocate, verifier, etc.) configured for a project.
    /// </summary>
    public class ProjectSpecialistAgent
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        /// <summary>
        /// Type identifier, e.g. "devils-advocate", "verifier", "security-auditor".
        /// </summary>
        public string AgentType { get; set; }
        public bool IsEnabled { get; set; } = true;
        /// <summary>
        /// Optional override prompt for this specialist agent in this project context.
        /// </summary>
        public string CustomPrompt { get; set; }
    }

    /// <summary>
    /// Represents an important filesystem path associated with a project.
    /// </summary>
    public class ProjectPath
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        /// <summary>
        /// Category label, e.g. "source", "deploy", "build_output", "docs".
        /// </summary>
        public string PathType { get; set; }
        public string PathValue { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Represents a stored prompt/instruction entry for a project.
    /// </summary>
    public class ProjectPromptEntry
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        /// <summary>
        /// Category label, e.g. "system", "user", "context", "workflow".
        /// </summary>
        public string PromptType { get; set; }
        public string PromptText { get; set; }
        public int DisplayOrder { get; set; }
    }

    /// <summary>
    /// Represents a skill enabled for a project.
    /// </summary>
    public class ProjectSkill
    {
        public int Id { get; set; }
        public string ProjectId { get; set; }
        public string SkillName { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

}
