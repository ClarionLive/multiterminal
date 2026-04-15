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
using MultiTerminal.Terminal;
using ChatMessage = MultiTerminal.MCPServer.Models.Message;

namespace MultiTerminal.ChatPanel
{
    /// <summary>
    /// WebView2-based control for displaying the chat panel UI.
    /// </summary>
    public class ChatPanelControl : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isPaused;
        private bool _isShuttingDown;
        private MessageBroker _broker;
        private double _pendingZoom = 1.0;

        /// <summary>
        /// Raised when the user clicks the inject button on a message.
        /// </summary>
        public event EventHandler<InjectMessageEventArgs> InjectRequested;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Applies immediately if initialized, otherwise deferred.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        /// <summary>
        /// Raised when the user clicks the reply button on a message.
        /// </summary>
        public event EventHandler<ReplyMessageEventArgs> ReplyRequested;

        public ChatPanelControl()
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

            // Wait for handle before initializing WebView2 (like WebViewTerminalRenderer)
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            // Only initialize if Initialize() was already called with a broker
            if (!_initializePending || _isInitializing || _isInitialized) return;
            await InitializeWebView2Async();
        }

        /// <summary>
        /// Initialize the chat panel with a message broker.
        /// </summary>
        public async void Initialize(MessageBroker broker)
        {
            _broker = broker;

            // Subscribe to broker events
            _broker.MessageSent += OnMessageSent;
            _broker.TerminalRegistered += OnTerminalChanged;
            _broker.TerminalDisconnected += OnTerminalChanged;
            _broker.HelperMessageLogged += OnHelperMessageLogged;
            _broker.ProfilesUpdated += OnProfilesUpdated;

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
                // Expected during shutdown — WebView2 aborts initialization when the host is closing
                System.Diagnostics.Debug.WriteLine("Chat panel WebView2 init cancelled (shutdown).");
                _isInitializing = false;
            }
            catch (Exception ex) when (_isShuttingDown || IsDisposed || Disposing)
            {
                // Suppress any errors during shutdown
                System.Diagnostics.Debug.WriteLine($"Chat panel WebView2 init failed during shutdown: {ex.Message}");
                _isInitializing = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chat panel WebView2 init failed: {ex.Message}");
                _isInitializing = false;
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                // Suppress error during shutdown — WebView2 COM teardown causes E_ABORT / "Class not registered"
                if (_isShuttingDown || IsDisposed || Disposing || !IsHandleCreated)
                    return;
                MessageBox.Show($"WebView2 initialization failed: {e.InitializationException?.Message}",
                    "Chat Panel Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load the HTML using robust path searching
            var htmlPath = GetHtmlPath();
            System.Diagnostics.Debug.WriteLine($"Chat panel HTML path: {htmlPath}");
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Chat panel HTML NOT FOUND at: {htmlPath}");
                _webView.CoreWebView2.NavigateToString($"<html><body><h1>Chat panel HTML not found</h1><p>Searched: {htmlPath}</p></body></html>");
            }

            _isInitialized = true;

            _webView.ZoomFactorChanged += (s, e) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
            if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                _webView.ZoomFactor = _pendingZoom;
        }

        private string GetHtmlPath()
        {
            // Try to find chat-panel.html relative to the assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Check in ChatPanel subfolder
            string path = Path.Combine(assemblyDir, "ChatPanel", "chat-panel.html");
            if (File.Exists(path)) return path;

            // Check in same folder as assembly
            path = Path.Combine(assemblyDir, "chat-panel.html");
            if (File.Exists(path)) return path;

            // Check in parent folder's ChatPanel subfolder (for development)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "ChatPanel", "chat-panel.html");
                if (File.Exists(path)) return path;
            }

            // Try AppDomain base directory as last resort
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatPanel", "chat-panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "ChatPanel", "chat-panel.html");
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

                var messageType = typeElement.GetString();

                switch (messageType)
                {
                    case "ready":
                        OnPanelReady();
                        break;

                    case "inject":
                        if (root.TryGetProperty("terminal", out var terminalEl) &&
                            root.TryGetProperty("content", out var contentEl))
                        {
                            InjectRequested?.Invoke(this, new InjectMessageEventArgs
                            {
                                TerminalName = terminalEl.GetString(),
                                Content = contentEl.GetString()
                            });
                        }
                        break;

                    case "reply":
                        if (root.TryGetProperty("messageId", out var messageIdEl) &&
                            root.TryGetProperty("senderName", out var senderNameEl))
                        {
                            ReplyRequested?.Invoke(this, new ReplyMessageEventArgs
                            {
                                MessageId = messageIdEl.GetString(),
                                SenderName = senderNameEl.GetString()
                            });
                        }
                        break;

                    case "pause":
                        ToggleChatPause();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chat panel message error: {ex.Message}");
            }
        }

        private void OnPanelReady()
        {
            // Send current state to the panel
            UpdateConnectionStatus(true);
            UpdateTerminalsList();

            // Send message history
            var history = _broker?.GetMessageHistory(100);
            if (history != null && history.Count > 0)
            {
                var messagesJson = JsonSerializer.Serialize(history.ConvertAll(m => new
                {
                    id = m.Id,
                    from = m.From,
                    to = m.To,
                    content = m.Content,
                    timestamp = m.Timestamp.ToString("HH:mm:ss"),
                    isBroadcast = m.IsBroadcast,
                    replyToId = m.ReplyToId,
                    threadId = m.ThreadId
                }), MultiTerminal.Services.JsonOptions.Unicode);

                PostMessage($"{{\"type\":\"messages\",\"messages\":{messagesJson}}}");
            }
        }

        private void OnMessageSent(object sender, ChatMessage message)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnMessageSent(sender, message)));
                return;
            }

            var messageJson = JsonSerializer.Serialize(new
            {
                id = message.Id,
                from = message.From,
                to = message.To,
                content = message.Content,
                timestamp = message.Timestamp.ToString("HH:mm:ss"),
                isBroadcast = message.IsBroadcast,
                replyToId = message.ReplyToId,
                threadId = message.ThreadId
            }, MultiTerminal.Services.JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"message\",\"message\":{messageJson}}}");
        }

        private void OnHelperMessageLogged(object sender, HelperMessage helperMessage)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnHelperMessageLogged(sender, helperMessage)));
                return;
            }

            // Push helper message to WebView with special formatting
            var messageJson = JsonSerializer.Serialize(new
            {
                id = helperMessage.Id,
                helperId = helperMessage.HelperId,
                message = helperMessage.Message,
                timestamp = helperMessage.Timestamp.ToString("HH:mm:ss"),
                isHelper = true
            }, MultiTerminal.Services.JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"helper_message_logged\",\"helperMessage\":{messageJson}}}");
        }

        private void OnTerminalChanged(object sender, TerminalInfo terminal)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTerminalChanged(sender, terminal)));
                return;
            }

            UpdateTerminalsList();
            UpdateConnectionStatus(true); // Update connection status when terminals register/disconnect
        }

        private void OnProfilesUpdated(object sender, List<TeamMemberProfile> profiles)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnProfilesUpdated(sender, profiles)));
                return;
            }

            // Refresh terminal list when profiles change (online/offline status)
            UpdateTerminalsList();
        }

        /// <summary>
        /// Update the connection status indicator.
        /// </summary>
        public void UpdateConnectionStatus(bool connected)
        {
            if (!_isInitialized)
                return;

            PostWebMessage($"status:{(connected ? "connected" : "disconnected")}");
        }

        /// <summary>
        /// Update the terminals list.
        /// </summary>
        public void UpdateTerminalsList()
        {
            if (!_isInitialized || _broker == null)
                return;

            var terminals = _broker.GetTerminals()
                .FindAll(t => !t.Name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase));
            var terminalsJson = JsonSerializer.Serialize(terminals.ConvertAll(t => new
            {
                id = t.Id,
                name = t.Name,
                color = t.Color
            }), MultiTerminal.Services.JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"terminals\",\"terminals\":{terminalsJson}}}");
        }

        /// <summary>
        /// Apply a theme to the chat panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            if (!_isInitialized)
                return;

            PostWebMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Clear all messages from the chat panel.
        /// </summary>
        public void ClearMessages()
        {
            if (!_isInitialized)
                return;

            PostMessage("{\"type\":\"clear\"}");
        }

        /// <summary>
        /// Sets the font size for the chat panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            if (!_isInitialized)
                return;

            size = Math.Max(8f, Math.Min(14f, size));
            PostWebMessage($"fontSize:{size}");
        }

        /// <summary>
        /// Toggle chat pause state and broadcast system message.
        /// </summary>
        public void ToggleChatPause()
        {
            _isPaused = !_isPaused;

            // Broadcast system message to all terminals
            var message = _isPaused ? "[SYSTEM: CHAT PAUSED]" : "[SYSTEM: CHAT RESUMED]";
            _broker?.BroadcastSystemMessage(message);

            // Update visual state in WebView
            PostMessage($"{{\"type\":\"pauseState\",\"isPaused\":{(_isPaused ? "true" : "false")}}}");
        }

        /// <summary>
        /// Gets whether chat is currently paused.
        /// </summary>
        public bool IsPaused => _isPaused;

        private void PostWebMessage(string message)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
        }

        private void PostMessage(string jsonMessage)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _isShuttingDown = true;

            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.MessageSent -= OnMessageSent;
                    _broker.TerminalRegistered -= OnTerminalChanged;
                    _broker.TerminalDisconnected -= OnTerminalChanged;
                    _broker.ProfilesUpdated -= OnProfilesUpdated;
                }

                _webView?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Event args for inject message requests.
    /// </summary>
    public class InjectMessageEventArgs : EventArgs
    {
        public string TerminalName { get; set; }
        public string Content { get; set; }
    }

    /// <summary>
    /// Event args for reply message requests.
    /// </summary>
    public class ReplyMessageEventArgs : EventArgs
    {
        public string MessageId { get; set; }
        public string SenderName { get; set; }
    }
}
