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
    /// Shows: the M menu, the project actions (Select Project, Browse All Projects, New Project,
    /// Just Claude, Open PowerShell), panel toggles, project info, active task. The M menu and the
    /// project picker open in <see cref="HeaderPopupForm"/> windows (task 4cac608c).
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

        // The M menu and the Select Project picker share one popup window; the Grid Layout flyout
        // has its own, so it can sit beside the menu (task 4cac608c).
        private HeaderPopupForm _menuPopup;
        private HeaderPopupForm _subPopup;
        private long _popupAutoClosedAt;      // Environment.TickCount64 when a click-away closed the popups
        private string _popupAutoClosedView;  // which view that was, so its own button doesn't reopen it

        // A click on the M or Select Project button while its popup is open first deactivates the
        // popup (closing it), then arrives as an action. Within this window, treat it as "close".
        private const int ReopenSuppressMs = 300;

        // Events for MainForm to handle actions
        public event Action BrowseAllRequested;
        public event Action NewProjectRequested;
        public event Action JustClaudeRequested;
        public event Action OpenPowerShellRequested;
        public event Action<string> SwitchProjectRequested;
        public event Action ToggleThemeRequested;
        public event Action SettingsRequested;
        public event Action DocsRequested;
        public event Action AboutRequested;
        public event Action<string> TogglePanelRequested;
        public event Action<string> GridLayoutRequested;
        public event Action ExitRequested;
        public event Action ShowChatHistoryRequested;
        public event Action DashboardReady;

        /// <summary>The projects the Select Project picker lists, and the current project's id.</summary>
        public Func<(IReadOnlyList<HeaderProjectItem> Projects, string CurrentProjectId)> ProjectListProvider { get; set; }

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
                        {
                            // The two popup buttons send their position so the popup can open under them.
                            var anchor = root.TryGetProperty("rect", out var rectEl) ? HeaderRectToScreen(rectEl) : (Rectangle?)null;
                            bool keyboard = root.TryGetProperty("keyboard", out var kbEl) && kbEl.ValueKind == JsonValueKind.True;
                            HandleAction(actionEl.GetString(), anchor, keyboard);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("DashboardHeader", $"Message error: {ex.Message}");
            }
        }

        /// <summary>
        /// Every action the header page and its popups can send (task 4cac608c: one vocabulary for
        /// both pages; DashboardHeaderDoorwayTests checks every action either page sends has a case).
        /// </summary>
        /// <param name="anchor">Screen rect of the button that sent it, for the two popup buttons.</param>
        /// <param name="keyboard">The button was pressed from the keyboard, so the popup takes focus on its first item.</param>
        private void HandleAction(string action, Rectangle? anchor = null, bool keyboard = false)
        {
            switch (action)
            {
                case "show_menu": ToggleAppMenu(anchor, keyboard); break;
                case "select_project": ToggleProjectPicker(anchor, keyboard); break;
                case "browse_all": BrowseAllRequested?.Invoke(); break;
                case "new_project": NewProjectRequested?.Invoke(); break;
                case "just_claude": JustClaudeRequested?.Invoke(); break;
                case "open_powershell": OpenPowerShellRequested?.Invoke(); break;
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
                case "toggle_attention": TogglePanelRequested?.Invoke("attention"); break;
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

        // ============ Header popups (task 4cac608c) ============

        /// <summary>Physical pixels per CSS pixel in the header page.</summary>
        private double HeaderScale => (_webView?.ZoomFactor ?? 1.0) * DeviceDpi / 96.0;

        private Rectangle? HeaderRectToScreen(JsonElement rect)
        {
            if (rect.ValueKind != JsonValueKind.Object) return null;
            double s = HeaderScale;
            var topLeft = _webView.PointToScreen(new Point(
                (int)Math.Round(rect.GetProperty("x").GetDouble() * s),
                (int)Math.Round(rect.GetProperty("y").GetDouble() * s)));
            return new Rectangle(topLeft, new Size(
                (int)Math.Round(rect.GetProperty("w").GetDouble() * s),
                (int)Math.Round(rect.GetProperty("h").GetDouble() * s)));
        }

        /// <summary>Fallback anchor (the M button's spot) when a message carries no rect.</summary>
        private Rectangle DefaultAnchor()
        {
            var s = HeaderScale;
            return new Rectangle(PointToScreen(new Point((int)(14 * s), (int)(16 * s))), new Size((int)(40 * s), (int)(40 * s)));
        }

        private void EnsurePopups()
        {
            if (_menuPopup != null) return;
            var owner = FindForm();

            _menuPopup = new HeaderPopupForm();
            _subPopup = new HeaderPopupForm();
            foreach (var popup in new[] { _menuPopup, _subPopup })
            {
                popup.SetOwnerForm(owner);
                popup.ActionChosen += OnPopupAction;
                popup.Deactivate += (s, e) => BeginInvoke(new Action(CloseIfFocusLeftPopups));
            }
            _menuPopup.SubmenuRequested += OnSubmenuRequested;
            _menuPopup.DismissRequested += back => CloseAllPopups();
            _subPopup.DismissRequested += back =>
            {
                _subPopup.HidePopup();
                if (back) _menuPopup.FocusPage();
                else CloseAllPopups();
            };

            _ = _menuPopup.WarmUpAsync();
            _ = _subPopup.WarmUpAsync();
        }

        private bool JustAutoClosed(string view) =>
            _popupAutoClosedView == view && Environment.TickCount64 - _popupAutoClosedAt < ReopenSuppressMs;

        private void ToggleAppMenu(Rectangle? anchor, bool keyboard)
        {
            EnsurePopups();
            if (_menuPopup.Visible && _menuPopup.CurrentView == "menu") { CloseAllPopups(); return; }
            if (JustAutoClosed("menu")) return;
            CloseAllPopups();
            var show = JsonSerializer.Serialize(new { type = "show", view = "menu", theme = ThemeName, keyboard });
            _menuPopup.ShowAt(show, anchor ?? DefaultAnchor(), PopupSide.Below, HeaderScale, _webView.ZoomFactor, activate: true);
        }

        private void ToggleProjectPicker(Rectangle? anchor, bool keyboard)
        {
            EnsurePopups();
            if (_menuPopup.Visible && _menuPopup.CurrentView == "projects") { CloseAllPopups(); return; }
            if (JustAutoClosed("projects")) return;
            CloseAllPopups();

            var (projects, currentId) = ProjectListProvider?.Invoke() ?? (Array.Empty<HeaderProjectItem>(), null);
            var show = JsonSerializer.Serialize(new
            {
                type = "show",
                view = "projects",
                theme = ThemeName,
                keyboard,
                currentProjectId = currentId ?? "",
                projects = projects.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    path = p.Path,
                    icon = p.Icon,
                    iconColor = p.IconColor,
                    isPinned = p.IsPinned,
                    status = p.Status,
                    lastOpenedAt = p.LastOpenedAt == default ? null : p.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                }),
            });
            _menuPopup.ShowAt(show, anchor ?? DefaultAnchor(), PopupSide.Below, HeaderScale, _webView.ZoomFactor, activate: true);
        }

        private void OnSubmenuRequested(bool open, double cssTop, bool focus)
        {
            if (!open)
            {
                _subPopup.HidePopup();
                return;
            }
            if (_subPopup.Visible)
            {
                if (focus) _subPopup.FocusPage();
                return;
            }
            // Line the flyout's first item up with the Grid Layout item: the panel has 6px padding.
            int top = _menuPopup.ScreenYForCssTop(cssTop) - (int)Math.Round(6 * HeaderScale);
            var anchor = new Rectangle(_menuPopup.Left, top, _menuPopup.Width, 1);
            var show = JsonSerializer.Serialize(new { type = "show", view = "submenu", theme = ThemeName, keyboard = focus });
            _subPopup.ShowAt(show, anchor, PopupSide.Right, HeaderScale, _webView.ZoomFactor, activate: focus);
        }

        private void OnPopupAction(string action, string projectId)
        {
            CloseAllPopups();
            if (action == "switch_project")
            {
                if (!string.IsNullOrEmpty(projectId)) SwitchProjectRequested?.Invoke(projectId);
                return;
            }
            HandleAction(action);
        }

        // Deactivate fires when focus moves anywhere else, including from the menu to its own
        // flyout. Only close when neither popup is the active window afterwards.
        private void CloseIfFocusLeftPopups()
        {
            var active = Form.ActiveForm;
            if (active == _menuPopup || active == _subPopup) return;
            if (_menuPopup?.Visible != true && _subPopup?.Visible != true) return;
            _popupAutoClosedView = _menuPopup?.Visible == true ? _menuPopup.CurrentView : "menu";
            _popupAutoClosedAt = Environment.TickCount64;
            CloseAllPopups();
        }

        private void CloseAllPopups()
        {
            _subPopup?.HidePopup();
            _menuPopup?.HidePopup();
        }

        private string ThemeName => _isDarkTheme ? "dark" : "light";

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
                RefreshActiveTask();
                RefreshInbox();

                // Build the popups now, hidden, so the first click on M opens instantly.
                EnsurePopups();

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

        public void UpdateInboxCount(int count)
        {
            PostJsonMessage($"{{\"type\":\"inbox_count\",\"count\":{count}}}");
        }

        public void UpdatePanelState(string panel, bool visible)
        {
            PostJsonMessage($"{{\"type\":\"panel_state\",\"panel\":\"{EscapeJson(panel)}\",\"visible\":{(visible ? "true" : "false")}}}");
        }

        public void UpdateVersion(string version)
        {
            PostJsonMessage($"{{\"type\":\"version\",\"version\":\"{EscapeJson(version)}\"}}");
        }

        // ============ Broker Event Handlers ============

        private void OnTasksUpdated(object sender, List<KanbanTask> e) => SafeInvoke(() => RefreshActiveTaskFromList(e));
        private void OnTaskClaimed(object sender, TaskClaimedEventArgs e) => SafeInvoke(RefreshActiveTask);
        // GH#6 (ticket 2b202b9a): InboxUpdatedEventArgs carries BOTH the UserId whose inbox
        // changed AND that user's unread count. This handler used to apply the count while
        // discarding the UserId, so a notification raised for ANY agent overwrote the badge
        // with THAT agent's total — the badge then showed a number belonging to an inbox the
        // user was not looking at, while the inbox list (which re-reads its own user) showed
        // something else entirely. That mismatch is exactly what #6 reports.
        private void OnInboxUpdated(object sender, InboxUpdatedEventArgs e) => SafeInvoke(() =>
        {
            if (!IsBadgeRecipient(e.UserId)) return;
            UpdateInboxCount(e.UnreadCount);
        });

        /// <summary>
        /// True when an inbox event belongs to the user whose count this badge displays.
        /// </summary>
        /// <remarks>
        /// Resolved LIVE from the broker on every call, never cached. <see cref="MessageBroker.DefaultInboxRecipient"/>
        /// is constructed as "Owner" and only later overwritten with the owner profile's first
        /// name (MainForm, post-construction) — a value captured when this control is built
        /// would pin the stale "Owner" and silently reintroduce the second half of GH#6.
        /// </remarks>
        private bool IsBadgeRecipient(string userId)
        {
            var recipient = _broker?.DefaultInboxRecipient;
            if (string.IsNullOrEmpty(recipient) || string.IsNullOrEmpty(userId)) return false;
            return string.Equals(userId, recipient, StringComparison.OrdinalIgnoreCase);
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

            // GH#6 (ticket 2b202b9a): this read the literal "Owner" while the inbox LIST reads
            // Broker.DefaultInboxRecipient — which MainForm overwrites with the owner profile's
            // first name precisely "so inbox notifications go to the actual user, not a
            // hardcoded name". On any machine with a profile set, the badge's baseline was
            // therefore the unread count of a user that has no inbox rows at all (observed:
            // badge 0 while the real owner had 209 unread).
            var recipient = _broker.DefaultInboxRecipient;
            if (string.IsNullOrEmpty(recipient)) return;

            var result = _broker.GetInbox(recipient);
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
                    _broker.TasksUpdated -= OnTasksUpdated;
                    _broker.TaskClaimed -= OnTaskClaimed;
                    _broker.InboxUpdated -= OnInboxUpdated;
                }
                _fallbackTimer?.Dispose();
                _webView?.Dispose();
                _subPopup?.Dispose();
                _menuPopup?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
