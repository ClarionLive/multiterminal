using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Interop;
using MultiTerminal.ActivityPanel;
using MultiTerminal.ChatPanel;
using MultiTerminal.TasksPanel;
using MultiTerminal.Controls;
using MultiTerminal.Dialogs;
using MultiTerminal.Docking;
using MultiTerminal.API;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Panels;
using MultiTerminal.Services;
using MultiTerminal.InboxPanel;
using MultiTerminal.TaskLifecycleBoard;
using MultiTerminal.Terminal; // For FontSizeChangedEventArgs
using MultiTerminal.AgentPanel;
using MultiTerminal.StartScreen;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal
{
    /// <summary>
    /// Main application form with dockable terminal panels.
    /// </summary>
    public partial class MainForm : Form
    {
        private DockPanel _dockPanel;
        private ToolStrip _toolStrip; // Legacy — kept for fallback, replaced by _dashboardHeader
        private DashboardHeader.DashboardHeaderControl _dashboardHeader;
        private SettingsService _settings;
        private GridLayoutManager _gridManager;
        private TerminalTheme _currentTheme = TerminalTheme.Dark;
        private ToolStripButton _themeButton;
        private ToolStripDropDownButton _gridDropdown;
        private ToolStripButton _settingsButton;
        private ToolStripButton _aboutButton;
        private ProjectPanelDocument _projectPanel;
        private PromptService _promptService;
        private ProjectService _projectService;
        private ToolStripButton _projectPanelButton;
        private ToolStripDropDownButton _recentFoldersDropdown;
        private TerminalDocument _lastActiveTerminal;
        private Models.Project _currentProject;

        // REST API Server for inter-terminal communication
        private MultiTerminalRestServer _mcpServer;
        private MCPServer.Services.HttpWebhookService _webhookService;
        private ChatPanelDocument _chatPanel;
        private SessionIndexingService _sessionIndexingService;
        private TaskDatabase _chatTaskDatabase; // Used for chat message persistence
        private OwnerProfileService _ownerProfileService;
        private readonly Dictionary<string, TerminalDocument> _terminalDocMap = new();
        private ToolStripButton _chatPanelButton;
        private ToolStripButton _chatHistoryButton;
        private ActivityPanelDocument _activityPanel;
        private ToolStripButton _activityPanelButton;
        private TasksPanelDocument _tasksPanel;
        private ToolStripButton _tasksPanelButton;
        private DebugPanel _debugPanel;
        private ToolStripButton _debugPanelButton;
        private DebugLogService _debugLogService;
        private ProfilePanel.ProfilePanelDocument _profilePanel;
        private ToolStripButton _profilePanelButton;
        private InboxPanelDocument _inboxPanel;
        private ToolStripButton _inboxPanelButton;
        private OfficePanel.OfficePanelDocument _officePanel;
        private ToolStripButton _officePanelButton;
        private ToolStripButton _agentPanelButton;
        private ToolStripButton _hudButton;
        private readonly Dictionary<string, AgentProcess> _agentProcessMap = new();
        private readonly Dictionary<string, (AgentPanelControl Control, Panel Slot, TerminalDocument Terminal)> _embeddedAgentMap = new();
        private TeamWatcherService _teamWatcher;
        private InboxMonitorService _inboxMonitor;
        private FilePreviewPanel.FilePreviewPanelDocument _filePreviewPanel;
        private ToolStripButton _filePreviewPanelButton;

        // Companion process manager — auto-launches external services on startup
        private Services.CompanionProcessManager _companionManager;

        // Shared project database for start screen — created once, disposed with form
        private Services.ProjectDatabase _sharedProjectDatabase;

        // MCP config service — generates .mcp.json for projects from the registry
        private Services.McpConfigService _mcpConfigService;

        // Gateway integration service — syncs MCP servers to the gateway's SQLite DB
        private Services.GatewayIntegrationService _gatewayService;

        // Oracle — on-demand advisory agent (no project, no coding)
        private OracleService _oracleService;
        private string _oracleTerminalId;
        private TerminalDocument _oracleTerminalDoc;
        private volatile bool _oracleSpawning; // Guard against concurrent spawn from thread pool

        // Anti-reentrance: throttle nudges per terminal (5-second cooldown)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastNudgeTime = new();
        private static readonly TimeSpan NudgeThrottle = TimeSpan.FromSeconds(5);

        // Message injection queue to prevent focus race conditions
        private readonly Queue<(string messageId, string recipientId, string sender, string message, TaskCompletionSource<bool> completion)> _messageQueue = new();
        private bool _injectionInProgress = false;
        private readonly object _injectionLock = new object();
        private readonly object _terminalDocMapLock = new object();

        // Reverse map: agent name → terminal document (populated on terminal registration)
        private readonly Dictionary<string, TerminalDocument> _agentNameToTerminalDoc = new(StringComparer.OrdinalIgnoreCase);

        // Message deduplication cache - tracks recently delivered message IDs to prevent duplicates
        private readonly Dictionary<string, DateTime> _deliveredMessageCache = new Dictionary<string, DateTime>();
        private readonly object _deduplicationLock = new object();
        private const int DeduplicationCacheMinutes = 5; // Keep delivered message IDs for 5 minutes

        // For session restore with XML layout
        private int _terminalRestoreIndex = 0;
        private List<TerminalSessionInfo> _pendingTerminalSessions;

        // Timer for polling pending message queue (retry delivery)
        private System.Windows.Forms.Timer _messageQueueTimer;
        private int _messageQueueCleanupCounter = 0;
        private const int CleanupIntervalTicks = 1800; // Every 1 hour at 2-second intervals (3600/2)

        /// <summary>
        /// Event fired when all terminals have finished loading.
        /// </summary>
        public event EventHandler LoadingComplete;

        /// <summary>
        /// Event fired when the dashboard header WebView2 is fully loaded and ready to display.
        /// </summary>
        public event EventHandler DashboardContentReady;

        public MainForm()
        {
            InitializeComponent();
            InitializeDockPanel();
            InitializeDashboardHeader();
            LoadSettings();
            ApplyTheme(isInitialLoad: true);

            System.Diagnostics.Trace.WriteLine("[MainForm] Calling InitializeMcpServerAndChatPanel...");
            // Initialize MCP server BEFORE RestoreSession so panels can be properly initialized
            // when restored from layout (panels need broker for WebView2 initialization)
            InitializeMcpServerAndChatPanel();
            System.Diagnostics.Trace.WriteLine("[MainForm] InitializeMcpServerAndChatPanel completed");

            // Launch companion processes (ClaudeRemote, Caddy, etc.) in background
            // Uses the shared instance from the REST server so the API controller sees tracked PIDs
            _companionManager = _mcpServer.CompanionProcessManager;
            _ = System.Threading.Tasks.Task.Run(() => _companionManager.StartAll());

            // Defer RestoreSession to after the form is shown to avoid blocking the constructor
            // This prevents WebView2 initialization from blocking the UI thread during startup
            System.Diagnostics.Trace.WriteLine("[MainForm] Deferring RestoreSession until form is shown...");
            System.Diagnostics.Trace.WriteLine("[MainForm] Constructor completed, exiting...");
            this.Shown += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[MainForm] ===== SHOWN EVENT FIRED =====");
                ShowOwnerProfileDialogIfNeeded();
                RestoreSession();
            };
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Text = $"MultiTerminal v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0;  // Start invisible, show after session restore

            // Handle form closing
            FormClosing += OnFormClosing;

            ResumeLayout(false);
        }

        /// <summary>
        /// Handle global keyboard shortcuts that must work even when WebView2 has focus.
        /// Ctrl+Shift+H: stop terminal and return to start screen on the active tab.
        /// </summary>
        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.H))
            {
                var activeTerminal = _dockPanel.ActiveDocument as TerminalDocument;
                activeTerminal?.ReturnToStartScreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void InitializeDockPanel()
        {
            _dockPanel = new DockPanel
            {
                Dock = DockStyle.Fill,
                Theme = new WeifenLuo.WinFormsUI.Docking.VS2015DarkTheme(),
                DocumentStyle = DocumentStyle.DockingWindow,
                ShowDocumentIcon = false
            };

            // Configure dock panel
            _dockPanel.DockBackColor = Color.FromArgb(30, 30, 30);

            // Handle active document changes
            _dockPanel.ActiveDocumentChanged += OnActiveDocumentChanged;

            Controls.Add(_dockPanel);

            _gridManager = new GridLayoutManager(_dockPanel);

            // Initialize services
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating PromptService...");
            _promptService = new PromptService();
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating ProjectService...");
            _projectService = new ProjectService();
            _projectService.RegistryChangedExternally += OnRegistryChangedExternally;
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating SessionIndexingService...");
            _sessionIndexingService = new SessionIndexingService();
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating TaskDatabase for chat persistence...");
            _chatTaskDatabase = new TaskDatabase();
            _ownerProfileService = new OwnerProfileService(_chatTaskDatabase.Connection);
            System.Diagnostics.Trace.WriteLine("[MainForm] TaskDatabase created successfully");

            // Shared project database for the start screen (one connection, shared across all terminals)
            _sharedProjectDatabase = new Services.ProjectDatabase();

            // Initialize project panel (combines project info and prompts)
            _projectPanel = new ProjectPanelDocument();
            _projectPanel.SetServices(_projectService, _promptService);
            // SessionLineageService is injected later in InitializeMcpServerAndChatPanel once TaskDatabase is available
            _projectPanel.SetProjectDatabase(_sharedProjectDatabase); // Share single connection used by the rest of the app
            _projectPanel.SetTheme(_currentTheme);

            // Project events
            _projectPanel.ProjectSelected += OnProjectSelected;
            _projectPanel.ProjectLaunchRequested += OnProjectLaunchRequested;

            // Prompt events
            _projectPanel.PromptPasteRequested += OnPromptPasteRequested;
            _projectPanel.PromptEditRequested += OnPromptEditRequested;
            _projectPanel.PromptDeleteRequested += OnPromptDeleteRequested;
            _projectPanel.NewPromptRequested += OnNewPromptRequested;
            _projectPanel.NewPromptInCategoryRequested += OnNewPromptInCategoryRequested;

            // Session events
            _projectPanel.ViewSessionRequested += OnViewSessionRequested;

            // MCP config events
            _projectPanel.McpJsonWriteRequested += OnMcpJsonWriteRequested;

            // File preview — route file explorer clicks to the separate preview panel
            _projectPanel.FilePreviewRequested += OnFilePreviewRequested;

            // Create debug log service (available immediately)
            _debugLogService = new DebugLogService();

            // Initialize McpConfigService — used by OnMcpJsonWriteRequested
            // Pass debug log callback so CLI errors are visible in the debug panel
            _mcpConfigService = new Services.McpConfigService(_sharedProjectDatabase,
                (source, msg) => _debugLogService?.Info(source, msg));

            // Initialize GatewayIntegrationService and wire it to McpConfigService and ProjectPanel
            _gatewayService = new Services.GatewayIntegrationService(
                (source, msg) => _debugLogService?.Info(source, msg));
            _mcpConfigService.GatewayService = _gatewayService;
            _projectPanel.SetGatewayService(_gatewayService);

            // Create panel instances early so they can be restored from layout
            // They will be initialized with MCP broker later in InitializeMcpServerAndChatPanel
            _chatPanel = new ChatPanelDocument();
            _activityPanel = new ActivityPanelDocument();
            _tasksPanel = new TasksPanelDocument();
            _profilePanel = new ProfilePanel.ProfilePanelDocument();
            _inboxPanel = new InboxPanelDocument();
            _officePanel = new OfficePanel.OfficePanelDocument();
            _debugPanel = new DebugPanel();
            _debugPanel.Initialize(_debugLogService);
            _filePreviewPanel = new FilePreviewPanel.FilePreviewPanelDocument();
            _filePreviewPanel.DebugLogService = _debugLogService;

        }

        private void InitializeMcpServerAndChatPanel()
        {
            // Guard against double initialization
            if (_mcpServer != null)
                return;

            try
            {
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 1: About to create REST API server");
                // Create REST API server
                _mcpServer = new MultiTerminalRestServer(5050);
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 2: REST API server created");

                if (_mcpServer == null)
                {
                    throw new InvalidOperationException("Failed to create MCP server instance");
                }

                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 3: Checking Broker");
                if (_mcpServer.Broker == null)
                {
                    throw new InvalidOperationException("MCP server Broker is null after construction");
                }
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 4: Broker is not null");

                // Wire up debug logging service
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 5: About to wire up debug logging");
                _mcpServer.Broker.DebugLogService = _debugLogService;
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 6: About to call _debugLogService.Info");
                _debugLogService.Info("MainForm", "MCP Server created, debug logging initialized");
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 7: Debug logging initialized");

                // Wire up SessionLineageService — uses the broker's shared TaskDatabase
                _mcpServer.Broker.SessionLineageService = new Services.SessionLineageService(_mcpServer.Broker.TaskDb);

                // Inject SessionLineageService into ProjectPanel (replaces old SessionDatabase dependency)
                _projectPanel.SetSessionLineageService(_mcpServer.Broker.SessionLineageService);

                // Set DefaultInboxRecipient from owner profile (so inbox notifications go to the actual user, not a hardcoded name)
                var ownerProfile = _ownerProfileService?.GetProfile();
                if (ownerProfile?.FullName != null)
                {
                    _mcpServer.Broker.DefaultInboxRecipient = ownerProfile.FullName.Split(' ')[0]; // First name
                }

                // Wire up KnowledgeDatabase — institutional memory (shares multiterminal.db via TaskDatabase)
                _mcpServer.Broker.KnowledgeDb = new Services.KnowledgeDatabase(_mcpServer.Broker.TaskDb);

                // DISABLED FOR DISTRIBUTION — re-enable after debugging auto-sync feature
                // Auto-sync Claude Code sessions on startup (background, non-blocking)
                // Scans all registered projects from SQLite, not just CWD
                // var lineageService = _mcpServer.Broker.SessionLineageService;
                // var debugLog = _debugLogService;
                // var projectDb = _sharedProjectDatabase;
                // System.Threading.Tasks.Task.Run(() =>
                // {
                //     try
                //     {
                //         var projects = projectDb?.GetAllProjects();
                //         if (projects == null || projects.Count == 0)
                //             return;
                //
                //         int totalImported = 0, totalSkipped = 0, totalFailed = 0;
                //
                //         foreach (var project in projects)
                //         {
                //             if (string.IsNullOrEmpty(project.Path))
                //                 continue;
                //
                //             string claudeFolder = Services.SessionLineageService.GetClaudeProjectFolder(project.Path);
                //             if (claudeFolder == null)
                //                 continue;
                //
                //             debugLog?.Info("MainForm", $"Session sync [{project.Name}]: scanning {claudeFolder}");
                //             var result = lineageService.SyncNewSessions(claudeFolder);
                //             totalImported += result.Imported;
                //             totalSkipped += result.Skipped;
                //             totalFailed += result.Failed;
                //             debugLog?.Info("MainForm", $"Session sync [{project.Name}]: {result.Imported} imported, {result.Skipped} skipped, {result.Failed} failed");
                //         }
                //
                //         debugLog?.Info("MainForm", $"Session sync totals: {totalImported} imported, {totalSkipped} skipped, {totalFailed} failed across {projects.Count} projects");
                //     }
                //     catch (Exception ex)
                //     {
                //         debugLog?.Error("MainForm", $"Session sync error: {ex.Message}");
                //     }
                // });

                // Note: Profiles are set to offline in MessageBroker constructor before loading

                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 8: Wiring ServerError event");
                _mcpServer.ServerError += (s, ex) =>
                {
                    var errorMsg = $"MCP Server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
                    _debugLogService.Error("MainForm", $"MCP Server error: {ex.Message}");

                    // Show error in UI
                    try
                    {
                        Invoke(new Action(() =>
                        {
                            MessageBox.Show(errorMsg, "MCP Server Runtime Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                    catch { /* Ignore invoke errors */ }
                };

                // Initialize panels with MCP broker (panels were created in InitializeDockPanel)
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 9: Checking chat panel");
                if (_chatPanel == null)
                {
                    throw new InvalidOperationException("Chat panel is null - InitializeDockPanel may not have run");
                }

                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 10: Initializing chat panel");
                _chatPanel.Initialize(_mcpServer.Broker);
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 11: Wiring chat panel events");
                _chatPanel.InjectRequested += OnChatInjectRequested;
                _chatPanel.ReplyRequested += OnChatReplyRequested;
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 12: Initializing tasks panel");
                _tasksPanel.SetDebugLogService(_debugLogService);
                _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 13: Wiring tasks panel events");
                _tasksPanel.InjectRequested += OnChatInjectRequested; // Reuse same inject handler

                // Save zoom level to settings whenever user ctrl+wheels in a standalone panel
                _tasksPanel.ZoomChanged += (s, zoom) => _settings?.SetTasksPanelZoom(zoom);
                _chatPanel.ZoomChanged += (s, zoom) => _settings?.SetChatPanelZoom(zoom);

                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Initializing inbox panel");
                _inboxPanel.Initialize(_mcpServer.Broker);

                // Initialize dashboard header alongside other panels (not deferred — deferring
                // caused the header to never appear if RestoreSession hit any issue)
                _dashboardHeader?.Initialize(_mcpServer.Broker);

                // Persist chat messages to database
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 14: Wiring MessageSent event");
                _mcpServer.Broker.MessageSent += OnChatMessageSent;

                // Push notification support - map terminal IDs to documents
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 15: Wiring TerminalRegistered event");
                _mcpServer.Broker.TerminalRegistered += OnMcpTerminalRegistered;
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 16: Setting OnMessageDelivery");
                _mcpServer.Broker.OnMessageDelivery = OnMcpMessageDelivery;

                // Browser tab support - route tab requests to correct terminal
                _mcpServer.Broker.BrowserTabRequested += OnBrowserTabRequested;

                // Wire up spawn callback for programmatic terminal spawning
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 17: Checking SpawnService");
                if (_mcpServer.SpawnService == null)
                {
                    throw new InvalidOperationException("SpawnService is null");
                }
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 18: Setting OnSpawnRequested");
                _mcpServer.SpawnService.OnSpawnRequested = OnSpawnRequested;
                _mcpServer.SpawnService.OnSpawnAgentRequested = OnSpawnAgentRequested;
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 19: All wiring complete");

                // Initialize HTTP webhook service for agent ready notifications
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 20: Creating HttpWebhookService");
                _webhookService = new MCPServer.Services.HttpWebhookService(_mcpServer.Broker);
                _webhookService.AgentReady += (s, args) =>
                {
                    System.Diagnostics.Trace.WriteLine($"[MainForm] Agent ready webhook received: {args.AgentName}");
                    _debugLogService.Info("MainForm", $"Agent {args.AgentName} sent ready notification via webhook");
                };
                _webhookService.Start();
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 21: HttpWebhookService started on http://localhost:5000/");

                // Start native agent team watcher
                InitializeTeamWatcher();

                // Start inbox file monitor for nudging idle terminals
                InitializeInboxMonitor();

                // Initialize Oracle advisory agent (on-demand — registers terminal but doesn't spawn yet)
                InitializeOracle();

                // Start MCP server in background
                Task.Run(async () =>
                {
                    try
                    {
                        await _mcpServer.StartAsync();

                        // Wire terminal stream resolver: resolves any terminal identifier
                        // (terminal ID, DocId, or agent name) to its ConPtyTerminal instance
                        _mcpServer.TerminalStreamService?.SetTerminalResolver(idOrNameOrDocId =>
                        {
                            // Use MessageBroker to resolve flexible identifiers to a terminal info
                            var termInfo = _mcpServer.Broker.GetTerminal(idOrNameOrDocId);
                            if (termInfo == null) return null;

                            // Look up the TerminalDocument by the terminal's registered ID
                            lock (_terminalDocMapLock)
                            {
                                if (_terminalDocMap.TryGetValue(termInfo.Id, out var doc))
                                    return doc.Terminal?.ConPty;
                            }
                            return null;
                        });

                        Invoke(new Action(() =>
                        {
                            _chatPanel?.UpdateConnectionStatus(true);
                            _activityPanel?.Initialize(_mcpServer.Broker.ActivityService, _mcpServer.PoolCoordinator, _mcpServer.Broker, _mcpServer.Broker.TaskDb);
                            _officePanel?.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                            if (_officePanel != null)
                                _officePanel.ZoomChanged += (s, zoom) => _settings?.SetOfficePanelZoom(zoom);
                            _profilePanel?.SetMessageBroker(_mcpServer.Broker);
                            // Refresh projects dropdown now that ProjectService is wired to broker
                            _tasksPanel?.RefreshProjects();
                            var successMsg = $"MCP Server started successfully on port {_mcpServer.Port} at {_mcpServer.Url}";
                            System.Diagnostics.Debug.WriteLine(successMsg);

                            // Also write to status (if status bar exists, we'll add it later)
                            Text = $"MultiTerminal - REST API: Running on port {_mcpServer.Port}";
                        }));
                    }
                    catch (Exception ex)
                    {
                        var detailedError = $"Failed to start MCP server: {ex.Message}\n\n" +
                                          $"Type: {ex.GetType().Name}\n\n" +
                                          $"Stack Trace:\n{ex.StackTrace}";

                        if (ex.InnerException != null)
                        {
                            detailedError += $"\n\nInner Exception: {ex.InnerException.Message}\n" +
                                           $"Inner Stack Trace:\n{ex.InnerException.StackTrace}";
                        }

                        System.Diagnostics.Debug.WriteLine(detailedError);
                        Invoke(new Action(() =>
                        {
                            MessageBox.Show(detailedError + "\n\nThe Chat feature will not be available.",
                                "MCP Server Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to initialize MCP server: {ex.Message}\n\n" +
                              $"Type: {ex.GetType().Name}\n\n" +
                              $"Stack Trace:\n{ex.StackTrace}";

                if (ex.InnerException != null)
                {
                    errorMsg += $"\n\nInner Exception: {ex.InnerException.Message}\n" +
                               $"Inner Stack Trace:\n{ex.InnerException.StackTrace}";
                }

                // Write to log file for debugging
                try
                {
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "multiterminal", "startup-error.log");
                    File.WriteAllText(logPath, $"{DateTime.Now}\n\n{errorMsg}\n\n{ex.ToString()}");
                }
                catch { }

                System.Diagnostics.Debug.WriteLine(errorMsg);
                MessageBox.Show(errorMsg + $"\n\nLog file: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "multiterminal", "startup-error.log")}\n\nThe Chat feature will not be available.",
                    "MCP Server Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnChatInjectRequested(object sender, InjectMessageEventArgs e)
        {
            // Find the terminal by name using _terminalDocMap (more reliable than _dockPanel.Documents)
            TerminalDocument targetTerminal = null;
            lock (_terminalDocMapLock)
            {
                targetTerminal = _terminalDocMap.Values.FirstOrDefault(t =>
                    (t.CustomTitle?.Equals(e.TerminalName, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    t.TabText.Equals(e.TerminalName, StringComparison.OrdinalIgnoreCase));
            }

            if (targetTerminal != null)
            {
                // TEST: Removed Activate() to prevent focus stealing
                // targetTerminal.Activate();
                // await Task.Delay(50);  // Let activation settle
                await targetTerminal.InjectInputAsync(e.Content);
            }
        }

        private async void OnChatReplyRequested(object sender, ReplyMessageEventArgs e)
        {
            // Find the terminal by sender name using _terminalDocMap (more reliable than _dockPanel.Documents)
            TerminalDocument targetTerminal = null;
            lock (_terminalDocMapLock)
            {
                targetTerminal = _terminalDocMap.Values.FirstOrDefault(t =>
                    (t.CustomTitle?.Equals(e.SenderName, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    t.TabText.Equals(e.SenderName, StringComparison.OrdinalIgnoreCase));
            }

            if (targetTerminal != null)
            {
                // Inject a prompt to reply to the message
                // Using the mcp__multiterminal__send_reply tool with the message ID
                string replyPrompt = $"Reply to message {e.MessageId}: ";

                // TEST: Removed Activate() to prevent focus stealing
                // targetTerminal.Activate();
                // await Task.Delay(50);  // Let activation settle
                await targetTerminal.InjectInputAsync(replyPrompt);
            }
        }

        private void OnChatMessageSent(object sender, MCPServer.Models.Message message)
        {
            // Persist chat message to centralized database
            Task.Run(() =>
            {
                var logPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "multiterminal", "chat-save.log");

                void Log(string msg) {
                    var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}\n";
                    System.Diagnostics.Debug.WriteLine($"[ChatSave] {msg}");
                    try { System.IO.File.AppendAllText(logPath, line); } catch { }
                }

                try
                {
                    Log($"START: From={message.From}, To={message.To}, Id={message.Id}");

                    _chatTaskDatabase.SaveChatMessage(
                        message.Id,
                        message.From,
                        message.To,
                        message.Content,
                        message.Timestamp,
                        message.IsBroadcast
                    );
                    Log($"SUCCESS - saved message {message.Id}");
                }
                catch (Exception ex)
                {
                    Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    Log($"Stack: {ex.StackTrace}");
                }
            });
        }

        private void OnBrowserTabRequested(object sender, BrowserTabEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(() => OnBrowserTabRequested(sender, e));
                return;
            }

            // Resolve terminal ID/name to a TerminalDocument
            var terminal = _mcpServer.Broker.GetTerminal(e.TerminalId);
            if (terminal == null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] BrowserTabRequested: terminal not found: {e.TerminalId}");
                return;
            }

            TerminalDocument targetDoc = null;
            lock (_terminalDocMapLock)
            {
                _terminalDocMap.TryGetValue(terminal.Id, out targetDoc);
            }

            // Fallback: search by DocId or name
            if (targetDoc == null)
            {
                targetDoc = _dockPanel.Documents
                    .OfType<TerminalDocument>()
                    .FirstOrDefault(t =>
                        t.DocId == terminal.DocId ||
                        (t.CustomTitle?.Equals(terminal.Name, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (targetDoc == null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] BrowserTabRequested: no TerminalDocument found for {e.TerminalId}");
                return;
            }

            switch (e.Action)
            {
                case "open":
                    var newTab = targetDoc.AddBrowserTab(e.TabId, e.Title, e.Url, e.HtmlContent, _currentTheme.IsDark);
                    if (newTab != null)
                    {
                        // Wire browser page messages to the inbox nudge pipeline so
                        // the agent gets a [cm] notification when the page posts a message.
                        var termName = terminal.Name;
                        var tabTitle = e.Title ?? e.TabId;
                        newTab.WebMessageReceived += (_, msg) =>
                        {
                            var content = $"[Browser Tab \"{tabTitle}\"] Page message: {msg.Data}";
                            InboxFileWriter.WriteMessage(
                                termName,
                                Guid.NewGuid().ToString("N").Substring(0, 8),
                                $"Browser:{tabTitle}",
                                content);
                            _debugLogService?.Info("MainForm",
                                $"Browser message from tab '{tabTitle}' written to {termName} inbox");
                        };
                    }
                    break;
                case "update":
                    targetDoc.SetBrowserContent(e.TabId, e.Title, e.Url, e.HtmlContent);
                    break;
                case "close":
                    targetDoc.RemoveBrowserTab(e.TabId);
                    break;
                case "execute_script":
                    _ = HandleExecuteScriptAsync(targetDoc, e);
                    break;
                case "get_console_logs":
                    _ = HandleGetConsoleLogsAsync(targetDoc, e);
                    break;
                case "get_element_content":
                    _ = HandleGetElementContentAsync(targetDoc, e);
                    break;
                case "capture_screenshot":
                    _ = HandleCaptureScreenshotAsync(targetDoc, e);
                    break;
                case "post_message":
                    _ = HandlePostMessageAsync(targetDoc, e);
                    break;
                case "get_messages":
                    _ = HandleGetMessagesAsync(targetDoc, e);
                    break;
            }
        }

        private async Task HandleExecuteScriptAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult("{\"error\": \"Tab not found\"}");
                    return;
                }
                var result = await tab.ExecuteScriptAsync(e.Script);
                e.ResultTcs?.TrySetResult(result);
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
        }

        private async Task HandleGetConsoleLogsAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult("[]");
                    return;
                }
                var logs = await tab.GetConsoleLogsAsync(e.Limit);
                var json = System.Text.Json.JsonSerializer.Serialize(logs);
                e.ResultTcs?.TrySetResult(json);
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
        }

        private async Task HandleGetElementContentAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult("{\"error\": \"Tab not found\"}");
                    return;
                }
                var result = await tab.GetElementContentAsync(e.Selector, e.Property);
                e.ResultTcs?.TrySetResult(result);
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
        }

        private async Task HandleCaptureScreenshotAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult(null);
                    return;
                }
                var pngBytes = await tab.CaptureScreenshotAsync();
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    e.ResultTcs?.TrySetResult(null);
                    return;
                }
                var base64 = Convert.ToBase64String(pngBytes);
                e.ResultTcs?.TrySetResult(base64);
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
        }

        private Task HandlePostMessageAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult("{\"error\": \"Tab not found\"}");
                    return Task.CompletedTask;
                }
                tab.PostMessageToPage(e.MessageData);
                e.ResultTcs?.TrySetResult("{\"success\": true}");
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
            return Task.CompletedTask;
        }

        private Task HandleGetMessagesAsync(TerminalDocument doc, BrowserTabEventArgs e)
        {
            try
            {
                var tab = doc.GetBrowserTab(e.TabId);
                if (tab == null)
                {
                    e.ResultTcs?.TrySetResult("[]");
                    return Task.CompletedTask;
                }
                var messages = tab.GetReceivedMessages(e.Limit);
                var json = System.Text.Json.JsonSerializer.Serialize(messages);
                e.ResultTcs?.TrySetResult(json);
            }
            catch (Exception ex)
            {
                e.ResultTcs?.TrySetException(ex);
            }
            return Task.CompletedTask;
        }

        private void OnMcpTerminalRegistered(object sender, TerminalInfo e)
        {
            // Ignore temporary subagents (e.g. "Agent Alice") - they are internal Claude Code
            // subagents that should not change the parent terminal's tab or appear in activity
            if (IsTemporaryAgent(e.Name))
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Ignoring temporary agent registration: {e.Name}");
                return;
            }

            // Map the MCP terminal ID to the TerminalDocument
            // Strategy: try DocId first, then name match, then last active as final fallback
            TerminalDocument targetDoc = null;

            if (!string.IsNullOrEmpty(e.DocId))
            {
                // Find terminal by DocId - most reliable mapping
                targetDoc = _dockPanel.Documents
                    .OfType<TerminalDocument>()
                    .FirstOrDefault(t => t.DocId == e.DocId);
            }

            // Fall back to name match (handles re-registration where Claude passes wrong DocId
            // but pre-registration already set CustomTitle correctly)
            if (targetDoc == null && !string.IsNullOrEmpty(e.Name))
            {
                targetDoc = _dockPanel.Documents
                    .OfType<TerminalDocument>()
                    .FirstOrDefault(t =>
                        (t.CustomTitle?.Equals(e.Name, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        t.TabText.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
            }

            // No last-resort fallback — DocId and name-match are the only reliable mapping paths.
            // The old _lastActiveTerminal fallback caused misrouting when two terminals registered
            // in quick succession and _lastActiveTerminal pointed to the wrong tab.
            if (targetDoc == null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Terminal '{e.Name}' (id={e.Id}, docId={e.DocId}) could not be mapped to any TerminalDocument — channel/inbox delivery still works.");
            }

            if (targetDoc != null)
            {
                lock (_terminalDocMapLock)
                {
                    _terminalDocMap[e.Id] = targetDoc;
                    // Maintain reverse lookup: agent name → terminal document
                    if (!string.IsNullOrEmpty(e.Name))
                        _agentNameToTerminalDoc[e.Name] = targetDoc;
                }

                // CRITICAL: Must update UI controls on the UI thread!
                // This event fires from MCP server background thread.
                if (InvokeRequired)
                {
                    Invoke(new Action(() =>
                    {
                        targetDoc.CustomTitle = e.Name;  // Display the Claude name in the tab
                        targetDoc.UpdateStatusBar();     // Update the terminal banner with name, avatar, and task
                    }));
                }
                else
                {
                    targetDoc.CustomTitle = e.Name;  // Display the Claude name in the tab
                    targetDoc.UpdateStatusBar();     // Update the terminal banner with name, avatar, and task
                }
            }

            // Auto-seed activity data for new terminal so Activity Panel shows it immediately
            _mcpServer?.Broker?.ActivityService?.UpdateActivity(
                e.Name,
                "idle",
                "Just connected"
            );
            System.Diagnostics.Debug.WriteLine($"[MainForm] Terminal registered: {e.Name}, seeded initial activity");
        }

        /// <summary>
        /// Callback for spawning new teammate terminals via MCP tool.
        /// </summary>
        private async Task<(bool success, string docId, string error)> OnSpawnRequested(
            string agentName,
            string agentType,
            string workingDir,
            string initialPrompt,
            string spawnerName)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Spawn requested: {agentName} ({agentType}) in {workingDir}");
                _debugLogService.Info("MainForm", $"Spawning teammate: {agentName} ({agentType})");

                // Track doc ID from terminal registration
                string docId = null;
                var registrationTcs = new TaskCompletionSource<string>();

                // Subscribe to terminal registered event to capture doc ID
                void registeredHandler(object sender, TerminalInfo e)
                {
                    if (e.Name == agentName)
                    {
                        docId = e.DocId;
                        registrationTcs.TrySetResult(docId);
                        _mcpServer.Broker.TerminalRegistered -= registeredHandler;
                    }
                }
                _mcpServer.Broker.TerminalRegistered += registeredHandler;

                // Spawn terminal on UI thread
                await Task.Run(() =>
                {
                    Invoke(new Action(() =>
                    {
                        // Default working directory if not provided
                        if (string.IsNullOrWhiteSpace(workingDir))
                        {
                            workingDir = System.IO.Path.Combine(
                                System.IO.Directory.GetParent(System.IO.Directory.GetCurrentDirectory())?.FullName ?? "",
                                "Deploy");
                        }

                        // Always spawn with fresh session to avoid stale context from previous work
                        // The auto-submit logic below will handle the "initializing..." prompt automatically
                        string claudeCommand = "claude --dangerously-skip-permissions";
                        System.Diagnostics.Debug.WriteLine($"[MainForm] Spawning {agentName} with fresh session");
                        _debugLogService.Info("MainForm", $"Spawning {agentName} with fresh session (no context pollution)");

                        // Spawn with Claude Code auto-run (resume if session exists, otherwise new session)
                        AddNewTerminal(
                            workingDirectory: workingDir,
                            fontSize: null,
                            forceTabMode: false,
                            identityName: agentName,
                            autoRunCommand: claudeCommand,
                            spawnerName: spawnerName);
                    }));
                });

                // Wait for registration with timeout
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(registrationTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _mcpServer.Broker.TerminalRegistered -= registeredHandler;
                    return (false, null, "Terminal spawn timed out waiting for registration");
                }

                _debugLogService.Info("MainForm", $"Terminal spawned for {agentName} (DocId: {docId})");

                // Auto-initialize Claude Code by injecting "initializing..." to trigger startup hooks
                // This removes the need for manual Enter press after spawning
                if (_terminalDocMap.TryGetValue(docId, out var spawnedDoc))
                {
                    // Wait for renderer to be ready before injecting (fixes race condition with slow-initializing terminals)
                    System.Diagnostics.Trace.WriteLine($"[MainForm.OnSpawnRequested] Waiting for renderer ready before auto-inject for {agentName}...");

                    // Increased timeout from 3s to 10s for more reliable initialization
                    var ready = await WaitForRendererReadyAsync(spawnedDoc, timeoutMs: 10000);

                    if (ready)
                    {
                        // Try injection with retry logic (sometimes first attempt fails)
                        bool injected = false;
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            injected = await spawnedDoc.InjectInputAsync("initializing...");
                            if (injected)
                            {
                                System.Diagnostics.Trace.WriteLine($"[MainForm.OnSpawnRequested] Auto-injected 'initializing...' for {agentName} (attempt {attempt}, success={injected})");
                                _debugLogService.Info("MainForm", $"Auto-submitted initializing prompt for {agentName} (attempt {attempt})");
                                break;
                            }
                            else
                            {
                                System.Diagnostics.Trace.WriteLine($"[MainForm.OnSpawnRequested] Injection attempt {attempt} failed for {agentName}, retrying...");
                                await Task.Delay(200); // Brief delay before retry
                            }
                        }

                        if (!injected)
                        {
                            System.Diagnostics.Trace.WriteLine($"[MainForm.OnSpawnRequested] All injection attempts failed for {agentName}");
                            _debugLogService.Warning("MainForm", $"Failed to auto-submit initializing prompt for {agentName} after 3 attempts");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Trace.WriteLine($"[MainForm.OnSpawnRequested] Timeout waiting for renderer ready for {agentName}, skipping auto-inject");
                        _debugLogService.Warning("MainForm", $"Failed to auto-submit initializing prompt for {agentName} - renderer not ready after 10 seconds");
                    }
                }

                return (true, docId, null);
            }
            catch (Exception ex)
            {
                _debugLogService.Error("MainForm", $"Spawn failed: {ex.Message}");
                return (false, null, $"Exception during spawn: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn a headless AgentProcess with piped stdin/stdout.
        /// </summary>
        private async Task<(bool success, AgentProcess agent, string error)> OnSpawnAgentRequested(
            string agentName,
            string workingDir,
            string initialPrompt,
            string mcpConfigPath,
            string spawnerName,
            string taskDescription,
            string subagentType)
        {
            try
            {
                _debugLogService.Info("MainForm", $"AgentProcess spawn requested: {agentName} by {spawnerName}");

                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    workingDir = Path.Combine(
                        Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "",
                        "Deploy");
                }

                var agent = new AgentProcess();
                await agent.SpawnAsync(
                    prompt: initialPrompt,
                    workingDir: workingDir,
                    mcpConfigPath: mcpConfigPath);

                // Register with MessageBroker so other terminals can message this agent
                string agentDocId = $"agent-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                var regResult = _mcpServer?.Broker?.RegisterTerminal(agentName, agentDocId);
                string agentTerminalId = regResult?.TerminalId ?? agentDocId;

                // Map the terminal ID to this AgentProcess for message delivery
                lock (_agentProcessMap)
                {
                    _agentProcessMap[agentTerminalId] = agent;
                }

                // Create a dedicated panel for this agent on the UI thread
                if (InvokeRequired)
                {
                    Invoke(new Action(() => CreateAgentPanel(agent, agentName, agentTerminalId, spawnerName, taskDescription, subagentType)));
                }
                else
                {
                    CreateAgentPanel(agent, agentName, agentTerminalId, spawnerName, taskDescription, subagentType);
                }

                // Clean up on exit
                agent.ProcessExited += (s, exitCode) =>
                {
                    lock (_agentProcessMap)
                    {
                        _agentProcessMap.Remove(agentTerminalId);
                    }

                    // Embedded agents are cleaned up via the Stopped handler in CreateAgentPanel.

                    _debugLogService.Info("MainForm", $"AgentProcess {agentName} exited with code {exitCode}");
                };

                _debugLogService.Info("MainForm", $"AgentProcess {agentName} spawned successfully (PID: {agent.ProcessId})");
                return (true, agent, null);
            }
            catch (Exception ex)
            {
                _debugLogService.Error("MainForm", $"AgentProcess spawn failed: {ex.Message}");
                return (false, null, $"AgentProcess spawn failed: {ex.Message}");
            }
        }

        private async Task<bool> OnMcpMessageDelivery(string messageId, string recipientId, string sender, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] OnMcpMessageDelivery ENTRY: messageId={messageId}, from={sender}, to={recipientId}");
            _debugLogService.Trace("MainForm", $"OnMcpMessageDelivery: messageId={messageId}, from={sender}, to={recipientId}, msg={message.Substring(0, Math.Min(50, message.Length))}...");

            // Check for duplicate - if we've delivered this message recently, skip it
            lock (_deduplicationLock)
            {
                // Clean up old entries first
                var cutoff = DateTime.Now.AddMinutes(-DeduplicationCacheMinutes);
                var expiredKeys = _deliveredMessageCache.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var key in expiredKeys)
                {
                    _deliveredMessageCache.Remove(key);
                }

                // Check if this message was already delivered
                if (_deliveredMessageCache.ContainsKey(messageId))
                {
                    _debugLogService.Info("MainForm", $"DUPLICATE DETECTED! Message {messageId} was already delivered at {_deliveredMessageCache[messageId]:HH:mm:ss.fff}. Skipping.");
                    return true; // Return true to mark as "delivered" (skip retry)
                }
            }

            var recipientTerminal = _mcpServer.Broker.GetTerminal(recipientId);
            string recipientName = recipientTerminal?.Name ?? recipientId;

            // ORACLE: On-demand spawn — if Oracle isn't running, spawn its terminal then deliver via channel.
            // If Oracle IS running, reset idle timer and let the normal channel delivery path handle it.
            if (_oracleService != null && string.Equals(recipientName, OracleService.OracleName, StringComparison.OrdinalIgnoreCase))
            {
                if (!_oracleService.IsRunning && !_oracleSpawning)
                {
                    _oracleSpawning = true;
                    _debugLogService?.Info("MainForm", $"Oracle not running — spawning for message {messageId} from {sender}");
                    _ = SpawnOracleAndDeliverAsync(messageId, sender, message);
                    return true; // We're handling delivery asynchronously
                }

                if (_oracleService.IsRunning)
                {
                    // Oracle is running — reset idle timer and fall through to normal channel delivery
                    _oracleService.ResetIdleTimer();
                    _debugLogService?.Trace("MainForm", $"Oracle running — channel delivery for message {messageId}");
                }
                else
                {
                    // Oracle is spawning (another message arrived during spawn) — let broker retry later
                    _debugLogService?.Info("MainForm", $"Oracle spawning — message {messageId} will retry via broker queue");
                    return false;
                }
            }

            // PRIMARY: Channel delivery — POST to recipient's Claude Code Channel HTTP port.
            // This pushes a <channel> event directly into the Claude Code session (instant, no polling).
            if (recipientTerminal?.ChannelPort != null)
            {
                try
                {
                    bool channelDelivery = await DeliverViaChannel(recipientTerminal.ChannelPort.Value, sender, message, "normal");
                    if (channelDelivery)
                    {
                        _debugLogService.Info("MainForm", $"Channel delivery SUCCESS for message {messageId} to {recipientName} (port {recipientTerminal.ChannelPort})");
                        lock (_deduplicationLock)
                        {
                            _deliveredMessageCache[messageId] = DateTime.Now;
                        }
                        return true;
                    }
                    _debugLogService.Warning("MainForm", $"Channel delivery FAILED for message {messageId}, falling back to inbox file");
                }
                catch (Exception ex)
                {
                    _debugLogService.Warning("MainForm", $"Channel delivery EXCEPTION for message {messageId}: {ex.Message}, falling back to inbox file");
                }
            }

            // FALLBACK: File-based delivery (legacy) — write to inbox file.
            // Used when the recipient doesn't have a channel port (not launched with --channels).
            bool fileDelivery = InboxFileWriter.WriteMessage(recipientName, messageId, sender, message);
            if (fileDelivery)
            {
                _debugLogService.Info("MainForm", $"Inbox file delivery SUCCESS for message {messageId} to {recipientName}");
                lock (_deduplicationLock)
                {
                    _deliveredMessageCache[messageId] = DateTime.Now;
                }
                return true;
            }

            // Fallback: queue for terminal injection if file delivery fails
            _debugLogService.Warning("MainForm", $"Inbox file delivery FAILED for message {messageId}, falling back to terminal injection");
            var completionSource = new TaskCompletionSource<bool>();

            // Queue message with completion callback
            lock (_injectionLock)
            {
                _messageQueue.Enqueue((messageId, recipientId, sender, message, completionSource));
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] Message {messageId} queued. Queue size: {_messageQueue.Count}, InProgress: {_injectionInProgress}");
                _debugLogService.Trace("MainForm", $"Message {messageId} queued with completion tracking. Queue size: {_messageQueue.Count}, InProgress: {_injectionInProgress}");
                if (!_injectionInProgress)
                {
                    _injectionInProgress = true;
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] Starting ProcessNextMessage");
                    _debugLogService.Trace("MainForm", "Starting ProcessNextMessage");
                    ProcessNextMessage();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] Injection already in progress, message will wait in queue");
                    _debugLogService.Trace("MainForm", "Injection already in progress, message will wait in queue");
                }
            }

            // Wait for injection to complete (with 10 second timeout to prevent hang)
            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _debugLogService.Warning("MainForm", "Message delivery timed out after 10 seconds");
                return false;
            }

            return await completionSource.Task;
        }

        /// <summary>
        /// Shared HttpClient for channel message delivery. Reused across calls to avoid
        /// socket exhaustion (TIME_WAIT buildup from per-request HttpClient instances).
        /// </summary>
        private static readonly System.Net.Http.HttpClient _channelHttpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Delivers a message to a terminal's Claude Code Channel via HTTP POST.
        /// The channel server pushes it as a native <channel> event into the Claude Code session.
        /// </summary>
        private async Task<bool> DeliverViaChannel(int channelPort, string sender, string message, string priority)
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    from = sender,
                    message = message,
                    priority = priority ?? "normal",
                    timestamp = DateTime.UtcNow.ToString("o")
                });

                var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var response = await _channelHttpClient.PostAsync($"http://127.0.0.1:{channelPort}/message", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _debugLogService.Warning("MainForm", $"DeliverViaChannel failed (port {channelPort}): {ex.Message}");
                return false;
            }
        }

        private void ProcessNextMessage()
        {
            _debugLogService.Trace("MainForm", "ProcessNextMessage called");

            (string messageId, string recipientId, string sender, string message, TaskCompletionSource<bool> completion) item;

            lock (_injectionLock)
            {
                if (_messageQueue.Count == 0)
                {
                    _injectionInProgress = false;
                    _debugLogService.Trace("MainForm", "Queue empty, setting InProgress=false");
                    return;
                }
                item = _messageQueue.Dequeue();
                _debugLogService.Trace("MainForm", $"Dequeued message {item.messageId} from {item.sender}. Remaining in queue: {_messageQueue.Count}");
            }

            BeginInvoke(async () =>
            {
                _debugLogService.Trace("MainForm", $"BeginInvoke executing for message {item.messageId} from {item.sender}");

                bool injectionSuccess = false;

                try
                {
                    // Thread-safe terminal lookup with lock
                    TerminalDocument doc = null;
                    lock (_terminalDocMapLock)
                    {
                        _terminalDocMap.TryGetValue(item.recipientId, out doc);
                    }

                    if (doc != null)
                    {
                        _debugLogService.Trace("MainForm", $"Found terminal doc for {item.recipientId}, testing background injection...");

                        // TEST: Removed all focus calls to prevent focus stealing
                        // doc.DockHandler.Activate();
                        // await Task.Delay(200);
                        // _debugLogService.Trace("MainForm", "Activation settled, focusing terminal...");
                        // doc.FocusTerminal();
                        // await Task.Delay(50);

                        // Wait for renderer to be ready before injecting
                        _debugLogService.Trace("MainForm", "Waiting for renderer to be ready...");
                        bool isReady = await WaitForRendererReadyAsync(doc, 5000);

                        if (!isReady)
                        {
                            _debugLogService.Warning("MainForm", $"Renderer not ready for {item.recipientId} after 5 second timeout");
                            injectionSuccess = false;
                        }
                        else
                        {
                            _debugLogService.Trace("MainForm", "Renderer ready, calling InjectInputAsync...");
                            var injectStart = DateTime.Now;

                            // Wait for injection to complete (Enter key sent) before processing next
                            injectionSuccess = await doc.InjectInputAsync($"[{item.sender}]: {item.message}");

                            var injectDuration = (DateTime.Now - injectStart).TotalMilliseconds;
                            _debugLogService.Trace("MainForm", $"InjectInputAsync completed in {injectDuration:F0}ms with result: {injectionSuccess}");
                        }
                    }
                    else
                    {
                        _debugLogService.Warning("MainForm", $"Terminal doc NOT FOUND for recipientId={item.recipientId}");
                        injectionSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    _debugLogService.Error("MainForm", $"ERROR during message injection: {ex.Message}");
                    injectionSuccess = false;
                }
                finally
                {
                    // Only cache successfully delivered messages (prevention by design)
                    if (injectionSuccess)
                    {
                        lock (_deduplicationLock)
                        {
                            _deliveredMessageCache[item.messageId] = DateTime.Now;
                            _debugLogService.Trace("MainForm", $"Message {item.messageId} added to delivered cache. Cache size: {_deliveredMessageCache.Count}");
                        }
                    }

                    // Complete the TaskCompletionSource to unblock the awaiting sender
                    item.completion?.TrySetResult(injectionSuccess);
                    _debugLogService.Trace("MainForm", $"Completion signaled with result: {injectionSuccess}");
                }

                // Process next message in queue
                _debugLogService.Trace("MainForm", "Calling ProcessNextMessage for next item...");
                ProcessNextMessage();
            });
        }

        private void InitializeDashboardHeader()
        {
            _dashboardHeader = new DashboardHeader.DashboardHeaderControl();

            // Wire action events to existing MainForm methods
            _dashboardHeader.NewTerminalRequested += () => AddNewTerminal();
            _dashboardHeader.ToggleThemeRequested += () => ToggleTheme();
            _dashboardHeader.SettingsRequested += () => ShowSettingsDialog();
            _dashboardHeader.AboutRequested += () => ShowAboutDialog();
            _dashboardHeader.ShowChatHistoryRequested += () => ShowChatHistoryDialog();
            _dashboardHeader.ExitRequested += () => Close();

            _dashboardHeader.DocsRequested += () =>
            {
                var docsPath = System.IO.Path.Combine(Application.StartupPath, "docs", "html", "index.html");
                if (System.IO.File.Exists(docsPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = docsPath,
                        UseShellExecute = true
                    });
                }
            };

            _dashboardHeader.TogglePanelRequested += (panel) =>
            {
                switch (panel)
                {
                    case "tasks": ToggleTasksPanel(); break;
                    case "chat": ToggleChatPanel(); break;
                    case "activity": ToggleActivityPanel(); break;
                    case "office": ToggleOfficePanel(); break;
                    case "profiles": ToggleProfilePanel(); break;
                    case "inbox": ToggleInboxPanel(); break;
                    case "debug": ToggleDebugPanel(); break;
                    case "preview": ToggleFilePreviewPanel(); break;
                    case "projects": ToggleProjectPanel(); break;
                }
            };

            _dashboardHeader.GridLayoutRequested += (layout) =>
            {
                switch (layout)
                {
                    case "2x2": ApplyGridLayout(GridLayoutManager.GridPreset.Grid2x2); break;
                    case "2x3": ApplyGridLayout(GridLayoutManager.GridPreset.Grid2x3); break;
                    case "3x2": ApplyGridLayout(GridLayoutManager.GridPreset.Grid3x2); break;
                    case "h2": ApplyGridLayout(GridLayoutManager.GridPreset.Horizontal2); break;
                    case "v2": ApplyGridLayout(GridLayoutManager.GridPreset.Vertical2); break;
                    case "h3": ApplyGridLayout(GridLayoutManager.GridPreset.Horizontal3); break;
                    case "v3": ApplyGridLayout(GridLayoutManager.GridPreset.Vertical3); break;
                    case "reset": _gridManager.ResetToTabs(); break;
                }
            };

            _dashboardHeader.SwitchTerminalRequested += (name) =>
            {
                // Find the terminal by name and activate it
                var terminal = _gridManager.GetTerminalDocuments()
                    .FirstOrDefault(t => string.Equals(t.TabText, name, StringComparison.OrdinalIgnoreCase));
                if (terminal != null)
                {
                    terminal.Activate();
                }
            };

            _dashboardHeader.DashboardReady += () =>
            {
                DashboardContentReady?.Invoke(this, EventArgs.Empty);
            };

            Controls.Add(_dashboardHeader);
        }

        private void RefreshDashboardProjectInfo()
        {
            if (_dashboardHeader == null) return;

            var projectName = _currentProject?.Name ?? "MultiTerminal";
            var branch = "—";

            try
            {
                // Get git branch from the working directory
                var workingDir = _currentProject?.SourcePath ?? _currentProject?.Path
                    ?? AppDomain.CurrentDomain.BaseDirectory;

                if (System.IO.Directory.Exists(workingDir))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "branch --show-current",
                        WorkingDirectory = workingDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        branch = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(2000);
                        if (string.IsNullOrEmpty(branch)) branch = "—";
                    }
                }
            }
            catch { /* git not available — leave as "—" */ }

            _dashboardHeader.UpdateProjectInfo(projectName, branch);
        }

        // Legacy toolbar — kept for reference during transition
        private void InitializeToolbar()
        {
            _toolStrip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new DarkToolStripRenderer(),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            // New Terminal button
            var newTerminalBtn = new ToolStripButton
            {
                Text = "New Terminal",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            newTerminalBtn.Click += (s, e) => AddNewTerminal();

            // Project panel toggle button
            _projectPanelButton = new ToolStripButton
            {
                Text = "Project",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _projectPanelButton.Click += (s, e) => ToggleProjectPanel();

            // Chat panel toggle button
            _chatPanelButton = new ToolStripButton
            {
                Text = "Chat",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Multi-Claude Chat Panel"
            };
            _chatPanelButton.Click += (s, e) => ToggleChatPanel();

            // Chat history button
            _chatHistoryButton = new ToolStripButton
            {
                Text = "History",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "View Chat Message History"
            };
            _chatHistoryButton.Click += (s, e) => ShowChatHistoryDialog();

            // Activity panel toggle button (Mission Control)
            _activityPanelButton = new ToolStripButton
            {
                Text = "Activity",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Activity Monitor (Mission Control)"
            };
            _activityPanelButton.Click += (s, e) => ToggleActivityPanel();

            // Tasks panel toggle button (Kanban board)
            _tasksPanelButton = new ToolStripButton
            {
                Text = "Tasks",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Tasks Panel (Kanban Board)"
            };
            _tasksPanelButton.Click += (s, e) => ToggleTasksPanel();

            // Profile panel toggle button
            _profilePanelButton = new ToolStripButton
            {
                Text = "Profiles",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Team Profiles Panel"
            };
            _profilePanelButton.Click += (s, e) => ToggleProfilePanel();

            // Inbox panel toggle button
            _inboxPanelButton = new ToolStripButton
            {
                Text = "Inbox",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Inbox Panel (Notifications)"
            };
            _inboxPanelButton.Click += (s, e) => ToggleInboxPanel();

            // Office panel toggle button (Pixel Art Visualization)
            _officePanelButton = new ToolStripButton
            {
                Text = "Office",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Office View (Animated Agent Visualization)"
            };
            _officePanelButton.Click += (s, e) => ToggleOfficePanel();

            // Agent panel toggle button
            _agentPanelButton = new ToolStripButton
            {
                Text = "Agent",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Agent Conversation Panel"
            };
            _agentPanelButton.Click += (s, e) => ToggleAgentPanel();

            // Debug panel toggle button
            _debugPanelButton = new ToolStripButton
            {
                Text = "Debug",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle Debug Log Panel"
            };
            _debugPanelButton.Click += (s, e) => ToggleDebugPanel();

            // HUD toggle button
            _hudButton = new ToolStripButton
            {
                Text = "HUD",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Show/Hide Task HUD on Active Terminal"
            };
            _hudButton.Click += (s, e) => ToggleHud();

            // Recent Folders dropdown
            _recentFoldersDropdown = new ToolStripDropDownButton
            {
                Text = "Recent",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _recentFoldersDropdown.DropDownOpening += OnRecentFoldersDropDownOpening;

            // Grid Layout dropdown
            _gridDropdown = new ToolStripDropDownButton
            {
                Text = "Grid Layout",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };

            AddDropdownItem("2x2 Grid", () => ApplyGridLayout(GridLayoutManager.GridPreset.Grid2x2));
            AddDropdownItem("2x3 Grid", () => ApplyGridLayout(GridLayoutManager.GridPreset.Grid2x3));
            AddDropdownItem("3x2 Grid", () => ApplyGridLayout(GridLayoutManager.GridPreset.Grid3x2));
            _gridDropdown.DropDownItems.Add(new ToolStripSeparator());
            AddDropdownItem("2 Horizontal", () => ApplyGridLayout(GridLayoutManager.GridPreset.Horizontal2));
            AddDropdownItem("2 Vertical", () => ApplyGridLayout(GridLayoutManager.GridPreset.Vertical2));
            AddDropdownItem("3 Horizontal", () => ApplyGridLayout(GridLayoutManager.GridPreset.Horizontal3));
            AddDropdownItem("3 Vertical", () => ApplyGridLayout(GridLayoutManager.GridPreset.Vertical3));
            _gridDropdown.DropDownItems.Add(new ToolStripSeparator());
            AddDropdownItem("Reset to Tabs", () => _gridManager.ResetToTabs());

            // File preview panel toggle button
            _filePreviewPanelButton = new ToolStripButton
            {
                Text = "Preview",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Toggle File Preview Panel"
            };
            _filePreviewPanelButton.Click += (s, e) => ToggleFilePreviewPanel();

            // Theme toggle button (icon-based)
            _themeButton = new ToolStripButton
            {
                Text = "\u2600", // Sun symbol - click to switch to light
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _themeButton.Click += (s, e) => ToggleTheme();

            // Settings button (gear icon)
            _settingsButton = new ToolStripButton
            {
                Text = "\u2699", // Gear symbol
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Settings"
            };
            _settingsButton.Click += (s, e) => ShowSettingsDialog();

            // Documentation button (book icon)
            var docsButton = new ToolStripButton
            {
                Text = "\uD83D\uDCD6", // Open book symbol
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Documentation"
            };
            docsButton.Click += (s, e) =>
            {
                var docsPath = System.IO.Path.Combine(Application.StartupPath, "docs", "html", "index.html");
                if (System.IO.File.Exists(docsPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = docsPath,
                        UseShellExecute = true
                    });
                }
            };

            // About button (info icon)
            _aboutButton = new ToolStripButton
            {
                Text = "\u2139", // Info symbol
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "About MultiTerminal"
            };
            _aboutButton.Click += (s, e) => ShowAboutDialog();

            // Add items to toolbar
            _toolStrip.Items.Add(newTerminalBtn);
            _toolStrip.Items.Add(_projectPanelButton);
            _toolStrip.Items.Add(_chatPanelButton);
            _toolStrip.Items.Add(_chatHistoryButton);
            _toolStrip.Items.Add(_activityPanelButton);
            _toolStrip.Items.Add(_tasksPanelButton);
            _toolStrip.Items.Add(_profilePanelButton);
            _toolStrip.Items.Add(_inboxPanelButton);
            _toolStrip.Items.Add(_officePanelButton);
            _toolStrip.Items.Add(_debugPanelButton);
            _toolStrip.Items.Add(_filePreviewPanelButton);
            _toolStrip.Items.Add(_recentFoldersDropdown);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_gridDropdown);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_themeButton);
            _toolStrip.Items.Add(docsButton);
            _toolStrip.Items.Add(_settingsButton);
            _toolStrip.Items.Add(_aboutButton);

            Controls.Add(_toolStrip);
        }

        private void AddDropdownItem(string text, Action onClick)
        {
            var item = new ToolStripMenuItem(text)
            {
                ForeColor = Color.White
            };
            item.Click += (s, e) => onClick();
            _gridDropdown.DropDownItems.Add(item);
        }

        private void ShowOwnerProfileDialogIfNeeded()
        {
            if (_ownerProfileService.IsConfigured()) return;

            ShowOwnerProfileDialog();
        }

        /// <summary>
        /// Shows the owner profile dialog. Called on first run or from Settings.
        /// </summary>
        private void ShowOwnerProfileDialog()
        {
            var dialog = new Dialogs.OwnerProfileDialog();
            new WindowInteropHelper(dialog) { Owner = this.Handle };

            // Pre-populate if profile exists (editing)
            var existing = _ownerProfileService.GetProfile();
            if (existing != null)
            {
                dialog.LoadExisting(existing.FullName, existing.Email,
                    existing.GitHubUsername, existing.HasGitHubToken);
            }

            if (dialog.ShowDialog() == true)
            {
                var profile = existing ?? new Models.OwnerProfile();
                profile.FullName = dialog.FullName;
                profile.Email = dialog.Email;
                profile.GitHubUsername = string.IsNullOrWhiteSpace(dialog.GitHubUsername)
                    ? null : dialog.GitHubUsername;

                _ownerProfileService.SaveProfile(profile);

                // Save token if provided (and not the placeholder)
                var token = dialog.GitHubToken;
                if (!string.IsNullOrEmpty(token) && token != "placeholder-existing")
                {
                    _ownerProfileService.SaveGitHubToken(token);
                }
            }
        }

        private void ShowSettingsDialog()
        {
            var dialog = new SettingsWpfDialog(_settings, _currentTheme.IsDark, _ownerProfileService);
            new WindowInteropHelper(dialog) { Owner = this.Handle };
            if (dialog.ShowDialog() == true)
            {
                ApplySettingsFromDialog(dialog);
            }
        }

        private void ShowAboutDialog()
        {
            using (var dialog = new Dialogs.AboutDialog(_currentTheme))
            {
                dialog.ShowDialog(this);
            }
        }

        private void ShowChatHistoryDialog()
        {
            using (var dialog = new Dialogs.ChatHistoryDialog(_chatTaskDatabase, _currentTheme))
            {
                dialog.ShowDialog(this);
            }
        }

        private void ApplySettingsFromDialog(SettingsWpfDialog dialog)
        {
            // Apply toolbar font (legacy toolstrip)
            if (_toolStrip != null)
                _toolStrip.Font = new Font(_toolStrip.Font.FontFamily, dialog.ToolbarFontSize);

            // Apply terminal font to all open terminals
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                doc.SetFontSize(dialog.TerminalFontSize);
            }

            // Apply project panel font
            _projectPanel?.SetFontSize(dialog.ProjectPanelFontSize);

            // Apply chat panel font
            _chatPanel?.SetFontSize(dialog.ChatPanelFontSize);

            // Apply tasks panel font
            _tasksPanel?.SetFontSize(dialog.TasksPanelFontSize);

            // Apply activity panel font
            _activityPanel?.SetFontSize(dialog.ActivityPanelFontSize);
        }

        private void LoadSettings()
        {
            _settings = new SettingsService();

            // Load theme preference
            string themeSetting = _settings.Get("Theme");
            if (themeSetting == "Light")
            {
                _currentTheme = TerminalTheme.Light;
            }

            // Restore window bounds
            var savedState = _settings.GetWindowState();
            var bounds = _settings.GetWindowBounds();

            // If window will be maximized, don't set manual bounds - causes multi-monitor shift bug
            if (savedState == FormWindowState.Maximized)
            {
                // Just use default size/position - maximization will handle it
                Size = new Size(1200, 800);
                StartPosition = FormStartPosition.CenterScreen;
            }
            else if (bounds.HasValue && IsVisibleOnAnyScreen(bounds.Value))
            {
                StartPosition = FormStartPosition.Manual;
                Left = bounds.Value.Left;
                Top = bounds.Value.Top;
                Width = bounds.Value.Width;
                Height = bounds.Value.Height;
            }
            else
            {
                Size = new Size(1200, 800);
                StartPosition = FormStartPosition.CenterScreen;
            }

            // Restore window state after form is shown
            this.Shown += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[MainForm] Shown event #1: Setting WindowState...");
                System.Diagnostics.Trace.WriteLine($"[MainForm] Shown event #1: Saved state is {savedState}");
                WindowState = savedState;
                System.Diagnostics.Trace.WriteLine("[MainForm] Shown event #1: WindowState set successfully");
                // MCP server now initialized earlier in constructor (before RestoreSession)
            };

            // Apply toolbar font size (legacy toolstrip)
            float toolbarFontSize = _settings.GetToolbarFontSize();
            if (_toolStrip != null)
                _toolStrip.Font = new Font(_toolStrip.Font.FontFamily, toolbarFontSize);

            // Restore panel font sizes
            float projectFontSize = _settings.GetPromptsFontSize();
            _projectPanel?.SetFontSize(projectFontSize);

            float chatFontSize = _settings.GetChatFontSize();
            _chatPanel?.SetFontSize(chatFontSize);

            float tasksFontSize = _settings.GetTasksFontSize();
            _tasksPanel?.SetFontSize(tasksFontSize);

            float activityFontSize = _settings.GetActivityFontSize();
            _activityPanel?.SetFontSize(activityFontSize);

            // Restore zoom levels for standalone WebView2 panels
            // SetZoomFactor stores a pending value applied when WebView2 initializes, so calling early is safe
            _tasksPanel?.SetZoomFactor(_settings.GetTasksPanelZoom());
            _chatPanel?.SetZoomFactor(_settings.GetChatPanelZoom());
            _officePanel?.SetZoomFactor(_settings.GetOfficePanelZoom());

            // Migrate legacy ShowPromptsPanel setting to new Panel_ProjectPanel_Visible format
            if (_settings.Get("ShowPromptsPanel") == "true" && _settings.Get("Panel_ProjectPanel_Visible") == null)
            {
                _settings.Set("Panel_ProjectPanel_Visible", "true");
                _settings.Set("Panel_ProjectPanel_DockState", "DockLeft");
            }

            // Panel visibility is restored in RestorePanelStates() called from RestoreSession
        }

        private bool IsVisibleOnAnyScreen(Rectangle rect)
        {
            return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
        }

        private static bool IsTemporaryAgent(string name)
        {
            return name.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyTheme(bool isInitialLoad = false)
        {
            if (_currentTheme.IsDark)
            {
                BackColor = Color.FromArgb(30, 30, 30);

                // Only set DockPanel theme on initial load (before any documents are open)
                if (isInitialLoad)
                {
                    _dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015DarkTheme();
                }
            }
            else
            {
                BackColor = Color.FromArgb(240, 240, 240);

                // Only set DockPanel theme on initial load (before any documents are open)
                if (isInitialLoad)
                {
                    _dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015LightTheme();
                }
            }

            // Update dashboard header theme
            _dashboardHeader?.ApplyTheme(_currentTheme.IsDark);

            // Legacy toolstrip theming (kept for fallback)
            if (_toolStrip != null)
            {
                Color textColor = _currentTheme.IsDark ? Color.White : Color.FromArgb(30, 30, 30);
                _toolStrip.BackColor = _currentTheme.IsDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 236, 242);
                _toolStrip.Renderer = _currentTheme.IsDark ? (ToolStripRenderer)new DarkToolStripRenderer() : new LightToolStripRenderer();
                foreach (ToolStripItem item in _toolStrip.Items)
                    item.ForeColor = textColor;
            }

            // Update all terminal themes
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                doc.SetTheme(_currentTheme);
            }

            // Update project panel theme
            _projectPanel?.SetTheme(_currentTheme);

            // Update activity panel theme
            _activityPanel?.ApplyTheme(_currentTheme.IsDark);

            // Update tasks panel theme
            _tasksPanel?.ApplyTheme(_currentTheme.IsDark);

            // Update profile panel theme
            _profilePanel?.SetTheme(_currentTheme.IsDark);

            // Update inbox panel theme
            _inboxPanel?.ApplyTheme(_currentTheme.IsDark);

            // Update office panel theme
            _officePanel?.ApplyTheme(_currentTheme.IsDark);

            // Update embedded agent control themes
            foreach (var info in _embeddedAgentMap.Values.ToList())
            {
                try { info.Control?.ApplyTheme(_currentTheme.IsDark); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainForm] Embedded agent theme error: {ex.Message}"); }
            }

            // Update all open lifecycle board windows
            TaskLifecycleBoardForm.ApplyThemeToAll(_currentTheme.IsDark);

        }

        private void ToggleTheme()
        {
            _currentTheme = _currentTheme.IsDark ? TerminalTheme.Light : TerminalTheme.Dark;
            _settings.Set("Theme", _currentTheme.IsDark ? "Dark" : "Light");
            ApplyTheme();
        }

        // Name pool for auto-registration (A-Z names)
        private static readonly string[] _terminalNamePool = new[]
        {
            "Alice", "Bob", "Charlie", "Diana", "Eve", "Frank", "Grace", "Henry",
            "Iris", "James", "Kate", "Leo", "Maya", "Noah", "Olive", "Paul",
            "Quinn", "Ruby", "Sam", "Tara", "Uma", "Vera", "Wade", "Xena", "Yuri", "Zara"
        };

        public void AddNewTerminal(string workingDirectory = null, float? fontSize = null, bool forceTabMode = false, string identityName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null)
        {
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] ===== START =====");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] workingDirectory: '{workingDirectory ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] fontSize: {fontSize?.ToString() ?? "null"}");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] forceTabMode: {forceTabMode}");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] identityName: '{identityName ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] autoRunCommand: '{autoRunCommand ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] projectId: '{projectId ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] isTeamLead: '{isTeamLead}'");
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] Creating TerminalDocument...");
            var doc = new TerminalDocument();
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] TerminalDocument created");

            doc.Terminal.SetDebugLogService(_debugLogService);
            doc.SetDebugLogService(_debugLogService); // Enable status bar logging
            doc.SetTheme(_currentTheme);
            doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
            doc.TerminalExited += OnTerminalExited;
            doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
            doc.AgentSplitRatioChanged += OnAgentSplitRatioChanged;
            doc.HudSplitRatioChanged += OnHudSplitRatioChanged;
            doc.StatusBarHeightChanged += OnStatusBarHeightChanged;
            doc.TaskHudZoomChanged += OnTaskHudZoomChanged;
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;

            // Wire start screen events
            WireStartScreenEvents(doc);

            string terminalName = null;

            // Smart terminal placement based on MaxGridPanes and MaxTabsPerGrid settings
            int maxGrids = _settings?.GetMaxGridPanes() ?? 4;
            int maxTabs = _settings?.GetMaxTabsPerGrid() ?? 3;
            var activeDoc = _dockPanel.ActiveDocument as TerminalDocument;

            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Showing terminal in DockPanel (maxGrids: {maxGrids}, maxTabs: {maxTabs}, forceTab: {forceTabMode})...");
            if (forceTabMode)
            {
                doc.Show(_dockPanel, DockState.Document);
            }
            else
            {
                // Get distinct panes containing terminal documents
                var terminalDocs = _dockPanel.Documents.OfType<TerminalDocument>().ToList();
                var panes = terminalDocs.Select(d => d.Pane).Where(p => p != null).Distinct().ToList();
                int gridCount = panes.Count;

                if (gridCount == 0)
                {
                    // First terminal — just show it
                    doc.Show(_dockPanel, DockState.Document);
                }
                else if (gridCount < maxGrids)
                {
                    // Room for another grid pane — split from active pane
                    var splitFrom = activeDoc?.Pane ?? panes[panes.Count - 1];
                    doc.Show(splitFrom, DockAlignment.Right, 0.5);
                }
                else
                {
                    // All grid slots taken — find a pane with room for tabs
                    DockPane bestPane = null;
                    int fewestTabs = int.MaxValue;
                    foreach (var pane in panes)
                    {
                        int tabCount = pane.Contents.Count;
                        if (tabCount < maxTabs && tabCount < fewestTabs)
                        {
                            fewestTabs = tabCount;
                            bestPane = pane;
                        }
                    }

                    if (bestPane != null)
                    {
                        // Add as tab to the pane with fewest tabs
                        doc.Show(bestPane, null);
                    }
                    else
                    {
                        // All panes full — undock as floating window
                        doc.Show(_dockPanel, DockState.Float);
                    }
                }
            }
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] Terminal shown in DockPanel");

            // When called with no launch parameters, show the start screen instead of starting a shell.
            // Any meaningful parameter (directory, identity, command, project) triggers immediate start.
            bool hasLaunchParams = !string.IsNullOrEmpty(workingDirectory)
                || !string.IsNullOrEmpty(identityName)
                || !string.IsNullOrEmpty(autoRunCommand)
                || !string.IsNullOrEmpty(projectId);

            // Register with MCP server AFTER adding to DockPanel so the
            // TerminalRegistered event handler can find this doc by DocId
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] Registering terminal with MCP server...");
            if (_mcpServer?.Broker != null)
            {
                if (!string.IsNullOrEmpty(identityName))
                {
                    System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Identity name provided: '{identityName}'");

                    // Apply team lead naming convention: "Team Lead {Name} - {3-digit random}"
                    if (isTeamLead)
                    {
                        string suffix = Random.Shared.Next(100, 999).ToString();
                        identityName = $"Team Lead {identityName} - {suffix}";
                        System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Team lead naming applied: '{identityName}'");
                    }

                    terminalName = PreRegisterTerminalWithName(doc.DocId, identityName, isTeamLead);
                    System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] PreRegisterTerminalWithName returned: '{terminalName}'");
                }
                else if (hasLaunchParams)
                {
                    System.Diagnostics.Trace.WriteLine("[AddNewTerminal] No identity name, using 'Unassigned'");
                    terminalName = "Unassigned";
                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId);
                }
                // else: start screen tab — no MCP registration yet; happens when user launches a project
            }
            System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Terminal registered as: '{terminalName ?? "null"}'");

            if (hasLaunchParams)
            {
                // Start the terminal with specified or default directory and optional auto-run command
                string dir = workingDirectory ?? _settings?.GetLastDirectory();
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Starting terminal...");
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal]   dir: '{dir}'");
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal]   terminalName: '{terminalName}'");
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal]   autoRunCommand: '{autoRunCommand ?? "null"}'");
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Calling doc.StartTerminal('{dir}', '{terminalName}', '{autoRunCommand ?? "null"}', '{spawnerName ?? "null"}', '{projectId ?? "null"}', isTeamLead={isTeamLead}, gatewayProfile='{gatewayProfile ?? "null"}')...");
                doc.StartTerminal(dir, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile);
                System.Diagnostics.Trace.WriteLine("[AddNewTerminal] doc.StartTerminal returned");
            }
            else
            {
                // No launch params: show start screen so user can pick a project
                System.Diagnostics.Trace.WriteLine("[AddNewTerminal] No launch params — showing start screen");
                doc.ShowStartScreen();
            }

            // Apply specified or default font size and saved split ratios
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);
            if (_settings != null)
            {
                doc.ApplyAgentSplitRatio(_settings.GetAgentPanelSplitRatio());
                doc.ApplyHudSplitRatio(_settings.GetHudSplitRatio());
                doc.ApplyTaskHudZoom(_settings.GetTaskHudZoom());
                doc.ApplyStatusBarHeight(_settings.GetStatusBarHeight());
            }

            // Focus the new terminal and track it as last active
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] Focusing terminal...");
            doc.Activate();
            if (!doc.IsStartScreenVisible)
                doc.FocusTerminal();
            _lastActiveTerminal = doc;
            System.Diagnostics.Trace.WriteLine("[AddNewTerminal] Completed");
        }

        /// <summary>
        /// Wires the start screen events on a newly created TerminalDocument and injects the project database.
        /// Call this at every TerminalDocument creation site.
        /// </summary>
        private void WireStartScreenEvents(TerminalDocument doc)
        {
            doc.SetProjectDatabase(_sharedProjectDatabase);
            doc.ProjectLaunched += OnStartScreenProjectLaunched;
            doc.StartScreenOpenPowerShellRequested += OnStartScreenOpenPowerShell;
            doc.StartScreenJustClaudeRequested += OnStartScreenJustClaude;
            doc.StartScreenNewProjectRequested += OnStartScreenNewProject;
        }

        /// <summary>
        /// Handles a project selection from the start screen.
        /// Loads the project, builds the Claude Code launch command, updates last_opened_at,
        /// then calls StartTerminal() on the source document.
        /// </summary>
        private void OnStartScreenProjectLaunched(object sender, StartScreenLaunchEventArgs e)
        {
            if (sender is not TerminalDocument doc) return;

            try
            {
                var project = _sharedProjectDatabase?.GetRichProject(e.ProjectId);
                if (project == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[StartScreen] Project not found: {e.ProjectId}");
                    return;
                }

                // Update last opened timestamp
                project.LastOpenedAt = DateTime.Now;
                _sharedProjectDatabase?.SaveRichProject(project);

                // Build Claude Code launch command (resolves working directory, adds MT config flags)
                var launchCmd = Services.LaunchCommandBuilder.BuildClaudeCommand(project);
                string launchDir = launchCmd.WorkingDirectory;
                string autoRunCommand = launchCmd.AutoRunCommand;

                // Register terminal before starting (start screen tabs are unregistered)
                // If the project has a designated team lead, use their name and flag the terminal accordingly
                string terminalName = null;
                bool isTeamLead = !string.IsNullOrEmpty(project.TeamLead);
                if (_mcpServer?.Broker != null)
                {
                    terminalName = isTeamLead ? project.TeamLead : "Unassigned";

                    // Check if this identity is already active in another terminal
                    if (isTeamLead)
                    {
                        var activeTerminals = _mcpServer.Broker.GetTerminals();
                        bool nameInUse = activeTerminals.Any(t =>
                            t.Name.Equals(terminalName, StringComparison.OrdinalIgnoreCase) &&
                            t.IsConnected &&
                            !string.IsNullOrEmpty(t.DocId) &&
                            !t.DocId.Equals(doc.DocId, StringComparison.OrdinalIgnoreCase));

                        if (nameInUse)
                        {
                            var identities = GetAvailableIdentities(launchDir);
                            using var picker = new Dialogs.IdentityPickerDialog(terminalName, identities, _currentTheme);
                            if (picker.ShowDialog(this) == DialogResult.OK)
                            {
                                terminalName = picker.SelectedIdentity;
                                isTeamLead = false; // Alternative identity, not the designated team lead
                            }
                            else
                            {
                                return; // User cancelled
                            }
                        }
                    }

                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId, isTeamLead);
                }

                // Sync MCP configs: gateway-aware path if available, else standard path
                string gatewayProfile = null;
                try
                {
                    if (_mcpConfigService != null)
                    {
                        _mcpConfigService.EnsureMcpConfigsForProjectWithGateway(project.Id, launchDir, project.Name);
                        if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                            gatewayProfile = Services.GatewayIntegrationService.GetGatewayProfileName(project.Name);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] MCP config sync failed: {ex.Message}"); }

                System.Diagnostics.Trace.WriteLine($"[StartScreen] Launching project '{project.Name}' in {launchDir}");
                doc.StartTerminal(launchDir, terminalName, autoRunCommand, projectId: project.Id, isTeamLead: isTeamLead, gatewayProfile: gatewayProfile);

                // Apply current font size
                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                doc.SetFontSize(terminalFontSize);

                doc.Activate();
                doc.FocusTerminal();
                _lastActiveTerminal = doc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] OnStartScreenProjectLaunched error: {ex.Message}");
                doc.ShowStartScreen(); // Restore start screen so the tab isn't blank
                MessageBox.Show($"Failed to launch project: {ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Handles "Open PowerShell" from the start screen: starts a plain terminal with no project context.
        /// </summary>
        private void OnStartScreenOpenPowerShell(object sender, EventArgs e)
        {
            if (sender is not TerminalDocument doc) return;

            try
            {
                string terminalName = null;
                if (_mcpServer?.Broker != null)
                {
                    terminalName = "Unassigned";
                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId);
                }

                string dir = _settings?.GetLastDirectory();
                doc.StartTerminal(dir, terminalName);

                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                doc.SetFontSize(terminalFontSize);

                doc.Activate();
                doc.FocusTerminal();
                _lastActiveTerminal = doc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] OnStartScreenOpenPowerShell error: {ex.Message}");
                doc.ShowStartScreen(); // Restore start screen so the tab isn't blank
                MessageBox.Show($"Failed to open PowerShell: {ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Handles "Just Claude" from the start screen: launches Claude Code with MT config flags
        /// but no project context and no skills — just a plain Claude session for quick tasks.
        /// </summary>
        private void OnStartScreenJustClaude(object sender, EventArgs e)
        {
            if (sender is not TerminalDocument doc) return;

            try
            {
                // Use LaunchCommandBuilder to get MT config flags (--mcp-config, channel)
                var launch = Services.LaunchCommandBuilder.BuildClaudeCommand(null);

                // Default working directory from Settings, fallback to user profile
                string defaultFolder = _settings?.GetDefaultWorkingDirectory() ?? launch.WorkingDirectory;
                var recentFolders = _settings?.GetRecentDirectories() ?? new List<string>();

                // Show identity + folder picker
                var identities = GetAvailableIdentities(defaultFolder);
                using var picker = new Dialogs.IdentityPickerDialog(
                    null, identities, _currentTheme,
                    isSelectionMode: true,
                    defaultFolder: defaultFolder,
                    recentFolders: recentFolders);

                if (picker.ShowDialog(this) != DialogResult.OK)
                    return; // User cancelled

                string terminalName = picker.SelectedIdentity;
                string workingDir = picker.SelectedFolder ?? defaultFolder;
                bool isTeamLead = true;

                // Track the selected folder as a recent directory
                _settings?.AddRecentDirectory(workingDir);

                if (_mcpServer?.Broker != null)
                {
                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId, isTeamLead);
                }

                doc.StartTerminal(workingDir, terminalName, launch.AutoRunCommand, isTeamLead: isTeamLead);

                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                doc.SetFontSize(terminalFontSize);

                doc.Activate();
                doc.FocusTerminal();
                _lastActiveTerminal = doc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] OnStartScreenJustClaude error: {ex.Message}");
                doc.ShowStartScreen();
                MessageBox.Show($"Failed to launch Claude: {ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Handles "New Project" from the start screen.
        /// Opens the WPF NewProjectWpfDialog, saves a minimal project record,
        /// then launches a terminal in the project folder with /new-project auto-injected.
        /// </summary>
        private void OnStartScreenNewProject(object sender, EventArgs e)
        {
            if (sender is not TerminalDocument sourceDoc) return;

            var teamLeads = _sharedProjectDatabase?.GetTeamLeadProfiles()
                            ?? new List<(string, string, string)>();

            var wpfDialog = new Dialogs.NewProjectWpfDialog(_currentTheme.IsDark, teamLeads);
            var helper = new System.Windows.Interop.WindowInteropHelper(wpfDialog);
            helper.Owner = this.Handle;
            if (wpfDialog.ShowDialog() != true) return;

            try
            {
                string projectFolder = wpfDialog.ProjectFolder;
                System.IO.Directory.CreateDirectory(projectFolder); // no-op if already exists

                // Create minimal project record in SQLite
                var project = Models.Project.Create(wpfDialog.ProjectName, projectFolder);
                project.TeamLead = wpfDialog.SelectedTeamLead;
                project.CreatedBy = "new-project-dialog";
                _sharedProjectDatabase?.SaveRichProject(project);

                // Build Claude Code launch command
                var launchCmd = Services.LaunchCommandBuilder.BuildClaudeCommand(project);
                string launchDir = launchCmd.WorkingDirectory;
                string autoRunCommand = launchCmd.AutoRunCommand;

                // Register terminal before starting
                string terminalName = null;
                bool isTeamLead = !string.IsNullOrEmpty(project.TeamLead);
                if (_mcpServer?.Broker != null)
                {
                    terminalName = isTeamLead ? project.TeamLead : "Unassigned";
                    _mcpServer.Broker.RegisterTerminal(terminalName, sourceDoc.DocId, isTeamLead);
                }

                // Wire one-shot ClaudeCodeDetected handler to inject /new-project
                EventHandler newProjectHandler = null;
                newProjectHandler = async (s, ev) =>
                {
                    sourceDoc.ClaudeCodeDetected -= newProjectHandler;

                    // Wait for Claude Code to settle after standard "initializing..." injection
                    await Task.Delay(3000);
                    System.Diagnostics.Trace.WriteLine("[MainForm] New project flow: injecting /new-project");

                    bool injected = await sourceDoc.InjectInputAsync("/new-project");
                    if (!injected)
                    {
                        System.Diagnostics.Trace.WriteLine("[MainForm] /new-project JS injection failed, trying direct write");
                        try { sourceDoc.Terminal.Write("/new-project\r"); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainForm] /new-project fallback failed: {ex.Message}"); }
                    }
                };
                sourceDoc.ClaudeCodeDetected += newProjectHandler;

                // Sync MCP configs: gateway-aware path if available, else standard path
                string gatewayProfile2 = null;
                try
                {
                    if (_mcpConfigService != null)
                    {
                        _mcpConfigService.EnsureMcpConfigsForProjectWithGateway(project.Id, launchDir, project.Name);
                        if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                            gatewayProfile2 = Services.GatewayIntegrationService.GetGatewayProfileName(project.Name);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] MCP config sync failed: {ex.Message}"); }

                // Start terminal in project folder
                System.Diagnostics.Trace.WriteLine($"[StartScreen] Launching new project '{project.Name}' in {launchDir}");
                sourceDoc.StartTerminal(launchDir, terminalName, autoRunCommand,
                    projectId: project.Id, isTeamLead: isTeamLead, gatewayProfile: gatewayProfile2);

                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                sourceDoc.SetFontSize(terminalFontSize);
                sourceDoc.Activate();
                sourceDoc.FocusTerminal();
                _lastActiveTerminal = sourceDoc;

                // Refresh other start screens so the new project card appears
                foreach (var termDoc in _dockPanel.Documents.OfType<TerminalDocument>())
                {
                    if (termDoc != sourceDoc && termDoc.IsStartScreenVisible)
                        termDoc.ShowStartScreen();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] OnStartScreenNewProject error: {ex.Message}");
                sourceDoc.ShowStartScreen();
                MessageBox.Show($"Failed to create project: {ex.Message}",
                    "New Project Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Creates a new terminal with a specific identity name and resumes the associated Claude session.
        /// Used by the "Launch as..." context menu and Launcher panel.
        /// </summary>
        /// <param name="identityName">The identity name (e.g., "Alice", "Bob")</param>
        /// <param name="workingDirectory">Optional working directory (defaults to last directory)</param>
        public void AddNewTerminalWithIdentity(string identityName, string workingDirectory = null)
        {
            // Create terminal with specific identity name
            // Note: We don't use "claude -r <session>" to resume - instead we start fresh
            // and let the session history MCP provide context via semantic search.
            // This avoids issues with stale sessions and context limits.
            AddNewTerminal(workingDirectory, null, false, identityName, null);
        }

        /// <summary>
        /// Gets available identities for the current project.
        /// Used by context menus and launcher panel.
        /// </summary>
        public List<string> GetAvailableIdentities(string projectPath = null)
        {
            var identities = new List<string>();
            projectPath = projectPath ?? _settings?.GetLastDirectory() ?? Directory.GetCurrentDirectory();

            try
            {
                var discovery = new MCPServer.Services.SessionDiscovery();
                var discovered = discovery.DiscoverIdentitiesInProject(projectPath);
                // Dictionary keys are identity names
                identities.AddRange(discovered.Keys);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Identity discovery failed: {ex.Message}");
            }

            // Also include the name pool for new identities
            var existing = identities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _terminalNamePool.Take(8)) // First 8 common names
            {
                if (!existing.Contains(name))
                    identities.Add(name);
            }

            return identities;
        }

        /// <summary>
        /// Pre-registers a terminal with the MCP server before the shell starts.
        /// Returns a unique name for the terminal.
        /// </summary>
        private string PreRegisterTerminal(string docId)
        {
            try
            {
                // Get list of existing terminal names
                var existingTerminals = _mcpServer.Broker.GetTerminals();
                var takenNames = existingTerminals.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Pick first available name from pool
                string terminalName = null;
                foreach (var name in _terminalNamePool)
                {
                    if (!takenNames.Contains(name))
                    {
                        terminalName = name;
                        break;
                    }
                }

                // Fallback: use Agent-{docId prefix} if all names taken
                if (terminalName == null)
                {
                    terminalName = $"Agent-{docId.Substring(0, 4)}";
                }

                // Register with MCP server
                var result = _mcpServer.Broker.RegisterTerminal(terminalName, docId);
                if (result.Success)
                {
                    return terminalName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pre-registration failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Pre-registers a terminal with a specific identity name.
        /// Used by AddNewTerminalWithIdentity for "Launch as..." feature.
        /// </summary>
        private string PreRegisterTerminalWithName(string docId, string identityName, bool isTeamLead = false)
        {
            try
            {
                // Register with the specific identity name
                var result = _mcpServer.Broker.RegisterTerminal(identityName, docId, isTeamLead);
                if (result.Success)
                {
                    return identityName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pre-registration with name failed: {ex.Message}");
            }

            // Fallback to regular pre-registration if specific name fails
            return PreRegisterTerminal(docId);
        }

        /// <summary>
        /// Creates a terminal and returns a Task that completes when it's fully initialized.
        /// Used during session restore to ensure each terminal is ready before creating the next.
        /// </summary>
        private Task<TerminalDocument> AddNewTerminalAsync(string workingDirectory, float? fontSize)
        {
            var tcs = new TaskCompletionSource<TerminalDocument>();

            var doc = new TerminalDocument();
            doc.Terminal.SetDebugLogService(_debugLogService);
            doc.SetDebugLogService(_debugLogService); // Enable status bar logging
            doc.SetTheme(_currentTheme);
            doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
            doc.TerminalExited += OnTerminalExited;
            doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
            doc.AgentSplitRatioChanged += OnAgentSplitRatioChanged;
            doc.HudSplitRatioChanged += OnHudSplitRatioChanged;
            doc.StatusBarHeightChanged += OnStatusBarHeightChanged;
            doc.TaskHudZoomChanged += OnTaskHudZoomChanged;
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            string terminalName = null;

            // Subscribe to ready event to signal completion
            void readyHandler(object s, EventArgs e)
            {
                doc.TerminalReady -= readyHandler;
                tcs.TrySetResult(doc);
            }
            doc.TerminalReady += readyHandler;

            // Show as tab (for restore, will apply grid layout after all terminals created)
            doc.Show(_dockPanel, DockState.Document);

            // Register with MCP server AFTER adding to DockPanel so the
            // TerminalRegistered event handler can find this doc by DocId
            if (_mcpServer?.Broker != null)
            {
                terminalName = "Unassigned";
                _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId);
            }

            // Start the terminal
            string dir = workingDirectory ?? _settings?.GetLastDirectory();
            doc.StartTerminal(dir, terminalName);

            // Apply font size and saved split ratios
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);
            if (_settings != null)
            {
                doc.ApplyAgentSplitRatio(_settings.GetAgentPanelSplitRatio());
                doc.ApplyHudSplitRatio(_settings.GetHudSplitRatio());
                doc.ApplyTaskHudZoom(_settings.GetTaskHudZoom());
                doc.ApplyStatusBarHeight(_settings.GetStatusBarHeight());
            }

            _lastActiveTerminal = doc;

            return tcs.Task;
        }

        /// <summary>
        /// Creates a terminal document and waits for WebView2/xterm.js to initialize.
        /// Does NOT start the shell - call StartTerminal() separately after grid layout.
        /// </summary>
        private Task<TerminalDocument> CreateTerminalDocumentAsync(float? fontSize)
        {
            var tcs = new TaskCompletionSource<TerminalDocument>();

            var doc = new TerminalDocument();
            doc.Terminal.SetDebugLogService(_debugLogService);
            doc.SetDebugLogService(_debugLogService); // Enable status bar logging
            doc.SetTheme(_currentTheme);
            doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
            doc.TerminalExited += OnTerminalExited;
            doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
            doc.AgentSplitRatioChanged += OnAgentSplitRatioChanged;
            doc.HudSplitRatioChanged += OnHudSplitRatioChanged;
            doc.StatusBarHeightChanged += OnStatusBarHeightChanged;
            doc.TaskHudZoomChanged += OnTaskHudZoomChanged;
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            // Wait for renderer (WebView2/xterm.js) to be ready
            void rendererReadyHandler(object s, EventArgs e)
            {
                doc.RendererReady -= rendererReadyHandler;
                tcs.TrySetResult(doc);
            }
            doc.RendererReady += rendererReadyHandler;

            // Show as tab
            doc.Show(_dockPanel, DockState.Document);

            // Apply font size and saved split ratios
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);
            if (_settings != null)
            {
                doc.ApplyAgentSplitRatio(_settings.GetAgentPanelSplitRatio());
                doc.ApplyHudSplitRatio(_settings.GetHudSplitRatio());
                doc.ApplyTaskHudZoom(_settings.GetTaskHudZoom());
                doc.ApplyStatusBarHeight(_settings.GetStatusBarHeight());
            }

            _lastActiveTerminal = doc;

            return tcs.Task;
        }

        /// <summary>
        /// Waits for the WebView2 renderer to be ready.
        /// </summary>
        private Task<bool> WaitForRendererReadyAsync(TerminalDocument doc, int timeoutMs = 5000)
        {
            // Check if already ready (event may have already fired)
            if (doc.IsRendererReady)
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>();
            System.Threading.CancellationTokenSource cts = null;

            void handler(object s, EventArgs e)
            {
                doc.RendererReady -= handler;
                cts?.Cancel();
                tcs.TrySetResult(true);
            }

            doc.RendererReady += handler;

            // Timeout fallback
            cts = new System.Threading.CancellationTokenSource();
            Task.Delay(timeoutMs, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    doc.RendererReady -= handler;
                    tcs.TrySetResult(false);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Starts a terminal and waits for PowerShell to be confirmed running.
        /// The DirectoryChanged event fires when PowerShell sets its title, confirming it's operational.
        /// </summary>
        private Task StartTerminalAndWaitAsync(TerminalDocument doc, string workingDirectory, string terminalName = null, int timeoutMs = 3000)
        {
            var tcs = new TaskCompletionSource<bool>();
            System.Threading.CancellationTokenSource cts = null;

            void directoryChangedHandler(object s, DirectoryChangedEventArgs e)
            {
                doc.DirectoryChanged -= directoryChangedHandler;
                cts?.Cancel();
                tcs.TrySetResult(true);
            }

            doc.DirectoryChanged += directoryChangedHandler;

            // Start the terminal with optional pre-registered name
            doc.StartTerminal(workingDirectory, terminalName);

            // Timeout fallback - don't wait forever
            cts = new System.Threading.CancellationTokenSource();
            Task.Delay(timeoutMs, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    doc.DirectoryChanged -= directoryChangedHandler;
                    tcs.TrySetResult(false); // Timed out, but continue anyway
                }
            });

            return tcs.Task;
        }

        private void OnTerminalFontSizeChanged(object sender, FontSizeChangedEventArgs e)
        {
            // Save all terminal states immediately so per-terminal font sizes
            // persist even on crash (not just on clean app close)
            SaveSession();
        }

        private void OnAgentSplitRatioChanged(object sender, double ratio)
        {
            _settings?.SetAgentPanelSplitRatio(ratio);
            // Apply to all other terminals
            var source = sender as TerminalDocument;
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                if (doc != source) doc.ApplyAgentSplitRatio(ratio);
            }
        }

        private void OnHudSplitRatioChanged(object sender, double ratio)
        {
            _settings?.SetHudSplitRatio(ratio);
            // Apply to all other terminals
            var source = sender as TerminalDocument;
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                if (doc != source) doc.ApplyHudSplitRatio(ratio);
            }
        }

        private void OnStatusBarHeightChanged(object sender, int height)
        {
            _settings?.SetStatusBarHeight(height);
            // Apply to all other terminals
            var source = sender as TerminalDocument;
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                if (doc != source) doc.ApplyStatusBarHeight(height);
            }
        }

        private bool _suppressHudZoomSync;
        private bool _suppressAgentPanelZoomSync;

        private void OnTaskHudZoomChanged(object sender, double zoom)
        {
            if (_suppressHudZoomSync) return;
            _suppressHudZoomSync = true;
            _settings?.SetTaskHudZoom(zoom);
            // Apply to all other terminals
            var source = sender as TerminalDocument;
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                if (doc != source) doc.ApplyTaskHudZoom(zoom);
            }
            _suppressHudZoomSync = false;
        }

        private void OnEmbeddedAgentPanelZoomChanged(double zoom)
        {
            if (_suppressAgentPanelZoomSync) return;
            _suppressAgentPanelZoomSync = true;
            _settings?.SetAgentPanelZoom(zoom);
            // Apply to all other embedded agent panels
            foreach (var kvp in _embeddedAgentMap)
            {
                kvp.Value.Control.SetZoomFactor(zoom);
            }
            _suppressAgentPanelZoomSync = false;
        }

        private void OnTerminalClicked(object sender, EventArgs e)
        {
            // Close any open dropdown menus when terminal is clicked
            _gridDropdown?.HideDropDown();
            // Settings button doesn't need dropdown hiding
            _recentFoldersDropdown?.HideDropDown();

            // Track which terminal was clicked for features like Recent Folders
            // (DockPanelSuite's ActiveDocument is unreliable with WebView2)
            if (sender is TerminalControl terminal)
            {
                foreach (var doc in _gridManager.GetTerminalDocuments())
                {
                    if (doc.Terminal == terminal)
                    {
                        _lastActiveTerminal = doc;

                        // Update focus borders — ActiveDocumentChanged doesn't fire reliably
                        // with WebView2, so we must update borders directly on click
                        SuspendLayout();
                        foreach (var termDoc in _gridManager.GetTerminalDocuments())
                        {
                            termDoc.SetFocusBorder(termDoc == doc);
                        }
                        ResumeLayout(true);

                        // Update dashboard header active session chip
                        _dashboardHeader?.SetActiveSession(doc.TabText);

                        break;
                    }
                }
            }
        }

        private void OnTerminalDirectoryChanged(object sender, DirectoryChangedEventArgs e)
        {
            System.Diagnostics.Trace.WriteLine($"[OnTerminalDirectoryChanged] Directory changed to: {e.Directory}");
            if (!string.IsNullOrEmpty(e.Directory))
            {
                _settings?.SetLastDirectory(e.Directory);

                // Auto-detect project in the new directory
                var project = _projectService?.DiscoverProject(e.Directory);
                if (project != null)
                {
                    // Check if registered
                    if (!_projectService.IsProjectRegistered(e.Directory))
                    {
                        // Offer to register the project
                        OfferProjectRegistration(e.Directory, project);
                    }
                    else
                    {
                        // Update current project and refresh panel
                        System.Diagnostics.Trace.WriteLine("[OnTerminalDirectoryChanged] Refreshing project panel...");
                        _currentProject = project;
                        _projectPanel?.RefreshForProject(project);
                        RefreshDashboardProjectInfo();
                    }
                }
                else
                {
                    // Not a project directory - clear project and show path-based prompts
                    _currentProject = null;
                    RefreshProjectPanel();
                    RefreshDashboardProjectInfo();
                }

                // Index sessions for this project directory in the background
                Task.Run(async () => await _sessionIndexingService?.IndexProjectSessionsAsync(e.Directory));
            }
        }

        private void OfferProjectRegistration(string directory, Models.Project discoveredProject)
        {
            // For auto-discovered projects (with existing .claude/project.json),
            // automatically add them to the registry
            if (!_projectService.IsProjectRegistered(directory))
            {
                // Auto-register the discovered project
                _projectService.SaveProject(discoveredProject);
                _currentProject = discoveredProject;
                _projectPanel?.RefreshForProject(discoveredProject);
            }
        }

        private void OnProjectFileChanged(object sender, EventArgs e)
        {
            // project.json was created or modified - refresh the project panel
            System.Diagnostics.Trace.WriteLine($"[MainForm] OnProjectFileChanged received from {sender}");
            RefreshProjectPanel();
        }

        private void OnRegistryChangedExternally(object sender, EventArgs e)
        {
            // Registry file changed (e.g., by Claude Code adding a new project)
            // Refresh on UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnRegistryChangedExternally(sender, e)));
                return;
            }

            System.Diagnostics.Trace.WriteLine("[MainForm] Registry changed externally - refreshing project panel and tasks panel");
            RefreshProjectPanel();
            _tasksPanel?.RefreshProjects();
        }

        private async void OnClaudeCodeDetected(object sender, EventArgs e)
        {
            // Claude Code was detected running in a terminal
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnClaudeCodeDetected(sender, e)));
                return;
            }

            System.Diagnostics.Trace.WriteLine("[MainForm] Claude Code detected in terminal");

            // Get the terminal document
            var doc = sender as TerminalDocument;
            string workingDir = doc?.GetWorkingDirectory();

            // Check if this directory is already registered
            if (!string.IsNullOrEmpty(workingDir))
            {
                var project = _projectService?.DiscoverProject(workingDir);
                if (project != null)
                {
                    // Already registered - just refresh
                    _currentProject = project;
                    _projectPanel?.RefreshForProject(project);
                }
                else
                {
                    // Not registered - show Claude-specific prompt
                    _currentProject = null;
                    _projectPanel?.ShowClaudeDetectedState(workingDir);
                }

                // Index sessions for this project when Claude starts (fire-and-forget)
                _ = Task.Run(async () => await _sessionIndexingService?.IndexProjectSessionsAsync(workingDir));
            }

            // Auto-initialize Claude Code by injecting "initializing..." to trigger startup hooks
            // This removes the need for manual input after launching via "Launch as..."
            if (doc != null)
            {
                // Wait for Claude Code to finish showing its banner and prompt before injecting.
                // Detection fires when "Claude Code" appears in output, but the input prompt
                // may not be ready yet. Wait 1.5s for the prompt to appear.
                await Task.Delay(1500);
                System.Diagnostics.Trace.WriteLine("[MainForm] Post-detection delay complete, injecting 'initializing...'");

                // Try the standard JS-based injection first
                bool injected = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    injected = await doc.InjectInputAsync("initializing...");
                    if (injected)
                    {
                        System.Diagnostics.Trace.WriteLine($"[MainForm] Auto-injected 'initializing...' to trigger startup hooks (attempt {attempt})");
                        break;
                    }
                    else
                    {
                        System.Diagnostics.Trace.WriteLine($"[MainForm] Injection attempt {attempt} failed, retrying...");
                        await Task.Delay(500);
                    }
                }

                // Fallback: write directly to ConPTY if JS-based injection failed
                if (!injected)
                {
                    System.Diagnostics.Trace.WriteLine("[MainForm] JS injection failed, falling back to direct ConPTY write");
                    try
                    {
                        doc.Terminal.Write("initializing...\r");
                        System.Diagnostics.Trace.WriteLine("[MainForm] Direct ConPTY write of 'initializing...' + Enter completed");
                        _debugLogService.Info("MainForm", "Auto-submitted initializing prompt via direct ConPTY write (fallback)");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[MainForm] Direct ConPTY write failed: {ex.Message}");
                        _debugLogService.Warning("MainForm", $"All auto-inject methods failed: {ex.Message}");
                    }
                }
            }
        }

        private async void OnTaskDroppedOnTerminal(object sender, TaskDroppedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTaskDroppedOnTerminal(sender, e)));
                return;
            }

            var doc = sender as TerminalDocument;
            if (doc == null) return;

            string agentName = doc.CustomTitle ?? doc.TabText ?? "Terminal";
            _debugLogService?.Info("MainForm", $"Task '{e.Title}' (ID: {e.TaskId}) dropped on terminal '{agentName}'");

            // Auto-claim the task for this terminal's agent
            var broker = _mcpServer?.Broker;
            if (broker != null)
            {
                var result = broker.ClaimTask(e.TaskId, agentName);
                if (result.Success)
                {
                    _debugLogService?.Info("MainForm", $"Task {e.TaskId} auto-claimed by {agentName}");
                }
                else
                {
                    _debugLogService?.Warning("MainForm", $"Failed to claim task {e.TaskId}: {result.Error}");
                }
            }

            // Type a prompt into the terminal so the agent starts working on the task
            string prompt = $"Pick up kanban task \"{e.Title}\" (ID: {e.TaskId}). Claim it and start working on it using /kanban-task.";

            // Try JS-based injection first, fall back to TypeInput
            bool injected = await doc.InjectInputAsync(prompt);
            if (!injected)
            {
                doc.TypeInput(prompt);
            }
        }

        private void OnRecentFoldersDropDownOpening(object sender, EventArgs e)
        {
            _recentFoldersDropdown.DropDownItems.Clear();
            Color textColor = _currentTheme.IsDark ? Color.White : Color.FromArgb(30, 30, 30);

            var recentDirs = _settings?.GetRecentDirectories();

            if (recentDirs == null || recentDirs.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem("(No recent folders)")
                {
                    Enabled = false,
                    ForeColor = textColor
                };
                _recentFoldersDropdown.DropDownItems.Add(emptyItem);
            }
            else
            {
                foreach (string dir in recentDirs)
                {
                    var item = new ToolStripMenuItem(dir)
                    {
                        Tag = dir,
                        ForeColor = textColor
                    };
                    item.Click += OnRecentFolderItemClick;
                    _recentFoldersDropdown.DropDownItems.Add(item);
                }
            }
        }

        private void OnRecentFolderItemClick(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem && menuItem.Tag is string directory)
            {
                // Use _lastActiveTerminal instead of _dockPanel.ActiveDocument
                // because DockPanelSuite's ActiveDocument is unreliable with WebView2
                var doc = _lastActiveTerminal ?? _dockPanel.ActiveDocument as TerminalDocument;
                if (doc != null)
                {
                    // Send cd command with Enter key (\r)
                    string cdCommand = $"cd \"{directory}\"\r";
                    doc.Terminal.Write(cdCommand);
                    doc.FocusTerminal();
                }
            }
        }

        private void CloseCurrentTerminal()
        {
            if (_dockPanel.ActiveDocument is TerminalDocument doc)
            {
                doc.Close();
            }
        }

        private void ApplyGridLayout(GridLayoutManager.GridPreset preset)
        {
            var docs = _gridManager.GetTerminalDocuments();
            int neededCount = GetTerminalCountForPreset(preset);

            // Add terminals if needed
            while (docs.Count < neededCount)
            {
                var doc = new TerminalDocument();
                doc.Terminal.SetDebugLogService(_debugLogService);
                doc.SetDebugLogService(_debugLogService); // Enable status bar logging
                doc.SetTheme(_currentTheme);
                doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
                doc.TerminalExited += OnTerminalExited;
                doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
                doc.Terminal.TerminalClicked += OnTerminalClicked;
                doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
                doc.DirectoryChanged += OnTerminalDirectoryChanged;
                doc.ProjectFileChanged += OnProjectFileChanged;
                doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

                // Wire up "Launch as..." context menu support
                doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
                doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
                WireStartScreenEvents(doc);

                doc.Show(_dockPanel, DockState.Document);

                // Pre-register with MessageBroker so the terminal is routable immediately
                var terminalName = PreRegisterTerminal(doc.DocId);
                doc.StartTerminal(_settings?.GetLastDirectory(), terminalName);
                doc.SetFontSize(_settings?.GetTerminalFontSize() ?? 10f);
                docs.Add(doc);
            }

            // Apply the layout
            _gridManager.ApplyPreset(preset);
        }

        private int GetTerminalCountForPreset(GridLayoutManager.GridPreset preset)
        {
            switch (preset)
            {
                case GridLayoutManager.GridPreset.Horizontal2:
                case GridLayoutManager.GridPreset.Vertical2:
                    return 2;
                case GridLayoutManager.GridPreset.Horizontal3:
                case GridLayoutManager.GridPreset.Vertical3:
                    return 3;
                case GridLayoutManager.GridPreset.Grid2x2:
                    return 4;
                case GridLayoutManager.GridPreset.Grid2x3:
                case GridLayoutManager.GridPreset.Grid3x2:
                    return 6;
                default:
                    return 4;
            }
        }

        private void OnActiveDocumentChanged(object sender, EventArgs e)
        {
            try
            {
                var activeDoc = _dockPanel.ActiveDocument as TerminalDocument;

                // Update focus borders on all terminals
                SuspendLayout();
                foreach (var termDoc in _gridManager.GetTerminalDocuments())
                {
                    termDoc.SetFocusBorder(termDoc == activeDoc);
                }
                ResumeLayout(true);

                if (activeDoc != null)
                {
                    // Focus the terminal when switching
                    activeDoc.FocusTerminal();

                    // Update dashboard header active session chip
                    _dashboardHeader?.SetActiveSession(activeDoc.TabText);
                }

                // Force all panes to refresh their tab strips to show correct active state
                var refreshedPanes = new HashSet<object>();
                foreach (var termDoc in _gridManager.GetTerminalDocuments())
                {
                    if (termDoc.Pane?.TabStripControl != null && refreshedPanes.Add(termDoc.Pane))
                    {
                        termDoc.Pane.TabStripControl.Refresh();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] OnActiveDocumentChanged error: {ex.Message}");
            }
        }

        private void OnTerminalExited(object sender, EventArgs e)
        {
            // Terminal exited - could auto-close or prompt
            // For now, just leave it open showing "[Exited]"

            // Remove from MCP terminal map and unregister terminal
            if (sender is TerminalDocument doc)
            {
                // Unregister terminal from broker (sets profile offline)
                if (_mcpServer?.Broker != null && !string.IsNullOrEmpty(doc.DocId))
                {
                    _mcpServer.Broker.UnregisterTerminal(doc.DocId);
                }

                lock (_terminalDocMapLock)
                {
                    var keysToRemove = _terminalDocMap.Where(kvp => kvp.Value == doc).Select(kvp => kvp.Key).ToList();
                    foreach (var key in keysToRemove)
                        _terminalDocMap.Remove(key);
                }
            }
        }

        /// <summary>
        /// Handles the "Launch as..." context menu request from a terminal.
        /// Creates a new terminal with the specified identity and resumes the associated Claude session.
        /// </summary>
        private void OnLaunchAsIdentityRequested(object sender, LaunchAsIdentityEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.IdentityName))
                return;

            // Get the terminal document that was right-clicked
            if (!(sender is TerminalDocument doc))
                return;

            string workingDirectory = doc.GetWorkingDirectory();
            string identityName = e.IdentityName;

            // Unregister the old terminal from MCP
            _mcpServer?.Broker?.UnregisterTerminal(doc.DocId);

            // Register with the new identity name
            string terminalName = PreRegisterTerminalWithName(doc.DocId, identityName);

            // Just launch claude - let the user choose to resume or start fresh
            // Using plain "claude" lets Claude prompt about resuming recent sessions
            // TODO: Make --dangerously-skip-permissions configurable in settings
            string autoRunCommand = "claude --dangerously-skip-permissions --dangerously-load-development-channels server:multiterminal-channel";

            // Stop current terminal and restart with new identity
            doc.Terminal.Stop();
            doc.CustomTitle = terminalName;
            doc.StartTerminal(workingDirectory, terminalName, autoRunCommand);

            // Auto-initialization is handled by OnClaudeCodeDetected when Claude Code's output is detected.
            // This ensures injection happens AFTER Claude Code is ready, not just after WebView2 loads.
            // The _claudeCodeDetectedThisSession flag is reset in TerminalControl.DoStart() so the event fires for restarted terminals.
            System.Diagnostics.Trace.WriteLine($"[MainForm.OnLaunchAsIdentityRequested] Terminal restarted for {terminalName}, waiting for Claude Code detection to auto-inject");
        }

        private bool _sessionSaveCompleted;

        private async void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // Save session context for all active terminals before closing
            if (!_sessionSaveCompleted)
            {
                _sessionSaveCompleted = true; // Set before await to prevent re-entrancy on double-click
                e.Cancel = true;
                Text += " \u2014 Saving session state...";

                try
                {
                    // Mechanical save — fast, guaranteed (<1 sec per terminal)
                    var terminals = _dockPanel.Documents.OfType<TerminalDocument>()
                        .Where(d => d.Terminal?.ConPty != null && d.Terminal.IsRunning)
                        .ToList();

                    // Save context for each registered terminal
                    foreach (var doc in terminals)
                    {
                        string name = doc.CustomTitle;
                        if (!string.IsNullOrEmpty(name))
                        {
                            await Services.SessionContextWriter.WriteContextAsync(name);
                            break; // Write once with the active terminal's perspective
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Session save failed: {ex.Message}");
                }

                Close(); // Re-trigger close, this time _sessionSaveCompleted is true so it proceeds
                return;
            }

            // Freeze all terminal splitter layouts FIRST to prevent bogus ratio saves
            // during the shutdown resize/layout cascade
            foreach (var doc in _dockPanel.Documents.OfType<TerminalDocument>())
                doc.FreezeLayout();

            // Batch all settings changes to avoid multiple disk writes
            _settings.BeginBatch();

            // Save window bounds (use RestoreBounds if maximized to get normal size)
            if (WindowState == FormWindowState.Normal)
                _settings.SetWindowBounds(new Rectangle(Left, Top, Width, Height));
            else
                _settings.SetWindowBounds(RestoreBounds);

            _settings.SetWindowState(WindowState);

            // Save dock panel layout (used by LoadFromXml on restore)
            try
            {
                _dockPanel.SaveAsXml(_settings.GetLayoutFilePath());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to save layout: {ex.Message}");
            }

            // Save all panel visibility and dock positions
            SavePanelState("ProjectPanel", _projectPanel);
            SavePanelState("ChatPanel", _chatPanel);
            SavePanelState("TasksPanel", _tasksPanel);
            SavePanelState("ActivityPanel", _activityPanel);
            SavePanelState("ProfilePanel", _profilePanel);
            SavePanelState("InboxPanel", _inboxPanel);
            SavePanelState("OfficePanel", _officePanel);
            SavePanelState("DebugPanel", _debugPanel);
            SavePanelState("FilePreviewPanel", _filePreviewPanel);

            // Save session before closing
            SaveSession();

            // Write all batched settings to disk
            _settings.EndBatch();

            // Set all profiles offline before stopping
            if (_mcpServer?.Broker?.TaskDb != null)
            {
                try
                {
                    _mcpServer.Broker.TaskDb.SetAllProfilesOffline();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to set profiles offline: {ex.Message}");
                }
            }

            // Stop HTTP webhook service
            if (_webhookService != null)
            {
                try
                {
                    _webhookService.Stop();
                    _webhookService.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to stop webhook service: {ex.Message}");
                }
            }

            // Stop Oracle advisory agent — close terminal first, then dispose service
            if (_oracleService != null)
            {
                try
                {
                    CloseOracleTerminal();
                    _oracleService.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to dispose Oracle: {ex.Message}");
                }
            }

            // Stop all agent processes before closing terminals
            lock (_agentProcessMap)
            {
                foreach (var kvp in _agentProcessMap)
                {
                    try
                    {
                        kvp.Value.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to dispose agent process {kvp.Key}: {ex.Message}");
                    }
                }
                _agentProcessMap.Clear();
            }

            // Stop companion processes that have StopOnExit=true
            try
            {
                _companionManager?.StopAll();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to stop companion processes: {ex.Message}");
            }

            // Stop MCP server synchronously with timeout to ensure Kestrel threads are cleaned up
            if (_mcpServer != null)
            {
                try
                {
                    _mcpServer.StopAsync().Wait(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to stop MCP server: {ex.Message}");
                }
            }

            // Close all terminals
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                doc.Terminal?.Stop();
            }

            // Explicitly dispose all WebView2 panels to prevent zombie processes.
            // WebView2 can hang during implicit dispose if it has pending navigation or JS,
            // which blocks the entire process from exiting and requires a reboot.
            DisposePanel(_projectPanel);
            DisposePanel(_chatPanel);
            DisposePanel(_tasksPanel);
            DisposePanel(_activityPanel);
            DisposePanel(_profilePanel);
            DisposePanel(_inboxPanel);
            DisposePanel(_officePanel);
            DisposePanel(_debugPanel);
            DisposePanel(_filePreviewPanel);

            // Dispose embedded agent panels
            lock (_embeddedAgentMap)
            {
                foreach (var kvp in _embeddedAgentMap)
                {
                    try { kvp.Value.Control?.Dispose(); } catch { }
                }
            }

            // Dispose all terminal documents (each has a WebView2 renderer)
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                try { doc.Dispose(); } catch { }
            }

            // Failsafe: force-exit after a short delay if something is still hanging.
            // This prevents the zombie process scenario that requires a reboot.
            Task.Run(async () =>
            {
                await Task.Delay(5000);
                Environment.Exit(0);
            });
        }

        private void DisposePanel(DockContent panel)
        {
            if (panel == null || panel.IsDisposed) return;
            try { panel.Dispose(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to dispose panel: {ex.Message}");
            }
        }

        private void SaveSession()
        {
            var terminals = _gridManager.GetTerminalDocuments();
            if (terminals.Count == 0)
                return;

            // Save terminal states
            var sessionData = terminals.Select(t => new TerminalSessionInfo
            {
                WorkingDirectory = t.GetWorkingDirectory() ?? _settings?.GetLastDirectory(),
                FontSize = t.GetFontSize(),
                CustomTitle = t.CustomTitle
            }).ToList();

            _settings.SetSessionTerminals(sessionData);

            // Save layout preset based on terminal count
            var preset = GridLayoutManager.GetRecommendedPreset(terminals.Count);
            _settings.SetSessionLayout(preset.ToString());
        }

        private void SavePanelState(string panelKey, DockContent panel)
        {
            if (panel == null || panel.IsDisposed)
            {
                _settings.Set($"Panel_{panelKey}_Visible", "false");
                return;
            }
            _settings.Set($"Panel_{panelKey}_Visible", panel.Visible ? "true" : "false");
            // Use VisibleState instead of DockState — it preserves the last docked position
            // even when the panel is hidden (DockState returns Hidden/Unknown when not visible)
            _settings.Set($"Panel_{panelKey}_DockState", panel.VisibleState.ToString());
        }

        private DockState GetSavedDockState(string panelKey, DockState defaultDock)
        {
            var savedState = _settings.Get($"Panel_{panelKey}_DockState");
            if (!string.IsNullOrEmpty(savedState) && Enum.TryParse<DockState>(savedState, out var parsed))
            {
                if (parsed == DockState.DockLeft || parsed == DockState.DockRight ||
                    parsed == DockState.DockTop || parsed == DockState.DockBottom ||
                    parsed == DockState.Document)
                {
                    return parsed;
                }
            }
            return defaultDock;
        }

        private void RestorePanelStates()
        {
            bool isDark = _currentTheme.IsDark;

            if (RestoreSinglePanel("ProjectPanel", _projectPanel, DockState.DockLeft))
            {
                _projectPanel.SetTheme(_currentTheme);
                RefreshProjectPanel();
            }
            if (RestoreSinglePanel("ChatPanel", _chatPanel, DockState.DockRight))
                _chatPanel.ApplyTheme(isDark);
            if (RestoreSinglePanel("TasksPanel", _tasksPanel, DockState.DockBottom))
                _tasksPanel.ApplyTheme(isDark);
            if (RestoreSinglePanel("ActivityPanel", _activityPanel, DockState.DockRight))
                _activityPanel.ApplyTheme(isDark);
            if (RestoreSinglePanel("ProfilePanel", _profilePanel, DockState.DockRight))
                _profilePanel.SetTheme(isDark);
            if (RestoreSinglePanel("InboxPanel", _inboxPanel, DockState.DockRight))
                _inboxPanel.ApplyTheme(isDark);
            if (RestoreSinglePanel("OfficePanel", _officePanel, DockState.DockRight))
                _officePanel.ApplyTheme(isDark);
            RestoreSinglePanel("DebugPanel", _debugPanel, DockState.DockBottom);
            if (RestoreSinglePanel("FilePreviewPanel", _filePreviewPanel, DockState.DockBottom))
                _filePreviewPanel.ApplyTheme(isDark);
        }

        private void ApplyThemesToPanels()
        {
            bool isDark = _currentTheme.IsDark;
            if (_projectPanel != null && !_projectPanel.IsDisposed && _projectPanel.Visible)
            {
                _projectPanel.SetTheme(_currentTheme);
                RefreshProjectPanel();
            }
            if (_chatPanel != null && !_chatPanel.IsDisposed && _chatPanel.Visible)
                _chatPanel.ApplyTheme(isDark);
            if (_tasksPanel != null && !_tasksPanel.IsDisposed && _tasksPanel.Visible)
                _tasksPanel.ApplyTheme(isDark);
            if (_activityPanel != null && !_activityPanel.IsDisposed && _activityPanel.Visible)
                _activityPanel.ApplyTheme(isDark);
            if (_profilePanel != null && !_profilePanel.IsDisposed && _profilePanel.Visible)
                _profilePanel.SetTheme(isDark);
            if (_inboxPanel != null && !_inboxPanel.IsDisposed && _inboxPanel.Visible)
                _inboxPanel.ApplyTheme(isDark);
            if (_officePanel != null && !_officePanel.IsDisposed && _officePanel.Visible)
                _officePanel.ApplyTheme(isDark);
            if (_filePreviewPanel != null && !_filePreviewPanel.IsDisposed && _filePreviewPanel.Visible)
                _filePreviewPanel.ApplyTheme(isDark);
        }

        private bool RestoreSinglePanel(string panelKey, DockContent panel, DockState defaultDock)
        {
            if (panel == null || panel.IsDisposed) return false;
            if (_settings.Get($"Panel_{panelKey}_Visible") != "true") return false;

            // Parse saved dock state, fall back to default
            var dockState = defaultDock;
            var savedState = _settings.Get($"Panel_{panelKey}_DockState");
            if (!string.IsNullOrEmpty(savedState) && Enum.TryParse<DockState>(savedState, out var parsed))
            {
                // Only use valid dockable states (not Hidden, Unknown, or Float)
                if (parsed == DockState.DockLeft || parsed == DockState.DockRight ||
                    parsed == DockState.DockTop || parsed == DockState.DockBottom ||
                    parsed == DockState.Document)
                {
                    dockState = parsed;
                }
            }

            try
            {
                panel.Show(_dockPanel, dockState);
                System.Diagnostics.Trace.WriteLine($"[RestoreSession] Restored {panelKey} at {dockState}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestoreSession] Failed to restore {panelKey}: {ex.Message}");
                return false;
            }
        }

        private void RestoreSession()
        {
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Starting...");

            _pendingTerminalSessions = _settings.GetSessionTerminals();
            int terminalCount = _pendingTerminalSessions?.Count ?? 0;
            System.Diagnostics.Trace.WriteLine($"[RestoreSession] Session terminals: {terminalCount}");

            bool restoredFromXml = false;
            string layoutPath = _settings.GetLayoutFilePath();

            // Try XML-based restore first — preserves full layout (positions, proportions, dock sides)
            if (terminalCount > 0 && System.IO.File.Exists(layoutPath))
            {
                try
                {
                    _terminalRestoreIndex = 0;
                    _dockPanel.LoadFromXml(layoutPath, GetContentFromPersistString);
                    restoredFromXml = true;
                    System.Diagnostics.Trace.WriteLine("[RestoreSession] Restored layout from XML");

                    // Apply themes to restored panels (LoadFromXml restores positions but not runtime state)
                    ApplyThemesToPanels();

                    // Show start screens on restored terminals
                    foreach (var pane in _dockPanel.Contents.OfType<TerminalDocument>())
                    {
                        pane.ShowStartScreen();
                    }

                    // Focus the first terminal
                    var firstTerminal = _dockPanel.Contents.OfType<TerminalDocument>().FirstOrDefault();
                    if (firstTerminal != null)
                    {
                        firstTerminal.Activate();
                        _lastActiveTerminal = firstTerminal;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RestoreSession] XML restore failed, falling back to manual: {ex.Message}");
                    restoredFromXml = false;
                }
            }

            // Fallback: manual restore from session data
            if (!restoredFromXml)
            {
                if (terminalCount == 0)
                {
                    System.Diagnostics.Trace.WriteLine("[RestoreSession] No session data, creating default terminal...");
                    AddNewTerminal();
                }
                else
                {
                    foreach (var session in _pendingTerminalSessions)
                    {
                        var doc = CreateTerminalForRestore(session);
                        doc.Show(_dockPanel, DockState.Document);
                        doc.ShowStartScreen();
                    }

                    // Apply grid layout to arrange terminals evenly
                    var terminals = _gridManager.GetTerminalDocuments();
                    if (terminals.Count > 1)
                    {
                        var preset = GridLayoutManager.GetRecommendedPreset(terminals.Count);
                        _gridManager.ApplyPreset(preset);
                        System.Diagnostics.Trace.WriteLine($"[RestoreSession] Applied preset {preset} for {terminals.Count} terminals");
                    }

                    // Focus the first terminal
                    var firstDoc = terminals.FirstOrDefault();
                    if (firstDoc != null)
                    {
                        firstDoc.Activate();
                        _lastActiveTerminal = firstDoc;
                    }
                }

                // Restore panel visibility and dock positions from saved state (manual mode only)
                RestorePanelStates();
            }

            // Refresh project panel now that terminals have their directories
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Calling RefreshProjectPanel...");
            RefreshProjectPanel();
            System.Diagnostics.Trace.WriteLine("[RestoreSession] RefreshProjectPanel returned");

            // Signal loading complete (splash will show main form when animation finishes)
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Invoking LoadingComplete event...");
            LoadingComplete?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Trace.WriteLine("[RestoreSession] LoadingComplete event invoked");

            // Refresh dashboard with project info now that session is restored
            RefreshDashboardProjectInfo();

            // Index sessions for vector search (session sync is handled by
            // SessionLineageService in InitializeMcpServerAndChatPanel — the old
            // SessionSyncService.SyncProject relied on sessions-index.json which
            // Claude Code stopped writing after Feb 2026)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _sessionIndexingService.IndexAllSessionsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Session indexing error: {ex.Message}");
                }
            });

            // DISABLED FOR DISTRIBUTION — re-enable after debugging auto-sync feature
            // Start message queue polling timer (every 2 seconds)
            _messageQueueTimer = new System.Windows.Forms.Timer();
            _messageQueueTimer.Interval = 2000; // 2 seconds
            _messageQueueTimer.Tick += OnMessageQueueTimerTick;
            _messageQueueTimer.Start();
            System.Diagnostics.Debug.WriteLine("[MainForm] Message queue timer started (2 second interval)");
        }

        /// <summary>
        /// Periodic session sync handler - imports new sessions to SessionLineageService (multiterminal.db).
        /// </summary>
        private void OnSessionSyncTimerTick(object sender, EventArgs e)
        {
            // Skip if already indexing
            if (_sessionIndexingService?.IsIndexing == true)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] Session sync skipped - indexing already in progress");
                return;
            }

            var lineageService = _mcpServer?.Broker?.SessionLineageService;
            if (lineageService == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] Session sync skipped - SessionLineageService not available");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[MainForm] Session sync timer triggered - starting background sync");

            var debugLog = _debugLogService;
            var projectDb = _sharedProjectDatabase;

            // Run sync in background — same logic as startup, syncs all registered projects
            _ = Task.Run(() =>
            {
                try
                {
                    var projects = projectDb?.GetAllProjects();
                    if (projects == null || projects.Count == 0)
                        return;

                    int totalImported = 0, totalSkipped = 0, totalFailed = 0;

                    foreach (var project in projects)
                    {
                        if (string.IsNullOrEmpty(project.Path))
                            continue;

                        string claudeFolder = Services.SessionLineageService.GetClaudeProjectFolder(project.Path);
                        if (claudeFolder == null)
                            continue;

                        var result = lineageService.SyncNewSessions(claudeFolder);
                        totalImported += result.Imported;
                        totalSkipped += result.Skipped;
                        totalFailed += result.Failed;
                    }

                    if (totalImported > 0)
                        debugLog?.Info("MainForm", $"Session sync (periodic): {totalImported} imported, {totalSkipped} skipped, {totalFailed} failed across {projects.Count} projects");
                    else
                        System.Diagnostics.Debug.WriteLine($"[MainForm] Session sync (periodic): no new sessions ({totalSkipped} already imported)");
                }
                catch (Exception ex)
                {
                    debugLog?.Error("MainForm", $"Session sync error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Message queue polling handler - processes pending messages that failed immediate delivery.
        /// Also handles periodic cleanup of old delivered messages.
        /// </summary>
        private async void OnMessageQueueTimerTick(object sender, EventArgs e)
        {
            if (_mcpServer?.Broker == null) return;

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] OnMessageQueueTimerTick ENTRY (2s timer fired)");

            try
            {
                // Process pending messages (retry delivery)
                int delivered = await _mcpServer.Broker.ProcessPendingMessages();
                if (delivered > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MainForm] Message queue: {delivered} pending messages delivered");
                }

                // Periodic cleanup of old messages (once per hour)
                _messageQueueCleanupCounter++;
                if (_messageQueueCleanupCounter >= CleanupIntervalTicks)
                {
                    _messageQueueCleanupCounter = 0;
                    int cleaned = _mcpServer.Broker.CleanupOldMessages(24);
                    if (cleaned > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] Message queue: cleaned up {cleaned} old messages");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Message queue timer error: {ex.Message}");
            }
        }

        private IDockContent GetContentFromPersistString(string persistString)
        {
            // Handle both old PromptTreeDocument and new ProjectPanelDocument for backward compatibility
            if (persistString == typeof(ProjectPanelDocument).FullName ||
                persistString == typeof(PromptTreeDocument).FullName)
            {
                return _projectPanel;
            }

            if (persistString == typeof(TerminalDocument).FullName)
            {
                // Get session info for this terminal (by index)
                TerminalSessionInfo sessionInfo = null;
                if (_pendingTerminalSessions != null && _terminalRestoreIndex < _pendingTerminalSessions.Count)
                {
                    sessionInfo = _pendingTerminalSessions[_terminalRestoreIndex++];
                }

                return CreateTerminalForRestore(sessionInfo);
            }

            // Handle Chat, Activity, and Tasks panels (recreate if disposed)
            if (persistString == "ChatPanel")
            {
                if (_chatPanel == null || _chatPanel.IsDisposed)
                {
                    _chatPanel = new ChatPanelDocument();
                    if (_mcpServer?.Broker != null)
                    {
                        _chatPanel.Initialize(_mcpServer.Broker);
                        _chatPanel.InjectRequested += OnChatInjectRequested;
                        _chatPanel.ReplyRequested += OnChatReplyRequested;
                    }
                }
                return _chatPanel;
            }

            if (persistString == "ActivityPanel")
            {
                if (_activityPanel == null || _activityPanel.IsDisposed)
                {
                    _activityPanel = new ActivityPanelDocument();
                    if (_mcpServer?.Broker?.ActivityService != null)
                    {
                        _activityPanel.Initialize(_mcpServer.Broker.ActivityService, _mcpServer.PoolCoordinator, _mcpServer.Broker, _mcpServer.Broker.TaskDb);
                    }
                }
                return _activityPanel;
            }

            if (persistString == "TasksPanel")
            {
                if (_tasksPanel == null || _tasksPanel.IsDisposed)
                {
                    _tasksPanel = new TasksPanelDocument();
                    _tasksPanel.SetDebugLogService(_debugLogService);
                    if (_mcpServer?.Broker != null)
                    {
                        _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                        _tasksPanel.InjectRequested += OnChatInjectRequested;
                    }
                }
                return _tasksPanel;
            }

            if (persistString == "ProfilePanel")
            {
                if (_profilePanel == null || _profilePanel.IsDisposed)
                {
                    _profilePanel = new ProfilePanel.ProfilePanelDocument();
                    if (_mcpServer?.Broker != null)
                    {
                        _profilePanel.SetMessageBroker(_mcpServer.Broker);
                    }
                }
                return _profilePanel;
            }

            if (persistString == "InboxPanel")
            {
                if (_inboxPanel == null || _inboxPanel.IsDisposed)
                {
                    _inboxPanel = new InboxPanelDocument();
                    if (_mcpServer?.Broker != null)
                    {
                        _inboxPanel.Initialize(_mcpServer.Broker);
                    }
                    _inboxPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                }
                return _inboxPanel;
            }

            if (persistString == "OfficePanel")
            {
                if (_officePanel == null || _officePanel.IsDisposed)
                {
                    _officePanel = new OfficePanel.OfficePanelDocument();
                    if (_mcpServer?.Broker?.ActivityService != null)
                    {
                        _officePanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                    }
                }
                return _officePanel;
            }

            if (persistString == "DebugPanel")
            {
                if (_debugPanel == null || _debugPanel.IsDisposed)
                {
                    _debugPanel = new DebugPanel();
                    _debugPanel.Initialize(_debugLogService);
                }
                return _debugPanel;
            }

            if (persistString == "FilePreviewPanel")
            {
                if (_filePreviewPanel == null || _filePreviewPanel.IsDisposed)
                {
                    _filePreviewPanel = new FilePreviewPanel.FilePreviewPanelDocument();
                    _filePreviewPanel.DebugLogService = _debugLogService;
                }
                return _filePreviewPanel;
            }

            return null;
        }

        private TerminalDocument CreateTerminalForRestore(TerminalSessionInfo sessionInfo)
        {
            var doc = new TerminalDocument();
            doc.Terminal.SetDebugLogService(_debugLogService);
            doc.SetDebugLogService(_debugLogService); // Enable status bar logging
            doc.SetTheme(_currentTheme);
            doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
            doc.TerminalExited += OnTerminalExited;
            doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
            doc.AgentSplitRatioChanged += OnAgentSplitRatioChanged;
            doc.HudSplitRatioChanged += OnHudSplitRatioChanged;
            doc.StatusBarHeightChanged += OnStatusBarHeightChanged;
            doc.TaskHudZoomChanged += OnTaskHudZoomChanged;
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            if (sessionInfo != null)
            {
                doc.PendingWorkingDirectory = sessionInfo.WorkingDirectory;
                doc.SetFontSize(sessionInfo.FontSize);
                if (!string.IsNullOrEmpty(sessionInfo.CustomTitle))
                {
                    doc.CustomTitle = sessionInfo.CustomTitle;
                }
            }
            else
            {
                doc.SetFontSize(_settings?.GetTerminalFontSize() ?? 10f);
            }

            // Apply global split ratios and zoom levels
            if (_settings != null)
            {
                doc.ApplyAgentSplitRatio(_settings.GetAgentPanelSplitRatio());
                doc.ApplyHudSplitRatio(_settings.GetHudSplitRatio());
                doc.ApplyTaskHudZoom(_settings.GetTaskHudZoom());
                doc.ApplyStatusBarHeight(_settings.GetStatusBarHeight());
            }

            _lastActiveTerminal = doc;
            return doc;
        }

        #region Project Panel

        private void ToggleProjectPanel()
        {
            if (_projectPanel == null) return;

            if (_projectPanel.Visible)
            {
                _projectPanel.Hide();
            }
            else
            {
                _projectPanel.Show(_dockPanel, GetSavedDockState("ProjectPanel", DockState.DockLeft));
                RefreshProjectPanel();
            }
        }

        private void ToggleFilePreviewPanel()
        {
            if (_filePreviewPanel == null || _filePreviewPanel.IsDisposed)
            {
                _filePreviewPanel = new FilePreviewPanel.FilePreviewPanelDocument();
                _filePreviewPanel.DebugLogService = _debugLogService;
                _filePreviewPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _filePreviewPanel.Show(_dockPanel, GetSavedDockState("FilePreviewPanel", DockState.DockBottom));
                return;
            }

            if (_filePreviewPanel.Visible)
            {
                _filePreviewPanel.Hide();
            }
            else
            {
                _filePreviewPanel.Show(_dockPanel, GetSavedDockState("FilePreviewPanel", DockState.DockBottom));
                _filePreviewPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void OnFilePreviewRequested(object sender, string filePath)
        {
            // Ensure the preview panel is visible
            if (_filePreviewPanel == null || _filePreviewPanel.IsDisposed)
            {
                _filePreviewPanel = new FilePreviewPanel.FilePreviewPanelDocument();
                _filePreviewPanel.DebugLogService = _debugLogService;
                _filePreviewPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _filePreviewPanel.Show(_dockPanel, GetSavedDockState("FilePreviewPanel", DockState.DockBottom));
            }
            else if (!_filePreviewPanel.Visible)
            {
                _filePreviewPanel.Show(_dockPanel, GetSavedDockState("FilePreviewPanel", DockState.DockBottom));
                _filePreviewPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }

            _debugLogService?.Info("FilePreview", $"OnFilePreviewRequested: {filePath}");
            _filePreviewPanel.PreviewFile(filePath);
        }

        private void ToggleChatPanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_chatPanel == null || _chatPanel.IsDisposed)
            {
                _chatPanel = new ChatPanelDocument();
                if (_mcpServer?.Broker != null)
                {
                    _chatPanel.Initialize(_mcpServer.Broker);
                    _chatPanel.InjectRequested += OnChatInjectRequested;
                    _chatPanel.ReplyRequested += OnChatReplyRequested;
                    _chatPanel.UpdateConnectionStatus(true);
                }
                _chatPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _chatPanel.Show(_dockPanel, GetSavedDockState("ChatPanel", DockState.DockRight));
                return;
            }

            if (_chatPanel.Visible)
            {
                _chatPanel.Hide();
            }
            else
            {
                _chatPanel.Show(_dockPanel, GetSavedDockState("ChatPanel", DockState.DockRight));
                _chatPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleActivityPanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_activityPanel == null || _activityPanel.IsDisposed)
            {
                _activityPanel = new ActivityPanelDocument();
                if (_mcpServer?.Broker?.ActivityService != null)
                {
                    _activityPanel.Initialize(_mcpServer.Broker.ActivityService, _mcpServer.PoolCoordinator, _mcpServer.Broker, _mcpServer.Broker.TaskDb);
                }
                _activityPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _activityPanel.Show(_dockPanel, GetSavedDockState("ActivityPanel", DockState.DockRight));
                return;
            }

            if (_activityPanel.Visible)
            {
                _activityPanel.Hide();
            }
            else
            {
                _activityPanel.Show(_dockPanel, GetSavedDockState("ActivityPanel", DockState.DockRight));
                _activityPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleOfficePanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_officePanel == null || _officePanel.IsDisposed)
            {
                _officePanel = new OfficePanel.OfficePanelDocument();
                if (_mcpServer?.Broker?.ActivityService != null)
                {
                    _officePanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                }
                _officePanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _officePanel.Show(_dockPanel, GetSavedDockState("OfficePanel", DockState.DockRight));
                return;
            }

            if (_officePanel.Visible)
            {
                _officePanel.Hide();
            }
            else
            {
                _officePanel.Show(_dockPanel, GetSavedDockState("OfficePanel", DockState.DockRight));
                _officePanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleAgentPanel()
        {
            // Agent panels are always embedded in terminals.
            // Toggle visibility of all EmbeddedAgentPanels across terminals.
            if (_embeddedAgentMap.Count == 0) return;

            // Get unique terminals that have embedded agents
            var terminals = new HashSet<TerminalDocument>();
            foreach (var info in _embeddedAgentMap.Values)
            {
                if (info.Terminal?.EmbeddedAgentPanel != null)
                    terminals.Add(info.Terminal);
            }

            foreach (var terminal in terminals)
            {
                terminal.EmbeddedAgentPanel.Visible = !terminal.EmbeddedAgentPanel.Visible;
            }
        }

        /// <summary>
        /// Create an embedded agent panel for a specific AgentProcess.
        /// </summary>
        private void CreateAgentPanel(AgentProcess agent, string agentName, string agentTerminalId, string spawnerName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            CreateAgentPanel((IAgentMessageSource)agent, agentName, agentTerminalId, spawnerName, taskDescription, subagentType, isTeamAgent);
        }

        /// <summary>
        /// Create an embedded agent panel for any IAgentMessageSource.
        /// Used by both AgentProcess (piped I/O) and TranscriptTailer (native team watching).
        /// Always embeds in a terminal's EmbeddedAgentPanel (spawner → last active → first available).
        /// </summary>
        private void CreateAgentPanel(IAgentMessageSource source, string agentName, string panelKey, string spawnerName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            string layout = _settings?.GetAgentPanelLayout() ?? "SplitRight";

            // DoNotShow - skip panel creation entirely
            if (layout == "DoNotShow")
                return;

            // Always embed in a terminal's EmbeddedAgentPanel.
            // Priority: exact name lookup → Text/TabText scan → last active → first available.
            TerminalDocument targetTerminal = null;

            if (!string.IsNullOrEmpty(spawnerName))
            {
                _debugLogService?.Info("CreateAgentPanel", $"Looking for spawner '{spawnerName}' in _agentNameToTerminalDoc ({_agentNameToTerminalDoc.Count} entries)");
                lock (_terminalDocMapLock)
                {
                    // Primary: exact agent name → terminal lookup (populated at registration)
                    if (_agentNameToTerminalDoc.TryGetValue(spawnerName, out var namedTerminal))
                    {
                        targetTerminal = namedTerminal;
                        _debugLogService?.Info("CreateAgentPanel", $"  MATCH FOUND via name map: spawner='{spawnerName}'");
                    }

                    // Fuzzy match: "Agent Alice" → "Alice", or "Alice" → "Agent Alice"
                    // The hook may record "Alice" but register_terminal uses "Agent Alice" or vice versa
                    if (targetTerminal == null)
                    {
                        foreach (var kvp in _agentNameToTerminalDoc)
                        {
                            if (kvp.Key.Contains(spawnerName, StringComparison.OrdinalIgnoreCase) ||
                                spawnerName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                targetTerminal = kvp.Value;
                                _debugLogService?.Info("CreateAgentPanel", $"  MATCH FOUND via fuzzy name: '{kvp.Key}' ~ '{spawnerName}'");
                                break;
                            }
                        }
                    }

                    // Fallback: scan terminal titles (handles unregistered or renamed terminals)
                    if (targetTerminal == null)
                    {
                        foreach (var kvp in _terminalDocMap)
                        {
                            if (kvp.Value.Text?.Contains(spawnerName, StringComparison.OrdinalIgnoreCase) == true ||
                                kvp.Value.TabText?.Contains(spawnerName, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                targetTerminal = kvp.Value;
                                _debugLogService?.Info("CreateAgentPanel", $"  MATCH FOUND via title scan: spawner='{kvp.Key}'");
                                break;
                            }
                        }
                    }
                }
            }

            // Fallback for NON-team agents only: try last active terminal, then first available.
            // Team agents MUST route to their spawner's terminal — wrong terminal causes
            // jittering and stale panels. Better to skip the panel than show it on the wrong terminal.
            if (!isTeamAgent)
            {
                if (targetTerminal?.EmbeddedAgentPanel == null && _lastActiveTerminal?.EmbeddedAgentPanel != null)
                {
                    targetTerminal = _lastActiveTerminal;
                    _debugLogService?.Info("CreateAgentPanel", $"FALLBACK: Using last active terminal for non-team agent '{agentName}'");
                }

                if (targetTerminal?.EmbeddedAgentPanel == null)
                {
                    lock (_terminalDocMapLock)
                    {
                        foreach (var kvp in _terminalDocMap)
                        {
                            if (kvp.Value.EmbeddedAgentPanel != null)
                            {
                                targetTerminal = kvp.Value;
                                _debugLogService?.Info("CreateAgentPanel", $"Using first available terminal '{kvp.Key}' as fallback for '{agentName}'");
                                break;
                            }
                        }
                    }
                }
            }
            else if (targetTerminal?.EmbeddedAgentPanel == null)
            {
                _debugLogService?.Warning("CreateAgentPanel", $"Team agent '{agentName}' spawner '{spawnerName ?? "null"}' not found in any terminal — skipping panel to avoid misrouting");
                return;
            }

            if (targetTerminal?.EmbeddedAgentPanel == null)
            {
                _debugLogService?.Error("CreateAgentPanel", $"No terminal with EmbeddedAgentPanel found for '{agentName}' - cannot create panel");
                return;
            }

            _debugLogService?.Info("CreateAgentPanel", $"Embedding agent '{agentName}' in terminal '{targetTerminal.TabText}'");

            var slot = targetTerminal.EmbeddedAgentPanel.AddAgentSlot(agentName);
            var control = new AgentPanelControl { Dock = DockStyle.Fill };
            slot.Controls.Add(control);
            control.AttachAgent(source, agentName, taskDescription, subagentType, isTeamAgent);
            control.ApplyTheme(_currentTheme == TerminalTheme.Dark);

            // Apply global agent panel zoom and propagate changes to all panels
            double agentZoom = _settings?.GetAgentPanelZoom() ?? 1.0;
            control.SetZoomFactor(agentZoom);
            control.ZoomChanged += (s, zoom) => OnEmbeddedAgentPanelZoomChanged(zoom);

            _embeddedAgentMap[panelKey] = (control, slot, targetTerminal);

            // Manual close via the X button inside the agent panel WebView
            control.CloseRequested += (s, e) =>
            {
                async void HandleEmbeddedCloseRequested()
                {
                    // If agent is still running, prompt user before closing
                    if (control.HasActiveAgent)
                    {
                        var result = MessageBox.Show(
                            this,
                            "An agent is still running in this panel.\n\nTerminate the agent process?",
                            "Close Agent Panel",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Cancel) return;
                        if (result == DialogResult.Yes)
                            await control.StopAgentAsync();
                    }

                    if (_embeddedAgentMap.Remove(panelKey, out var info))
                    {
                        info.Control?.DetachAgent();
                        info.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(info.Slot);
                        info.Control?.Dispose();
                        if (info.Terminal?.EmbeddedAgentPanel?.AgentSlotCount == 0)
                            info.Terminal.ForceCollapseAgentPanel();
                    }
                }

                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action(HandleEmbeddedCloseRequested));
                    else
                        HandleEmbeddedCloseRequested();
                }
                catch (ObjectDisposedException) { }
            };

            // Auto-remove slot when agent completes.
            // Panel is removed for all agents (team and non-team) once the process exits.
            source.Stopped += (s, exitCode) =>
            {
                void HandleEmbeddedStopped()
                {
                    if (_embeddedAgentMap.Remove(panelKey, out var info))
                    {
                        info.Control?.DetachAgent();
                        info.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(info.Slot);
                        info.Control?.Dispose();
                        // Force-collapse if no remaining slots
                        if (info.Terminal?.EmbeddedAgentPanel?.AgentSlotCount == 0)
                            info.Terminal.ForceCollapseAgentPanel();
                    }
                }

                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action(HandleEmbeddedStopped));
                    else
                        HandleEmbeddedStopped();
                }
                catch (ObjectDisposedException) { }
            };

        }

        /// <summary>
        /// Initialize the TeamWatcherService to detect native Claude Code agent teams
        /// and display their conversations in Agent Panel tabs.
        /// </summary>
        private void InitializeTeamWatcher()
        {
            try
            {
                // Derive the project slug from the working directory
                // Pattern: H:\DevLaptop\Foo\Bar → H--DevLaptop-Foo-Bar
                string workingDir = System.IO.Directory.GetCurrentDirectory();
                string projectSlug = GetClaudeProjectSlug(workingDir);

                _teamWatcher = new TeamWatcherService(projectSlug);
                _teamWatcher.DebugLogService = _debugLogService;

                _teamWatcher.TeammateDiscovered += (s, e) =>
                {
                    // Resolve spawner: if hook didn't provide it, try matching the team lead's
                    // cwd against registered terminal working directories.
                    string resolvedSpawner = e.SpawnerName;
                    if (string.IsNullOrEmpty(resolvedSpawner) && !string.IsNullOrEmpty(e.LeadCwd))
                    {
                        lock (_terminalDocMapLock)
                        {
                            foreach (var kvp in _terminalDocMap)
                            {
                                string termCwd = kvp.Value.GetWorkingDirectory();
                                if (!string.IsNullOrEmpty(termCwd) &&
                                    termCwd.Equals(e.LeadCwd, StringComparison.OrdinalIgnoreCase))
                                {
                                    resolvedSpawner = kvp.Value.TabText ?? kvp.Key;
                                    _debugLogService?.Info("MainForm", $"Resolved spawner for team '{e.TeamName}' via cwd match: '{resolvedSpawner}' (cwd={e.LeadCwd})");
                                    break;
                                }
                            }
                        }
                    }

                    _debugLogService?.Info("MainForm", $"Native teammate discovered: {e.MemberName} in team {e.TeamName}, spawner: {resolvedSpawner ?? "(unknown)"}");

                    string panelKey = $"team:{e.TeamName}:{e.MemberName}";

                    void DoCreate()
                    {
                        if (!_embeddedAgentMap.ContainsKey(panelKey))
                            CreateAgentPanel(e.Tailer, $"{e.MemberName} ({e.TeamName})", panelKey, resolvedSpawner, e.TaskDescription, e.SubagentType, isTeamAgent: true);
                    }

                    // Use BeginInvoke (async) instead of Invoke (sync) to prevent deadlocks
                    // when multiple terminals discover team agents concurrently.
                    if (InvokeRequired)
                    {
                        try { BeginInvoke(new Action(DoCreate)); }
                        catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        DoCreate();
                    }
                };

                _teamWatcher.TeamRemoved += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Native team removed: {e.TeamName} (members: {string.Join(", ", e.MemberNames)})");

                    if (InvokeRequired)
                    {
                        try { BeginInvoke(new Action(() => CleanupTeamPanels(e.TeamName, e.MemberNames, e.TranscriptPaths))); }
                        catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        CleanupTeamPanels(e.TeamName, e.MemberNames, e.TranscriptPaths);
                    }
                };

                _teamWatcher.MemberRemoved += (s, e) =>
                {
                    string panelKey = $"team:{e.TeamName}:{e.MemberName}";
                    _debugLogService?.Info("MainForm", $"Native teammate removed: {e.MemberName} from team {e.TeamName}, closing panel");

                    void CloseAgentPanel()
                    {
                        if (_embeddedAgentMap.Remove(panelKey, out var embedded))
                        {
                            embedded.Control?.DetachAgent();
                            if (embedded.Control != null)
                                _ = embedded.Control.StopAgentAsync();
                            embedded.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(embedded.Slot);
                            embedded.Control?.Dispose();
                            // Force-collapse if no remaining slots
                            if (embedded.Terminal?.EmbeddedAgentPanel?.AgentSlotCount == 0)
                                embedded.Terminal.ForceCollapseAgentPanel();
                        }
                    }

                    if (InvokeRequired)
                    {
                        try { BeginInvoke(new Action(CloseAgentPanel)); }
                        catch (ObjectDisposedException) { }
                    }
                    else
                        CloseAgentPanel();
                };

                _teamWatcher.SubagentDiscovered += (s, e) =>
                {
                    // Resolve spawner from session_agent_map when hook tracking missed it
                    string resolvedSpawner = e.SpawnerName;
                    if (string.IsNullOrEmpty(resolvedSpawner) && !string.IsNullOrEmpty(e.ParentSessionId))
                    {
                        try
                        {
                            resolvedSpawner = _mcpServer?.Broker?.TaskDb?.GetSessionAgentName(e.ParentSessionId);
                            if (!string.IsNullOrEmpty(resolvedSpawner))
                                _debugLogService?.Info("MainForm", $"Resolved spawner from session_agent_map: session={e.ParentSessionId} → {resolvedSpawner}");
                        }
                        catch { /* best effort */ }
                    }

                    _debugLogService?.Info("MainForm", $"Non-team subagent discovered: {e.MemberName}, spawner: {resolvedSpawner ?? "(unknown)"}, desc: \"{e.TaskDescription ?? ""}\"");

                    string panelKey = $"subagent:{e.MemberName}";

                    void DoCreate()
                    {
                        if (!_embeddedAgentMap.ContainsKey(panelKey))
                            CreateAgentPanel(e.Tailer, e.MemberName, panelKey, resolvedSpawner, e.TaskDescription, e.SubagentType);
                    }

                    if (InvokeRequired)
                    {
                        try { BeginInvoke(new Action(DoCreate)); }
                        catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        DoCreate();
                    }
                };

                // Wire broker event for SubagentStop hook → close agent panel
                if (_mcpServer?.Broker != null)
                {
                    _mcpServer.Broker.AgentPanelCloseRequested += (s, transcriptPath) =>
                    {
                        _teamWatcher?.StopOrphanTailer(transcriptPath);
                    };
                }

                // Bridge team agent SendMessage calls to ChatPanel via MessageBroker
                _teamWatcher.TeamMessageSent += (s, e) =>
                {
                    try
                    {
                        var json = System.Text.Json.JsonDocument.Parse(e.Message.Content);
                        var root = json.RootElement;
                        string msgType = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                        string recipient = root.TryGetProperty("recipient", out var r) ? r.GetString() : "";
                        string content = root.TryGetProperty("content", out var c) ? c.GetString() : "";

                        if (msgType == "message" && !string.IsNullOrEmpty(content))
                        {
                            _mcpServer?.Broker?.RecordTeamMessage(e.MemberName, recipient, content, e.TeamName);
                            // Native team agents deliver messages via Claude Code's built-in
                            // SendMessage/teammate-message channel. No inbox bridging needed —
                            // writing to InboxFileWriter caused duplicate delivery.
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogService?.Trace("MainForm", $"Failed to parse team message from {e.MemberName}: {ex.Message}");
                    }
                };

                _teamWatcher.StartWatching();
                System.Diagnostics.Debug.WriteLine($"[MainForm] TeamWatcherService started (slug: {projectSlug})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] Error initializing TeamWatcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize the InboxMonitorService to detect new inbox files and nudge idle terminals.
        /// When Claude Code is idle (waiting for user input), hooks don't fire and messages go
        /// unread. This service watches for inbox file creation and injects a minimal prompt
        /// into the target terminal, triggering the UserPromptSubmit hook which reads the inbox.
        /// </summary>
        private void InitializeInboxMonitor()
        {
            try
            {
                _inboxMonitor = new InboxMonitorService();
                _inboxMonitor.DebugLogService = _debugLogService;

                _inboxMonitor.InboxFileDetected += (s, terminalName) =>
                {
                    // Anti-reentrance: don't nudge if we nudged this terminal recently
                    var now = DateTime.UtcNow;
                    if (_lastNudgeTime.TryGetValue(terminalName, out var lastNudge))
                    {
                        if (now - lastNudge < NudgeThrottle)
                        {
                            _debugLogService?.Trace("MainForm",
                                $"Inbox nudge throttled for {terminalName} (last nudge {(now - lastNudge).TotalSeconds:F1}s ago)");
                            return;
                        }
                    }

                    // Marshal to UI thread for dock panel access
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() => NudgeTerminal(terminalName)));
                    }
                    else
                    {
                        NudgeTerminal(terminalName);
                    }
                };

                _inboxMonitor.StartWatching();
                _debugLogService?.Info("MainForm", "InboxMonitorService started");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Error initializing InboxMonitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize the Oracle advisory agent service.
        /// Registers "Oracle" as a terminal (always visible in terminal list) but doesn't
        /// spawn the terminal until someone sends Oracle a message (on-demand lifecycle).
        /// Oracle runs as a real ConPTY terminal — same as every other agent — so auth,
        /// channels, and MCP tools all work through the standard path.
        /// </summary>
        private void InitializeOracle()
        {
            try
            {
                // Register Oracle as a terminal so it's always visible
                string oracleDocId = $"oracle-{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                _oracleService = new OracleService(
                    log: (source, msg) => _debugLogService?.Info(source, msg));
                var regResult = _mcpServer?.Broker?.RegisterTerminal(OracleService.OracleName, oracleDocId);
                _oracleTerminalId = regResult?.TerminalId ?? oracleDocId;

                // On idle timeout, close Oracle's terminal from the UI thread
                // Guard: timer callback may fire during app shutdown after form is disposed
                _oracleService.IdleTimeout += (s, e) =>
                {
                    if (IsDisposed) return;
                    if (InvokeRequired)
                        BeginInvoke(new Action(CloseOracleTerminal));
                    else
                        CloseOracleTerminal();
                };

                _debugLogService?.Info("MainForm", $"Oracle registered as terminal '{OracleService.OracleName}' (ID: {_oracleTerminalId}) — on-demand, will spawn on first message");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to initialize Oracle: {ex.Message}");
            }
        }

        /// <summary>
        /// Create and dock Oracle's ConPTY terminal. Called on-demand when Oracle
        /// receives its first message (or after idle shutdown + new message).
        /// </summary>
        private void CreateOracleTerminal()
        {
            if (_oracleTerminalDoc != null)
                return; // Already running

            try
            {
                var doc = new Docking.TerminalDocument();
                doc.Terminal.SetDebugLogService(_debugLogService);
                doc.SetDebugLogService(_debugLogService);
                doc.SetTheme(_currentTheme);
                doc.SetMessageBroker(_mcpServer?.Broker);

                string autoRunCommand = _oracleService.BuildAutoRunCommand();
                string workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                doc.StartTerminal(
                    workingDirectory: workingDir,
                    terminalName: OracleService.OracleName,
                    autoRunCommand: autoRunCommand);

                // Dock alongside other terminal tabs
                doc.Show(_dockPanel, DockState.Document);

                // Clean up when Oracle's terminal exits (idle timeout, manual close, or Claude exit)
                // Guard: CloseOracleTerminal may have already nulled the reference
                doc.TerminalExited += (s, e) =>
                {
                    if (_oracleTerminalDoc == null) return;
                    _oracleTerminalDoc = null;
                    _oracleService.NotifyStopped();
                    _debugLogService?.Info("MainForm", "Oracle terminal exited");
                };

                _oracleTerminalDoc = doc;
                _oracleService.NotifyStarted();
                _debugLogService?.Info("MainForm", "Oracle terminal created and docked");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to create Oracle terminal: {ex.Message}");
            }
        }

        /// <summary>
        /// Close Oracle's terminal (called on idle timeout or app shutdown).
        /// </summary>
        private void CloseOracleTerminal()
        {
            try
            {
                var doc = _oracleTerminalDoc;
                if (doc != null)
                {
                    _oracleTerminalDoc = null;
                    _oracleService?.NotifyStopped();
                    doc.Close();
                    _debugLogService?.Info("MainForm", "Oracle terminal closed");
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to close Oracle terminal: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn Oracle's terminal and deliver a queued message after the channel is ready.
        /// Called from OnMcpMessageDelivery when Oracle isn't running and a message arrives.
        /// </summary>
        private async System.Threading.Tasks.Task SpawnOracleAndDeliverAsync(string messageId, string sender, string message)
        {
            try
            {
                // Create Oracle terminal on UI thread
                if (InvokeRequired)
                    Invoke(new Action(CreateOracleTerminal));
                else
                    CreateOracleTerminal();

                // Wait for Oracle's channel server to register its port with the broker.
                // IMPORTANT: Verify via /health that the port actually belongs to Oracle,
                // not another terminal — registration races can cause stale ports.
                for (int i = 0; i < 120; i++) // Up to 60 seconds
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    var oracleTerminal = _mcpServer?.Broker?.GetTerminal(OracleService.OracleName);
                    if (oracleTerminal?.ChannelPort > 0)
                    {
                        int port = oracleTerminal.ChannelPort.Value;

                        // Verify the port belongs to Oracle by checking the channel health endpoint
                        bool portVerified = false;
                        try
                        {
                            var healthResponse = await _channelHttpClient.GetAsync($"http://127.0.0.1:{port}/health");
                            if (healthResponse.IsSuccessStatusCode)
                            {
                                var healthJson = await healthResponse.Content.ReadAsStringAsync();
                                var healthDoc = System.Text.Json.JsonDocument.Parse(healthJson);
                                var agentName = healthDoc.RootElement.GetProperty("agent").GetString();
                                portVerified = string.Equals(agentName, OracleService.OracleName, StringComparison.OrdinalIgnoreCase);
                                if (!portVerified)
                                {
                                    _debugLogService?.Warning("MainForm", $"Oracle port {port} health check returned agent '{agentName}' — wrong terminal, will keep polling");
                                    continue; // Port belongs to someone else, keep waiting
                                }
                            }
                        }
                        catch
                        {
                            continue; // Port not responding yet, keep waiting
                        }

                        if (!portVerified)
                            continue;

                        // Channel verified as Oracle — deliver message
                        bool delivered = await DeliverViaChannel(port, sender, message, "normal");
                        if (delivered)
                        {
                            lock (_deduplicationLock)
                            {
                                _deliveredMessageCache[messageId] = DateTime.Now;
                            }
                            _debugLogService?.Info("MainForm", $"Delivered queued message {messageId} to Oracle via channel (port {port}, verified)");
                        }
                        else
                        {
                            _debugLogService?.Warning("MainForm", $"Channel delivery failed for queued Oracle message {messageId}");
                        }
                        return;
                    }
                }

                _debugLogService?.Warning("MainForm", $"Oracle channel did not register in 60s — message {messageId} from {sender} was NOT delivered");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"SpawnOracleAndDeliverAsync failed: {ex.Message}");
            }
            finally
            {
                _oracleSpawning = false;
            }
        }

        /// <summary>
        /// Inject a nudge prompt into a terminal's ConPTY input to wake it up.
        /// This triggers the UserPromptSubmit hook which runs inbox-check-hook.js.
        /// </summary>
        private void NudgeTerminal(string terminalName)
        {
            try
            {
                // Find the terminal document by name using _terminalDocMap (more reliable than
                // _dockPanel.Documents which can miss terminals that are mid-turn/busy)
                TerminalDocument targetTerminal = null;
                lock (_terminalDocMapLock)
                {
                    targetTerminal = _terminalDocMap.Values.FirstOrDefault(t =>
                        (t.CustomTitle?.Equals(terminalName, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        t.TabText.Equals(terminalName, StringComparison.OrdinalIgnoreCase));
                }

                if (targetTerminal == null)
                {
                    _debugLogService?.Warning("MainForm", $"No terminal found for inbox nudge: {terminalName}");
                    return;
                }

                if (!targetTerminal.IsRendererReady)
                {
                    _debugLogService?.Warning("MainForm", $"Terminal renderer not ready for nudge: {terminalName}");
                    return;
                }

                // Record nudge time for anti-reentrance
                _lastNudgeTime[terminalName] = DateTime.UtcNow;

                // Char-by-char injection via xterm.js input path.
                // Sends [cm] + CR as individual characters with 15ms delay to mimic typing.
                // Claude sees this as user input and the UserPromptSubmit hook fires.
                // This is more reliable than the old InjectInputAsync approach which used
                // separate pipe write (text) + xterm.js Enter (submission) — the two-channel
                // approach had timing issues where Enter could fail to fire.
                _debugLogService?.Info("MainForm", $"Nudging terminal {terminalName} (char-by-char, 15ms)");

                targetTerminal.TypeInput("[cm]", "cr", 20);

                _debugLogService?.Info("MainForm", $"Nudge fired on {terminalName}");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Error nudging terminal {terminalName}: {ex.Message}");
            }
        }


        private void CleanupTeamPanels(string teamName, List<string> memberNames = null, List<string> transcriptPaths = null)
        {
            string prefix = $"team:{teamName}:";

            // Close and remove team-keyed embedded panels
            var embeddedKeys = _embeddedAgentMap.Keys.Where(k => k.StartsWith(prefix)).ToList();

            // Also remove orphan panels that match the team's agent names.
            // Uses fuzzy matching (contains) to handle name variants like "Agent Alice" vs "Alice".
            if (memberNames != null)
            {
                foreach (var name in memberNames)
                {
                    // Exact match
                    string orphanKey = $"subagent:{name}";
                    if (_embeddedAgentMap.ContainsKey(orphanKey) && !embeddedKeys.Contains(orphanKey))
                        embeddedKeys.Add(orphanKey);

                    // Fuzzy match: orphan keyed as "subagent:Agent Alice" when member is "Alice" (or vice versa)
                    foreach (var key in _embeddedAgentMap.Keys)
                    {
                        if (!key.StartsWith("subagent:") || embeddedKeys.Contains(key)) continue;
                        var orphanName = key.Substring("subagent:".Length);
                        if (orphanName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(orphanName, StringComparison.OrdinalIgnoreCase))
                        {
                            embeddedKeys.Add(key);
                        }
                    }
                }
            }

            // Match orphan panels by transcript path stored in the agent's slot tag
            if (transcriptPaths != null)
            {
                foreach (var kvp in _embeddedAgentMap)
                {
                    if (embeddedKeys.Contains(kvp.Key)) continue;
                    if (kvp.Value.Control?.AttachedAgent is TranscriptTailer tailer)
                    {
                        // Check if any of the tailer's transcript files match a team transcript
                        foreach (var tp in transcriptPaths)
                        {
                            if (tailer.HasTranscriptFile(tp))
                            {
                                embeddedKeys.Add(kvp.Key);
                                _debugLogService?.Info("CleanupTeamPanels", $"Matched orphan panel '{kvp.Key}' by transcript path");
                                break;
                            }
                        }
                    }
                }
            }

            // Track which terminals had slots removed so we can force-collapse if empty
            var affectedTerminals = new HashSet<TerminalDocument>();

            foreach (var key in embeddedKeys)
            {
                if (_embeddedAgentMap.Remove(key, out var info))
                {
                    if (info.Terminal != null)
                        affectedTerminals.Add(info.Terminal);
                    // Detach before stopping to prevent the Stopped event from
                    // trying to remove an already-removed key (race condition)
                    info.Control?.DetachAgent();
                    if (info.Control != null)
                        _ = info.Control.StopAgentAsync();
                    info.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(info.Slot);
                    info.Control?.Dispose();
                }
            }

            // Force-collapse any affected terminal's agent panel if it has no remaining slots.
            // This is the direct fix — don't rely on VisibilityRequested event chain.
            foreach (var terminal in affectedTerminals)
            {
                if (terminal.EmbeddedAgentPanel?.AgentSlotCount == 0)
                {
                    terminal.ForceCollapseAgentPanel();
                }
            }
        }

        /// <summary>
        /// Convert a project path to Claude Code's project slug format.
        /// H:\DevLaptop\Foo\Bar → H--DevLaptop-Foo-Bar
        /// </summary>
        private static string GetClaudeProjectSlug(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return "unknown";

            string normalized = projectPath.Replace('/', '\\').TrimEnd('\\');
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == ':')
                {
                    sb.Append("--");
                    if (i + 1 < normalized.Length && normalized[i + 1] == '\\')
                        i++; // Skip the backslash after colon
                }
                else if (c == '\\')
                {
                    sb.Append('-');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private void ToggleTasksPanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_tasksPanel == null || _tasksPanel.IsDisposed)
            {
                _tasksPanel = new TasksPanelDocument();
                _tasksPanel.SetDebugLogService(_debugLogService);
                if (_mcpServer?.Broker != null)
                {
                    _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService);
                    _tasksPanel.InjectRequested += OnChatInjectRequested;
                }
                _tasksPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _tasksPanel.Show(_dockPanel, GetSavedDockState("TasksPanel", DockState.DockBottom));
                return;
            }

            if (_tasksPanel.Visible)
            {
                _tasksPanel.Hide();
            }
            else
            {
                _tasksPanel.Show(_dockPanel, GetSavedDockState("TasksPanel", DockState.DockBottom));
                _tasksPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleInboxPanel()
        {
            if (_inboxPanel == null || _inboxPanel.IsDisposed)
            {
                _inboxPanel = new InboxPanelDocument();
                if (_mcpServer?.Broker != null)
                {
                    _inboxPanel.Initialize(_mcpServer.Broker);
                }
                _inboxPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                _inboxPanel.Show(_dockPanel, GetSavedDockState("InboxPanel", DockState.DockRight));
                return;
            }

            if (_inboxPanel.Visible)
            {
                _inboxPanel.Hide();
            }
            else
            {
                _inboxPanel.Show(_dockPanel, GetSavedDockState("InboxPanel", DockState.DockRight));
                _inboxPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleProfilePanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_profilePanel == null || _profilePanel.IsDisposed)
            {
                _profilePanel = new ProfilePanel.ProfilePanelDocument();
                if (_mcpServer?.Broker != null)
                {
                    _profilePanel.SetMessageBroker(_mcpServer.Broker);
                }
                _profilePanel.SetTheme(_currentTheme == TerminalTheme.Dark);
                _profilePanel.Show(_dockPanel, GetSavedDockState("ProfilePanel", DockState.DockRight));
                return;
            }

            if (_profilePanel.Visible)
            {
                _profilePanel.Hide();
            }
            else
            {
                _profilePanel.Show(_dockPanel, GetSavedDockState("ProfilePanel", DockState.DockRight));
                _profilePanel.SetTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleDebugPanel()
        {
            // Recreate panel if it was disposed (user closed it with X button)
            if (_debugPanel == null || _debugPanel.IsDisposed)
            {
                _debugPanel = new DebugPanel();
                _debugPanel.Initialize(_debugLogService);
                _debugPanel.Show(_dockPanel, GetSavedDockState("DebugPanel", DockState.DockBottom));
                return;
            }

            if (_debugPanel.Visible)
            {
                _debugPanel.Hide();
            }
            else
            {
                _debugPanel.Show(_dockPanel, GetSavedDockState("DebugPanel", DockState.DockBottom));
            }
        }

        private void ToggleHud()
        {
            var doc = _lastActiveTerminal ?? _dockPanel.ActiveDocument as TerminalDocument;
            doc?.ToggleHud();
        }


        /// <summary>
        private void RefreshProjectPanel([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            System.Diagnostics.Trace.WriteLine($"[RefreshProjectPanel] Called by: {caller}");
            string workingDir = _lastActiveTerminal?.GetWorkingDirectory() ?? _settings?.GetLastDirectory();

            if (!string.IsNullOrEmpty(workingDir))
            {
                // Check for project in this directory
                var project = _projectService?.DiscoverProject(workingDir);
                if (project != null)
                {
                    // Auto-register if not already registered
                    if (!_projectService.IsProjectRegistered(workingDir))
                    {
                        _projectService.SaveProject(project);
                    }
                    _currentProject = project;
                    _projectPanel?.RefreshForProject(project);
                    return;
                }
            }

            // No project found - clear and show path-based prompts
            _currentProject = null;
            _projectPanel?.RefreshPrompts(workingDir);
        }

        private enum ProjectTerminalAction
        {
            ReplaceCurrentTerminal,
            CreateNewTerminal,
            Cancel
        }

        private class ProjectDialogResult
        {
            public ProjectTerminalAction Action { get; set; }
            public string ClaudeCommand { get; set; }
        }

        private void OnProjectSelected(object sender, ProjectSelectedEventArgs e)
        {
            if (e.Project == null) return;

            // Just view the project — no terminal launch
            _currentProject = e.Project;
            _projectService?.MarkProjectOpened(e.Project.Id);
            _projectPanel?.RefreshForProject(e.Project);
        }

        private void OnMcpJsonWriteRequested(object sender, (string ProjectId, string SourcePath) args)
        {
            if (_mcpConfigService == null || string.IsNullOrEmpty(args.ProjectId))
            {
                _projectPanel?.NotifyMcpJsonWriteResult(false, "MCP config service not available");
                return;
            }
            try
            {
                // Regenerate both global and project-level MCP configs.
                // Global config goes to ~/.claude/.mcp.json (available in all sessions).
                // Project config goes to {sourcePath}/.mcp.json (project-specific servers).
                _mcpConfigService.EnsureMcpConfigsForProject(args.ProjectId, args.SourcePath);
                _projectPanel?.NotifyMcpJsonWriteResult(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] OnMcpJsonWriteRequested error: {ex.Message}");
                _projectPanel?.NotifyMcpJsonWriteResult(false, ex.Message);
            }
        }

        private void OnProjectLaunchRequested(object sender, ProjectSelectedEventArgs e)
        {
            if (e.Project == null) return;

            // Show dialog with terminal options
            var result = ShowProjectTerminalDialog(e.Project.Name);

            TerminalDocument targetTerminal = null;

            switch (result.Action)
            {
                case ProjectTerminalAction.ReplaceCurrentTerminal:
                    // Change directory in active terminal (use source_path as canonical path)
                    if (_lastActiveTerminal != null)
                    {
                        string rawPath = !string.IsNullOrEmpty(e.Project.SourcePath) ? e.Project.SourcePath : e.Project.Path;
                        // Validate path is a rooted local directory (not a UNC path) that exists
                        string projectPath = (!string.IsNullOrEmpty(rawPath) && Path.IsPathRooted(rawPath) && !rawPath.StartsWith("\\\\") && Directory.Exists(rawPath))
                            ? rawPath
                            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        _lastActiveTerminal.Terminal.Write($"cd \"{projectPath}\"\r");
                        // Inject project ID into environment for context hook
                        if (!string.IsNullOrEmpty(e.Project.Id))
                            _lastActiveTerminal.Terminal.Write($"$env:MULTITERMINAL_PROJECT_ID = '{e.Project.Id.Replace("'", "''")}'\r");
                        targetTerminal = _lastActiveTerminal;
                    }
                    break;

                case ProjectTerminalAction.CreateNewTerminal:
                {
                    // Create new terminal at project source_path with project ID for context injection
                    string rawSourcePath = !string.IsNullOrEmpty(e.Project.SourcePath) ? e.Project.SourcePath : e.Project.Path;
                    // Validate path is a rooted local directory (not a UNC path) that exists
                    string sourcePath = (!string.IsNullOrEmpty(rawSourcePath) && Path.IsPathRooted(rawSourcePath) && !rawSourcePath.StartsWith("\\\\") && Directory.Exists(rawSourcePath))
                        ? rawSourcePath
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    // Resolve gateway profile for per-project MCP server filtering
                    string projGatewayProfile = null;
                    if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                        projGatewayProfile = Services.GatewayIntegrationService.GetGatewayProfileName(e.Project.Name);
                    AddNewTerminal(sourcePath, projectId: e.Project.Id, gatewayProfile: projGatewayProfile);
                    targetTerminal = _lastActiveTerminal; // AddNewTerminal sets this
                    break;
                }

                case ProjectTerminalAction.Cancel:
                    // Do nothing with terminal
                    break;
            }

            // Execute Claude command if selected and we have a terminal
            if (targetTerminal != null && !string.IsNullOrEmpty(result.ClaudeCommand))
            {
                // Small delay to ensure cd command completes first
                System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
                {
                    BeginInvoke(new Action(() =>
                    {
                        targetTerminal.Terminal.Write($"{result.ClaudeCommand}\r");
                    }));
                });
            }

            // Update project state and panel (unless cancelled)
            if (result.Action != ProjectTerminalAction.Cancel)
            {
                _currentProject = e.Project;
                _projectService?.MarkProjectOpened(e.Project.Id);
                _projectPanel?.RefreshForProject(e.Project);
            }
        }

        private ProjectDialogResult ShowProjectTerminalDialog(string projectName)
        {
            var dialogResult = new ProjectDialogResult { Action = ProjectTerminalAction.Cancel, ClaudeCommand = null };

            using (var form = new Form())
            {
                form.Text = "Open Project";
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = new Font("Segoe UI", 10f);

                bool isDark = _currentTheme?.IsDark ?? true;
                form.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
                form.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

                var label = new Label
                {
                    Text = $"How would you like to open \"{projectName}\"?",
                    Location = new Point(20, 20),
                    AutoSize = true
                };

                // Calculate form width based on label text length (minimum 460, grows with text)
                int minWidth = 460;
                int labelWidth = TextRenderer.MeasureText(label.Text, form.Font).Width + 50;
                int formWidth = Math.Max(minWidth, labelWidth);

                // Claude command dropdown
                var claudeLabel = new Label
                {
                    Text = "Claude Command:",
                    Location = new Point(20, 55),
                    AutoSize = true
                };

                var claudeCombo = new ComboBox
                {
                    Location = new Point(150, 52),
                    Size = new Size(formWidth - 170, 25),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.White,
                    ForeColor = isDark ? Color.White : Color.Black
                };

                // Populate Claude commands
                claudeCombo.Items.Add("(None)");
                var commands = _settings.GetClaudeCommands();
                int defaultIndex = 0;
                foreach (var cmd in commands)
                {
                    int idx = claudeCombo.Items.Add(cmd.Command);
                    if (cmd.IsDefault)
                    {
                        defaultIndex = idx;
                    }
                }
                claudeCombo.SelectedIndex = defaultIndex;

                var btnReplace = new Button
                {
                    Text = "Replace Current Terminal",
                    Location = new Point(20, 95),
                    Size = new Size(180, 34),
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225),
                    ForeColor = isDark ? Color.White : Color.Black,
                    FlatStyle = FlatStyle.Flat
                };
                btnReplace.FlatAppearance.BorderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);

                var btnCreate = new Button
                {
                    Text = "Create New Terminal",
                    Location = new Point(210, 95),
                    Size = new Size(170, 34),
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225),
                    ForeColor = isDark ? Color.White : Color.Black,
                    FlatStyle = FlatStyle.Flat
                };
                btnCreate.FlatAppearance.BorderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    Location = new Point((formWidth - 100) / 2, 145),
                    Size = new Size(100, 34),
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225),
                    ForeColor = isDark ? Color.White : Color.Black,
                    FlatStyle = FlatStyle.Flat,
                    DialogResult = DialogResult.Cancel
                };
                btnCancel.FlatAppearance.BorderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);

                // Handle button clicks to capture the selected command
                btnReplace.Click += (s, ev) =>
                {
                    dialogResult.Action = ProjectTerminalAction.ReplaceCurrentTerminal;
                    dialogResult.ClaudeCommand = claudeCombo.SelectedIndex > 0 ? claudeCombo.SelectedItem.ToString() : null;
                    form.DialogResult = DialogResult.OK;
                };

                btnCreate.Click += (s, ev) =>
                {
                    dialogResult.Action = ProjectTerminalAction.CreateNewTerminal;
                    dialogResult.ClaudeCommand = claudeCombo.SelectedIndex > 0 ? claudeCombo.SelectedItem.ToString() : null;
                    form.DialogResult = DialogResult.OK;
                };

                form.ClientSize = new Size(formWidth, 195);
                form.Controls.AddRange(new Control[] { label, claudeLabel, claudeCombo, btnReplace, btnCreate, btnCancel });
                form.CancelButton = btnCancel;

                form.ShowDialog(this);

                return dialogResult;
            }
        }

        private TerminalDocument FindEmptyTerminal()
        {
            // Look for a terminal that hasn't had any commands typed
            // For simplicity, we'll just return null and always create a new one
            // In a more sophisticated implementation, you could track terminal state
            return null;
        }

        private TerminalDocument AddNewTerminalForProject()
        {
            var doc = new TerminalDocument();
            doc.Terminal.SetDebugLogService(_debugLogService);
            doc.SetDebugLogService(_debugLogService); // Enable status bar logging
            doc.SetTheme(_currentTheme);
            doc.SetMessageBroker(_mcpServer?.Broker); // Enable status bar updates
            doc.TerminalExited += OnTerminalExited;
            doc.Terminal.FontSizeChanged += OnTerminalFontSizeChanged;
            doc.AgentSplitRatioChanged += OnAgentSplitRatioChanged;
            doc.HudSplitRatioChanged += OnHudSplitRatioChanged;
            doc.StatusBarHeightChanged += OnStatusBarHeightChanged;
            doc.TaskHudZoomChanged += OnTaskHudZoomChanged;
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;
            doc.TaskDropped += OnTaskDroppedOnTerminal;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            // Show as tab
            doc.Show(_dockPanel, DockState.Document);

            // Pre-register with MessageBroker so the terminal is routable immediately
            var terminalName = PreRegisterTerminal(doc.DocId);

            // Start the terminal
            doc.StartTerminal(_settings?.GetLastDirectory(), terminalName);

            // Apply font size and saved split ratios
            float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);
            if (_settings != null)
            {
                doc.ApplyAgentSplitRatio(_settings.GetAgentPanelSplitRatio());
                doc.ApplyHudSplitRatio(_settings.GetHudSplitRatio());
                doc.ApplyTaskHudZoom(_settings.GetTaskHudZoom());
                doc.ApplyStatusBarHeight(_settings.GetStatusBarHeight());
            }

            return doc;
        }

        private void OnOpenProjectManager(object sender, EventArgs e)
        {
            ShowProjectManagerDialog();
        }

        private void ShowProjectManagerDialog()
        {
            using var projectDb = new MultiTerminal.Services.ProjectDatabase();
            using (var dialog = new ProjectManagerDialog(_projectService, projectDb, _currentTheme))
            {
                dialog.ProjectOpened += (s, args) =>
                {
                    // This will be handled after dialog closes
                };

                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedProject != null)
                {
                    // Open the selected project
                    OnProjectSelected(this, new ProjectSelectedEventArgs(dialog.SelectedProject));
                }

            }
        }

        private void OnPromptPasteRequested(object sender, PromptEventArgs e)
        {
            // Use _lastActiveTerminal instead of _dockPanel.ActiveDocument
            // because DockPanelSuite's ActiveDocument is unreliable with WebView2
            var doc = _lastActiveTerminal ?? _dockPanel.ActiveDocument as TerminalDocument;
            if (doc != null && e.Prompt != null)
            {
                doc.Terminal.Write(e.Prompt.Text);
                doc.FocusTerminal();
            }
        }

        private void OnPromptEditRequested(object sender, PromptEventArgs e)
        {
            if (e.Prompt != null)
            {
                ShowSavePromptDialog(existingPrompt: e.Prompt);
            }
        }

        private void OnPromptDeleteRequested(object sender, PromptEventArgs e)
        {
            if (e.Prompt != null)
            {
                var result = MessageBox.Show(
                    $"Delete prompt \"{e.Prompt.Description}\"?",
                    "Confirm Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    string workingDir = _settings?.GetLastDirectory();
                    _promptService.DeletePrompt(e.Prompt.Id, workingDir);
                    RefreshProjectPanel();
                }
            }
        }

        private void OnNewPromptRequested(object sender, EventArgs e)
        {
            ShowSavePromptDialog();
        }

        private void OnNewPromptInCategoryRequested(object sender, NewPromptInCategoryEventArgs e)
        {
            ShowSavePromptDialog(defaultCategory: e.Category);
        }

        private void OnViewSessionRequested(object sender, string sessionId)
        {
            // Get current project path from current project or last active terminal (prefer SourcePath)
            string projectPath = (!string.IsNullOrEmpty(_currentProject?.SourcePath) ? _currentProject.SourcePath : _currentProject?.Path)
                ?? _lastActiveTerminal?.GetWorkingDirectory()
                ?? _settings?.GetLastDirectory();

            if (string.IsNullOrEmpty(projectPath))
            {
                MessageBox.Show(
                    "Unable to determine project path for session viewing.",
                    "View Session",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Try to find HTML export from Claude's storage
            var claudePath = SessionLineageService.GetClaudeProjectFolder(projectPath);

            if (!string.IsNullOrEmpty(claudePath))
            {
                // Look for session-{sessionId}.html
                string htmlPath = Path.Combine(claudePath, $"session-{sessionId}.html");
                if (File.Exists(htmlPath))
                {
                    // Open in default browser
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = htmlPath,
                        UseShellExecute = true
                    });
                    return;
                }

                // Fallback: Look for matching .jsonl file and open in default application
                string jsonlPath = Path.Combine(claudePath, $"{sessionId}.jsonl");
                if (File.Exists(jsonlPath))
                {
                    // Open JSONL file in default application (e.g., text editor)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = jsonlPath,
                        UseShellExecute = true
                    });
                    return;
                }
            }

            // Session file not found
            MessageBox.Show(
                $"Could not find session file for session '{sessionId}'.\n\n" +
                $"Looked in: {claudePath ?? "(Claude path not found)"}",
                "View Session",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void OnSaveAsPromptRequested(object sender, SavePromptRequestEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.SelectedText))
            {
                ShowSavePromptDialog(promptText: e.SelectedText);
            }
        }

        private void ShowSavePromptDialog(string promptText = null, Prompt existingPrompt = null, string defaultCategory = null)
        {
            string workingDir = _lastActiveTerminal?.GetWorkingDirectory() ?? _settings?.GetLastDirectory();
            string projectName = _currentProject?.Name
                ?? (string.IsNullOrEmpty(workingDir) ? "Current Directory" : Path.GetFileName(workingDir));

            var categories = _promptService.GetCategories(workingDir);

            using (var dialog = new SavePromptDialog(categories, projectName, _currentTheme))
            {
                if (existingPrompt != null)
                {
                    // Edit mode
                    dialog.IsEditMode = true;
                    dialog.SelectedCategory = existingPrompt.Category;
                    dialog.Description = existingPrompt.Description;
                    dialog.PromptText = existingPrompt.Text;
                    dialog.SetIsGlobal(existingPrompt.IsGlobal);
                    dialog.AllowPromptEdit = true;
                }
                else if (!string.IsNullOrEmpty(promptText))
                {
                    // Save selected text
                    dialog.PromptText = promptText;
                    dialog.AllowPromptEdit = false;
                    if (!string.IsNullOrEmpty(defaultCategory))
                    {
                        dialog.SelectedCategory = defaultCategory;
                    }
                }
                else
                {
                    // New prompt from scratch
                    dialog.AllowPromptEdit = true;
                    if (!string.IsNullOrEmpty(defaultCategory))
                    {
                        dialog.SelectedCategory = defaultCategory;
                    }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var prompt = existingPrompt ?? new Prompt();
                    prompt.Category = dialog.Category;
                    prompt.Description = dialog.Description;
                    prompt.Text = dialog.PromptText;
                    prompt.IsGlobal = dialog.IsGlobal;

                    _promptService.SavePrompt(prompt, workingDir);
                    RefreshProjectPanel();
                }
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _messageQueueTimer?.Stop();
                _messageQueueTimer?.Dispose();
                _teamWatcher?.Dispose();
                _inboxMonitor?.Dispose();
                _sessionIndexingService?.Dispose();
                _chatTaskDatabase?.Dispose();
                _sharedProjectDatabase?.Dispose();
                _gatewayService?.Dispose();
                _debugLogService?.Dispose();
                _mcpServer?.Dispose();
                _mcpServer = null;
                _dockPanel?.Dispose();
                _dashboardHeader?.Dispose();
                _toolStrip?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
