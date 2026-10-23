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

        /// <summary>
        /// "Quiet start" (GitHub #34, task e0fa9d90): when true, a terminal opened on this project
        /// only gets into its folder and registers its identity. MT does not type "initializing...",
        /// and the SessionStart hook does not ask for /multiterminal:session-start, so the user gets
        /// an idle prompt instead of the recap and menu.
        /// <para>Claude Code only: a Codex launch always passes its startup prompt, and the setting
        /// does not change that (labelled as such in the UI).</para>
        /// <para>Nullable for the same reason as <see cref="Status"/>: it is a SQLite-only column
        /// that project.json does not carry, and the SaveRichProject UPSERT COALESCEs it, so a
        /// re-save from project.json leaves null and must not reset a project the user turned
        /// quiet. Null reads as OFF (the Owner's default for existing projects); use
        /// <see cref="IsQuietStart"/>. Written deliberately via
        /// ProjectDatabase.SetQuietStart / UpdateProjectField.</para>
        /// </summary>
        public bool? QuietStart { get; set; }

        /// <summary><see cref="QuietStart"/> with null read as off.</summary>
        public bool IsQuietStart => QuietStart == true;

        /// <summary>
        /// What a project launch does about the previous session (task ec2ed636): ask
        /// (new / continue last / pick one), always start new, or always resume the newest.
        /// <para>SQLite-only (<c>projects.launch_mode</c>), NOT carried by project.json, and NOT in
        /// the SaveRichProject UPSERT at all, so a project.json re-save can neither reset nor write
        /// it. Read via <see cref="ParseLaunchMode"/> (NULL / unknown read as
        /// <see cref="ProjectLaunchMode.Ask"/>); written only by ProjectDatabase.SetLaunchMode.</para>
        /// </summary>
        public ProjectLaunchMode LaunchMode { get; set; } = ProjectLaunchMode.Ask;

        /// <summary>
        /// Parses a stored <c>launch_mode</c> value, case-insensitively and ignoring surrounding
        /// whitespace. Null, blank and unrecognized values read as <see cref="ProjectLaunchMode.Ask"/>,
        /// so a bad DB value can never make a launch skip the prompt.
        /// </summary>
        public static ProjectLaunchMode ParseLaunchMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ProjectLaunchMode.Ask;
            switch (value.Trim().ToLowerInvariant())
            {
                case "new": return ProjectLaunchMode.New;
                case "continue": return ProjectLaunchMode.Continue;
                default: return ProjectLaunchMode.Ask;
            }
        }

        /// <summary>The lowercase storage string for a launch mode: "ask", "new" or "continue".</summary>
        public static string LaunchModeToStorage(ProjectLaunchMode mode)
        {
            switch (mode)
            {
                case ProjectLaunchMode.New: return "new";
                case ProjectLaunchMode.Continue: return "continue";
                default: return "ask";
            }
        }

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

        /// <summary>
        /// "Watch issues and pull requests" (ticket df58b35d, GitHub #53). SQLite-only
        /// (<c>projects.repo_watch_enabled</c>), not carried by project.json and not named by the
        /// SaveRichProject UPSERT, so a project.json re-save can neither reset nor write it. Null reads
        /// as OFF; use <see cref="IsRepoWatchEnabled"/>. Written through ProjectDatabase.UpdateProjectField.
        /// </summary>
        public bool? RepoWatchEnabled { get; set; }

        /// <summary><see cref="RepoWatchEnabled"/> with null read as off.</summary>
        public bool IsRepoWatchEnabled => RepoWatchEnabled == true;

        /// <summary>
        /// How often, in minutes, the repositories are checked (<c>projects.repo_watch_interval_min</c>):
        /// one of <see cref="RepoWatchSettings.AllowedIntervals"/>, 0 meaning only at startup and when the
        /// project is launched. Null reads as <see cref="RepoWatchSettings.DefaultIntervalMinutes"/>; use
        /// <see cref="EffectiveRepoWatchIntervalMinutes"/>. SQLite-only, like <see cref="RepoWatchEnabled"/>.
        /// </summary>
        public int? RepoWatchIntervalMin { get; set; }

        /// <summary><see cref="RepoWatchIntervalMin"/> with null read as the default.</summary>
        public int EffectiveRepoWatchIntervalMinutes => RepoWatchIntervalMin ?? RepoWatchSettings.DefaultIntervalMinutes;

        /// <summary>
        /// The repositories to watch (<c>projects.repo_watch_repos</c>), stored normalized as
        /// <c>owner/repo, owner/repo</c> by <see cref="RepoWatchSettings.TryNormalizeRepos"/>. Null = every
        /// GitHub remote of the project folder. Read the list with <see cref="RepoWatchSettings.ParseRepos"/>.
        /// SQLite-only, like <see cref="RepoWatchEnabled"/>.
        /// </summary>
        public string RepoWatchRepos { get; set; }

        /// <summary>
        /// Name of the Claude Code config profile this project's terminals launch under (ticket c8ee0eec,
        /// GitHub #21). The profile NAME, never a path, so a machine with the profile in a different
        /// folder still matches. Null or blank = the implicit Default profile. Resolve it with
        /// <see cref="ClaudeProfileResolver.Resolve(string, string)"/>, which BLOCKS the launch when this
        /// machine has no profile of that name (it never falls back to Default). At launch the value is
        /// read from the database row, not from this object: see <c>ClaudeProfileLaunchPolicy</c>.
        /// </summary>
        public string ClaudeProfile { get; set; }

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

    /// <summary>
    /// Per-project launch behaviour (task ec2ed636), stored in <c>projects.launch_mode</c>.
    /// </summary>
    public enum ProjectLaunchMode
    {
        /// <summary>Show the "new session / continue last / pick a session" prompt. The default.</summary>
        Ask = 0,

        /// <summary>Always start a new session.</summary>
        New = 1,

        /// <summary>Always resume the newest session.</summary>
        Continue = 2,
    }
}
