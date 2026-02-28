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

        public ProjectSelectedEventArgs(Project project, bool launchClaude = false)
        {
            Project = project;
            LaunchClaude = launchClaude;
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
        private static SessionDatabase _sharedSessionDb;
        private static readonly object _sessionDbLock = new object();
        private SessionSyncService _syncService;
        private TerminalTheme _currentTheme;
        private string _currentWorkingDirectory;
        private Project _currentProject;

        // Header controls
        private Panel _headerPanel;
        private Button _recentsButton;
        private Button _newProjectButton;
        private ContextMenuStrip _recentsContextMenu;

        // WebView2 renderer
        private ProjectPanelRenderer _renderer;

        // Fonts
        private Font _smallFont;

        #region Events

        public event EventHandler<ProjectSelectedEventArgs> ProjectSelected;
        public event EventHandler<PromptEventArgs> PromptPasteRequested;
        public event EventHandler<PromptEventArgs> PromptEditRequested;
        public event EventHandler<PromptEventArgs> PromptDeleteRequested;
        public event EventHandler NewPromptRequested;
        public event EventHandler<NewPromptInCategoryEventArgs> NewPromptInCategoryRequested;
        public event EventHandler<string> ViewSessionRequested;

        #endregion

        public ProjectPanelDocument()
        {
            Text = "Project";
            TabText = "Project";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
            ShowHint = DockState.DockLeft;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _smallFont = new Font("Segoe UI", 8.5f);
            _syncService = new SessionSyncService();

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);

            // Header panel with buttons
            _headerPanel = CreateHeaderPanel();
            _headerPanel.Dock = DockStyle.Top;

            // WebView2 renderer for content
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

            Controls.Add(_renderer);
            Controls.Add(_headerPanel);

            ResumeLayout(false);
        }

        private Panel CreateHeaderPanel()
        {
            var panel = new Panel
            {
                Height = 34,
                Padding = new Padding(4)
            };

            _recentsButton = new Button
            {
                Text = "Projects \u25BC",
                Width = 90,
                Height = 24,
                Font = _smallFont,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(4, 5),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _recentsButton.FlatAppearance.BorderSize = 1;
            _recentsButton.Click += OnRecentsButtonClick;

            // + button: opens /new-project skill guidance in the panel instead of a dialog
            _newProjectButton = new Button
            {
                Text = "+",
                Width = 26,
                Height = 24,
                Font = _smallFont,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(99, 5),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _newProjectButton.FlatAppearance.BorderSize = 1;
            _newProjectButton.Click += OnNewProjectButtonClick;

            panel.Controls.Add(_recentsButton);
            panel.Controls.Add(_newProjectButton);

            return panel;
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

        public void SetSessionDatabase(SessionDatabase sessionDatabase)
        {
            lock (_sessionDbLock)
            {
                _sharedSessionDb = sessionDatabase;
                System.Diagnostics.Trace.WriteLine($"[ProjectPanel] SessionDatabase injected from MainForm");
            }
        }

        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;
            bool isDark = theme.IsDark;

            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _headerPanel.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 236, 242);

            Color buttonBg = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);
            Color buttonFg = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            Color borderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);

            foreach (Control c in _headerPanel.Controls)
            {
                if (c is Button btn)
                {
                    btn.BackColor = buttonBg;
                    btn.ForeColor = buttonFg;
                    btn.FlatAppearance.BorderColor = borderColor;
                }
            }

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
                DisposeSessionDatabase();
                return;
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
                GitAutoCommit = project.GitAutoCommit
            };

            // Show project immediately without stats (to avoid UI freeze)
            System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Calling renderer.ShowProject for {projectWithAllPrompts.Name} (initial, no stats)");
            _renderer?.ShowProject(projectWithAllPrompts, null);

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
                // Count files (excluding common ignore patterns)
                var extensions = new[] { ".cs", ".js", ".ts", ".html", ".css", ".json", ".xml", ".md", ".py", ".clw", ".inc", ".equ" };
                var ignoreFolders = new[] { "bin", "obj", "node_modules", ".git", ".vs", "packages" };

                int fileCount = 0;
                int lineCount = 0;

                var files = Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var dir = Path.GetDirectoryName(f);
                        return !ignoreFolders.Any(ig => dir.Contains(Path.DirectorySeparatorChar + ig + Path.DirectorySeparatorChar) ||
                                                         dir.EndsWith(Path.DirectorySeparatorChar + ig));
                    })
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
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
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] OnFieldUpdateRequested error: {ex.Message}");
                _renderer?.SendFieldSaved(e.Field, false);
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
                return (_projectDatabase.DeleteProjectMcpServer(projectId, GetJsonString(el, "serverName")), 0);

            _projectDatabase.SaveProjectMcpServer(new MultiTerminal.Models.ProjectMcpServer
            {
                ProjectId = projectId,
                ServerName = GetJsonString(el, "serverName"),
                IsEnabled = GetJsonBool(el, "isEnabled", defaultValue: true)
            });
            return (true, 0);
        }

        private (bool success, int newId) HandleSpecialistAgentCrud(string projectId, string action, string itemJson)
        {
            var item = TryParseItemJson(itemJson);
            if (!item.HasValue) return (false, 0);
            var el = item.Value;

            if (action == "delete")
                return (_projectDatabase.DeleteProjectSpecialistAgent(projectId, GetJsonString(el, "agentType")), 0);

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

            _projectDatabase.SaveProjectSkill(new MultiTerminal.Models.ProjectSkill
            {
                ProjectId = projectId,
                SkillName = GetJsonString(el, "skillName"),
                IsEnabled = GetJsonBool(el, "isEnabled", defaultValue: true)
            });
            return (true, 0);
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
            return el.ValueKind == JsonValueKind.True;
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

            // Check if project has Claude sessions (quick check, can stay on UI thread)
            string claudeProjectPath = _syncService.GetClaudeProjectPath(projectPath);
            if (claudeProjectPath == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] No Claude sessions folder found for {projectPath}");
                _renderer?.ClearSessions();
                return;
            }

            try
            {
                // Load sessions from database on background thread
                var sessionSummaries = await Task.Run(() =>
                {
                    // Ensure session database is available
                    var db = GetSessionDatabase();

                    // Load sessions from the database (includes orphaned/active sessions)
                    var dbSessions = db?.GetSessionsByProject(projectPath, 20) ?? new List<Session>();
                    return dbSessions
                        .Select(s => new SessionSummary
                        {
                            SessionId = s.SessionId,
                            Summary = s.Summary,
                            FirstPrompt = s.InitialPrompt,
                            MessageCount = s.TotalMessages,
                            Created = s.StartedAt,
                            Modified = s.EndedAt ?? s.StartedAt,
                            GitBranch = null // Not stored in db currently
                        })
                        .ToList();
                });

                // Back on UI thread - update renderer
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Loaded {sessionSummaries.Count} sessions from database for {projectPath}");
                _renderer?.ShowSessions(sessionSummaries);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Failed to load sessions: {ex.Message}");
                _renderer?.ClearSessions();
            }
        }

        private SessionDatabase GetSessionDatabase()
        {
            lock (_sessionDbLock)
            {
                if (_sharedSessionDb == null)
                {
                    System.Diagnostics.Trace.WriteLine($"[ProjectPanel] WARNING: SessionDatabase not injected, returning null");
                }
                return _sharedSessionDb;
            }
        }

        private void DisposeSessionDatabase()
        {
            // Shared database is kept alive - no per-project disposal needed
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
                var db = GetSessionDatabase();
                if (db != null)
                {
                    // Search message content in the database
                    var results = db.SearchMessagesWithSessionInfo(query, _currentProject.Path, 50);
                    System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Found {results.Count} message matches for '{query}'");
                    _renderer?.ShowSearchResults(results, query);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ProjectPanel] SessionDatabase not available for search");
                }
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
                var db = GetSessionDatabase();
                if (db == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ProjectPanel] Failed to get session database");
                    _renderer?.ShowSyncResult(0);
                    return;
                }

                // Sync from Claude's storage to local database on background thread
                var projectPath = _currentProject.Path;
                int count = await Task.Run(() => _syncService.SyncProject(projectPath, db));
                System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Synced {count} sessions to local database");

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
                var db = GetSessionDatabase();
                if (db == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Session database not available");
                    _renderer?.ShowSessionMessages(sessionId, new List<SessionMessageSummary>());
                    return;
                }

                // Query messages from SQLite on background thread
                var summaries = await Task.Run(() =>
                {
                    // Get the session by its UUID
                    var session = db.GetSessionBySessionId(sessionId);
                    if (session == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ProjectPanel] Session not found in database: {sessionId}");
                        return new List<SessionMessageSummary>();
                    }

                    // Get messages from database
                    var messages = db.GetMessages(session.Id);

                    // Convert to summaries (only user and assistant messages)
                    return messages
                        .Where(m => m.Role == "user" || m.Role == "assistant")
                        .Take(50) // Limit for performance
                        .Select(m => new SessionMessageSummary
                        {
                            Role = m.Role,
                            Content = m.Content,
                            Timestamp = m.Timestamp
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

        private void OnRecentsButtonClick(object sender, EventArgs e)
        {
            ShowRecentsMenu();
        }

        private void OnNewProjectButtonClick(object sender, EventArgs e)
        {
            _renderer?.ShowNewProjectHint();
        }

        private void ShowRecentsMenu()
        {
            if (_recentsContextMenu != null)
            {
                _recentsContextMenu.Dispose();
            }

            _recentsContextMenu = new ContextMenuStrip();

            bool isDark = _currentTheme?.IsDark ?? true;
            _recentsContextMenu.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
            _recentsContextMenu.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

            // Get all projects and recent projects
            var allProjects = _projectService?.GetAllRegisteredProjects() ?? new List<ProjectRegistryEntry>();
            var recentProjects = _projectService?.GetRecentProjects(5) ?? new List<ProjectRegistryEntry>();
            var recentIds = new HashSet<string>(recentProjects.Select(p => p.Id));

            // Add recent projects first
            foreach (var entry in recentProjects)
            {
                var item = new ToolStripMenuItem(entry.Name)
                {
                    Tag = entry
                };
                item.Click += OnProjectMenuItemClick;
                _recentsContextMenu.Items.Add(item);
            }

            // Get remaining projects (not in recents), sorted alphabetically
            var otherProjects = allProjects
                .Where(p => !recentIds.Contains(p.Id))
                .OrderBy(p => p.Name)
                .ToList();

            // Add separator and other projects if any exist
            if (recentProjects.Any() && otherProjects.Any())
            {
                _recentsContextMenu.Items.Add(new ToolStripSeparator());
            }

            foreach (var entry in otherProjects)
            {
                var item = new ToolStripMenuItem(entry.Name)
                {
                    Tag = entry
                };
                item.Click += OnProjectMenuItemClick;
                _recentsContextMenu.Items.Add(item);
            }

            if (_recentsContextMenu.Items.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem("(No projects)")
                {
                    Enabled = false
                };
                _recentsContextMenu.Items.Add(emptyItem);
            }

            _recentsContextMenu.Show(_recentsButton, new Point(0, _recentsButton.Height));
        }

        private void OnProjectMenuItemClick(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem item && item.Tag is ProjectRegistryEntry entry)
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Menu item clicked: {entry.Name} at {entry.Path}");

                var project = _projectService?.LoadProject(entry.Path);
                if (project != null)
                {
                    System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Project loaded successfully, firing ProjectSelected event");
                    ProjectSelected?.Invoke(this, new ProjectSelectedEventArgs(project));
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[ProjectPanel] Failed to load project at {entry.Path}");
                    MessageBox.Show(
                        $"Could not load project at:\n{entry.Path}\n\nThe .claude/project.json file may be missing.",
                        "Project Load Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        protected override string GetPersistString()
        {
            return typeof(ProjectPanelDocument).FullName;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _smallFont?.Dispose();
                _recentsContextMenu?.Dispose();
                _renderer?.Dispose();
                _projectDatabase?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
