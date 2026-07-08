using System;
using System.Collections.Generic;
using MultiTerminal.Services;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a development project with associated metadata and prompts.
    /// Stored in .claude/project.json within the project folder for portability,
    /// and persisted to SQLite via ProjectDatabase.
    /// </summary>
    public class Project
    {
        /// <summary>
        /// Unique identifier (GUID).
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Human-readable project name (independent of folder name).
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Absolute path to the project folder (legacy field, kept for backward compatibility).
        /// Use SourcePath as the canonical field for new code.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Canonical source/working directory path for the project.
        /// Populated from Path during migration if not already set.
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// Path to the deployment output folder (e.g. Deploy\).
        /// </summary>
        public string DeployPath { get; set; }

        /// <summary>
        /// Path to build output directory (e.g. bin\Release\).
        /// </summary>
        public string BuildOutputPath { get; set; }

        /// <summary>
        /// Shell command used to build the project (e.g. "dotnet build").
        /// </summary>
        public string BuildCommand { get; set; }

        /// <summary>
        /// Shell command used to deploy the project.
        /// </summary>
        public string DeployCommand { get; set; }

        /// <summary>
        /// Shell command used to launch the project.
        /// </summary>
        public string LaunchCommand { get; set; }

        /// <summary>
        /// Project technology type (e.g. "dotnet", "node", "python", "clarion").
        /// </summary>
        public string ProjectType { get; set; }

        /// <summary>
        /// Project description/purpose.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Change history notes (user-maintained changelog).
        /// </summary>
        public string ChangeLog { get; set; }

        /// <summary>
        /// Current semantic version of the project (e.g., "1.2.3").
        /// Automatically updated by ChangelogService when tasks are completed.
        /// </summary>
        public string CurrentVersion { get; set; } = "0.1.0";

        /// <summary>
        /// Whether this project is pinned/favorite.
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>
        /// Lifecycle status of the project. One of <see cref="StatusValues"/>:
        /// "Active", "Parked", or "Archived". Drives the Home-page status filter
        /// (Archived hidden by default) and the card status badge.
        /// <para>Deliberately NULL-by-default (NOT "Active"): the SaveRichProject UPSERT
        /// COALESCEs this column, so callers that re-save from a project.json (which lacks
        /// this SQLite-only field) leave it null and MUST NOT clobber an existing
        /// Parked/Archived value — exactly the team_lead precedent. Readers and the DTO
        /// coerce null → "Active" via <see cref="NormalizeStatus"/>, so a null never
        /// surfaces to the UI.</para>
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Icon identifier for the project (e.g. a Material icon name or emoji).
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Hex or named color for the project icon (e.g. "#4A90D9").
        /// </summary>
        public string IconColor { get; set; }

        /// <summary>
        /// When the project was first registered.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the project record was last updated in the database.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// When the project was last opened in MultiTerminal.
        /// </summary>
        public DateTime LastOpenedAt { get; set; }

        /// <summary>
        /// Who created/registered the project.
        /// </summary>
        public string CreatedBy { get; set; }

        /// <summary>
        /// The profile display_name of the designated team lead for this project.
        /// When set, launching from the start screen uses this as the terminal name with isTeamLead=true.
        /// </summary>
        public string TeamLead { get; set; }

        /// <summary>
        /// Storage value for the project's default terminal CLI.
        /// Valid values: <see cref="TerminalKindHelper.ClaudeCodeValue"/> (default) or
        /// <see cref="TerminalKindHelper.CodexValue"/>. Null / empty / unknown values
        /// are treated as ClaudeCode by <see cref="TerminalKindHelper.ParseOrDefault"/>.
        /// Consumed by the project-card split button and the Project settings form.
        /// </summary>
        public string DefaultTerminal { get; set; } = TerminalKindHelper.ClaudeCodeValue;

        // ── Git configuration ─────────────────────────────────────────────────

        /// <summary>
        /// Remote repository URL (e.g. https://github.com/user/repo).
        /// </summary>
        public string GitRepoUrl { get; set; }

        /// <summary>
        /// Default branch name (e.g. "main", "master").
        /// </summary>
        public string GitDefaultBranch { get; set; }

        /// <summary>
        /// When true, the project management workflow auto-commits after milestones.
        /// </summary>
        public bool GitAutoCommit { get; set; }

        /// <summary>
        /// Id of the assigned source control account (source_control_accounts.id) used to
        /// resolve credentials + identity for push/publish operations. Null = no account assigned.
        /// </summary>
        public string SourceControlAccountId { get; set; }

        // ── Legacy collections (loaded from .claude/project.json) ─────────────

        /// <summary>
        /// Project-specific prompts (stored in .claude/project.json).
        /// Not persisted directly in projects table — use project_prompts table via ProjectDatabase.
        /// </summary>
        public List<Prompt> Prompts { get; set; } = new List<Prompt>();

        /// <summary>
        /// Agent names from the team.agents array in project.json.
        /// Used for team assembly workflow (spawning native Team Agents with profile data).
        /// Not persisted directly in projects table — use project_agents table via ProjectDatabase.
        /// </summary>
        public List<string> TeamAgents { get; set; } = new List<string>();

        /// <summary>
        /// The canonical lifecycle status values, in display order.
        /// </summary>
        public static readonly string[] StatusValues = { "Active", "Parked", "Archived" };

        /// <summary>
        /// Coerces any stored/incoming status string to a canonical value
        /// (case-insensitive). Null/blank/unrecognized values map to "Active" so a
        /// bad DB value or a legacy row can never break the filter or badge.
        /// </summary>
        public static string NormalizeStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Active";
            switch (status.Trim().ToLowerInvariant())
            {
                case "parked": return "Parked";
                case "archived": return "Archived";
                default: return "Active";
            }
        }

        /// <summary>
        /// Creates a new Project with a generated GUID and current timestamp.
        /// </summary>
        public static Project Create(string name, string path)
        {
            return new Project
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Path = path,
                SourcePath = path,
                CreatedAt = DateTime.Now,
                LastOpenedAt = DateTime.Now,
                Prompts = new List<Prompt>()
            };
        }
    }
}
