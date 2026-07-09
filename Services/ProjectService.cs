using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Event arguments for project operations.
    /// </summary>
    public class ProjectEventArgs : EventArgs
    {
        public Project Project { get; }
        public ProjectEventArgs(Project project) => Project = project;
    }

    /// <summary>
    /// Service for managing projects backed by SQLite (ProjectDatabase).
    /// Project records are stored in the projects table (same multiterminal.db as TaskDatabase).
    ///
    /// Also maintains portable .claude/project.json files in each project folder for
    /// backward compatibility and migration support.
    ///
    /// Phase 4 change: removed in-memory JSON registry cache and FileSystemWatcher.
    /// All registry read/write operations now go through ProjectDatabase.
    /// </summary>
    public class ProjectService : IDisposable
    {
        private readonly ProjectDatabase _projectDb;
        private bool _isDisposed;

        /// <summary>
        /// Fired when a project is opened.
        /// </summary>
        public event EventHandler<ProjectEventArgs> ProjectOpened;

        /// <summary>
        /// Fired when a project is registered.
        /// </summary>
        public event EventHandler<ProjectEventArgs> ProjectRegistered;

        /// <summary>
        /// Fired when a project is updated.
        /// </summary>
        public event EventHandler<ProjectEventArgs> ProjectUpdated;

        /// <summary>
        /// Fired when a project is removed.
        /// </summary>
        public event EventHandler<ProjectEventArgs> ProjectRemoved;

        /// <summary>
        /// Kept for API compatibility. Fired when the project list changes programmatically.
        /// Previously fired by a FileSystemWatcher watching projects.json — the watcher has
        /// been removed as part of the SQLite-only migration.
        /// </summary>
        public event EventHandler RegistryChangedExternally;

        /// <summary>
        /// Creates a ProjectService over a fresh ProjectDatabase.
        /// CANONICAL INSTANCE: the app has ONE ProjectService — <c>MainForm._projectService</c> — which
        /// is threaded into the REST DI via the <see cref="MultiTerminal.API.MultiTerminalRestServer"/>
        /// ctor (G8) so <c>broker.ProjectService</c> and every REST controller share it. Do NOT
        /// construct a second instance for any event-bearing path (ProjectRegistered/Updated/Removed):
        /// subscribers on a different instance (e.g. CodeGraphWatcher) won't hear its events.
        /// </summary>
        public ProjectService()
        {
            _projectDb = new ProjectDatabase();
        }

        /// <summary>
        /// Constructor that accepts an existing ProjectDatabase instance (for DI / testing).
        /// </summary>
        public ProjectService(ProjectDatabase projectDb)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
        }

        #region Registry Operations (backed by SQLite)

        /// <summary>
        /// Gets all registered projects from SQLite.
        /// </summary>
        public List<ProjectRegistryEntry> GetAllRegisteredProjects()
        {
            return _projectDb.GetAllRichProjects()
                .Select(ProjectRegistryEntry.FromProject)
                .ToList();
        }

        /// <summary>
        /// Gets recently opened projects (sorted by LastOpenedAt descending).
        /// </summary>
        public List<ProjectRegistryEntry> GetRecentProjects(int maxCount = 10)
        {
            return _projectDb.GetAllRichProjects()
                .OrderByDescending(p => p.LastOpenedAt)
                .Take(maxCount)
                .Select(ProjectRegistryEntry.FromProject)
                .ToList();
        }

        /// <summary>
        /// Gets pinned/favorite projects.
        /// </summary>
        public List<ProjectRegistryEntry> GetPinnedProjects()
        {
            return _projectDb.GetAllRichProjects()
                .Where(p => p.IsPinned)
                .OrderBy(p => p.Name)
                .Select(ProjectRegistryEntry.FromProject)
                .ToList();
        }

        /// <summary>
        /// Searches projects by name (case-insensitive partial match).
        /// </summary>
        public List<ProjectRegistryEntry> SearchProjects(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return GetAllRegisteredProjects();

            return _projectDb.GetAllRichProjects()
                .Where(p => p.Name != null && p.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.Name)
                .Select(ProjectRegistryEntry.FromProject)
                .ToList();
        }

        #endregion

        #region Project CRUD

        /// <summary>
        /// Loads a full project from its .claude/project.json file.
        /// Returns null if file doesn't exist or is invalid.
        /// NOTE: This reads from the filesystem (for migration support and portability).
        /// For SQLite-backed reads, use ProjectDatabase.GetRichProject().
        /// </summary>
        public Project LoadProject(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            string configPath = GetProjectConfigPath(projectPath);
            // CA3003: projectPath is an app-managed project root (from registered projects
            // registry or user-selected folder dialog), not attacker-controlled web input.
#pragma warning disable CA3003
            if (!File.Exists(configPath))
                return null;

            try
            {
                string json = File.ReadAllText(configPath);
#pragma warning restore CA3003
                var project = ParseProjectJson(json);
                if (project != null)
                {
                    // Ensure path is set correctly (in case folder was moved)
                    project.Path = projectPath;
                }
                return project;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to load project from {configPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads a project by its registry entry.
        /// </summary>
        public Project LoadProject(ProjectRegistryEntry entry)
        {
            if (entry == null)
                return null;
            return LoadProject(entry.Path);
        }

        /// <summary>
        /// Saves a project to its .claude/project.json file and upserts into SQLite.
        /// </summary>
        public void SaveProject(Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            if (string.IsNullOrEmpty(project.Path))
                throw new ArgumentException("Project path is required", nameof(project));

            // Guard the canonical project path at the source (task 19d0d867): if a caller passes a
            // git-worktree directory (e.g. create_project / the New Project dialog invoked from a
            // worktree cwd), rewrite it to the stable repo root BEFORE we write .claude/project.json
            // and upsert into SQLite. A worktree path is ephemeral — once pruned the project goes
            // invisible to CodeGraphWatcher and every other path-dependent feature. The DB layer
            // (ProjectDatabase) enforces this too as a chokepoint; normalizing here additionally
            // keeps the on-disk project.json under the durable root rather than the worktree.
            if (WorktreeLayout.TryResolveStableProjectPath(project.Path, project.SourcePath, out var canonicalRoot))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ProjectService] Canonicalized worktree path for project '{project.Name}': '{project.Path}' -> '{canonicalRoot}'");
                project.Path = canonicalRoot;
            }

            // Ensure .claude folder exists
            string claudeFolder = Path.Combine(project.Path, ".claude");
            // CA3003: project.Path is an app-managed Project object path, assigned at
            // registration time from a user-selected folder — not attacker-controlled input.
#pragma warning disable CA3003
            if (!Directory.Exists(claudeFolder))
                Directory.CreateDirectory(claudeFolder);

            // Save portable project config to .claude/project.json
            string configPath = GetProjectConfigPath(project.Path);
            string json = SerializeProjectJson(project);
            File.WriteAllText(configPath, json);
#pragma warning restore CA3003

            // Upsert into SQLite (SQLite is now the authoritative registry)
            _projectDb.SaveRichProject(project);

            ProjectUpdated?.Invoke(this, new ProjectEventArgs(project));
        }

        /// <summary>
        /// Registers a new project (creates .claude/project.json, upserts to SQLite).
        /// </summary>
        public Project RegisterProject(string path, string name, string description = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is required", nameof(path));

            if (string.IsNullOrEmpty(name))
                name = Path.GetFileName(path);

            var project = Project.Create(name, path);
            project.Description = description ?? "";

            SaveProject(project);

            ProjectRegistered?.Invoke(this, new ProjectEventArgs(project));
            return project;
        }

        /// <summary>
        /// Removes a project from SQLite.
        /// Optionally deletes the .claude/project.json file.
        /// </summary>
        public void UnregisterProject(string projectId, bool deleteLocalConfig = false)
        {
            var richProject = _projectDb.GetRichProject(projectId);
            if (richProject == null)
                return;

            if (deleteLocalConfig && !string.IsNullOrEmpty(richProject.Path))
            {
                string configPath = GetProjectConfigPath(richProject.Path);
                if (File.Exists(configPath))
                {
                    try
                    {
                        File.Delete(configPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to delete project config: {ex.Message}");
                    }
                }
            }

            _projectDb.DeleteProject(projectId);

            ProjectRemoved?.Invoke(this, new ProjectEventArgs(richProject));
            RegistryChangedExternally?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Updates the LastOpenedAt timestamp when a project is opened.
        /// </summary>
        public void MarkProjectOpened(string projectId)
        {
            var richProject = _projectDb.GetRichProject(projectId);
            if (richProject != null)
            {
                var stampedAt = DateTime.Now;
                richProject.LastOpenedAt = stampedAt;

                // Persist best-effort on a background thread: the stamp is cosmetic recency
                // metadata, and a busy multiterminal.db (e.g. a code-graph reindex holding the
                // write lock for seconds) must not throw into the caller's launch/select
                // handler or stall the UI thread (task 93ad8184). Single-column UpdateLastOpened,
                // NOT SaveRichProject — a deferred full-row snapshot could revert a concurrent
                // project edit or resurrect a deleted row (pipeline Run-1 finding).
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _projectDb.UpdateLastOpened(richProject.Id, stampedAt);

                        // Also update the portable project.json if it exists
                        if (!string.IsNullOrEmpty(richProject.Path))
                        {
                            var fileProject = LoadProject(richProject.Path);
                            if (fileProject != null)
                            {
                                fileProject.LastOpenedAt = stampedAt;
                                string configPath = GetProjectConfigPath(richProject.Path);
                                string json = SerializeProjectJson(fileProject);
                                try { File.WriteAllText(configPath, json); }
                                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to update project.json lastOpenedAt: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProjectService] Best-effort LastOpenedAt stamp failed: {ex.Message}");
                    }
                });

                ProjectOpened?.Invoke(this, new ProjectEventArgs(richProject));
            }
        }

        /// <summary>
        /// Toggles the pinned state of a project.
        /// </summary>
        public void ToggleProjectPinned(string projectId)
        {
            var richProject = _projectDb.GetRichProject(projectId);
            if (richProject != null)
            {
                richProject.IsPinned = !richProject.IsPinned;
                _projectDb.SaveRichProject(richProject);

                // Also update the portable project.json if it exists
                if (!string.IsNullOrEmpty(richProject.Path))
                {
                    var fileProject = LoadProject(richProject.Path);
                    if (fileProject != null)
                    {
                        fileProject.IsPinned = richProject.IsPinned;
                        string configPath = GetProjectConfigPath(richProject.Path);
                        string json = SerializeProjectJson(fileProject);
                        try { File.WriteAllText(configPath, json); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to update project.json isPinned: {ex.Message}"); }
                    }
                }
            }
        }

        /// <summary>
        /// Sets a project's default terminal CLI. Writes SQLite AND the portable
        /// project.json, mirroring <see cref="ToggleProjectPinned"/>. DefaultTerminal is
        /// serialized to project.json (unlike Status), so a DB-only write would be silently
        /// reverted the next time ChangelogService / VersioningService does
        /// LoadProject → SaveProject and the stale on-disk value re-asserts through the
        /// SaveRichProject COALESCE (which can't guard a non-null field). Keeping both stores
        /// in sync is what makes a live terminal change durable.
        /// </summary>
        public void SetDefaultTerminal(string projectId, string terminalKind)
        {
            var richProject = _projectDb.GetRichProject(projectId);
            if (richProject != null)
            {
                var normalized = MultiTerminal.Models.TerminalKindHelper.Normalize(terminalKind);
                richProject.DefaultTerminal = normalized;
                _projectDb.SaveRichProject(richProject);

                // Also update the portable project.json if it exists (keeps it from reverting the DB).
                if (!string.IsNullOrEmpty(richProject.Path))
                {
                    var fileProject = LoadProject(richProject.Path);
                    if (fileProject != null)
                    {
                        fileProject.DefaultTerminal = normalized;
                        string configPath = GetProjectConfigPath(richProject.Path);
                        string json = SerializeProjectJson(fileProject);
                        try { File.WriteAllText(configPath, json); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to update project.json defaultTerminal: {ex.Message}"); }
                    }
                }
            }
        }

        #endregion

        #region Discovery & Auto-Detection

        /// <summary>
        /// Checks if a directory contains a .claude/project.json file.
        /// </summary>
        public bool IsProjectDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
                return false;

            string configPath = GetProjectConfigPath(directoryPath);
            return File.Exists(configPath);
        }

        /// <summary>
        /// Attempts to discover and load a project from a directory.
        /// Returns null if not a project directory.
        /// </summary>
        public Project DiscoverProject(string directoryPath)
        {
            if (!IsProjectDirectory(directoryPath))
                return null;

            return LoadProject(directoryPath);
        }

        /// <summary>
        /// Checks if a discovered project is already in SQLite.
        /// </summary>
        public bool IsProjectRegistered(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return false;

            return _projectDb.GetAllRichProjects()
                .Any(p => string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(p.SourcePath, projectPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets a registry entry by path.
        /// </summary>
        public ProjectRegistryEntry GetRegistryEntryByPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            var richProject = _projectDb.GetAllRichProjects()
                .FirstOrDefault(p =>
                    string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.SourcePath, projectPath, StringComparison.OrdinalIgnoreCase));

            return richProject == null ? null : ProjectRegistryEntry.FromProject(richProject);
        }

        /// <summary>
        /// Validates that SQLite project entries still point to valid project folders.
        /// Removes entries where the path no longer exists.
        /// </summary>
        public int CleanupRegistry()
        {
            var projects = _projectDb.GetAllRichProjects();
            int removed = 0;

            foreach (var project in projects)
            {
                string checkPath = project.SourcePath ?? project.Path;
                if (string.IsNullOrEmpty(checkPath) || !Directory.Exists(checkPath))
                {
                    _projectDb.DeleteProject(project.Id);
                    removed++;
                }
            }

            return removed;
        }

        #endregion

        #region Prompt Management (reads from .claude/project.json)

        /// <summary>
        /// Gets all prompts for a project (from .claude/project.json).
        /// </summary>
        public List<Prompt> GetProjectPrompts(string projectPath)
        {
            var project = LoadProject(projectPath);
            return project?.Prompts ?? new List<Prompt>();
        }

        /// <summary>
        /// Adds or updates a prompt in a project.
        /// </summary>
        public void SaveProjectPrompt(string projectPath, Prompt prompt)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            var project = LoadProject(projectPath);
            if (project == null)
                throw new InvalidOperationException("Project not found at path: " + projectPath);

            if (string.IsNullOrEmpty(prompt.Id))
                prompt.Id = Guid.NewGuid().ToString();

            if (prompt.CreatedAt == default)
                prompt.CreatedAt = DateTime.Now;

            // Remove existing prompt with same ID
            project.Prompts.RemoveAll(p => p.Id == prompt.Id);
            project.Prompts.Add(prompt);

            SaveProject(project);
        }

        /// <summary>
        /// Deletes a prompt from a project.
        /// </summary>
        public bool DeleteProjectPrompt(string projectPath, string promptId)
        {
            if (string.IsNullOrEmpty(promptId))
                return false;

            var project = LoadProject(projectPath);
            if (project == null)
                return false;

            int removed = project.Prompts.RemoveAll(p => p.Id == promptId);
            if (removed > 0)
            {
                SaveProject(project);
                return true;
            }

            return false;
        }

        #endregion

        #region Private Helpers

        private string GetProjectConfigPath(string projectPath)
        {
            return Path.Combine(projectPath, ".claude", "project.json");
        }

        #endregion

        #region JSON Serialization (project.json — for portable project files)

        private string SerializeProjectJson(Project project)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"id\": {JsonEscape(project.Id)},");
            sb.AppendLine($"  \"name\": {JsonEscape(project.Name)},");
            sb.AppendLine($"  \"description\": {JsonEscape(project.Description)},");
            sb.AppendLine($"  \"changeLog\": {JsonEscape(project.ChangeLog)},");
            sb.AppendLine($"  \"currentVersion\": {JsonEscape(project.CurrentVersion)},");
            sb.AppendLine($"  \"createdAt\": {JsonEscape(project.CreatedAt.ToString("o"))},");
            sb.AppendLine($"  \"lastOpenedAt\": {JsonEscape(project.LastOpenedAt.ToString("o"))},");
            sb.AppendLine($"  \"isPinned\": {(project.IsPinned ? "true" : "false")},");
            sb.AppendLine($"  \"defaultTerminal\": {JsonEscape(TerminalKindHelper.Normalize(project.DefaultTerminal))},");
            // sourceControlAccountId is intentionally NOT serialized to project.json — SQLite is
            // the single source of truth for the account binding (same convention as gitRepoUrl /
            // gitDefaultBranch / gitAutoCommit). Serializing it here let a stale on-disk value
            // re-assert itself through LoadProject -> SaveProject -> SaveRichProject (COALESCE),
            // silently reverting account changes/clears made via the edit dialog.
            sb.AppendLine("  \"prompts\": [");

            for (int i = 0; i < project.Prompts.Count; i++)
            {
                var p = project.Prompts[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"id\": {JsonEscape(p.Id)},");
                sb.AppendLine($"      \"category\": {JsonEscape(p.Category)},");
                sb.AppendLine($"      \"description\": {JsonEscape(p.Description)},");
                sb.AppendLine($"      \"text\": {JsonEscape(p.Text)},");
                sb.AppendLine($"      \"isGlobal\": {(p.IsGlobal ? "true" : "false")},");
                sb.AppendLine($"      \"createdAt\": {JsonEscape(p.CreatedAt.ToString("o"))}");
                sb.Append("    }");
                if (i < project.Prompts.Count - 1)
                    sb.AppendLine(",");
                else
                    sb.AppendLine();
            }

            sb.AppendLine("  ],");

            // Serialize team.agents
            sb.AppendLine("  \"team\": {");
            sb.Append("    \"agents\": [");
            if (project.TeamAgents != null && project.TeamAgents.Count > 0)
            {
                for (int i = 0; i < project.TeamAgents.Count; i++)
                {
                    sb.Append(JsonEscape(project.TeamAgents[i]));
                    if (i < project.TeamAgents.Count - 1)
                        sb.Append(", ");
                }
            }
            sb.AppendLine("]");
            sb.AppendLine("  }");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private string JsonEscape(string value)
        {
            if (value == null)
                return "null";

            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private Project ParseProjectJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            json = json.Trim();
            if (!json.StartsWith("{"))
                return null;

            var project = new Project();
            int pos = 1;

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "id":
                            project.Id = ParseJsonString(json, ref pos);
                            break;
                        case "name":
                            project.Name = ParseJsonString(json, ref pos);
                            break;
                        case "description":
                            project.Description = ParseJsonString(json, ref pos);
                            break;
                        case "changelog":
                            project.ChangeLog = ParseJsonString(json, ref pos);
                            break;
                        case "currentversion":
                            project.CurrentVersion = ParseJsonString(json, ref pos);
                            break;
                        case "createdat":
                            string createdStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(createdStr, out var created))
                                project.CreatedAt = created;
                            break;
                        case "lastopeenedat":
                        case "lastopenat":
                        case "lastopenedat":
                            string openedStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(openedStr, out var opened))
                                project.LastOpenedAt = opened;
                            break;
                        case "ispinned":
                            project.IsPinned = ParseJsonBool(json, ref pos);
                            break;
                        case "defaultterminal":
                            project.DefaultTerminal = TerminalKindHelper.Normalize(ParseJsonString(json, ref pos));
                            break;
                        case "sourcecontrolaccountid":
                            // Consume-and-discard: SQLite is the source of truth for this field
                            // (no longer serialized). Existing on-disk project.json files may still
                            // carry the key, so we must still advance pos past its value, but we
                            // deliberately do NOT assign it — leaving Project.SourceControlAccountId
                            // null so the subsequent SaveRichProject COALESCE preserves the SQLite value.
                            ParseJsonString(json, ref pos);
                            break;
                        case "prompts":
                            project.Prompts = ParsePromptsArray(json, ref pos);
                            break;
                        case "team":
                            project.TeamAgents = ParseTeamObject(json, ref pos);
                            break;
                        case "hooks":
                            // Skip hooks - not used by ProjectService
                            SkipJsonValue(json, ref pos);
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            return project;
        }

        private List<Prompt> ParsePromptsArray(string json, ref int pos)
        {
            var prompts = new List<Prompt>();
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length || json[pos] != '[')
                return prompts;

            pos++; // skip '['

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == ']')
                    break;

                if (json[pos] == '{')
                {
                    var prompt = ParsePromptObject(json, ref pos);
                    if (prompt != null)
                        prompts.Add(prompt);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == ']')
                pos++;

            return prompts;
        }

        private Prompt ParsePromptObject(string json, ref int pos)
        {
            if (json[pos] != '{')
                return null;

            pos++;
            var prompt = new Prompt();

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "id":
                            prompt.Id = ParseJsonString(json, ref pos);
                            break;
                        case "category":
                            prompt.Category = ParseJsonString(json, ref pos);
                            break;
                        case "description":
                            prompt.Description = ParseJsonString(json, ref pos);
                            break;
                        case "text":
                            prompt.Text = ParseJsonString(json, ref pos);
                            break;
                        case "isglobal":
                            prompt.IsGlobal = ParseJsonBool(json, ref pos);
                            break;
                        case "createdat":
                            string dateStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(dateStr, out var dt))
                                prompt.CreatedAt = dt;
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == '}')
                pos++;

            return prompt;
        }

        private List<string> ParseTeamObject(string json, ref int pos)
        {
            var agents = new List<string>();
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length || json[pos] != '{')
            {
                SkipJsonValue(json, ref pos);
                return agents;
            }

            pos++; // skip '{'

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null && key.ToLowerInvariant() == "agents")
                {
                    agents = ParseStringArray(json, ref pos);
                }
                else
                {
                    SkipJsonValue(json, ref pos);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == '}')
                pos++;

            return agents;
        }

        private List<string> ParseStringArray(string json, ref int pos)
        {
            var items = new List<string>();
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length || json[pos] != '[')
                return items;

            pos++; // skip '['

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == ']')
                    break;

                // If the element is not a string (e.g. an object or array), skip it
                // to avoid an infinite loop where ParseJsonString returns null without advancing pos.
                if (json[pos] != '"' && json[pos] != 'n') // not a string or null literal
                {
                    SkipJsonValue(json, ref pos);
                }
                else
                {
                    string value = ParseJsonString(json, ref pos);
                    if (value != null)
                        items.Add(value);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == ']')
                pos++;

            return items;
        }

        private void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                pos++;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length)
                return null;

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "null")
            {
                pos += 4;
                return null;
            }

            if (json[pos] != '"')
                return null;

            pos++;
            var sb = new StringBuilder();

            while (pos < json.Length && json[pos] != '"')
            {
                if (json[pos] == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (pos + 4 < json.Length)
                            {
                                string hex = json.Substring(pos + 1, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                    sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default:
                            sb.Append(json[pos]);
                            break;
                    }
                }
                else
                {
                    sb.Append(json[pos]);
                }
                pos++;
            }

            if (pos < json.Length && json[pos] == '"')
                pos++;

            return sb.ToString();
        }

        private bool ParseJsonBool(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "true")
            {
                pos += 4;
                return true;
            }

            if (pos + 4 < json.Length && json.Substring(pos, 5) == "false")
            {
                pos += 5;
                return false;
            }

            return false;
        }

        private void SkipJsonValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                return;

            char c = json[pos];
            if (c == '"')
            {
                ParseJsonString(json, ref pos);
            }
            else if (c == '{')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '{')
                    {
                        depth++;
                        pos++;
                    }
                    else if (json[pos] == '}')
                    {
                        depth--;
                        pos++;
                    }
                    else if (json[pos] == '"')
                    {
                        ParseJsonString(json, ref pos);
                    }
                    else
                    {
                        pos++;
                    }
                }
            }
            else if (c == '[')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '[')
                    {
                        depth++;
                        pos++;
                    }
                    else if (json[pos] == ']')
                    {
                        depth--;
                        pos++;
                    }
                    else if (json[pos] == '"')
                    {
                        ParseJsonString(json, ref pos);
                    }
                    else
                    {
                        pos++;
                    }
                }
            }
            else
            {
                while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']')
                    pos++;
            }
        }

        #endregion

        #region IDisposable

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
                _projectDb?.Dispose();
            }
            _isDisposed = true;
        }

        #endregion
    }
}
