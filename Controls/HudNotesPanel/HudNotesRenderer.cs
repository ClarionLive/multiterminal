using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based notes editor with multi-tab support.
    /// Each project gets multiple named note tabs stored in project_note_tabs.
    /// </summary>
    /// <remarks>
    /// The unread count (task c133b27d) is computed here from the database, not reported by the page:
    /// this control is built and subscribed when the terminal opens, but its WebView2 only loads the first
    /// time the Notes tab is shown, so a page-side count would read zero for every note written before then.
    /// </remarks>
    public class HudNotesRenderer : UserControl, IZoomableTab, ITabBadgeSource
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectPath;
        private int _badgeCount;
        private string _countedProject; // the project _badgeCount was counted for
        // The window whose minimise/restore changes whether the Owner can see us. Borrowed, not owned:
        // we only subscribe to it, and Dispose unsubscribes.
#pragma warning disable CA2213
        private Form _seenForm;
#pragma warning restore CA2213
        private bool? _lastSentSeen;
        private Action<string> _pageSinkForTests; // stands in for the WebView2 page in tests

        public event EventHandler<double> ZoomChanged;

        /// <inheritdoc/>
        public event EventHandler BadgeCountChanged;

        /// <summary>Unread agent notes in this pane's project (task c133b27d).</summary>
        public int BadgeCount => _badgeCount;

        public HudNotesRenderer()
        {
            SuspendLayout();

            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudNotesRenderer";
            Visible = false;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "notesWebView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += OnVisibleChanged;
        }

        private void OnVisibleChanged(object sender, EventArgs e)
        {
            if (Visible && !_isInitialized && !_isInitializing)
            {
                InitializeWebView();
            }
            HookSeenForm();
            SendSeen();
        }

        /// <summary>
        /// Whether the Owner can see this pane right now (task c133b27d): it and every parent are visible
        /// (so the Notes HUD tab is the selected one and the HUD is not collapsed) and its window is shown
        /// and not minimised. <see cref="Control.Visible"/> stays true on a minimised window, hence the
        /// separate check. This is what lets the page count the active note as read; loading the page is
        /// not enough.
        /// </summary>
        internal bool IsSeenByOwner()
        {
            if (!Visible) return false;
            var form = SeenWindowFor(this);
            return form != null && form.Visible && form.WindowState != FormWindowState.Minimized;
        }

        /// <summary>
        /// The window whose minimise decides whether <paramref name="control"/> can be seen: the TOP-LEVEL
        /// form (MainForm when docked, the float window when floated). Not <see cref="Control.FindForm"/>:
        /// that stops at the nearest Form, and a docked <c>TerminalDocument</c> is a DockContent, which is
        /// itself a non-top-level Form whose WindowState never becomes Minimized and which gets no
        /// Resize when the main window is minimised (pipeline Run 1, debugger HIGH).
        /// </summary>
        internal static Form SeenWindowFor(Control control) => control?.TopLevelControl as Form;

        // The window can change under us (a docked pane floated out), so re-resolve it on every SendSeen.
        private void HookSeenForm()
        {
            var form = SeenWindowFor(this);
            if (ReferenceEquals(form, _seenForm)) return;
            if (_seenForm != null)
            {
                _seenForm.Resize -= OnSeenFormChanged;
                _seenForm.VisibleChanged -= OnSeenFormChanged;
            }
            _seenForm = form;
            if (_seenForm != null)
            {
                _seenForm.Resize += OnSeenFormChanged;
                _seenForm.VisibleChanged += OnSeenFormChanged;
            }
        }

        private void OnSeenFormChanged(object sender, EventArgs e) => SendSeen();

        private void SendSeen()
        {
            if (!_isInitialized) return;
            HookSeenForm(); // docking or floating the pane changes the top-level window
            bool seen = IsSeenByOwner();
            if (_lastSentSeen == seen) return;
            _lastSentSeen = seen;
            _broker?.DebugLogService?.Trace("HudNotesRenderer",
                $"seen={seen} window={_seenForm?.Name ?? "(none)"} state={_seenForm?.WindowState.ToString() ?? "-"}");
            PostJsonMessage(new { type = "seen", seen });
        }

        /// <summary>
        /// Re-reads the unread count and raises <see cref="BadgeCountChanged"/> when it moved. Runs whether
        /// or not the page has loaded; that independence is the point. A failed read keeps the last known
        /// count: publishing 0 on an error would hide unread notes, which is the one thing the badge is for.
        /// </summary>
        internal void RecountUnread()
        {
            int count = 0;
            try
            {
                if (!string.IsNullOrEmpty(_projectPath) && _broker?.TaskDb != null)
                    count = _broker.TaskDb.CountUnreadNoteTabs(_projectPath);
            }
            catch
            {
                // Keep the last known count; the next note event recounts. But never carry another
                // project's count across a project switch: there, no badge is the honest answer.
                if (string.Equals(_countedProject, _projectPath, StringComparison.Ordinal)) return;
                count = 0;
            }

            _countedProject = _projectPath;
            if (count == _badgeCount) return;
            _badgeCount = count;
            BadgeCountChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46)
                    : System.Drawing.Color.FromArgb(245, 245, 245);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    _isInitializing = false;
                }
            }
            catch
            {
                _isInitializing = false;
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "Controls", "HudNotesPanel", "hud-notes.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "HudNotesPanel", "hud-notes.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "Controls", "HudNotesPanel", "hud-notes.html");
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                string type = typeEl.GetString();

                switch (type)
                {
                    case "ready":
                        _isInitialized = true;
                        _isInitializing = false;
                        PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });
                        _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                        if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                            _webView.ZoomFactor = _pendingZoom;
                        LoadAllTabs();
                        _lastSentSeen = null;
                        HookSeenForm();
                        SendSeen();
                        break;

                    case "mark_read":
                        if (root.TryGetProperty("tabName", out var readNameEl) &&
                            root.TryGetProperty("rev", out var readRevEl) &&
                            readRevEl.TryGetInt64(out long readRev))
                        {
                            MarkRead(readNameEl.GetString(), readRev);
                        }
                        break;

                    case "save_tab":
                        if (root.TryGetProperty("tabName", out var saveNameEl) &&
                            root.TryGetProperty("content", out var contentEl))
                        {
                            SaveTab(saveNameEl.GetString(), contentEl.GetString());
                        }
                        break;

                    case "add_tab":
                        if (root.TryGetProperty("tabName", out var addNameEl))
                        {
                            AddTab(addNameEl.GetString());
                        }
                        break;

                    case "rename_tab":
                        if (root.TryGetProperty("oldName", out var oldNameEl) &&
                            root.TryGetProperty("newName", out var newNameEl))
                        {
                            RenameTab(oldNameEl.GetString(), newNameEl.GetString());
                        }
                        break;

                    case "delete_tab":
                        if (root.TryGetProperty("tabName", out var delNameEl))
                        {
                            DeleteTab(delNameEl.GetString());
                        }
                        break;

                    case "reorder_tabs":
                        if (root.TryGetProperty("tabNames", out var orderEl))
                        {
                            var names = new List<string>();
                            foreach (var item in orderEl.EnumerateArray())
                                names.Add(item.GetString());
                            ReorderTabs(names);
                        }
                        break;

                    // Legacy protocol support (single-tab save)
                    case "save_notes":
                        if (root.TryGetProperty("content", out var legacyContentEl))
                        {
                            SaveTab("General", legacyContentEl.GetString());
                        }
                        break;
                }
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        public void Initialize(MessageBroker broker)
        {
            if (_broker != null)
            {
                _broker.ProjectNotesChanged -= OnProjectNotesChanged;
                _broker.ProjectNoteReadStateChanged -= OnProjectNoteReadStateChanged;
            }
            _broker = broker;
            if (_broker != null)
            {
                _broker.ProjectNotesChanged += OnProjectNotesChanged;
                _broker.ProjectNoteReadStateChanged += OnProjectNoteReadStateChanged;
            }
            RecountUnread();
        }

        /// <summary>
        /// Another pane (or this one) read, edited or deleted a note (task c133b27d). Recount, and refresh
        /// the page's per-tab unread flags if it is loaded, so every terminal on the project agrees.
        /// </summary>
        private void OnProjectNoteReadStateChanged(object sender, ProjectNoteReadStateChangedEventArgs e)
        {
            if (e == null || IsDisposed) return;
            if (InvokeRequired)
            {
                PostToUiThread(() => OnProjectNoteReadStateChanged(sender, e));
                return;
            }

            if (!IsForThisProject(e.ProjectKey)) return;
            RecountUnread();
            if (!_isInitialized) return;
            // A rename or delete in another pane makes this page's tab names stale: a flags-only update
            // would leave it posting mark_read under a name that no longer exists, and the badge stuck.
            if (e.TabsChanged) SendTabsMerge(e.RenamedFrom, e.RenamedTo);
            else SendUnreadState();
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread from a broker event raised elsewhere (agent note
        /// writes arrive on the REST thread). Posts through the nearest control that HAS a window handle,
        /// not necessarily this one: a Notes tab that has never been shown has no handle of its own (it is
        /// created hidden), while <see cref="Control.InvokeRequired"/> is already true through its parent.
        /// Requiring our own handle dropped every agent write for such a pane, so its badge never counted
        /// a new note until the page was first opened (Owner live test of c133b27d, bug 2).
        /// </summary>
        internal bool PostToUiThread(Action action)
        {
            Control target = this;
            while (target != null && !target.IsHandleCreated) target = target.Parent;
            if (target == null || target.IsDisposed) return false;
            try
            {
                target.BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // handle torn down between the check and the post
            }
        }

        private bool IsForThisProject(string projectKey)
            => !string.IsNullOrEmpty(_projectPath)
               && string.Equals(TaskDatabase.CanonicalNoteTabKey(_projectPath), projectKey, StringComparison.Ordinal);

        /// <summary>
        /// An agent wrote a note (task 5d94ac91). Raised on the REST thread, so marshal to the UI
        /// thread, and only for the project this pane shows: the key is compared in its canonical
        /// form, ordinally, because that is how the note rows themselves are keyed.
        /// </summary>
        private void OnProjectNotesChanged(object sender, ProjectNotesChangedEventArgs e)
        {
            if (e == null || IsDisposed) return;
            if (InvokeRequired)
            {
                PostToUiThread(() => OnProjectNotesChanged(sender, e));
                return;
            }

            if (!IsForThisProject(e.ProjectKey)) return;
            RecountUnread(); // before the page check: the badge must count notes the page has never loaded
            if (!_isInitialized) return;
            LoadAllTabs(changedTab: e.TabName, changedBy: e.AgentName);
        }

        public void SetProject(string projectPath)
        {
            _projectPath = projectPath;
            RecountUnread();
            if (_isInitialized)
            {
                LoadAllTabs();
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized)
                PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        // -------------------------------------------------------------------------
        // Tab operations
        // -------------------------------------------------------------------------

        /// <summary>
        /// Sends every tab to the page. With <paramref name="changedTab"/> it is a live update
        /// (<c>notes_changed</c>) that the page MERGES, keeping the Owner's unsaved typing and current
        /// tab; otherwise a full <c>load_all_tabs</c>.
        /// </summary>
        private void LoadAllTabs(string selectTab = null, string changedTab = null, string changedBy = null)
        {
            if (string.IsNullOrEmpty(_projectPath))
            {
                PostJsonMessage(new { type = "no_project" });
                return;
            }

            try
            {
                var tabData = BuildTabData();
                if (tabData == null) return;

                if (changedTab != null)
                    PostJsonMessage(new { type = "notes_changed", tabs = tabData, changedTab, changedBy });
                else if (selectTab != null)
                    PostJsonMessage(new { type = "load_all_tabs", tabs = tabData, activeTab = selectTab });
                else
                    PostJsonMessage(new { type = "load_all_tabs", tabs = tabData });
            }
            catch { }
        }

        private List<object> BuildTabData()
        {
            var taskDb = _broker?.TaskDb;
            if (taskDb == null) return null;

            var tabData = new List<object>();
            foreach (var tab in taskDb.GetProjectNoteTabDetails(_projectPath))
            {
                tabData.Add(new
                {
                    name = tab.Name,
                    content = tab.Content,
                    isDefault = tab.IsDefault,
                    createdBy = tab.CreatedBy,
                    updatedBy = tab.UpdatedBy,
                    unread = tab.IsUnread,
                    agentRev = tab.AgentRev,
                });
            }
            return tabData;
        }

        /// <summary>
        /// The tab list changed in another pane (task c133b27d): send it as a <c>notes_changed</c> MERGE, which
        /// keeps the Owner's current tab and unsaved typing, with the rename (if any) so the page can carry
        /// that typing across to the new name. No <c>changedTab</c>: nothing was written by an agent.
        /// </summary>
        private void SendTabsMerge(string renamedFrom, string renamedTo)
        {
            if (string.IsNullOrEmpty(_projectPath)) return;
            try
            {
                var tabData = BuildTabData();
                if (tabData == null) return;
                object renamed = renamedFrom != null && renamedTo != null ? new { from = renamedFrom, to = renamedTo } : null;
                PostJsonMessage(new { type = "notes_changed", tabs = tabData, changedTab = (string)null, changedBy = (string)null, renamed });
            }
            catch { }
        }

        /// <summary>
        /// The page's per-tab unread flags, without resending content (task c133b27d). A full reload here
        /// would disturb the Owner's typing for what is only a read-state change.
        /// </summary>
        private void SendUnreadState()
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                var state = new List<object>();
                foreach (var tab in _broker.TaskDb.GetProjectNoteTabDetails(_projectPath))
                    state.Add(new { name = tab.Name, unread = tab.IsUnread, agentRev = tab.AgentRev });
                PostJsonMessage(new { type = "unread_state", tabs = state });
            }
            catch { }
        }

        internal void MarkRead(string tabName, long seenRev)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null || string.IsNullOrEmpty(tabName)) return;
            // The page asks only while it believes it is seen; re-check here, since a WebView2 message can
            // arrive after the window was minimised or the Notes tab switched away.
            if (!IsSeenByOwner())
            {
                RefuseMarkRead();
                return;
            }
            try
            {
                if (_broker.TaskDb.MarkNoteTabRead(_projectPath, tabName, seenRev))
                    _broker.NotifyProjectNoteReadStateChanged(_projectPath);
                else
                    SendUnreadState(); // a stale rev: the page dropped its dot, so give it the stored truth and the current rev
            }
            catch { }
        }

        /// <summary>
        /// The page asked to mark a tab read while the Owner could not see it. The page has already
        /// cleared its dot, so without an answer the note stays unread in the database with nothing on
        /// screen to say so, and nothing ever asks again (Owner live test of c133b27d, bug 1: a note
        /// appended while MT was minimised stayed at agent_rev 3 / owner_read_rev 2 after the restore).
        /// Tell the page it is not seen and restore its flags; the next real visibility change then
        /// sends <c>seen</c> again, which re-marks the active tab with the revision it now holds.
        /// The page only asks while it believes it is seen, so a refusal also means the host missed the
        /// change that hid it. Why it can miss one is not known (moving the pane to another window was
        /// tried and is noticed). Re-resolve and re-subscribe the window here anyway, so whatever detached
        /// it cannot also hide the restore that should re-mark the tab.
        /// </summary>
        private void RefuseMarkRead()
        {
            HookSeenForm();
            _broker?.DebugLogService?.Trace("HudNotesRenderer",
                $"mark_read refused (not seen); last sent seen={_lastSentSeen?.ToString() ?? "none"} window={_seenForm?.Name ?? "(none)"}");
            _lastSentSeen = false;
            PostJsonMessage(new { type = "seen", seen = false });
            SendUnreadState();
        }

        private void SaveTab(string tabName, string content)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                // Editing a note the agent wrote also reads it.
                if (_broker.TaskDb.SaveNoteTab(_projectPath, tabName, content))
                    _broker.NotifyProjectNoteReadStateChanged(_projectPath);
            }
            catch { }
        }

        private void AddTab(string tabName)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null || string.IsNullOrEmpty(tabName)) return;
            try
            {
                _broker.TaskDb.SaveNoteTab(_projectPath, tabName, "");
                LoadAllTabs(tabName);
            }
            catch { }
        }

        internal void RenameTab(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                if (_broker.TaskDb.RenameNoteTab(_projectPath, oldName, newName))
                {
                    LoadAllTabs();
                    _broker.NotifyProjectNoteTabsChanged(_projectPath, oldName, newName); // other panes take the new name
                }
            }
            catch { }
        }

        internal void DeleteTab(string tabName)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                if (_broker.TaskDb.DeleteNoteTab(_projectPath, tabName))
                {
                    LoadAllTabs();
                    _broker.NotifyProjectNoteTabsChanged(_projectPath); // drops the count, and the tab in other panes
                }
            }
            catch { }
        }

        private void ReorderTabs(List<string> tabNames)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                _broker.TaskDb.ReorderNoteTabs(_projectPath, tabNames);
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Messaging
        // -------------------------------------------------------------------------

        private void PostJsonMessage(object data)
        {
            if (_pageSinkForTests != null)
            {
                _pageSinkForTests(JsonSerializer.Serialize(data));
                return;
            }
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        /// <summary>
        /// Tests only: behave as if the page had loaded and sent <c>ready</c>, delivering every message the
        /// host would post to the page to <paramref name="sink"/> as JSON. No WebView2 is created.
        /// <see cref="DetachSeenWindowForTests"/> stops listening to the window, to reproduce a host that
        /// missed a minimise (Owner live test of c133b27d, bug 1).
        /// </summary>
        internal void DetachSeenWindowForTests()
        {
            if (_seenForm == null) return;
            _seenForm.Resize -= OnSeenFormChanged;
            _seenForm.VisibleChanged -= OnSeenFormChanged;
            _seenForm = null;
        }

        internal void AttachPageForTests(Action<string> sink)
        {
            _pageSinkForTests = sink;
            _isInitialized = true;
            _lastSentSeen = null;
            HookSeenForm();
            SendSeen();
        }

        // -------------------------------------------------------------------------
        // Dispose
        // -------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.ProjectNotesChanged -= OnProjectNotesChanged;
                    _broker.ProjectNoteReadStateChanged -= OnProjectNoteReadStateChanged;
                }
                if (_seenForm != null)
                {
                    _seenForm.Resize -= OnSeenFormChanged;
                    _seenForm.VisibleChanged -= OnSeenFormChanged;
                    _seenForm = null;
                }
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
