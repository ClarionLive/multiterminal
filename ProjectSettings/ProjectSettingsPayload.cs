using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.ProjectSettings
{
    /// <summary>Choices for the form's dropdowns and pickers, gathered by the form from live services.</summary>
    internal sealed class ProjectSettingsOptions
    {
        public IReadOnlyList<ClaudeProfileChoice> ClaudeProfiles { get; set; } = Array.Empty<ClaudeProfileChoice>();
        public IReadOnlyList<(string Id, string DisplayName)> TeamLeads { get; set; } = Array.Empty<(string, string)>();
        public IReadOnlyList<(string Id, string DisplayName)> SourceAccounts { get; set; } = Array.Empty<(string, string)>();
        public IReadOnlyList<(string Name, string Role, string PreferredModel)> AvailableAgents { get; set; } = Array.Empty<(string, string, string)>();
        public IReadOnlyList<(string Name, string Description)> AvailableMcpServers { get; set; } = Array.Empty<(string, string)>();
        public IReadOnlyList<string> AvailableSkills { get; set; } = Array.Empty<string>();
        public IReadOnlyList<(string Type, string Description)> AvailableSpecialists { get; set; } = Array.Empty<(string, string)>();

        /// <summary>One line: can this project's GitHub repo be watched, and how did the last check go (task 92eeeb47).</summary>
        public string GitHubWatchStatus { get; set; } = string.Empty;
    }

    /// <summary>
    /// The JSON contract between the project settings form's page (project-settings.html) and C#
    /// (ticket e20e2ce0). Pure: no WebView, no database, so both directions are unit-tested.
    /// </summary>
    internal static class ProjectSettingsPayload
    {
        /// <summary>Project types offered in the Type dropdown (the list EditProjectDialog offered).</summary>
        public static readonly string[] ProjectTypes =
            { "", "dotnet", "multiterminal", "clarion-com", "clarion-webview2", "clarion", "node", "python", "other" };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>
        /// The <c>load</c> message. <c>fields</c> holds exactly <see cref="ProjectSettingsService.EditableFields"/>,
        /// read the way the service compares them, so an untouched field saves as unchanged.
        /// </summary>
        public static string BuildLoad(Project project, ProjectContext lists, ProjectSettingsOptions options, bool isDark)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in ProjectSettingsService.EditableFields)
            {
                string value = ProjectSettingsService.ReadField(project, name);
                if (name == "isPinned" || name == "quietStart" || name == "gitAutoCommit" || name == "gitHubCheckOnStart")
                    fields[name] = value == "true";
                else
                    fields[name] = value ?? string.Empty;
            }

            var message = new
            {
                type = "load",
                theme = isDark ? "dark" : "light",
                projectId = project.Id,
                fields,
                about = new
                {
                    path = project.Path ?? string.Empty,
                    createdAt = FormatDate(project.CreatedAt),
                    updatedAt = FormatDate(project.UpdatedAt),
                    lastOpenedAt = FormatDate(project.LastOpenedAt),
                    gitHubWatchStatus = options.GitHubWatchStatus ?? string.Empty,
                },
                lists = new
                {
                    agents = (lists?.Agents ?? new List<ProjectAgent>()).Select(a => new { agentName = a.AgentName, role = a.Role ?? "", preferredModel = a.PreferredModel ?? "" }),
                    specialistAgents = (lists?.SpecialistAgents ?? new List<ProjectSpecialistAgent>()).Select(s => new { agentType = s.AgentType, isEnabled = s.IsEnabled, customPrompt = s.CustomPrompt ?? "" }),
                    mcpServers = (lists?.McpServers ?? new List<ProjectMcpServer>()).Select(m => new { serverName = m.ServerName, isEnabled = m.IsEnabled }),
                    skills = (lists?.Skills ?? new List<ProjectSkill>()).Select(s => new { skillName = s.SkillName, isEnabled = s.IsEnabled }),
                    paths = (lists?.Paths ?? new List<ProjectPath>()).Select(p => new { id = p.Id, pathType = p.PathType ?? "", pathValue = p.PathValue ?? "", description = p.Description ?? "" }),
                    prompts = (lists?.Prompts ?? new List<ProjectPromptEntry>()).OrderBy(p => p.DisplayOrder).Select(p => new { id = p.Id, promptType = p.PromptType ?? "", promptText = p.PromptText ?? "", displayOrder = p.DisplayOrder }),
                },
                options = new
                {
                    claudeProfiles = options.ClaudeProfiles.Select(c => new { value = c.Name ?? "", label = c.Label, missing = c.IsMissing }),
                    teamLeads = options.TeamLeads.Select(t => new { value = t.DisplayName, label = t.DisplayName }),
                    sourceAccounts = options.SourceAccounts.Select(a => new { value = a.Id, label = a.DisplayName }),
                    projectTypes = ProjectTypes.Select(t => new { value = t, label = t.Length == 0 ? "(none)" : t }),
                    statuses = Project.StatusValues.Select(s => new { value = s.ToLowerInvariant(), label = s }),
                    launchModes = new[]
                    {
                        new { value = Project.LaunchModeToStorage(ProjectLaunchMode.Ask), label = "Ask each time" },
                        new { value = Project.LaunchModeToStorage(ProjectLaunchMode.New), label = "Always start a new session" },
                        new { value = Project.LaunchModeToStorage(ProjectLaunchMode.Continue), label = "Always continue the last session" },
                    },
                    gitHubWatchModes = new[]
                    {
                        new { value = GitHubWatchSettings.ModeToStorage(GitHubWatchMode.Off), label = "Off" },
                        new { value = GitHubWatchSettings.ModeToStorage(GitHubWatchMode.Notify), label = "Notify me of new issues and PRs" },
                        new { value = GitHubWatchSettings.ModeToStorage(GitHubWatchMode.Board), label = "Notify me and add them to the board" },
                    },
                    gitHubCheckIntervals = new[]
                    {
                        new { value = GitHubWatchSettings.IntervalToStorage(GitHubCheckInterval.Off), label = "Never (only on terminal start)" },
                        new { value = GitHubWatchSettings.IntervalToStorage(GitHubCheckInterval.Minutes15), label = "Every 15 minutes" },
                        new { value = GitHubWatchSettings.IntervalToStorage(GitHubCheckInterval.Hour1), label = "Every hour" },
                        new { value = GitHubWatchSettings.IntervalToStorage(GitHubCheckInterval.Hours4), label = "Every 4 hours" },
                        new { value = GitHubWatchSettings.IntervalToStorage(GitHubCheckInterval.Daily), label = "Once a day" },
                    },
                    terminals = new[]
                    {
                        new { value = TerminalKindHelper.ClaudeCodeValue, label = "Claude Code" },
                        new { value = TerminalKindHelper.CodexValue, label = "Codex" },
                    },
                    availableAgents = options.AvailableAgents.Select(a => new { name = a.Name, role = a.Role ?? "", preferredModel = a.PreferredModel ?? "" }),
                    availableMcpServers = options.AvailableMcpServers.Select(m => new { name = m.Name, description = m.Description ?? "" }),
                    availableSkills = options.AvailableSkills,
                    availableSpecialists = options.AvailableSpecialists.Select(s => new { type = s.Type, description = s.Description ?? "" }),
                },
            };
            return JsonSerializer.Serialize(message, JsonOptions);
        }

        private static string FormatDate(DateTime value) =>
            value == default ? "" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Turns the page's <c>save</c> message into a <see cref="ProjectSettingsUpdate"/>. Booleans become
        /// "true"/"false"; a list the page did not send stays null (untouched). Field names are passed through
        /// as sent: the service, not this parser, decides which ones may be written.
        /// </summary>
        public static ProjectSettingsUpdate ParseSave(string projectId, JsonElement root)
        {
            var update = new ProjectSettingsUpdate { ProjectId = projectId };

            if (root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in fields.EnumerateObject())
                {
                    update.Fields[f.Name] = f.Value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String => f.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => f.Value.GetRawText(),
                    };
                }
            }

            update.Agents = ReadList(root, "agents", e => new ProjectAgent { AgentName = Str(e, "agentName"), Role = Str(e, "role"), PreferredModel = Str(e, "preferredModel") });
            update.SpecialistAgents = ReadList(root, "specialistAgents", e => new ProjectSpecialistAgent { AgentType = Str(e, "agentType"), IsEnabled = Bool(e, "isEnabled", true), CustomPrompt = Str(e, "customPrompt") });
            update.McpServers = ReadList(root, "mcpServers", e => new ProjectMcpServer { ServerName = Str(e, "serverName"), IsEnabled = Bool(e, "isEnabled", true) });
            update.Skills = ReadList(root, "skills", e => new ProjectSkill { SkillName = Str(e, "skillName"), IsEnabled = Bool(e, "isEnabled", true) });
            update.Paths = ReadList(root, "paths", e => new ProjectPath { Id = Int(e, "id"), PathType = Str(e, "pathType"), PathValue = Str(e, "pathValue"), Description = Str(e, "description") });
            update.Prompts = ReadList(root, "prompts", e => new ProjectPromptEntry { Id = Int(e, "id"), PromptType = Str(e, "promptType"), PromptText = Str(e, "promptText"), DisplayOrder = Int(e, "displayOrder") });
            return update;
        }

        private static List<T> ReadList<T>(JsonElement root, string name, Func<JsonElement, T> read)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                return null;
            return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(read).ToList();
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name, bool fallback) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;

        private static int Int(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
            return 0;
        }
    }
}
