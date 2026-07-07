using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// WebView2-based dashboard header that replaces the traditional ToolStrip.
    /// Shows: logo menu, new terminal, panel toggles, project info, active task, session chips.
    /// </summary>
    public class DashboardHeaderControl : UserControl
    {
        private WebView2 _webView;
        private MessageBroker _broker;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isDarkTheme = true;
        private bool _dashboardReadyFired;
        private System.Windows.Forms.Timer _fallbackTimer;
        private double _pendingZoom = 1.0;
        private readonly Queue<string> _pendingMessages = new();
        private ContextMenuStrip _logoMenu;

        // Events for MainForm to handle actions
        public event Action NewTerminalRequested;
        public event Action ToggleThemeRequested;
        public event Action SettingsRequested;
        public event Action DocsRequested;
        public event Action AboutRequested;
        public event Action<string> TogglePanelRequested;
        public event Action<string> GridLayoutRequested;
        public event Action<string> SwitchTerminalRequested;
        public event Action ExitRequested;
        public event Action ShowChatHistoryRequested;
        public event Action DashboardReady;

        public DashboardHeaderControl()
        {
            Height = 80;
            Dock = DockStyle.Top;
            // Start visible immediately with matching background — WebView2 content loads on top.
            // Previously Visible=false caused the header to never appear if WebView2 init failed.
            BackColor = Color.FromArgb(30, 30, 37);

            _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.DefaultBackgroundColor = Color.FromArgb(30, 30, 37);

            Controls.Add(_webView);

            BuildLogoMenu();

            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (!_initializePending || _isInitializing || _isInitialized) return;
            await InitializeWebView2Async();
        }

        public async void Initialize(MessageBroker broker)
        {
            _broker = broker;

            // Subscribe to broker events for live updates
            _broker.TerminalRegistered += OnTerminalRegistered;
            _broker.TerminalDisconnected += OnTerminalDisconnected;
            _broker.TasksUpdated += OnTasksUpdated;
            _broker.TaskClaimed += OnTaskClaimed;
            _broker.InboxUpdated += OnInboxUpdated;

            _initializePending = true;

            if (IsHandleCreated && !_isInitializing && !_isInitialized)
            {
                await InitializeWebView2Async();
            }
        }

        /// <summary>
        /// Fire DashboardReady if not already fired (fallback for WebView2 init failure).
        /// </summary>
        private void FireDashboardReadyIfNeeded()
        {
            if (_dashboardReadyFired) return;
            _dashboardReadyFired = true;

            // Dispose fallback timer if still running (happy path — dashboard ready before timeout)
            if (_fallbackTimer != null)
            {
                _fallbackTimer.Stop();
                _fallbackTimer.Dispose();
                _fallbackTimer = null;
            }

            DashboardReady?.Invoke();
        }

        private async Task InitializeWebView2Async()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                // Start a fallback timer — if WebView2 never fires "ready", unblock startup after 5 seconds
                _fallbackTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                _fallbackTimer.Tick += (s, e) =>
                {
                    _fallbackTimer.Stop();
                    _fallbackTimer.Dispose();
                    _fallbackTimer = null;
                    FireDashboardReadyIfNeeded();
                };
                _fallbackTimer.Start();

                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("DashboardHeader", $"WebView2 init failed: {ex.Message}");
                _isInitializing = false;
                FireDashboardReadyIfNeeded();
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                _broker?.DebugLogService?.Error("DashboardHeader", $"WebView2 init error: {e.InitializationException?.Message}");
                return;
            }

            // Disable scrollbars, context menu, and devtools
            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;

            var htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                _webView.CoreWebView2.NavigateToString("<html><body style='background:#1e1e2e;color:white;font-family:sans-serif;padding:20px'>Dashboard HTML not found</body></html>");
            }

            // NOTE: Do NOT set _isInitialized here. The page hasn't loaded yet.
            // _isInitialized is set in OnDashboardReady() when JS sends the "ready" signal.

            if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                _webView.ZoomFactor = _pendingZoom;
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Check in DashboardHeader subfolder
            string path = Path.Combine(assemblyDir, "DashboardHeader", "dashboard.html");
            if (File.Exists(path)) return path;

            // Check in same folder as assembly
            path = Path.Combine(assemblyDir, "dashboard.html");
            if (File.Exists(path)) return path;

            // Check parent directory (development layout)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "DashboardHeader", "dashboard.html");
                if (File.Exists(path)) return path;
            }

            // AppDomain base directory as last resort
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DashboardHeader", "dashboard.html");
            return path;
        }

        // ============ JS → C# Message Handling ============

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                var messageType = typeEl.GetString();

                switch (messageType)
                {
                    case "ready":
                        OnDashboardReady();
                        break;

                    case "action":
                        if (root.TryGetProperty("action", out var actionEl))
                            HandleAction(actionEl.GetString());
                        break;

                    case "switch_terminal":
                        if (root.TryGetProperty("name", out var nameEl))
                            SwitchTerminalRequested?.Invoke(nameEl.GetString());
                        break;
                }
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("DashboardHeader", $"Message error: {ex.Message}");
            }
        }

        private void HandleAction(string action)
        {
            switch (action)
            {
                case "show_menu": ShowLogoMenu(); break;
                case "new_terminal": NewTerminalRequested?.Invoke(); break;
                case "toggle_theme": ToggleThemeRequested?.Invoke(); break;
                case "settings": SettingsRequested?.Invoke(); break;
                case "docs": DocsRequested?.Invoke(); break;
                case "about": AboutRequested?.Invoke(); break;
                case "exit": ExitRequested?.Invoke(); break;
                case "toggle_history": ShowChatHistoryRequested?.Invoke(); break;

                // Panel toggles
                case "toggle_tasks": TogglePanelRequested?.Invoke("tasks"); break;
                case "toggle_chat": TogglePanelRequested?.Invoke("chat"); break;
                case "toggle_activity": TogglePanelRequested?.Invoke("activity"); break;
                case "toggle_office": TogglePanelRequested?.Invoke("office"); break;
                case "toggle_profiles": TogglePanelRequested?.Invoke("profiles"); break;
                case "toggle_inbox": TogglePanelRequested?.Invoke("inbox"); break;
                case "toggle_debug": TogglePanelRequested?.Invoke("debug"); break;
                case "toggle_preview": TogglePanelRequested?.Invoke("preview"); break;
                case "toggle_projects": TogglePanelRequested?.Invoke("projects"); break;

                // Grid layouts
                case "grid_2x2": GridLayoutRequested?.Invoke("2x2"); break;
                case "grid_2x3": GridLayoutRequested?.Invoke("2x3"); break;
                case "grid_3x2": GridLayoutRequested?.Invoke("3x2"); break;
                case "grid_h2": GridLayoutRequested?.Invoke("h2"); break;
                case "grid_v2": GridLayoutRequested?.Invoke("v2"); break;
                case "grid_h3": GridLayoutRequested?.Invoke("h3"); break;
                case "grid_v3": GridLayoutRequested?.Invoke("v3"); break;
                case "grid_reset": GridLayoutRequested?.Invoke("reset"); break;
            }
        }

        private void BuildLogoMenu()
        {
            _logoMenu = new ContextMenuStrip();
            _logoMenu.RenderMode = ToolStripRenderMode.Professional;

            _logoMenu.Items.Add("Toggle Theme", null, (s, e) => ToggleThemeRequested?.Invoke());
            _logoMenu.Items.Add("Documentation", null, (s, e) => DocsRequested?.Invoke());
            _logoMenu.Items.Add("Settings", null, (s, e) => SettingsRequested?.Invoke());
            _logoMenu.Items.Add(new ToolStripSeparator());

            // Grid Layout submenu
            var gridMenu = new ToolStripMenuItem("Grid Layout");
            gridMenu.DropDownItems.Add("2x2 Grid", null, (s, e) => GridLayoutRequested?.Invoke("2x2"));
            gridMenu.DropDownItems.Add("2x3 Grid", null, (s, e) => GridLayoutRequested?.Invoke("2x3"));
            gridMenu.DropDownItems.Add("3x2 Grid", null, (s, e) => GridLayoutRequested?.Invoke("3x2"));
            gridMenu.DropDownItems.Add(new ToolStripSeparator());
            gridMenu.DropDownItems.Add("2 Horizontal", null, (s, e) => GridLayoutRequested?.Invoke("h2"));
            gridMenu.DropDownItems.Add("2 Vertical", null, (s, e) => GridLayoutRequested?.Invoke("v2"));
            gridMenu.DropDownItems.Add("3 Horizontal", null, (s, e) => GridLayoutRequested?.Invoke("h3"));
            gridMenu.DropDownItems.Add("3 Vertical", null, (s, e) => GridLayoutRequested?.Invoke("v3"));
            gridMenu.DropDownItems.Add(new ToolStripSeparator());
            gridMenu.DropDownItems.Add("Reset to Tabs", null, (s, e) => GridLayoutRequested?.Invoke("reset"));
            _logoMenu.Items.Add(gridMenu);

            _logoMenu.Items.Add(new ToolStripSeparator());
            _logoMenu.Items.Add("About", null, (s, e) => AboutRequested?.Invoke());
            _logoMenu.Items.Add(new ToolStripSeparator());
            _logoMenu.Items.Add("Exit", null, (s, e) => ExitRequested?.Invoke());
        }

        private void ShowLogoMenu()
        {
            if (_logoMenu.Visible)
            {
                _logoMenu.Close();
                return;
            }

            // Close the menu when the parent form is clicked anywhere (WebView2 HWNDs
            // swallow mouse events so ContextMenuStrip's auto-dismiss doesn't work)
            var parentForm = FindForm();
            if (parentForm != null)
            {
                // Use a message filter to catch any mouse click in the application
                var filter = new LogoMenuClickFilter(_logoMenu);
                Application.AddMessageFilter(filter);
                _logoMenu.Closed += (s, e) => Application.RemoveMessageFilter(filter);
            }

            // Show below the logo button (left edge, below the 40px button + padding)
            _logoMenu.Show(this, new Point(14, Height - 4));
        }

        /// <summary>
        /// Message filter that closes the logo menu when the user clicks anywhere
        /// outside the menu. Needed because WebView2 HWNDs don't forward mouse events
        /// to WinForms, so ContextMenuStrip auto-dismiss fails within the app.
        /// </summary>
        private class LogoMenuClickFilter : IMessageFilter
        {
            private readonly ContextMenuStrip _menu;
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_RBUTTONDOWN = 0x0204;
            private const int WM_NCLBUTTONDOWN = 0x00A1;

            public LogoMenuClickFilter(ContextMenuStrip menu) => _menu = menu;

            public bool PreFilterMessage(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_NCLBUTTONDOWN)
                {
                    if (_menu.Visible && !_menu.Bounds.Contains(Cursor.Position))
                    {
                        _menu.Close();
                    }
                }
                return false; // never eat the message
            }
        }

        private void OnDashboardReady()
        {
            try
            {
                // JS page has loaded and listeners are active — now safe to send messages
                _isInitialized = true;
                _isInitializing = false;

                // Show the WebView2 now that content is loaded — kept hidden during init
                // to prevent the native browser window from flashing while the form has Opacity=0
                _webView.Visible = true;

                // Apply theme
                PostWebMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");

                // Flush pending messages
                while (_pendingMessages.Count > 0)
                {
                    var msg = _pendingMessages.Dequeue();
                    _webView.CoreWebView2.PostWebMessageAsJson(msg);
                }

                // Send initial data
                RefreshSessions();
                RefreshActiveTask();
                RefreshInbox();

                // Signal that the dashboard is ready for display
                FireDashboardReadyIfNeeded();
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("DashboardHeader", $"OnDashboardReady error: {ex.Message}");
            }
        }

        // ============ C# → JS Communication ============

        private void PostWebMessage(string message)
        {
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
        }

        private void PostJsonMessage(string jsonMessage)
        {
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
            }
            else
            {
                _pendingMessages.Enqueue(jsonMessage);
            }
        }

        // ============ Public Update Methods ============

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 37) : Color.FromArgb(239, 241, 245);
            PostWebMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        public void UpdateProjectInfo(string projectName, string branch)
        {
            var name = EscapeJson(projectName ?? "—");
            var br = EscapeJson(branch ?? "—");
            PostJsonMessage($"{{\"type\":\"project\",\"name\":\"{name}\",\"branch\":\"{br}\"}}");
        }

        public void UpdateActiveTask(string title, int done, int total)
        {
            var t = EscapeJson(title ?? "");
            PostJsonMessage($"{{\"type\":\"task\",\"title\":\"{t}\",\"done\":{done},\"total\":{total}}}");
        }

        public void UpdateSessions(IEnumerable<SessionInfo> sessions)
        {
            var items = sessions.Select(s => $"{{\"name\":\"{EscapeJson(s.Name)}\",\"status\":\"{EscapeJson(s.Status)}\",\"active\":{(s.IsActive ? "true" : "false")}}}");
            var json = $"{{\"type\":\"sessions\",\"sessions\":[{string.Join(",", items)}]}}";
            PostJsonMessage(json);
        }

        public void UpdateInboxCount(int count)
        {
            PostJsonMessage($"{{\"type\":\"inbox_count\",\"count\":{count}}}");
        }

        public void UpdatePanelState(string panel, bool visible)
        {
            PostJsonMessage($"{{\"type\":\"panel_state\",\"panel\":\"{EscapeJson(panel)}\",\"visible\":{(visible ? "true" : "false")}}}");
        }

        public void SetActiveSession(string terminalName)
        {
            PostJsonMessage($"{{\"type\":\"active_session\",\"name\":\"{EscapeJson(terminalName ?? "")}\"}}");
        }

        public void UpdateVersion(string version)
        {
            PostJsonMessage($"{{\"type\":\"version\",\"version\":\"{EscapeJson(version)}\"}}");
        }

        // ============ Broker Event Handlers ============

        private void OnTerminalRegistered(object sender, TerminalInfo e) => SafeInvoke(RefreshSessions);
        private void OnTerminalDisconnected(object sender, TerminalInfo e) => SafeInvoke(RefreshSessions);
        private void OnTasksUpdated(object sender, List<KanbanTask> e) => SafeInvoke(() => RefreshActiveTaskFromList(e));
        private void OnTaskClaimed(object sender, TaskClaimedEventArgs e) => SafeInvoke(RefreshActiveTask);
        private void OnInboxUpdated(object sender, InboxUpdatedEventArgs e) => SafeInvoke(() => UpdateInboxCount(e.UnreadCount));

        private void RefreshSessions()
        {
            if (_broker == null) return;
            var terminals = _broker.GetTerminals();
            var sessions = terminals.Select(t => new SessionInfo
            {
                Name = t.Name ?? "Unknown",
                Status = t.IsConnected ? "running" : "idle",
                IsActive = false // Will be set by MainForm based on active tab
            });
            UpdateSessions(sessions);
        }

        private void RefreshActiveTask()
        {
            if (_broker == null) return;
            var tasks = _broker.GetTasks();
            RefreshActiveTaskFromList(tasks);
        }

        private void RefreshActiveTaskFromList(List<KanbanTask> tasks)
        {
            if (tasks == null) return;

            // Find the active in-progress task
            var activeTask = tasks.FirstOrDefault(t =>
                t.Status == "in_progress" && t.SubStatus == "active");

            if (activeTask == null)
            {
                UpdateActiveTask(null, 0, 0);
                return;
            }

            var checklist = activeTask.GetChecklist();
            int done = checklist.Count(c => c.Status == "done");
            int total = checklist.Count;
            UpdateActiveTask(activeTask.Title, done, total);
        }

        private void RefreshInbox()
        {
            if (_broker == null) return;
            var result = _broker.GetInbox("Owner");
            if (result.Success)
                UpdateInboxCount(result.UnreadCount);
        }

        // ============ Helpers ============

        private void SafeInvoke(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch { /* disposed race */ }
            }
            else
            {
                action();
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.TerminalRegistered -= OnTerminalRegistered;
                    _broker.TerminalDisconnected -= OnTerminalDisconnected;
                    _broker.TasksUpdated -= OnTasksUpdated;
                    _broker.TaskClaimed -= OnTaskClaimed;
                    _broker.InboxUpdated -= OnInboxUpdated;
                }
                _fallbackTimer?.Dispose();
                _webView?.Dispose();
                _logoMenu?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ============ Data Model ============

        public class SessionInfo
        {
            public string Name { get; set; }
            public string Status { get; set; }
            public bool IsActive { get; set; }
        }
    }
}
