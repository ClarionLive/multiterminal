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
        private ToolStrip _toolStrip;
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
        private SessionSyncService _sessionSyncService;
        private SessionDatabase _sessionDatabase;
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
        private readonly Dictionary<string, AgentProcess> _agentProcessMap = new();
        private readonly Dictionary<string, AgentPanelDocument> _agentPanelMap = new();
        private readonly Dictionary<string, WeifenLuo.WinFormsUI.Docking.DockPane> _spawnerAgentPanes = new();
        private readonly Dictionary<string, (AgentPanelControl Control, Panel Slot, TerminalDocument Terminal)> _embeddedAgentMap = new();
        private TeamWatcherService _teamWatcher;
        private InboxMonitorService _inboxMonitor;

        // Shared project database for start screen — created once, disposed with form
        private Services.ProjectDatabase _sharedProjectDatabase;

        // MCP config service — generates .mcp.json for projects from the registry
        private Services.McpConfigService _mcpConfigService;

        // Anti-reentrance: throttle nudges per terminal (5-second cooldown)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastNudgeTime = new();
        private static readonly TimeSpan NudgeThrottle = TimeSpan.FromSeconds(5);

        // Message injection queue to prevent focus race conditions
        private readonly Queue<(string messageId, string recipientId, string sender, string message, TaskCompletionSource<bool> completion)> _messageQueue = new();
        private bool _injectionInProgress = false;
        private readonly object _injectionLock = new object();
        private readonly object _terminalDocMapLock = new object();

        // Message deduplication cache - tracks recently delivered message IDs to prevent duplicates
        private readonly Dictionary<string, DateTime> _deliveredMessageCache = new Dictionary<string, DateTime>();
        private readonly object _deduplicationLock = new object();
        private const int DeduplicationCacheMinutes = 5; // Keep delivered message IDs for 5 minutes

        // For session restore with XML layout
        private int _terminalRestoreIndex = 0;
        private List<TerminalSessionInfo> _pendingTerminalSessions;

        // Background timer for periodic session sync and indexing
        private System.Windows.Forms.Timer _sessionSyncTimer;

        // Timer for polling pending message queue (retry delivery)
        private System.Windows.Forms.Timer _messageQueueTimer;
        private int _messageQueueCleanupCounter = 0;
        private const int CleanupIntervalTicks = 1800; // Every 1 hour at 2-second intervals (3600/2)

        /// <summary>
        /// Event fired when all terminals have finished loading.
        /// </summary>
        public event EventHandler LoadingComplete;

        public MainForm()
        {
            InitializeComponent();
            InitializeDockPanel();
            InitializeToolbar();
            LoadSettings();
            ApplyTheme(isInitialLoad: true);

            System.Diagnostics.Trace.WriteLine("[MainForm] Calling InitializeMcpServerAndChatPanel...");
            // Initialize MCP server BEFORE RestoreSession so panels can be properly initialized
            // when restored from layout (panels need broker for WebView2 initialization)
            InitializeMcpServerAndChatPanel();
            System.Diagnostics.Trace.WriteLine("[MainForm] InitializeMcpServerAndChatPanel completed");

            // Defer RestoreSession to after the form is shown to avoid blocking the constructor
            // This prevents WebView2 initialization from blocking the UI thread during startup
            System.Diagnostics.Trace.WriteLine("[MainForm] Deferring RestoreSession until form is shown...");
            System.Diagnostics.Trace.WriteLine("[MainForm] Constructor completed, exiting...");
            this.Shown += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[MainForm] ===== SHOWN EVENT FIRED =====");
                RestoreSession();
            };
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            Text = "MultiTerminal";
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
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating SessionSyncService...");
            _sessionSyncService = new SessionSyncService();
            System.Diagnostics.Trace.WriteLine("[MainForm] Creating SessionDatabase...");
            _sessionDatabase = new SessionDatabase(); // Uses centralized path in APPDATA
            System.Diagnostics.Trace.WriteLine("[MainForm] SessionDatabase created successfully");

            // Shared project database for the start screen (one connection, shared across all terminals)
            _sharedProjectDatabase = new Services.ProjectDatabase();

            // Initialize project panel (combines project info and prompts)
            _projectPanel = new ProjectPanelDocument();
            _projectPanel.SetServices(_projectService, _promptService);
            _projectPanel.SetSessionDatabase(_sessionDatabase); // Inject shared database to prevent duplicate creation
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
            _projectPanel.McpRegistrySaveRequested += OnMcpRegistrySaveRequested;
            _projectPanel.McpRegistryDeleteRequested += OnMcpRegistryDeleteRequested;
            _projectPanel.ImportMcpJsonRequested += OnImportMcpJsonRequested;
            _projectPanel.RegenerateAllMcpConfigsRequested += OnRegenerateAllMcpConfigsRequested;

            // Create debug log service (available immediately)
            _debugLogService = new DebugLogService();

            // Initialize McpConfigService — used by OnMcpJsonWriteRequested and OnImportMcpJsonRequested
            // Pass debug log callback so CLI errors are visible in the debug panel
            _mcpConfigService = new Services.McpConfigService(_sharedProjectDatabase,
                (source, msg) => _debugLogService?.Info(source, msg));

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

                // Persist chat messages to database
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 14: Wiring MessageSent event");
                _mcpServer.Broker.MessageSent += OnChatMessageSent;

                // Push notification support - map terminal IDs to documents
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 15: Wiring TerminalRegistered event");
                _mcpServer.Broker.TerminalRegistered += OnMcpTerminalRegistered;
                System.Diagnostics.Trace.WriteLine("[InitializeMcpServerAndChatPanel] Step 16: Setting OnMessageDelivery");
                _mcpServer.Broker.OnMessageDelivery = OnMcpMessageDelivery;

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

                // Start MCP server in background
                Task.Run(async () =>
                {
                    try
                    {
                        await _mcpServer.StartAsync();
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
                    var dbPath = Services.SessionDatabase.GetCentralizedDatabasePath();
                    Log($"DB path: {dbPath}");
                    Log($"DB exists: {System.IO.File.Exists(dbPath)}");

                    // Use default constructor - uses centralized database path
                    // NOT the project constructor which treats arg as project folder!
                    using (var db = new Services.SessionDatabase())
                    {
                        Log("SessionDatabase opened, calling SaveChatMessage...");
                        db.SaveChatMessage(
                            message.Id,
                            message.From,
                            message.To,
                            message.Content,
                            message.Timestamp,
                            message.IsBroadcast
                        );
                        Log($"SUCCESS - saved message {message.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    Log($"Stack: {ex.StackTrace}");
                }
            });
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

            // Last resort fallback
            targetDoc ??= _lastActiveTerminal;

            if (targetDoc != null)
            {
                lock (_terminalDocMapLock)
                {
                    _terminalDocMap[e.Id] = targetDoc;
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
                    // Dock-based panels: update status and optionally auto-close.
                    if (_agentPanelMap.TryGetValue(agentTerminalId, out var panel) && !panel.IsDisposed)
                    {
                        try
                        {
                            if (InvokeRequired)
                                BeginInvoke(new Action(() => panel.Text = $"Agent: {agentName} (done)"));
                            else
                                panel.Text = $"Agent: {agentName} (done)";
                        }
                        catch (ObjectDisposedException) { }

                        // Auto-close if setting is enabled
                        string closeMode = _settings?.GetAgentPanelCloseMode() ?? "ManualClose";
                        if (closeMode == "AutoClose")
                        {
                            try
                            {
                                if (InvokeRequired)
                                    BeginInvoke(new Action(() => { if (!panel.IsDisposed) panel.Close(); }));
                                else
                                    panel.Close();
                            }
                            catch (ObjectDisposedException) { }
                        }
                    }

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

            // File-based delivery for ALL terminals — write to inbox file
            // instead of unreliable terminal paste injection. Hook scripts
            // (inbox-check-hook.js) read these files and inject messages
            // into Claude's context via additionalContext.
            // Resolve terminal name from ID — hook uses MULTITERMINAL_NAME (the name, not ID)
            var recipientTerminal = _mcpServer.Broker.GetTerminal(recipientId);
            string recipientName = recipientTerminal?.Name ?? recipientId;
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
            _toolStrip.Items.Add(_agentPanelButton);
            _toolStrip.Items.Add(_debugPanelButton);
            _toolStrip.Items.Add(_recentFoldersDropdown);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_gridDropdown);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_themeButton);
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

        private void ShowSettingsDialog()
        {
            var dialog = new SettingsWpfDialog(_settings, _currentTheme.IsDark);
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
            using (var db = new Services.SessionDatabase())
            using (var dialog = new Dialogs.ChatHistoryDialog(db, _currentTheme))
            {
                dialog.ShowDialog(this);
            }
        }

        private void ApplySettingsFromDialog(SettingsWpfDialog dialog)
        {
            // Apply toolbar font
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

            // Apply toolbar font size
            float toolbarFontSize = _settings.GetToolbarFontSize();
            _toolStrip.Font = new Font(_toolStrip.Font.FontFamily, toolbarFontSize);

            // Restore project panel visibility and font size
            bool showProjectPanel = _settings.Get("ShowPromptsPanel") == "true"; // Keep same setting key for compatibility
            float projectFontSize = _settings.GetPromptsFontSize();
            _projectPanel?.SetFontSize(projectFontSize);

            // Apply font sizes to other panels
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

            // DISABLED: Project panel auto-show causes DockPanel to deserialize and create duplicate services via reflection
            // TODO: Fix DockPanel deserialization to not create new service instances
            /*
            if (showProjectPanel)
            {
                this.Shown += (s, e) =>
                {
                    System.Diagnostics.Trace.WriteLine("[MainForm] Shown event: Showing project panel...");
                    _projectPanel.Show(_dockPanel, DockState.DockLeft);
                    System.Diagnostics.Trace.WriteLine("[MainForm] Shown event: Calling RefreshProjectPanel...");
                    RefreshProjectPanel();
                    System.Diagnostics.Trace.WriteLine("[MainForm] Shown event: RefreshProjectPanel completed");
                };
            }
            */
            System.Diagnostics.Trace.WriteLine($"[MainForm] LoadSettings: showProjectPanel={showProjectPanel} (auto-show disabled)");
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
                _toolStrip.BackColor = Color.FromArgb(45, 45, 48);
                _toolStrip.Renderer = new DarkToolStripRenderer();
                _themeButton.Text = "\u2600"; // Sun - click to switch to light
                _themeButton.ToolTipText = "Switch to Light Theme";

                // Only set DockPanel theme on initial load (before any documents are open)
                if (isInitialLoad)
                {
                    _dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015DarkTheme();
                }
            }
            else
            {
                BackColor = Color.FromArgb(240, 240, 240);
                _toolStrip.BackColor = Color.FromArgb(230, 236, 242);
                _toolStrip.Renderer = new LightToolStripRenderer();
                _themeButton.Text = "\u263E"; // Moon - click to switch to dark
                _themeButton.ToolTipText = "Switch to Dark Theme";

                // Only set DockPanel theme on initial load (before any documents are open)
                if (isInitialLoad)
                {
                    _dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015LightTheme();
                }
            }

            // Update toolbar item colors
            Color textColor = _currentTheme.IsDark ? Color.White : Color.FromArgb(30, 30, 30);
            foreach (ToolStripItem item in _toolStrip.Items)
            {
                item.ForeColor = textColor;
            }

            // Update dropdown menu item colors
            if (_gridDropdown != null)
            {
                foreach (ToolStripItem item in _gridDropdown.DropDownItems)
                {
                    item.ForeColor = textColor;
                }
            }

            // Update settings and about button colors
            if (_settingsButton != null)
            {
                _settingsButton.ForeColor = textColor;
            }
            if (_aboutButton != null)
            {
                _aboutButton.ForeColor = textColor;
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

            // Update all agent panel themes (snapshot to avoid collection-modified crash)
            foreach (var panel in _agentPanelMap.Values.ToList())
            {
                try
                {
                    if (!panel.IsDisposed)
                        panel.ApplyTheme(_currentTheme.IsDark);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Agent panel theme error: {ex.Message}");
                }
            }

            // Update embedded agent control themes
            foreach (var info in _embeddedAgentMap.Values.ToList())
            {
                try { info.Control?.ApplyTheme(_currentTheme.IsDark); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainForm] Embedded agent theme error: {ex.Message}"); }
            }

            // Update all open lifecycle board windows
            TaskLifecycleBoardForm.ApplyThemeToAll(_currentTheme.IsDark);

            // Update project toggle button color
            if (_projectPanelButton != null)
            {
                _projectPanelButton.ForeColor = textColor;
            }
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

        public void AddNewTerminal(string workingDirectory = null, float? fontSize = null, bool forceTabMode = false, string identityName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false)
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
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;

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

                    terminalName = PreRegisterTerminalWithName(doc.DocId, identityName);
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
                System.Diagnostics.Trace.WriteLine($"[AddNewTerminal] Calling doc.StartTerminal('{dir}', '{terminalName}', '{autoRunCommand ?? "null"}', '{spawnerName ?? "null"}', '{projectId ?? "null"}', isTeamLead={isTeamLead})...");
                doc.StartTerminal(dir, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead);
                System.Diagnostics.Trace.WriteLine("[AddNewTerminal] doc.StartTerminal returned");
            }
            else
            {
                // No launch params: show start screen so user can pick a project
                System.Diagnostics.Trace.WriteLine("[AddNewTerminal] No launch params — showing start screen");
                doc.ShowStartScreen();
            }

            // Apply specified or default font size
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);

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

                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId);
                }

                // Write project .mcp.json synchronously (fast file I/O, needed before launch)
                try { _mcpConfigService?.WriteMcpJsonToProject(project.Id, launchDir); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] MCP config write failed: {ex.Message}"); }

                // Sync global MCP servers in background (slow subprocess calls, not needed before launch)
                if (_mcpConfigService != null)
                {
                    var mcp = _mcpConfigService;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { mcp.SyncGlobalMcpServers(); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] Background MCP sync failed: {ex.Message}"); }
                    });
                }

                System.Diagnostics.Trace.WriteLine($"[StartScreen] Launching project '{project.Name}' in {launchDir}");
                doc.StartTerminal(launchDir, terminalName, autoRunCommand, projectId: project.Id, isTeamLead: isTeamLead);

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
                    _mcpServer.Broker.RegisterTerminal(terminalName, sourceDoc.DocId);
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

                // Write project .mcp.json synchronously (fast file I/O, needed before launch)
                try { _mcpConfigService?.WriteMcpJsonToProject(project.Id, launchDir); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] MCP config write failed: {ex.Message}"); }

                // Sync global MCP servers in background (slow subprocess calls, not needed before launch)
                if (_mcpConfigService != null)
                {
                    var mcp = _mcpConfigService;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { mcp.SyncGlobalMcpServers(); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StartScreen] Background MCP sync failed: {ex.Message}"); }
                    });
                }

                // Start terminal in project folder
                System.Diagnostics.Trace.WriteLine($"[StartScreen] Launching new project '{project.Name}' in {launchDir}");
                sourceDoc.StartTerminal(launchDir, terminalName, autoRunCommand,
                    projectId: project.Id, isTeamLead: isTeamLead);

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
        private string PreRegisterTerminalWithName(string docId, string identityName)
        {
            try
            {
                // Register with the specific identity name
                var result = _mcpServer.Broker.RegisterTerminal(identityName, docId);
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
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;

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

            // Apply font size
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);

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
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;

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

            // Apply font size
            float terminalFontSize = fontSize ?? _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);

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

                        // Project panel refresh is handled by:
                        // - OnProjectFileChanged (FileSystemWatcher for project.json changes)
                        // - OnTerminalDirectoryChanged (when terminal changes directories)
                        // No need to refresh on every click - it causes layout thrashing
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
                    }
                }
                else
                {
                    // Not a project directory - clear project and show path-based prompts
                    _currentProject = null;
                    RefreshProjectPanel();
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

                // Wire up "Launch as..." context menu support
                doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
                doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
                WireStartScreenEvents(doc);

                doc.Show(_dockPanel, DockState.Document);
                doc.StartTerminal(_settings?.GetLastDirectory());
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
            if (_dockPanel.ActiveDocument is TerminalDocument doc)
            {
                // Focus the terminal when switching
                doc.FocusTerminal();
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
            string autoRunCommand = "claude --dangerously-skip-permissions";

            // Stop current terminal and restart with new identity
            doc.Terminal.Stop();
            doc.CustomTitle = terminalName;
            doc.StartTerminal(workingDirectory, terminalName, autoRunCommand);

            // Auto-initialization is handled by OnClaudeCodeDetected when Claude Code's output is detected.
            // This ensures injection happens AFTER Claude Code is ready, not just after WebView2 loads.
            // The _claudeCodeDetectedThisSession flag is reset in TerminalControl.DoStart() so the event fires for restarted terminals.
            System.Diagnostics.Trace.WriteLine($"[MainForm.OnLaunchAsIdentityRequested] Terminal restarted for {terminalName}, waiting for Claude Code detection to auto-inject");
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // Batch all settings changes to avoid multiple disk writes
            _settings.BeginBatch();

            // Save window bounds (use RestoreBounds if maximized to get normal size)
            if (WindowState == FormWindowState.Normal)
                _settings.SetWindowBounds(new Rectangle(Left, Top, Width, Height));
            else
                _settings.SetWindowBounds(RestoreBounds);

            _settings.SetWindowState(WindowState);

            // Save dock panel layout
            try
            {
                _dockPanel.SaveAsXml(_settings.GetLayoutFilePath());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to save layout: {ex.Message}");
            }

            // Save project panel state
            _settings.Set("ShowPromptsPanel", (_projectPanel?.Visible ?? false) ? "true" : "false");

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

            // Stop MCP server - fire and forget to avoid blocking UI thread
            // The server will stop asynchronously; if it takes too long, the process exit will clean up
            if (_mcpServer != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _mcpServer.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to stop MCP server: {ex.Message}");
                    }
                });
            }

            // Close all terminals
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                doc.Terminal?.Stop();
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
                CustomTitle = t.CustomTitle,
                AgentPanelZoom = t.GetAgentPanelZoom(),
                TaskHudZoom = t.GetTaskHudZoom()
            }).ToList();

            _settings.SetSessionTerminals(sessionData);

            // Save layout preset based on terminal count
            var preset = GridLayoutManager.GetRecommendedPreset(terminals.Count);
            _settings.SetSessionLayout(preset.ToString());
        }

        private void RestoreSession()
        {
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Starting...");
            string layoutPath = _settings.GetLayoutFilePath();
            System.Diagnostics.Trace.WriteLine($"[RestoreSession] Layout path: {layoutPath}");

            _pendingTerminalSessions = _settings.GetSessionTerminals();
            _terminalRestoreIndex = 0;
            System.Diagnostics.Trace.WriteLine($"[RestoreSession] Pending terminal sessions: {_pendingTerminalSessions?.Count ?? 0}");

            bool layoutRestored = false;

            // Try to restore from XML layout if it exists and we have terminal sessions
            bool layoutExists = File.Exists(layoutPath);
            System.Diagnostics.Trace.WriteLine($"[RestoreSession] Layout file exists: {layoutExists}");

            if (layoutExists && _pendingTerminalSessions?.Count > 0)
            {
                System.Diagnostics.Trace.WriteLine("[RestoreSession] Attempting to load layout from XML...");
                try
                {
                    _dockPanel.LoadFromXml(layoutPath, GetContentFromPersistString);
                    layoutRestored = true;
                    System.Diagnostics.Trace.WriteLine("[RestoreSession] Layout loaded successfully");

                    // All restored terminals show start screen so the user can pick a project.
                    // Previously we auto-started PowerShell for terminals with saved directories,
                    // but with the start screen feature, users should always choose what to launch.
                    var terminals = _gridManager.GetTerminalDocuments();
                    foreach (var doc in terminals.Where(d => !d.IsTerminalStarted))
                    {
                        doc.ShowStartScreen();
                    }

                    // Focus the first terminal that is running; fall back to first start screen
                    var firstStarted = terminals.FirstOrDefault(d => d.IsTerminalStarted);
                    var firstDoc = firstStarted ?? terminals.FirstOrDefault();
                    if (firstDoc != null)
                    {
                        firstDoc.Activate();
                        if (firstDoc.IsTerminalStarted)
                            firstDoc.FocusTerminal();
                        _lastActiveTerminal = firstDoc;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to restore layout: {ex.Message}");
                    layoutRestored = false;
                }
            }

            if (!layoutRestored)
            {
                System.Diagnostics.Trace.WriteLine("[RestoreSession] Layout not restored, creating default terminal...");
                // Fall back to creating a single terminal
                AddNewTerminal();
                System.Diagnostics.Trace.WriteLine("[RestoreSession] Default terminal created");
            }

            System.Diagnostics.Trace.WriteLine("[RestoreSession] Completed");

            _pendingTerminalSessions = null;

            // Refresh project panel now that terminals have their directories
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Calling RefreshProjectPanel...");
            RefreshProjectPanel();
            System.Diagnostics.Trace.WriteLine("[RestoreSession] RefreshProjectPanel returned");

            // Signal loading complete (splash will show main form when animation finishes)
            System.Diagnostics.Trace.WriteLine("[RestoreSession] Invoking LoadingComplete event...");
            LoadingComplete?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Trace.WriteLine("[RestoreSession] LoadingComplete event invoked");

            // Sync and index all sessions in background for search functionality
            _ = Task.Run(async () =>
            {
                try
                {
                    // Get the current project path
                    string projectPath = _lastActiveTerminal?.GetWorkingDirectory()
                        ?? _settings?.GetLastDirectory()
                        ?? Directory.GetCurrentDirectory();

                    // First sync sessions from Claude's folder
                    if (_sessionSyncService != null && _sessionDatabase != null)
                    {
                        int syncedCount = _sessionSyncService.SyncProject(projectPath, _sessionDatabase);
                        System.Diagnostics.Debug.WriteLine($"[MainForm] Initial sync: {syncedCount} sessions from Claude storage");
                    }

                    // Then index for vector search
                    await _sessionIndexingService.IndexAllSessionsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Initial session sync/index error: {ex.Message}");
                }
            });

            // Start periodic session sync timer (every 5 minutes)
            _sessionSyncTimer = new System.Windows.Forms.Timer();
            _sessionSyncTimer.Interval = 5 * 60 * 1000; // 5 minutes
            _sessionSyncTimer.Tick += OnSessionSyncTimerTick;
            _sessionSyncTimer.Start();
            System.Diagnostics.Debug.WriteLine("[MainForm] Session sync timer started (5 minute interval)");

            // Start message queue polling timer (every 2 seconds)
            _messageQueueTimer = new System.Windows.Forms.Timer();
            _messageQueueTimer.Interval = 2000; // 2 seconds
            _messageQueueTimer.Tick += OnMessageQueueTimerTick;
            _messageQueueTimer.Start();
            System.Diagnostics.Debug.WriteLine("[MainForm] Message queue timer started (2 second interval)");
        }

        /// <summary>
        /// Periodic session sync handler - imports new sessions and indexes them.
        /// </summary>
        private void OnSessionSyncTimerTick(object sender, EventArgs e)
        {
            // Skip if already indexing
            if (_sessionIndexingService?.IsIndexing == true)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] Session sync skipped - indexing already in progress");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[MainForm] Session sync timer triggered - starting background sync");

            // Get the current project path from active terminal or settings
            string projectPath = _lastActiveTerminal?.GetWorkingDirectory()
                ?? _settings?.GetLastDirectory()
                ?? Directory.GetCurrentDirectory();

            // Run sync and indexing in background
            _ = Task.Run(async () =>
            {
                try
                {
                    // STEP 1: Sync sessions from Claude's folder into the database
                    if (_sessionSyncService != null && _sessionDatabase != null)
                    {
                        int syncedCount = _sessionSyncService.SyncProject(projectPath, _sessionDatabase);
                        System.Diagnostics.Debug.WriteLine($"[MainForm] Synced {syncedCount} sessions from Claude storage");
                    }

                    // STEP 2: Index sessions (generate vector embeddings)
                    await _sessionIndexingService.IndexProjectSessionsAsync(projectPath);

                    // STEP 3: Index chat messages
                    await _sessionIndexingService.IndexChatMessagesAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Session sync error: {ex.Message}");
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
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            if (sessionInfo != null)
            {
                doc.PendingWorkingDirectory = sessionInfo.WorkingDirectory;
                doc.SetFontSize(sessionInfo.FontSize);
                doc.SetTaskHudZoom(sessionInfo.TaskHudZoom);
                doc.SetAgentPanelZoom(sessionInfo.AgentPanelZoom);
                if (!string.IsNullOrEmpty(sessionInfo.CustomTitle))
                {
                    doc.CustomTitle = sessionInfo.CustomTitle;
                }
            }
            else
            {
                doc.SetFontSize(_settings?.GetTerminalFontSize() ?? 10f);
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
                _projectPanel.Show(_dockPanel, DockState.DockLeft);
                RefreshProjectPanel();
            }
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
                _chatPanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_chatPanel.Visible)
            {
                _chatPanel.Hide();
            }
            else
            {
                _chatPanel.Show(_dockPanel, DockState.DockRight);
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
                _activityPanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_activityPanel.Visible)
            {
                _activityPanel.Hide();
            }
            else
            {
                _activityPanel.Show(_dockPanel, DockState.DockRight);
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
                _officePanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_officePanel.Visible)
            {
                _officePanel.Hide();
            }
            else
            {
                _officePanel.Show(_dockPanel, DockState.DockRight);
                _officePanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
            }
        }

        private void ToggleAgentPanel()
        {
            // Clean up disposed panels
            var disposed = new List<string>();
            foreach (var kvp in _agentPanelMap)
            {
                if (kvp.Value.IsDisposed)
                    disposed.Add(kvp.Key);
            }
            foreach (var key in disposed)
                _agentPanelMap.Remove(key);

            // If there are active agent panels, toggle their visibility
            if (_agentPanelMap.Count > 0)
            {
                // Check if any are visible
                bool anyVisible = false;
                foreach (var panel in _agentPanelMap.Values)
                {
                    if (panel.Visible)
                    {
                        anyVisible = true;
                        break;
                    }
                }

                foreach (var panel in _agentPanelMap.Values)
                {
                    if (anyVisible)
                    {
                        panel.Hide();
                    }
                    else
                    {
                        panel.Show(_dockPanel, DockState.DockRight);
                        panel.ApplyTheme(_currentTheme == TerminalTheme.Dark);
                    }
                }
            }
        }

        /// <summary>
        /// Create a new AgentPanelDocument for a specific agent and dock it.
        /// </summary>
        private AgentPanelDocument CreateAgentPanel(AgentProcess agent, string agentName, string agentTerminalId, string spawnerName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            return CreateAgentPanel((IAgentMessageSource)agent, agentName, agentTerminalId, spawnerName, taskDescription, subagentType, isTeamAgent);
        }

        /// <summary>
        /// Create a new AgentPanelDocument for any IAgentMessageSource and dock it.
        /// Used by both AgentProcess (piped I/O) and TranscriptTailer (native team watching).
        /// Prefers embedding in the spawner terminal's EmbeddedAgentPanel when available.
        /// Falls back to DockPane-based docking when spawner not found (e.g., team agents).
        /// </summary>
        private AgentPanelDocument CreateAgentPanel(IAgentMessageSource source, string agentName, string panelKey, string spawnerName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            string layout = _settings?.GetAgentPanelLayout() ?? "SplitRight";

            // DoNotShow - skip panel creation entirely
            if (layout == "DoNotShow")
                return null;

            // Try to embed in spawner terminal's EmbeddedAgentPanel
            if (!string.IsNullOrEmpty(spawnerName))
            {
                TerminalDocument spawnerTerminal = null;
                _debugLogService?.Info("CreateAgentPanel", $"Looking for spawner '{spawnerName}' in _terminalDocMap ({_terminalDocMap.Count} entries)");
                lock (_terminalDocMapLock)
                {
                    foreach (var kvp in _terminalDocMap)
                    {
                        if (kvp.Value.Text?.Contains(spawnerName) == true || kvp.Value.TabText?.Contains(spawnerName) == true)
                        {
                            spawnerTerminal = kvp.Value;
                            _debugLogService?.Info("CreateAgentPanel", $"  MATCH FOUND: spawner='{kvp.Key}'");
                            break;
                        }
                    }
                }

                if (spawnerTerminal?.EmbeddedAgentPanel != null)
                {
                    _debugLogService?.Info("CreateAgentPanel", $"Embedding agent '{agentName}' in spawner '{spawnerName}' EmbeddedAgentPanel");

                    var slot = spawnerTerminal.EmbeddedAgentPanel.AddAgentSlot(agentName);
                    var control = new AgentPanelControl { Dock = DockStyle.Fill };
                    slot.Controls.Add(control);
                    control.AttachAgent(source, agentName, taskDescription, subagentType, isTeamAgent);
                    control.ApplyTheme(_currentTheme == TerminalTheme.Dark);

                    // Apply stored zoom from the spawner terminal and wire zoom changes back
                    double agentZoom = spawnerTerminal.GetAgentPanelZoom();
                    control.SetZoomFactor(agentZoom);
                    control.ZoomChanged += (s, zoom) => spawnerTerminal.SetAgentPanelZoom(zoom);

                    _embeddedAgentMap[panelKey] = (control, slot, spawnerTerminal);

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

                    // Auto-remove slot when agent completes (non-team agents only).
                    // Team agent panels stay visible so the user can review the conversation
                    // or send follow-up messages, even after the agent goes idle/stops.
                    source.Stopped += (s, exitCode) =>
                    {
                        void HandleEmbeddedStopped()
                        {
                            if (isTeamAgent) return; // Team agent panels persist until manually closed

                            if (_embeddedAgentMap.Remove(panelKey, out var info))
                            {
                                info.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(info.Slot);
                                info.Control?.Dispose();
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

                    return null; // No DockContent panel created
                }
                else
                {
                    _debugLogService?.Info("CreateAgentPanel", $"No spawner terminal found for '{spawnerName}' - falling back to dock pane");
                }
            }
            else
            {
                _debugLogService?.Info("CreateAgentPanel", "spawnerName is null/empty - using dock pane");
            }

            // Fallback: create a DockContent-based agent panel (for team agents, or when spawner not found)
            var panel = new AgentPanelDocument();
            panel.AttachAgent(source, agentName, taskDescription, subagentType, isTeamAgent);
            panel.ApplyTheme(_currentTheme == TerminalTheme.Dark);

            // Try to dock relative to last active terminal
            var fallbackPane = _lastActiveTerminal?.Pane;
            if (fallbackPane != null && !fallbackPane.IsDisposed)
            {
                var fallbackDocId = "_fallback_";
                switch (layout)
                {
                    case "SplitBelow":
                        if (_spawnerAgentPanes.TryGetValue(fallbackDocId, out var existingFallbackBottom) && !existingFallbackBottom.IsDisposed)
                        {
                            panel.Show(existingFallbackBottom, WeifenLuo.WinFormsUI.Docking.DockAlignment.Right, 0.5);
                        }
                        else
                        {
                            panel.Show(fallbackPane, WeifenLuo.WinFormsUI.Docking.DockAlignment.Bottom, 0.5);
                            _spawnerAgentPanes[fallbackDocId] = panel.Pane;
                        }
                        break;
                    case "TabbedRight":
                        if (_spawnerAgentPanes.TryGetValue(fallbackDocId, out var existingFallbackTabbed) && !existingFallbackTabbed.IsDisposed)
                        {
                            panel.Show(existingFallbackTabbed, null);
                        }
                        else
                        {
                            panel.Show(fallbackPane, WeifenLuo.WinFormsUI.Docking.DockAlignment.Right, 0.5);
                            _spawnerAgentPanes[fallbackDocId] = panel.Pane;
                        }
                        break;
                    default: // SplitRight
                        if (_spawnerAgentPanes.TryGetValue(fallbackDocId, out var existingFallbackRight) && !existingFallbackRight.IsDisposed)
                        {
                            panel.Show(existingFallbackRight, WeifenLuo.WinFormsUI.Docking.DockAlignment.Bottom, 0.5);
                        }
                        else
                        {
                            panel.Show(fallbackPane, WeifenLuo.WinFormsUI.Docking.DockAlignment.Right, 0.5);
                            _spawnerAgentPanes[fallbackDocId] = panel.Pane;
                        }
                        break;
                }
            }
            else
            {
                // Ultimate fallback: respect layout setting for global docking
                panel.Show(_dockPanel, layout == "SplitBelow" ? DockState.DockBottom : DockState.DockRight);
            }

            _agentPanelMap[panelKey] = panel;

            // Manual close via the X button inside the agent panel WebView
            panel.CloseRequested += (s, e) =>
            {
                async void HandleDockCloseRequested()
                {
                    if (panel.IsDisposed) return;

                    // If agent is still running, prompt user before closing
                    if (panel.HasActiveAgent)
                    {
                        var result = MessageBox.Show(
                            this,
                            "An agent is still running in this panel.\n\nTerminate the agent process?",
                            "Close Agent Panel",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Cancel) return;
                        if (result == DialogResult.Yes)
                            await panel.StopAgentAsync();
                    }

                    panel.DetachAgent();
                    panel.HideOnClose = false;
                    panel.Close();
                }

                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action(HandleDockCloseRequested));
                    else
                        HandleDockCloseRequested();
                }
                catch (ObjectDisposedException) { }
            };

            // Clean up panel map and spawner pane cache when panel is closed
            panel.FormClosed += (s, e) =>
            {
                _agentPanelMap.Remove(panelKey);
                _spawnerAgentPanes.Remove("_fallback_");
            };

            // For TranscriptTailer sources: handle tab rename + auto-close on agent completion.
            // (AgentProcess has its own ProcessExited handler for this; TranscriptTailer detects
            // completion via a "result" JSONL entry and fires Stopped.)
            if (source is TranscriptTailer)
            {
                source.Stopped += (s, exitCode) =>
                {
                    void HandleStopped()
                    {
                        if (panel.IsDisposed) return;
                        panel.Text = $"Agent: {agentName} (done)";

                        // Team agent panels never auto-close — they stay visible so the user
                        // can review what the agent did or send follow-up messages.
                        if (isTeamAgent) return;

                        // Non-team subagents (Explore, general-purpose, etc.) always auto-close
                        // since they're fire-and-forget. Otherwise respect user setting.
                        bool isSubagent = panelKey.StartsWith("subagent:");
                        string closeMode = _settings?.GetAgentPanelCloseMode() ?? "ManualClose";
                        if ((isSubagent || closeMode == "AutoClose") && !panel.IsDisposed)
                            panel.Close();
                    }

                    try
                    {
                        if (InvokeRequired)
                            BeginInvoke(new Action(HandleStopped));
                        else
                            HandleStopped();
                    }
                    catch (ObjectDisposedException) { }
                };
            }

            return panel;
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
                    _debugLogService?.Info("MainForm", $"Native teammate discovered: {e.MemberName} in team {e.TeamName}, spawner: {e.SpawnerName ?? "(unknown)"}");

                    string panelKey = $"team:{e.TeamName}:{e.MemberName}";

                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            if (!_agentPanelMap.ContainsKey(panelKey) && !_embeddedAgentMap.ContainsKey(panelKey))
                                CreateAgentPanel(e.Tailer, $"{e.MemberName} ({e.TeamName})", panelKey, e.SpawnerName, e.TaskDescription, e.SubagentType, isTeamAgent: true);
                        }));
                    }
                    else
                    {
                        if (!_agentPanelMap.ContainsKey(panelKey) && !_embeddedAgentMap.ContainsKey(panelKey))
                            CreateAgentPanel(e.Tailer, $"{e.MemberName} ({e.TeamName})", panelKey, e.SpawnerName, e.TaskDescription, e.SubagentType, isTeamAgent: true);
                    }
                };

                _teamWatcher.TeamRemoved += (s, teamName) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] Native team removed: {teamName}");

                    if (InvokeRequired)
                    {
                        Invoke(new Action(() => CleanupTeamPanels(teamName)));
                    }
                    else
                    {
                        CleanupTeamPanels(teamName);
                    }
                };

                _teamWatcher.MemberRemoved += (s, e) =>
                {
                    string panelKey = $"team:{e.TeamName}:{e.MemberName}";
                    _debugLogService?.Info("MainForm", $"Native teammate removed: {e.MemberName} from team {e.TeamName}, closing panel");

                    void CloseAgentPanel()
                    {
                        // Close floating panel
                        if (_agentPanelMap.Remove(panelKey, out var panel) && !panel.IsDisposed)
                        {
                            panel.DetachAgent();
                            panel.HideOnClose = false;
                            panel.Close();
                        }

                        // Close embedded panel
                        if (_embeddedAgentMap.Remove(panelKey, out var embedded))
                        {
                            embedded.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(embedded.Slot);
                            embedded.Control?.Dispose();
                        }
                    }

                    if (InvokeRequired)
                        Invoke(new Action(CloseAgentPanel));
                    else
                        CloseAgentPanel();
                };

                _teamWatcher.SubagentDiscovered += (s, e) =>
                {
                    _debugLogService?.Info("MainForm", $"Non-team subagent discovered: {e.MemberName}, spawner: {e.SpawnerName ?? "(unknown)"}, desc: \"{e.TaskDescription ?? ""}\"");

                    string panelKey = $"subagent:{e.MemberName}";

                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            if (!_agentPanelMap.ContainsKey(panelKey) && !_embeddedAgentMap.ContainsKey(panelKey))
                                CreateAgentPanel(e.Tailer, e.MemberName, panelKey, e.SpawnerName, e.TaskDescription, e.SubagentType);
                        }));
                    }
                    else
                    {
                        if (!_agentPanelMap.ContainsKey(panelKey) && !_embeddedAgentMap.ContainsKey(panelKey))
                            CreateAgentPanel(e.Tailer, e.MemberName, panelKey, e.SpawnerName, e.TaskDescription, e.SubagentType);
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

                targetTerminal.TypeInput("[cm]", "cr", 15);

                _debugLogService?.Info("MainForm", $"Nudge fired on {terminalName}");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Error nudging terminal {terminalName}: {ex.Message}");
            }
        }


        private void CleanupTeamPanels(string teamName)
        {
            string prefix = $"team:{teamName}:";

            // Close and remove floating panels
            var teamKeys = _agentPanelMap.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in teamKeys)
            {
                if (_agentPanelMap.Remove(key, out var panel) && !panel.IsDisposed)
                {
                    panel.DetachAgent();
                    panel.HideOnClose = false;
                    panel.Close();
                }
            }

            // Close and remove embedded panels
            var embeddedKeys = _embeddedAgentMap.Keys.Where(k => k.StartsWith(prefix)).ToList();
            foreach (var key in embeddedKeys)
            {
                if (_embeddedAgentMap.Remove(key, out var info))
                {
                    info.Terminal?.EmbeddedAgentPanel?.RemoveAgentSlot(info.Slot);
                    info.Control?.Dispose();
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
                _tasksPanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_tasksPanel.Visible)
            {
                _tasksPanel.Hide();
            }
            else
            {
                _tasksPanel.Show(_dockPanel, DockState.DockRight);
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
                _inboxPanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_inboxPanel.Visible)
            {
                _inboxPanel.Hide();
            }
            else
            {
                _inboxPanel.Show(_dockPanel, DockState.DockRight);
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
                _profilePanel.Show(_dockPanel, DockState.DockRight);
                return;
            }

            if (_profilePanel.Visible)
            {
                _profilePanel.Hide();
            }
            else
            {
                _profilePanel.Show(_dockPanel, DockState.DockRight);
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
                _debugPanel.Show(_dockPanel, DockState.DockBottom);
                return;
            }

            if (_debugPanel.Visible)
            {
                _debugPanel.Hide();
            }
            else
            {
                _debugPanel.Show(_dockPanel, DockState.DockBottom);
            }
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

        private void OnRegenerateAllMcpConfigsRequested(object sender, EventArgs e)
        {
            _debugLogService?.Info("McpConfig", "Regenerate ALL requested");
            if (_mcpConfigService == null)
            {
                _debugLogService?.Warning("McpConfig", "McpConfigService is null — cannot regenerate");
                _projectPanel?.NotifyMcpRegenResult(false, error: "MCP config service not available");
                return;
            }
            try
            {
                var (globalCount, projectCount) = _mcpConfigService.RegenerateAllMcpConfigs();
                _debugLogService?.Info("McpConfig", $"Regenerated: {globalCount} global (CLI), {projectCount} project(s)");
                _projectPanel?.NotifyMcpRegenResult(true, globalCount, projectCount);
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("McpConfig", $"Regeneration failed: {ex.Message}\n{ex.StackTrace}");
                System.Diagnostics.Debug.WriteLine($"[MainForm] OnRegenerateAllMcpConfigsRequested error: {ex.Message}");
                _projectPanel?.NotifyMcpRegenResult(false, error: ex.Message);
            }
        }

        private void OnMcpRegistrySaveRequested(object sender, string itemJson)
        {
            // ProjectPanelDocument already handled the DB save and sent result back to JS.
            // MainForm hook is here in case we need to refresh other UI (e.g. availableMcpServers cache).
            // For now, nothing additional needed.
        }

        private void OnMcpRegistryDeleteRequested(object sender, string serverName)
        {
            // ProjectPanelDocument already handled the DB delete and sent result back to JS.
        }

        private void OnImportMcpJsonRequested(object sender, string filePath)
        {
            if (_mcpConfigService == null)
            {
                _projectPanel?.NotifyMcpImportResult(false, 0, "MCP config service not available");
                return;
            }

            // If filePath is empty, open a file dialog so the user can pick the file
            string path = filePath;
            if (string.IsNullOrEmpty(path))
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Import MCP Servers from .mcp.json",
                    Filter = "MCP Config|*.mcp.json;*.json|All files|*.*",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;
                path = dlg.FileName;
            }

            try
            {
                int count = _mcpConfigService.ImportFromMcpJsonFile(path);
                _projectPanel?.NotifyMcpImportResult(true, count);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] OnImportMcpJsonRequested error: {ex.Message}");
                _projectPanel?.NotifyMcpImportResult(false, 0, ex.Message);
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
                    AddNewTerminal(sourcePath, projectId: e.Project.Id);
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
            doc.Terminal.TerminalClicked += OnTerminalClicked;
            doc.SaveAsPromptRequested += OnSaveAsPromptRequested;
            doc.DirectoryChanged += OnTerminalDirectoryChanged;
            doc.ProjectFileChanged += OnProjectFileChanged;
            doc.ClaudeCodeDetected += OnClaudeCodeDetected;

            // Wire up "Launch as..." context menu support
            doc.GetAvailableIdentities = () => GetAvailableIdentities().ToArray();
            doc.LaunchAsIdentityRequested += OnLaunchAsIdentityRequested;
            WireStartScreenEvents(doc);

            // Show as tab
            doc.Show(_dockPanel, DockState.Document);

            // Start the terminal
            doc.StartTerminal(_settings?.GetLastDirectory());

            // Apply font size
            float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
            doc.SetFontSize(terminalFontSize);

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
            var syncService = new SessionSyncService();
            var claudePath = syncService.GetClaudeProjectPath(projectPath);

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
                _sessionSyncTimer?.Stop();
                _sessionSyncTimer?.Dispose();
                _teamWatcher?.Dispose();
                _inboxMonitor?.Dispose();
                _sessionIndexingService?.Dispose();
                _sessionDatabase?.Dispose();
                _sharedProjectDatabase?.Dispose();
                _debugLogService?.Dispose();
                _dockPanel?.Dispose();
                _toolStrip?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
