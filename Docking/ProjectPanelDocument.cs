using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.ProjectPanel;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Event arguments for project selection events.
    /// </summary>
    public class ProjectSelectedEventArgs : EventArgs
    {
        public Project Project { get; }
        public bool LaunchClaude { get; }

        /// <summary>
        /// Explicit terminal-kind override the user picked from the split-button
        /// dropdown. Null means 'use project.DefaultTerminal'. Valid values:
        /// <see cref="TerminalKindHelper.ClaudeCodeValue"/> or
        /// <see cref="TerminalKindHelper.CodexValue"/> — anything else should be
        /// coerced via <see cref="TerminalKindHelper.Normalize"/>.
        /// </summary>
        public string TerminalKindOverride { get; }

        public ProjectSelectedEventArgs(Project project, bool launchClaude = false, string terminalKindOverride = null)
        {
            Project = project;
            LaunchClaude = launchClaude;
            TerminalKindOverride = terminalKindOverride;
        }
    }

    /// <summary>
    /// DockContent panel for displaying project information using WebView2.
    /// </summary>
    public class ProjectPanelDocument : DockContent
    {
        // Services
        private ProjectService _projectService;
        private PromptService _promptService;
        private ProjectDatabase _projectDatabase;
        private ProjectContextService _projectContextService;
        private SessionLineageService _lineageService;
        private GatewayIntegrationService _gatewayService;
        private TerminalTheme _currentTheme;
        private string _currentWorkingDirectory;
        private Project _currentProject;

        // Header controls removed — project selector now lives in WebView2

        // WebView2 renderer
        private ProjectPanelRenderer _renderer;


        #region Events

        public event EventHandler<ProjectSelectedEventArgs> ProjectSelected;
        public event EventHandler<ProjectSelectedEventArgs> ProjectLaunchRequested;
        public event EventHandler<PromptEventArgs> PromptPasteRequested;
        public event EventHandler<PromptEventArgs> PromptEditRequested;
        public event EventHandler<PromptEventArgs> PromptDeleteRequested;
        public event EventHandler NewPromptRequested;
        public event EventHandler<NewPromptInCategoryEventArgs> NewPromptInCategoryRequested;
        public event EventHandler<string> ViewSessionRequested;

        /// <summary>
        /// Raised when the user saves the MCP servers picker and requests .mcp.json regeneration.
        /// Carries (ProjectId, SourcePath) so the handler can skip a SQLite project lookup —
        /// projects from the JSON registry may not be in the SQLite projects table yet.
        /// </summary>
        public event EventHandler<(string ProjectId, string SourcePath)> McpJsonWriteRequested;

        /// <summary>
        /// Raised when the user clicks a file in the file explorer.
        /// Carries the full file path so the host can open it in the FilePreviewPanel.
        /// </summary>
        public event EventHandler<string> FilePreviewRequested;

        #endregion

        public ProjectPanelDocument()
        {
            Text = "Project";
            TabText = "Project";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
            ShowHint = DockState.DockLeft;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);

            // WebView2 renderer for content (project selector now lives inside WebView2)
            _renderer = new ProjectPanelRenderer
            {
                Dock = DockStyle.Fill
            };
            _renderer.PastePromptRequested += OnRendererPastePrompt;
            _renderer.CopyPathRequested += OnRendererCopyPath;
            _renderer.Ready += OnRendererReady;
            _renderer.OpenSessionRequested += OnOpenSessionRequested;
            _renderer.SearchSessionsRequested += OnSearchSessionsRequested;
            _renderer.SyncSessionsRequested += OnSyncSessionsRequested;
            _renderer.GetSessionMessagesRequested += OnGetSessionMessagesRequested;
            _renderer.FieldUpdateRequested += OnFieldUpdateRequested;
            _renderer.AssociationUpdateRequested += OnAssociationUpdateRequested;
            _renderer.RefreshAssociationsRequested += OnRefreshAssociationsRequested;
            _renderer.LaunchRequested += OnRendererLaunchRequested;
            _renderer.WriteMcpJsonRequested += OnWriteMcpJsonRequested;
            _renderer.AvailableMcpServersRequested += OnAvailableMcpServersRequested;
            _renderer.AvailableSkillsRequested += OnAvailableSkillsRequested;
            _renderer.AvailableSpecialistAgentsRequested += OnAvailableSpecialistAgentsRequested;
            _renderer.SelectProjectRequested += OnSelectProjectRequested;
            _renderer.NewProjectRequested += OnNewProjectRequested;
            _renderer.ProjectListRequested += OnProjectListRequested;
            _renderer.ListDirectoryRequested += OnListDirectoryRequested;
            _renderer.ReadFileRequested += OnReadFileRequested;

            Controls.Add(_renderer);

            ResumeLayout(false);
        }


        public void SetServices(ProjectService projectService, PromptService promptService)
        {
            _projectService = projectService;
            _promptService = promptService;
        }

        /// <summary>
        /// Inject ProjectDatabase (and build ProjectContextService) for field/association editing.
        /// Call this from MainForm after SetServices.
        /// </summary>
        public void SetProjectDatabase(ProjectDatabase projectDatabase)
        {
            _projectDatabase = projectDatabase;
            _projectContextService = new ProjectContextService(projectDatabase);
        }

        public void SetGatewayService(GatewayIntegrationService gatewayService)
        {
            _gatewayService = gatewayService;
        }

        public void SetSessionLineageService(SessionLineageService lineageService)
        {
            _lineageService = lineageService;
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] SessionLineageService injected from MainForm");
        }

        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;
            bool isDark = theme.IsDark;

            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);

            _renderer?.SetTheme(isDark);
        }

        /// <summary>
        /// Sets the font size for the project panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            _renderer?.SetFontSize(size);
        }

        public async void RefreshForProject(Project project)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] RefreshForProject called: {project?.Name ?? "null"}");

            _currentProject = project;

            if (project == null)
            {
                _renderer?.ShowWelcome();
                return;
            }

            // Overlay with full SQLite data so SQLite-only fields (e.g. team_lead) are populated
            // even when the project was loaded from JSON (which lacks those fields).
            if (_projectDatabase != null && !string.IsNullOrEmpty(project.Id))
            {
                var richProject = _projectDatabase.GetRichProject(project.Id);
                if (richProject != null)
                {
                    // Preserve Prompts from the caller — they come from JSON/PromptService
                    richProject.Prompts = project.Prompts;
                    project = richProject;
                    _currentProject = richProject;
                }
            }

            // Show project immediately with basic info (no stats yet)
            var allPrompts = new List<Prompt>();
            if (project.Prompts != null)
            {
                allPrompts.AddRange(project.Prompts);
            }
            if (_promptService != null)
            {
                var globalPrompts = _promptService.GetAllPrompts(project.Path)
                    .Where(p => p.IsGlobal)
                    .ToList();
                allPrompts.AddRange(globalPrompts);
            }

            var projectWithAllPrompts = new Project
            {
                Id = project.Id,
                Name = project.Name,
                Path = project.Path,
                Description = project.Description,
                ChangeLog = project.ChangeLog,
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt,
                LastOpenedAt = project.LastOpenedAt,
                IsPinned = project.IsPinned,
                CreatedBy = project.CreatedBy,
                Prompts = allPrompts,
                // Project info fields
                ProjectType = project.ProjectType,
                CurrentVersion = project.CurrentVersion,
                Icon = project.Icon,
                IconColor = project.IconColor,
                // Path fields (new)
                SourcePath = project.SourcePath,
                DeployPath = project.DeployPath,
                BuildOutputPath = project.BuildOutputPath,
                // Commands
                BuildCommand = project.BuildCommand,
                DeployCommand = project.DeployCommand,
                LaunchCommand = project.LaunchCommand,
                // Git
                GitRepoUrl = project.GitRepoUrl,
                GitDefaultBranch = project.GitDefaultBranch,
                GitAutoCommit = project.GitAutoCommit,
                // Team lead
                TeamLead = project.TeamLead
            };

            // Show project immediately without stats (to avoid UI freeze)
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Calling renderer.ShowProject for {projectWithAllPrompts.Name} (initial, no stats)");
            _renderer?.ShowProject(projectWithAllPrompts, null);

            // Fetch team lead options once and send to dropdown (reused after stats re-render)
            var teamLeadProfiles = SendTeamLeadOptions();

            // Fetch source control account options once and send to Git-section dropdown
            var sourceAccountOptions = SendSourceAccountOptions();

            // Send available agents for picker popup (reused after stats re-render)
            var availableAgents = SendAvailableAgents();

            // Send gateway server list for picker popup (reused after stats re-render)
            var availableMcpServers = SendAvailableMcpServers();

            // Send available skills for picker popup (reused after stats re-render)
            var availableSkills = SendAvailableSkills();

            // Send available specialist agents for picker popup (reused after stats re-render)
            var availableSpecialistAgents = SendAvailableSpecialistAgents();

            // Send association data if we have database access
            if (_projectContextService != null && !string.IsNullOrEmpty(project.Id))
            {
                var context = await Task.Run(() => _projectContextService.GetProjectContext(project.Id));
                _renderer?.ShowAssociations(context);
            }

            // Calculate stats asynchronously in background
            var stats = await Task.Run(() => CalculateProjectStats(project.Path));

            // Update renderer with stats (back on UI thread)
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Updating renderer with stats for {projectWithAllPrompts.Name}");
            _renderer?.ShowProject(projectWithAllPrompts, stats);

            // Re-send cached team lead options (stats re-render clears the dropdown options)
            if (teamLeadProfiles != null)
                _renderer?.SendTeamLeadOptions(teamLeadProfiles);

            // Re-send cached source control account options (stats re-render clears the dropdown)
            if (sourceAccountOptions != null)
                _renderer?.SendSourceAccountOptions(sourceAccountOptions);

            // Re-send available agents (stats re-render clears the cached data)
            if (availableAgents != null)
                _renderer?.SendAvailableAgents(availableAgents);

            // Re-send gateway server list (stats re-render clears the cached data)
            if (availableMcpServers != null)
                _renderer?.SendAvailableMcpServers(availableMcpServers);

            // Re-send available skills (stats re-render clears the cached data)
            if (availableSkills != null)
                _renderer?.SendAvailableSkills(availableSkills);

            // Re-send available specialist agents (stats re-render clears the cached data)
            if (availableSpecialistAgents != null)
                _renderer?.SendAvailableSpecialistAgents(availableSpecialistAgents);

            // Send project list so the in-panel project selector popup has data
            SendProjectListToRenderer();

            // Sessions section hidden for now (feature incomplete)
            // await LoadSessionsForProjectAsync(project.Path);
        }

        public void ShowClaudeDetectedState(string workingDirectory)
        {
            _currentWorkingDirectory = workingDirectory;
            _currentProject = null;
            _renderer?.ShowNotRegistered(workingDirectory);
        }

        public void RefreshPrompts(string workingDirectory)
        {
            _currentWorkingDirectory = workingDirectory;
            _currentProject = null;
            _renderer?.ShowWelcome();
        }

        private ProjectStats CalculateProjectStats(string projectPath)
        {
            var stats = new ProjectStats();

            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return stats;

            try
            {
                // Count files (excluding common ignore patterns — uses shared IgnoreFolders)
                var extensions = new[] { ".cs", ".js", ".ts", ".html", ".css", ".json", ".xml", ".md", ".py", ".clw", ".inc", ".equ" };

                int fileCount = 0;
                int lineCount = 0;

                var files = Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var dir = Path.GetDirectoryName(f);
                        return !IgnoreFolders.Any(ig => dir.Contains(Path.DirectorySeparatorChar + ig + Path.DirectorySeparatorChar) ||
                                                         dir.EndsWith(Path.DirectorySeparatorChar + ig));
                    })
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Take(1000); // Limit for performance

                foreach (var file in files)
                {
                    fileCount++;
                    try
                    {
                        lineCount += File.ReadLines(file).Count();
                    }
                    catch { }
                }

                stats.FileCount = fileCount;
                stats.LinesOfCode = lineCount;

                // Calculate days since project created
                var claudeFolder = Path.Combine(projectPath, ".claude");
                var projectJson = Path.Combine(claudeFolder, "project.json");
                if (File.Exists(projectJson))
                {
                    var created = File.GetCreationTime(projectJson);
                    stats.DaysOld = (int)(DateTime.Now - created).TotalDays;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error calculating stats: {ex.Message}");
            }

            return stats;
        }

        /// <summary>
        /// Fetches team lead profiles and sends them to the panel dropdown.
        /// Returns the fetched profiles so the caller can reuse them (e.g. after a stats re-render)
        /// without hitting the database again. Returns null if unavailable.
        /// </summary>
        private List<(string Id, string DisplayName, string AvatarUrl)> SendTeamLeadOptions()
        {
            if (_renderer == null || _projectDatabase == null) return null;
            try
            {
                var profiles = _projectDatabase.GetTeamLeadProfiles();
                _renderer.SendTeamLeadOptions(profiles);
                return profiles;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendTeamLeadOptions error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches the configured source control accounts and sends them to the panel's
        /// Git-section dropdown. Returns the fetched options so the caller can re-send them
        /// after a stats re-render without re-querying. Returns null if unavailable.
        /// Reuses the ProjectDatabase connection (same multiterminal.db file as
        /// source_control_accounts) rather than opening a second connection.
        /// </summary>
        private List<(string Id, string DisplayName)> SendSourceAccountOptions()
        {
            if (_renderer == null || _projectDatabase?.Connection == null) return null;
            try
            {
                var service = new SourceControlAccountService(_projectDatabase.Connection);
                var options = new List<(string Id, string DisplayName)>();
                foreach (var account in service.GetAll())
                    options.Add((account.Id, account.DisplayName));
                _renderer.SendSourceAccountOptions(options);
                return options;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendSourceAccountOptions error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches all profile summaries and sends them to the panel for the agents picker popup.
        /// Returns the fetched profiles so the caller can reuse them after stats re-render.
        /// </summary>
        private List<(string Id, string DisplayName, string Role, string PreferredModel)> SendAvailableAgents()
        {
            if (_renderer == null || _projectDatabase == null) return null;
            try
            {
                var profiles = _projectDatabase.GetAllProfileSummaries();
                _renderer.SendAvailableAgents(profiles);
                return profiles;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendAvailableAgents error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches all gateway server entries and sends them to the panel for the MCP server picker popup.
        /// Returns the fetched entries so the caller can reuse them after stats re-render.
        /// </summary>
        private List<GatewayServerDto> SendAvailableMcpServers()
        {
            if (_renderer == null) return null;
            try
            {
                var entries = _gatewayService?.GetAllGatewayServers() ?? new List<GatewayServerDto>();
                _renderer.SendAvailableMcpServers(entries);
                return entries;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendAvailableMcpServers error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Discovers available skill names by scanning ~/.claude/skills/ subdirectories.
        /// Returns the list so the caller can reuse after stats re-render.
        /// </summary>
        private List<string> SendAvailableSkills()
        {
            if (_renderer == null) return null;
            try
            {
                var skillsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "skills");
                var skillNames = new List<string>();
                if (Directory.Exists(skillsDir))
                {
                    foreach (var dir in Directory.GetDirectories(skillsDir))
                    {
                        skillNames.Add(Path.GetFileName(dir));
                    }
                    skillNames.Sort(StringComparer.OrdinalIgnoreCase);
                }
                _renderer.SendAvailableSkills(skillNames);
                return skillNames;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendAvailableSkills error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Discovers available specialist agent types by scanning .claude/agents/*.md in the project dir.
        /// Returns the list so the caller can reuse after stats re-render.
        /// </summary>
        private List<(string AgentType, string Description)> SendAvailableSpecialistAgents()
        {
            if (_renderer == null) return null;
            try
            {
                var agentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Scan user-level ~/.claude/agents/ first (global agents)
                var userAgentsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "agents");
                ScanAgentsDirectory(userAgentsDir, agentMap);

                // Scan project-level .claude/agents/ (project agents override user-level)
                var projectPath = _currentProject?.SourcePath ?? _currentProject?.Path;
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var projectAgentsDir = Path.Combine(projectPath, ".claude", "agents");
                    ScanAgentsDirectory(projectAgentsDir, agentMap);
                }

                var agents = agentMap
                    .Select(kv => (AgentType: kv.Key, Description: kv.Value))
                    .OrderBy(a => a.AgentType, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _renderer.SendAvailableSpecialistAgents(agents);
                return agents;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SendAvailableSpecialistAgents error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Scans a directory for *.md agent definitions and adds them to the map.
        /// Later calls override earlier entries (project-level overrides user-level).
        /// </summary>
        private void ScanAgentsDirectory(string agentsDir, Dictionary<string, string> agentMap)
        {
            if (string.IsNullOrEmpty(agentsDir) || !Directory.Exists(agentsDir)) return;
            foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
            {
                var agentType = Path.GetFileNameWithoutExtension(file);
                var desc = "";
                try
                {
                    using var reader = new StreamReader(file);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim().TrimStart('#').Trim();
                        if (!string.IsNullOrEmpty(line))
                        {
                            desc = line.Length > 80 ? line.Substring(0, 80) + "..." : line;
                            break;
                        }
                    }
                }
                catch { /* ignore read errors */ }
                agentMap[agentType] = desc;
            }
        }

        private void OnFieldUpdateRequested(object sender, FieldUpdateEventArgs e)
        {
            if (_currentProject == null || _projectDatabase == null || string.IsNullOrEmpty(e.Field))
            {
                _renderer?.SendFieldSaved(e.Field, false);
                return;
            }

            try
            {
                bool success = _projectDatabase.UpdateProjectField(_currentProject.Id, e.Field, e.Value);
                _renderer?.SendFieldSaved(e.Field, success);

                if (success)
                {
                    System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Field '{e.Field}' updated to '{e.Value}' for project {_currentProject.Id}");
                    UpdateProjectFieldInMemory(e.Field, e.Value);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnFieldUpdateRequested error: {ex.Message}");
                _renderer?.SendFieldSaved(e.Field, false);
            }
        }

        /// <summary>
        /// Keeps _currentProject in sync after an in-place field edit so that subsequent
        /// ShowProject calls (triggered by async stats completion) display the latest values.
        /// </summary>
        private void UpdateProjectFieldInMemory(string camelCaseField, string value)
        {
            if (_currentProject == null) return;
            switch (camelCaseField)
            {
                case "name": _currentProject.Name = value; break;
                case "description": _currentProject.Description = value; break;
                case "projectType": _currentProject.ProjectType = value; break;
                case "currentVersion": _currentProject.CurrentVersion = value; break;
                case "icon": _currentProject.Icon = value; break;
                case "iconColor": _currentProject.IconColor = value; break;
                case "sourcePath": _currentProject.SourcePath = value; break;
                case "deployPath": _currentProject.DeployPath = value; break;
                case "buildOutputPath": _currentProject.BuildOutputPath = value; break;
                case "buildCommand": _currentProject.BuildCommand = value; break;
                case "deployCommand": _currentProject.DeployCommand = value; break;
                case "launchCommand": _currentProject.LaunchCommand = value; break;
                case "gitRepoUrl": _currentProject.GitRepoUrl = value; break;
                case "gitDefaultBranch": _currentProject.GitDefaultBranch = value; break;
                case "gitAutoCommit": _currentProject.GitAutoCommit = value == "true" || value == "1"; break;
                case "sourceControlAccountId": _currentProject.SourceControlAccountId = value; break;
                case "changeLog": _currentProject.ChangeLog = value; break;
                case "teamLead": _currentProject.TeamLead = value; break;
                case "createdBy": _currentProject.CreatedBy = value; break;
                case "defaultTerminal":
                    _currentProject.DefaultTerminal = MultiTerminal.Models.TerminalKindHelper.Normalize(value);
                    break;
            }
        }

        private async void OnRefreshAssociationsRequested(object sender, EventArgs e)
        {
            if (_currentProject == null || _projectContextService == null) return;
            try
            {
                var context = await Task.Run(() => _projectContextService.GetProjectContext(_currentProject.Id));
                _renderer?.ShowAssociations(context);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnRefreshAssociationsRequested error: {ex.Message}");
            }
        }

        private void OnAssociationUpdateRequested(object sender, AssociationUpdateEventArgs e)
        {
            if (_currentProject == null || _projectDatabase == null
                || string.IsNullOrEmpty(e.TableName) || string.IsNullOrEmpty(e.Action))
            {
                _renderer?.SendAssociationSaved(e.TableName, e.Action, false);
                return;
            }

            // Declare outside try so catch block can reference them for error logging
            string tableName = e.TableName;
            string action = e.Action;

            try
            {
                bool success = false;
                int newId = 0;
                string projectId = _currentProject.Id;
                string itemJson = e.ItemJson;

                switch (tableName)
                {
                    case "agents":
                        (success, newId) = HandleAgentCrud(projectId, action, itemJson);
                        break;
                    case "mcpServers":
                        (success, newId) = HandleMcpServerCrud(projectId, action, itemJson);
                        break;
                    case "specialistAgents":
                        (success, newId) = HandleSpecialistAgentCrud(projectId, action, itemJson);
                        break;
                    case "projectPaths":
                        (success, newId) = HandlePathCrud(projectId, action, itemJson);
                        break;
                    case "dbPrompts":
                        (success, newId) = HandlePromptCrud(projectId, action, itemJson);
                        break;
                    case "skills":
                        (success, newId) = HandleSkillCrud(projectId, action, itemJson);
                        break;
                    default:
                        System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Unknown association tableName: {tableName}");
                        break;
                }

                _renderer?.SendAssociationSaved(tableName, action, success, newId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnAssociationUpdateRequested error ({tableName}/{action}): {ex.Message}");
                _renderer?.SendAssociationSaved(tableName, action, false);
            }
        }

        private (bool success, int newId) HandleAgentCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectAgent(projectId, GetJsonString(el, "agentName")), 0);

            _projectDatabase.SaveProjectAgent(new MultiTerminal.Models.ProjectAgent
            {
                ProjectId = projectId,
                AgentName = GetJsonString(el, "agentName"),
                Role = GetJsonString(el, "role"),
                PreferredModel = GetJsonString(el, "preferredModel")
            });
            return (true, 0);
        }

        private (bool success, int newId) HandleMcpServerCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
            {
                bool deleted = _projectDatabase.DeleteProjectMcpServer(projectId, GetJsonString(el, "serverName"));
                if (deleted) SyncGatewayProfile(projectId);
                return (deleted, 0);
            }

            if (action == "update")
            {
                int id = GetJsonInt(el, "id");
                if (id > 0 && el.TryGetProperty("isEnabled", out _))
                {
                    bool updated = _projectDatabase.UpdateMcpServerEnabled(id, GetJsonBool(el, "isEnabled"));
                    if (updated) SyncGatewayProfile(projectId);
                    return (updated, 0);
                }
            }

            _projectDatabase.SaveProjectMcpServer(new MultiTerminal.Models.ProjectMcpServer
            {
                ProjectId = projectId,
                ServerName = GetJsonString(el, "serverName"),
                IsEnabled = GetJsonBool(el, "isEnabled", defaultValue: true)
            });
            SyncGatewayProfile(projectId);
            return (true, 0);
        }

        /// <summary>
        /// Syncs the project's enabled MCP servers to the gateway profile so changes
        /// take effect immediately (next tools/list call).
        /// </summary>
        private void SyncGatewayProfile(string projectId)
        {
            try
            {
                if (_gatewayService == null || !_gatewayService.IsGatewayInstalled()) return;
                string projectName = _currentProject?.Name;
                if (string.IsNullOrWhiteSpace(projectName)) return;

                var enabledNames = _projectDatabase.GetEnabledMcpServerNamesForProject(projectId);
                _gatewayService.SyncProjectProfile(projectId, projectName, enabledNames);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SyncGatewayProfile failed: {ex.Message}");
            }
        }

        private (bool success, int newId) HandleSpecialistAgentCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectSpecialistAgent(projectId, GetJsonString(el, "agentType")), 0);

            if (action == "update")
            {
                int id = GetJsonInt(el, "id");
                if (id > 0 && el.TryGetProperty("isEnabled", out _))
                    return (_projectDatabase.UpdateSpecialistAgentEnabled(id, GetJsonBool(el, "isEnabled")), 0);
            }

            _projectDatabase.SaveProjectSpecialistAgent(new MultiTerminal.Models.ProjectSpecialistAgent
            {
                ProjectId = projectId,
                AgentType = GetJsonString(el, "agentType"),
                IsEnabled = GetJsonBool(el, "isEnabled", defaultValue: true),
                CustomPrompt = GetJsonString(el, "customPrompt")
            });
            return (true, 0);
        }

        private (bool success, int newId) HandlePathCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectPath(GetJsonInt(el, "id")), 0);

            int id = _projectDatabase.SaveProjectPath(new MultiTerminal.Models.ProjectPath
            {
                Id = GetJsonInt(el, "id"),
                ProjectId = projectId,
                PathType = GetJsonString(el, "pathType"),
                PathValue = GetJsonString(el, "pathValue"),
                Description = GetJsonString(el, "description")
            });
            return (true, id);
        }

        private (bool success, int newId) HandlePromptCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectPrompt(GetJsonInt(el, "id")), 0);

            int id = _projectDatabase.SaveProjectPrompt(new MultiTerminal.Models.ProjectPromptEntry
            {
                Id = GetJsonInt(el, "id"),
                ProjectId = projectId,
                PromptType = GetJsonString(el, "promptType"),
                PromptText = GetJsonString(el, "promptText"),
                DisplayOrder = GetJsonInt(el, "displayOrder")
            });
            return (true, id);
        }

        private (bool success, int newId) HandleSkillCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectSkill(projectId, GetJsonString(el, "skillName")), 0);

            if (action == "update")
            {
                int id = GetJsonInt(el, "id");
                if (id > 0 && el.TryGetProperty("isEnabled", out _))
                    return (_projectDatabase.UpdateSkillEnabled(id, GetJsonBool(el, "isEnabled")), 0);
            }

            _projectDatabase.SaveProjectSkill(new MultiTerminal.Models.ProjectSkill
            {
                ProjectId = projectId,
                SkillName = GetJsonString(el, "skillName"),
                IsEnabled = GetJsonBool(el, "isEnabled", defaultValue: true)
            });
            return (true, 0);
        }

        // ============================================================
        // MCP .mcp.json write handlers
        // ============================================================

        private void OnWriteMcpJsonRequested(object sender, EventArgs e)
        {
            if (_currentProject == null || string.IsNullOrEmpty(_currentProject.Id))
            {
                _renderer?.SendMcpJsonWriteResult(false, "No project selected");
                return;
            }
            // Pass source path so the handler can write without a SQLite project lookup.
            string sourcePath = _currentProject.SourcePath ?? _currentProject.Path;
            McpJsonWriteRequested?.Invoke(this, (_currentProject.Id, sourcePath));
        }

        /// <summary>
        /// Called by MainForm after the .mcp.json write completes.
        /// Forwards the result to JS so the user sees a success/error notification.
        /// </summary>
        public void NotifyMcpJsonWriteResult(bool success, string error = null)
        {
            _renderer?.SendMcpJsonWriteResult(success, error);
        }

        private void OnAvailableMcpServersRequested(object sender, EventArgs e)
        {
            SendAvailableMcpServers();
        }

        private void OnAvailableSkillsRequested(object sender, EventArgs e)
        {
            SendAvailableSkills();
        }

        private void OnAvailableSpecialistAgentsRequested(object sender, EventArgs e)
        {
            SendAvailableSpecialistAgents();
        }

        // Parse itemJson once and return the root element for use by all Handle*Crud methods.
        private static JsonElement? TryParseItemJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                // JsonDocument is disposed by the caller via the using block in each Handle* method.
                // We return a clone of RootElement so the doc can be safely disposed.
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch { }
            return null;
        }

        private static string GetJsonString(JsonElement root, string key)
        {
            return root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;
        }

        private static int GetJsonInt(JsonElement root, string key)
        {
            return root.TryGetProperty(key, out var el) && el.TryGetInt32(out int v) ? v : 0;
        }

        private static bool GetJsonBool(JsonElement root, string key, bool defaultValue = false)
        {
            if (!root.TryGetProperty(key, out var el)) return defaultValue;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String)
                return string.Equals(el.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            return defaultValue;
        }

        private void OnRendererPastePrompt(object sender, string promptId)
        {
            // Find the prompt by ID
            Prompt prompt = null;

            if (_currentProject?.Prompts != null)
            {
                prompt = _currentProject.Prompts.FirstOrDefault(p => p.Id == promptId);
            }

            if (prompt == null && _promptService != null)
            {
                var globalPrompts = _promptService.GetAllPrompts(_currentWorkingDirectory);
                prompt = globalPrompts?.FirstOrDefault(p => p.Id == promptId);
            }

            if (prompt != null)
            {
                PromptPasteRequested?.Invoke(this, new PromptEventArgs(prompt));
            }
        }

        private void OnRendererCopyPath(object sender, string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    Clipboard.SetText(path);
                }
                catch { }
            }
        }

        private void OnRendererLaunchRequested(object sender, MultiTerminal.ProjectPanel.LaunchRequestedEventArgs e)
        {
            if (_currentProject != null)
            {
                ProjectLaunchRequested?.Invoke(this,
                    new ProjectSelectedEventArgs(_currentProject, launchClaude: true, terminalKindOverride: e?.TerminalKindOverride));
            }
        }

        private void OnRendererReady(object sender, EventArgs e)
        {
            // Refresh with current state when renderer is ready
            if (_currentProject != null)
            {
                RefreshForProject(_currentProject);
            }
            else
            {
                _renderer?.ShowWelcome();
            }
        }

        #region Session Management

        private async Task LoadSessionsForProjectAsync(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                _renderer?.ClearSessions();
                return;
            }

            var lineageService = _lineageService;
            if (lineageService == null)
            {
                System.Diagnostics.Debug.WriteLine("[ProjectPanel] SessionLineageService not available");
                _renderer?.ClearSessions();
                return;
            }

            var claudeFolder = SessionLineageService.GetClaudeProjectFolder(projectPath);
            if (claudeFolder == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] No Claude project folder found for {projectPath}");
                _renderer?.ClearSessions();
                return;
            }

            try
            {
                // Load sessions from database on background thread
                var sessionSummaries = await Task.Run(() =>
                {
                    var lineageRecords = lineageService.GetSessionsByFolder(claudeFolder, 20);
                    return lineageRecords
                        .Select(s => new SessionSummary
                        {
                            SessionId = s.SessionId,
                            Summary = s.Summary,
                            FirstPrompt = null,
                            MessageCount = 0,
                            Created = DateTime.TryParse(s.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started) ? started : DateTime.MinValue,
                            Modified = DateTime.TryParse(s.EndedAt ?? s.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ended) ? ended : DateTime.MinValue,
                            GitBranch = null
                        })
                        .ToList();
                });

                // Back on UI thread - update renderer
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Loaded {sessionSummaries.Count} sessions from lineage for {projectPath}");
                _renderer?.ShowSessions(sessionSummaries);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Failed to load sessions: {ex.Message}");
                _renderer?.ClearSessions();
            }
        }


        private void OnOpenSessionRequested(object sender, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OpenSessionRequested: {sessionId}");

            // Raise the event for MainForm to handle session viewing
            ViewSessionRequested?.Invoke(this, sessionId);
        }

        private async void OnSearchSessionsRequested(object sender, string query)
        {
            if (_currentProject == null || string.IsNullOrWhiteSpace(query))
            {
                // If no query, reload recent sessions
                if (_currentProject != null)
                {
                    await LoadSessionsForProjectAsync(_currentProject.Path);
                }
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SearchSessionsRequested: {query}");

            try
            {
                var lineageService = _lineageService;
                if (lineageService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ProjectPanel] SessionLineageService not available for search");
                    return;
                }

                var results = await Task.Run(() =>
                {
                    var messages = lineageService.SearchSessionMessages(query: query, limit: 50);
                    return messages.Select(r => new MessageSearchResult
                    {
                        SessionId = r.SessionId,
                        SessionSummary = null,
                        MessageId = r.DbId,
                        Role = r.Role,
                        ContentSnippet = r.Content?.Length > 200 ? r.Content.Substring(0, 200) + "..." : r.Content,
                        Timestamp = DateTime.TryParse(r.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.MinValue
                    }).ToList();
                });

                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Found {results.Count} message matches for '{query}'");
                _renderer?.ShowSearchResults(results, query);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Failed to search sessions: {ex.Message}");
            }
        }

        private async void OnSyncSessionsRequested(object sender, EventArgs e)
        {
            if (_currentProject?.Path == null)
                return;

            System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SyncSessionsRequested for project: {_currentProject.Path}");

            try
            {
                var lineageService = _lineageService;
                if (lineageService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ProjectPanel] SessionLineageService not available for sync");
                    _renderer?.ShowSyncResult(0);
                    return;
                }

                var claudeFolder = SessionLineageService.GetClaudeProjectFolder(_currentProject.Path);
                if (claudeFolder == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProjectPanel] No Claude project folder for sync: {_currentProject.Path}");
                    _renderer?.ShowSyncResult(0);
                    return;
                }

                // Sync from Claude's storage to local database on background thread
                var syncResult = await Task.Run(() => lineageService.SyncNewSessions(claudeFolder));
                int count = syncResult.Imported;
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Synced {count} sessions to lineage database");

                // Refresh the display (back on UI thread)
                await LoadSessionsForProjectAsync(_currentProject.Path);

                // Show feedback
                _renderer?.ShowSyncResult(count);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Failed to sync sessions: {ex.Message}");
                _renderer?.ShowSyncResult(0);
            }
        }

        private async void OnGetSessionMessagesRequested(object sender, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || _currentProject?.Path == null)
            {
                _renderer?.ShowSessionMessages(sessionId, new List<SessionMessageSummary>());
                return;
            }

            try
            {
                var lineageService = _lineageService;
                if (lineageService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ProjectPanel] SessionLineageService not available");
                    _renderer?.ShowSessionMessages(sessionId, new List<SessionMessageSummary>());
                    return;
                }

                // Query messages from SQLite on background thread
                var summaries = await Task.Run(() =>
                {
                    var messages = lineageService.GetSessionMessagesBySessionId(sessionId, 500);

                    // Convert to summaries (only user and assistant messages)
                    return messages
                        .Where(m => m.Role == "user" || m.Role == "assistant")
                        .Take(50) // Limit for performance
                        .Select(m => new SessionMessageSummary
                        {
                            Role = m.Role,
                            Content = m.Content?.Length > 300 ? m.Content.Substring(0, 300) + "..." : m.Content,
                            Timestamp = DateTime.TryParse(m.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.MinValue
                        })
                        .ToList();
                });

                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Loaded {summaries.Count} messages for session {sessionId}");
                _renderer?.ShowSessionMessages(sessionId, summaries);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Failed to load session messages: {ex.Message}");
                _renderer?.ShowSessionMessages(sessionId, new List<SessionMessageSummary>());
            }
        }

        #endregion

        /// <summary>
        /// Handles JS request to switch to a different project by ID.
        /// Loads from SQLite first, falls back to JSON, then fires ProjectSelected.
        /// </summary>
        private void OnSelectProjectRequested(object sender, string projectId)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] SelectProject requested: {projectId}");

            Project project = null;
            if (_projectDatabase != null && !string.IsNullOrEmpty(projectId))
            {
                project = _projectDatabase.GetRichProject(projectId);
                if (project != null)
                    System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Project loaded from SQLite: {projectId}");
            }

            // Fall back to JSON registry
            if (project == null)
            {
                var allEntries = _projectService?.GetAllRegisteredProjects() ?? new List<ProjectRegistryEntry>();
                var entry = allEntries.FirstOrDefault(e => e.Id == projectId);
                if (entry != null)
                {
                    project = _projectService?.LoadProject(entry.Path);
                    if (project != null)
                        System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Project loaded from JSON: {entry.Path}");
                }
            }

            if (project != null)
            {
                ProjectSelected?.Invoke(this, new ProjectSelectedEventArgs(project));
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Failed to load project: {projectId}");
            }
        }

        /// <summary>
        /// Handles JS request to create a new project (the "+" button in WebView2).
        /// </summary>
        private void OnNewProjectRequested(object sender, EventArgs e)
        {
            _renderer?.ShowNewProjectHint();
        }

        /// <summary>
        /// Handles JS request for the project list (to populate the selector popup).
        /// </summary>
        private void OnProjectListRequested(object sender, EventArgs e)
        {
            SendProjectListToRenderer();
        }

        /// <summary>
        /// Sends all registered projects (with descriptions from SQLite) to the WebView2 panel.
        /// </summary>
        private void SendProjectListToRenderer()
        {
            if (_renderer == null) return;

            var allEntries = _projectService?.GetAllRegisteredProjects() ?? new List<ProjectRegistryEntry>();
            var projectList = new List<(string Id, string Name, string Description, string Path, string Icon, string IconColor)>();

            foreach (var entry in allEntries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                string description = "";
                string icon = "";
                string iconColor = "";

                // Try to get description from SQLite
                if (_projectDatabase != null && !string.IsNullOrEmpty(entry.Id))
                {
                    var richProject = _projectDatabase.GetRichProject(entry.Id);
                    if (richProject != null)
                    {
                        description = richProject.Description ?? "";
                        icon = richProject.Icon ?? "";
                        iconColor = richProject.IconColor ?? "";
                    }
                }

                projectList.Add((entry.Id, entry.Name, description, entry.Path, icon, iconColor));
            }

            _renderer.SendProjectList(projectList, _currentProject?.Id);
        }

        /// <summary>
        /// Handles JS request to list directory contents for the file explorer.
        /// </summary>
        /// <summary>
        /// Validates that the given path is within the current project root.
        /// Prevents path traversal attacks from the WebView2 JS layer.
        /// </summary>
        private bool IsPathWithinProject(string requestedPath)
        {
            var projectRoot = _currentProject?.Path;
            if (string.IsNullOrEmpty(projectRoot)) return false;
            try
            {
                var normalizedPath = Path.GetFullPath(requestedPath);
                var normalizedRoot = Path.GetFullPath(projectRoot);
                // Ensure trailing separator for prefix check
                if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    normalizedRoot += Path.DirectorySeparatorChar;
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFullPath(requestedPath).Equals(Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static readonly HashSet<string> IgnoreFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", "node_modules", ".git", ".vs", "packages", "__pycache__", ".idea", ".svn", ".hg" };

        private void OnListDirectoryRequested(object sender, string dirPath)
        {
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;
            if (!IsPathWithinProject(dirPath)) return;

            try
            {
                var entries = new List<(string Name, string FullPath, bool IsDirectory, long Size)>();

                // Directories first, sorted (use IgnoreFolders instead of hiding all dotfiles)
                foreach (var dir in Directory.GetDirectories(dirPath)
                    .Select(d => new DirectoryInfo(d))
                    .Where(d => !IgnoreFolders.Contains(d.Name))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add((dir.Name, dir.FullName, true, 0));
                }

                // Files, sorted
                foreach (var file in Directory.GetFiles(dirPath)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add((file.Name, file.FullName, false, file.Length));
                }

                _renderer?.SendDirectoryListing(dirPath, entries);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnListDirectoryRequested error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles JS request to read a file for the file viewer.
        /// Sends content for text files, or a base64 data URL for images.
        /// </summary>
        private void OnReadFileRequested(object sender, string filePath)
        {
            System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnReadFileRequested: filePath={filePath}");
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnReadFileRequested: file is null/empty or doesn't exist");
                return;
            }
            if (!IsPathWithinProject(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnReadFileRequested: path not within project. ProjectPath={_currentProject?.Path ?? "null"}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnReadFileRequested: invoking FilePreviewRequested");
            // Route to the external FilePreviewPanel instead of rendering inline
            FilePreviewRequested?.Invoke(this, filePath);
        }

        protected override string GetPersistString()
        {
            return typeof(ProjectPanelDocument).FullName;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _renderer?.Dispose();
                _projectDatabase = null; // Shared instance — disposed by MainForm
            }
            base.Dispose(disposing);
        }
    }
}
