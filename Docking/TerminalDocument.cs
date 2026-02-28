using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MultiTerminal.Controls;
using MultiTerminal.Dialogs;
using MultiTerminal.Terminal;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using MultiTerminal.StartScreen;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// DockContent wrapper for a terminal control.
    /// Represents a single terminal instance in the docking framework.
    /// </summary>
    public class TerminalDocument : DockContent
    {
        private TerminalControl _terminal;
        private TerminalStatusBarRenderer _statusBar;
        private TaskHudRenderer _taskHud;
        private SplitContainer _terminalAgentSplitter;
        private SplitContainer _terminalHudSplitter;
        private EmbeddedAgentPanel _embeddedAgentPanel;
        private MessageBroker _messageBroker;
        private DebugLogService _debugLogService;
        private static int _instanceCount = 0;
        private readonly string _docId = Guid.NewGuid().ToString("N").Substring(0, 8);

        // Start screen — shown before a terminal shell is started
        private StartScreenControl _startScreen;
        private bool _isStartScreenVisible;

        /// <summary>
        /// Gets the unique document ID for this terminal.
        /// Used for MCP push notification mapping.
        /// </summary>
        public string DocId => _docId;

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
        /// Function to retrieve available identity names for the "Launch as..." menu.
        /// Set by MainForm to provide identity list.
        /// </summary>
        public Func<string[]> GetAvailableIdentities { get; set; }

        // Per-terminal zoom levels for WebView2 panels
        private double _agentPanelZoom = 1.0;
        private double _taskHudZoom = 1.0;

        // Track the last known working directory for session persistence
        private string _lastKnownDirectory;

        // FileSystemWatcher to detect project.json creation/changes
        private FileSystemWatcher _projectFileWatcher;
        private string _lastWatchedDirectory;

        // Custom user-defined title (overrides auto title from working directory)
        private string _customTitle;

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
        private ContextMenuStrip _currentContextMenu;

        /// <summary>
        /// Gets or sets a custom user-defined title for the tab.
        /// When set, this overrides the automatic title from the working directory.
        /// </summary>
        public string CustomTitle
        {
            get => _customTitle;
            set
            {
                _customTitle = value;
                UpdateTabTitle();
            }
        }

        private void UpdateTabTitle()
        {
            if (!string.IsNullOrEmpty(_customTitle))
            {
                Text = _customTitle;
                TabText = _customTitle;
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
            if (_taskHud == null || string.IsNullOrEmpty(_customTitle)) return;

            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.UpdateTaskHudTerminalName] customTitle='{_customTitle}' IsBrokerInitialized={_taskHud.IsBrokerInitialized} brokerSet={_messageBroker != null}");

            if (!_taskHud.IsBrokerInitialized && _messageBroker != null)
            {
                // Broker is set on TerminalDocument but HUD has not been initialized yet.
                // This is the common late-name scenario: SetMessageBroker() was called first
                // (CustomTitle was null then), and now CustomTitle has arrived.
                _taskHud.Initialize(_messageBroker, _customTitle);
            }
            else
            {
                // Either HUD is already initialized (update the name), or broker not yet set
                // (SetTerminalName will queue the name for when Initialize is called later).
                _taskHud.SetTerminalName(_customTitle);
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
            _taskHud = new TaskHudRenderer
            {
                Visible = false,
                Dock = DockStyle.Fill
            };

            // Persist task HUD zoom changes back to this terminal's stored zoom level
            _taskHud.ZoomChanged += (s, zoom) => { _taskHudZoom = zoom; };

            // Create SplitContainer: terminal (top, 75%) | task HUD (bottom, 25%)
            // Task HUD only spans terminal width, not the agent panel
            _terminalHudSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal, // top/bottom split
                FixedPanel = FixedPanel.Panel2, // task HUD keeps fixed size on resize
                BorderStyle = BorderStyle.None,
                SplitterWidth = 3,
                BackColor = Color.FromArgb(40, 40, 50),
                Panel1MinSize = 100,
                Panel2MinSize = 80
            };

            _terminalHudSplitter.Panel1.Controls.Add(_terminal);
            _terminalHudSplitter.Panel2.Controls.Add(_taskHud);

            // Hide Panel2 (task HUD) until a task is active
            _terminalHudSplitter.Panel2Collapsed = true;

            // Handle HUD show/hide requests by controlling Panel2Collapsed directly.
            // This avoids the WinForms deadlock where VisibleChanged won't fire on a
            // control inside a collapsed panel (parent invisible → child VisibleChanged
            // never fires → panel never uncollapses).
            _taskHud.HudVisibilityRequested += (s, show) =>
            {
                _terminalHudSplitter.Panel2Collapsed = !show;

                // Re-apply the 75/25 split every time HUD becomes visible.
                // WinForms doesn't reliably restore SplitterDistance when uncollapsing Panel2.
                if (show && _terminalHudSplitter.Height > 0)
                {
                    try
                    {
                        int distance = (int)(_terminalHudSplitter.Height * 0.75);
                        if (distance > _terminalHudSplitter.Panel1MinSize)
                            _terminalHudSplitter.SplitterDistance = distance;
                    }
                    catch { /* bounds during layout */ }
                }
            };

            // Set the 75/25 split once the splitter has a valid height
            _terminalHudSplitter.SizeChanged += OnHudSplitterSizeChanged;

            // Create SplitContainer: terminal+hud (left, 75%) | agent panel (right, 25%)
            _terminalAgentSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical, // left/right split
                FixedPanel = FixedPanel.Panel2, // agent panel keeps fixed size on resize
                BorderStyle = BorderStyle.None,
                SplitterWidth = 3,
                BackColor = Color.FromArgb(40, 40, 50), // subtle splitter color
                Panel1MinSize = 100,
                Panel2MinSize = 80
            };

            _terminalAgentSplitter.Panel1.Controls.Add(_terminalHudSplitter);
            _terminalAgentSplitter.Panel2.Controls.Add(_embeddedAgentPanel);

            // Set the 75/25 split once the splitter has a valid width
            _terminalAgentSplitter.SizeChanged += OnSplitterSizeChanged;

            // Layout order: _terminalAgentSplitter (Fill) | _statusBar (Top)
            Controls.Add(_terminalAgentSplitter);
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
            Controls.Add(_startScreen);
            _startScreen.BringToFront();
            _isStartScreenVisible = true;

            // Configure dock behavior
            DockAreas = DockAreas.Document | DockAreas.Float;
            CloseButton = true;
            CloseButtonVisible = true;
            ShowHint = DockState.Document;

            // Initialize tab header context menu
            InitializeTabContextMenu();

            ResumeLayout(false);
            PerformLayout();
        }

        private bool _initialSplitApplied;
        private bool _initialHudSplitApplied;

        /// <summary>
        /// Sets the 75/25 splitter distance once the container has a valid width.
        /// </summary>
        private void OnSplitterSizeChanged(object sender, EventArgs e)
        {
            if (_initialSplitApplied) return;
            if (_terminalAgentSplitter.Width <= 0) return;

            try
            {
                int distance = (int)(_terminalAgentSplitter.Width * 0.75);
                if (distance > _terminalAgentSplitter.Panel1MinSize &&
                    distance < _terminalAgentSplitter.Width - _terminalAgentSplitter.Panel2MinSize)
                {
                    _terminalAgentSplitter.SplitterDistance = distance;
                    _initialSplitApplied = true;
                }
            }
            catch
            {
                // Ignore if splitter distance is out of bounds during layout
            }
        }

        /// <summary>
        /// Sets the 75/25 horizontal splitter distance for terminal vs task HUD.
        /// </summary>
        private void OnHudSplitterSizeChanged(object sender, EventArgs e)
        {
            if (_initialHudSplitApplied) return;
            if (_terminalHudSplitter.Height <= 0) return;

            try
            {
                int distance = (int)(_terminalHudSplitter.Height * 0.75);
                if (distance > _terminalHudSplitter.Panel1MinSize &&
                    distance < _terminalHudSplitter.Height - _terminalHudSplitter.Panel2MinSize)
                {
                    _terminalHudSplitter.SplitterDistance = distance;
                    _initialHudSplitApplied = true;
                }
            }
            catch
            {
                // Ignore if splitter distance is out of bounds during layout
            }
        }

        private void InitializeTabContextMenu()
        {
            _tabContextMenu = new ContextMenuStrip();

            // Home — show start screen
            var homeItem = new ToolStripMenuItem("Home");
            homeItem.Click += (s, args) => ShowStartScreen();
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
        public void StartTerminal(string workingDirectory = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null)
        {
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] ===== START =====");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] workingDirectory: '{workingDirectory ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] terminalName: '{terminalName ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] autoRunCommand: '{autoRunCommand ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] spawnerName: '{spawnerName ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] projectId: '{projectId ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] _docId: '{_docId}'");

            // Hide start screen before launching the shell
            HideStartScreen();

            _lastKnownDirectory = workingDirectory;
            ToolTipText = workingDirectory;
            _isTerminalStarted = true;

            // Set terminal name as custom title if provided
            if (!string.IsNullOrEmpty(terminalName))
            {
                System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] Setting CustomTitle to '{terminalName}'");
                CustomTitle = terminalName;
            }

            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] Calling _terminal.Start...");
            _terminal.Start(workingDirectory, _docId, terminalName, autoRunCommand, spawnerName, projectId);
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] _terminal.Start returned");

            // Update status bar after terminal starts
            UpdateStatusBar();
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument.StartTerminal] ===== COMPLETE =====");
        }

        /// <summary>
        /// Sets the terminal theme.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;
            _terminal.SetTheme(theme);
            _statusBar?.SetTheme(theme.IsDark);
            _taskHud?.ApplyTheme(theme.IsDark);
            _embeddedAgentPanel?.ApplyTheme(theme.IsDark);
            _startScreen?.ApplyTheme(theme.IsDark);

            // Theme the splitter handle
            if (_terminalAgentSplitter != null)
            {
                _terminalAgentSplitter.BackColor = theme.IsDark
                    ? Color.FromArgb(40, 40, 50)
                    : Color.FromArgb(210, 210, 215);
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
        /// Gets the stored zoom level for the agent panel associated with this terminal.
        /// </summary>
        public double GetAgentPanelZoom() => _agentPanelZoom;

        /// <summary>
        /// Stores the agent panel zoom level for this terminal.
        /// Agent panel controls are managed by MainForm; it applies the zoom when creating/attaching panels.
        /// </summary>
        public void SetAgentPanelZoom(double zoom)
        {
            _agentPanelZoom = zoom;
        }

        /// <summary>
        /// Gets the stored zoom level for the task HUD of this terminal.
        /// </summary>
        public double GetTaskHudZoom() => _taskHudZoom;

        /// <summary>
        /// Sets the zoom level for the task HUD and applies it immediately if the HUD exists.
        /// </summary>
        public void SetTaskHudZoom(double zoom)
        {
            _taskHudZoom = zoom;
            _taskHud?.SetZoomFactor(zoom);
        }

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

            // PowerShell title format: "Administrator: C:\Users\John" or just "C:\Users\John"
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
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument] UpdateProjectFileWatcher called with: {directory}");

            // Skip if already watching this directory
            if (_projectFileWatcher != null && _lastWatchedDirectory == directory)
            {
                System.Diagnostics.Trace.WriteLine($"[TerminalDocument] Already watching {directory}, skipping");
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
                System.Diagnostics.Trace.WriteLine($"[TerminalDocument] FileSystemWatcher created for: {_projectFileWatcher.Path}, Filter: {_projectFileWatcher.Filter}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[TerminalDocument] Failed to create FileSystemWatcher: {ex.Message}");
            }
        }

        private void OnProjectFileEvent(object sender, FileSystemEventArgs e)
        {
            System.Diagnostics.Trace.WriteLine($"[TerminalDocument] OnProjectFileEvent: Name={e.Name}, ChangeType={e.ChangeType}, FullPath={e.FullPath}");

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
                            System.Diagnostics.Trace.WriteLine($"[TerminalDocument] Failed to update FileSystemWatcher: {ex.Message}");
                        }
                    }
                    return;
                }

                // Marshal to UI thread and fire event
                System.Diagnostics.Trace.WriteLine($"[TerminalDocument] Firing ProjectFileChanged event");
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
        /// Injects a ProjectDatabase so the start screen can list projects.
        /// Call after construction, before the document is shown.
        /// </summary>
        public void SetProjectDatabase(ProjectDatabase projectDatabase)
        {
            _startScreen?.Initialize(projectDatabase);
        }

        /// <summary>
        /// Show the start screen and hide the terminal area.
        /// Called on new tab creation (before shell starts) and after shell exit.
        /// </summary>
        public void ShowStartScreen()
        {
            if (_startScreen == null) return;
            _isStartScreenVisible = true;

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
        /// </summary>
        public void HideStartScreen()
        {
            if (_startScreen == null) return;
            _isStartScreenVisible = false;
            _startScreen.Visible = false;
        }

        /// <summary>
        /// Whether the start screen is currently visible (shell not yet started / has exited).
        /// </summary>
        public bool IsStartScreenVisible => _isStartScreenVisible;

        /// <summary>
        /// Sets the debug log service for status bar logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
            _statusBar?.SetDebugLogService(debugLogService);
            _taskHud?.SetDebugLogService(debugLogService);
        }

        /// <summary>
        /// Sets the message broker for status bar and task HUD updates.
        /// </summary>
        public void SetMessageBroker(MessageBroker broker)
        {
            _messageBroker = broker;

            if (_messageBroker?.ActivityService != null)
            {
                // Subscribe to activity updates for this terminal
                _messageBroker.ActivityService.ActivityUpdated += OnActivityUpdated;
            }

            // Wire task HUD to broker.
            // If CustomTitle is already set, initialize fully.
            // If not, still call Initialize so HUD can pick up any _pendingTerminalName
            // set by an earlier SetTerminalName() call (e.g. OnLaunchAsIdentityRequested path).
            if (!string.IsNullOrEmpty(CustomTitle))
            {
                _taskHud?.Initialize(broker, CustomTitle);
            }
            else if (_taskHud?.HasPendingTerminalName == true)
            {
                // Name arrived before broker via SetTerminalName — initialize now
                _taskHud.Initialize(broker, null);
            }

            // Update status bar if terminal name is already set
            // Otherwise, StartTerminal() will call UpdateStatusBar() later
            if (!string.IsNullOrEmpty(CustomTitle))
            {
                UpdateStatusBar();
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

            _debugLogService?.Trace("TerminalDocument", $"UpdateStatusBar: Calling _statusBar.UpdateStatus with:");
            _debugLogService?.Trace("TerminalDocument", $"  - terminalName: '{terminalName}'");
            _debugLogService?.Trace("TerminalDocument", $"  - avatarUrl: '{avatarUrl}'");
            _debugLogService?.Trace("TerminalDocument", $"  - activityDescription: '{activityDescription}'");
            _debugLogService?.Trace("TerminalDocument", $"  - taskTitle: '{taskTitle}'");
            _debugLogService?.Trace("TerminalDocument", $"  - taskId: '{taskId}'");
            _debugLogService?.Trace("TerminalDocument", $"  - status: '{status}'");

            _statusBar.UpdateStatus(terminalName, avatarUrl, activityDescription, taskTitle, taskId, status);

            _debugLogService?.Trace("TerminalDocument", "_statusBar.UpdateStatus call completed");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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

                if (_taskHud != null)
                {
                    _taskHud.Dispose();
                    _taskHud = null;
                }

                if (_statusBar != null)
                {
                    _statusBar.Dispose();
                    _statusBar = null;
                }

                if (_embeddedAgentPanel != null)
                {
                    _embeddedAgentPanel.Dispose();
                    _embeddedAgentPanel = null;
                }

                if (_terminalAgentSplitter != null)
                {
                    _terminalAgentSplitter.SizeChanged -= OnSplitterSizeChanged;
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
}
