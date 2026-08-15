using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.InboxPanel
{
    /// <summary>
    /// WebView2-based control for displaying the user's inbox notifications.
    /// Shows inbox messages with actions to mark read, reply, and navigate to related tasks.
    /// </summary>
    public class InboxPanelControl : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isShuttingDown;
        private MessageBroker _broker;
        private string _defaultUserId = "Owner";

        /// <summary>
        /// Row cap for a single inbox fetch. Passed EXPLICITLY to <see cref="MessageBroker.GetInbox"/>
        /// rather than relying on its <c>limit = 50</c> default.
        /// </summary>
        /// <remarks>
        /// Taking that default is what made this panel lie. It fetches the newest N rows of ALL
        /// messages, while the badge renders <c>unreadCount</c> — the TRUE total. Measured on a real
        /// inbox: 2951 rows, 259 unread, but the panel held only the newest 50, of which 46 were
        /// unread. So 213 unread messages were unreachable — not visible, not individually clearable,
        /// and the badge cheerfully reported all 259. Same defect class as GH#6: a counter and a list
        /// reading different data.
        ///
        /// A cap still exists (this is pushed to a WebView on every refresh, so it cannot be
        /// unbounded), but it is now (a) explicit, (b) large enough to cover a realistic unread
        /// backlog, and (c) DISCLOSED — see <c>totalCount</c>/<c>unreadCount</c> in the payload and
        /// the "showing N of M" line the panel renders when the list is short of the count. A capped
        /// list that admits it is capped is honest; one that silently implies completeness is not.
        /// </remarks>
        private const int InboxFetchLimit = 500;

        /// <summary>
        /// Whether the panel is currently filtering to unread only. Mirrors the JS
        /// <c>showUnreadOnly</c>, which starts <c>true</c>, and is updated by the panel whenever the
        /// user toggles the filter. The fetch follows the view: in unread mode we ask the broker for
        /// unread rows, so the list can show the whole unread set rather than whichever unread rows
        /// happen to fall inside a window of recent traffic.
        /// </summary>
        private bool _showUnreadOnly = true;

        /// <summary>
        /// Raised when the user clicks a task link to navigate to that task on the kanban board.
        /// The string argument is the task ID.
        /// </summary>
        public event EventHandler<string> NavigateToTask;

        public InboxPanelControl()
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

            // Wait for handle before initializing WebView2
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            // Only initialize if Initialize() was already called with a broker
            if (!_initializePending || _isInitializing || _isInitialized) return;
            await InitializeWebView2Async();
        }

        /// <summary>
        /// Initialize the inbox panel with a message broker.
        /// </summary>
        /// <param name="broker">The MessageBroker instance for inbox operations.</param>
        /// <param name="defaultUserId">The default user ID whose inbox to display.</param>
        public async void Initialize(MessageBroker broker, string defaultUserId = "Owner")
        {
            _broker = broker;
            _defaultUserId = defaultUserId;

            // Subscribe to broker events for real-time inbox updates
            _broker.InboxUpdated += OnInboxUpdated;

            _initializePending = true;

            // Only initialize WebView2 if handle already exists
            // Otherwise, OnHandleCreated will trigger initialization
            if (IsHandleCreated && !_isInitializing && !_isInitialized)
            {
                await InitializeWebView2Async();
            }
        }

        private async System.Threading.Tasks.Task InitializeWebView2Async()
        {
            if (_isInitializing || _isInitialized || _isShuttingDown) return;
            _isInitializing = true;

            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (OperationCanceledException)
            {
                _broker?.DebugLogService?.Warning("InboxPanel", "Inbox panel WebView2 init cancelled (shutdown).");
                _isInitializing = false;
            }
            catch (Exception ex) when (_isShuttingDown || IsDisposed || Disposing)
            {
                _broker?.DebugLogService?.Error("InboxPanel", $"Inbox panel WebView2 init failed during shutdown: {ex.Message}");
                _isInitializing = false;
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("InboxPanel", $"Inbox panel WebView2 init failed: {ex.Message}");
                _isInitializing = false;
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                if (_isShuttingDown || IsDisposed || Disposing || !IsHandleCreated)
                    return;
                MessageBox.Show($"WebView2 initialization failed: {e.InitializationException?.Message}",
                    "Inbox Panel Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load the HTML using robust path searching
            var htmlPath = GetHtmlPath();
            _broker?.DebugLogService?.Info("InboxPanel", $"Inbox panel HTML path: {htmlPath}");
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                _broker?.DebugLogService?.Warning("InboxPanel", $"Inbox panel HTML NOT FOUND at: {htmlPath}");
                _webView.CoreWebView2.NavigateToString(
                    $"<html><body><h1>Inbox panel HTML not found</h1><p>Searched: {htmlPath}</p></body></html>");
            }

            _isInitialized = true;
        }

        private string GetHtmlPath()
        {
            // Try to find inbox-panel.html relative to the assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Check in InboxPanel subfolder
            string path = Path.Combine(assemblyDir, "InboxPanel", "inbox-panel.html");
            if (File.Exists(path)) return path;

            // Check in same folder as assembly
            path = Path.Combine(assemblyDir, "inbox-panel.html");
            if (File.Exists(path)) return path;

            // Check in parent folder's InboxPanel subfolder (for development)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "InboxPanel", "inbox-panel.html");
                if (File.Exists(path)) return path;
            }

            // Try AppDomain base directory as last resort
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InboxPanel", "inbox-panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "InboxPanel", "inbox-panel.html");
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var type = typeElement.GetString();

                // The panel owns the filter, so it reports the mode on every message that triggers a
                // fetch. Adopting it BEFORE the switch means "ready", "refresh" and "set_filter" all
                // fetch for the view the user is actually looking at, without three copies of this.
                // Absent field => leave the current mode alone (older panel builds, and messages
                // like navigate_to_task that have no opinion about filtering).
                if (root.TryGetProperty("unreadOnly", out var unreadOnlyElement) &&
                    (unreadOnlyElement.ValueKind == JsonValueKind.True || unreadOnlyElement.ValueKind == JsonValueKind.False))
                {
                    _showUnreadOnly = unreadOnlyElement.GetBoolean();
                }

                switch (type)
                {
                    case "ready":
                        // JS is loaded and ready - send initial inbox data
                        SendInboxData();
                        break;

                    case "set_filter":
                        // The user toggled Unread Only / Show All. The mode was adopted above; the
                        // re-fetch matters because the two modes are DIFFERENT QUERIES now, not two
                        // client-side views of one payload.
                        SendInboxData();
                        break;

                    case "mark_read":
                        if (root.TryGetProperty("messageId", out var markReadId))
                        {
                            _broker.MarkInboxRead(markReadId.GetString());
                            SendInboxData();
                        }
                        break;

                    case "mark_all_read":
                        _broker.MarkAllInboxRead(_defaultUserId);
                        SendInboxData();
                        break;

                    case "reply":
                        if (root.TryGetProperty("messageId", out var replyMsgId) &&
                            root.TryGetProperty("replyText", out var replyText))
                        {
                            _broker.ReplyToInbox(replyMsgId.GetString(), replyText.GetString());
                            SendInboxData();
                        }
                        break;

                    case "navigate_to_task":
                        if (root.TryGetProperty("taskId", out var taskId))
                        {
                            NavigateToTask?.Invoke(this, taskId.GetString());
                        }
                        break;

                    case "refresh":
                        SendInboxData();
                        break;
                }
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("InboxPanel", $"Inbox panel message error: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieve inbox data from the broker and send it to the WebView for rendering.
        /// </summary>
        private void SendInboxData()
        {
            if (!_isInitialized || _broker == null) return;

            try
            {
                // Explicit arguments on purpose — see InboxFetchLimit. Relying on GetInbox's
                // defaults (unreadOnly:false, limit:50) is the bug this panel had: it fetched a
                // window of recent traffic while the badge counted the whole backlog.
                var result = _broker.GetInbox(_defaultUserId, unreadOnly: _showUnreadOnly, limit: InboxFetchLimit);
                if (result.Success)
                {
                    // `truncated` lets the panel say "showing N of M" instead of implying the list
                    // is everything. We only claim truncation when the fetch actually hit the cap;
                    // a short list under the cap is complete for the current filter.
                    var returned = result.Messages?.Count ?? 0;
                    var data = new
                    {
                        type = "inbox_data",
                        messages = result.Messages,
                        unreadCount = result.UnreadCount,
                        totalCount = result.TotalCount,
                        unreadOnly = _showUnreadOnly,
                        returnedCount = returned,
                        truncated = returned >= InboxFetchLimit
                    };
                    var jsonString = JsonSerializer.Serialize(data, JsonOptions.UnicodeCamelCase);
                    PostMessage(jsonString);
                }
                else
                {
                    // A failed fetch MUST still push, and must still echo the host's mode.
                    // GetInbox turns every exception into Success=false, so without this branch the
                    // failure was invisible twice over: nothing logged, and nothing sent. After a
                    // set_filter that leaves the checkbox already flipped while the list on screen
                    // is still the PREVIOUS mode's payload, with nothing to correct it until an
                    // unrelated InboxUpdated happens to arrive — which is item 5's original
                    // complaint (the control disagreeing with the list) coming back through the
                    // error path. Echoing unreadOnly re-syncs the checkbox to the data actually
                    // being displayed.
                    _broker?.DebugLogService?.Error(
                        "InboxPanel",
                        $"Inbox fetch failed for '{_defaultUserId}' (unreadOnly={_showUnreadOnly}): {result.Error}");
                    var failure = new
                    {
                        type = "inbox_data",
                        messages = Array.Empty<object>(),
                        unreadCount = 0,
                        totalCount = 0,
                        unreadOnly = _showUnreadOnly,
                        returnedCount = 0,
                        truncated = false,
                        error = result.Error ?? "Could not load your inbox."
                    };
                    PostMessage(JsonSerializer.Serialize(failure, JsonOptions.UnicodeCamelCase));
                }
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("InboxPanel", $"Inbox panel SendInboxData error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle real-time inbox updates from the message broker.
        /// </summary>
        private void OnInboxUpdated(object sender, InboxUpdatedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnInboxUpdated(sender, e)));
                return;
            }

            // Refresh the full inbox when any update occurs
            SendInboxData();
        }

        /// <summary>
        /// Post a JSON message to the WebView2 JavaScript layer.
        /// </summary>
        private void PostMessage(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => PostMessage(json)));
                    return;
                }

                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("InboxPanel", $"Inbox panel PostMessage error: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply theme to the inbox panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            var themeMsg = JsonSerializer.Serialize(new { type = "theme", isDark = isDark });
            PostMessage(themeMsg);
        }

        /// <summary>
        /// Sets the font size for the inbox panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            var fontMsg = JsonSerializer.Serialize(new { type = "font_size", size = size });
            PostMessage(fontMsg);
        }

        protected override void Dispose(bool disposing)
        {
            _isShuttingDown = true;

            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.InboxUpdated -= OnInboxUpdated;
                }
                _webView?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
