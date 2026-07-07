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
using MultiTerminal.Services.Startup;
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
        // CA2213 pragma rationale for the field block below:
        // - ToolStrip* buttons are added to _toolStrip.Items and disposed transitively via _toolStrip?.Dispose() in Dispose(bool).
        // - *PanelDocument fields are DockContent panels registered with _dockPanel; disposed transitively via _dockPanel?.Dispose() in Dispose(bool).
        // - _lastActiveTerminal is a borrowed reference (points to whichever TerminalDocument is currently active); not owned.
        // - _mcpServer is deliberately not disposed here because OnFormClosing already called StopAsync; a second dispose would deadlock the UI thread.
        // - _projectService, _webhookService have explicit disposal added to Dispose(bool) below; the pragma is a no-op on them.
#pragma warning disable CA2213
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
        // MultiRemote phone-gateway in-process host (Phase 2, task ca6c5344) — second,
        // independent Kestrel on :5100 serving the PWA. Started alongside the REST server.
        private MultiTerminal.API.Gateway.MultiRemoteGatewayHost _remoteGateway;
        private MCPServer.Services.HttpWebhookService _webhookService;
        private ChatPanelDocument _chatPanel;
        private SessionIndexingService _sessionIndexingService;
        private TaskDatabase _chatTaskDatabase; // Used for chat message persistence
        private OwnerProfileService _ownerProfileService;
        private SourceControlAccountService _sourceControlAccountService;
        private readonly Dictionary<string, TerminalDocument> _terminalDocMap = new();
        private ToolStripButton _chatPanelButton;
        private ToolStripButton _chatHistoryButton;
        private ToolStripButton _helpButton;
        private ActivityPanelDocument _activityPanel;
        private ToolStripButton _activityPanelButton;
        private TasksPanelDocument _tasksPanel;
        private ToolStripButton _tasksPanelButton;
        private DebugPanel _debugPanel;
        private ToolStripButton _debugPanelButton;
        private DebugLogService _debugLogService;

        // Phase 4 Track 3 worktree janitor — periodic 5-min sweep with 30-sec
        // startup delay. Disposed in OnFormClosing.
        private System.Threading.Timer _worktreeJanitorTimer;
        // fa1101db R2b idle-auto-on watcher — periodic 60s check. Disposed in OnFormClosing.
        private System.Threading.Timer _idleRemoteModeTimer;
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
        private Services.CodeGraphWatcher _codeGraphWatcher;
        private FilePreviewPanel.FilePreviewPanelDocument _filePreviewPanel;
        private ToolStripButton _filePreviewPanelButton;
#pragma warning restore CA2213

        // Companion process manager — auto-launches external services on startup
        private Services.CompanionProcessManager _companionManager;

        // Shared project database for start screen — created once, disposed with form
        private Services.ProjectDatabase _sharedProjectDatabase;

        // MCP config service — generates .mcp.json for projects from the registry
        private Services.McpConfigService _mcpConfigService;

        // Gateway integration service — syncs MCP servers to the gateway's SQLite DB
        private Services.GatewayIntegrationService _gatewayService;

        // Oracle — always-on advisory agent (no project, no coding)
        private OracleService _oracleService;

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

        // Timer for periodic session sync (imports JSONL files into session_lineage)
        private System.Windows.Forms.Timer _sessionSyncTimer;

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

            _debugLogService?.Trace("MainForm", "Calling InitializeMcpServerAndChatPanel...");
            // Initialize MCP server BEFORE RestoreSession so panels can be properly initialized
            // when restored from layout (panels need broker for WebView2 initialization)
            InitializeMcpServerAndChatPanel();
            _debugLogService?.Trace("MainForm", "InitializeMcpServerAndChatPanel completed");

            // Launch companion processes (ClaudeRemote, Caddy, etc.) in background
            // Uses the shared instance from the REST server so the API controller sees tracked PIDs
            _companionManager = _mcpServer.CompanionProcessManager;
            _ = System.Threading.Tasks.Task.Run(() => _companionManager.StartAll());

            // Defer RestoreSession to after the form is shown to avoid blocking the constructor
            // This prevents WebView2 initialization from blocking the UI thread during startup
            _debugLogService?.Trace("MainForm", "Deferring RestoreSession until form is shown...");
            _debugLogService?.Trace("MainForm", "Constructor completed, exiting...");
            this.Shown += (s, e) =>
            {
                _debugLogService?.Trace("MainForm", "===== SHOWN EVENT FIRED =====");
                ShowOwnerProfileDialogIfNeeded();
                RestoreSession();
                StartWorktreeJanitor();
                StartIdleRemoteModeWatcher();
            };
        }

        /// <summary>
        /// Spin up the Phase 4 Track 3 janitor timer: first fire 30 seconds
        /// after startup, then every 5 minutes. The janitor reconciles
        /// task_worktrees rows against on-disk and git state. Disposed in
        /// OnFormClosing.
        /// </summary>
        private void StartWorktreeJanitor()
        {
            try
            {
                var broker = _mcpServer?.Broker;
                if (broker?.WorktreeJanitor == null) return;
                _worktreeJanitorTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try
                        {
                            broker.WorktreeJanitor.SweepAsync(
                                getProjectPathForTask: id => broker.TryGetProjectPathForTask(id),
                                recordActivity: (action, content, relatedId) =>
                                {
                                    try
                                    {
                                        broker.RecordActivity(new MCPServer.Models.ActivityEvent
                                        {
                                            Terminal = "Janitor",
                                            Type = "worktree",
                                            Action = action,
                                            Content = content,
                                            RelatedId = relatedId,
                                        });
                                    }
                                    catch (Exception ex) { _debugLogService?.Error("Janitor", $"activity log failed: {ex.Message}"); }
                                },
                                tryDeferredPruneRetry: id => broker.TryDeferredPruneRetryAsync(id),
                                tryMergeForTask: (id, root) => broker.TryAutoMergeForTaskAsync(id, root)).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _debugLogService?.Error("Janitor", $"Sweep threw: {ex.Message}");
                        }
                    },
                    state: null,
                    dueTime: TimeSpan.FromSeconds(30),
                    period: TimeSpan.FromMinutes(5));
                _debugLogService?.Trace("MainForm", "Worktree janitor timer started (30s startup, 5m cadence).");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to start worktree janitor: {ex.Message}");
            }
        }

        /// <summary>
        /// fa1101db R2b — idle auto-on watcher. Every 60s (first fire 60s after startup) checks
        /// whether the desk has been quiet long enough (broker setting "idleRemoteOnMinutes",
        /// default 30 min) to flip remote mode ON so phone notifications/permission prompts flow
        /// while the user is away. Auto-ON only; a desktop signal turns it back off instantly.
        /// Disposed in OnFormClosing.
        /// </summary>
        private void StartIdleRemoteModeWatcher()
        {
            try
            {
                var broker = _mcpServer?.Broker;
                if (broker == null) return;
                _idleRemoteModeTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try { broker.CheckIdleRemoteAutoOn(); }
                        catch (Exception ex) { _debugLogService?.Error("IdleRemote", $"check threw: {ex.Message}"); }
                    },
                    state: null,
                    dueTime: TimeSpan.FromSeconds(60),
                    period: TimeSpan.FromSeconds(60));
                _debugLogService?.Trace("MainForm", "Idle remote-mode watcher started (60s cadence, default 30m threshold).");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to start idle remote-mode watcher: {ex.Message}");
            }
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
            _debugLogService?.Trace("MainForm", "Creating PromptService...");
            _promptService = new PromptService();
            _debugLogService?.Trace("MainForm", "Creating ProjectService...");
            _projectService = new ProjectService();
            _projectService.RegistryChangedExternally += OnRegistryChangedExternally;
            _debugLogService?.Trace("MainForm", "Creating SessionIndexingService...");
            _sessionIndexingService = new SessionIndexingService();
            _debugLogService?.Trace("MainForm", "Creating TaskDatabase for chat persistence...");
            _chatTaskDatabase = new TaskDatabase();
            // bb2b0104: these services now open and own their OWN connection to multiterminal.db
            // (one owner per connection) instead of borrowing _chatTaskDatabase's handle. Disposed
            // in Dispose(bool). _chatTaskDatabase remains solely the chat-message persistence instance.
            _ownerProfileService = new OwnerProfileService();
            _sourceControlAccountService = new SourceControlAccountService();
            _debugLogService?.Trace("MainForm", "TaskDatabase created successfully");

            // One-time migration: seed the legacy single GitHub account into the new
            // multi-account store. Idempotent + wrapped in try/catch so it never blocks startup.
            MigrateLegacyGitHubAccount();

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
            _projectPanel.ProjectDeleteRequested += OnProjectPanelDeleteRequested;

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
            _projectPanel.SetDebugLogService(_debugLogService); // route ProjectPanel + its renderer's diagnostics to the unified sink (4c86f18d)

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

        /// <summary>
        /// One-time, idempotent migration of the legacy single GitHub credential
        /// (owner_profile.github_username + the MultiTerminal:GitHubToken secret) into the
        /// new multi-account source_control_accounts store. Runs on every startup but is a
        /// no-op once any account exists. Wrapped in try/catch so a hiccup never blocks startup.
        /// </summary>
        private void MigrateLegacyGitHubAccount()
        {
            try
            {
                // Idempotency: if any account already exists, the migration has run (or the
                // user is already managing accounts) — never touch the legacy fields again.
                if (_sourceControlAccountService.GetAll().Count > 0)
                    return;

                var profile = _ownerProfileService.GetProfile();
                if (profile == null || !profile.HasGitHubToken)
                    return;

                string token = _ownerProfileService.GetGitHubToken();
                if (string.IsNullOrEmpty(token))
                    return; // has_github_token flag set but secret missing — nothing to migrate.

                string displayName = string.IsNullOrWhiteSpace(profile.GitHubUsername)
                    ? "GitHub"
                    : profile.GitHubUsername;

                var account = _sourceControlAccountService.Add(new SourceControlAccount
                {
                    DisplayName = displayName,
                    Provider = "github",
                    // username is NOT NULL; GitHubUsername can be blank even when a token
                    // exists, so reuse the guaranteed-non-empty displayName as the fallback.
                    Username = string.IsNullOrWhiteSpace(profile.GitHubUsername)
                        ? displayName
                        : profile.GitHubUsername,
                });

                if (_sourceControlAccountService.SaveToken(account.Id, token))
                {
                    // Only clear the legacy credential once the token is safely re-stored
                    // under MultiTerminal:SourceAccount:<id>, so a failure can't lose the token.
                    _ownerProfileService.RemoveGitHubToken();
                    _ownerProfileService.ClearGitHubUsername();
                    _debugLogService?.Info("MainForm",
                        $"Migrated legacy GitHub account '{displayName}' to source_control_accounts ({account.Id}).");
                }
                else
                {
                    // Token re-store failed: roll back the orphan row (has_token=0) so the
                    // GetAll().Count>0 idempotency guard doesn't permanently block retries.
                    // Leave the legacy credential in place so the next boot can retry cleanly.
                    _sourceControlAccountService.Delete(account.Id);
                    _debugLogService?.Info("MainForm",
                        $"Legacy GitHub account migration: SaveToken failed; rolled back account {account.Id} for retry.");
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Legacy GitHub account migration failed: {ex.Message}");
                _debugLogService?.Info("MainForm", $"Legacy GitHub account migration failed: {ex.Message}");
            }
        }

        private void InitializeMcpServerAndChatPanel()
        {
            // Guard against double initialization
            if (_mcpServer != null)
                return;

            try
            {
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 1: About to create REST API server");
                // Create REST API server. Pass MainForm's existing _projectService so the REST DI
                // shares the ONE ProjectService instance the CodeGraphWatcher subscribes to (G8) —
                // otherwise the container creates a second instance and project-creation events
                // (ProjectUpdated) fire on an instance the watcher never hears.
                _mcpServer = new MultiTerminalRestServer(5050, _projectService);
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 2: REST API server created");

                if (_mcpServer == null)
                {
                    throw new InvalidOperationException("Failed to create MCP server instance");
                }

                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 3: Checking Broker");
                if (_mcpServer.Broker == null)
                {
                    throw new InvalidOperationException("MCP server Broker is null after construction");
                }
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 4: Broker is not null");

                // Wire up debug logging service
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 5: About to wire up debug logging");
                _mcpServer.Broker.DebugLogService = _debugLogService;
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 6: About to call _debugLogService.Info");
                _debugLogService.Info("MainForm", "MCP Server created, debug logging initialized");
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 7: Debug logging initialized");

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

                // Wire up KnowledgeDatabase — institutional memory (owns its own multiterminal.db connection; bb2b0104).
                // Still takes TaskDb to read the IsFts5Available flag (a bool, not the handle).
                _mcpServer.Broker.KnowledgeDb = new Services.KnowledgeDatabase(_mcpServer.Broker.TaskDb);

                // Wire up SessionMemoryDatabase — vector-embedded session chunks (owns its own connection; bb2b0104).
                _mcpServer.Broker.SessionMemoryDb = new Services.SessionMemoryDatabase(_mcpServer.Broker.TaskDb);

                // Wire up CodeGraphDatabase — Roslyn-based C# code indexer (owns its own connection; bb2b0104).
                var codeGraphDb = new Services.CodeGraphDatabase();
                _mcpServer.Broker.CodeGraphDb = codeGraphDb;
                _mcpServer.Broker.CodeGraphQuery = new Services.CodeGraphQuery(codeGraphDb);

                // Wire up CodeGraphIndexCoordinator — single global permit shared by the manual REST
                // index trigger and the background CodeGraphWatcher so reindexes never run concurrently.
                _mcpServer.Broker.CodeGraphIndexCoordinator = new Services.CodeGraphIndexCoordinator(
                    codeGraphDb, _mcpServer.Broker.CodeGraphQuery);

                // Wire up CodeGraphWatcher — background service that debounce-reindexes registered C#
                // roots on .cs changes so the code graph stays fresh without manual index_code_graph runs.
                _codeGraphWatcher = new Services.CodeGraphWatcher(
                    _projectService,
                    codeGraphDb,
                    _mcpServer.Broker.CodeGraphQuery,
                    _mcpServer.Broker.CodeGraphIndexCoordinator)
                {
                    DebugLogService = _debugLogService
                };
                _codeGraphWatcher.Start();

                // Wire up WikiGeneratorService — produces per-subsystem markdown articles
                _mcpServer.Broker.WikiGenerator = new Services.WikiGeneratorService(
                    _mcpServer.Broker.CodeGraphQuery,
                    _mcpServer.Broker.KnowledgeDb);

                // Wire up GitRepoManager — per-project git read-layer cache for HUD Git tab + dashboard widget
                _mcpServer.Broker.GitRepos = new Services.GitRepoManager();

                // Wire up WorktreeListService — supplies the HUD Git tab's switcher (parent + linked worktrees)
                _mcpServer.Broker.WorktreeList = new Services.WorktreeListService(
                    _mcpServer.Broker.TaskDb, _mcpServer.Broker);

                // Wire up GitAttributionService — Phase 2 overlays for the HUD Git tab (agent / task / pipeline-status chips + contamination banner)
                _mcpServer.Broker.GitAttribution = new Services.GitAttributionService(_mcpServer.Broker.TaskDb);

                // Wire up BranchMetadataService — per-branch outcome strings for the HUD Git tree (HudGitRenderer reads via broker; REST controllers get their own DI instance)
                _mcpServer.Broker.BranchMetadata = new Services.BranchMetadataService(_mcpServer.Broker);

                // Wire up ChangelogAttributionService (Phase 4b, task d42423e3 D3) — drives the HUD Git
                // auto-link pass that routes .claude/project.json changelog edits to the right kanban
                // task instead of "Needs a quick task". Parser list is built explicitly per D3 ("no
                // registry on day one") — new file formats (CHANGELOG.md, RELEASES.md) plug in by
                // adding their IChangelogParser to this list.
                _mcpServer.Broker.ChangelogAttribution = new Services.ChangelogAttributionService(
                    new System.Collections.Generic.List<Services.IChangelogParser>
                    {
                        new Services.ProjectJsonChangelogParser(),
                    });

                // Note: Session memory crash recovery moved to after _mcpServer.StartAsync()
                // so that ProjectService is available (it's wired during server startup)

                // Clean up stale session_agent_map entries (sessions stuck as "active" from past crashes)
                try
                {
                    int cleaned = _mcpServer.Broker.TaskDb.CleanupStaleActiveSessions();
                    if (cleaned > 0)
                        _debugLogService?.Info("MainForm", $"Cleaned up {cleaned} stale active sessions in session_agent_map");
                }
                catch (Exception ex)
                {
                    _debugLogService?.Error("MainForm", $"session_agent_map cleanup failed: {ex.Message}");
                }

                // One-time cleanup: remove orphan empty note-tab rows that older
                // terminals created by keying HUD Notes/Sessions to a worktree path
                // (root cause of task 0ef06717). Only empty rows whose path isn't a
                // registered project are removed; non-empty rows are always kept.
                //
                // FAIL CLOSED: this is a destructive op gated entirely by the keep-set.
                // If the authoritative project enumeration throws or yields nothing, an
                // empty keep-set would make EVERY empty note row (incl. real projects'
                // empty default tabs) look like an orphan. So we only purge when the
                // ProjectDatabase enumeration succeeds AND produced at least one path.
                // Both identity fields (SourcePath and Path) are added to the keep-set,
                // matching how note tabs are now keyed (SourcePath ?? Path).
                try
                {
                    var keepPaths = new List<string>();
                    bool authoritativeOk = false;
                    try
                    {
                        // GetAllRichProjects (not GetAllProjects): the rich Models.Project
                        // carries SourcePath, which is the primary note-tab key field.
                        var dbProjects = _sharedProjectDatabase?.GetAllRichProjects();
                        if (dbProjects != null)
                        {
                            authoritativeOk = true;
                            foreach (var p in dbProjects)
                            {
                                if (!string.IsNullOrEmpty(p.SourcePath)) keepPaths.Add(p.SourcePath);
                                if (!string.IsNullOrEmpty(p.Path)) keepPaths.Add(p.Path);
                            }
                        }
                    }
                    catch { authoritativeOk = false; }
                    try
                    {
                        var regProjects = _mcpServer.Broker.ProjectService?.GetAllRegisteredProjects();
                        if (regProjects != null)
                            foreach (var p in regProjects)
                                if (!string.IsNullOrEmpty(p.Path)) keepPaths.Add(p.Path);
                    }
                    catch { /* secondary source — best-effort, doesn't gate the purge */ }

                    if (!authoritativeOk || keepPaths.Count == 0)
                    {
                        _debugLogService?.Info("MainForm", "Skipped orphan note-tab purge — project enumeration unavailable/empty (fail-closed to avoid deleting legitimate tabs)");
                    }
                    else
                    {
                        int purged = _mcpServer.Broker.TaskDb.PurgeOrphanEmptyNoteTabs(keepPaths);
                        if (purged > 0)
                            _debugLogService?.Info("MainForm", $"Purged {purged} orphan empty note-tab row(s) keyed to non-registered (worktree) paths");
                    }
                }
                catch (Exception ex)
                {
                    _debugLogService?.Error("MainForm", $"orphan note-tab purge failed: {ex.Message}");
                }

                // Auto-sync Claude Code sessions on startup (background, non-blocking)
                // Scans all registered projects from SQLite, not just CWD
                var startupLineageService = _mcpServer.Broker.SessionLineageService;
                var startupDebugLog = _debugLogService;
                var startupProjectDb = _sharedProjectDatabase;
                var startupBroker = _mcpServer.Broker;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var projects = startupProjectDb?.GetAllProjects();
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

                            var result = startupLineageService.SyncNewSessions(claudeFolder);
                            totalImported += result.Imported;
                            totalSkipped += result.Skipped;
                            totalFailed += result.Failed;
                        }

                        if (totalImported > 0)
                        {
                            startupDebugLog?.Info("MainForm", $"Session sync (startup): {totalImported} imported, {totalSkipped} skipped, {totalFailed} failed across {projects.Count} projects");
                            startupBroker.FireSessionLineageUpdated(null);
                        }
                    }
                    catch (Exception ex)
                    {
                        startupDebugLog?.Error("MainForm", $"Session sync startup error: {ex.Message}");
                    }
                });

                // Note: Profiles are set to offline in MessageBroker constructor before loading

                _debugLogService?.Error("InitializeMcpServerAndChatPanel", "Step 8: Wiring ServerError event");
                _mcpServer.ServerError += (s, ex) =>
                {
                    // A :5050 "address already in use" bind failure is owned by the dedicated
                    // port-contention path (TryStartRestServerWithContentionHandlingAsync →
                    // classified Retry/Exit dialog). StartAsync raises ServerError before it
                    // rethrows, so without this guard the user would see this raw stack-trace
                    // dialog FIRST and the friendly one second — repeated on every Retry
                    // (task 4fec40e2). Let the contention handler be the sole voice for that case.
                    if (StartupPortContentionClassifier.IsAddressInUse(ex))
                        return;

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
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 9: Checking chat panel");
                if (_chatPanel == null)
                {
                    throw new InvalidOperationException("Chat panel is null - InitializeDockPanel may not have run");
                }

                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 10: Initializing chat panel");
                _chatPanel.Initialize(_mcpServer.Broker);
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 11: Wiring chat panel events");
                _chatPanel.InjectRequested += OnChatInjectRequested;
                _chatPanel.ReplyRequested += OnChatReplyRequested;
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 12: Initializing tasks panel");
                _tasksPanel.SetDebugLogService(_debugLogService);
                _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService, _settings);
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 13: Wiring tasks panel events");
                _tasksPanel.InjectRequested += OnChatInjectRequested; // Reuse same inject handler

                // Save zoom level to settings whenever user ctrl+wheels in a standalone panel
                _tasksPanel.ZoomChanged += (s, zoom) => _settings?.SetTasksPanelZoom(zoom);
                _chatPanel.ZoomChanged += (s, zoom) => _settings?.SetChatPanelZoom(zoom);

                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Initializing inbox panel");
                _inboxPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.DefaultInboxRecipient);

                // Initialize dashboard header alongside other panels (not deferred — deferring
                // caused the header to never appear if RestoreSession hit any issue)
                _dashboardHeader?.Initialize(_mcpServer.Broker);

                // Persist chat messages to database
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 14: Wiring MessageSent event");
                _mcpServer.Broker.MessageSent += OnChatMessageSent;

                // Push notification support - map terminal IDs to documents
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 15: Wiring TerminalRegistered event");
                _mcpServer.Broker.TerminalRegistered += OnMcpTerminalRegistered;
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 16: Setting OnMessageDelivery");
                _mcpServer.Broker.OnMessageDelivery = OnMcpMessageDelivery;

                // Browser tab support - route tab requests to correct terminal
                _mcpServer.Broker.BrowserTabRequested += OnBrowserTabRequested;

                // AC2: when the broker swaps an agent's active task, push a
                // task_active_changed event over the agent's channel so the
                // auto-cd hook can react. Subscriber lives in MainForm because
                // the broker is HTTP-free.
                _mcpServer.Broker.TaskActiveChanged += OnBrokerTaskActiveChanged;

                // Task db4b18c6: broadcast worktree_pruning to every live
                // terminal before the broker runs PruneForTaskAsync, so any
                // agent (assignee or helper/subagent) with cwd inside the
                // worktree can cd out before Windows holds an open handle.
                _mcpServer.Broker.WorktreePruning += OnBrokerWorktreePruning;

                // Task be599e08: hook-driven /clear → session-start. The SessionStart(source=clear)
                // hook POSTs to /api/terminals/inject; the broker raises TerminalInjectRequested and
                // we resolve the agent to its terminal and inject "initializing..." so the cleared
                // session gets a turn and runs /multiterminal:session-start. Replaces the unreliable
                // keystroke-sniffing detection (which is kept as a deduped fallback).
                _mcpServer.Broker.TerminalInjectRequested += OnBrokerTerminalInjectRequested;

                // Wire up spawn callback for programmatic terminal spawning
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 17: Checking SpawnService");
                if (_mcpServer.SpawnService == null)
                {
                    throw new InvalidOperationException("SpawnService is null");
                }
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 18: Setting OnSpawnRequested");
                _mcpServer.SpawnService.OnSpawnRequested = OnSpawnRequested;
                _mcpServer.SpawnService.OnSpawnAgentRequested = OnSpawnAgentRequested;
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 19: All wiring complete");

                // Initialize HTTP webhook service for agent ready notifications
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 20: Creating HttpWebhookService");
                _webhookService = new MCPServer.Services.HttpWebhookService(_mcpServer.Broker);
                _webhookService.AgentReady += (s, args) =>
                {
                    _debugLogService?.Trace("MainForm", $"Agent ready webhook received: {args.AgentName}");
                    _debugLogService.Info("MainForm", $"Agent {args.AgentName} sent ready notification via webhook");
                };
                _webhookService.Start();
                _debugLogService?.Trace("InitializeMcpServerAndChatPanel", "Step 21: HttpWebhookService started on http://localhost:5000/");

                // Start native agent team watcher
                InitializeTeamWatcher();

                // Initialize and start Oracle advisory agent (always-on, dockable/floatable)
                InitializeOracle();

                // Start MCP server in background
                Task.Run(async () =>
                {
                    try
                    {
                        // Bind the :5050 REST host. If the port is already taken, show a
                        // Retry/Exit dialog that classifies the holder (another MultiTerminal
                        // vs a foreign process) instead of a dead-API error (task 4fec40e2).
                        // A false return means the user chose to exit — stop the init chain.
                        if (!await TryStartRestServerWithContentionHandlingAsync().ConfigureAwait(false))
                        {
                            return;
                        }

                        // Crash recovery: index any unflushed sessions from previous runs
                        // Must run after StartAsync so ProjectService is wired
                        try
                        {
                            var sessionMemDb = _mcpServer.Broker.SessionMemoryDb;
                            var projects = _mcpServer.Broker.ProjectService?.GetAllRegisteredProjects();
                            if (sessionMemDb != null && projects != null)
                            {
                                foreach (var project in projects)
                                {
                                    if (!string.IsNullOrEmpty(project.Path))
                                        sessionMemDb.IndexProjectSessions(project.Path);
                                }
                            }
                        }
                        catch (Exception crEx)
                        {
                            _debugLogService?.Error("MainForm", $"Session memory crash recovery failed: {crEx.Message}");
                        }

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

                        // Start the MultiRemote phone gateway (Phase 2, task ca6c5344) as a
                        // second in-process host on :5100. Independent of the :5050 REST host;
                        // a failure here must not break the MCP/chat path, so it's isolated.
                        try
                        {
                            // Hand the gateway MT's live, fully-wired service singletons so its
                            // in-process controllers/handlers call them directly (no :5050 hop).
                            // Constructed here — after _mcpServer.StartAsync() and all broker/
                            // spawn/stream UI wiring — so every instance below is live. Port is
                            // the fallback only; MultiRemote:Port in appsettings.json wins.
                            _remoteGateway = CreateGatewayHost();
                            await _remoteGateway.StartAsync();
                            _debugLogService?.Info("MainForm", $"MultiRemote gateway started on {_remoteGateway.Url}");

                            // Wire the Multi-Connect restart hook (task 642c14e3, item 2) so the
                            // Settings tab / config endpoints can re-apply restart-required fields
                            // (port, VapidSubject, NotificationSecret, relay BaseUrl) without a full
                            // app relaunch.
                            MultiTerminal.API.Gateway.MultiConnectConfig.GatewayRestarter = RestartGatewayAsync;
                        }
                        catch (Exception gwEx)
                        {
                            _debugLogService?.Error("MainForm", $"MultiRemote gateway failed to start: {gwEx.Message}");
                        }

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
                            _debugLogService?.Info("MainForm", successMsg);

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

                        _debugLogService?.Error("MainForm", detailedError);
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

                _debugLogService?.Error("MainForm", errorMsg);
                MessageBox.Show(errorMsg + $"\n\nLog file: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "multiterminal", "startup-error.log")}\n\nThe Chat feature will not be available.",
                    "MCP Server Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Start the :5050 REST host, converting an "address already in use" bind failure into
        /// a friendly Retry/Exit dialog (task 4fec40e2). Returns true once the host is bound,
        /// false if the user chose to exit. Non-contention failures propagate to the caller's
        /// existing generic startup-error handler unchanged.
        /// </summary>
        private async Task<bool> TryStartRestServerWithContentionHandlingAsync()
        {
            while (true)
            {
                try
                {
                    await _mcpServer.StartAsync().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (StartupPortContentionClassifier.IsAddressInUse(ex))
                {
                    _debugLogService?.Error("MainForm", $":5050 bind failed (address already in use): {ex.Message}");

                    if (ResolvePortContentionDialog(_mcpServer.Port) == DialogResult.Retry)
                    {
                        continue; // user freed the port (or wants another attempt)
                    }

                    // User chose to exit — tear down the app rather than run with a dead API.
                    try
                    {
                        Invoke(new Action(Application.Exit));
                    }
                    catch (Exception exitEx)
                    {
                        // The window handle may not be created yet if the bind failed very early
                        // (Invoke throws). Fall back to a hard exit so a held port can't leave a
                        // headless dead-API process running (task 4fec40e2 debugger MEDIUM).
                        _debugLogService?.Error("MainForm", $"Application.Exit during port-contention exit failed, forcing process exit: {exitEx.Message}");
                        Environment.Exit(0);
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// Probe + classify the current :5050 holder and show the Retry/Cancel dialog. The
        /// probe/lookup/message decisions belong to the pure StartupPortContentionClassifier;
        /// this method is only the WinForms glue. Returns the user's choice (Retry or Cancel).
        /// </summary>
        private DialogResult ResolvePortContentionDialog(int port)
        {
            var probe = StartupHealthProbe.Probe(port);
            // Always resolve the real OS owner: needed both to name a foreign holder AND to
            // cross-check a marker-positive probe against the actual socket owner — a hostile
            // process can echo our public marker but cannot fake which PID owns :5050
            // (task 4fec40e2 security finding).
            PortHolderInfo holder = TcpPortOwnerLookup.Lookup(port);
            // Verify a marker-positive probe against the OS-resolved owner's process identity,
            // NOT the spoofable HTTP body (task 4fec40e2). A genuine second MultiTerminal owns the
            // socket under this same executable name; a foreign squatter does not.
            string expectedProcessName;
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                expectedProcessName = self.ProcessName;
            }
            catch (Exception)
            {
                expectedProcessName = "MultiTerminal";
            }

            var verdict = StartupPortContentionClassifier.ClassifyWithOwner(probe, holder, expectedProcessName);

            string message = StartupPortContentionClassifier.BuildMessage(verdict, port, probe, holder);
            string caption = verdict == PortContentionVerdict.MultiTerminalAlreadyRunning
                ? "MultiTerminal already running"
                : "MultiTerminal — port in use";

            DialogResult result = DialogResult.Cancel;
            try
            {
                Invoke(new Action(() =>
                {
                    result = MessageBox.Show(message, caption, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                }));
            }
            catch (Exception ex)
            {
                // If we can't even show the dialog (e.g. no window handle yet), default to exit.
                _debugLogService?.Error("MainForm", $"Port-contention dialog failed to show: {ex.Message}");
                result = DialogResult.Cancel;
            }

            return result;
        }

        // Serializes Multi-Connect gateway restarts so two rapid Save/Restart clicks can't
        // race a half-disposed host against a fresh StartAsync (task 642c14e3, item 2).
        private readonly System.Threading.SemaphoreSlim _gatewayRestartLock = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// Builds a MultiRemote gateway host from MT's live service singletons. Single source of the
        /// ctor arg list + port fallback so the startup and restart paths can't drift (task 642c14e3).
        /// Port 5100 is only the fallback — the host resolves the effective port from settings/appsettings.
        /// </summary>
        private MultiTerminal.API.Gateway.MultiRemoteGatewayHost CreateGatewayHost()
            => new MultiTerminal.API.Gateway.MultiRemoteGatewayHost(
                5100,
                _mcpServer?.Broker,
                _mcpServer?.SpawnService,
                _mcpServer?.TerminalStreamService,
                _sharedProjectDatabase);

        /// <summary>
        /// Restarts the MultiRemote phone gateway in-process (StopAsync → Dispose → reconstruct →
        /// StartAsync) so restart-required Multi-Connect fields — gateway port, VapidSubject,
        /// NotificationSecret, relay BaseUrl — re-apply WITHOUT a full app relaunch (task 642c14e3,
        /// item 2). The new host re-reads the resolver, so settings.txt changes take effect here.
        /// Wired to <see cref="MultiTerminal.API.Gateway.MultiConnectConfig.GatewayRestarter"/> at
        /// startup; the Settings tab / config endpoints invoke it. Throws on failure so callers can
        /// surface a clear pass/fail to the user.
        /// </summary>
        public async Task RestartGatewayAsync()
        {
            await _gatewayRestartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var old = _remoteGateway;
                _remoteGateway = null;
                if (old != null)
                {
                    try { await old.StopAsync().ConfigureAwait(false); } catch (Exception ex) { _debugLogService?.Error("MainForm", $"Gateway stop during restart failed: {ex.Message}"); }
                    try { old.Dispose(); } catch (Exception ex) { _debugLogService?.Error("MainForm", $"Gateway dispose during restart failed: {ex.Message}"); }
                }

                // Reconstruct with the same live service singletons the original used. Port is the
                // fallback only — the new host resolves the effective port from settings/appsettings.
                // Dedicated local + finally dispose so a failed StartAsync can't leak the host (CA2000);
                // null it after ownership transfers to the field on success.
                MultiTerminal.API.Gateway.MultiRemoteGatewayHost fresh = null;
                try
                {
                    fresh = CreateGatewayHost();
                    await fresh.StartAsync().ConfigureAwait(false);
                    _remoteGateway = fresh;
                    _debugLogService?.Info("MainForm", $"MultiRemote gateway restarted on {fresh.Url}");
                    fresh = null;
                }
                finally
                {
                    fresh?.Dispose();
                }
            }
            finally
            {
                _gatewayRestartLock.Release();
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
                    _debugLogService?.Info("ChatSave", $"{msg}");
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

        /// <summary>
        /// Handles a hook-driven terminal injection request (POST /api/terminals/inject, raised
        /// via MessageBroker.TerminalInjectRequested). Resolves the agent name to its
        /// TerminalDocument and routes through the shared, deduped post-/clear trigger on the UI
        /// thread. This is the reliable replacement for keystroke-sniffing /clear detection
        /// (task be599e08) — the SessionStart(source=clear) hook fires on every /clear.
        /// </summary>
        private void OnBrokerTerminalInjectRequested(object sender, TerminalInjectEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnBrokerTerminalInjectRequested(sender, e)));
                return;
            }

            TerminalDocument targetDoc = null;
            lock (_terminalDocMapLock)
            {
                if (!string.IsNullOrWhiteSpace(e.AgentName))
                    _agentNameToTerminalDoc.TryGetValue(e.AgentName, out targetDoc);
            }

            if (targetDoc?.Terminal == null)
            {
                _debugLogService?.Info("MainForm", $"TerminalInjectRequested: no live terminal for agent '{e.AgentName}'");
                return;
            }

            // Kind=="submit" (task 1d6e599d): type the text and press Enter as a normal
            // prompt submission. Used by the self-clear MCP tool to submit "/clear" into the
            // agent's own terminal. Best-effort, fire-and-forget — matches the broker's
            // "delivery is the subscriber's responsibility" contract. NOT routed through the
            // post-/clear dedup trigger (that's for the SessionStart(source=clear) recovery).
            if (string.Equals(e.Kind, "submit", StringComparison.OrdinalIgnoreCase))
            {
                _ = targetDoc.InjectInputAsync(e.Text).ContinueWith(
                    t => _debugLogService?.Error("MainForm",
                        $"TerminalInjectRequested(submit) for '{e.AgentName}' faulted: {t.Exception?.GetBaseException().Message}"),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                return;
            }

            // Default (Kind=="clear-trigger"): funnel through the deduped trigger so the hook
            // path and the keystroke fallback can't double-inject for the same /clear.
            targetDoc.Terminal.TriggerClearSessionStart("hook", e.Text);
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
                _debugLogService?.Warning("MainForm", $"BrowserTabRequested: terminal not found: {e.TerminalId}");
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
                _debugLogService?.Info("MainForm", $"BrowserTabRequested: no TerminalDocument found for {e.TerminalId}");
                return;
            }

            switch (e.Action)
            {
                case "open":
                    // Page messages are buffered in BrowserTabPage._receivedMessages and retrieved
                    // on demand via get_browser_messages — no push wiring needed. (Previously every
                    // page postMessage was written to the agent's inbox, which the now-removed
                    // InboxMonitor turned into a [cm] nudge typed into the host terminal.)
                    targetDoc.AddBrowserTab(e.TabId, e.Title, e.Url, e.HtmlContent, _currentTheme.IsDark);
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
                _debugLogService?.Info("MainForm", $"Ignoring temporary agent registration: {e.Name}");
                return;
            }

            // Map the MCP terminal ID to the TerminalDocument
            // Strategy: try DocId first, then name match, then last active as final fallback
            TerminalDocument targetDoc = null;

            // Local helper: case-insensitive match of a document by its displayed title
            // (CustomTitle, falling back to TabText). Both are nullable, so null-safe.
            static bool MatchesByName(TerminalDocument t, string name) =>
                (t.CustomTitle?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.TabText?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false);

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
                    .FirstOrDefault(t => MatchesByName(t, e.Name));
            }

            // No last-resort fallback — DocId and name-match are the only reliable mapping paths.
            // The old _lastActiveTerminal fallback caused misrouting when two terminals registered
            // in quick succession and _lastActiveTerminal pointed to the wrong tab.
            if (targetDoc == null)
            {
                _debugLogService?.Info("MainForm", $"Terminal '{e.Name}' (id={e.Id}, docId={e.DocId}) could not be mapped to any TerminalDocument — channel/inbox delivery still works.");
            }

            // SWAPDIAG (task ab32897c): header-swap diagnostic. Captures, per registration,
            // the broker-received (name,docId) and which TerminalDocument it bound to, plus a
            // snapshot of every live doc's (docId,instance,customTitle,contentDir). Comparing
            // the matched docId against each doc's own launch dir reveals the cross. Remove
            // after root cause is confirmed.
            try
            {
                var allDocs = _dockPanel.Documents.OfType<TerminalDocument>()
                    .Select(d => $"[inst={d.InstanceId} docId={d.DocId} title='{d.CustomTitle}' dir='{d.GetWorkingDirectory()}']");
                _debugLogService?.Info("SWAPDIAG",
                    $"REGISTER name='{e.Name}' e.DocId='{e.DocId}' e.Id='{e.Id}' => BOUND " +
                    (targetDoc == null
                        ? "(none)"
                        : $"inst={targetDoc.InstanceId} docId={targetDoc.DocId} title='{targetDoc.CustomTitle}' dir='{targetDoc.GetWorkingDirectory()}'") +
                    " | ALL_DOCS: " + string.Join(" ", allDocs));

                // SWAPDIAG cross detector (task ab32897c): the swap is rare/unreproducible,
                // so flag it LOUDLY the moment it happens. _originalAgentName is the doc's
                // launch-time identity (first-wins promotion); it is read here BEFORE this
                // registration's own promotion (below, lines ~1266/1273) overwrites it, so
                // a mismatch means this registration is binding a name onto a document that
                // launched as a DIFFERENT agent — i.e. the header cross. WARNING-level so a
                // recurrence is grep-able as "SWAPDIAG CROSS" across all persisted logs.
                if (targetDoc != null && !string.IsNullOrEmpty(e.Name))
                {
                    var launchIdentity = targetDoc.OriginalAgentName;
                    if (!string.IsNullOrEmpty(launchIdentity) &&
                        !launchIdentity.Equals(e.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _debugLogService?.Warning("SWAPDIAG",
                            $"CROSS DETECTED: registration name='{e.Name}' (e.DocId='{e.DocId}') bound to " +
                            $"inst={targetDoc.InstanceId} docId={targetDoc.DocId} which LAUNCHED as " +
                            $"'{launchIdentity}' dir='{targetDoc.GetWorkingDirectory()}'. Header will show " +
                            $"'{e.Name}' over '{launchIdentity}' content. task ab32897c");
                    }
                }
            }
            catch { /* diagnostic only */ }

            // 1:1 binding guard: if the resolved document is already confirmed-bound to a
            // DIFFERENT agent identity, this registration belongs to another terminal — do
            // NOT overwrite its title/identity. That cross-wire is what makes a second
            // terminal's name clobber the first's tab, and it also strands the first
            // terminal's HUD Git rebind (which is filtered by _originalAgentName). Re-resolve
            // to an as-yet unclaimed document matching this name so the registering terminal
            // still binds to its OWN tab; if none exists, skip the tab/identity update
            // (channel/inbox delivery still works via e.Id / e.Name).
            if (targetDoc != null
                && !string.IsNullOrEmpty(e.Name)
                && !string.IsNullOrEmpty(targetDoc.OriginalAgentName)
                && !targetDoc.OriginalAgentName.Equals(e.Name, StringComparison.OrdinalIgnoreCase))
            {
                _debugLogService?.Info("MainForm", $"Registration collision: '{e.Name}' (id={e.Id}, docId={e.DocId}) resolved to a document already bound to '{targetDoc.OriginalAgentName}' (docId={targetDoc.DocId}, title='{targetDoc.CustomTitle}'). Refusing to clobber it; re-resolving to this terminal's own document.");

                // Re-resolve to THIS terminal's OWN document, keyed by the un-clobberable
                // stable identity FIRST: a freshly-launched terminal's own doc is already
                // claimed under its own name (StartTerminal promotes _originalAgentName at
                // launch), so prefer the doc whose OriginalAgentName == e.Name. Only if no
                // such doc exists (e.g. an identity not yet promoted) fall back to an
                // as-yet unclaimed doc matching by displayed title. Without the identity-first
                // step, the legitimate target — already self-claimed — would be skipped by
                // the unclaimed filter and the terminal would never bind to its own tab.
                var docs = _dockPanel.Documents.OfType<TerminalDocument>().ToList();
                targetDoc =
                    docs.FirstOrDefault(t => t.OriginalAgentName?.Equals(e.Name, StringComparison.OrdinalIgnoreCase) ?? false)
                    ?? docs.FirstOrDefault(t => string.IsNullOrEmpty(t.OriginalAgentName) && MatchesByName(t, e.Name));

                if (targetDoc == null)
                {
                    _debugLogService?.Warning("MainForm", $"No document matches '{e.Name}' — skipping tab/identity update to avoid clobbering another terminal.");
                }
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
                        // Broker-confirmed registration is an authoritative
                        // identity source — promote into stable agent name
                        // for OnBrokerTaskActiveChanged filtering. First-wins,
                        // so StartTerminal's prior promotion (if any) keeps
                        // priority (cycle-7 codex-adversary HIGH fix).
                        targetDoc.PromoteOriginalAgentName(e.Name);
                        targetDoc.UpdateStatusBar();     // Update the terminal banner with name, avatar, and task
                    }));
                }
                else
                {
                    targetDoc.CustomTitle = e.Name;  // Display the Claude name in the tab
                    targetDoc.PromoteOriginalAgentName(e.Name);
                    targetDoc.UpdateStatusBar();     // Update the terminal banner with name, avatar, and task
                }
            }

            // Auto-seed activity data for new terminal so Activity Panel shows it immediately
            _mcpServer?.Broker?.ActivityService?.UpdateActivity(
                e.Name,
                "idle",
                "Just connected"
            );
            _debugLogService?.Info("MainForm", $"Terminal registered: {e.Name}, seeded initial activity");

            // ORACLE BOOTSTRAP: When Oracle registers its channel, send it the digest processing message.
            // Only send once per app session to avoid duplicate digest tasks on crash restarts.
            if (!_oracleBootstrapped && string.Equals(e.Name, OracleService.OracleName, StringComparison.OrdinalIgnoreCase) && e.ChannelPort > 0)
            {
                _oracleBootstrapped = true;
                _ = SendOracleBootstrapAsync(e.ChannelPort.Value);
            }
        }

        /// <summary>
        /// Send Oracle its bootstrap message after channel registration.
        /// Oracle processes the daily digest and creates suggestion tasks.
        /// </summary>
        private async Task SendOracleBootstrapAsync(int channelPort)
        {
            try
            {
                // Small delay to let Oracle's session fully initialize
                await Task.Delay(3000);

                string bootstrapMessage = "You just started up with MultiTerminal. " +
                    "Run the /daily-intel skill to process the digest pipeline. Do NOT call get_daily_digest directly — you MUST use /daily-intel. " +
                    "Then let the Owner know you're online and ready.";

                bool delivered = await DeliverViaChannel(channelPort, "System", bootstrapMessage, "normal");
                if (delivered)
                    _debugLogService?.Info("MainForm", "Oracle bootstrap message delivered");
                else
                    _debugLogService?.Warning("MainForm", "Oracle bootstrap message delivery failed");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Oracle bootstrap failed: {ex.Message}");
            }
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
                _debugLogService?.Info("MainForm", $"Spawn requested: {agentName} ({agentType}) in {workingDir}");

                // Oracle is always-on — reject spawn requests, it's managed by OracleService
                if (agentName.Equals(OracleService.OracleName, StringComparison.OrdinalIgnoreCase))
                    return (false, null, "Oracle is always-on and managed by OracleService. Send messages directly.");

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
                        _debugLogService?.Info("MainForm", $"Spawning {agentName} with fresh session");
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

                // Auto-initialization is handled by OnClaudeCodeDetected when Claude Code's
                // banner appears in output. That handler uses atomic TypeInput (character-by-character
                // via xterm.js) which is reliable and avoids the double-injection bug caused by
                // the old InjectInputAsync retry loop. No injection needed here.

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

                // Phase 1 worktree isolation: route the headless AgentProcess
                // spawn through the same resolver every other spawn site uses.
                // When this identity owns an active task with a materialized
                // worktree, override cwd AND inject MULTITERMINAL_TASK_WORKTREE
                // so MCP tools that read the env var see the right path.
                string taskWorktreePath = ResolveTaskWorktreePath(agentName);
                Dictionary<string, string> spawnEnv = null;
                if (taskWorktreePath != null)
                {
                    workingDir = taskWorktreePath;
                    spawnEnv = new Dictionary<string, string>
                    {
                        [WorktreeConfig.TaskWorktreeEnvVar] = taskWorktreePath,
                    };
                }

                var agent = new AgentProcess();
                await agent.SpawnAsync(
                    prompt: initialPrompt,
                    workingDir: workingDir,
                    mcpConfigPath: mcpConfigPath,
                    environmentVars: spawnEnv);

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
            _debugLogService?.Trace("MainForm", $"OnMcpMessageDelivery ENTRY: messageId={messageId}, from={sender}, to={recipientId}");
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

            // ORACLE: Always-on — Oracle's channel delivery is handled by the normal path below.
            // No special spawn logic needed since Oracle starts with MultiTerminal.

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

            // FALLBACK: File-based delivery (legacy) — write to inbox file. Used when the
            // recipient has no channel port (not launched with --channels).
            // BEST-EFFORT, not confirmed delivery: the [cm] nudge that used to wake an idle
            // non-channel terminal was removed (the Claude Code Channel is the supported live
            // path now), so nothing surfaces this file until that terminal next runs an
            // inbox-reading hook (PostToolUse/Stop/UserPromptSubmit) — which may be never for a
            // truly idle agent. We still persist + dedup it (so it isn't lost outright or
            // double-appended), but log it as BUFFERED rather than SUCCESS so a message that is
            // never surfaced stays visible in the logs instead of masquerading as delivered.
            bool fileDelivery = InboxFileWriter.WriteMessage(recipientName, messageId, sender, message);
            if (fileDelivery)
            {
                _debugLogService.Warning("MainForm", $"Message {messageId} to {recipientName} BUFFERED to inbox file (no channel port) — best-effort, NOT confirmed surfaced; an idle non-channel terminal may not read it until its next hook fires.");
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
                _debugLogService?.Trace("MainForm", $"Message {messageId} queued. Queue size: {_messageQueue.Count}, InProgress: {_injectionInProgress}");
                _debugLogService.Trace("MainForm", $"Message {messageId} queued with completion tracking. Queue size: {_messageQueue.Count}, InProgress: {_injectionInProgress}");
                if (!_injectionInProgress)
                {
                    _injectionInProgress = true;
                    _debugLogService?.Trace("MainForm", $"Starting ProcessNextMessage");
                    _debugLogService.Trace("MainForm", "Starting ProcessNextMessage");
                    ProcessNextMessage();
                }
                else
                {
                    _debugLogService?.Trace("MainForm", $"Injection already in progress, message will wait in queue");
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
        /// Subscriber for MessageBroker.TaskActiveChanged (AC2). Looks up the
        /// agent's registered terminal, encodes the event as a JSON message,
        /// and posts it to the agent's Claude Code Channel via DeliverViaChannel.
        /// The agent-side auto-cd hook parses the JSON envelope and acts on it.
        ///
        /// <para>Fire-and-forget intentionally: the broker fires the event
        /// synchronously from SetTaskActive and we don't want to block the
        /// caller on an HTTP round-trip. Failures are logged but never
        /// surfaced — a missed event just means the agent won't auto-cd
        /// (manual cd remains an option).</para>
        /// </summary>
        private void OnBrokerTaskActiveChanged(object sender, MCPServer.Services.TaskActiveChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.AgentName)) return;

            try
            {
                var terminal = _mcpServer?.Broker?.GetTerminal(e.AgentName);
                if (terminal == null || terminal.ChannelPort == null)
                {
                    _debugLogService.Trace("MainForm",
                        $"TaskActiveChanged for '{e.AgentName}' but agent has no live channel port — skipping push (agent will pick up the new task on next spawn).");
                    return;
                }

                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "task_active_changed",
                    agentName = e.AgentName,
                    oldTaskId = e.OldTaskId,
                    oldWorktree = e.OldWorktreePath,
                    newTaskId = e.NewTaskId,
                    newWorktree = e.NewWorktreePath,
                });

                int port = terminal.ChannelPort.Value;
                _ = Task.Run(async () =>
                {
                    bool ok = await DeliverViaChannel(port, "MultiTerminal", payload, "normal").ConfigureAwait(false);
                    if (!ok)
                    {
                        _debugLogService.Warning("MainForm",
                            $"task_active_changed push failed for '{e.AgentName}' on channel port {port}.");
                    }
                });
            }
            catch (Exception ex)
            {
                _debugLogService.Warning("MainForm",
                    $"OnBrokerTaskActiveChanged threw for '{e?.AgentName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Subscriber for MessageBroker.WorktreePruning (task db4b18c6).
        /// Broadcasts a <c>{type:"worktree_pruning",...}</c> envelope to every
        /// connected terminal's Claude Code Channel so any agent with cwd
        /// inside the worktree can <c>cd</c> to the repo root before the
        /// broker invokes <c>git worktree remove</c>.
        ///
        /// <para>Cycle-2 fixes:</para>
        /// <list type="bullet">
        ///   <item><b>Audience (debugger HIGH):</b> uses
        ///   <see cref="MessageBroker.GetAllConnectedTerminals"/> instead of
        ///   <see cref="MessageBroker.GetTerminals"/>. The latter filters out
        ///   <c>"Agent *"</c> subagents — exactly the shells most likely to
        ///   hold cwd inside the worktree.</item>
        ///   <item><b>Observability (adversary HIGH):</b> the handler now
        ///   synchronously awaits delivery completion (or a 1.5s timeout)
        ///   before returning, replacing the broker's removed bare
        ///   Thread.Sleep(500). The broker calls subscribers synchronously
        ///   from <c>FireWorktreePruning</c>, so blocking here is the natural
        ///   synchronization point — agents are notified before prune fires.</item>
        /// </list>
        ///
        /// <para>The block is still best-effort: a delivered HTTP POST does
        /// not prove the agent reacted before prune. Pass 3 catches that
        /// residual gap.</para>
        /// </summary>
        private void OnBrokerWorktreePruning(object sender, MCPServer.Services.WorktreePruningEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.WorktreePath)) return;

            try
            {
                // Task d32c80eb: target only the task's own agents (assignee +
                // helpers + all temporary "Agent *" subagents) instead of every
                // connected terminal, so unrelated peer terminals don't render
                // the control envelope as visible channel noise. The agents that
                // can actually be stranded cwd-inside the worktree are still
                // covered; only named bystanders are trimmed.
                var terminals = _mcpServer?.Broker?.GetWorktreeEvictionAudience(e.TaskId, e.AgentName);
                if (terminals == null || terminals.Count == 0) return;

                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "worktree_pruning",
                    taskId = e.TaskId,
                    worktreePath = e.WorktreePath,
                    repoRoot = e.RepoRoot,
                    agentName = e.AgentName,
                });

                // Cycle-4 adversary MED fix: track per-delivery success, not
                // just completion. A 500/refused channel server completes the
                // task with ok==false; without this, Task.WhenAll wins and
                // AllDelivered stays true even though no agent acknowledged.
                int failureCount = 0;
                var deliveries = new List<Task>();
                foreach (var t in terminals)
                {
                    if (t?.ChannelPort == null) continue;
                    int port = t.ChannelPort.Value;
                    string termName = t.Name;
                    deliveries.Add(Task.Run(async () =>
                    {
                        bool ok = await DeliverViaChannel(port, "MultiTerminal", payload, "normal").ConfigureAwait(false);
                        if (!ok)
                        {
                            System.Threading.Interlocked.Increment(ref failureCount);
                            _debugLogService.Warning("MainForm",
                                $"worktree_pruning push failed for '{termName}' on channel port {port}.");
                        }
                    }));
                }

                if (deliveries.Count == 0) return;

                // Block subscriber until deliveries land OR 1.5s elapses,
                // whichever first. Broker fires the event synchronously, so
                // returning here means the agents have been notified before
                // the broker proceeds to PruneForTaskAsync. The 1.5s cap
                // bounds the broker stall on a sick channel server.
                //
                // Cycle-3 adversary HIGH fix: track which task won the race.
                // If Task.Delay wins, at least one delivery didn't complete in
                // time — signal that to the broker via args.AllDelivered=false
                // so it can DEFER prune to the janitor instead of plowing
                // ahead with a likely-doomed rmdir.
                //
                // Cycle-4 adversary MED fix: defer also if any delivery
                // returned false (failure was fast but real — no acknowledgement).
                var allDeliveries = Task.WhenAll(deliveries);
                var timeout = Task.Delay(1500);
                var winner = Task.WhenAny(allDeliveries, timeout).GetAwaiter().GetResult();
                bool timedOut = winner == timeout;
                bool anyFailed = System.Threading.Interlocked.CompareExchange(ref failureCount, 0, 0) > 0;
                if (timedOut || anyFailed)
                {
                    e.AllDelivered = false;
                    _debugLogService.Warning("MainForm",
                        $"worktree_pruning broadcast for '{e.WorktreePath}' incomplete (timedOut={timedOut}, failedDeliveries={failureCount}) — signaling defer to broker.");
                }
            }
            catch (Exception ex)
            {
                _debugLogService.Warning("MainForm",
                    $"OnBrokerWorktreePruning threw for worktree '{e?.WorktreePath}': {ex.Message}");
            }
        }

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

                using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await _channelHttpClient.PostAsync($"http://127.0.0.1:{channelPort}/message", content);
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

            // Route through OpenHelpDocs() (same path the legacy help button uses) so a
            // missing docs/html/index.html surfaces a MessageBox instead of silently
            // no-op'ing, and the open logic lives in one place.
            _dashboardHeader.DocsRequested += () => OpenHelpDocs();

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
                // Oracle click → activate/bring her to front (she's a dockable DockContent now)
                if (_oracleService != null && string.Equals(name, OracleService.OracleName, StringComparison.OrdinalIgnoreCase))
                {
                    _oracleService.Activate();
                    return;
                }

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

            // Help button (opens the local HTML documentation site in the default browser)
            _helpButton = new ToolStripButton
            {
                Text = "\u2753", // Question mark symbol
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Help & Documentation"
            };
            _helpButton.Click += (s, e) => OpenHelpDocs();

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
            _toolStrip.Items.Add(_helpButton);
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
                dialog.LoadExisting(existing.FullName, existing.Email);
            }

            if (dialog.ShowDialog() == true)
            {
                var profile = existing ?? new Models.OwnerProfile();
                profile.FullName = dialog.FullName;
                profile.Email = dialog.Email;

                _ownerProfileService.SaveProfile(profile);
            }
        }

        private void ShowSettingsDialog()
        {
            var dialog = new SettingsWpfDialog(_settings, _currentTheme.IsDark, _ownerProfileService, _sourceControlAccountService);
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

        /// <summary>
        /// Opens the bundled HTML documentation site (docs/html/index.html) in the user's default browser.
        /// </summary>
        private void OpenHelpDocs()
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
            else
            {
                MessageBox.Show(
                    this,
                    "Documentation was not found at:\n" + docsPath,
                    "Help & Documentation",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void ApplySettingsFromDialog(SettingsWpfDialog dialog)
        {
            // Apply toolbar font (legacy toolstrip)
            if (_toolStrip != null)
                _toolStrip.Font = new Font(_toolStrip.Font.FontFamily, dialog.ToolbarFontSize);

            // Apply terminal font to all open terminals (including Oracle)
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                doc.SetFontSize(dialog.TerminalFontSize);
            }
            _oracleService?.SetFontSize(dialog.TerminalFontSize);

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
            _settings = SettingsService.Default;

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
                _debugLogService?.Trace("MainForm", "Shown event #1: Setting WindowState...");
                _debugLogService?.Trace("MainForm", $"Shown event #1: Saved state is {savedState}");
                WindowState = savedState;
                _debugLogService?.Trace("MainForm", "Shown event #1: WindowState set successfully");
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
                catch (Exception ex) { _debugLogService?.Error("MainForm", $"Embedded agent theme error: {ex.Message}"); }
            }

            // Update all open lifecycle board windows
            TaskLifecycleBoardForm.ApplyThemeToAll(_currentTheme.IsDark);

            // Update all open Code Review popups
            Dialogs.CodeReviewPopupManager.ApplyThemeToAll(_currentTheme.IsDark);
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

        // Thin wrapper around the canonical resolver on MessageBroker. Kept
        // for ergonomics at MainForm spawn sites that already had this name —
        // the broker version is the single source of truth and is also reachable
        // from non-UI spawn paths (REST controllers, headless AgentProcess flow,
        // future callers). See <see cref="MessageBroker.ResolveTaskWorktreePath"/>.
        private string ResolveTaskWorktreePath(string terminalName)
        {
            return _mcpServer?.Broker?.ResolveTaskWorktreePath(terminalName);
        }

        // AC7 launch-root helper. When an agent owns an active task with a
        // materialized worktree, the spawn dir becomes the project's repo root
        // (so Claude Code's permission scope + harness cwd-pin cover the entire
        // repo and any sibling worktrees are reachable). The in-shell cd inside
        // ConPtyTerminal then narrows to taskWorktreePath. Returns:
        //   - (fallbackDir, null) when the agent has no active task / no worktree
        //   - (repoRoot, worktreePath) when both resolve cleanly
        //   - (worktreePath, worktreePath) when worktree exists but repoRoot
        //     can't be resolved — preserves pre-AC7 behavior on that edge.
        private string ResolveSpawnDir(string terminalName, string fallbackDir, out string taskWorktreePath)
        {
            taskWorktreePath = ResolveTaskWorktreePath(terminalName);
            if (taskWorktreePath == null) return fallbackDir;
            string repoRoot = _mcpServer?.Broker?.ResolveTaskRepoRoot(terminalName);
            if (!string.IsNullOrEmpty(repoRoot) && System.IO.Directory.Exists(repoRoot))
                return repoRoot;
            return taskWorktreePath;
        }

        public void AddNewTerminal(string workingDirectory = null, float? fontSize = null, bool forceTabMode = false, string identityName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, bool atomicIdentityUniqueness = false)
        {
            _debugLogService?.Trace("AddNewTerminal", "===== START =====");
            _debugLogService?.Trace("AddNewTerminal", $"workingDirectory: '{workingDirectory ?? "null"}'");
            _debugLogService?.Trace("AddNewTerminal", $"fontSize: {fontSize?.ToString() ?? "null"}");
            _debugLogService?.Trace("AddNewTerminal", $"forceTabMode: {forceTabMode}");
            _debugLogService?.Trace("AddNewTerminal", $"identityName: '{identityName ?? "null"}'");
            _debugLogService?.Trace("AddNewTerminal", $"autoRunCommand: '{autoRunCommand ?? "null"}'");
            _debugLogService?.Trace("AddNewTerminal", $"projectId: '{projectId ?? "null"}'");
            _debugLogService?.Trace("AddNewTerminal", $"isTeamLead: '{isTeamLead}'");
            _debugLogService?.Trace("AddNewTerminal", "Creating TerminalDocument...");
            var doc = new TerminalDocument();
            _debugLogService?.Trace("AddNewTerminal", "TerminalDocument created");

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

            _debugLogService?.Trace("AddNewTerminal", $"Showing terminal in DockPanel (maxGrids: {maxGrids}, maxTabs: {maxTabs}, forceTab: {forceTabMode})...");
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
            _debugLogService?.Trace("AddNewTerminal", "Terminal shown in DockPanel");

            // When called with no launch parameters, show the start screen instead of starting a shell.
            // Any meaningful parameter (directory, identity, command, project) triggers immediate start.
            bool hasLaunchParams = !string.IsNullOrEmpty(workingDirectory)
                || !string.IsNullOrEmpty(identityName)
                || !string.IsNullOrEmpty(autoRunCommand)
                || !string.IsNullOrEmpty(projectId);

            // Register with MCP server AFTER adding to DockPanel so the
            // TerminalRegistered event handler can find this doc by DocId
            _debugLogService?.Trace("AddNewTerminal", "Registering terminal with MCP server...");
            if (_mcpServer?.Broker != null)
            {
                if (!string.IsNullOrEmpty(identityName))
                {
                    _debugLogService?.Trace("AddNewTerminal", $"Identity name provided: '{identityName}'");

                    // Apply team lead naming convention: "Team Lead {Name} - {3-digit random}"
                    if (isTeamLead)
                    {
                        // CA5394: display-label disambiguator, not a security identifier — insecure RNG is fine.
#pragma warning disable CA5394
                        string suffix = Random.Shared.Next(100, 999).ToString();
#pragma warning restore CA5394
                        identityName = $"Team Lead {identityName} - {suffix}";
                        _debugLogService?.Trace("AddNewTerminal", $"Team lead naming applied: '{identityName}'");
                    }

                    terminalName = PreRegisterTerminalWithName(doc.DocId, identityName, isTeamLead, atomicIdentityUniqueness);
                    _debugLogService?.Trace("AddNewTerminal", $"PreRegisterTerminalWithName returned: '{terminalName}'");
                }
                else if (hasLaunchParams)
                {
                    _debugLogService?.Trace("AddNewTerminal", "No identity name, using 'Unassigned'");
                    terminalName = "Unassigned";
                    _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId);
                }
                // else: start screen tab — no MCP registration yet; happens when user launches a project
            }
            _debugLogService?.Trace("AddNewTerminal", $"Terminal registered as: '{terminalName ?? "null"}'");

            if (hasLaunchParams)
            {
                // Start the terminal with specified or default directory and optional auto-run command
                string dir = workingDirectory ?? _settings?.GetLastDirectory();

                // AC7 launch-root strategy (task c6ed236c): when this terminal is bound to
                // an identity that owns an active task with a materialized worktree, spawn
                // at the project repo root so Claude Code's permission scope + harness
                // cwd-pin cover the entire repo (worktrees live inside as descendants).
                // ConPtyTerminal's in-shell cd then narrows to taskWorktreePath before
                // claude starts. Falls through to `dir` when no worktree is in play.
                dir = ResolveSpawnDir(terminalName, dir, out string taskWorktreePath);

                _debugLogService?.Trace("AddNewTerminal", $"Starting terminal...");
                _debugLogService?.Trace("AddNewTerminal", $"dir: '{dir}'");
                _debugLogService?.Trace("AddNewTerminal", $"terminalName: '{terminalName}'");
                _debugLogService?.Trace("AddNewTerminal", $"autoRunCommand: '{autoRunCommand ?? "null"}'");
                _debugLogService?.Trace("AddNewTerminal", $"taskWorktreePath: '{taskWorktreePath ?? "null"}'");
                _debugLogService?.Trace("AddNewTerminal", $"Calling doc.StartTerminal('{dir}', '{terminalName}', '{autoRunCommand ?? "null"}', '{spawnerName ?? "null"}', '{projectId ?? "null"}', isTeamLead={isTeamLead}, gatewayProfile='{gatewayProfile ?? "null"}', taskWorktreePath='{taskWorktreePath ?? "null"}')...");
                doc.StartTerminal(dir, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile, taskWorktreePath);
                _debugLogService?.Trace("AddNewTerminal", "doc.StartTerminal returned");
            }
            else
            {
                // No launch params: show start screen so user can pick a project
                _debugLogService?.Trace("AddNewTerminal", "No launch params — showing start screen");
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
            _debugLogService?.Trace("AddNewTerminal", "Focusing terminal...");
            doc.Activate();
            if (!doc.IsStartScreenVisible)
                doc.FocusTerminal();
            _lastActiveTerminal = doc;
            _debugLogService?.Trace("AddNewTerminal", "Completed");
        }

        /// <summary>
        /// Wires the start screen events on a newly created TerminalDocument and injects the project database.
        /// Call this at every TerminalDocument creation site.
        /// </summary>
        private void WireStartScreenEvents(TerminalDocument doc)
        {
            doc.SetProjectDatabase(_sharedProjectDatabase, _projectService);
            doc.ProjectLaunched += OnStartScreenProjectLaunched;
            doc.StartScreenOpenPowerShellRequested += OnStartScreenOpenPowerShell;
            doc.StartScreenJustClaudeRequested += OnStartScreenJustClaude;
            doc.StartScreenNewProjectRequested += OnStartScreenNewProject;
            doc.StartScreenProjectDeleteRequested += OnStartScreenProjectDeleteRequested;
        }

        /// <summary>
        /// Handles a project delete from a Home/start-screen card. Confirms (native MessageBox),
        /// routes through the canonical broker delete (unregisters the project — firing
        /// ProjectRemoved so the code-graph watcher drops it — evicts its cg rows, records activity),
        /// then refreshes the start-screen card grid. Unregister only: .claude/project.json is kept.
        /// </summary>
        private void OnStartScreenProjectDeleteRequested(object sender, string projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;

            var broker = _mcpServer?.Broker;
            if (broker == null) return;

            string name = broker.GetProjectsList()?.FirstOrDefault(p => p.Id == projectId)?.Name ?? projectId;

            var confirm = MessageBox.Show(
                this,
                $"Delete project '{name}' from MultiTerminal?\n\nThe project is unregistered and its code-graph data removed. The .claude/project.json file on disk is kept, and tasks are not deleted.",
                "Delete Project",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            var result = broker.DeleteProject(projectId, "ui", deleteLocalConfig: false);
            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.Error ?? "Failed to delete project.",
                    "Delete Project",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            (sender as TerminalDocument)?.RefreshStartScreenProjects();
        }

        /// <summary>
        /// Handles a project selection from the start screen.
        /// Loads the project, builds the Claude Code launch command, updates last_opened_at,
        /// then calls StartTerminal() on the source document.
        /// </summary>
        private void OnStartScreenProjectLaunched(object sender, StartScreenLaunchEventArgs e)
        {
            _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] ===== ENTER ===== projectId='{e?.ProjectId}'");
            if (sender is not TerminalDocument doc)
            {
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] sender is not TerminalDocument: {sender?.GetType().Name ?? "null"}");
                return;
            }
            _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] sender doc: DocId='{doc.DocId}' InstanceId={doc.InstanceId} HashCode={doc.GetHashCode()} CustomTitle='{doc.CustomTitle}' TabText='{doc.TabText}' Text='{doc.Text}'");

            try
            {
                var project = _sharedProjectDatabase?.GetRichProject(e.ProjectId);
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] _sharedProjectDatabase.GetRichProject('{e.ProjectId}') returned: {(project == null ? "NULL" : $"id='{project.Id}' name='{project.Name}' sourcePath='{project.SourcePath}' path='{project.Path}'")}");
                if (project == null)
                {
                    _debugLogService?.Warning("StartScreen", $"Project not found: {e.ProjectId}");
                    return;
                }

                // Sanity check: requested ID must equal returned ID
                if (!string.Equals(project.Id, e.ProjectId, StringComparison.Ordinal))
                {
                    _debugLogService?.Warning("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] *** ID MISMATCH *** requested='{e.ProjectId}' returned='{project.Id}' name='{project.Name}'");
                }

                // Update last opened timestamp
                project.LastOpenedAt = DateTime.Now;
                _sharedProjectDatabase?.SaveRichProject(project);

                // Resolve terminal kind. Explicit override from the start-screen split-button
                // dropdown wins; otherwise fall back to the project's stored default (normalized).
                var kind = !string.IsNullOrEmpty(e.TerminalKindOverride)
                    ? TerminalKindHelper.ParseOrDefault(e.TerminalKindOverride)
                    : TerminalKindHelper.ParseOrDefault(project.DefaultTerminal);
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] Resolved kind='{kind}' (override='{e.TerminalKindOverride ?? "(none)"}' projectDefault='{project.DefaultTerminal ?? "(none)"}')");

                // Build launch command for the chosen kind. BuildCommand dispatches to
                // BuildClaudeCommand/BuildCodexCommand; the Codex path also refreshes
                // ~/.codex/config.toml + writes the launcher scaffolding as a side-effect.
                var launchCmd = Services.LaunchCommandBuilder.BuildCommand(kind, project);

                // Bootstrap gate — see HandleBootstrapErrorIfAny for rationale.
                if (HandleBootstrapErrorIfAny(launchCmd, onBeforeDialog: () => doc.ShowStartScreen()))
                    return;

                string launchDir = launchCmd.WorkingDirectory;
                string autoRunCommand = launchCmd.AutoRunCommand;
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] LaunchCommandBuilder: workingDir='{launchDir}' autoRun='{autoRunCommand}' for project name='{project.Name}' id='{project.Id}' kind='{kind}'");

                // Register terminal before starting (start screen tabs are unregistered).
                // Identity: team lead if set; else for Codex use the configured default agent
                // name so headless Codex launches get a stable identity instead of "Unassigned".
                bool isTeamLead = !string.IsNullOrEmpty(project.TeamLead);
                string terminalName = ResolveCodexIdentityName(kind, project) ?? "Unassigned";
                if (_mcpServer?.Broker != null)
                {
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

                    // Codex non-team-lead launches route through RegisterTerminalUnique
                    // so the probe-then-register race (adversary HIGH from Run 3) is
                    // closed: the broker locks around probe + register, and if a
                    // concurrent launch took the name since our resolve, we get a
                    // fresh suffix atomically. Team-lead path keeps the existing
                    // IdentityPickerDialog flow (dialog is authoritative).
                    if (!isTeamLead && kind == TerminalKind.Codex)
                    {
                        _mcpServer.Broker.RegisterTerminalUnique(terminalName, out string resolved, doc.DocId, isTeamLead);
                        terminalName = resolved;
                    }
                    else
                    {
                        _mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId, isTeamLead);
                    }
                }

                // Sync MCP configs: gateway-aware path if available, else standard path.
                // Skip when launchDir is the user-profile fallback (same guard as
                // OnProjectLaunchRequested) so a broken project path can't mutate ~/.mcp.json.
                string gatewayProfile = null;
                if (Services.LaunchCommandBuilder.IsDistinctProjectRoot(project, launchDir))
                {
                    try
                    {
                        if (_mcpConfigService != null)
                        {
                            _mcpConfigService.EnsureMcpConfigsForProjectWithGateway(project.Id, launchDir, project.Name);
                            if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                                gatewayProfile = Services.GatewayIntegrationService.GetGatewayProfileName(project.Name);
                        }
                    }
                    catch (Exception ex) { _debugLogService?.Error("StartScreen", $"MCP config sync failed: {ex.Message}"); }
                }

                // AC7 launch-root strategy (task c6ed236c): spawn at repo root, in-shell
                // cd narrows to the worktree. Falls through to launchDir on no worktree.
                launchDir = ResolveSpawnDir(terminalName, launchDir, out string taskWorktreePath);

                _debugLogService?.Trace("StartScreen", $"Launching project '{project.Name}' in {launchDir}");
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] Calling doc.StartTerminal: doc.DocId='{doc.DocId}' launchDir='{launchDir}' terminalName='{terminalName}' projectId='{project.Id}' projectName='{project.Name}' isTeamLead={isTeamLead} taskWorktreePath='{taskWorktreePath ?? "null"}'");
                doc.StartTerminal(launchDir, terminalName, autoRunCommand, projectId: project.Id, isTeamLead: isTeamLead, gatewayProfile: gatewayProfile, taskWorktreePath: taskWorktreePath);
                _debugLogService?.Trace("MainForm", $"#PROJ# [MainForm.OnStartScreenProjectLaunched] After StartTerminal: doc.CustomTitle='{doc.CustomTitle}' doc.TabText='{doc.TabText}' doc.Text='{doc.Text}'");

                // Apply current font size
                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                doc.SetFontSize(terminalFontSize);

                doc.Activate();
                doc.FocusTerminal();
                _lastActiveTerminal = doc;
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("StartScreen", $"OnStartScreenProjectLaunched error: {ex.Message}");
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
                _debugLogService?.Error("StartScreen", $"OnStartScreenOpenPowerShell error: {ex.Message}");
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

                // AC7 launch-root strategy (task c6ed236c): spawn at repo root, in-shell
                // cd narrows to the worktree. Falls through to workingDir on no worktree.
                workingDir = ResolveSpawnDir(terminalName, workingDir, out string taskWorktreePath);

                doc.StartTerminal(workingDir, terminalName, launch.AutoRunCommand, isTeamLead: isTeamLead, taskWorktreePath: taskWorktreePath);

                float terminalFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                doc.SetFontSize(terminalFontSize);

                doc.Activate();
                doc.FocusTerminal();
                _lastActiveTerminal = doc;
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("StartScreen", $"OnStartScreenJustClaude error: {ex.Message}");
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

                // Converged creation path: the broker.CreateProject method is the shared
                // entry point for both this UI dialog and the create_project MCP tool. It
                // generates the 8-char ID, writes both the SQLite row (rich columns
                // included) and the portable .claude/project.json, fires ProjectsUpdated
                // to refresh dashboards, and records an activity feed entry. Falls back
                // to the legacy ProjectService.SaveProject path if the broker isn't
                // available (degenerate scenario).
                Models.Project project;
                if (_mcpServer?.Broker != null)
                {
                    var createResult = _mcpServer.Broker.CreateProject(
                        name: wpfDialog.ProjectName,
                        description: null,
                        createdBy: "new-project-dialog",
                        path: projectFolder,
                        teamLead: wpfDialog.SelectedTeamLead,
                        defaultTerminal: wpfDialog.SelectedDefaultTerminal,
                        // The UI permits creating a project on an existing folder that
                        // happens to have a .claude/project.json from a prior session —
                        // that's a "register existing" intent, not a duplicate-create.
                        allowReuseExisting: true);
                    if (!createResult.Success || createResult.CreatedFileProject == null)
                        throw new InvalidOperationException(createResult.Error ?? "Failed to create project");
                    project = createResult.CreatedFileProject;
                }
                else
                {
                    project = Models.Project.Create(wpfDialog.ProjectName, projectFolder);
                    project.TeamLead = wpfDialog.SelectedTeamLead;
                    project.DefaultTerminal = wpfDialog.SelectedDefaultTerminal;
                    project.CreatedBy = "new-project-dialog";
                    if (_projectService != null)
                        _projectService.SaveProject(project);
                    else
                        _sharedProjectDatabase?.SaveRichProject(project);
                }

                // Build launch command for the project's default terminal (Claude Code or Codex)
                var terminalKind = Models.TerminalKindHelper.ParseOrDefault(project.DefaultTerminal);
                var launchCmd = Services.LaunchCommandBuilder.BuildCommand(terminalKind, project);

                // Bootstrap gate — tail message tweaked because the project was created
                // but no terminal started, so the user can retry from the project card.
                if (HandleBootstrapErrorIfAny(launchCmd,
                        tailMessage: "The project was created but no terminal has been started. You can retry launching from the project card."))
                    return;

                string launchDir = launchCmd.WorkingDirectory;
                string autoRunCommand = launchCmd.AutoRunCommand;

                // Identity: same shape as sibling launch sites. See ResolveCodexIdentityName.
                // Codex non-team-lead launches use the atomic RegisterTerminalUnique path.
                bool isTeamLead = !string.IsNullOrEmpty(project.TeamLead);
                string terminalName = ResolveCodexIdentityName(terminalKind, project) ?? "Unassigned";
                if (_mcpServer?.Broker != null)
                {
                    if (!isTeamLead && terminalKind == Models.TerminalKind.Codex)
                    {
                        _mcpServer.Broker.RegisterTerminalUnique(terminalName, out string resolved, sourceDoc.DocId, isTeamLead);
                        terminalName = resolved;
                    }
                    else
                    {
                        _mcpServer.Broker.RegisterTerminal(terminalName, sourceDoc.DocId, isTeamLead);
                    }
                }

                // Claude Code only: wire one-shot ClaudeCodeDetected handler to inject /new-project.
                // Codex has no equivalent onboarding skill — the startup prompt (see CodexPromptService)
                // handles the "tell the agent its name and first actions" briefing.
                if (terminalKind == Models.TerminalKind.ClaudeCode)
                {
                    EventHandler newProjectHandler = null;
                    newProjectHandler = async (s, ev) =>
                    {
                        sourceDoc.ClaudeCodeDetected -= newProjectHandler;

                        // Wait for Claude Code to settle after standard "initializing..." injection
                        await Task.Delay(3000);
                        _debugLogService?.Trace("MainForm", "New project flow: injecting /new-project");

                        bool injected = await sourceDoc.InjectInputAsync("/new-project");
                        if (!injected)
                        {
                            _debugLogService?.Error("MainForm", "/new-project JS injection failed, trying direct write");
                            try { sourceDoc.Terminal.Write("/new-project\r"); }
                            catch (Exception ex) { _debugLogService?.Error("MainForm", $"/new-project fallback failed: {ex.Message}"); }
                        }
                    };
                    sourceDoc.ClaudeCodeDetected += newProjectHandler;
                }

                // Sync MCP configs: gateway-aware path if available, else standard path.
                // Skip when launchDir is the user-profile fallback (same guard as sibling sites).
                string gatewayProfile2 = null;
                if (Services.LaunchCommandBuilder.IsDistinctProjectRoot(project, launchDir))
                {
                    try
                    {
                        if (_mcpConfigService != null)
                        {
                            _mcpConfigService.EnsureMcpConfigsForProjectWithGateway(project.Id, launchDir, project.Name);
                            if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                                gatewayProfile2 = Services.GatewayIntegrationService.GetGatewayProfileName(project.Name);
                        }
                    }
                    catch (Exception ex) { _debugLogService?.Error("StartScreen", $"MCP config sync failed: {ex.Message}"); }
                }

                // AC7 launch-root strategy (task c6ed236c): spawn at repo root, in-shell
                // cd narrows to the worktree. Falls through to launchDir on no worktree.
                launchDir = ResolveSpawnDir(terminalName, launchDir, out string taskWorktreePath2);

                // Start terminal in project folder
                _debugLogService?.Trace("StartScreen", $"Launching new project '{project.Name}' in {launchDir} (taskWorktreePath='{taskWorktreePath2 ?? "null"}')");
                sourceDoc.StartTerminal(launchDir, terminalName, autoRunCommand,
                    projectId: project.Id, isTeamLead: isTeamLead, gatewayProfile: gatewayProfile2, taskWorktreePath: taskWorktreePath2);

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
                _debugLogService?.Error("StartScreen", $"OnStartScreenNewProject error: {ex.Message}");
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
                _debugLogService?.Error("MainForm", $"Identity discovery failed: {ex.Message}");
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
                _debugLogService?.Error("MainForm", $"Pre-registration failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Pre-registers a terminal with a specific identity name.
        /// Used by AddNewTerminalWithIdentity for "Launch as..." feature.
        /// </summary>
        /// <summary>
        /// Central gate for Codex bootstrap failures. Used by the three Codex launch
        /// paths (start-screen, new-project, project-panel) to show a consistent
        /// MessageBox when <see cref="Services.LaunchCommand.BootstrapError"/> is
        /// populated. Returns true when an error was handled (caller should early-return).
        /// </summary>
        private bool HandleBootstrapErrorIfAny(Services.LaunchCommand cmd, Action onBeforeDialog = null, string tailMessage = null)
        {
            if (cmd == null || string.IsNullOrEmpty(cmd.BootstrapError))
                return false;

            onBeforeDialog?.Invoke();
            string tail = tailMessage ?? "The terminal has not been started.";
            MessageBox.Show(
                $"Codex cannot launch: {cmd.BootstrapError}\n\n{tail}",
                "Codex bootstrap failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        /// <summary>
        /// Resolves the identity name for a terminal launch. For team-lead launches
        /// returns the team-lead name directly. For Codex launches without a team
        /// lead, reads the configured default agent name and applies
        /// <see cref="MCPServer.Services.MessageBroker.GetUniqueNameFor"/> so two
        /// concurrent Codex terminals sharing the same default don't alias to one
        /// broker identity. Returns null when the caller should fall back to
        /// "Unassigned" (or leave identity unset entirely, as OnProjectLaunchRequested does).
        /// </summary>
        private string ResolveCodexIdentityName(Models.TerminalKind kind, Models.Project project)
        {
            if (project != null && !string.IsNullOrEmpty(project.TeamLead))
                return project.TeamLead;

            if (kind != Models.TerminalKind.Codex)
                return null;

            string codexDefault = _settings?.GetCodexDefaultAgentName();
            if (string.IsNullOrWhiteSpace(codexDefault))
                return null;

            // Skip uniqueness suffixing for the "Unassigned" sentinel — it's
            // intentionally shared across multiple unnamed terminals.
            if (codexDefault.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
                return codexDefault;

            return _mcpServer?.Broker != null
                ? _mcpServer.Broker.GetUniqueNameFor(codexDefault)
                : codexDefault;
        }

        private string PreRegisterTerminalWithName(string docId, string identityName, bool isTeamLead = false, bool atomicUniqueness = false)
        {
            try
            {
                // Register with the specific identity name. When the caller asked
                // for atomic uniqueness (Codex default-agent launches), route
                // through RegisterTerminalUnique so the probe + register critical
                // section is held under a broker-level lock. "Unassigned" is
                // exempt inside the broker.
                if (atomicUniqueness && !isTeamLead)
                {
                    var uniqueResult = _mcpServer.Broker.RegisterTerminalUnique(identityName, out string resolved, docId, isTeamLead);
                    if (uniqueResult.Success)
                        return resolved;
                }
                else
                {
                    var result = _mcpServer.Broker.RegisterTerminal(identityName, docId, isTeamLead);
                    if (result.Success)
                        return identityName;
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Pre-registration with name failed: {ex.Message}");
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
        private async Task<bool> WaitForRendererReadyAsync(TerminalDocument doc, int timeoutMs = 5000)
        {
            // Check if already ready (event may have already fired)
            if (doc.IsRendererReady)
            {
                return true;
            }

            var tcs = new TaskCompletionSource<bool>();

            void rendererReadyHandler(object s, EventArgs e)
            {
                doc.RendererReady -= rendererReadyHandler;
                tcs.TrySetResult(true);
            }

            doc.RendererReady += rendererReadyHandler;

            var delayTask = Task.Delay(timeoutMs);
            var winner = await Task.WhenAny(tcs.Task, delayTask);
            if (winner == delayTask)
            {
                doc.RendererReady -= rendererReadyHandler;
                return false;
            }

            return true;
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
            _debugLogService?.Trace("OnTerminalDirectoryChanged", $"Directory changed to: {e.Directory}");
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
                        _debugLogService?.Trace("OnTerminalDirectoryChanged", "Refreshing project panel...");
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
            _debugLogService?.Trace("MainForm", $"OnProjectFileChanged received from {sender}");
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

            _debugLogService?.Trace("MainForm", "Registry changed externally - refreshing project panel and tasks panel");
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

            _debugLogService?.Trace("MainForm", "Claude Code detected in terminal");

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

            // Auto-initialize Claude Code by injecting "initializing..." to trigger startup hooks.
            // Uses TypeInput (atomic xterm.js character typing + Enter) instead of the old
            // two-step InjectInputAsync (ConPTY write + separate JS Enter) which caused
            // double-injection when the Enter key failed and the retry wrote text again.
            if (doc != null && doc.IsRendererReady)
            {
                // Wait for Claude Code to finish showing its banner and prompt before injecting.
                // Detection fires when "Claude Code" appears in output, but the input prompt
                // may not be ready yet. Wait 1.5s for the prompt to appear.
                await Task.Delay(1500);
                _debugLogService?.Trace("MainForm", "Post-detection delay complete, injecting 'initializing...' via TypeInput");
                doc.TypeInput("initializing...", "cr", 20);
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
                _debugLogService?.Error("MainForm", $"OnActiveDocumentChanged error: {ex.Message}");
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

            // AC7 launch-root strategy (task c6ed236c): same logic as AddNewTerminal —
            // when this identity owns an active task with a materialized worktree, spawn
            // at the project repo root and let the in-shell cd narrow to the worktree.
            // Without this, "Launch as..." preserves the prior terminal's cwd and the
            // env var stays empty — bypassing the wiring fix from AddNewTerminal.
            workingDirectory = ResolveSpawnDir(terminalName, workingDirectory, out string taskWorktreePath);

            // Just launch claude - let the user choose to resume or start fresh
            // Using plain "claude" lets Claude prompt about resuming recent sessions
            // TODO: Make --dangerously-skip-permissions configurable in settings
            string pluginDir = LaunchCommandBuilder.GetMtPluginPath();
            string pluginFlag = pluginDir != null ? $" --plugin-dir '{pluginDir.Replace("'", "''")}'" : "";
            string autoRunCommand = $"claude --dangerously-skip-permissions{pluginFlag}";

            // Stop current terminal and restart with new identity
            doc.Terminal.Stop();
            doc.CustomTitle = terminalName;
            doc.StartTerminal(workingDirectory, terminalName, autoRunCommand, taskWorktreePath: taskWorktreePath);

            // Auto-initialization is handled by OnClaudeCodeDetected when Claude Code's output is detected.
            // This ensures injection happens AFTER Claude Code is ready, not just after WebView2 loads.
            // The _claudeCodeDetectedThisSession flag is reset in TerminalControl.DoStart() so the event fires for restarted terminals.
            _debugLogService?.Trace("MainForm.OnLaunchAsIdentityRequested", $"Terminal restarted for {terminalName}, waiting for Claude Code detection to auto-inject");
        }

        private bool _sessionSaveCompleted;
        private bool _exitConfirmDialogOpen;

        private async void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // Confirm a genuine user-initiated close (title-bar X, Alt+F4, or the window's
            // system-menu Close \u2014 all reported as CloseReason.UserClosing) so an accidental
            // click doesn't quit the app. UserClosing also keeps OS shutdown / Task Manager
            // (which we can't reliably block) from getting a confirmation dialog.
            //
            // Two guards:
            //  - _sessionSaveCompleted: the session-save path below cancels and re-triggers
            //    Close() internally; that second pass must not prompt again.
            //  - _exitConfirmDialogOpen: the modal MessageBox runs a nested message pump, so
            //    a second close gesture (double-click the X, taskbar Close, Alt+F4) can
            //    re-enter this handler while the dialog is still up. Swallow that re-entrant
            //    close so we don't stack a second dialog or race the teardown.
            if (e.CloseReason == CloseReason.UserClosing && !_sessionSaveCompleted)
            {
                if (_exitConfirmDialogOpen)
                {
                    // A confirmation is already showing \u2014 ignore this duplicate close request.
                    e.Cancel = true;
                    return;
                }

                DialogResult confirm;
                _exitConfirmDialogOpen = true;
                try
                {
                    confirm = MessageBox.Show(
                        this,
                        "Are you sure you want to exit MultiTerminal?",
                        "Exit MultiTerminal",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2); // default to No \u2014 accidental Enter won't exit
                }
                finally
                {
                    _exitConfirmDialogOpen = false;
                }

                if (confirm != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // Stop the worktree janitor before any other shutdown work \u2014 its
            // timer thread should not fire mid-shutdown.
            try { _worktreeJanitorTimer?.Dispose(); _worktreeJanitorTimer = null; }
            catch (Exception ex) { _debugLogService?.Info("MainForm", $"Janitor timer dispose: {ex.Message}"); }

            try { _idleRemoteModeTimer?.Dispose(); _idleRemoteModeTimer = null; }
            catch (Exception ex) { _debugLogService?.Info("MainForm", $"Idle remote-mode timer dispose: {ex.Message}"); }

            // Close any live Code Review popups so their bounds/zoom get flushed
            // to SettingsService. The CloseAll call is idempotent \u2014 safe even if
            // a popup has already been closed manually.
            try { Dialogs.CodeReviewPopupManager.CloseAll(); }
            catch (Exception ex) { _debugLogService?.Info("MainForm", $"Code Review popup CloseAll: {ex.Message}"); }

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

                    // Flush session memory — index any completed sessions that haven't been embedded yet
                    var sessionMemDb = _mcpServer?.Broker?.SessionMemoryDb;
                    if (sessionMemDb != null)
                    {
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            var flushedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var doc in terminals)
                            {
                                string projectPath = doc.GetWorkingDirectory();
                                if (!string.IsNullOrEmpty(projectPath) && flushedPaths.Add(projectPath))
                                {
                                    try
                                    {
                                        sessionMemDb.IndexProjectSessions(projectPath, doc.CustomTitle);
                                    }
                                    catch (Exception ex)
                                    {
                                        _debugLogService?.Error("MainForm", $"Session memory flush failed for {projectPath}: {ex.Message}");
                                    }
                                }
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _debugLogService?.Error("MainForm", $"Session save failed: {ex.Message}");
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
                _debugLogService?.Error("MultiTerminal", $"Failed to save layout: {ex.Message}");
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

            // Oracle is an OracleService-owned singleton (not a _xxxPanel field), but she's a
            // DockContent now — persist her dock side here too so EnsureVisible() can restore it
            // on the no-XML restore path (zero saved terminals), where LoadFromXml never runs.
            SavePanelState("OraclePanel", _oracleService?.DockContent as DockContent);

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
                    _debugLogService?.Error("MultiTerminal", $"Failed to set profiles offline: {ex.Message}");
                }
            }

            // --- Fast shutdown: fire-and-forget blocking waits, kill without waiting ---
            // The Environment.Exit failsafe at the end ensures we never hang.

            // Start the failsafe timer FIRST — everything below is best-effort cleanup.
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Environment.Exit(0);
            });

            // Stop HTTP webhook service (cancel, don't wait for listener task)
            try
            {
                _webhookService?.Stop();
                _webhookService?.Dispose();
            }
            catch { }

            // Stop Oracle advisory agent and digest timer
            try
            {
                _oracleDigestTimer?.Stop();
                _oracleDigestTimer?.Dispose();
                _oracleService?.Shutdown();
                _oracleService?.Dispose();
            }
            catch { }

            // Kill all agent processes (Kill is fast, no WaitForExit needed)
            lock (_agentProcessMap)
            {
                foreach (var kvp in _agentProcessMap)
                {
                    try { kvp.Value.Dispose(); } catch { }
                }
                _agentProcessMap.Clear();
            }

            // Kill companion processes — fire-and-forget, no WaitForExit
            _ = Task.Run(() =>
            {
                try { _companionManager?.StopAll(); } catch { }
            });

            // Stop MCP server — fire-and-forget, no blocking .Wait()
            if (_mcpServer != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _mcpServer.StopAsync(); } catch { }
                });
            }

            // Stop the MultiRemote gateway — fire-and-forget, same pattern as the REST host
            if (_remoteGateway != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _remoteGateway.StopAsync(); } catch { }
                });
            }

            // Close all terminals (sends kill signal, doesn't wait for process exit)
            foreach (var doc in _gridManager.GetTerminalDocuments())
            {
                try { doc.Terminal?.Stop(); } catch { }
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
        }

        private void DisposePanel(DockContent panel)
        {
            if (panel == null || panel.IsDisposed) return;
            try { panel.Dispose(); }
            catch (Exception ex)
            {
                _debugLogService?.Error("MultiTerminal", $"Failed to dispose panel: {ex.Message}");
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
            _oracleService?.ApplyTheme(isDark);
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
                _debugLogService?.Trace("RestoreSession", $"Restored {panelKey} at {dockState}");
                return true;
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("RestoreSession", $"Failed to restore {panelKey}: {ex.Message}");
                return false;
            }
        }

        private void RestoreSession()
        {
            _debugLogService?.Trace("RestoreSession", "Starting...");

            _pendingTerminalSessions = _settings.GetSessionTerminals();
            int terminalCount = _pendingTerminalSessions?.Count ?? 0;
            _debugLogService?.Trace("RestoreSession", $"Session terminals: {terminalCount}");

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
                    _debugLogService?.Trace("RestoreSession", "Restored layout from XML");

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
                    _debugLogService?.Error("RestoreSession", $"XML restore failed, falling back to manual: {ex.Message}");
                    restoredFromXml = false;
                }
            }

            // Fallback: manual restore from session data
            if (!restoredFromXml)
            {
                if (terminalCount == 0)
                {
                    _debugLogService?.Trace("RestoreSession", "No session data, creating default terminal...");
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
                        _debugLogService?.Trace("RestoreSession", $"Applied preset {preset} for {terminals.Count} terminals");
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
            _debugLogService?.Trace("RestoreSession", "Calling RefreshProjectPanel...");
            RefreshProjectPanel();
            _debugLogService?.Trace("RestoreSession", "RefreshProjectPanel returned");

            // Ensure Oracle is visible on launch. If the saved layout already restored her
            // dock/float position (via the "Oracle" factory case above), this is a no-op;
            // otherwise (first run, zero-terminal relaunch, or a layout saved before Oracle was
            // dockable) show her at her settings-persisted dock side, falling back to Float.
            _oracleService?.EnsureVisible(GetSavedDockState("OraclePanel", DockState.Float));

            // Signal loading complete (splash will show main form when animation finishes)
            _debugLogService?.Trace("RestoreSession", "Invoking LoadingComplete event...");
            LoadingComplete?.Invoke(this, EventArgs.Empty);
            _debugLogService?.Trace("RestoreSession", "LoadingComplete event invoked");

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
                    _debugLogService?.Error("MainForm", $"Session indexing error: {ex.Message}");
                }
            });

            // Start message queue polling timer (every 2 seconds)
            _messageQueueTimer = new System.Windows.Forms.Timer();
            _messageQueueTimer.Interval = 2000; // 2 seconds
            _messageQueueTimer.Tick += OnMessageQueueTimerTick;
            _messageQueueTimer.Start();
            _debugLogService?.Info("MainForm", "Message queue timer started (2 second interval)");

            // Start session sync timer — imports completed JSONL sessions into session_lineage (every 30 seconds)
            _sessionSyncTimer = new System.Windows.Forms.Timer();
            _sessionSyncTimer.Interval = 30000; // 30 seconds
            _sessionSyncTimer.Tick += OnSessionSyncTimerTick;
            _sessionSyncTimer.Start();
            _debugLogService?.Info("MainForm", "Session sync timer started (30 second interval)");
        }

        /// <summary>
        /// Periodic session sync handler - imports new sessions to SessionLineageService (multiterminal.db).
        /// </summary>
        private void OnSessionSyncTimerTick(object sender, EventArgs e)
        {
            // Skip if already indexing
            if (_sessionIndexingService?.IsIndexing == true)
            {
                _debugLogService?.Warning("MainForm", "Session sync skipped - indexing already in progress");
                return;
            }

            var lineageService = _mcpServer?.Broker?.SessionLineageService;
            if (lineageService == null)
            {
                _debugLogService?.Warning("MainForm", "Session sync skipped - SessionLineageService not available");
                return;
            }

            _debugLogService?.Trace("MainForm", "Session sync timer triggered - starting background sync");

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
                    {
                        debugLog?.Info("MainForm", $"Session sync (periodic): {totalImported} imported, {totalSkipped} skipped, {totalFailed} failed across {projects.Count} projects");
                        // Notify HUD Sessions tab to refresh
                        _mcpServer?.Broker?.FireSessionLineageUpdated(null);
                    }
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

            _debugLogService?.Trace("MainForm", $"OnMessageQueueTimerTick ENTRY (2s timer fired)");

            try
            {
                // Process pending messages (retry delivery)
                int delivered = await _mcpServer.Broker.ProcessPendingMessages();
                if (delivered > 0)
                {
                    _debugLogService?.Trace("MainForm", $"Message queue: {delivered} pending messages delivered");
                }

                // Periodic cleanup of old messages (once per hour)
                _messageQueueCleanupCounter++;
                if (_messageQueueCleanupCounter >= CleanupIntervalTicks)
                {
                    _messageQueueCleanupCounter = 0;
                    int cleaned = _mcpServer.Broker.CleanupOldMessages(24);
                    if (cleaned > 0)
                    {
                        _debugLogService?.Trace("MainForm", $"Message queue: cleaned up {cleaned} old messages");
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Message queue timer error: {ex.Message}");
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

            // Oracle: re-bind a saved dock/float position to the live always-on singleton
            // (created in InitializeOracle before this restore runs). Returns null if Oracle
            // failed to initialize, in which case DockPanelSuite skips the saved entry.
            if (persistString == "Oracle")
            {
                return _oracleService?.DockContent;
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
                        _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService, _settings);
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
                        _inboxPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.DefaultInboxRecipient);
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
            var control = new AgentPanelControl { Dock = DockStyle.Fill, DebugLogService = _debugLogService };
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
                    _debugLogService?.Info("MainForm", $"Native team removed: {e.TeamName} (members: {string.Join(", ", e.MemberNames)})");

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
                _debugLogService?.Info("MainForm", $"TeamWatcherService started (slug: {projectSlug})");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Error initializing TeamWatcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize and start the Oracle advisory agent.
        /// Oracle is always-on: launches with MultiTerminal in a dockable/floatable terminal,
        /// auto-restarts on crash, only shuts down when MT closes. Her dock/float position is
        /// persisted across sessions. Clicking Oracle in the dashboard header activates her.
        /// </summary>
        private bool _oracleBootstrapped; // Guard: only send digest bootstrap once per app session
        private System.Windows.Forms.Timer _oracleDigestTimer; // Recurring digest trigger

        private void InitializeOracle()
        {
            try
            {
                _oracleService = new OracleService(
                    log: (source, msg) => _debugLogService?.Info(source, msg),
                    debugLogService: _debugLogService);

                // Start Oracle first so it generates its docId. Pass the dock panel so Oracle
                // is hosted as a dockable/floatable DockContent. The form is created here
                // (before RestoreSession's LoadFromXml) so the layout-restore factory can
                // re-bind a saved "Oracle" position to this live instance.
                _oracleService.Start(_dockPanel);

                // Register Oracle terminal with the same docId so broker and ConPTY are in sync
                _mcpServer?.Broker?.RegisterTerminal(OracleService.OracleName, _oracleService.DocId);

                // Apply user's font size and theme to Oracle's terminal
                float oracleFontSize = _settings?.GetTerminalFontSize() ?? 10f;
                _oracleService.SetFontSize(oracleFontSize);
                _oracleService.ApplyTheme(_currentTheme.IsDark);

                // Start recurring digest timer (every 2 hours)
                _oracleDigestTimer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 60 * 1000 };
                _oracleDigestTimer.Tick += OnOracleDigestTimerTick;
                _oracleDigestTimer.Start();

                _debugLogService?.Info("MainForm", $"Oracle started (always-on, dockable/floatable, docId={_oracleService.DocId}, digest every 2h)");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Failed to initialize Oracle: {ex.Message}");
            }
        }

        private async void OnOracleDigestTimerTick(object sender, EventArgs e)
        {
            var oracleTerminal = _mcpServer?.Broker?.GetTerminal(OracleService.OracleName);
            if (oracleTerminal == null || !oracleTerminal.ChannelPort.HasValue)
            {
                _debugLogService?.Warning("MainForm", "Oracle digest timer: Oracle not connected or no channel port");
                return;
            }

            try
            {
                string digestMessage = "Scheduled digest check: run the /daily-intel skill to process the digest pipeline. Do NOT call get_daily_digest directly — you MUST use /daily-intel.";
                bool delivered = await DeliverViaChannel(oracleTerminal.ChannelPort.Value, "System", digestMessage, "normal");
                _debugLogService?.Info("MainForm", $"Oracle scheduled digest {(delivered ? "delivered" : "failed")}");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("MainForm", $"Oracle digest timer failed: {ex.Message}");
            }
        }

        // Oracle terminal management removed — OracleService now owns the terminal
        // via OracleTerminalForm (always-on, hidden popup, auto-restart)

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
                    _tasksPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.ActivityService, _settings);
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
                    _inboxPanel.Initialize(_mcpServer.Broker, _mcpServer.Broker.DefaultInboxRecipient);
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
            _debugLogService?.Trace("RefreshProjectPanel", $"Called by: {caller}");
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
                _debugLogService?.Error("MainForm", $"OnMcpJsonWriteRequested error: {ex.Message}");
                _projectPanel?.NotifyMcpJsonWriteResult(false, ex.Message);
            }
        }

        private void OnProjectLaunchRequested(object sender, ProjectSelectedEventArgs e)
        {
            if (e.Project == null) return;

            // Resolve terminal kind. Explicit override from the split-button dropdown
            // wins; otherwise fall back to the project's stored default (normalized).
            var kind = !string.IsNullOrEmpty(e.TerminalKindOverride)
                ? Models.TerminalKindHelper.ParseOrDefault(e.TerminalKindOverride)
                : Models.TerminalKindHelper.ParseOrDefault(e.Project.DefaultTerminal);

            // Validate working directory (project source > legacy path > user profile),
            // rejecting UNC paths and non-existent dirs like the prior dialog-based flow.
            string rawPath = !string.IsNullOrEmpty(e.Project.SourcePath) ? e.Project.SourcePath : e.Project.Path;
            string workingDir = (!string.IsNullOrEmpty(rawPath) && Path.IsPathRooted(rawPath)
                                 && !rawPath.StartsWith("\\\\") && Directory.Exists(rawPath))
                ? rawPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Build the CLI launch command for the chosen kind. BuildCommand also
            // refreshes ~/.codex/config.toml and writes the Codex launcher scaffolding
            // when kind == Codex (side-effect inside BuildCodexCommand).
            var launchCmd = Services.LaunchCommandBuilder.BuildCommand(kind, e.Project);

            if (HandleBootstrapErrorIfAny(launchCmd))
                return;

            // Identity: team-lead if set; for Codex, use configured default via
            // ResolveCodexIdentityName (applies GetUniqueNameFor). For non-team-lead
            // Claude the resolver returns null and we leave identityName null — the
            // downstream AddNewTerminal handles that by defaulting to "Unassigned".
            bool isTeamLead = !string.IsNullOrEmpty(e.Project.TeamLead);
            string identityName = ResolveCodexIdentityName(kind, e.Project);

            // Resolve gateway profile for per-project MCP server filtering.
            string gatewayProfile = null;
            if (_gatewayService != null && _gatewayService.IsGatewayInstalled())
                gatewayProfile = Services.GatewayIntegrationService.GetGatewayProfileName(e.Project.Name);

            // Ensure the global .mcp.json is up to date (source of truth for both
            // Claude's --mcp-config and Codex's config.toml translation).
            //
            // Guard: only run project-scoped MCP sync when workingDir is the real
            // project root. If we fell back to %USERPROFILE% because the project
            // path was missing/invalid, McpConfigService.WriteMcpJsonToProject
            // would treat ~/ as the project root and could mutate/delete the
            // user's personal ~/.mcp.json. Mirrors the AGENTS.md guard in
            // LaunchCommandBuilder.BuildCodexCommand.
            if (Services.LaunchCommandBuilder.IsDistinctProjectRoot(e.Project, workingDir))
            {
                try
                {
                    _mcpConfigService?.EnsureMcpConfigsForProjectWithGateway(e.Project.Id, workingDir, e.Project.Name);
                }
                catch (Exception ex)
                {
                    _debugLogService?.Error("ProjectPanel", $"MCP config sync failed: {ex.Message}");
                }
            }
            else
            {
                _debugLogService?.Warning("ProjectPanel", $"MCP config sync skipped — workingDir is the user-profile fallback, not a distinct project root.");
            }

            // Atomic uniqueness for Codex non-team-lead launches so two concurrent
            // launches with the same per-user default-agent name can't alias to
            // one broker identity.
            bool atomicIdentityUniqueness = kind == Models.TerminalKind.Codex && !isTeamLead;

            AddNewTerminal(
                workingDirectory: workingDir,
                identityName: identityName,
                autoRunCommand: launchCmd.AutoRunCommand,
                projectId: e.Project.Id,
                isTeamLead: isTeamLead,
                gatewayProfile: gatewayProfile,
                atomicIdentityUniqueness: atomicIdentityUniqueness);

            _currentProject = e.Project;
            _projectService?.MarkProjectOpened(e.Project.Id);
            _projectPanel?.RefreshForProject(e.Project);
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

        /// <summary>
        /// Handles the projects-pane delete button. Confirms, then routes through the canonical
        /// broker delete (which unregisters the project — firing ProjectRemoved so the code-graph
        /// watcher drops it — evicts its code-graph rows, and records activity), then refreshes the
        /// pane. The on-disk .claude/project.json is kept; tasks are not deleted.
        /// </summary>
        private void OnProjectPanelDeleteRequested(object sender, string projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;

            var broker = _mcpServer?.Broker;
            if (broker == null) return;

            string name = broker.GetProjectsList()?.FirstOrDefault(p => p.Id == projectId)?.Name ?? projectId;

            var confirm = MessageBox.Show(
                this,
                $"Delete project '{name}' from MultiTerminal?\n\nThe project is unregistered and its code-graph data removed. The .claude/project.json file on disk is kept, and tasks are not deleted.",
                "Delete Project",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            var result = broker.DeleteProject(projectId, "ui", deleteLocalConfig: false);
            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.Error ?? "Failed to delete project.",
                    "Delete Project",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _projectPanel?.RefreshAfterProjectDeleted(projectId);
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
                _oracleDigestTimer?.Stop();
                _oracleDigestTimer?.Dispose();
                _oracleService?.Dispose();
                _webhookService?.Dispose();
                _projectService?.Dispose();

                _messageQueueTimer?.Stop();
                _messageQueueTimer?.Dispose();
                _sessionSyncTimer?.Stop();
                _sessionSyncTimer?.Dispose();
                _teamWatcher?.Dispose();
                _codeGraphWatcher?.Dispose();
                // Dispose the coordinator after the watcher that uses it, before the DB it indexes.
                _mcpServer?.Broker?.CodeGraphIndexCoordinator?.Dispose();
                _sessionIndexingService?.Dispose();
                _chatTaskDatabase?.Dispose();
                _sharedProjectDatabase?.Dispose();
                _gatewayService?.Dispose();
                _debugLogService?.Dispose();
                // bb2b0104: each of these now owns its own multiterminal.db connection — dispose them.
                // CodeGraphDb is disposed after its watcher (_codeGraphWatcher) and coordinator above.
                _mcpServer?.Broker?.SessionMemoryDb?.Dispose();
                _mcpServer?.Broker?.KnowledgeDb?.Dispose();
                _mcpServer?.Broker?.CodeGraphDb?.Dispose();
                _mcpServer?.Broker?.BranchMetadata?.Dispose();
                _ownerProfileService?.Dispose();
                _sourceControlAccountService?.Dispose();
                // MCP server already stopped in OnFormClosing (fire-and-forget StopAsync).
                // Calling Dispose here would invoke StopAsync().GetAwaiter().GetResult()
                // a second time, risking a UI-thread deadlock. Skip it — the process is exiting.
                _mcpServer = null;
                // Gateway likewise already StopAsync'd in OnFormClosing; null it rather than
                // block the UI thread on DisposeAsync (same rationale as _mcpServer above).
                _remoteGateway = null;
                _gatewayRestartLock?.Dispose();
                _dockPanel?.Dispose();
                _dashboardHeader?.Dispose();
                _toolStrip?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
