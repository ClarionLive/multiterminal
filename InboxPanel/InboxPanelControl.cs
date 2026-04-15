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
                System.Diagnostics.Debug.WriteLine("Inbox panel WebView2 init cancelled (shutdown).");
                _isInitializing = false;
            }
            catch (Exception ex) when (_isShuttingDown || IsDisposed || Disposing)
            {
                System.Diagnostics.Debug.WriteLine($"Inbox panel WebView2 init failed during shutdown: {ex.Message}");
                _isInitializing = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Inbox panel WebView2 init failed: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Inbox panel HTML path: {htmlPath}");
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Inbox panel HTML NOT FOUND at: {htmlPath}");
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

                switch (type)
                {
                    case "ready":
                        // JS is loaded and ready - send initial inbox data
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
                System.Diagnostics.Debug.WriteLine($"Inbox panel message error: {ex.Message}");
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
                var result = _broker.GetInbox(_defaultUserId);
                if (result.Success)
                {
                    var data = new
                    {
                        type = "inbox_data",
                        messages = result.Messages,
                        unreadCount = result.UnreadCount,
                        totalCount = result.TotalCount
                    };
                    var jsonString = JsonSerializer.Serialize(data, JsonOptions.UnicodeCamelCase);
                    PostMessage(jsonString);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Inbox panel SendInboxData error: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"Inbox panel PostMessage error: {ex.Message}");
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
