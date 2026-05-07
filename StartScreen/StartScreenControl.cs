using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal; // WebView2EnvironmentCache

namespace MultiTerminal.StartScreen
{
    /// <summary>
    /// Event args carrying the project ID the user wants to launch.
    /// TerminalKindOverride is populated when the split-button dropdown picks
    /// a specific terminal ("claude-code"|"codex"); null means use the project's
    /// stored DefaultTerminal.
    /// </summary>
    public class StartScreenLaunchEventArgs : EventArgs
    {
        public string ProjectId { get; }
        public string TerminalKindOverride { get; }

        public StartScreenLaunchEventArgs(string projectId)
            : this(projectId, null) { }

        public StartScreenLaunchEventArgs(string projectId, string terminalKindOverride)
        {
            ProjectId = projectId;
            TerminalKindOverride = terminalKindOverride;
        }
    }

    /// <summary>
    /// WebView2-based start screen control displayed in new terminal tabs before a shell is started.
    /// Follows the same pattern as TasksPanelControl: UserControl wrapping a WebView2 that loads
    /// an HTML file and exchanges JSON messages via postMessage.
    ///
    /// Raises events that TerminalDocument (and MainForm) wire up to trigger Claude Code launches.
    /// </summary>
    public class StartScreenControl : UserControl
    {
        // ── WebView2 state ────────────────────────────────────────────────────
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        // ── Data source ───────────────────────────────────────────────────────
        // Injected by TerminalDocument so we can call GetAllRichProjects().
        private ProjectDatabase _projectDatabase;
        private ProjectService _projectService;

        // ── Public events ─────────────────────────────────────────────────────

        /// <summary>
        /// Raised when the user clicks a project card's "Launch" action.
        /// TerminalDocument/MainForm should hide the start screen, start the terminal,
        /// and launch Claude Code in the project directory.
        /// </summary>
        public event EventHandler<StartScreenLaunchEventArgs> ProjectLaunched;

        /// <summary>
        /// Raised when the user clicks "Open PowerShell" (no project selected).
        /// </summary>
        public event EventHandler OpenPowerShellRequested;

        /// <summary>
        /// Raised when the user clicks "New Project".
        /// </summary>
        public event EventHandler NewProjectRequested;
        public event EventHandler JustClaudeRequested;

        // ── Constructor ───────────────────────────────────────────────────────

        public StartScreenControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;

            Controls.Add(_webView);

            // Wait for handle before initializing WebView2 (same pattern as TasksPanelControl)
            HandleCreated += OnHandleCreated;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialise the control with a project database instance.
        /// Call this once after the control is created.
        /// </summary>
        public async void Initialize(ProjectDatabase projectDatabase, ProjectService projectService = null)
        {
            _projectDatabase = projectDatabase;
            _projectService = projectService;
            _initializePending = true;

            if (IsHandleCreated && !_isInitializing && !_isInitialized)
            {
                await InitializeWebView2Async();
            }
            // else: OnHandleCreated will call InitializeWebView2Async when handle is ready
        }

        /// <summary>
        /// Re-queries the database and pushes a fresh project list to the WebView.
        /// Safe to call from any thread.
        /// </summary>
        public void RefreshProjects()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshProjects));
                return;
            }
            SendProjectsToWebView();
        }

        /// <summary>
        /// Apply a dark/light theme. Sends a string message to the HTML layer.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (!_isInitialized) return;
            PostWebMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Set the WebView2 zoom factor. Applies immediately if ready, otherwise deferred.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        // ── WebView2 lifecycle ────────────────────────────────────────────────

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (!_initializePending || _isInitializing || _isInitialized) return;
            await InitializeWebView2Async();
        }

        private async System.Threading.Tasks.Task InitializeWebView2Async()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] WebView2 init failed: {ex.Message}");
                _isInitializing = false;
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                // CoreWebView2 is null on init failure — log and reset so Initialize() can retry
                System.Diagnostics.Debug.WriteLine($"[StartScreen] WebView2 init failed: {e.InitializationException?.Message}");
                _isInitializing = false;
                return;
            }

            var htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] HTML not found at: {htmlPath}");
                _webView.CoreWebView2.NavigateToString(
                    "<html><body style='background:#1a1a2e;color:#ccc;font-family:sans-serif;padding:2rem'>" +
                    $"<h2>Start screen HTML not found</h2><p>Searched: {System.Net.WebUtility.HtmlEncode(htmlPath)}</p></body></html>");
            }

            _isInitialized = true;

            if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                _webView.ZoomFactor = _pendingZoom;
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "StartScreen", "start-screen.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "start-screen.html");
            if (File.Exists(path)) return path;

            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "StartScreen", "start-screen.html");
                if (File.Exists(path)) return path;
            }

            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StartScreen", "start-screen.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "StartScreen", "start-screen.html");
        }

        // ── Message handling ──────────────────────────────────────────────────

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                switch (typeElement.GetString())
                {
                    case "ready":
                        // JS has finished loading — push initial data
                        ApplyTheme(_isDarkTheme);
                        SendProjectsToWebView();
                        break;

                    case "launch_project":
                        if (root.TryGetProperty("projectId", out var launchIdEl))
                        {
                            var projectId = launchIdEl.GetString();
                            string clickedName = root.TryGetProperty("projectName", out var nEl) ? nEl.GetString() : "(none)";
                            // Optional per-click terminal override from the split-button dropdown.
                            // Null/empty means use the project's stored DefaultTerminal.
                            string terminalOverride = root.TryGetProperty("terminal", out var tEl) ? tEl.GetString() : null;
                            System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.OnWebMessageReceived] launch_project received: id='{projectId}' clickedName='{clickedName}' terminalOverride='{terminalOverride ?? "(none)"}' validLen={(projectId?.Length ?? -1)}");
                            // Cross-check with the cached list we sent to JS
                            try
                            {
                                var all = _projectDatabase?.GetAllRichProjects();
                                var match = all?.FirstOrDefault(p => p.Id == projectId);
                                System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.OnWebMessageReceived] DB lookup from same db: id='{match?.Id ?? "NULL"}' name='{match?.Name ?? "NULL"}' path='{match?.SourcePath ?? match?.Path ?? "NULL"}'");
                            }
                            catch (Exception dbgEx) { System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.OnWebMessageReceived] DB lookup threw: {dbgEx.Message}"); }
                            // Validate: non-empty, GUID-length (36 chars max)
                            if (!string.IsNullOrEmpty(projectId) && projectId.Length <= 36)
                            {
                                System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.OnWebMessageReceived] Firing ProjectLaunched event with id='{projectId}' terminalOverride='{terminalOverride ?? "(none)"}'");
                                ProjectLaunched?.Invoke(this, new StartScreenLaunchEventArgs(projectId, terminalOverride));
                            }
                        }
                        break;

                    case "toggle_pin":
                        if (root.TryGetProperty("projectId", out var pinIdEl))
                        {
                            var pinId = pinIdEl.GetString();
                            // Validate: non-empty, GUID-length (36 chars max)
                            if (!string.IsNullOrEmpty(pinId) && pinId.Length <= 36)
                                ToggleProjectPin(pinId);
                        }
                        break;

                    case "just_claude":
                        JustClaudeRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "open_powershell":
                        OpenPowerShellRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "new_project":
                        NewProjectRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "browse_all":
                        // Re-send full list (same as ready — JS uses this to remove any search filter)
                        SendProjectsToWebView();
                        break;

                    case "search":
                        // JS sends search client-side; we just respond with the full list
                        // so JS can apply its own filter. Alternatively re-filter server-side here.
                        SendProjectsToWebView();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] Message handling error: {ex.Message}");
            }
        }

        // ── Data helpers ──────────────────────────────────────────────────────

        private void SendProjectsToWebView()
        {
            if (!_isInitialized || _projectDatabase == null)
                return;

            try
            {
                var projects = _projectDatabase.GetAllRichProjects();
                System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.SendProjectsToWebView] Sending {projects?.Count ?? 0} projects to JS");
                if (projects != null)
                {
                    int dbgIdx = 0;
                    foreach (var dp in projects.Take(20))
                    {
                        System.Diagnostics.Trace.WriteLine($"#PROJ# [StartScreenControl.SendProjectsToWebView]   [{dbgIdx++}] id='{dp.Id}' name='{dp.Name}' path='{dp.SourcePath ?? dp.Path}'");
                    }
                }
                var projectDtos = new List<object>();

                foreach (var p in projects)
                {
                    projectDtos.Add(new
                    {
                        id = p.Id,
                        name = p.Name,
                        description = p.Description,
                        path = p.SourcePath ?? p.Path,
                        isPinned = p.IsPinned,
                        icon = p.Icon,
                        iconColor = p.IconColor,
                        projectType = p.ProjectType,
                        lastOpenedAt = p.LastOpenedAt == default
                            ? null
                            : p.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        currentVersion = p.CurrentVersion,
                        gitDefaultBranch = p.GitDefaultBranch,
                        gitRepoUrl = p.GitRepoUrl,
                        defaultTerminal = TerminalKindHelper.Normalize(p.DefaultTerminal)
                    });
                }

                var payload = JsonSerializer.Serialize(new
                {
                    type = "projects",
                    projects = projectDtos
                }, JsonOptions.Unicode);

                PostMessage(payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] SendProjectsToWebView error: {ex.Message}");
            }
        }

        private void ToggleProjectPin(string projectId)
        {
            if (string.IsNullOrEmpty(projectId) || _projectDatabase == null)
                return;

            try
            {
                // Delegate to ProjectService which writes to both SQLite AND project.json.
                // Without the file write, other code paths that load from project.json
                // (ChangelogService, VersioningService) would overwrite is_pinned back to false.
                if (_projectService != null)
                {
                    _projectService.ToggleProjectPinned(projectId);
                }
                else
                {
                    var project = _projectDatabase.GetRichProject(projectId);
                    if (project == null) return;
                    project.IsPinned = !project.IsPinned;
                    _projectDatabase.SaveRichProject(project);
                }

                // Push refreshed list back to JS
                SendProjectsToWebView();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartScreen] ToggleProjectPin error: {ex.Message}");
            }
        }

        // ── Low-level message posting ─────────────────────────────────────────

        /// <summary>Posts a JSON object message to the WebView.</summary>
        private void PostMessage(string jsonMessage)
        {
            _webView?.CoreWebView2?.PostWebMessageAsJson(jsonMessage);
        }

        /// <summary>Posts a plain string message to the WebView (e.g. "theme:dark").</summary>
        private void PostWebMessage(string message)
        {
            _webView?.CoreWebView2?.PostWebMessageAsString(message);
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
