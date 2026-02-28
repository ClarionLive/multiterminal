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
    /// Service for managing projects with hybrid storage:
    /// - Central registry in %APPDATA%\MultiTerminal\projects.json (for discovery and recents)
    /// - Portable config in each project folder .claude/project.json (for portability)
    /// </summary>
    public class ProjectService : IDisposable
    {
        private readonly string _registryPath;
        private readonly string _appDataFolder;
        private List<ProjectRegistryEntry> _registry;
        private FileSystemWatcher _registryWatcher;
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
        /// Fired when the registry file changes externally (e.g., by Claude Code).
        /// </summary>
        public event EventHandler RegistryChangedExternally;

        public ProjectService()
        {
            var stackTrace = new System.Diagnostics.StackTrace(true);
            System.Diagnostics.Trace.WriteLine($"[ProjectService] Constructor called from:");
            for (int i = 0; i < Math.Min(5, stackTrace.FrameCount); i++)
            {
                var frame = stackTrace.GetFrame(i);
                System.Diagnostics.Trace.WriteLine($"  [{i}] {frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name} (Line {frame.GetFileLineNumber()})");
            }

            _appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MultiTerminal");

            try
            {
                if (!Directory.Exists(_appDataFolder))
                    Directory.CreateDirectory(_appDataFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to create app data folder: {ex.Message}");
            }

            _registryPath = Path.Combine(_appDataFolder, "projects.json");
            LoadRegistry();
            SetupRegistryWatcher();
        }

        private void SetupRegistryWatcher()
        {
            try
            {
                _registryWatcher = new FileSystemWatcher
                {
                    Path = _appDataFolder,
                    Filter = "projects.json",
                    // Include FileName to catch atomic writes (temp file + rename)
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName
                };

                // Subscribe to ALL events to catch any file operation
                _registryWatcher.Changed += OnRegistryFileChanged;
                _registryWatcher.Created += OnRegistryFileChanged;
                _registryWatcher.Renamed += OnRegistryFileRenamed;
                _registryWatcher.EnableRaisingEvents = true;

                System.Diagnostics.Trace.WriteLine($"[ProjectService] Registry watcher set up for: {_registryPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectService] Failed to set up registry watcher: {ex.Message}");
            }
        }

        private DateTime _lastRegistryChange = DateTime.MinValue;

        private void OnRegistryFileChanged(object sender, FileSystemEventArgs e)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectService] OnRegistryFileChanged triggered: {e.ChangeType}, {e.FullPath}");

            // Debounce - FileSystemWatcher can fire multiple times for a single change
            var now = DateTime.Now;
            if ((now - _lastRegistryChange).TotalMilliseconds < 500)
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectService] Debounced - ignoring");
                return;
            }
            _lastRegistryChange = now;

            try
            {
                // Reload the registry
                var oldCount = _registry.Count;
                LoadRegistry();
                var newCount = _registry.Count;

                System.Diagnostics.Trace.WriteLine($"[ProjectService] Registry reloaded: {oldCount} -> {newCount} projects");

                // Always fire the event when the file changes - let the UI decide what to do
                System.Diagnostics.Trace.WriteLine($"[ProjectService] Firing RegistryChangedExternally event");
                RegistryChangedExternally?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectService] Error processing registry change: {ex.Message}");
            }
        }

        private void OnRegistryFileRenamed(object sender, RenamedEventArgs e)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectService] Registry file renamed: {e.OldName} -> {e.Name}");
            // If a file was renamed TO projects.json, treat it as a change
            if (e.Name == "projects.json")
            {
                OnRegistryFileChanged(sender, e);
            }
        }

        #region Registry Operations

        /// <summary>
        /// Gets all registered projects from the central registry.
        /// </summary>
        public List<ProjectRegistryEntry> GetAllRegisteredProjects()
        {
            return _registry.ToList();
        }

        /// <summary>
        /// Gets recently opened projects (sorted by LastOpenedAt descending).
        /// </summary>
        public List<ProjectRegistryEntry> GetRecentProjects(int maxCount = 10)
        {
            return _registry
                .OrderByDescending(p => p.LastOpenedAt)
                .Take(maxCount)
                .ToList();
        }

        /// <summary>
        /// Gets pinned/favorite projects.
        /// </summary>
        public List<ProjectRegistryEntry> GetPinnedProjects()
        {
            return _registry
                .Where(p => p.IsPinned)
                .OrderBy(p => p.Name)
                .ToList();
        }

        /// <summary>
        /// Searches projects by name (case-insensitive partial match).
        /// </summary>
        public List<ProjectRegistryEntry> SearchProjects(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return GetAllRegisteredProjects();

            return _registry
                .Where(p => p.Name != null && p.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.Name)
                .ToList();
        }

        #endregion

        #region Project CRUD

        /// <summary>
        /// Loads a full project from its .claude/project.json file.
        /// Returns null if file doesn't exist or is invalid.
        /// </summary>
        public Project LoadProject(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            string configPath = GetProjectConfigPath(projectPath);
            if (!File.Exists(configPath))
                return null;

            try
            {
                string json = File.ReadAllText(configPath);
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
        /// Saves a project to its .claude/project.json file.
        /// Also updates the central registry.
        /// </summary>
        public void SaveProject(Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            if (string.IsNullOrEmpty(project.Path))
                throw new ArgumentException("Project path is required", nameof(project));

            // Ensure .claude folder exists
            string claudeFolder = Path.Combine(project.Path, ".claude");
            if (!Directory.Exists(claudeFolder))
                Directory.CreateDirectory(claudeFolder);

            // Save project config
            string configPath = GetProjectConfigPath(project.Path);
            string json = SerializeProjectJson(project);
            File.WriteAllText(configPath, json);

            // Update registry
            UpdateRegistry(project);

            ProjectUpdated?.Invoke(this, new ProjectEventArgs(project));
        }

        /// <summary>
        /// Registers a new project (creates .claude/project.json and adds to registry).
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
        /// Removes a project from the registry.
        /// Optionally deletes the .claude/project.json file.
        /// </summary>
        public void UnregisterProject(string projectId, bool deleteLocalConfig = false)
        {
            var entry = _registry.FirstOrDefault(p => p.Id == projectId);
            if (entry == null)
                return;

            Project project = null;
            if (deleteLocalConfig && !string.IsNullOrEmpty(entry.Path))
            {
                project = LoadProject(entry.Path);
                string configPath = GetProjectConfigPath(entry.Path);
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

            _registry.RemoveAll(p => p.Id == projectId);
            SaveRegistry();

            if (project != null)
                ProjectRemoved?.Invoke(this, new ProjectEventArgs(project));
        }

        /// <summary>
        /// Updates the LastOpenedAt timestamp when a project is opened.
        /// </summary>
        public void MarkProjectOpened(string projectId)
        {
            var entry = _registry.FirstOrDefault(p => p.Id == projectId);
            if (entry != null)
            {
                entry.LastOpenedAt = DateTime.Now;
                SaveRegistry();

                // Also update the project file
                var project = LoadProject(entry.Path);
                if (project != null)
                {
                    project.LastOpenedAt = DateTime.Now;
                    SaveProject(project);
                    ProjectOpened?.Invoke(this, new ProjectEventArgs(project));
                }
            }
        }

        /// <summary>
        /// Toggles the pinned state of a project.
        /// </summary>
        public void ToggleProjectPinned(string projectId)
        {
            var entry = _registry.FirstOrDefault(p => p.Id == projectId);
            if (entry != null)
            {
                entry.IsPinned = !entry.IsPinned;
                SaveRegistry();

                // Also update the project file
                var project = LoadProject(entry.Path);
                if (project != null)
                {
                    project.IsPinned = entry.IsPinned;
                    SaveProject(project);
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
        /// Checks if a discovered project is already in the registry.
        /// </summary>
        public bool IsProjectRegistered(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return false;

            return _registry.Any(p =>
                string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets a registry entry by path.
        /// </summary>
        public ProjectRegistryEntry GetRegistryEntryByPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            return _registry.FirstOrDefault(p =>
                string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validates that registry entries still point to valid project folders.
        /// Removes entries where the path no longer exists.
        /// </summary>
        public int CleanupRegistry()
        {
            int removed = _registry.RemoveAll(p =>
                string.IsNullOrEmpty(p.Path) || !Directory.Exists(p.Path));

            if (removed > 0)
                SaveRegistry();

            return removed;
        }

        #endregion

        #region Prompt Management

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

        private void UpdateRegistry(Project project)
        {
            var entry = _registry.FirstOrDefault(p => p.Id == project.Id);
            if (entry != null)
            {
                // Update existing entry
                entry.Name = project.Name;
                entry.Path = project.Path;
                entry.LastOpenedAt = project.LastOpenedAt;
                entry.IsPinned = project.IsPinned;
            }
            else
            {
                // Add new entry
                _registry.Add(ProjectRegistryEntry.FromProject(project));
            }
            SaveRegistry();
        }

        private void LoadRegistry()
        {
            _registry = new List<ProjectRegistryEntry>();

            if (!File.Exists(_registryPath))
                return;

            try
            {
                string json = File.ReadAllText(_registryPath);
                _registry = ParseRegistryJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to load registry: {ex.Message}");
            }
        }

        private void SaveRegistry()
        {
            try
            {
                string json = SerializeRegistryJson(_registry);
                File.WriteAllText(_registryPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectService] Failed to save registry: {ex.Message}");
            }
        }

        #endregion

        #region JSON Serialization (no external dependencies)

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

        private string SerializeRegistryJson(List<ProjectRegistryEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"projects\": [");

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"id\": {JsonEscape(e.Id)},");
                sb.AppendLine($"      \"name\": {JsonEscape(e.Name)},");
                sb.AppendLine($"      \"path\": {JsonEscape(e.Path)},");
                sb.AppendLine($"      \"lastOpenedAt\": {JsonEscape(e.LastOpenedAt.ToString("o"))},");
                sb.AppendLine($"      \"isPinned\": {(e.IsPinned ? "true" : "false")}");
                sb.Append("    }");
                if (i < entries.Count - 1)
                    sb.AppendLine(",");
                else
                    sb.AppendLine();
            }

            sb.AppendLine("  ]");
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
                        case "prompts":
                            project.Prompts = ParsePromptsArray(json, ref pos);
                            break;
                        case "team":
                            project.TeamAgents = ParseTeamObject(json, ref pos);
                            break;
                        case "hooks":
                            // Skip hooks - not currently used by ProjectService
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

        private List<ProjectRegistryEntry> ParseRegistryJson(string json)
        {
            var entries = new List<ProjectRegistryEntry>();
            if (string.IsNullOrWhiteSpace(json))
                return entries;

            json = json.Trim();
            if (!json.StartsWith("{"))
                return entries;

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

                if (key != null && key.ToLowerInvariant() == "projects")
                {
                    entries = ParseRegistryEntriesArray(json, ref pos);
                }
                else
                {
                    SkipJsonValue(json, ref pos);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            return entries;
        }

        private List<ProjectRegistryEntry> ParseRegistryEntriesArray(string json, ref int pos)
        {
            var entries = new List<ProjectRegistryEntry>();
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length || json[pos] != '[')
                return entries;

            pos++; // skip '['

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == ']')
                    break;

                if (json[pos] == '{')
                {
                    var entry = ParseRegistryEntry(json, ref pos);
                    if (entry != null)
                        entries.Add(entry);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == ']')
                pos++;

            return entries;
        }

        private ProjectRegistryEntry ParseRegistryEntry(string json, ref int pos)
        {
            if (json[pos] != '{')
                return null;

            pos++;
            var entry = new ProjectRegistryEntry();

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
                            entry.Id = ParseJsonString(json, ref pos);
                            break;
                        case "name":
                            entry.Name = ParseJsonString(json, ref pos);
                            break;
                        case "path":
                            entry.Path = ParseJsonString(json, ref pos);
                            break;
                        case "lastopeenedat":
                        case "lastopenat":
                        case "lastopenedat":
                            string dateStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(dateStr, out var dt))
                                entry.LastOpenedAt = dt;
                            break;
                        case "ispinned":
                            entry.IsPinned = ParseJsonBool(json, ref pos);
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

            return entry;
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

                string value = ParseJsonString(json, ref pos);
                if (value != null)
                    items.Add(value);

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
            if (!_isDisposed)
            {
                _registryWatcher?.Dispose();
                _registryWatcher = null;
                _isDisposed = true;
            }
        }

        #endregion
    }
}
