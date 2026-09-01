using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// WebView2 host for the attention rail — every open agent session as a card, pulsing when the
    /// agent is blocked on the owner (task 2289bb8a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Item 4 delivers the shell: the panel renders and themes correctly against seed data. Live
    /// data arrives in item 5 via <see cref="SetSessions"/>.
    /// </para>
    /// <para>
    /// ⚠️ Every field name crossing this boundary is a STRING CONTRACT NO COMPILER CHECKS. A
    /// PascalCase host paired with a camelCase view once produced a CRITICAL on a clean build with
    /// 442 green tests. Both directions here are camelCase, and item 9 pins them with a cross-file
    /// test in the manner of <c>BoardHudDoorwayTests</c>. Do not rename a field on one side alone.
    /// </para>
    /// </remarks>
    public class AttentionPanelControl : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private string _pendingSessionsJson;
        private string _order = "attention";

        /// <summary>Raised when the user clicks a card. Argument is the session id.</summary>
        public event EventHandler<string> FocusSessionRequested;

        /// <summary>Raised when the user clicks a ticket chip. Argument is the task id.</summary>
        public event EventHandler<string> OpenTicketRequested;

        /// <summary>Raised when the user changes the ordering preference ("attention" | "fixed").</summary>
        public event EventHandler<string> OrderChanged;

        /// <summary>Creates the control. The WebView2 initialises lazily, on first show.</summary>
        public AttentionPanelControl()
        {
            SuspendLayout();
            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "AttentionPanelControl";

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "attentionWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += (s, e) =>
            {
                if (Visible && !_isInitialized && !_isInitializing) InitializeWebView();
            };
        }

        /// <summary>Applies the current ordering preference, echoing it to the view.</summary>
        public void SetOrder(string order)
        {
            _order = string.Equals(order, "fixed", StringComparison.OrdinalIgnoreCase) ? "fixed" : "attention";
            if (_isInitialized) PostJson(new { type = "order", order = _order });
        }

        /// <summary>
        /// Pushes the full card list to the view. Safe to call before the WebView2 is ready — the
        /// most recent payload is held and replayed once the view reports ready.
        /// </summary>
        public void SetSessions(IEnumerable<object> sessions)
        {
            string json = JsonSerializer.Serialize(new { type = "sessions", sessions });
            if (!_isInitialized)
            {
                // Keep only the LATEST: replaying a queue of stale snapshots would animate the
                // panel through history it should never show.
                _pendingSessionsJson = json;
                return;
            }

            PostRaw(json);
        }

        /// <summary>Applies the app theme. Safe before initialisation; replayed on ready.</summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_webView != null)
            {
                _webView.DefaultBackgroundColor = isDark
                    ? System.Drawing.Color.FromArgb(26, 26, 46)
                    : System.Drawing.Color.FromArgb(245, 245, 245);
            }

            if (_isInitialized) PostJson(new { type = "theme", isDark });
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46)
                    : System.Drawing.Color.FromArgb(245, 245, 245);

                var s = _webView.CoreWebView2.Settings;
                s.IsScriptEnabled = true;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string htmlPath = FindHtml("AttentionPanel/attention-panel.html");
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    // Without the <Content Include PreserveNewest> in MultiTerminal.csproj the file
                    // is simply absent from the output and the panel renders blank with no error at
                    // all. Say so rather than presenting an empty panel as normal.
                    _isInitializing = false;
                }
            }
            catch
            {
                _isInitializing = false;
            }
        }

        private string FindHtml(params string[] relativePaths)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var rel in relativePaths)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
            }

            return Path.Combine(dir, relativePaths[0]);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (!doc.RootElement.TryGetProperty("type", out var t)) return;
                string msgType = t.GetString();

                if (msgType == "ready")
                {
                    _isInitialized = true;
                    _isInitializing = false;
                    PostJson(new { type = "theme", isDark = _isDarkTheme });
                    PostJson(new { type = "order", order = _order });
                    if (_pendingSessionsJson != null)
                    {
                        PostRaw(_pendingSessionsJson);
                        _pendingSessionsJson = null;
                    }
                }
                else if (msgType == "focus_session")
                {
                    if (doc.RootElement.TryGetProperty("sessionId", out var idEl))
                    {
                        string id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id)) FocusSessionRequested?.Invoke(this, id);
                    }
                }
                else if (msgType == "open_ticket")
                {
                    if (doc.RootElement.TryGetProperty("taskId", out var taskEl))
                    {
                        string taskId = taskEl.GetString();
                        if (!string.IsNullOrEmpty(taskId)) OpenTicketRequested?.Invoke(this, taskId);
                    }
                }
                else if (msgType == "set_order")
                {
                    if (doc.RootElement.TryGetProperty("order", out var orderEl))
                    {
                        string order = orderEl.GetString();
                        if (!string.IsNullOrEmpty(order))
                        {
                            _order = string.Equals(order, "fixed", StringComparison.OrdinalIgnoreCase) ? "fixed" : "attention";
                            OrderChanged?.Invoke(this, _order);
                        }
                    }
                }
            }
            catch
            {
                // A malformed message from the view must never take the panel down.
            }
        }

        private void PostJson(object payload) => PostRaw(JsonSerializer.Serialize(payload));

        private void PostRaw(string json)
        {
            try
            {
                if (_webView?.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // The view can be torn down between the check and the post.
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_webView?.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    }
                }
                catch
                {
                    // Already torn down.
                }

                _webView?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
