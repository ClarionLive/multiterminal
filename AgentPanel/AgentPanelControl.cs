using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.AgentPanel
{
    public class AgentPanelControl : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;
        private IAgentMessageSource _attachedAgent;
        private string _agentName; // stored so WebView2 "ready" handler can use it
        private string _taskDescription; // task description from Task tool call
        private string _subagentType; // subagent type (Explore, Plan, general-purpose)
        private bool _isTeamAgent; // true for team agents (can receive messages), false for one-off subagents
        private bool _webViewReady; // true after JS sends 'ready'

        public IAgentMessageSource AttachedAgent => _attachedAgent;

        /// <summary>
        /// Whether an agent is currently attached and actively running.
        /// </summary>
        public bool HasActiveAgent => _attachedAgent?.IsActive == true;

        /// <summary>
        /// Stop the attached agent process. Handles both AgentProcess and TranscriptTailer.
        /// </summary>
        public async System.Threading.Tasks.Task StopAgentAsync()
        {
            if (_attachedAgent is AgentProcess ap && ap.IsRunning)
                await ap.StopAsync();
            else if (_attachedAgent is TranscriptTailer tt && tt.IsActive)
                await tt.StopAsync();
        }

        /// <summary>
        /// Fired when the user clicks the close (X) button inside the agent panel WebView.
        /// MainForm should handle this to remove or hide the panel.
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// Fired when the attached agent process exits. Carries the exit code.
        /// </summary>
        public event EventHandler<int> AgentExited;

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

        public AgentPanelControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;
            Controls.Add(_webView);

            _initializePending = true;
            HandleCreated += async (s, e) =>
            {
                if (_initializePending && !_isInitializing && !_isInitialized)
                    await InitializeWebView2Async();
            };
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
                Debug.WriteLine($"[AgentPanel] WebView2 init failed: {ex.Message}");
                _isInitializing = false;
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Debug.WriteLine($"[AgentPanel] WebView2 init error: {e.InitializationException?.Message}");
                return;
            }

            var htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            else
                _webView.CoreWebView2.NavigateToString("<html><body style='color:white;background:#1a1a2e;font-family:sans-serif;padding:20px'><h2>agent-panel.html not found</h2><p>Expected at: " + htmlPath + "</p></body></html>");

            _isInitialized = true;
            _isInitializing = false;

            _webView.ZoomFactorChanged += (s, e) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
            if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                _webView.ZoomFactor = _pendingZoom;
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try: AgentPanel subfolder
            string path = Path.Combine(assemblyDir, "AgentPanel", "agent-panel.html");
            if (File.Exists(path)) return path;

            // Try: same folder
            path = Path.Combine(assemblyDir, "agent-panel.html");
            if (File.Exists(path)) return path;

            // Try: parent folder (development)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "AgentPanel", "agent-panel.html");
                if (File.Exists(path)) return path;
            }

            // Try: AppDomain base
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgentPanel", "agent-panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "AgentPanel", "agent-panel.html");
        }

        /// <summary>
        /// Handle messages from JavaScript (WebView2 → C#)
        /// </summary>
        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                string msgType = typeEl.GetString();

                switch (msgType)
                {
                    case "ready":
                        _webViewReady = true;
                        Debug.WriteLine($"[AgentPanel] WebView2 ready signal received, attachedAgent={((_attachedAgent != null) ? _attachedAgent.GetType().Name : "null")}, buffered={_attachedAgent?.Messages.Count ?? 0}");
                        // Apply current theme
                        PostMessage(JsonSerializer.Serialize(new { type = "theme", theme = _isDarkTheme ? "dark" : "light" }));
                        // If agent is already attached, replay messages and send status
                        if (_attachedAgent != null)
                        {
                            Debug.WriteLine($"[AgentPanel] Replaying {_attachedAgent.Messages.Count} buffered messages on WebView2 ready");
                            SendSessionInfo(_agentName);
                            SendStatusUpdate();
                            ReplayMessages();
                        }
                        break;

                    case "send_message":
                        if (root.TryGetProperty("content", out var contentEl))
                        {
                            string content = contentEl.GetString();
                            if (_attachedAgent?.IsActive == true && !string.IsNullOrWhiteSpace(content))
                            {
                                await _attachedAgent.SendMessageAsync(content);
                            }
                        }
                        break;

                    case "interrupt":
                        if (_attachedAgent?.IsActive == true)
                        {
                            await _attachedAgent.InterruptAsync();
                        }
                        break;

                    case "close_panel":
                        CloseRequested?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentPanel] Error processing web message: {ex.Message}");
            }
        }

        /// <summary>
        /// Attach to an agent message source to display its conversation.
        /// Works with both AgentProcess (piped I/O) and TranscriptTailer (file watching).
        /// </summary>
        public void AttachAgent(IAgentMessageSource agent, string agentName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            // Detach previous agent if any
            DetachAgent();

            _attachedAgent = agent;
            _agentName = agentName;
            _taskDescription = taskDescription;
            _subagentType = subagentType;
            _isTeamAgent = isTeamAgent;

            Debug.WriteLine($"[AgentPanel] AttachAgent called: name='{agentName}', agentType={agent.GetType().Name}, webViewReady={_webViewReady}, bufferedMessages={agent.Messages.Count}");

            // Subscribe to agent events
            _attachedAgent.MessageReceived += OnAgentMessageReceived;
            _attachedAgent.Stopped += OnAgentProcessExited;

            if (_webViewReady)
            {
                Debug.WriteLine($"[AgentPanel] WebView2 already ready - replaying {agent.Messages.Count} messages now");
                SendSessionInfo(_agentName);
                SendStatusUpdate();
                ReplayMessages();
            }
            else
            {
                Debug.WriteLine($"[AgentPanel] WebView2 NOT ready yet - messages will replay when ready");
            }
        }

        /// <summary>
        /// Detach from the current agent.
        /// </summary>
        public void DetachAgent()
        {
            if (_attachedAgent != null)
            {
                _attachedAgent.MessageReceived -= OnAgentMessageReceived;
                _attachedAgent.Stopped -= OnAgentProcessExited;
                _attachedAgent = null;
                _agentName = null;
                _taskDescription = null;
                _subagentType = null;
                _isTeamAgent = false;
            }

            if (_webViewReady)
            {
                PostMessage(JsonSerializer.Serialize(new { type = "clear" }));
                PostMessage(JsonSerializer.Serialize(new { type = "status_update", status = "idle" }));
                PostMessage(JsonSerializer.Serialize(new { type = "session_info", agentName = "Agent", sessionId = (string)null }));
            }
        }

        /// <summary>
        /// Handle live messages from the attached agent.
        /// </summary>
        private void OnAgentMessageReceived(object sender, AgentMessage msg)
        {
            if (!_webViewReady) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnAgentMessageReceived(sender, msg))); }
                catch (ObjectDisposedException) { }
                return;
            }

            SendAgentMessage(msg);

            // Update status based on message type
            if (msg.Type == AgentMessageType.Result)
            {
                PostMessage(JsonSerializer.Serialize(new { type = "status_update", status = "idle" }));
            }
            else if (msg.Type == AgentMessageType.Assistant || msg.Type == AgentMessageType.StreamDelta || msg.Type == AgentMessageType.ToolUse)
            {
                PostMessage(JsonSerializer.Serialize(new { type = "status_update", status = "running" }));
            }
        }

        /// <summary>
        /// Handle agent process exit.
        /// </summary>
        private void OnAgentProcessExited(object sender, int exitCode)
        {
            if (!_webViewReady) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnAgentProcessExited(sender, exitCode))); }
                catch (ObjectDisposedException) { }
                return;
            }

            string status = exitCode == 0 ? "completed" : "error";
            PostMessage(JsonSerializer.Serialize(new { type = "status_update", status }));

            AgentExited?.Invoke(this, exitCode);
        }

        /// <summary>
        /// Send a single AgentMessage to the JavaScript panel.
        /// </summary>
        private void SendAgentMessage(AgentMessage msg)
        {
            var payload = new
            {
                type = "agent_message",
                message = new
                {
                    type = msg.Type.ToString(),
                    content = msg.Content ?? "",
                    toolName = msg.ToolName,
                    timestamp = msg.Timestamp.ToString("o"),
                    sessionId = msg.SessionId
                }
            };
            PostMessage(JsonSerializer.Serialize(payload));
        }

        /// <summary>
        /// Replay all buffered messages from the attached agent.
        /// </summary>
        private void ReplayMessages()
        {
            if (_attachedAgent == null) { Debug.WriteLine("[AgentPanel] ReplayMessages: no attached agent"); return; }

            var messages = _attachedAgent.Messages;
            Debug.WriteLine($"[AgentPanel] ReplayMessages: {messages.Count} messages to replay");
            if (messages.Count == 0) return;

            var payload = new
            {
                type = "replay_messages",
                messages = messages.Select(m => new
                {
                    type = m.Type.ToString(),
                    content = m.Content ?? "",
                    toolName = m.ToolName,
                    timestamp = m.Timestamp.ToString("o"),
                    sessionId = m.SessionId
                }).ToArray()
            };
            PostMessage(JsonSerializer.Serialize(payload));
        }

        /// <summary>
        /// Send session info to JavaScript.
        /// </summary>
        private void SendSessionInfo(string agentName = null)
        {
            var payload = new
            {
                type = "session_info",
                agentName = agentName ?? "Agent",
                sessionId = _attachedAgent?.SessionId,
                taskDescription = _taskDescription ?? "",
                subagentType = _subagentType ?? "",
                isTeamAgent = _isTeamAgent
            };
            PostMessage(JsonSerializer.Serialize(payload));
        }

        /// <summary>
        /// Send current status to JavaScript.
        /// </summary>
        private void SendStatusUpdate()
        {
            string status = "idle";
            if (_attachedAgent != null)
            {
                status = _attachedAgent.IsActive ? "running" : "completed";
            }
            PostMessage(JsonSerializer.Serialize(new { type = "status_update", status }));
        }

        /// <summary>
        /// Update the status label displayed in the panel (e.g., "disconnected").
        /// </summary>
        public void SetStatusLabel(string status)
        {
            PostMessage(JsonSerializer.Serialize(new { type = "status_update", status }));
        }

        /// <summary>
        /// Post a JSON message to the WebView2 JavaScript.
        /// </summary>
        private void PostMessage(string jsonMessage)
        {
            if (_webView?.CoreWebView2 != null)
            {
                try
                {
                    _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentPanel] PostMessage error: {ex.Message}");
                }
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_webViewReady)
            {
                PostMessage(JsonSerializer.Serialize(new { type = "theme", theme = isDark ? "dark" : "light" }));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachAgent();
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
