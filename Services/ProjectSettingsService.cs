using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What <see cref="ProjectSettingsService"/> needs from outside the project tables (ticket e20e2ce0).
    /// Narrow on purpose, like the broker-extraction hosts: it is the census of everything a project
    /// settings save touches beyond <see cref="ProjectDatabase"/> and <see cref="ProjectService"/>.
    /// </summary>
    public interface IProjectSettingsHost
    {
        /// <summary>
        /// Renames the project and/or changes its description through the broker, which owns the cached
        /// project name, syncs project.json and records the activity entry. Returns null on success, else
        /// the reason it failed.
        /// </summary>
        string RenameProject(string projectId, string name, string description);

        /// <summary>Tells every listener the project list changed (MessageBroker.ProjectsUpdated).</summary>
        void NotifyProjectsChanged();

        /// <summary>Pushes the project's enabled MCP server names to the gateway profile, if one is installed.</summary>
        void SyncGatewayProfile(string projectId, string projectName, ISet<string> enabledServerNames);
    }

    /// <summary>One save of the project settings form. A null list leaves that table untouched.</summary>
    public sealed class ProjectSettingsUpdate
    {
        public string ProjectId { get; set; }

        /// <summary>
        /// Scalar fields by their camelCase name (the names <see cref="ProjectDatabase.UpdateProjectField"/>
        /// accepts). Only fields in <see cref="ProjectSettingsService.EditableFields"/> are allowed. A field
        /// that is absent is left as it is; a blank value clears it.
        /// </summary>
        public Dictionary<string, string> Fields { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public List<ProjectAgent> Agents { get; set; }
        public List<ProjectSpecialistAgent> SpecialistAgents { get; set; }
        public List<ProjectMcpServer> McpServers { get; set; }
        public List<ProjectSkill> Skills { get; set; }
        public List<ProjectPath> Paths { get; set; }
        public List<ProjectPromptEntry> Prompts { get; set; }
    }

    /// <summary>What a save did. <see cref="Warnings"/> are things that saved but not everywhere.</summary>
    public sealed class ProjectSettingsSaveResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>The camelCase names of the scalar fields whose stored value changed.</summary>
        public List<string> ChangedFields { get; } = new List<string>();

        public bool McpServersChanged { get; set; }

        internal static ProjectSettingsSaveResult Fail(string error) => new ProjectSettingsSaveResult { Success = false, Error = error };
    }

    /// <summary>
    /// The one save path for a project's settings (ticket e20e2ce0). The project settings form saves
    /// through it; it replaced EditProjectDialog's and the Project pane's separate writers, which covered
    /// different fields and told nothing else the project had changed.
    ///
    /// <para>Order: validate everything first, so a bad value writes nothing; rename through the host; every
    /// other scalar through <see cref="ProjectDatabase.UpdateProjectField"/> (its whitelist, worktree-path
    /// guard and Claude profile normalization); the six child tables, diff-saved; the project.json copy of
    /// the fields that file carries; the gateway profile when MCP servers changed; then one change
    /// notification.</para>
    ///
    /// <para>project.json is written with <see cref="ProjectService.UpdateProjectJson"/>, never
    /// <see cref="ProjectService.SaveProject"/>. SaveProject re-upserts the row from the file, and the file
    /// has no gitAutoCommit, so it reset Auto-commit to off; that is what the retired Project Manager edit
    /// did after every save.</para>
    /// </summary>
    public sealed class ProjectSettingsService
    {
        /// <summary>
        /// Scalar fields the form may write. <c>path</c> is deliberately absent (the project's identity
        /// folder is not edited here) and so is <c>lastOpenedAt</c> (system-maintained).
        /// </summary>
        public static readonly IReadOnlyCollection<string> EditableFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "description", "projectType", "status", "currentVersion", "icon", "iconColor",
            "isPinned", "createdBy", "claudeProfile", "defaultTerminal", "quietStart", "teamLead",
            "launchCommand", "sourcePath", "deployPath", "buildOutputPath", "buildCommand", "deployCommand",
            "gitRepoUrl", "gitDefaultBranch", "gitAutoCommit", "sourceControlAccountId", "changeLog",
        };

        private static readonly HashSet<string> BooleanFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "isPinned", "quietStart", "gitAutoCommit",
        };

        private readonly ProjectDatabase _db;
        private readonly ProjectService _projectService;
        private readonly IProjectSettingsHost _host;

        public ProjectSettingsService(ProjectDatabase db, ProjectService projectService, IProjectSettingsHost host)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public ProjectSettingsSaveResult Save(ProjectSettingsUpdate update)
        {
            if (update == null || string.IsNullOrWhiteSpace(update.ProjectId))
                return ProjectSettingsSaveResult.Fail("No project was given.");

            var current = _db.GetRichProject(update.ProjectId);
            if (current == null)
                return ProjectSettingsSaveResult.Fail($"Project not found: {update.ProjectId}");

            // ── Validate everything before writing anything ──────────────────────────
            var unknown = update.Fields.Keys.Where(k => !EditableFields.Contains(k)).ToList();
            if (unknown.Count > 0)
                return ProjectSettingsSaveResult.Fail($"These settings cannot be saved here: {string.Join(", ", unknown)}");

            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in update.Fields)
                normalized[pair.Key] = NormalizeValue(pair.Key, pair.Value);

            if (normalized.TryGetValue("name", out var newName) && string.IsNullOrWhiteSpace(newName))
                return ProjectSettingsSaveResult.Fail("The project name cannot be blank.");

            string listError = ValidateLists(update);
            if (listError != null)
                return ProjectSettingsSaveResult.Fail(listError);

            var result = new ProjectSettingsSaveResult { Success = true };
            var changed = normalized.Where(p => !SameValue(p.Key, ReadField(current, p.Key), p.Value))
                                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

            // ── Name and description go through the broker (cache + project.json + activity) ──
            bool renamed = changed.ContainsKey("name");
            bool redescribed = changed.ContainsKey("description");
            if (renamed || redescribed)
            {
                string error = _host.RenameProject(
                    update.ProjectId,
                    renamed ? changed["name"] : null,
                    redescribed ? (changed["description"] ?? string.Empty) : null);
                if (error != null)
                    return ProjectSettingsSaveResult.Fail($"Saving the name or description failed: {error}");

                // The broker writes the slim row; the description it writes is "" for a cleared one.
                // Restate both through the rich column writer so the rich row says exactly what was saved.
                if (renamed) _db.UpdateProjectField(update.ProjectId, "name", changed["name"]);
                if (redescribed) _db.UpdateProjectField(update.ProjectId, "description", changed["description"]);
            }

            // ── Every other scalar ───────────────────────────────────────────────────
            foreach (var pair in changed)
            {
                if (pair.Key == "name" || pair.Key == "description")
                    continue;
                if (!_db.UpdateProjectField(update.ProjectId, pair.Key, pair.Value))
                    return PartialFail(result, $"Saving '{pair.Key}' failed.");
            }
            result.ChangedFields.AddRange(changed.Keys);

            // ── Child tables ─────────────────────────────────────────────────────────
            try
            {
                SaveAgents(update.ProjectId, update.Agents);
                SaveSpecialists(update.ProjectId, update.SpecialistAgents);
                result.McpServersChanged = SaveMcpServers(update.ProjectId, update.McpServers);
                SaveSkills(update.ProjectId, update.Skills);
                SavePaths(update.ProjectId, update.Paths);
                SavePrompts(update.ProjectId, update.Prompts);
            }
            catch (Exception ex) when (ex is System.Data.SQLite.SQLiteException || ex is InvalidOperationException)
            {
                return PartialFail(result, $"Saving the project lists failed: {ex.Message}");
            }

            // ── project.json copy of the fields that file carries ────────────────────
            SyncProjectJson(current, changed, result);

            // ── Gateway profile ──────────────────────────────────────────────────────
            if (result.McpServersChanged)
            {
                string projectName = renamed ? changed["name"] : current.Name;
                _host.SyncGatewayProfile(update.ProjectId, projectName, _db.GetEnabledMcpServerNamesForProject(update.ProjectId));
            }

            _host.NotifyProjectsChanged();
            return result;
        }

        // Something was already written; still tell listeners, so the screen shows what is stored.
        private ProjectSettingsSaveResult PartialFail(ProjectSettingsSaveResult result, string error)
        {
            result.Success = false;
            result.Error = error + " Some settings before it were saved.";
            _host.NotifyProjectsChanged();
            return result;
        }

        // ── Scalars ────────────────────────────────────────────────────────────────

        internal static string NormalizeValue(string field, string value)
        {
            if (BooleanFields.Contains(field))
                return value == "true" || value == "1" ? "true" : "false";

            switch (field)
            {
                case "name":
                    return value ?? string.Empty;
                case "status":
                    return Project.NormalizeStatus(value).ToLowerInvariant();
                case "defaultTerminal":
                    return TerminalKindHelper.Normalize(value);
                case "claudeProfile":
                    return ClaudeProfile.NormalizeName(value);
                case "currentVersion":
                    return string.IsNullOrWhiteSpace(value) ? "0.1.0" : value;
                default:
                    // A blank optional field is stored as null, as EditProjectDialog did.
                    return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        private static bool SameValue(string field, string stored, string incoming)
        {
            if (field == "status" || field == "claudeProfile")
                return string.Equals(stored, incoming, StringComparison.OrdinalIgnoreCase);
            if (field == "name")
                return string.Equals(stored, incoming, StringComparison.Ordinal);
            // Null and blank are the same stored state for an optional text field.
            return string.Equals(string.IsNullOrEmpty(stored) ? null : stored, incoming, StringComparison.Ordinal);
        }

        /// <summary>The stored value of a field, in the same form <see cref="NormalizeValue"/> produces.</summary>
        internal static string ReadField(Project p, string field)
        {
            switch (field)
            {
                case "name": return p.Name;
                case "description": return p.Description;
                case "projectType": return p.ProjectType;
                case "status": return Project.NormalizeStatus(p.Status).ToLowerInvariant();
                case "currentVersion": return p.CurrentVersion;
                case "icon": return p.Icon;
                case "iconColor": return p.IconColor;
                case "isPinned": return p.IsPinned ? "true" : "false";
                case "createdBy": return p.CreatedBy;
                case "claudeProfile": return ClaudeProfile.NormalizeName(p.ClaudeProfile);
                case "defaultTerminal": return TerminalKindHelper.Normalize(p.DefaultTerminal);
                case "quietStart": return p.IsQuietStart ? "true" : "false";
                case "teamLead": return p.TeamLead;
                case "launchCommand": return p.LaunchCommand;
                case "sourcePath": return p.SourcePath;
                case "deployPath": return p.DeployPath;
                case "buildOutputPath": return p.BuildOutputPath;
                case "buildCommand": return p.BuildCommand;
                case "deployCommand": return p.DeployCommand;
                case "gitRepoUrl": return p.GitRepoUrl;
                case "gitDefaultBranch": return p.GitDefaultBranch;
                case "gitAutoCommit": return p.GitAutoCommit ? "true" : "false";
                case "sourceControlAccountId": return p.SourceControlAccountId;
                case "changeLog": return p.ChangeLog;
                default: throw new ArgumentOutOfRangeException(nameof(field), field, "Not an editable project field.");
            }
        }

        // ── project.json ───────────────────────────────────────────────────────────

        // project.json carries name and description (the broker's rename already wrote those), plus these.
        private static readonly string[] ProjectJsonFields = { "changeLog", "currentVersion", "isPinned", "defaultTerminal", "claudeProfile" };

        private void SyncProjectJson(Project current, Dictionary<string, string> changed, ProjectSettingsSaveResult result)
        {
            if (!ProjectJsonFields.Any(changed.ContainsKey) || string.IsNullOrWhiteSpace(current.Path))
                return;

            try
            {
                bool written = _projectService.UpdateProjectJson(current.Path, current.Id, file =>
                {
                    if (changed.TryGetValue("changeLog", out var log)) file.ChangeLog = log;
                    if (changed.TryGetValue("currentVersion", out var version)) file.CurrentVersion = version;
                    if (changed.TryGetValue("isPinned", out var pinned)) file.IsPinned = pinned == "true";
                    if (changed.TryGetValue("defaultTerminal", out var terminal)) file.DefaultTerminal = terminal;
                    if (changed.TryGetValue("claudeProfile", out var profile)) file.ClaudeProfile = profile;
                });
                // No project.json, or one answering to another id, is a folder this project does not
                // carry a portable copy in; only say so when the file exists and disagrees.
                if (!written && _projectService.IsProjectDirectory(current.Path))
                    result.Warnings.Add($"The project.json in '{current.Path}' was not updated: it belongs to a different project id or could not be read.");
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Warnings.Add($"The project.json in '{current.Path}' was not updated: {ex.Message}");
            }
        }

        // ── Child tables ───────────────────────────────────────────────────────────

        private static string ValidateLists(ProjectSettingsUpdate u)
        {
            string dup =
                FirstDuplicate(u.Agents?.Select(a => a.AgentName), "agent") ??
                FirstDuplicate(u.SpecialistAgents?.Select(a => a.AgentType), "specialist agent") ??
                FirstDuplicate(u.McpServers?.Select(a => a.ServerName), "MCP server") ??
                FirstDuplicate(u.Skills?.Select(a => a.SkillName), "skill");
            if (dup != null) return dup;

            if (u.Paths != null && u.Paths.Any(p => string.IsNullOrWhiteSpace(p.PathValue)))
                return "Every project path needs a path.";
            if (u.Prompts != null && u.Prompts.Any(p => string.IsNullOrWhiteSpace(p.PromptText)))
                return "Every prompt needs text.";
            return null;
        }

        // Rows are keyed by name in these tables (UNIQUE(project_id, name)); a blank key is a row the
        // user left empty and is skipped, as the old grids did.
        private static string FirstDuplicate(IEnumerable<string> keys, string what)
        {
            if (keys == null) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                if (!seen.Add(k)) return $"The {what} '{k}' is listed twice.";
            }
            return null;
        }

        private void SaveAgents(string projectId, List<ProjectAgent> rows)
        {
            if (rows == null) return;
            var keep = rows.Where(r => !string.IsNullOrWhiteSpace(r.AgentName)).ToList();
            var keepNames = new HashSet<string>(keep.Select(r => r.AgentName), StringComparer.Ordinal);
            foreach (var stored in _db.GetProjectAgents(projectId).Where(s => !keepNames.Contains(s.AgentName)))
                _db.DeleteProjectAgent(projectId, stored.AgentName);
            foreach (var r in keep)
                _db.SaveProjectAgent(new ProjectAgent { ProjectId = projectId, AgentName = r.AgentName, Role = r.Role, PreferredModel = r.PreferredModel });
        }

        private void SaveSpecialists(string projectId, List<ProjectSpecialistAgent> rows)
        {
            if (rows == null) return;
            var keep = rows.Where(r => !string.IsNullOrWhiteSpace(r.AgentType)).ToList();
            var keepNames = new HashSet<string>(keep.Select(r => r.AgentType), StringComparer.Ordinal);
            foreach (var stored in _db.GetProjectSpecialistAgents(projectId).Where(s => !keepNames.Contains(s.AgentType)))
                _db.DeleteProjectSpecialistAgent(projectId, stored.AgentType);
            foreach (var r in keep)
                _db.SaveProjectSpecialistAgent(new ProjectSpecialistAgent { ProjectId = projectId, AgentType = r.AgentType, IsEnabled = r.IsEnabled, CustomPrompt = r.CustomPrompt });
        }

        /// <returns>True when the stored set of servers or any enabled flag changed.</returns>
        private bool SaveMcpServers(string projectId, List<ProjectMcpServer> rows)
        {
            if (rows == null) return false;
            var stored = _db.GetProjectMcpServers(projectId);
            var keep = rows.Where(r => !string.IsNullOrWhiteSpace(r.ServerName)).ToList();
            var keepNames = new HashSet<string>(keep.Select(r => r.ServerName), StringComparer.Ordinal);

            bool changed = false;
            foreach (var s in stored.Where(s => !keepNames.Contains(s.ServerName)))
            {
                _db.DeleteProjectMcpServer(projectId, s.ServerName);
                changed = true;
            }
            foreach (var r in keep)
            {
                var before = stored.FirstOrDefault(s => string.Equals(s.ServerName, r.ServerName, StringComparison.Ordinal));
                if (before != null && before.IsEnabled == r.IsEnabled)
                    continue;
                _db.SaveProjectMcpServer(new ProjectMcpServer { ProjectId = projectId, ServerName = r.ServerName, IsEnabled = r.IsEnabled });
                changed = true;
            }
            return changed;
        }

        private void SaveSkills(string projectId, List<ProjectSkill> rows)
        {
            if (rows == null) return;
            var keep = rows.Where(r => !string.IsNullOrWhiteSpace(r.SkillName)).ToList();
            var keepNames = new HashSet<string>(keep.Select(r => r.SkillName), StringComparer.Ordinal);
            foreach (var stored in _db.GetProjectSkills(projectId).Where(s => !keepNames.Contains(s.SkillName)))
                _db.DeleteProjectSkill(projectId, stored.SkillName);
            foreach (var r in keep)
                _db.SaveProjectSkill(new ProjectSkill { ProjectId = projectId, SkillName = r.SkillName, IsEnabled = r.IsEnabled });
        }

        // Paths and prompts are keyed by row id. An id the project does not own is treated as new, so a
        // save can never update or delete another project's row.
        private void SavePaths(string projectId, List<ProjectPath> rows)
        {
            if (rows == null) return;
            var storedIds = new HashSet<int>(_db.GetProjectPaths(projectId).Select(p => p.Id));
            var keepIds = new HashSet<int>(rows.Where(r => storedIds.Contains(r.Id)).Select(r => r.Id));
            foreach (var id in storedIds.Where(id => !keepIds.Contains(id)))
                _db.DeleteProjectPath(id);
            foreach (var r in rows)
            {
                _db.SaveProjectPath(new ProjectPath
                {
                    Id = storedIds.Contains(r.Id) ? r.Id : 0,
                    ProjectId = projectId,
                    PathType = r.PathType,
                    PathValue = r.PathValue,
                    Description = r.Description,
                });
            }
        }

        private void SavePrompts(string projectId, List<ProjectPromptEntry> rows)
        {
            if (rows == null) return;
            var storedIds = new HashSet<int>(_db.GetProjectPrompts(projectId).Select(p => p.Id));
            var keepIds = new HashSet<int>(rows.Where(r => storedIds.Contains(r.Id)).Select(r => r.Id));
            foreach (var id in storedIds.Where(id => !keepIds.Contains(id)))
                _db.DeleteProjectPrompt(id);
            foreach (var r in rows)
            {
                _db.SaveProjectPrompt(new ProjectPromptEntry
                {
                    Id = storedIds.Contains(r.Id) ? r.Id : 0,
                    ProjectId = projectId,
                    PromptType = r.PromptType,
                    PromptText = r.PromptText,
                    DisplayOrder = r.DisplayOrder,
                });
            }
        }
    }
}
