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
    public class HudNotesRenderer : UserControl, IZoomableTab
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectPath;

        public event EventHandler<double> ZoomChanged;

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
            if (_broker != null) _broker.ProjectNotesChanged -= OnProjectNotesChanged;
            _broker = broker;
            if (_broker != null) _broker.ProjectNotesChanged += OnProjectNotesChanged;
        }

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
                if (!IsHandleCreated) return;
                try { BeginInvoke(new Action(() => OnProjectNotesChanged(sender, e))); }
                catch (InvalidOperationException) { /* handle torn down between the check and the post */ }
                return;
            }

            if (string.IsNullOrEmpty(_projectPath) || !_isInitialized) return;
            if (!string.Equals(TaskDatabase.CanonicalNoteTabKey(_projectPath), e.ProjectKey, StringComparison.Ordinal)) return;
            LoadAllTabs(changedTab: e.TabName, changedBy: e.AgentName);
        }

        public void SetProject(string projectPath)
        {
            _projectPath = projectPath;
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
                var taskDb = _broker?.TaskDb;
                if (taskDb == null) return;

                var tabs = taskDb.GetProjectNoteTabDetails(_projectPath);
                var tabData = new List<object>();
                foreach (var tab in tabs)
                {
                    tabData.Add(new
                    {
                        name = tab.Name,
                        content = tab.Content,
                        isDefault = tab.IsDefault,
                        createdBy = tab.CreatedBy,
                        updatedBy = tab.UpdatedBy,
                    });
                }

                if (changedTab != null)
                    PostJsonMessage(new { type = "notes_changed", tabs = tabData, changedTab, changedBy });
                else if (selectTab != null)
                    PostJsonMessage(new { type = "load_all_tabs", tabs = tabData, activeTab = selectTab });
                else
                    PostJsonMessage(new { type = "load_all_tabs", tabs = tabData });
            }
            catch { }
        }

        private void SaveTab(string tabName, string content)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                _broker.TaskDb.SaveNoteTab(_projectPath, tabName, content);
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

        private void RenameTab(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                if (_broker.TaskDb.RenameNoteTab(_projectPath, oldName, newName))
                    LoadAllTabs();
            }
            catch { }
        }

        private void DeleteTab(string tabName)
        {
            if (string.IsNullOrEmpty(_projectPath) || _broker?.TaskDb == null) return;
            try
            {
                if (_broker.TaskDb.DeleteNoteTab(_projectPath, tabName))
                    LoadAllTabs();
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
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Dispose
        // -------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null) _broker.ProjectNotesChanged -= OnProjectNotesChanged;
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
