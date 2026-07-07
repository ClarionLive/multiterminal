using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using MultiTerminal.Controls;
using MultiTerminal.Controls.Shared;
using MultiTerminal.Dialogs;
using MultiTerminal.Terminal;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.TasksPanel;
using MultiTerminal.Services;
using MultiTerminal.StartScreen;
using Microsoft.Web.WebView2.WinForms;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// DockContent wrapper for a terminal control.
    /// Represents a single terminal instance in the docking framework.
    /// </summary>
    public class TerminalDocument : DockContent
    {
        // Controls nested inside inner container panels (_dropZone, _hudTabContainer, _terminalAgentSplitter);
        // disposed transitively via the respective container's Dispose() in the override below, or via
        // base DockContent.Controls for top-level children.
#pragma warning disable CA2213
        private TerminalControl _terminal;
        private TerminalStatusBarRenderer _statusBar;
        private Splitter _statusBarSplitter;
        private Panel _dropZone;
        private Label _dropZoneLabel;
        private HudTabContainer _hudTabContainer;
        private HudDashboardRenderer _hudDashboard;
        private HudNotesRenderer _hudNotes;
        private HudKnowledgeRenderer _hudKnowledge;
        private HudGitRenderer _hudGit;
        private HudSessionsRenderer _hudSessions;
        private SplitContainer _terminalAgentSplitter;
        private SplitContainer _terminalHudSplitter;
        private EmbeddedAgentPanel _embeddedAgentPanel;
#pragma warning restore CA2213
        private System.Windows.Forms.Timer _agentPanelSanityTimer;
        private MessageBroker _messageBroker;
        // Stable agent identity for broker-event filtering. Survives tab
        // renames (cycle-5 fix). Cycle-7: ONLY set via PromoteOriginalAgentName
        // from authoritative identity sources — StartTerminal (live launch
        // terminalName) and MainForm.OnMcpTerminalRegistered (broker-confirmed
        // registration). The CustomTitle setter no longer seeds this, because
        // restored sessionInfo.CustomTitle could otherwise lock a stale title
        // as the stable broker identity (codex-cross-model-adversary HIGH
        // Run 3 fix).
        private string _originalAgentName;
        // Latch so the empty-name diagnostic Trace fires at most once per
        // terminal lifetime instead of per-event. Volatile because broker
        // events can fire concurrently from broker threads; without it, two
        // simultaneous fires could race past the !_loggedEmptyAgentName check
        // and log twice (cycle-7 debugger LOW NIT, logging-only correctness).
        private volatile bool _loggedEmptyAgentName;
        private MultiTerminal.Services.CodeReviewService _codeReviewService;
        private DebugLogService _debugLogService;
        private static int _instanceCount = 0;
        private readonly string _docId = Guid.NewGuid().ToString("N").Substring(0, 8);

        // Wall-clock (Unix ms) this TerminalDocument instance was constructed. Used as
        // the freshness floor for the statusline name-glob fallback (task 1ba59334): a
        // terminal must never adopt a statusline file written by a PRIOR run / a foreign
        // same-named terminal. A legitimately restored MT child keeps writing fresh files
        // (timestamp > this floor) so it's still picked up; only stale prior-run files
        // (timestamp < this floor) are rejected, so the banner shows a fresh blank
        // placeholder instead of a frozen, misleading value. Constructed fresh per
        // instance (incl. on restore/re-adopt), so it tracks this terminal's own launch.
        private readonly long _launchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Grace window (ms) for the statusline freshness floor: a fallback file timestamped up
        // to this far BEFORE launch is still adopted, so a restored terminal whose child renders
        // just before (or doesn't re-render right after) an MT restart keeps its last-known data
        // rather than going permanently blank. Far smaller than a typical prior-run gap (hours),
        // so genuinely stale files are still rejected (task 1ba59334).
        private const long StatuslineFreshnessGraceMs = 5 * 60 * 1000;

        // Token meter (task f2702f69): last session this terminal fed, so the shared singleton meter
        // can drop the session's accumulated state (accumulator + all file-tail offsets) on close.
        private string _lastTokenSessionId;

        // Diff popup saved bounds and zoom (persisted across sessions)
        private static Rectangle? _diffPopupBounds;
        private static double _diffPopupZoom = 1.0;
        private static bool _diffPopupSettingsLoaded;

        // Start screen — shown before a terminal shell is started
        private StartScreenControl _startScreen;
        private bool _isStartScreenVisible;

        /// <summary>
        /// Gets the unique document ID for this terminal.
        /// Used for MCP push notification mapping.
        /// </summary>
        public string DocId => _docId;

        /// <summary>
        /// The broker-confirmed, stable agent identity for this terminal (set once via
        /// <see cref="PromoteOriginalAgentName"/> from an authoritative source). Empty
        /// until the terminal's identity is confirmed. Exposed so the registration
        /// handler can detect a cross-wire — a registration resolving to a document
        /// already bound to a different identity — and refuse to clobber it.
        /// </summary>
        public string OriginalAgentName => _originalAgentName;

        /// <summary>
        /// Gets the underlying terminal control.
        /// </summary>
        public TerminalControl Terminal => _terminal;

        /// <summary>
        /// Gets the embedded agent panel on the right side of the terminal.
        /// </summary>
        public EmbeddedAgentPanel EmbeddedAgentPanel => _embeddedAgentPanel;

        /// <summary>
        /// Gets the unique instance ID.
        /// </summary>
        public int InstanceId { get; }

        /// <summary>
        /// Gets whether the terminal renderer is initialized and ready for input.
        /// </summary>
        public bool IsRendererReady => _terminal?.IsRendererReady ?? false;

        /// <summary>
        /// Event fired when the terminal process exits.
        /// </summary>
        public event EventHandler TerminalExited;

        /// <summary>
        /// Event fired when user requests to save selected text as a prompt.
        /// </summary>
        public event EventHandler<SavePromptRequestEventArgs> SaveAsPromptRequested;

        /// <summary>
        /// Event fired when the terminal's working directory changes.
        /// </summary>
        public event EventHandler<DirectoryChangedEventArgs> DirectoryChanged;

        /// <summary>
        /// Event fired when the terminal is fully initialized and ready.
        /// </summary>
        public event EventHandler TerminalReady;

        /// <summary>
        /// Event fired when project.json is created or modified in the terminal's working directory.
        /// </summary>
        public event EventHandler ProjectFileChanged;

        /// <summary>
        /// Event fired when the WebView2/xterm.js renderer is initialized (before shell starts).
        /// </summary>
        public event EventHandler RendererReady;

        /// <summary>
        /// Event fired when Claude Code is detected running in this terminal.
        /// </summary>
        public event EventHandler ClaudeCodeDetected;

        /// <summary>
        /// Event fired when a kanban task card is dropped onto this terminal.
        /// </summary>
        public event EventHandler<TaskDroppedEventArgs> TaskDropped;

        /// <summary>
        /// Event fired when user requests to launch a new terminal with a specific identity.
        /// </summary>
        public event EventHandler<LaunchAsIdentityEventArgs> LaunchAsIdentityRequested;

        /// <summary>
        /// Forwarded from StartScreenControl: user clicked a project card to launch.
        /// MainForm wires this to build the Claude Code command and call StartTerminal().
        /// </summary>
        public event EventHandler<StartScreenLaunchEventArgs> ProjectLaunched;

        /// <summary>
        /// Forwarded from StartScreenControl: user clicked "Open PowerShell" (no project).
        /// MainForm calls StartTerminal() with no project context.
        /// </summary>
        public event EventHandler StartScreenOpenPowerShellRequested;

        /// <summary>
        /// Forwarded from StartScreenControl: user clicked "New Project".
        /// MainForm opens the EditProjectDialog.
        /// </summary>
        public event EventHandler StartScreenNewProjectRequested;

        /// <summary>
        /// Forwarded from StartScreenControl: user clicked "Just Claude".
        /// MainForm launches Claude Code with no project, no skills, just MT config flags.
        /// </summary>
        public event EventHandler StartScreenJustClaudeRequested;

        /// <summary>
        /// Forwarded from StartScreenControl: user clicked a project card's trash button.
        /// Carries the project ID. MainForm confirms and runs the canonical broker delete,
        /// then calls <see cref="RefreshStartScreenProjects"/> to update the card grid.
        /// </summary>
        public event EventHandler<string> StartScreenProjectDeleteRequested;

        /// <summary>
        /// Function to retrieve available identity names for the "Launch as..." menu.
        /// Set by MainForm to provide identity list.
        /// </summary>
        public Func<string[]> GetAvailableIdentities { get; set; }

        // Split ratios for splitter containers (global, applied from settings)
        private double _agentSplitRatio = 0.75;
        private double _hudSplitRatio = 0.60;
        private bool _suppressSplitterEvents;
        private bool _isDisposing;

        // Status line polling timer — reads temp file written by Claude Code statusline script
        private System.Threading.Timer _statusLineTimer;
        private string _lastStatusLineContent;
        private string _lastSharedQuotaContent;

        // Working-tree dirty poll — GitRepoWatcher only watches .git/, so plain
        // working-tree edits (modify, no stage) never fire RepoStateChanged.
        // This timer ticks on a slow cadence and refreshes the HUD's
        // uncommitted-changes strip + Git tab so silent dirt never accumulates.
        private System.Windows.Forms.Timer _workingTreeDirtyTimer;
        private bool _workingTreeRefreshInFlight;
        // Last folder pushed to the HUD Dashboard — lets polling detect real cwd drift
        // and re-push so the HUD header tracks Claude Code's workspace instead of the
        // stale launch-dir that came from the global _settings.GetLastDirectory() fallback.
        private string _hudDispatchedFolder;
        // Canonical project path the Notes/Sessions HUD panes are keyed to. These
        // panes are per-PROJECT (not per-folder), so they must NOT follow the
        // worktree the way _hudDispatchedFolder does — a worktree path has no
        // notes and would render empty (and pollute project_note_tabs with an
        // auto-created empty "General" row). Resolved to the registered project
        // path at launch and only ever upgraded (never downgraded to a raw
        // worktree path) by the StatusLinePoll folder correction.
        private string _hudNotesProjectKey;

        // Track the last known working directory for session persistence
        private string _lastKnownDirectory;
        // The directory passed to StartTerminal — authoritative project path
        private string _startingWorkingDirectory;

        // FileSystemWatcher to detect project.json creation/changes
        private FileSystemWatcher _projectFileWatcher;
        private string _lastWatchedDirectory;

        // Custom user-defined title (overrides auto title from working directory)
        private string _customTitle;

        // Project name for display in tab title (e.g., "Alice - MultiTerminal")
        private string _projectName;

        // For session restore: working directory to use when starting terminal
        private string _pendingWorkingDirectory;

        // Track whether the terminal shell has been started
        private bool _isTerminalStarted;

        /// <summary>
        /// Gets or sets the pending working directory for session restore.
        /// Set this before the terminal is started during layout restoration.
        /// </summary>
        public string PendingWorkingDirectory
        {
            get => _pendingWorkingDirectory;
            set => _pendingWorkingDirectory = value;
        }

        /// <summary>
        /// Gets whether the terminal shell has been started.
        /// </summary>
        public bool IsTerminalStarted => _isTerminalStarted;

        // Tab header context menu
        private ContextMenuStrip _tabContextMenu;
        private TerminalTheme _currentTheme;

        // Terminal content context menu (for dismissing on click)
#pragma warning disable CA2213 // Borrowed reference — we track the currently-shown menu to Close() it; we do not own its lifecycle.
        private ContextMenuStrip _currentContextMenu;
#pragma warning restore CA2213

        /// <summary>
        /// Gets or sets a custom user-defined title for the tab.
        /// When set, this overrides the automatic title from the working directory.
        /// </summary>
        public string CustomTitle
        {
            get => _customTitle;
            set
            {
                _debugLogService?.Trace("TerminalDocument", $"#PROJ# [TerminalDocument.CustomTitle.set] Instance={InstanceId} DocId='{_docId}' old='{_customTitle}' new='{value}' _projectName='{_projectName}'");
                _customTitle = value;
                // NOTE: cycle-7 removed the setter-side promotion of
                // _originalAgentName — restored sessionInfo.CustomTitle (set
                // here pre-launch) could otherwise lock a stale title as the
                // stable broker identity (codex-cross-model-adversary HIGH
                // Run 3). The promotion now happens only via the dedicated
                // PromoteOriginalAgentName method called from authoritative
                // identity sources: StartTerminal (live launch) and MainForm's
                // OnMcpTerminalRegistered (broker-confirmed registration).
                UpdateTabTitle();
                // Start polling for status line data once we know the terminal name
                StartStatusLinePolling();
            }
        }

        /// <summary>
        /// Promotes <paramref name="authoritativeAgentName"/> into
        /// <c>_originalAgentName</c> if it isn't already set. Called from
        /// authoritative identity sources only — <see cref="StartTerminal"/>
        /// (live launch with its terminalName parameter) and MainForm's
        /// <c>OnMcpTerminalRegistered</c> (broker-confirmed name). NOT called
        /// from the <see cref="CustomTitle"/> setter, because that setter is
        /// also reachable via session-restore (`doc.CustomTitle = sessionInfo.CustomTitle`)
        /// where the value is persisted UI state rather than a freshly-confirmed
        /// broker identity. First authoritative value wins; subsequent calls
        /// are no-ops so this preserves the cycle-5 stable-identity contract
        /// across tab renames AND late-arriving registrations.
        /// </summary>
        public void PromoteOriginalAgentName(string authoritativeAgentName)
        {
            if (string.IsNullOrEmpty(authoritativeAgentName)) return;
            if (!string.IsNullOrEmpty(_originalAgentName)) return;
            _originalAgentName = authoritativeAgentName;
            _debugLogService?.Trace("TerminalDocument.PromoteOriginalAgentName", $"DocId='{_docId}' set _originalAgentName='{authoritativeAgentName}'.");
        }

        private void UpdateTabTitle()
        {
            if (!string.IsNullOrEmpty(_customTitle))
            {
                var title = !string.IsNullOrEmpty(_projectName)
                    ? $"{_customTitle} - {_projectName}"
                    : _customTitle;
                _debugLogService?.Trace("TerminalDocument", $"#PROJ# [TerminalDocument.UpdateTabTitle] Instance={InstanceId} DocId='{_docId}' setting Text/TabText='{title}' (customTitle='{_customTitle}' projectName='{_projectName}')");
                Text = title;
                TabText = title;
            }

            // Update status bar and task HUD when terminal name changes
            // Use BeginInvoke to ensure UI is ready
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    UpdateStatusBar();
                    UpdateTaskHudTerminalName();
                }));
            }
            else
            {
                UpdateStatusBar();
                UpdateTaskHudTerminalName();
            }
        }

        /// <summary>
        /// Updates the task HUD with the current terminal name.
        /// Handles two late-arrival scenarios:
        /// 1. Broker arrived first, name arrives now → call Initialize()
        /// 2. Name arrives first (or name changes after init) → call SetTerminalName()
        ///    SetTerminalName queues the name internally if broker not yet set.
        /// </summary>
        private void UpdateTaskHudTerminalName()
        {
            var taskHud = _hudTabContainer?.TaskHud;
            if (taskHud == null || string.IsNullOrEmpty(_customTitle)) return;

            _debugLogService?.Trace("TerminalDocument.UpdateTaskHudTerminalName", $"customTitle='{_customTitle}' IsBrokerInitialized={taskHud.IsBrokerInitialized} brokerSet={_messageBroker != null}");

            if (!taskHud.IsBrokerInitialized && _messageBroker != null)
            {
                // Broker is set on TerminalDocument but HUD has not been initialized yet.
                // This is the common late-name scenario: SetMessageBroker() was called first
                // (CustomTitle was null then), and now CustomTitle has arrived.
                taskHud.Initialize(_messageBroker, _customTitle);
            }
            else
            {
                // Either HUD is already initialized (update the name), or broker not yet set
                // (SetTerminalName will queue the name for when Initialize is called later).
                taskHud.SetTerminalName(_customTitle);
            }
        }

        private void ShowRenameDialog()
        {
            using (var dialog = new RenameTabDialog(TabText))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.NewName))
                {
                    CustomTitle = dialog.NewName;
                }
            }
        }

        public TerminalDocument()
        {
            InstanceId = ++_instanceCount;
            Text = $"Terminal {InstanceId}";
            TabText = Text;

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            // Create status bar (shows above terminal)
            _statusBar = new TerminalStatusBarRenderer();
            _statusBar.Ready += (s, e) =>
            {
                // Only update status bar if we have a name and broker set
                // Otherwise wait for SetMessageBroker() or StartTerminal() to call it
                if (!string.IsNullOrEmpty(CustomTitle) && _messageBroker != null)
                {
                    UpdateStatusBar();
                }

                // Push current remoteMode state so the Input pill reflects reality on load
                if (_messageBroker != null)
                {
                    _statusBar.SetRemoteMode(_messageBroker.IsRemoteMode);
                }

                // Re-deliver the statusline (folder + model/git/context rows) now that the WebView is
                // confirmed ready. Rows 2-3 start hidden and only un-hide when a `statusline:` message
                // is processed. The renderer now REPLAYS its last-known statusline on ready and the
                // poll re-delivers until JS acks the render (task d14048ef), so this is belt-and-
                // suspenders: clear the change-detection cache so the next tick re-delivers, then make
                // sure a tick actually happens.
                _lastStatusLineContent = null;
                _lastSharedQuotaContent = null;

                // Fire a poll immediately rather than waiting up to ~2s. If the timer was never
                // started — Ready beat the CustomTitle setter that starts polling, a real ordering on
                // fresh launches that left the old code's Change() a no-op (the timer-null gap behind
                // the random missing bands) — start it now. Guarded because Change() throws if the
                // timer raced a Dispose during teardown.
                if (_statusLineTimer != null)
                {
                    try { _statusLineTimer.Change(0, 2000); } catch { /* timer disposed during teardown */ }
                }
                else if (!string.IsNullOrEmpty(CustomTitle))
                {
                    StartStatusLinePolling();
                }
            };
            _statusBar.HomeRequested += (s, e) => ReturnToStartScreen();
            _statusBar.OpenFolderRequested += OnOpenFolderRequested;
            _statusBar.HudToggleRequested += (s, e) =>
            {
                ToggleHud();
                _statusBar.SetHudToggleState(IsHudVisible);
            };

            // Create terminal control
            _terminal = new TerminalControl
            {
                Dock = DockStyle.Fill
            };

            // Subscribe to terminal events
            _terminal.TitleChanged += OnTerminalTitleChanged;
            _terminal.ProcessExited += OnTerminalProcessExited;
            _terminal.ContextMenuRequested += OnTerminalContextMenuRequested;
            _terminal.Ready += (s, e) => TerminalReady?.Invoke(this, EventArgs.Empty);
            _terminal.RendererReady += (s, e) => RendererReady?.Invoke(this, EventArgs.Empty);
            _terminal.ClaudeCodeDetected += (s, e) => ClaudeCodeDetected?.Invoke(this, EventArgs.Empty);
            _terminal.TerminalClicked += (s, args) =>
            {
                // Close any open context menu
                _currentContextMenu?.Close();

                // VS2015 theme uses IsActiveDocumentPane (not IsActivated) for tab colors.
                // WebView2 doesn't report focus through native Win32 handles, so
                // DockPanelSuite's FocusManager never updates IsActiveDocumentPane.
                // We must use reflection to directly call the internal method.
                if (this.Pane != null && this.DockPanel != null)
                {
                    var setMethod = typeof(DockPane)
                        .GetMethod("SetIsActiveDocumentPane", BindingFlags.NonPublic | BindingFlags.Instance);

                    if (setMethod != null)
                    {
                        // Deactivate all other document panes
                        foreach (var pane in this.DockPanel.Panes)
                        {
                            if (pane != this.Pane && pane.IsActiveDocumentPane)
                            {
                                setMethod.Invoke(pane, new object[] { false });
                            }
                        }
                        // Activate this pane as the active document pane
                        setMethod.Invoke(this.Pane, new object[] { true });
                    }

                    // Also set this as the ActiveDocument so features like
                    // Recent Folders know which terminal to target
                    this.Activate();
                }
            };

            // Create embedded agent panel (right side of terminal)
            _embeddedAgentPanel = new EmbeddedAgentPanel();

            // Create task HUD (shows active task checklist, hidden until task found)
            var taskHudRenderer = new TaskHudRenderer
            {
                Visible = false,
                Dock = DockStyle.Fill
            };

            // Wrap TaskHudRenderer in HudTabContainer (supports browser tabs alongside HUD)
            _hudTabContainer = new HudTabContainer(taskHudRenderer)
            {
                Dock = DockStyle.Fill
            };

            // Add permanent HUD tabs: Dashboard, then Notes (Tasks is already tab 0)
            _hudDashboard = new HudDashboardRenderer();
            _hudTabContainer.AddPermanentTab("__dashboard__", "\ud83d\udcca Dashboard", _hudDashboard);

            _hudNotes = new HudNotesRenderer();
            _hudTabContainer.AddPermanentTab("__notes__", "\ud83d\udcdd Notes", _hudNotes);

            _hudKnowledge = new HudKnowledgeRenderer();
            _hudTabContainer.AddPermanentTab("__knowledge__", "\ud83d\udcda Knowledge", _hudKnowledge);

            _hudSessions = new HudSessionsRenderer();
            _hudTabContainer.AddPermanentTab("__sessions__", "\ud83d\udd52 Sessions", _hudSessions);

            // \ud83d\udd00 Git \u2014 per-project read-only git tab. Lives after Tasks per the
            // design lock so it's adjacent to the workflow ("pick task \u2192 see its
            // impact in git"). Backed by GitRepoService via the broker's
            // GitRepoManager DI; multi-repo aware (no-remote vs has-remote
            // header variants), with empty-states for worktree/submodule and
            // not-a-repo cases.
            _hudGit = new HudGitRenderer();
            _hudTabContainer.AddPermanentTab("__git__", "\ud83d\udd00 Git", _hudGit);

            // Set desired tab order: Dashboard, Tasks, Git, Notes, Knowledge, Sessions
            _hudTabContainer.ReorderPermanentTabs("__dashboard__", "__tasks__", "__git__", "__notes__", "__knowledge__", "__sessions__");

            // Fire event when user Ctrl+wheels in the task HUD (for global propagation)
            taskHudRenderer.ZoomChanged += (s, zoom) => { TaskHudZoomChanged?.Invoke(this, zoom); };

            // Create SplitContainer: terminal (top, 75%) | task HUD (bottom, 25%)
            // Task HUD only spans terminal width, not the agent panel
            _terminalHudSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal, // top/bottom split
                FixedPanel = FixedPanel.Panel2, // task HUD keeps fixed size on resize
                BorderStyle = BorderStyle.None,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(80, 80, 110),
                Panel1MinSize = 100,
                Panel2MinSize = 80
            };

            _terminalHudSplitter.Panel1.Controls.Add(_terminal);
            _terminalHudSplitter.Panel2.Controls.Add(_hudTabContainer);

            // HUD is always visible — Panel2 never collapses
            _terminalHudSplitter.Panel2Collapsed = false;

            // Apply saved HUD split ratio once the splitter has a valid height.
            _terminalHudSplitter.SizeChanged += OnHudSplitterSizeChanged;

            // Handle HUD visibility requests. The HUD is always-on now, so we
            // only use this to apply the splitter ratio when first shown.
            _hudTabContainer.VisibilityRequested += (s, show) =>
            {
                // HUD is always visible — ignore hide requests.
                // Only process show requests to ensure splitter ratio is applied.
                if (!show) return;

                double ratioToApply = _hudSplitRatio;
                _debugLogService?.Trace("HUD-DEBUG", $"VisibilityRequested: show={show}, ratioToApply={ratioToApply:F4}, suppress={_suppressSplitterEvents}, initialApplied={_initialHudSplitApplied}");

                _suppressSplitterEvents = true;

                if (_terminalHudSplitter.Height > 0 && IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        _debugLogService?.Trace("HUD-DEBUG", $"VisibilityRequested BeginInvoke: suppress={_suppressSplitterEvents}, ratioToApply={ratioToApply:F4}, currentRatio={_hudSplitRatio:F4}, height={_terminalHudSplitter.Height}, dist={_terminalHudSplitter.SplitterDistance}");
                        try
                        {
                            int maxDistance = _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize;
                            int distance = (int)(_terminalHudSplitter.Height * ratioToApply);
                            // Clamp to respect Panel2MinSize
                            if (distance > maxDistance)
                                distance = maxDistance;
                            if (distance > _terminalHudSplitter.Panel1MinSize)
                                _terminalHudSplitter.SplitterDistance = distance;
                            // Restore in case stray events slipped through
                            _hudSplitRatio = ratioToApply;
                            _debugLogService?.Trace("HUD-DEBUG", $"VisibilityRequested BeginInvoke DONE: distance={distance}, maxDist={maxDistance}, clearing suppress");
                        }
                        catch { /* bounds during layout */ }
                        finally
                        {
                            _suppressSplitterEvents = false;

                            // Hook SplitterMoved AFTER the ratio is applied and suppress is cleared.
                            // Must be inside BeginInvoke so the layout pass is complete and
                            // no spurious SplitterMoved events can fire with bogus distances.
                            if (!_initialHudSplitApplied)
                            {
                                _initialHudSplitApplied = true;
                                _terminalHudSplitter.SplitterMoved += OnHudSplitterMoved;
                            }
                        }
                    }));
                }
                else
                {
                    _suppressSplitterEvents = false;
                }
            };

            // Create SplitContainer: terminal+hud (left, 75%) | agent panel (right, 25%)
            // Panel2MinSize is set in OnSplitterSizeChanged once the control has a valid width,
            // because setting it here (400px) exceeds the default ~150px width and throws.
            _terminalAgentSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical, // left/right split
                FixedPanel = FixedPanel.Panel2, // agent panel keeps fixed size on resize
                BorderStyle = BorderStyle.None,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(80, 80, 110),
                Panel1MinSize = 100,
                Panel2MinSize = 25
            };

            _terminalAgentSplitter.Panel1.Controls.Add(_terminalHudSplitter);
            _terminalAgentSplitter.Panel2.Controls.Add(_embeddedAgentPanel);

            // Hide agent panel until an agent is spawned (same pattern as task HUD)
            _terminalAgentSplitter.Panel2Collapsed = true;

            _embeddedAgentPanel.VisibilityRequested += (s, show) =>
            {
                // INVARIANT: Panel2 must be collapsed when AgentSlotCount == 0.
                // Always verify slot count — don't trust the 'show' parameter alone,
                // because event races can fire show=true after slots were removed.
                int slotCount = _embeddedAgentPanel.AgentSlotCount;
                bool shouldShow = show && slotCount > 0;

                bool alreadyCollapsed = _terminalAgentSplitter.Panel2Collapsed;
                if (shouldShow && !alreadyCollapsed) return; // already showing
                if (!shouldShow && alreadyCollapsed) return;  // already hidden

                try
                {
                    _suppressSplitterEvents = true;
                    _terminalAgentSplitter.Panel2Collapsed = !shouldShow;
                }
                finally { _suppressSplitterEvents = false; }

                if (shouldShow && _terminalAgentSplitter.Width > 0 && IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            _suppressSplitterEvents = true;
                            int distance = (int)(_terminalAgentSplitter.Width * _agentSplitRatio);
                            if (distance > _terminalAgentSplitter.Panel1MinSize &&
                                distance < _terminalAgentSplitter.Width - _terminalAgentSplitter.Panel2MinSize)
                            {
                                _terminalAgentSplitter.SplitterDistance = distance;
                            }
                        }
                        catch { /* WinForms sizing edge case -- safe to ignore */ }
                        finally { _suppressSplitterEvents = false; }
                    }));
                }

                // Deferred safety check: verify collapsed state is still correct after
                // WinForms finishes its layout pass. Catches races where another event
                // toggles visibility between now and the next message pump cycle.
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() => EnforceAgentPanelInvariant()));
                }
            };

            // Set the saved split ratio once the splitter has a valid width.
            // SplitterMoved is hooked AFTER the initial ratio is applied (in OnSplitterSizeChanged)
            // to prevent WinForms layout events from overwriting the saved ratio during init.
            _terminalAgentSplitter.SizeChanged += OnSplitterSizeChanged;

            // Periodic sanity check: enforce Panel2 collapsed state matches actual slot count.
            // This is the backstop that makes all event races harmless — even if every
            // event handler misses, this timer catches stale panels within 2 seconds.
            _agentPanelSanityTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _agentPanelSanityTimer.Tick += (s, e) => EnforceAgentPanelInvariant();
            _agentPanelSanityTimer.Start();

            // Splitter handle between status bar and terminal area
            _statusBarSplitter = new Splitter
            {
                Dock = DockStyle.Top,
                Height = 6,
                Cursor = Cursors.HSplit,
                BackColor = Color.FromArgb(50, 50, 70),
                MinSize = 60,    // min status bar height
                MinExtra = 200   // min remaining space for terminal
            };
            _statusBarSplitter.SplitterMoved += OnStatusBarSplitterMoved;

            // Drop zone: native WinForms panel below the status bar for reliable
            // kanban drag-and-drop. WebView2 surfaces intercept WinForms drag events,
            // so we use a dedicated native panel instead of document-level AllowDrop.
            _dropZoneLabel = new Label
            {
                Text = "\u2193 Drop task here",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.FromArgb(140, 140, 160),
                BackColor = Color.Transparent,
                Cursor = Cursors.Default
            };
            _dropZone = new Panel
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = Color.FromArgb(35, 35, 50),
                Padding = new Padding(0),
                AllowDrop = true
            };
            _dropZone.Controls.Add(_dropZoneLabel);
            _dropZoneLabel.AllowDrop = true;
            _dropZoneLabel.DragEnter += OnTaskDragEnter;
            _dropZoneLabel.DragOver += OnTaskDragOver;
            _dropZoneLabel.DragLeave += OnTaskDragLeave;
            _dropZoneLabel.DragDrop += OnTaskDragDrop;
            _dropZone.DragEnter += OnTaskDragEnter;
            _dropZone.DragOver += OnTaskDragOver;
            _dropZone.DragLeave += OnTaskDragLeave;
            _dropZone.DragDrop += OnTaskDragDrop;

            // Layout order: _terminalAgentSplitter (Fill) | _dropZone (Top) | _statusBarSplitter (Top) | _statusBar (Top)
            Controls.Add(_terminalAgentSplitter);
            Controls.Add(_dropZone);
            Controls.Add(_statusBarSplitter);
            Controls.Add(_statusBar);

            // Start screen: overlays the full client area, shown before terminal starts
            _startScreen = new StartScreenControl
            {
                Dock = DockStyle.Fill,
                Visible = true // Visible by default on new tabs
            };
            _startScreen.ProjectLaunched += (s, e) => ProjectLaunched?.Invoke(this, e);
            _startScreen.OpenPowerShellRequested += (s, e) => StartScreenOpenPowerShellRequested?.Invoke(this, e);
            _startScreen.NewProjectRequested += (s, e) => StartScreenNewProjectRequested?.Invoke(this, e);
            _startScreen.JustClaudeRequested += (s, e) => StartScreenJustClaudeRequested?.Invoke(this, e);
            _startScreen.ProjectDeleteRequested += (s, id) => StartScreenProjectDeleteRequested?.Invoke(this, id);
            Controls.Add(_startScreen);
            _startScreen.BringToFront();
            _isStartScreenVisible = true;

            // Configure dock behavior
            DockAreas = DockAreas.Document | DockAreas.Float;
            CloseButton = true;
            CloseButtonVisible = true;
            ShowHint = DockState.Document;

            // Reserve space for focus border (always 2px padding, color toggles)
            Padding = new Padding(2);
            BackColor = Color.FromArgb(30, 30, 30);

            // Initialize tab header context menu
            InitializeTabContextMenu();

            ResumeLayout(false);
            PerformLayout();
        }

        private bool _initialSplitApplied;
        private bool _initialHudSplitApplied;

        /// <summary>
        /// Sets the saved splitter ratio once the container has a valid width.
        /// </summary>
        private void OnSplitterSizeChanged(object sender, EventArgs e)
        {
            if (_initialSplitApplied) return;
            if (_terminalAgentSplitter.Width <= 0) return;

            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                // Apply the real Panel2MinSize now that the control has a valid width
                // (can't set 400 at construction time when default width is ~150)
                if (_terminalAgentSplitter.Width > 506)
                    _terminalAgentSplitter.Panel2MinSize = 400;
                int distance = (int)(_terminalAgentSplitter.Width * _agentSplitRatio);
                if (distance > _terminalAgentSplitter.Panel1MinSize &&
                    distance < _terminalAgentSplitter.Width - _terminalAgentSplitter.Panel2MinSize)
                {
                    _terminalAgentSplitter.SplitterDistance = distance;
                    _initialSplitApplied = true;
                    // Now safe to listen for user-initiated splitter drags
                    _terminalAgentSplitter.SplitterMoved += OnAgentSplitterMoved;
                }
            }
            catch
            {
                // Ignore if splitter distance is out of bounds during layout
            }
            finally { _suppressSplitterEvents = prevSuppress; }
        }

        /// <summary>
        /// Applies the saved HUD splitter ratio once the container has a valid height.
        /// Mirrors OnSplitterSizeChanged for the agent panel. Now that the HUD is always
        /// visible (Panel2 never collapses), VisibilityRequested may not fire on startup,
        /// so we need this to apply the persisted height.
        /// </summary>
        private void OnHudSplitterSizeChanged(object sender, EventArgs e)
        {
            if (_initialHudSplitApplied) return;
            if (_terminalHudSplitter == null || _terminalHudSplitter.Height <= 0) return;

            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                int maxDistance = _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize;
                int distance = (int)(_terminalHudSplitter.Height * _hudSplitRatio);
                if (distance > maxDistance)
                    distance = maxDistance;
                if (distance > _terminalHudSplitter.Panel1MinSize &&
                    distance < _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize)
                {
                    _terminalHudSplitter.SplitterDistance = distance;
                    _initialHudSplitApplied = true;
                    _terminalHudSplitter.SplitterMoved += OnHudSplitterMoved;
                }
            }
            catch { }
            finally { _suppressSplitterEvents = prevSuppress; }
        }

        /// <summary>
        /// Enforces the invariant: Panel2 collapsed iff AgentSlotCount == 0.
        /// Called from deferred checks and the periodic sanity timer.
        /// Safe to call at any time — idempotent and suppresses splitter events.
        /// </summary>
        private void EnforceAgentPanelInvariant()
        {
            if (_isDisposing || _terminalAgentSplitter == null || _embeddedAgentPanel == null) return;

            bool shouldBeCollapsed = _embeddedAgentPanel.AgentSlotCount == 0;
            bool isCollapsed = _terminalAgentSplitter.Panel2Collapsed;

            if (shouldBeCollapsed != isCollapsed)
            {
                bool prevSuppress = _suppressSplitterEvents;
                try
                {
                    _suppressSplitterEvents = true;
                    _terminalAgentSplitter.Panel2Collapsed = shouldBeCollapsed;
                }
                catch { }
                finally { _suppressSplitterEvents = prevSuppress; }
            }
        }

        /// <summary>
        /// Fires when the user drags the agent panel splitter.
        /// </summary>
        private void OnAgentSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_suppressSplitterEvents || _isDisposing) return;
            if (_terminalAgentSplitter == null || _terminalAgentSplitter.Panel2Collapsed) return;
            if (_terminalAgentSplitter.Width <= 0) return;
            double ratio = (double)_terminalAgentSplitter.SplitterDistance / _terminalAgentSplitter.Width;
            _agentSplitRatio = ratio;
            AgentSplitRatioChanged?.Invoke(this, ratio);
        }

        /// <summary>
        /// Fires when the user drags the HUD splitter.
        /// </summary>
        private void OnHudSplitterMoved(object sender, SplitterEventArgs e)
        {
            _debugLogService?.Trace("HUD-DEBUG", $"OnHudSplitterMoved: suppress={_suppressSplitterEvents}, collapsed={_terminalHudSplitter?.Panel2Collapsed}, height={_terminalHudSplitter?.Height}, dist={_terminalHudSplitter?.SplitterDistance}, caller={new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name}");
            if (_suppressSplitterEvents || _isDisposing) return;
            if (_terminalHudSplitter == null || _terminalHudSplitter.Panel2Collapsed) return;
            if (_terminalHudSplitter.Height <= 0) return;
            double ratio = (double)_terminalHudSplitter.SplitterDistance / _terminalHudSplitter.Height;
            _debugLogService?.Trace("HUD-DEBUG", $"OnHudSplitterMoved SAVING ratio={ratio:F4} (dist={_terminalHudSplitter.SplitterDistance}, height={_terminalHudSplitter.Height})");
            _hudSplitRatio = ratio;
            HudSplitRatioChanged?.Invoke(this, ratio);
        }

        /// <summary>
        /// Fires when the user drags the status bar splitter.
        /// </summary>
        private void OnStatusBarSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_statusBar == null) return;
            int height = _statusBar.Height;
            if (height > 0)
            {
                StatusBarHeightChanged?.Invoke(this, height);
            }
        }

        /// <summary>
        /// Applies a saved status bar height.
        /// </summary>
        public void ApplyStatusBarHeight(int height)
        {
            if (_statusBar != null && height > 0)
            {
                _statusBar.Height = height;
            }
        }

        private void InitializeTabContextMenu()
        {
            _tabContextMenu = new ContextMenuStrip();

            // Home — stop terminal and show start screen
            var homeItem = new ToolStripMenuItem("Home");
            homeItem.Click += (s, args) => ReturnToStartScreen();
            _tabContextMenu.Items.Add(homeItem);

            _tabContextMenu.Items.Add(new ToolStripSeparator());

            // Close
            var closeItem = new ToolStripMenuItem("Close");
            closeItem.Click += (s, args) => this.Close();
            _tabContextMenu.Items.Add(closeItem);

            // Close All Tabs
            var closeAllItem = new ToolStripMenuItem("Close All Tabs");
            closeAllItem.Click += (s, args) => CloseAllTabs();
            _tabContextMenu.Items.Add(closeAllItem);

            // Close all but this
            var closeOthersItem = new ToolStripMenuItem("Close all but this");
            closeOthersItem.Click += (s, args) => CloseAllButThis();
            _tabContextMenu.Items.Add(closeOthersItem);

            _tabContextMenu.Items.Add(new ToolStripSeparator());

            // Copy file path/name
            var copyPathItem = new ToolStripMenuItem("Copy file path/name");
            copyPathItem.Click += (s, args) => CopyPathToClipboard();
            _tabContextMenu.Items.Add(copyPathItem);

            // Open Containing Folder
            var openFolderItem = new ToolStripMenuItem("Open Containing Folder");
            openFolderItem.Click += (s, args) => OpenContainingFolder();
            _tabContextMenu.Items.Add(openFolderItem);

            // Note: Don't use TabPageContextMenuStrip in .NET 8 due to DockPanelSuite compatibility issues
            // The context menu will be shown manually via DockPanel events if needed
        }

        private void CloseAllTabs()
        {
            if (DockPanel == null) return;

            var docsToClose = DockPanel.Documents
                .OfType<TerminalDocument>()
                .ToList();

            foreach (var doc in docsToClose)
            {
                doc.Close();
            }
        }

        private void CloseAllButThis()
        {
            if (DockPanel == null) return;

            var docsToClose = DockPanel.Documents
                .OfType<TerminalDocument>()
                .Where(doc => doc != this)
                .ToList();

            foreach (var doc in docsToClose)
            {
                doc.Close();
            }
        }

        private void CopyPathToClipboard()
        {
            string path = GetWorkingDirectory();
            if (!string.IsNullOrEmpty(path))
            {
                Clipboard.SetText(path);
            }
        }

        private void OpenContainingFolder()
        {
            string path = GetWorkingDirectory();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Process.Start("explorer.exe", path);
            }
        }

        /// <summary>
        /// Starts the terminal with an optional working directory and pre-registered name.
        /// </summary>
        /// <param name="workingDirectory">Initial working directory</param>
        /// <param name="terminalName">Pre-registered terminal name for MCP (null if not pre-registered)</param>
        /// <param name="autoRunCommand">Command to run automatically after shell starts (e.g., "claude -r session_id")</param>
        /// <param name="projectId">Project ID for context injection (sets MULTITERMINAL_PROJECT_ID env var)</param>
        /// <param name="isTeamLead">Whether this terminal is a team lead (sets MULTITERMINAL_TEAM_LEAD env var)</param>
        /// <param name="gatewayProfile">MCP Gateway profile name (sets MCP_GATEWAY_PROFILE env var)</param>
        /// <param name="taskWorktreePath">Per-task worktree path resolved from the active task (sets MULTITERMINAL_TASK_WORKTREE env var). Empty when no task worktree is in play.</param>
        public void StartTerminal(string workingDirectory = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, string taskWorktreePath = null)
        {
            _debugLogService?.Info("TerminalDocument.StartTerminal", $"===== START =====");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"workingDirectory: '{workingDirectory ?? "null"}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"terminalName: '{terminalName ?? "null"}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"autoRunCommand: '{autoRunCommand ?? "null"}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"spawnerName: '{spawnerName ?? "null"}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"projectId: '{projectId ?? "null"}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"isTeamLead: '{isTeamLead}'");
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"_docId: '{_docId}'");

            // Hide start screen before launching the shell
            HideStartScreen();

            _lastKnownDirectory = workingDirectory;
            _startingWorkingDirectory = workingDirectory;
            ToolTipText = workingDirectory;
            _isTerminalStarted = true;

            // Derive project name for tab title: use project lookup, or fall back to folder name
            if (!string.IsNullOrEmpty(projectId) && _messageBroker != null)
            {
                try
                {
                    var project = _messageBroker.ProjectDatabase?.GetProject(projectId);
                    _debugLogService?.Trace("TerminalDocument", $"#PROJ# [TerminalDocument.StartTerminal] _messageBroker.ProjectDatabase.GetProject('{projectId}') => {(project == null ? "NULL" : $"id='{project.Id}' name='{project.Name}' path='{project.Path}'")}");
                    if (project != null && !string.IsNullOrEmpty(project.Name))
                    {
                        if (!string.Equals(project.Id, projectId, StringComparison.Ordinal))
                            _debugLogService?.Warning("TerminalDocument", $"#PROJ# [TerminalDocument.StartTerminal] *** BROKER DB ID MISMATCH *** requested='{projectId}' returned='{project.Id}' name='{project.Name}'");
                        _projectName = project.Name;
                    }
                }
                catch (Exception lookupEx)
                {
                    _debugLogService?.Error("TerminalDocument", $"#PROJ# [TerminalDocument.StartTerminal] project lookup threw: {lookupEx.Message}");
                }
            }
            if (string.IsNullOrEmpty(_projectName) && !string.IsNullOrEmpty(workingDirectory))
            {
                _projectName = System.IO.Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
                _debugLogService?.Warning("TerminalDocument", $"#PROJ# [TerminalDocument.StartTerminal] Fell back to folder-name projectName='{_projectName}' from workingDirectory='{workingDirectory}'");
            }
            _debugLogService?.Trace("TerminalDocument", $"#PROJ# [TerminalDocument.StartTerminal] Final _projectName='{_projectName}' for projectId='{projectId}'");

            // Set terminal name as custom title if provided. StartTerminal is
            // an AUTHORITATIVE identity source — the terminalName comes from
            // the live launch context, not from persisted UI state — so we
            // also promote it into _originalAgentName for stable broker-event
            // filtering. PromoteOriginalAgentName is first-wins so subsequent
            // launches (rare) won't clobber.
            if (!string.IsNullOrEmpty(terminalName))
            {
                _debugLogService?.Trace("TerminalDocument.StartTerminal", $"Setting CustomTitle to '{terminalName}'");
                CustomTitle = terminalName;
                PromoteOriginalAgentName(terminalName);
            }

            // Pre-write a fallback statusline file with the folder path so the header
            // always shows SOMETHING even if the Claude Code process has a project-level
            // statusLine override and never invokes scripts/statusline.js. When the real
            // script does run, it overwrites this file with full model/git/quota data.
            WriteFallbackStatusline(terminalName, workingDirectory);

            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"Calling _terminal.Start...");
            // SWAPDIAG (task ab32897c): records which TerminalDocument (_docId/instance) sent
            // which docId to its own child shell, plus the launch name/dir. Cross-reference with
            // the SWAPDIAG REGISTER lines to detect a doc↔docId cross. Remove after root cause.
            _debugLogService?.Info("SWAPDIAG",
                $"LAUNCH inst={InstanceId} docId={_docId} name='{terminalName}' projectId='{projectId}' dir='{workingDirectory}'");
            _terminal.Start(workingDirectory, _docId, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile, taskWorktreePath);
            _debugLogService?.Trace("TerminalDocument.StartTerminal", $"_terminal.Start returned");

            // Update status bar after terminal starts
            UpdateStatusBar();

            // Update all HUD tabs with project context
            _hudDashboard?.SetProject(projectId, workingDirectory, _projectName);
            _hudDispatchedFolder = workingDirectory;
            // Notes/Sessions are per-PROJECT: key them to the canonical registered
            // project path, not the launch dir (which for an active-task terminal is
            // the worktree path — no notes there, and GetProjectNoteTabs would
            // auto-create an empty "General" row). Fall back to the normalized
            // working dir only when the project isn't registered.
            // Fallback (unregistered project): the RAW working directory — the exact
            // string old terminals keyed notes under — so existing rows still match.
            _hudNotesProjectKey = TryResolveCanonicalProjectPath(projectId, workingDirectory)
                                  ?? workingDirectory;
            _hudNotes?.SetProject(_hudNotesProjectKey);
            _hudSessions?.SetProject(_hudNotesProjectKey);
            _hudKnowledge?.SetProject(projectId);
            // Pass projectId explicitly — workingDirectory may be a worktree
            // subfolder that isn't in the project registry, and the path-only
            // overload would silently leave _projectId null (breaks per-(project,
            // branch) outcome read/write). Debugger Run-1 finding.
            _hudGit?.SetProject(projectId, workingDirectory);
            // (_hudSessions is keyed above to the canonical project path, not the
            // launch/worktree dir — see _hudNotesProjectKey resolution.)
            // Switch HUD to dashboard tab by default when starting a terminal
            _hudTabContainer?.SwitchToTabById("__dashboard__");

            // Kick off working-tree dirty polling now that we have a project
            // context. This surfaces uncommitted edits (which don't touch .git/)
            // in the HUD header strip + Git tab on a slow cadence.
            StartWorkingTreeDirtyPolling();

            _debugLogService?.Info("TerminalDocument.StartTerminal", $"===== COMPLETE =====");
        }

        /// <summary>
        /// Sets the terminal theme.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;
            _terminal.SetTheme(theme);
            _statusBar?.SetTheme(theme.IsDark);
            _hudTabContainer?.ApplyTheme(theme.IsDark);
            _embeddedAgentPanel?.ApplyTheme(theme.IsDark);
            _startScreen?.ApplyTheme(theme.IsDark);

            // Theme the drop zone
            if (_dropZone != null)
            {
                _dropZone.BackColor = theme.IsDark
                    ? Color.FromArgb(35, 35, 50)
                    : Color.FromArgb(235, 235, 245);
                _dropZoneLabel.ForeColor = theme.IsDark
                    ? Color.FromArgb(140, 140, 160)
                    : Color.FromArgb(120, 120, 140);
            }

            // Theme the splitter handle
            if (_terminalAgentSplitter != null)
            {
                _terminalAgentSplitter.BackColor = theme.IsDark
                    ? Color.FromArgb(80, 80, 110)
                    : Color.FromArgb(180, 180, 200);
            }
            if (_terminalHudSplitter != null)
            {
                _terminalHudSplitter.BackColor = theme.IsDark
                    ? Color.FromArgb(80, 80, 110)
                    : Color.FromArgb(180, 180, 200);
            }
        }

        /// <summary>
        /// Focuses the terminal for input.
        /// </summary>
        public void FocusTerminal()
        {
            _terminal.FocusTerminal();
        }

        /// <summary>
        /// Shows or hides a green focus border around this terminal.
        /// Padding is always 2 to avoid layout shift; only BackColor changes.
        /// </summary>
        public void SetFocusBorder(bool hasFocus)
        {
            var newColor = hasFocus
                ? Color.FromArgb(76, 175, 80) // Green focus indicator
                : Color.FromArgb(30, 30, 30); // Blend with background

            if (BackColor != newColor)
                BackColor = newColor;
        }

        /// <summary>
        /// Sets the terminal font size.
        /// </summary>
        public void SetFontSize(float size)
        {
            _terminal?.SetFontSize(size);
        }

        /// <summary>
        /// Gets the terminal font size.
        /// </summary>
        public float GetFontSize()
        {
            return _terminal?.GetFontSize() ?? 10f;
        }

        /// <summary>
        /// Applies a zoom level to the task HUD WebView2 immediately.
        /// </summary>
        public void ApplyTaskHudZoom(double zoom)
        {
            _hudTabContainer?.SetZoomFactor(zoom);
        }

        /// <summary>
        /// Adds a browser tab to the HUD tab container.
        /// </summary>
        public BrowserTabPage AddBrowserTab(string tabId, string title, string url = null, string htmlContent = null, bool? isDark = null)
        {
            return _hudTabContainer?.AddBrowserTab(tabId, title, url, htmlContent);
        }

        /// <summary>
        /// Removes a browser tab from the HUD tab container.
        /// </summary>
        public void RemoveBrowserTab(string tabId)
        {
            _hudTabContainer?.RemoveBrowserTab(tabId);
        }

        /// <summary>
        /// Updates content of an existing browser tab.
        /// </summary>
        public void SetBrowserContent(string tabId, string title, string url = null, string htmlContent = null)
        {
            _hudTabContainer?.SetBrowserContent(tabId, title, url, htmlContent);
        }

        /// <summary>
        /// Gets a browser tab by ID, or null if not found.
        /// </summary>
        public BrowserTabPage GetBrowserTab(string tabId)
        {
            return _hudTabContainer?.GetBrowserTab(tabId);
        }

        /// <summary>
        /// Handles the dashboard widget's dirty-state click — deep-links to this
        /// project's HUD Git tab (item [12]). Marshals to UI thread because
        /// HudTabContainer.SwitchToTabById manipulates WinForms control visibility.
        /// Graceful no-op if the Git tab isn't registered (SwitchToTabById guards on
        /// the tab-id lookup internally).
        /// </summary>
        private void OnOpenGitTabRequested(object sender, EventArgs e)
        {
            if (_isDisposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnOpenGitTabRequested(sender, e))); }
                catch { }
                return;
            }
            try { _hudTabContainer?.SwitchToTabById("__git__"); }
            catch { }
        }

        /// <summary>
        /// Handles a diff request from the dashboard activity feed.
        /// Finds the file in the project, runs git diff, and opens a browser tab with the diff.
        /// </summary>
        private async void OnShowDiffRequested(object sender, string fileName)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnShowDiffRequested(sender, fileName)));
                return;
            }

            string workDir = GetWorkingDirectory();
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) return;

            try
            {
                // Run git diff on a background thread
                var diffResult = await System.Threading.Tasks.Task.Run(() =>
                {
                    // Find the git root
                    string gitRoot = RunGitCommandInDir(workDir, "rev-parse --show-toplevel")?.Trim();
                    if (string.IsNullOrEmpty(gitRoot)) return (diff: "", fullPath: "");

                    // Find the file relative to git root
                    // fileName may be a full relative path (e.g. "Controls/Foo/Bar.cs") or just a basename
                    string diffFiles = RunGitCommandInDir(gitRoot, "diff --name-only");
                    string stagedFiles = RunGitCommandInDir(gitRoot, "diff --cached --name-only");
                    string allChanged = (diffFiles ?? "") + "\n" + (stagedFiles ?? "");

                    string fileBaseName = Path.GetFileName(fileName);
                    string relativePath = allChanged
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(f =>
                            f.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                            f.Replace('/', '\\').Equals(fileName.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(f).Equals(fileBaseName, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(relativePath))
                    {
                        // Try HEAD~1 diff as fallback (recently committed)
                        string headFiles = RunGitCommandInDir(gitRoot, "diff HEAD~1 --name-only");
                        relativePath = headFiles?
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault(f =>
                                f.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                                f.Replace('/', '\\').Equals(fileName.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(f).Equals(fileBaseName, StringComparison.OrdinalIgnoreCase));

                        if (!string.IsNullOrEmpty(relativePath))
                        {
                            string d = RunGitCommandInDir(gitRoot, $"diff HEAD~1 -- \"{relativePath}\"");
                            return (diff: d ?? "", fullPath: Path.Combine(gitRoot, relativePath));
                        }
                        return (diff: "", fullPath: "");
                    }

                    string diff = RunGitCommandInDir(gitRoot, $"diff -- \"{relativePath}\"");
                    if (string.IsNullOrEmpty(diff))
                        diff = RunGitCommandInDir(gitRoot, $"diff --cached -- \"{relativePath}\"");

                    return (diff: diff ?? "", fullPath: Path.Combine(gitRoot, relativePath));
                });

                string displayName = Path.GetFileName(fileName);
                string html;
                if (string.IsNullOrEmpty(diffResult.diff))
                    html = DiffRenderer.RenderUnifiedDiff(displayName, diffResult.fullPath, "(no changes found)");
                else
                    html = DiffRenderer.RenderUnifiedDiff(displayName, diffResult.fullPath, diffResult.diff);

                ShowDiffPopup(displayName, html);
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument", $"ShowDiff failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes the Git tab's "Open Code Review" right-click menu directly to
        /// <see cref="Dialogs.CodeReviewPopupManager.OpenOrFocus"/>. The clicked
        /// file's repo-relative path is passed as <c>filePath</c> so the popup
        /// preselects it. If the file has no task linkage, surfaces a
        /// <see cref="MessageBox"/> suggesting <c>link_task_file</c> rather than
        /// opening an unrooted popup (single-instance keying is by taskId).
        ///
        /// <para>The popup loads files + diffs + the latest code-reviewer report
        /// via <see cref="MultiTerminal.Services.CodeReviewService"/> (in-proc).
        /// We no longer pre-fetch the diff here.</para>
        /// </summary>
        private async void OnHudGitOpenDiffPopupRequested(object sender, string repoRelativePath)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnHudGitOpenDiffPopupRequested(sender, repoRelativePath)));
                return;
            }
            if (string.IsNullOrEmpty(repoRelativePath)) return;
            if (_messageBroker == null) return;

            string workDir = GetWorkingDirectory();
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) return;

            var manager = _messageBroker.GitRepos;
            if (manager == null) return;

            // Capture broker-owned services into locals before the background pass —
            // same race-mitigation pattern as HudGitRenderer.RefreshAsync. The
            // auto-property on MessageBroker has no volatile or lock; freezing the
            // reference here ensures we use one stable service for the duration of
            // this call.
            var attributionSvc = _messageBroker.GitAttribution;

            try
            {
                string repoRoot = await System.Threading.Tasks.Task.Run(() =>
                {
                    var svc = manager.GetOrCreate(workDir);
                    return svc?.RepoRoot ?? string.Empty;
                });

                string taskId = null;
                string taskTitle = null;
                if (attributionSvc != null && !string.IsNullOrEmpty(repoRoot))
                {
                    try
                    {
                        var attrs = await System.Threading.Tasks.Task.Run(() =>
                            attributionSvc.GetAttributionForFiles(
                                repoRoot,
                                new[] { repoRelativePath }));
                        if (attrs != null && attrs.Count > 0)
                        {
                            taskId = attrs[0].TaskId;
                            taskTitle = attrs[0].TaskTitle;
                        }
                    }
                    catch
                    {
                        // Degrade silently — caller decides what to do without a taskId.
                    }
                }

                // Resolve the repo-relative path to absolute once; both the
                // task-mode and working-tree-mode popup paths need it (task
                // mode for file_links preselect match, working-tree mode as
                // the entire file list).
                string absoluteFilePathForAll = repoRelativePath;
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    try
                    {
                        absoluteFilePathForAll = Path.Combine(
                            repoRoot,
                            repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    }
                    catch (Exception pathEx)
                    {
                        _debugLogService?.Error("TerminalDocument", $"absoluteFilePath resolve failed: {pathEx.Message}");
                    }
                }

                if (string.IsNullOrEmpty(taskId))
                {
                    // Phase 3b: file isn't linked to a task — instead of the
                    // old "No Task Linked" dead-end, open the popup in
                    // working-tree mode against the single file. The popup
                    // hides Pass/Submit-Notes since there's no task to
                    // transition; Phase 3c will replace those with a
                    // "Wrap in quick task" affordance.
                    if (string.IsNullOrEmpty(repoRoot))
                    {
                        _debugLogService?.Warning("TerminalDocument", "no repoRoot resolved for taskless review request — dropping");
                        return;
                    }
                    var crServiceWt = _codeReviewService ??= new MultiTerminal.Services.CodeReviewService(_messageBroker);
                    bool isDarkWt = _currentTheme?.IsDark ?? true;
                    Dialogs.CodeReviewPopupManager.OpenOrFocusWorkingTree(
                        repoRoot,
                        new[] { absoluteFilePathForAll },
                        absoluteFilePathForAll,
                        isDarkWt,
                        _messageBroker,
                        crServiceWt,
                        FindForm(),
                        // Phase 3c follow-up: when the popup's "Wrap in quick task"
                        // create succeeds, re-fetch git_state_tree so the wrapped
                        // file moves out of the "Needs a quick task" group without
                        // the user having to click HUD Git's Refresh button.
                        // Mirrors the in-panel HandleCreateQuickTask path which
                        // calls RefreshAsync after a successful link.
                        onQuickTaskCreated: _ => _hudGit?.RequestRefresh());
                    return;
                }

                // Fallback: if attribution didn't return a title but we have the
                // taskId, ask the broker. Cheap in-memory lookup.
                if (string.IsNullOrEmpty(taskTitle))
                {
                    try { taskTitle = _messageBroker.GetTask(taskId)?.Title; }
                    catch { /* leave blank — popup falls back to taskId in title bar */ }
                }

                // F-R2-6: task file_links store absolute paths; the JS
                // findFileIndex matches strictly. The absolute path was
                // resolved above (absoluteFilePathForAll) so both the task
                // and working-tree branches see the same canonical form.
                var crService = _codeReviewService ??= new MultiTerminal.Services.CodeReviewService(_messageBroker);
                bool isDark = _currentTheme?.IsDark ?? true;
                Dialogs.CodeReviewPopupManager.OpenOrFocus(
                    taskId,
                    taskTitle,
                    absoluteFilePathForAll,
                    isDark,
                    _messageBroker,
                    crService,
                    FindForm());
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument", $"OnHudGitOpenDiffPopupRequested failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a standalone read-only popup window showing diff HTML content.
        /// Used by the HUD activity-feed "Show Diff" flow (<see cref="ShowDiff"/>);
        /// the Code Review popup goes through
        /// <see cref="Dialogs.CodeReviewPopupManager.OpenOrFocus"/> instead.
        /// </summary>
        private async void ShowDiffPopup(string fileName, string html)
        {
            LoadDiffPopupSettings();

            // CA2000: Form shown non-modally; disposed in FormClosed handler below.
#pragma warning disable CA2000
            var form = new Form
            {
                Text = $"Diff: {fileName}",
                Width = _diffPopupBounds?.Width ?? 800,
                Height = _diffPopupBounds?.Height ?? 600,
                StartPosition = _diffPopupBounds.HasValue
                    ? FormStartPosition.Manual
                    : FormStartPosition.CenterParent,
                Icon = FindForm()?.Icon
            };
#pragma warning restore CA2000

            if (_diffPopupBounds.HasValue)
                form.Location = _diffPopupBounds.Value.Location;

            var webView = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(webView);

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await webView.EnsureCoreWebView2Async(environment);
                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(26, 26, 46);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                if (Math.Abs(_diffPopupZoom - 1.0) > 0.01)
                    webView.ZoomFactor = _diffPopupZoom;

                webView.CoreWebView2.NavigateToString(html);
            }
            catch { }

            form.FormClosed += (s, e) =>
            {
                // Save bounds (use RestoreBounds if maximized to get normal size)
                _diffPopupBounds = form.WindowState == FormWindowState.Normal
                    ? form.Bounds
                    : form.RestoreBounds;

                if (webView.CoreWebView2 != null)
                    _diffPopupZoom = webView.ZoomFactor;

                SaveDiffPopupSettings();
                webView.Dispose();
                form.Dispose();
            };

            form.Show(FindForm());
        }

        private static void LoadDiffPopupSettings()
        {
            if (_diffPopupSettingsLoaded) return;
            _diffPopupSettingsLoaded = true;

            try
            {
                var settings = SettingsService.Default;
                string x = settings.Get("DiffPopupX");
                string y = settings.Get("DiffPopupY");
                string w = settings.Get("DiffPopupWidth");
                string h = settings.Get("DiffPopupHeight");
                string z = settings.Get("DiffPopupZoom");

                if (int.TryParse(x, out int ix) && int.TryParse(y, out int iy) &&
                    int.TryParse(w, out int iw) && int.TryParse(h, out int ih) &&
                    iw > 100 && ih > 100)
                {
                    var bounds = new Rectangle(ix, iy, iw, ih);
                    // Verify the saved position is on a visible screen
                    bool onScreen = false;
                    foreach (var screen in Screen.AllScreens)
                    {
                        if (screen.WorkingArea.IntersectsWith(bounds))
                        {
                            onScreen = true;
                            break;
                        }
                    }
                    if (onScreen)
                        _diffPopupBounds = bounds;
                }

                if (double.TryParse(z, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double zoom) &&
                    zoom >= 0.25 && zoom <= 5.0)
                {
                    _diffPopupZoom = zoom;
                }
            }
            catch { }
        }

        private static void SaveDiffPopupSettings()
        {
            try
            {
                var settings = SettingsService.Default;
                if (_diffPopupBounds.HasValue)
                {
                    var b = _diffPopupBounds.Value;
                    settings.BeginBatch();
                    settings.Set("DiffPopupX", b.X.ToString());
                    settings.Set("DiffPopupY", b.Y.ToString());
                    settings.Set("DiffPopupWidth", b.Width.ToString());
                    settings.Set("DiffPopupHeight", b.Height.ToString());
                    settings.Set("DiffPopupZoom", _diffPopupZoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    settings.EndBatch();
                }
            }
            catch { }
        }

        private static string RunGitCommandInDir(string workDir, string args)
        {
            try
            {
                if (!Directory.Exists(workDir)) return null;
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return output;
            }
            catch { return null; }
        }

        /// <summary>
        /// Force-collapse the agent panel. Called by MainForm when it knows all slots
        /// have been removed but the event-driven collapse didn't fire.
        /// </summary>
        public void ForceCollapseAgentPanel()
        {
            if (_isDisposing || _terminalAgentSplitter == null) return;
            if (_terminalAgentSplitter.Panel2Collapsed) return; // already collapsed
            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                _terminalAgentSplitter.Panel2Collapsed = true;
            }
            catch { }
            finally { _suppressSplitterEvents = prevSuppress; }
        }

        /// <summary>
        /// Sets the agent panel split ratio and applies it immediately if the splitter is visible.
        /// </summary>
        public void ApplyAgentSplitRatio(double ratio)
        {
            _agentSplitRatio = ratio;
            if (_isDisposing || _terminalAgentSplitter == null) return;
            if (_terminalAgentSplitter.Panel2Collapsed || _terminalAgentSplitter.Width <= 0) return;
            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                int distance = (int)(_terminalAgentSplitter.Width * ratio);
                if (distance > _terminalAgentSplitter.Panel1MinSize &&
                    distance < _terminalAgentSplitter.Width - _terminalAgentSplitter.Panel2MinSize)
                {
                    _terminalAgentSplitter.SplitterDistance = distance;
                }
            }
            catch { }
            finally { _suppressSplitterEvents = prevSuppress; }
        }

        /// <summary>
        /// Sets the HUD split ratio and applies it immediately if the splitter is visible.
        /// </summary>
        public void ApplyHudSplitRatio(double ratio)
        {
            _hudSplitRatio = ratio;
            if (_isDisposing || _terminalHudSplitter == null) return;
            _debugLogService?.Trace("HUD-DEBUG", $"ApplyHudSplitRatio: ratio={ratio:F4}, collapsed={_terminalHudSplitter.Panel2Collapsed}, height={_terminalHudSplitter.Height}");
            if (_terminalHudSplitter.Panel2Collapsed || _terminalHudSplitter.Height <= 0) return;
            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                int maxDistance = _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize;
                int distance = (int)(_terminalHudSplitter.Height * ratio);
                // Clamp to respect Panel2MinSize
                if (distance > maxDistance)
                    distance = maxDistance;
                if (distance > _terminalHudSplitter.Panel1MinSize &&
                    distance < _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize)
                {
                    _terminalHudSplitter.SplitterDistance = distance;
                }
            }
            catch { }
            finally { _suppressSplitterEvents = prevSuppress; }
        }

        /// <summary>Fired when user drags the agent panel splitter. Arg is the new ratio.</summary>
        public event EventHandler<double> AgentSplitRatioChanged;

        /// <summary>Whether the Task HUD panel is currently visible.</summary>
        public bool IsHudVisible => _terminalHudSplitter != null && !_terminalHudSplitter.Panel2Collapsed;

        /// <summary>
        /// Permanently suppresses splitter events to prevent bogus ratio saves during shutdown.
        /// </summary>
        public void FreezeLayout()
        {
            _suppressSplitterEvents = true;
            _isDisposing = true;
        }

        /// <summary>
        /// Manually toggles the Task HUD panel visibility.
        /// When showing, restores the saved splitter ratio.
        /// </summary>
        public void ToggleHud()
        {
            if (_terminalHudSplitter == null) return;

            bool show = _terminalHudSplitter.Panel2Collapsed; // collapsed → show it
            double ratioToApply = _hudSplitRatio;
            _debugLogService?.Trace("HUD-DEBUG", $"ToggleHud: show={show}, ratioToApply={ratioToApply:F4}, suppress={_suppressSplitterEvents}");

            _suppressSplitterEvents = true;
            _terminalHudSplitter.Panel2Collapsed = !show;

            if (show && _terminalHudSplitter.Height > 0 && IsHandleCreated)
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        int maxDistance = _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize;
                        int distance = (int)(_terminalHudSplitter.Height * ratioToApply);
                        // Clamp to respect Panel2MinSize
                        if (distance > maxDistance)
                            distance = maxDistance;
                        if (distance > _terminalHudSplitter.Panel1MinSize)
                            _terminalHudSplitter.SplitterDistance = distance;
                        _hudSplitRatio = ratioToApply;
                    }
                    catch { }
                    finally { _suppressSplitterEvents = false; }
                }));
            }
            else
            {
                _suppressSplitterEvents = false;
            }
        }

        /// <summary>Fired when user drags the HUD splitter. Arg is the new ratio.</summary>
        public event EventHandler<double> HudSplitRatioChanged;

        /// <summary>Fired when user drags the status bar splitter. Arg is the new height in pixels.</summary>
        public event EventHandler<int> StatusBarHeightChanged;

        /// <summary>Fired when user Ctrl+wheels in the task HUD. Arg is the new zoom factor.</summary>
        public event EventHandler<double> TaskHudZoomChanged;

        /// <summary>
        /// Injects text input into the terminal as if the user typed it.
        /// </summary>
        /// <param name="text">The text to inject.</param>
        public void InjectInput(string text)
        {
            _terminal?.InjectInput(text);
        }

        /// <summary>
        /// Injects text input into the terminal as if the user typed it.
        /// Returns a Task<bool> that completes when the Enter key has been sent.
        /// </summary>
        /// <param name="text">The text to inject.</param>
        /// <returns>A Task<bool> indicating whether injection succeeded.</returns>
        public async System.Threading.Tasks.Task<bool> InjectInputAsync(string text)
        {
            // Logging handled by TerminalControl.InjectInputAsync
            if (_terminal != null)
            {
                return await _terminal.InjectInputAsync(text);
            }
            return false; // Terminal not available
        }

        /// <summary>
        /// Sends only the Enter key without writing text. Used to retry Enter when text is already in the prompt.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SendEnterAsync()
        {
            if (_terminal != null)
            {
                return await _terminal.SendEnterAsync();
            }
            return false;
        }

        /// <summary>
        /// Types text into the terminal character-by-character via xterm.js.
        /// Mimics real keyboard typing to avoid paste detection.
        /// </summary>
        public void TypeInput(string text, string lineEnding = "cr", int charDelayMs = 15)
        {
            _terminal?.TypeInput(text, lineEnding, charDelayMs);
        }

        /// <summary>
        /// Gets the current working directory from the terminal title or last known value.
        /// </summary>
        public string GetWorkingDirectory()
        {
            string title = _terminal?.TerminalTitle;
            string extracted = ExtractDirectoryFromTitle(title);
            return extracted ?? _lastKnownDirectory;
        }

        private void OnTerminalTitleChanged(object sender, TerminalTitleChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTerminalTitleChanged(sender, e)));
                return;
            }

            // Extract directory from PowerShell title and fire event
            string directory = ExtractDirectoryFromTitle(e.Title);
            if (!string.IsNullOrEmpty(directory))
            {
                _lastKnownDirectory = directory;
                ToolTipText = directory;
                DirectoryChanged?.Invoke(this, new DirectoryChangedEventArgs(directory));
                UpdateProjectFileWatcher(directory);
            }

            // Only update tab text if no custom title is set
            if (string.IsNullOrEmpty(_customTitle))
            {
                string title = e.Title ?? $"Terminal {InstanceId}";
                if (title.Length > 30)
                {
                    title = title.Substring(0, 27) + "...";
                }
                Text = title;
                TabText = title;
            }
        }

        private string ExtractDirectoryFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return null;

            // PowerShell title format: "Administrator: C:\Users\<username>" or just "C:\Users\<username>"
            var match = Regex.Match(title, @"([A-Za-z]:\\[^""<>|*?\r\n]*)");
            if (match.Success)
            {
                string path = match.Groups[1].Value.TrimEnd();
                if (Directory.Exists(path))
                    return path;
            }

            // Handle drive root: "H:\" or "H:"
            var driveMatch = Regex.Match(title, @"([A-Za-z]:)\\?(?:\s|$)");
            if (driveMatch.Success)
            {
                string drivePath = driveMatch.Groups[1].Value + "\\";
                if (Directory.Exists(drivePath))
                    return drivePath;
            }

            return null;
        }

        private void UpdateProjectFileWatcher(string directory)
        {
            _debugLogService?.Trace("TerminalDocument", $"UpdateProjectFileWatcher called with: {directory}");

            // Skip if already watching this directory
            if (_projectFileWatcher != null && _lastWatchedDirectory == directory)
            {
                _debugLogService?.Trace("TerminalDocument", $"Already watching {directory}, skipping");
                return;
            }

            // Dispose existing watcher
            _projectFileWatcher?.Dispose();
            _projectFileWatcher = null;

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            _lastWatchedDirectory = directory;
            string claudeFolder = Path.Combine(directory, ".claude");

            try
            {
                // Create watcher for .claude folder if it exists, otherwise watch for its creation
                if (Directory.Exists(claudeFolder))
                {
                    _projectFileWatcher = new FileSystemWatcher
                    {
                        Path = claudeFolder,
                        Filter = "project.json",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                    };
                }
                else
                {
                    // Watch for .claude folder creation
                    _projectFileWatcher = new FileSystemWatcher
                    {
                        Path = directory,
                        Filter = ".claude",
                        NotifyFilter = NotifyFilters.DirectoryName
                    };
                }

                _projectFileWatcher.Created += OnProjectFileEvent;
                _projectFileWatcher.Changed += OnProjectFileEvent;
                _projectFileWatcher.EnableRaisingEvents = true;
                _debugLogService?.Trace("TerminalDocument", $"FileSystemWatcher created for: {_projectFileWatcher.Path}, Filter: {_projectFileWatcher.Filter}");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument", $"Failed to create FileSystemWatcher: {ex.Message}");
            }
        }

        private void OnProjectFileEvent(object sender, FileSystemEventArgs e)
        {
            _debugLogService?.Trace("TerminalDocument", $"OnProjectFileEvent: Name={e.Name}, ChangeType={e.ChangeType}, FullPath={e.FullPath}");

            // Check if it's the project.json file or .claude folder
            if (e.Name == "project.json" || e.Name == ".claude")
            {
                // If .claude folder was just created, switch to watching for project.json
                if (e.Name == ".claude" && e.ChangeType == WatcherChangeTypes.Created)
                {
                    string claudeFolder = e.FullPath;
                    if (Directory.Exists(claudeFolder))
                    {
                        try
                        {
                            _projectFileWatcher?.Dispose();
                            _projectFileWatcher = new FileSystemWatcher
                            {
                                Path = claudeFolder,
                                Filter = "project.json",
                                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                            };
                            _projectFileWatcher.Created += OnProjectFileEvent;
                            _projectFileWatcher.Changed += OnProjectFileEvent;
                            _projectFileWatcher.EnableRaisingEvents = true;

                            // Check if project.json already exists (might have been created before watcher was ready)
                            string projectJsonPath = Path.Combine(claudeFolder, "project.json");
                            if (File.Exists(projectJsonPath))
                            {
                                // Fire event since file already exists
                                if (this.DockPanel?.InvokeRequired == true)
                                {
                                    this.DockPanel.BeginInvoke(new Action(() => ProjectFileChanged?.Invoke(this, EventArgs.Empty)));
                                }
                                else
                                {
                                    ProjectFileChanged?.Invoke(this, EventArgs.Empty);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _debugLogService?.Error("TerminalDocument", $"Failed to update FileSystemWatcher: {ex.Message}");
                        }
                    }
                    return;
                }

                // Marshal to UI thread and fire event
                _debugLogService?.Trace("TerminalDocument", $"Firing ProjectFileChanged event");
                if (this.DockPanel?.InvokeRequired == true)
                {
                    this.DockPanel.BeginInvoke(new Action(() => ProjectFileChanged?.Invoke(this, EventArgs.Empty)));
                }
                else
                {
                    ProjectFileChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void OnTerminalProcessExited(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTerminalProcessExited(sender, e)));
                return;
            }

            // Reset terminal state so the tab can be reused for a new session
            _isTerminalStarted = false;

            // Return to start screen on process exit so the tab can be reused
            ShowStartScreen();

            TerminalExited?.Invoke(this, EventArgs.Empty);
        }

        private void OnTerminalContextMenuRequested(object sender, TerminalContextMenuEventArgs e)
        {
            // Create context menu
            var menu = new ContextMenuStrip();

            // Copy
            var copyItem = new ToolStripMenuItem("Copy");
            copyItem.Enabled = !string.IsNullOrEmpty(e.SelectedText);
            copyItem.Click += (s, args) =>
            {
                if (!string.IsNullOrEmpty(e.SelectedText))
                {
                    Clipboard.SetText(e.SelectedText);
                }
            };
            menu.Items.Add(copyItem);

            // Paste
            var pasteItem = new ToolStripMenuItem("Paste");
            pasteItem.Enabled = Clipboard.ContainsText();
            pasteItem.Click += (s, args) =>
            {
                if (Clipboard.ContainsText())
                {
                    _terminal.Write(Clipboard.GetText());
                }
            };
            menu.Items.Add(pasteItem);

            // Save as Prompt
            var savePromptItem = new ToolStripMenuItem("Save as Prompt...");
            savePromptItem.Enabled = !string.IsNullOrEmpty(e.SelectedText);
            savePromptItem.Click += (s, args) =>
            {
                SaveAsPromptRequested?.Invoke(this, new SavePromptRequestEventArgs(e.SelectedText));
            };
            menu.Items.Add(savePromptItem);

            menu.Items.Add(new ToolStripSeparator());

            // Clear
            var clearItem = new ToolStripMenuItem("Clear");
            clearItem.Click += (s, args) => _terminal.Clear();
            menu.Items.Add(clearItem);

            menu.Items.Add(new ToolStripSeparator());

            // Rename Tab
            var renameItem = new ToolStripMenuItem("Rename Tab...");
            renameItem.Click += (s, args) => ShowRenameDialog();
            menu.Items.Add(renameItem);

            // Launch as... submenu (for identity-based terminal launch)
            var identities = GetAvailableIdentities?.Invoke();
            if (identities != null && identities.Length > 0)
            {
                menu.Items.Add(new ToolStripSeparator());

                var launchAsMenu = new ToolStripMenuItem("Launch as...");
                foreach (var identity in identities)
                {
                    var identityName = identity; // Capture for closure
                    var item = new ToolStripMenuItem(identityName);
                    item.Click += (s, args) =>
                    {
                        LaunchAsIdentityRequested?.Invoke(this, new LaunchAsIdentityEventArgs(identityName));
                    };
                    launchAsMenu.DropDownItems.Add(item);
                }
                menu.Items.Add(launchAsMenu);
            }

            // Show menu (store reference so it can be closed on terminal click)
            _currentContextMenu = menu;
            menu.Show(_terminal, e.Location);
        }

        /// <summary>
        /// Injects a ProjectDatabase (and optional ProjectService) so the start screen can list projects.
        /// Call after construction, before the document is shown.
        /// </summary>
        public void SetProjectDatabase(ProjectDatabase projectDatabase, ProjectService projectService = null)
        {
            _startScreen?.Initialize(projectDatabase, projectService);
        }

        /// <summary>
        /// Re-fetches the project list and re-renders the start-screen card grid. Called by
        /// MainForm after a project is deleted from a card so the removed project disappears.
        /// </summary>
        public void RefreshStartScreenProjects()
        {
            _startScreen?.RefreshProjects();
        }

        /// <summary>
        /// Stops any running terminal process and returns to the start screen.
        /// Used by Home context menu and Ctrl+Shift+H hotkey.
        /// </summary>
        public void ReturnToStartScreen()
        {
            StopStatusLinePolling();
            if (_isTerminalStarted)
            {
                _terminal.Stop();
                _isTerminalStarted = false;

                // Notify MainForm — OnTerminalExited handles UnregisterTerminal + doc map cleanup
                // (consistent with OnTerminalProcessExited pattern)
                TerminalExited?.Invoke(this, EventArgs.Empty);
            }
            ShowStartScreen();
        }

        /// <summary>
        /// Show the start screen and hide the terminal area.
        /// Called on new tab creation (before shell starts) and after shell exit.
        /// WebView2 controls use native HWNDs that don't respect WinForms Z-order,
        /// so we must explicitly hide the terminal controls to prevent them painting
        /// over the start screen.
        /// </summary>
        public void ShowStartScreen()
        {
            if (_startScreen == null) return;
            _isStartScreenVisible = true;

            // Hide terminal area to prevent WebView2 HWND Z-order conflicts
            _terminalAgentSplitter.Visible = false;
            if (_statusBarSplitter != null) _statusBarSplitter.Visible = false;
            _statusBar.Visible = false;

            // Show start screen over the entire content area
            _startScreen.BringToFront();
            _startScreen.Visible = true;
            _startScreen.RefreshProjects();

            // Update tab title
            Text = "Home";
            TabText = "Home";

            // Give WebView2 keyboard focus so the start screen is immediately interactive
            _startScreen.Focus();
        }

        /// <summary>
        /// Hide the start screen and reveal the terminal area.
        /// Called by StartTerminal() just before the shell launches.
        /// Must re-show terminal controls that were hidden by ShowStartScreen().
        /// </summary>
        public void HideStartScreen()
        {
            if (_startScreen == null) return;
            _isStartScreenVisible = false;
            _startScreen.Visible = false;

            // Re-show terminal area (hidden by ShowStartScreen to fix WebView2 Z-order)
            _terminalAgentSplitter.Visible = true;
            if (_statusBarSplitter != null) _statusBarSplitter.Visible = true;
            _statusBar.Visible = true;
        }

        /// <summary>
        /// Whether the start screen is currently visible (shell not yet started / has exited).
        /// </summary>
        public bool IsStartScreenVisible => _isStartScreenVisible;

        /// <summary>
        /// Sets the debug log service for status bar, task HUD, and terminal (ConPTY +
        /// WebView2 renderer) logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
            _statusBar?.SetDebugLogService(debugLogService);
            _hudTabContainer?.TaskHud?.SetDebugLogService(debugLogService);
            _terminal?.SetDebugLogService(debugLogService);
            _startScreen?.SetDebugLogService(debugLogService); // StartScreenControl diagnostics → unified sink (4c86f18d)
        }

        /// <summary>
        /// Sets the message broker for status bar and task HUD updates.
        /// </summary>
        public void SetMessageBroker(MessageBroker broker)
        {
            _messageBroker = broker;

            if (_messageBroker?.ActivityService != null)
            {
                // Subscribe to activity updates for this terminal. Pre-detach
                // for symmetry with the other broker subscriptions below — re-
                // entry into SetMessageBroker would otherwise leak duplicate
                // handlers.
                _messageBroker.ActivityService.ActivityUpdated -= OnActivityUpdated;
                _messageBroker.ActivityService.ActivityUpdated += OnActivityUpdated;
            }

            // Keep the Input: Local/Remote pill in sync with server-side remoteMode flips.
            // Pre-detach pattern matches the TaskActiveChanged subscription below so
            // re-entry into SetMessageBroker (e.g. broker re-assignment) doesn't double-
            // subscribe and double-fire handlers.
            if (_messageBroker != null)
            {
                _messageBroker.RemoteModeChanged -= OnRemoteModeChanged;
                _messageBroker.RemoteModeChanged += OnRemoteModeChanged;
                // Push current state immediately (renderer buffers if not yet ready)
                _statusBar?.SetRemoteMode(_messageBroker.IsRemoteMode);

                // Rebind the HUD Git panel when this terminal's active task
                // moves to a different worktree. SetMessageBroker is the right
                // hook because (a) broker assignment happens before any task
                // can be activated by this terminal's agent, and (b) detach
                // happens in Dispose alongside the other broker unsubscribes.
                //
                // Subscribe ONLY — don't seed _originalAgentName here. The
                // broker can arrive before any title is set (the documented
                // late-name path) and before the live launch identity is
                // known, so promoting from CustomTitle at this site can lock
                // in a non-authoritative value (restored sessionInfo title,
                // empty string, etc.). PromoteOriginalAgentName is called
                // from authoritative sources only — StartTerminal and
                // MainForm.OnMcpTerminalRegistered (cycle-7 codex-adversary
                // HIGH fix).
                _messageBroker.TaskActiveChanged -= OnBrokerTaskActiveChanged;
                _messageBroker.TaskActiveChanged += OnBrokerTaskActiveChanged;
            }

            // Wire task HUD to broker.
            // If CustomTitle is already set, initialize fully.
            // If not, still call Initialize so HUD can pick up any _pendingTerminalName
            // set by an earlier SetTerminalName() call (e.g. OnLaunchAsIdentityRequested path).
            var taskHud = _hudTabContainer?.TaskHud;
            if (!string.IsNullOrEmpty(CustomTitle))
            {
                taskHud?.Initialize(broker, CustomTitle);
            }
            else if (taskHud?.HasPendingTerminalName == true)
            {
                // Name arrived before broker via SetTerminalName — initialize now
                taskHud.Initialize(broker, null);
            }

            // Wire HUD tabs to broker
            _hudDashboard?.Initialize(broker);
            _hudDashboard.ShowDiffRequested -= OnShowDiffRequested;
            _hudDashboard.ShowDiffRequested += OnShowDiffRequested;
            _hudDashboard.OpenGitTabRequested -= OnOpenGitTabRequested;
            _hudDashboard.OpenGitTabRequested += OnOpenGitTabRequested;
            _hudNotes?.Initialize(broker);
            _hudKnowledge?.Initialize(broker);
            _hudGit?.Initialize(broker);
            if (_hudGit != null)
            {
                _hudGit.OpenDiffPopupRequested -= OnHudGitOpenDiffPopupRequested;
                _hudGit.OpenDiffPopupRequested += OnHudGitOpenDiffPopupRequested;
            }
            _hudSessions?.Initialize(broker);
            if (!string.IsNullOrEmpty(CustomTitle))
                _hudDashboard?.SetTerminalName(CustomTitle);

            // Update status bar if terminal name is already set
            // Otherwise, StartTerminal() will call UpdateStatusBar() later
            if (!string.IsNullOrEmpty(CustomTitle))
            {
                UpdateStatusBar();
            }
        }

        /// <summary>
        /// Pushes a remoteMode change to the status bar renderer. Runs on UI thread.
        /// </summary>
        private void OnRemoteModeChanged(object sender, bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnRemoteModeChanged(sender, enabled)));
                return;
            }
            _statusBar?.SetRemoteMode(enabled);
        }

        // Rebind the HUD Git panel when our terminal's active task moves to a
        // different worktree (or to none). TaskActiveChanged is broadcast in-
        // proc to every subscriber; filter by _originalAgentName (captured at
        // subscribe time, immune to subsequent tab renames) so we don't react
        // to other agents' task switches and don't go silently no-op after a
        // user renames this tab.
        //
        // When the resolved worktree path is null/empty (worktree mode off or
        // task without a worktree), leave the panel where it is — the user
        // explicitly opened the terminal on a particular repo and the kanban
        // state change shouldn't yank it away.
        //
        // Path comes from broker.Worktrees.GetWorktreePathForTask, NOT from
        // args.NewWorktreePath. TaskActiveChangedEventArgs is now immutable
        // (cycle-6 fix), so the original mutation attack is closed at the
        // type level — we still re-resolve here as fresh-state preference:
        // GetWorktreePathForTask reads broker DB at handler-run time, which
        // is more current than the snapshot captured by the event producer
        // if any worktree state shifted between fire and dispatch.
        private void OnBrokerTaskActiveChanged(object sender, MCPServer.Services.TaskActiveChangedEventArgs args)
        {
            if (args == null) return;
            if (string.IsNullOrEmpty(_originalAgentName))
            {
                // One-shot diagnostic — if a terminal reaches this branch its
                // PromoteOriginalAgentName hasn't been called yet. Cycle-7:
                // promotion happens via StartTerminal and MainForm's
                // OnMcpTerminalRegistered. In the restored-terminal window
                // BEFORE the user picks a project (no Claude process running
                // for this doc yet) this branch will fire for cross-agent
                // broker chatter and the event is correctly dropped. If you
                // see this log AFTER a deliberate launch, the registration
                // ordering needs investigation.
                if (!_loggedEmptyAgentName)
                {
                    _loggedEmptyAgentName = true;
                    _debugLogService?.Warning("TerminalDocument.OnBrokerTaskActiveChanged", $"Empty _originalAgentName — dropping event AgentName='{args.AgentName}' NewTaskId='{args.NewTaskId}'. Expected during the restored-terminal window before user launches; investigate if seen after StartTerminal/OnMcpTerminalRegistered have run.");
                }
                return;
            }
            if (!string.Equals(args.AgentName, _originalAgentName, StringComparison.Ordinal)) return;
            if (_hudGit == null) return;

            // Re-resolve worktree path AND projectId from broker-owned state
            // rather than trusting the (mutable) event payload.
            string resolvedWorktreePath = null;
            string newProjectId = null;
            try
            {
                if (!string.IsNullOrEmpty(args.NewTaskId))
                {
                    // Per-agent isolation: resolve THIS terminal's own worktree — the
                    // event now carries the acting agent (which may be a helper), so a
                    // single-arg lookup would rebind a helper's HUD to the assignee's
                    // canonical worktree. Fall back to canonical. Mirrors the REST
                    // auto-cd path (ResolveTaskWorktreePath) so in-proc + REST agree.
                    resolvedWorktreePath = _messageBroker?.Worktrees?.GetWorktreePathForTask(args.NewTaskId, args.AgentName)
                                           ?? _messageBroker?.Worktrees?.GetWorktreePathForTask(args.NewTaskId);
                    newProjectId = _messageBroker?.GetTask(args.NewTaskId)?.ProjectId;
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument.OnBrokerTaskActiveChanged", $"broker lookup failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(resolvedWorktreePath)) return;

            try
            {
                if (InvokeRequired)
                {
                    string capturedProjectId = newProjectId;
                    string capturedWorktreePath = resolvedWorktreePath;
                    BeginInvoke(new Action(() => _hudGit?.SetProject(capturedProjectId, capturedWorktreePath)));
                }
                else
                {
                    _hudGit.SetProject(newProjectId, resolvedWorktreePath);
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument.OnBrokerTaskActiveChanged", $"SetProject failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when terminal activity is updated in the ActivityService.
        /// </summary>
        private void OnActivityUpdated(object sender, TerminalActivity activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnActivityUpdated(sender, activity)));
                return;
            }

            // Only update if this activity belongs to our terminal
            if (activity.Terminal == CustomTitle)
            {
                UpdateStatusBar();
            }
        }

        /// <summary>
        /// Updates the status bar with current terminal information.
        /// </summary>
        public void UpdateStatusBar()
        {
            if (_statusBar == null)
            {
                _debugLogService?.Trace("TerminalDocument", "UpdateStatusBar: _statusBar is null");
                return;
            }

            // Wait for both name and broker to be set before updating
            // This prevents showing "Terminal" placeholder before identity is set
            string terminalName = CustomTitle ?? TabText ?? "Terminal";
            if (string.IsNullOrEmpty(CustomTitle) && _messageBroker == null)
            {
                _debugLogService?.Trace("TerminalDocument", "UpdateStatusBar: Skipping - no custom title and no broker yet");
                return;
            }

            string avatarUrl = null;
            string activityDescription = null;
            string taskTitle = null;
            string taskId = null;
            string status = "idle";

            // Get profile and activity data from MessageBroker
            if (_messageBroker != null)
            {
                // Get profile for avatar
                var profileResult = _messageBroker.GetProfile(terminalName);
                if (profileResult != null && profileResult.Success && profileResult.Profile != null)
                {
                    avatarUrl = profileResult.Profile.AvatarUrl;
                }

                // Get activity for current task
                var activity = _messageBroker.ActivityService?.GetActivity(terminalName);
                if (activity != null)
                {
                    status = activity.Status ?? "idle";
                    activityDescription = activity.Activity;
                    taskId = activity.TaskId;

                    // Get task title if task ID is set
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        var task = _messageBroker.GetTasks().FirstOrDefault(t => t.Id == taskId);
                        if (task != null)
                        {
                            taskTitle = task.Title;
                        }
                    }
                }
            }
            else
            {
                _debugLogService?.Trace("TerminalDocument", $"UpdateStatusBar: _messageBroker is null, updating with name only: {terminalName}");
            }

            // Look up project by working directory path. If the folder isn't a
            // registered MT project, fall back to the folder's leaf name so Row 1
            // still shows something meaningful (not the agent name).
            string projectName = null;
            string projectDescription = null;
            string workDir = GetWorkingDirectory();
            if (_messageBroker?.ProjectService != null && !string.IsNullOrEmpty(workDir))
            {
                try
                {
                    var projects = _messageBroker.ProjectService.GetAllRegisteredProjects();
                    var matchedEntry = projects.FirstOrDefault(p =>
                        !string.IsNullOrEmpty(p.Path) &&
                        string.Equals(p.Path.TrimEnd('\\', '/'), workDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (matchedEntry != null)
                    {
                        projectName = matchedEntry.Name;
                        // Load full project for description
                        var fullProject = _messageBroker.ProjectService.LoadProject(matchedEntry.Path);
                        if (fullProject != null)
                            projectDescription = fullProject.Description;
                    }
                }
                catch { /* Non-critical — fall back to folder name below */ }
            }
            if (string.IsNullOrEmpty(projectName) && !string.IsNullOrEmpty(workDir))
            {
                projectName = System.IO.Path.GetFileName(workDir.TrimEnd('\\', '/'));
            }

            _debugLogService?.Trace("TerminalDocument", $"UpdateStatusBar: Calling _statusBar.UpdateStatus with:");
            _debugLogService?.Trace("TerminalDocument", $"  - terminalName: '{terminalName}'");
            _debugLogService?.Trace("TerminalDocument", $"  - avatarUrl: '{avatarUrl}'");
            _debugLogService?.Trace("TerminalDocument", $"  - activityDescription: '{activityDescription}'");
            _debugLogService?.Trace("TerminalDocument", $"  - taskTitle: '{taskTitle}'");
            _debugLogService?.Trace("TerminalDocument", $"  - taskId: '{taskId}'");
            _debugLogService?.Trace("TerminalDocument", $"  - status: '{status}'");
            _debugLogService?.Trace("TerminalDocument", $"  - projectName: '{projectName}'");

            _statusBar.UpdateStatus(terminalName, avatarUrl, activityDescription, taskTitle, taskId, status, projectName, projectDescription);

            _debugLogService?.Trace("TerminalDocument", "_statusBar.UpdateStatus call completed");
        }

        /// <summary>
        /// Pre-writes a fallback statusline JSON file containing just the folder path
        /// and terminal name. Ensures the header renders something (folder row + model
        /// placeholder) even if the child Claude Code process has a project-level
        /// statusLine override that points elsewhere and never invokes our script.
        /// The real statusline.js overwrites this on its first tick if it runs.
        /// </summary>
        private void WriteFallbackStatusline(string terminalName, string workingDirectory)
        {
            if (string.IsNullOrEmpty(terminalName) || string.IsNullOrEmpty(_docId)) return;

            try
            {
                string folder = workingDirectory ?? "";
                string folderName = string.IsNullOrEmpty(folder)
                    ? ""
                    : Path.GetFileName(folder.TrimEnd('\\', '/'));

                var fallback = new Dictionary<string, object>
                {
                    ["terminalName"] = terminalName,
                    ["model"] = (string)null,
                    ["folder"] = folder,
                    ["folderName"] = folderName,
                    ["contextPct"] = (int?)null,
                    ["quota5h"] = (int?)null,
                    ["quota7d"] = (int?)null,
                    ["pace5h"] = (int?)null,
                    ["pace7d"] = (int?)null,
                    ["resetIn5h"] = (string)null,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["fallback"] = true
                };

                string filePath = Path.Combine(
                    Path.GetTempPath(),
                    $"mt-statusline-{terminalName}-{_docId}.json");

                File.WriteAllText(filePath, JsonSerializer.Serialize(fallback));
                _debugLogService?.Trace("TerminalDocument.WriteFallbackStatusline", $"Wrote fallback to '{filePath}'");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument.WriteFallbackStatusline", $"Failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves which statusline temp file to read for this terminal. Prefers the
        /// docId-scoped file (<c>mt-statusline-{name}-{_docId}.json</c>) written for a
        /// freshly spawned child. If that's missing — the restore/re-adopt case where the
        /// child keeps writing under a prior instance's docId while this TerminalDocument
        /// minted a new random <c>_docId</c> — returns the newest
        /// <c>mt-statusline-{name}-*.json</c> by its embedded <c>timestamp</c> so the
        /// restored terminal's banner picks up live data instead of staying blank. Returns
        /// <c>null</c> when no candidate exists. Mirrors the name-based fallback the REST
        /// stats endpoint already uses (StatusLineStatsReader.FindNewestPerTerminalFile),
        /// including its skip of future-dated (clock-skewed/planted) files.
        ///
        /// <para>Freshness floor (task 1ba59334): glob candidates older than this
        /// terminal's launch (<see cref="_launchedAtMs"/>) are rejected, so a terminal can
        /// never adopt a PRIOR run's or a foreign same-named terminal's frozen value (the
        /// symptom: a restored terminal whose child uses a project statusLine override —
        /// e.g. a Clarion-addin terminal — writes no fresh mt-statusline file, and the
        /// banner stuck on a stale file). A legitimately restored MT child keeps writing
        /// fresh files (timestamp &gt; floor) so it is still adopted; only stale files are
        /// dropped, leaving the fresh blank placeholder rather than a misleading number.</para>
        /// </summary>
        private string ResolveStatusLineFilePath(string terminalName)
        {
            string exact = Path.Combine(Path.GetTempPath(), $"mt-statusline-{terminalName}-{_docId}.json");
            if (File.Exists(exact)) return exact;

            // Only the fallback glob needs a safe segment; reject names that could widen
            // the search beyond the intended mt-statusline-{name}-*.json shape.
            if (!IsSafeStatusLineSegment(terminalName)) return null;

            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(Path.GetTempPath(), $"mt-statusline-{terminalName}-*.json");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            string newest = null;
            long newestTs = long.MinValue;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var path in candidates)
            {
                try
                {
                    string content = ReadAllTextShared(path);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    using var doc = JsonDocument.Parse(content);
                    if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl) ||
                        tsEl.ValueKind != JsonValueKind.Number) continue;
                    long ts = tsEl.GetInt64();
                    if (ts > nowMs) continue;          // ignore future-dated (skewed/planted) files
                    // Reject stale prior-run / foreign files (task 1ba59334): never show a frozen
                    // value from before this terminal launched. A grace window admits a render that
                    // landed just before an MT restart/re-adopt — so a RESTORED IDLE terminal (whose
                    // child may not re-render after re-adopt, codex adversary MEDIUM) still shows its
                    // last-known data instead of a permanent blank — while hours-old stale files are
                    // still rejected.
                    if (ts < _launchedAtMs - StatuslineFreshnessGraceMs) continue;
                    if (ts > newestTs) { newestTs = ts; newest = path; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    // Torn/corrupt/locked sibling — skip it, keep scanning.
                }
            }
            return newest;
        }

        /// <summary>
        /// True when <paramref name="s"/> is a safe path segment for the statusline glob:
        /// letters, digits, '-' or '_' only (no separators, ':' or '.'). Matches the shape
        /// of MT terminal names / docIds and keeps the fallback glob confined.
        /// </summary>
        private static bool IsSafeStatusLineSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Reads a file while allowing other processes to concurrently write, rename,
        /// or delete it (FileShare.ReadWrite | FileShare.Delete). statusline.js replaces
        /// these temp files via an atomic write-temp-then-rename; a plain File.ReadAllText
        /// opens with FileShare.Read only, which on Windows blocks that rename-replace
        /// (transient EPERM/EACCES on the writer) and can drop a status/quota update —
        /// the exact cross-process contention the atomic write was meant to eliminate.
        /// Sharing Delete lets the writer's rename succeed while this handle still reads a
        /// consistent snapshot of the pre-rename content.
        /// </summary>
        private static string ReadAllTextShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Resolves the canonical, registered project path for the Notes/Sessions
        /// HUD panes (which are keyed per-project, not per-folder). Prefers a lookup
        /// by projectId; falls back to matching the folder against the registered
        /// project list. Returns null when the project can't be resolved to a
        /// registered entry (caller decides the fallback). Never returns a raw
        /// worktree path — that's the whole point: worktree paths have no notes and
        /// cause an empty pane plus an orphan auto-created note-tab row.
        ///
        /// The canonical key is <c>SourcePath ?? Path</c> — the SAME precedence the
        /// launcher uses to pick a terminal's working directory (LaunchCommandBuilder
        /// "SourcePath → Path", Project.cs "Use SourcePath as the canonical field").
        /// Historical note tabs were keyed by that working directory, so resolving by
        /// Path alone would re-key Notes to a different path than the data lives under
        /// for any project whose SourcePath and Path diverge — re-introducing the
        /// empty pane this fix is meant to eliminate. We also match the folder against
        /// BOTH identity fields so either one locates the project.
        /// </summary>
        private string TryResolveCanonicalProjectPath(string projectId, string folder)
        {
            try
            {
                if (!string.IsNullOrEmpty(projectId))
                {
                    // GetRichProject (not GetProject): only the rich Models.Project
                    // carries SourcePath; the lightweight MCPServer.Models.Project doesn't.
                    string canon = CanonicalProjectKey(_messageBroker?.ProjectDatabase?.GetRichProject(projectId));
                    if (!string.IsNullOrEmpty(canon)) return canon;
                }

                if (!string.IsNullOrEmpty(folder))
                {
                    string normFolder = NormalizeProjectPath(folder);
                    // Use the rich project list (carries SourcePath) so the folder
                    // match can consider both identity fields. Ambiguity guard: if the
                    // folder matches more than one DISTINCT project key (e.g. project
                    // A's SourcePath == project B's Path, since the schema enforces no
                    // uniqueness on those fields), DON'T remap — picking the first row
                    // would silently bind Notes/Sessions to the wrong project's notes.
                    var projects = _messageBroker?.ProjectDatabase?.GetAllRichProjects();
                    if (projects != null)
                    {
                        string firstMatch = null;
                        foreach (var p in projects)
                        {
                            if (ProjectFieldMatchesFolder(p?.SourcePath, normFolder) ||
                                ProjectFieldMatchesFolder(p?.Path, normFolder))
                            {
                                string canon = CanonicalProjectKey(p);
                                if (string.IsNullOrEmpty(canon)) continue;
                                if (firstMatch == null)
                                {
                                    firstMatch = canon;
                                }
                                // Compare distinctness on the NORMALIZED key so two registry
                                // rows for the SAME folder that differ only by formatting
                                // (trailing slash, slash style, case) don't read as an
                                // ambiguous multi-project match. Only a genuinely different
                                // folder trips the guard. We still RETURN the raw firstMatch.
                                else if (!string.Equals(NormalizeProjectPath(firstMatch), NormalizeProjectPath(canon),
                                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    _debugLogService?.Trace("TerminalDocument",
                                        $"Notes key: folder '{folder}' matches multiple projects — not remapping to avoid cross-project notes");
                                    return null;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(firstMatch)) return firstMatch;
                    }
                }
            }
            catch { /* non-critical — caller falls back */ }

            return null;
        }

        /// <summary>
        /// The note-tab key for a project: the RAW <c>SourcePath ?? Path</c>, matching the
        /// launcher's working-dir precedence; null when the project is null or has neither
        /// field.
        ///
        /// Kept raw here for symmetry with launch/folder resolution — the same
        /// <c>SourcePath ?? Path</c> identity is also matched against both fields during
        /// folder lookup, where the original spelling matters. Canonicalization of the
        /// stored key is owned by the persistence layer: <c>TaskDatabase.NormalizeNoteTabPath</c>
        /// (full path + trailing-separator trim) runs on every note-tab read/write and a
        /// one-time migration rewrites pre-existing rows, so trailing-slash / slash-style
        /// spelling differences in the key handed down from here are absorbed downstream —
        /// this method need not (and must not, to stay symmetric with folder matching)
        /// normalize. Case-insensitive needs are applied at each comparison site
        /// (OrdinalIgnoreCase), not baked into the stored key.
        /// </summary>
        private static string CanonicalProjectKey(MultiTerminal.Models.Project project)
        {
            if (project == null) return null;
            string raw = !string.IsNullOrEmpty(project.SourcePath) ? project.SourcePath : project.Path;
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        /// <summary>True when a project identity field normalizes to the given folder.</summary>
        private static bool ProjectFieldMatchesFolder(string projectField, string normalizedFolder)
        {
            if (string.IsNullOrEmpty(projectField)) return false;
            return string.Equals(NormalizeProjectPath(projectField), normalizedFolder,
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Canonicalizes a project path so the same project always maps to one
        /// note-tab key: full path with trailing separators trimmed. Guards against
        /// GetFullPath throwing on malformed input by falling back to a simple trim.
        /// </summary>
        private static string NormalizeProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        /// <summary>
        /// Starts polling the status line temp file for Claude Code session data.
        /// Called when the terminal identity is known (CustomTitle is set).
        /// </summary>
        public void StartStatusLinePolling()
        {
            if (_statusLineTimer != null) return;

            string terminalName = CustomTitle;
            if (string.IsNullOrEmpty(terminalName)) return;

            _statusLineTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    // File path is scoped by both terminal name AND docId so sibling
                    // terminals with the same name can't read each other's statusline.
                    // When the docId-scoped file is absent — a terminal restored/re-adopted
                    // after an MT restart, whose child Claude process keeps writing under
                    // the PRIOR instance's docId while this fresh TerminalDocument minted a
                    // new random _docId — fall back to the newest mt-statusline-{name}-*.json
                    // so the banner recovers live instead of staying blank until relaunch.
                    string filePath = ResolveStatusLineFilePath(terminalName);
                    if (filePath == null) return;

                    string content = ReadAllTextShared(filePath);
                    if (string.IsNullOrEmpty(content)) return;

                    // Check if either the per-terminal file or shared quota file changed
                    string sharedQuotaPath = Path.Combine(Path.GetTempPath(), "mt-statusline-quota.json");
                    string sharedContent = null;
                    try { if (File.Exists(sharedQuotaPath)) sharedContent = ReadAllTextShared(sharedQuotaPath); } catch { }

                    bool perTerminalChanged = content != _lastStatusLineContent;
                    bool sharedQuotaChanged = sharedContent != null && sharedContent != _lastSharedQuotaContent;

                    // Keep delivering even when content is unchanged until the renderer confirms it
                    // actually un-hid rows 2-3 (task d14048ef). A delivery that raced the WebView's
                    // async ready would otherwise be suppressed by change-detection forever, leaving
                    // the folder + stats bands hidden. Once JS acks (IsStatusLineRendered flips true),
                    // normal change-detection resumes.
                    bool awaitingRender = _statusBar != null && _statusBar.IsInitialized && !_statusBar.IsStatusLineRendered;
                    if (!perTerminalChanged && !sharedQuotaChanged && !awaitingRender) return;

                    _debugLogService?.Trace("TerminalDocument", $"statusline poll delivering (changed={perTerminalChanged}, quotaChanged={sharedQuotaChanged}, awaitingRender={awaitingRender})");

                    // Only cache content AFTER successfully delivering to UI thread.
                    // Otherwise, if IsHandleCreated is false on first read, the content
                    // gets cached but never delivered — and subsequent polls skip it.
                    if (!IsHandleCreated || IsDisposed) return;

                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    string model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                    string folder = root.TryGetProperty("folder", out var f) ? f.GetString() : null;

                    // If statusline reports the home directory but we have a real project path, use that instead.
                    // Claude Code's first statusline event can report the shell's cwd before navigating to the project.
                    // (Outer guard: only worth the home-dir env lookup when both folder and the starting dir exist.)
                    if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(_startingWorkingDirectory))
                    {
                        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (string.Equals(folder.TrimEnd('\\', '/'), homeDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            folder = _startingWorkingDirectory;
                        }
                    }

                    // Folder fallback (task d14048ef, gap #4): the JS leaves row-folder hidden when
                    // `folder` is empty. If the statusline carries no folder yet, use the known
                    // starting working directory so the path band still renders instead of vanishing.
                    if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrEmpty(_startingWorkingDirectory))
                    {
                        folder = _startingWorkingDirectory;
                    }

                    int? contextPct = root.TryGetProperty("contextPct", out var cp) && cp.ValueKind == JsonValueKind.Number
                        ? cp.GetInt32() : (int?)null;
                    // Token meter (task f2702f69): sessionId + transcriptPath locate this terminal's
                    // Claude transcript JSONL to tail for live token totals.
                    string tokenSessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                        ? sidEl.GetString() : null;
                    string transcriptPath = root.TryGetProperty("transcriptPath", out var tpEl) && tpEl.ValueKind == JsonValueKind.String
                        ? tpEl.GetString() : null;
                    // Read account-level quota from the shared file so all terminals
                    // show identical 5h/7d stats regardless of which one was updated last.
                    int? quota5h = null, quota7d = null, pace5h = null, pace7d = null;
                    string resetIn5h = null;

                    try
                    {
                        // Reuse sharedContent already read during change detection above
                        if (!string.IsNullOrEmpty(sharedContent))
                        {
                            using var sharedDoc = JsonDocument.Parse(sharedContent);
                            var sq = sharedDoc.RootElement;
                            quota5h = sq.TryGetProperty("quota5h", out var sq5) && sq5.ValueKind == JsonValueKind.Number
                                ? sq5.GetInt32() : (int?)null;
                            quota7d = sq.TryGetProperty("quota7d", out var sq7) && sq7.ValueKind == JsonValueKind.Number
                                ? sq7.GetInt32() : (int?)null;
                            pace5h = sq.TryGetProperty("pace5h", out var sp5) && sp5.ValueKind == JsonValueKind.Number
                                ? sp5.GetInt32() : (int?)null;
                            pace7d = sq.TryGetProperty("pace7d", out var sp7) && sp7.ValueKind == JsonValueKind.Number
                                ? sp7.GetInt32() : (int?)null;
                            resetIn5h = sq.TryGetProperty("resetIn5h", out var sr5) && sr5.ValueKind == JsonValueKind.String
                                ? sr5.GetString() : null;
                            _lastSharedQuotaContent = sharedContent;
                        }
                    }
                    catch (Exception sharedEx)
                    {
                        // Shared file may be mid-write; fall back to per-terminal data.
                        // Log it (not silent) so persistent corruption is observable —
                        // a torn write that never self-heals previously left this null
                        // forever, sticking 5h/7d on "--%" for non-Claude terminals.
                        _debugLogService?.Trace("TerminalDocument",
                            $"StatusLinePoll: shared quota parse failed ({sharedEx.Message}); falling back to per-terminal quota");
                    }

                    // Fall back to per-terminal quota if shared file unavailable
                    if (quota5h == null && quota7d == null)
                    {
                        quota5h = root.TryGetProperty("quota5h", out var q5) && q5.ValueKind == JsonValueKind.Number
                            ? q5.GetInt32() : (int?)null;
                        quota7d = root.TryGetProperty("quota7d", out var q7) && q7.ValueKind == JsonValueKind.Number
                            ? q7.GetInt32() : (int?)null;
                        pace5h = root.TryGetProperty("pace5h", out var p5) && p5.ValueKind == JsonValueKind.Number
                            ? p5.GetInt32() : (int?)null;
                        pace7d = root.TryGetProperty("pace7d", out var p7) && p7.ValueKind == JsonValueKind.Number
                            ? p7.GetInt32() : (int?)null;
                        resetIn5h = root.TryGetProperty("resetIn5h", out var r5) && r5.ValueKind == JsonValueKind.String
                            ? r5.GetString() : null;
                    }

                    // --- Token meter (task f2702f69): feed the shared meter from this terminal's
                    // transcript + subagent JSONLs, then snapshot + price for the banner. Reuses this
                    // 2s poll (already UI-marshaled) rather than a separate per-terminal watcher. ---
                    long? tmTotal = null, tmSub = null, tmCache = null;
                    decimal? tmCost = null;
                    double? tmBurn = null;
                    bool tmEstimate = true;
                    bool tmLowerBound = false;
                    var tokenMeter = _messageBroker?.TokenMeter;
                    // SECURITY: tokenSessionId is read from a temp file and used as a PATH SEGMENT in
                    // the subagent rollup below; reject anything that isn't a strict identifier so a
                    // hostile temp file can't traverse via '..' or separators (codex-security HIGH,
                    // mirrors the IsSafeStatusLineSegment gate the docId-scoped poll already uses).
                    if (tokenMeter != null
                        && !string.IsNullOrEmpty(tokenSessionId) && IsSafeStatusLineSegment(tokenSessionId)
                        && !string.IsNullOrEmpty(transcriptPath))
                    {
                        try
                        {
                            tokenMeter.ProcessTranscriptFile(tokenSessionId, transcriptPath, false);

                            // Subagent rollup: tail {claudeProjectFolder}/{sessionId}/subagents/agent-*.jsonl.
                            try
                            {
                                string claudeFolder = MultiTerminal.Services.SessionLineageService.GetClaudeProjectFolder(
                                    folder ?? _startingWorkingDirectory);
                                if (!string.IsNullOrEmpty(claudeFolder))
                                {
                                    string subDir = Path.Combine(claudeFolder, tokenSessionId, "subagents");
                                    if (Directory.Exists(subDir))
                                    {
                                        foreach (string af in Directory.GetFiles(subDir, "agent-*.jsonl"))
                                        {
                                            tokenMeter.ProcessTranscriptFile(tokenSessionId, af, true);
                                        }
                                    }
                                }
                            }
                            catch { /* subagent dir best-effort */ }

                            // If this terminal rolled to a DIFFERENT Claude session (a resume / new
                            // chat in the same tab), evict the previous session's state now rather than
                            // letting it linger until close. [codex-security MEDIUM — session rollover]
                            if (!string.IsNullOrEmpty(_lastTokenSessionId)
                                && !string.Equals(_lastTokenSessionId, tokenSessionId, StringComparison.Ordinal))
                            {
                                tokenMeter.Forget(_lastTokenSessionId);
                            }

                            // Remember for cleanup so the singleton meter can drop this session's
                            // state (accumulator + all file offsets) when the terminal closes.
                            _lastTokenSessionId = tokenSessionId;

                            var snap = tokenMeter.GetSnapshot(tokenSessionId);
                            if (snap != null)
                            {
                                tmTotal = snap.TotalTokens;
                                tmSub = snap.SubagentTokens;
                                tmCache = snap.CacheTokens;
                                tmBurn = snap.TokensPerMinute;
                                // Plan-aware cost: we have NO reliable signal that positively identifies a
                                // metered API key (rate-limit data is optional, absent on older Claude Code
                                // / some runs), and showing exact "$" for a subscription would be a false
                                // billing claim. Default to an ESTIMATE ("~$"); exact pricing is gated on an
                                // explicit plan setting (follow-up). [codex-adversary — don't guess exact]
                                var cost = MultiTerminal.Services.PricingTable.Estimate(
                                    snap.ByModel, MultiTerminal.Services.PricingPlan.Subscription);
                                tmCost = cost.TotalUsd;
                                tmEstimate = cost.IsEstimate;
                                tmLowerBound = cost.HasUnpricedTokens;
                            }
                        }
                        catch (Exception tmEx)
                        {
                            _debugLogService?.Trace("TerminalDocument", $"StatusLinePoll: token meter failed ({tmEx.Message})");
                        }
                    }

                    // Marshal to UI thread
                    string folderForUi = folder;
                    BeginInvoke(new Action(() =>
                    {
                        _statusBar?.UpdateStatusLine(model, folderForUi, contextPct, quota5h, quota7d, pace5h, pace7d, resetIn5h);
                        _statusBar?.UpdateTokenMeter(tmTotal, tmCost, tmEstimate, tmLowerBound, tmBurn, tmSub, tmCache);

                        // If Claude Code's real workspace drifts from what the HUD Dashboard
                        // is showing (e.g. because the launch dir came from the stale global
                        // _settings.GetLastDirectory() fallback), push the real folder to the
                        // HUD so its header reflects reality. Look up the matching project so
                        // the dashboard can show name/description too; pass null projectId if
                        // no registered project matches.
                        if (!string.IsNullOrEmpty(folderForUi) &&
                            !string.Equals(folderForUi.TrimEnd('\\', '/'),
                                           (_hudDispatchedFolder ?? "").TrimEnd('\\', '/'),
                                           StringComparison.OrdinalIgnoreCase))
                        {
                            string resolvedProjectId = null;
                            string resolvedProjectName = null;
                            try
                            {
                                var projects = _messageBroker?.ProjectService?.GetAllRegisteredProjects();
                                if (projects != null)
                                {
                                    var match = projects.FirstOrDefault(p =>
                                        !string.IsNullOrEmpty(p.Path) &&
                                        string.Equals(p.Path.TrimEnd('\\', '/'),
                                                      folderForUi.TrimEnd('\\', '/'),
                                                      StringComparison.OrdinalIgnoreCase));
                                    if (match != null)
                                    {
                                        resolvedProjectId = match.Id;
                                        resolvedProjectName = match.Name;
                                    }
                                }
                            }
                            catch { /* non-critical — fall back to deriving name from path */ }

                            if (string.IsNullOrEmpty(resolvedProjectName))
                            {
                                resolvedProjectName = System.IO.Path.GetFileName(folderForUi.TrimEnd('\\', '/'));
                            }

                            _debugLogService?.Trace("TerminalDocument", $"#PROJ# [TerminalDocument.StatusLinePoll] Instance={InstanceId} DocId='{_docId}' folderForUi='{folderForUi}' resolvedProjectId='{resolvedProjectId}' resolvedProjectName='{resolvedProjectName}' (was _projectName='{_projectName}')");
                            _projectName = resolvedProjectName;
                            // Also refresh the status bar Row 1 so its project-name
                            // title tracks Claude Code's real workspace instead of
                            // the stale launch-dir lookup from StartTerminal.
                            _lastKnownDirectory = folderForUi;
                            _hudDashboard?.SetProject(resolvedProjectId, folderForUi, resolvedProjectName);
                            _hudKnowledge?.SetProject(resolvedProjectId);
                            // Pass resolvedProjectId — see comment in StartTerminal call site for rationale.
                            _hudGit?.SetProject(resolvedProjectId, folderForUi);
                            // Notes/Sessions are per-project: only UPGRADE their key to a
                            // canonical registered project path; never downgrade to a raw
                            // worktree folder (which has no notes and would blank the pane).
                            string canonNotesKey = TryResolveCanonicalProjectPath(resolvedProjectId, folderForUi);
                            if (!string.IsNullOrEmpty(canonNotesKey) &&
                                !string.Equals(canonNotesKey, _hudNotesProjectKey, StringComparison.OrdinalIgnoreCase))
                            {
                                _hudNotesProjectKey = canonNotesKey;
                                _hudNotes?.SetProject(canonNotesKey);
                                _hudSessions?.SetProject(canonNotesKey);
                            }
                            _hudDispatchedFolder = folderForUi;
                            UpdateStatusBar();
                        }
                    }));

                    // Advance the change-detection cache ONLY on a genuine content change — never as a
                    // side effect of an awaitingRender-only re-delivery. Caching unconfirmed content here
                    // (set on the timer thread right after the queue-only BeginInvoke) is what let a
                    // raced/stale ack leave change-detection satisfied while the rows were still hidden,
                    // suppressing recovery (pipeline Run-1 debugger HIGH-2).
                    if (perTerminalChanged) _lastStatusLineContent = content;
                }
                catch
                {
                    // Silently ignore parse/IO errors — file may be mid-write
                }
            }, null, 1000, 2000); // Start after 1s, poll every 2s
        }

        /// <summary>
        /// Starts the working-tree dirty poll. Ticks every 10s, refreshes the
        /// HUD header strip + Git tab so working-tree edits that don't touch
        /// <c>.git/</c> (and so don't fire <c>RepoStateChanged</c>) still
        /// surface. Idempotent; also hooks <see cref="Form.Activated"/> for
        /// an on-focus refresh and triggers an immediate refresh on start.
        /// </summary>
        public void StartWorkingTreeDirtyPolling()
        {
            if (_isDisposing) return;
            if (_workingTreeDirtyTimer != null) return;

            _workingTreeDirtyTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            _workingTreeDirtyTimer.Tick += (s, e) => RefreshWorkingTreeDirty();
            _workingTreeDirtyTimer.Start();

            // On-focus refresh — DockContent is a Form, so Activated fires when
            // this docked pane becomes the active document in the dock panel.
            this.Activated += OnDocActivatedForDirtyRefresh;

            // Kick an immediate refresh so the strip reflects current state
            // without waiting 10s for the first tick.
            RefreshWorkingTreeDirty();
        }

        /// <summary>
        /// Stops the working-tree dirty poll and unsubscribes the focus handler.
        /// </summary>
        public void StopWorkingTreeDirtyPolling()
        {
            if (_workingTreeDirtyTimer != null)
            {
                _workingTreeDirtyTimer.Stop();
                _workingTreeDirtyTimer.Dispose();
                _workingTreeDirtyTimer = null;
            }
            this.Activated -= OnDocActivatedForDirtyRefresh;
        }

        private void OnDocActivatedForDirtyRefresh(object sender, EventArgs e)
        {
            RefreshWorkingTreeDirty();
        }

        /// <summary>
        /// Computes the working-tree dirty count + current branch on a
        /// background thread and pushes the result to the HUD strip. Also
        /// kicks the Git tab to refresh so its per-worktree dirty counts
        /// stay live. Guarded by a single-flight flag so slow git operations
        /// don't pile up on the thread pool.
        /// </summary>
        private void RefreshWorkingTreeDirty()
        {
            if (_isDisposing) return;
            if (_workingTreeRefreshInFlight) return;

            string folder = _hudDispatchedFolder;
            var broker = _messageBroker;
            if (string.IsNullOrEmpty(folder) || broker == null) return;

            _workingTreeRefreshInFlight = true;
            System.Threading.Tasks.Task.Run(async () =>
            {
                int count = 0;
                string branch = "";
                string aggregateText = null;
                try
                {
                    var svc = broker.GitRepos?.GetOrCreate(folder);
                    if (svc != null)
                    {
                        count = svc.GetWorkingTreeSummaryCount();
                        branch = svc.CurrentBranch ?? "";
                    }

                    // Aggregate roll-up across ALL worktrees of this repo so the
                    // top dirty strip mirrors the Git tab's panel header
                    // ("5 uncommitted · 1 master · 4 worktrees (2)") instead of
                    // showing only the count for this terminal's bound worktree.
                    // Falls back to the single-worktree path if the list service
                    // isn't wired or returns no entries.
                    //
                    // **MUST mirror the format in Controls/HudGitPanel/hud-git.html
                    // renderUnifiedTree (~line 1610) — that helper builds the same
                    // string from the git_state_tree payload for the panel header.
                    // If you change the parts order/separators here, change them
                    // there too (cycle-5 dedup note).
                    var wtList = broker.WorktreeList;
                    if (wtList != null)
                    {
                        var entries = await wtList.GetWorktreesForRepoAsync(folder).ConfigureAwait(false);
                        if (entries != null && entries.Count > 0)
                        {
                            int mainDirty = 0;
                            int nonMainDirty = 0;
                            int nonMainDirtyTrees = 0;
                            string mainBranchLabel = "master";
                            foreach (var e in entries)
                            {
                                if (e == null) continue;
                                if (e.IsMain)
                                {
                                    mainDirty = e.DirtyCount;
                                    if (!string.IsNullOrEmpty(e.Branch)) mainBranchLabel = e.Branch;
                                }
                                else if (e.DirtyCount > 0)
                                {
                                    nonMainDirty += e.DirtyCount;
                                    nonMainDirtyTrees++;
                                }
                            }
                            int totalDirty = mainDirty + nonMainDirty;
                            count = totalDirty;
                            if (totalDirty > 0)
                            {
                                var parts = new System.Collections.Generic.List<string> { totalDirty + " uncommitted" };
                                if (mainDirty > 0) parts.Add(mainDirty + " " + mainBranchLabel);
                                if (nonMainDirtyTrees > 0)
                                {
                                    parts.Add(nonMainDirty + " worktree" + (nonMainDirtyTrees == 1 ? "" : "s")
                                        + " (" + nonMainDirtyTrees + ")");
                                }
                                aggregateText = string.Join(" · ", parts);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLogService?.Trace("TerminalDocument.RefreshWorkingTreeDirty", $"{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _workingTreeRefreshInFlight = false;
                }

                try
                {
                    _hudTabContainer?.SetWorkingTreeDirty(count, branch, aggregateText);
                    _hudGit?.RequestRefresh();
                }
                catch { }
            });
        }

        /// <summary>
        /// Stops the status line polling timer.
        /// </summary>
        public void StopStatusLinePolling()
        {
            _statusLineTimer?.Dispose();
            _statusLineTimer = null;

            // Drop this terminal's session from the shared token meter so the singleton doesn't
            // retain every session for the app's lifetime (task f2702f69 — bounded retention).
            try
            {
                _messageBroker?.TokenMeter?.Forget(_lastTokenSessionId);
            }
            catch { /* best-effort */ }

            // Delete this terminal's scoped statusline file so %TEMP% doesn't
            // accumulate orphans. Best-effort — missing/locked file is fine.
            try
            {
                string terminalName = CustomTitle;
                if (!string.IsNullOrEmpty(terminalName) && !string.IsNullOrEmpty(_docId))
                {
                    string filePath = Path.Combine(Path.GetTempPath(), $"mt-statusline-{terminalName}-{_docId}.json");
                    if (File.Exists(filePath)) File.Delete(filePath);
                }
            }
            catch { /* best-effort cleanup */ }
        }

        /// <summary>
        /// Handles the Open Folder button click from the status bar.
        /// Opens the folder in Windows Explorer.
        /// </summary>
        private void OnOpenFolderRequested(object sender, string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            try
            {
                if (Directory.Exists(folderPath))
                {
                    Process.Start("explorer.exe", folderPath);
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalDocument", $"Failed to open folder: {ex.Message}");
            }
        }

        // --- Drag-and-drop from kanban board ---

        private Color? _originalBackColor;

        private bool TryParseTaskDragData(IDataObject data, out string taskId, out string title)
        {
            // Primary: use side-channel from TasksPanelControl (WebView2 postMessage)
            // WebView2 doesn't reliably pass HTML5 dataTransfer to WinForms OLE drag-drop
            if (TasksPanelControl.TryGetPendingDragData(out taskId, out title))
                return true;

            // Fallback: try OLE data (in case it works on some WebView2 versions)
            taskId = null;
            title = null;
            if (!data.GetDataPresent(DataFormats.Text)) return false;

            var text = data.GetData(DataFormats.Text) as string;
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("source", out var src) && src.GetString() == "multiterminal-kanban")
                {
                    taskId = root.GetProperty("taskId").GetString();
                    title = root.TryGetProperty("title", out var t) ? t.GetString() : "";
                    return !string.IsNullOrEmpty(taskId);
                }
            }
            catch { /* not JSON or wrong shape — not a kanban drag */ }

            return false;
        }

        private void OnTaskDragEnter(object sender, DragEventArgs e)
        {
            if (TryParseTaskDragData(e.Data, out _, out _))
            {
                e.Effect = DragDropEffects.Move | DragDropEffects.Copy;
                ShowDropHighlight();
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnTaskDragOver(object sender, DragEventArgs e)
        {
            e.Effect = TryParseTaskDragData(e.Data, out _, out _)
                ? DragDropEffects.Move | DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void OnTaskDragLeave(object sender, EventArgs e)
        {
            HideDropHighlight();
        }

        private void OnTaskDragDrop(object sender, DragEventArgs e)
        {
            HideDropHighlight();

            if (TryParseTaskDragData(e.Data, out var taskId, out var title))
            {
                TasksPanelControl.ClearPendingDragData();
                TaskDropped?.Invoke(this, new TaskDroppedEventArgs(taskId, title));
            }
        }

        private void ShowDropHighlight()
        {
            if (_originalBackColor.HasValue) return; // already highlighted
            _originalBackColor = _dropZone.BackColor;
            _dropZone.BackColor = Color.FromArgb(30, 80, 160);
            _dropZoneLabel.ForeColor = Color.White;
            _dropZoneLabel.Text = "\u2193 Drop to assign task";
        }

        private void HideDropHighlight()
        {
            if (!_originalBackColor.HasValue) return;
            _dropZone.BackColor = _originalBackColor.Value;
            _dropZoneLabel.ForeColor = Color.FromArgb(140, 140, 160);
            _dropZoneLabel.Text = "\u2193 Drop task here";
            _originalBackColor = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Prevent stray splitter events from saving bogus ratios during teardown
                _isDisposing = true;
                _suppressSplitterEvents = true;

                _tabContextMenu?.Dispose();
                _tabContextMenu = null;

                // Unregister terminal from broker so the team leader name is released
                if (_messageBroker != null && !string.IsNullOrEmpty(_docId))
                {
                    _messageBroker.UnregisterTerminal(_docId);
                }

                StopStatusLinePolling();
                StopWorkingTreeDirtyPolling();

                _projectFileWatcher?.Dispose();
                _projectFileWatcher = null;

                if (_startScreen != null)
                {
                    _startScreen.Dispose();
                    _startScreen = null;
                }

                // Unsubscribe from activity updates
                if (_messageBroker?.ActivityService != null)
                {
                    _messageBroker.ActivityService.ActivityUpdated -= OnActivityUpdated;
                }

                // Unsubscribe from remoteMode change notifications
                if (_messageBroker != null)
                {
                    _messageBroker.RemoteModeChanged -= OnRemoteModeChanged;
                    _messageBroker.TaskActiveChanged -= OnBrokerTaskActiveChanged;
                }

                // Unsubscribe splitter events BEFORE disposing child controls
                // to prevent WinForms layout changes from firing save events
                if (_terminalHudSplitter != null)
                {
                    _terminalHudSplitter.SizeChanged -= OnHudSplitterSizeChanged;
                    _terminalHudSplitter.SplitterMoved -= OnHudSplitterMoved;
                }

                if (_hudTabContainer != null)
                {
                    _hudTabContainer.Dispose();
                    _hudTabContainer = null;
                }

                if (_dropZone != null)
                {
                    _dropZone.Dispose();
                    _dropZone = null;
                    _dropZoneLabel = null;
                }

                if (_statusBarSplitter != null)
                {
                    _statusBarSplitter.SplitterMoved -= OnStatusBarSplitterMoved;
                    _statusBarSplitter.Dispose();
                    _statusBarSplitter = null;
                }

                if (_statusBar != null)
                {
                    _statusBar.Dispose();
                    _statusBar = null;
                }

                if (_agentPanelSanityTimer != null)
                {
                    _agentPanelSanityTimer.Stop();
                    _agentPanelSanityTimer.Dispose();
                    _agentPanelSanityTimer = null;
                }

                if (_embeddedAgentPanel != null)
                {
                    _embeddedAgentPanel.Dispose();
                    _embeddedAgentPanel = null;
                }

                if (_terminalAgentSplitter != null)
                {
                    _terminalAgentSplitter.SizeChanged -= OnSplitterSizeChanged;
                    _terminalAgentSplitter.SplitterMoved -= OnAgentSplitterMoved;
                    _terminalAgentSplitter.Dispose();
                    _terminalAgentSplitter = null;
                }

                if (_terminal != null)
                {
                    _terminal.TitleChanged -= OnTerminalTitleChanged;
                    _terminal.ProcessExited -= OnTerminalProcessExited;
                    _terminal.ContextMenuRequested -= OnTerminalContextMenuRequested;
                    _terminal.Stop();
                    _terminal.Dispose();
                    _terminal = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Gets the persist string for layout serialization.
        /// </summary>
        protected override string GetPersistString()
        {
            return typeof(TerminalDocument).FullName;
        }
    }

    /// <summary>
    /// Event args for save prompt request from terminal context menu.
    /// </summary>
    public class SavePromptRequestEventArgs : EventArgs
    {
        public string SelectedText { get; }
        public SavePromptRequestEventArgs(string selectedText)
        {
            SelectedText = selectedText;
        }
    }

    /// <summary>
    /// Event args for launching a terminal with a specific identity.
    /// </summary>
    public class LaunchAsIdentityEventArgs : EventArgs
    {
        public string IdentityName { get; }
        public LaunchAsIdentityEventArgs(string identityName)
        {
            IdentityName = identityName;
        }
    }

    /// <summary>
    /// Event args for directory change in terminal.
    /// </summary>
    public class DirectoryChangedEventArgs : EventArgs
    {
        public string Directory { get; }
        public DirectoryChangedEventArgs(string directory)
        {
            Directory = directory;
        }
    }

    /// <summary>
    /// Event args for a kanban task dropped onto a terminal.
    /// </summary>
    public class TaskDroppedEventArgs : EventArgs
    {
        public string TaskId { get; }
        public string Title { get; }
        public TaskDroppedEventArgs(string taskId, string title)
        {
            TaskId = taskId;
            Title = title;
        }
    }
}
