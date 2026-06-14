using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;
using MultiTerminal.Services;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based renderer for terminal status bar.
    /// Displays team member avatar, name, and current task.
    /// </summary>
    public class TerminalStatusBarRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        // Last-known payloads — NOT single-shot. Stored on every Update* call and REPLAYED on every
        // WebView 'ready'. A renderer that becomes ready after the data was pushed (or re-navigates)
        // still gets the latest state, instead of losing it from a one-shot pending slot. This is the
        // core fix for rows 2-3 (folder + stats) randomly failing to render (task d14048ef).
        private string _lastUpdateJson;
        private string _lastStatusLineJson;
        private string _lastTokenMeterJson;
        private bool? _lastRemoteMode;
        // Set true when JS confirms (acks) it actually rendered the statusline rows. Lets the poll keep
        // re-pushing until confirmed, so a raced/dropped delivery self-heals. NOT a bounded retry — it
        // converges on the ack; absent an ack it costs one idempotent 2s re-push (cheap). The ack is
        // per-band (see _lastStatusLineFolderNonEmpty): a delivery that carried a folder is only
        // considered rendered once JS reports the folder row actually un-hid, so an empty-folder
        // delivery can't falsely satisfy the loop while the folder band stays hidden (pipeline Run-1
        // HIGH, debugger + cross-model adversary).
        private volatile bool _statusLineRendered;
        // Whether the last statusline we sent carried a non-empty folder — gates the per-band ack.
        private volatile bool _lastStatusLineFolderNonEmpty;
        // Monotonic id stamped on each statusline send; the ack must echo the LATEST id to be honored,
        // so a delayed ack from an older (or failed/raced) send can't satisfy a newer one and strand it
        // (pipeline Run-3 cross-model HIGH). Written + read on the UI thread (send + ack handler).
        private int _statusLineSeq;
        private int _expectedAckSeq = -1;
        private DebugLogService _debugLogService;

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when the user clicks the Home button on the status bar.
        /// </summary>
        public event EventHandler HomeRequested;

        /// <summary>
        /// Event fired when the user clicks the Open Folder button on the status bar.
        /// The string argument is the folder path.
        /// </summary>
        public event EventHandler<string> OpenFolderRequested;

        /// <summary>
        /// Event fired when the user clicks the HUD toggle switch.
        /// </summary>
        public event EventHandler HudToggleRequested;

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// True once JS has acknowledged it un-hid the statusline rows (2-3). The status-line poll
        /// uses this to keep re-delivering until the render is confirmed (task d14048ef), so a
        /// delivery that raced the WebView's async ready doesn't leave the bands permanently hidden.
        /// Reset on each WebView 'ready' (a fresh page starts with the rows hidden again).
        /// </summary>
        public bool IsStatusLineRendered => _statusLineRendered;

        /// <summary>
        /// Sets the debug log service for logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
        }

        public TerminalStatusBarRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "TerminalStatusBarRenderer";
            Height = 140; // 3-row status bar: identity + folder + statusline
            Dock = DockStyle.Top;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

                string htmlPath = GetStatusBarHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("Status bar HTML file not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
                _isInitializing = false;
            }
        }

        private string GetStatusBarHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try Controls/TerminalStatusBar/statusbar.html
            string path = Path.Combine(assemblyDir, "Controls", "TerminalStatusBar", "statusbar.html");
            if (File.Exists(path)) return path;

            // Try TerminalStatusBar/statusbar.html
            path = Path.Combine(assemblyDir, "TerminalStatusBar", "statusbar.html");
            if (File.Exists(path)) return path;

            // Fallback
            return Path.Combine(assemblyDir, "Controls", "TerminalStatusBar", "statusbar.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Any (re)navigation invalidates the rendered page. Drop to not-initialized so the new page
            // re-enters the replay-on-ready path, and clear the rendered ack so the poll re-delivers once
            // the new page is ready. Without this, a mid-session reload/nav-failure that never posts a
            // fresh 'ready' would leave stale _isInitialized/_statusLineRendered == true and change-
            // detection would suppress recovery (pipeline Run-1: debugger HIGH-2 + adversary MEDIUM).
            _isInitialized = false;
            _statusLineRendered = false;
        }

        /// <summary>
        /// True only when the WebView's current document is EXACTLY our packaged statusbar.html. Used to
        /// gate initialization + replay of cached (potentially sensitive) payloads so a 'ready' from an
        /// unexpected document can't unlock delivery (pipeline Run-2 security MEDIUM). Uses a normalized
        /// local-path comparison — NOT a substring — so a URL that merely contains "statusbar.html" in a
        /// path/query/fragment does not pass. Fails closed on any error.
        /// </summary>
        private bool IsTrustedSource()
        {
            try
            {
                string src = _webView?.CoreWebView2?.Source;
                if (string.IsNullOrEmpty(src)) return false;
                if (!Uri.TryCreate(src, UriKind.Absolute, out var srcUri) || !srcUri.IsFile) return false;

                string expected = GetStatusBarHtmlPath();
                if (string.IsNullOrEmpty(expected)) return false;

                return string.Equals(
                    Path.GetFullPath(srcUri.LocalPath).TrimEnd('\\', '/'),
                    Path.GetFullPath(expected).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load status bar: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = JsonDocument.Parse(json);
                var root = message.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    string type = typeEl.GetString();

                    // Trust-gate EVERY inbound message: this WebView only ever loads the packaged
                    // statusbar.html. Ignore all commands (ready/home/hudToggle/openFolder/ack) from any
                    // other document — so an unexpected page can't unlock delivery, re-disclose cached
                    // data, or reach the openFolder → explorer.exe sink (pipeline Run-2/Run-3 security).
                    if (!IsTrustedSource())
                    {
                        _debugLogService?.Error("TerminalStatusBar", $"Ignoring '{type}' web message from untrusted source '{_webView?.CoreWebView2?.Source}'");
                        return;
                    }

                    if (type == "ready")
                    {
                        _debugLogService?.Trace("TerminalStatusBar", $"JS 'ready' received — replaying last-known state (statusline={(string.IsNullOrEmpty(_lastStatusLineJson) ? "none" : "present")}, tokenmeter={(string.IsNullOrEmpty(_lastTokenMeterJson) ? "none" : "present")})");
                        _isInitialized = true;
                        _isInitializing = false;
                        // Fresh page → folder/stats rows are hidden again until JS re-confirms render.
                        _statusLineRendered = false;

                        // Send theme
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");

                        // Replay the latest known payloads. Do NOT clear them — a later re-navigation
                        // (WebView reload) fires 'ready' again and must re-render from the last state.
                        if (!string.IsNullOrEmpty(_lastUpdateJson))
                            SendMessage($"update:{_lastUpdateJson}");
                        if (!string.IsNullOrEmpty(_lastStatusLineJson))
                            SendStatusLine(_lastStatusLineJson);
                        if (!string.IsNullOrEmpty(_lastTokenMeterJson))
                            SendMessage($"tokenmeter:{_lastTokenMeterJson}");
                        if (_lastRemoteMode.HasValue)
                            SendMessage($"remoteMode:{(_lastRemoteMode.Value ? "true" : "false")}");

                        Ready?.Invoke(this, EventArgs.Empty);
                    }
                    else if (type == "statuslineRendered")
                    {
                        // Honor the ack ONLY if it echoes the latest send's seq — a delayed ack from an
                        // older (or failed/raced) delivery must not satisfy a newer send and strand it
                        // (pipeline Run-3 cross-model HIGH).
                        int ackSeq = root.TryGetProperty("seq", out var sqEl) && sqEl.ValueKind == JsonValueKind.Number && sqEl.TryGetInt32(out var sv) ? sv : -1;
                        if (ackSeq != _expectedAckSeq)
                        {
                            _debugLogService?.Trace("TerminalStatusBar", $"Ignoring stale statusline ack (seq={ackSeq}, expected={_expectedAckSeq})");
                        }
                        else
                        {
                            // Per-band ack: JS reports whether the FOLDER row actually un-hid. The stats row
                            // always renders; the folder row only un-hides when a folder was supplied. So a
                            // delivery that carried a folder is only "rendered" once JS confirms folderShown —
                            // otherwise keep re-pushing (pipeline Run-1 HIGH). A delivery with no folder is
                            // rendered on the stats row alone (nothing to show; the C# fallback supplies a
                            // folder whenever the working dir is known).
                            bool folderShown = root.TryGetProperty("folderShown", out var fsEl) && fsEl.ValueKind == JsonValueKind.True;
                            _statusLineRendered = folderShown || !_lastStatusLineFolderNonEmpty;
                            _debugLogService?.Trace("TerminalStatusBar", $"JS statusline ack seq={ackSeq} (folderShown={folderShown}, sentFolder={_lastStatusLineFolderNonEmpty}) → rendered={_statusLineRendered}");
                        }
                    }
                    else if (type == "home")
                    {
                        _debugLogService?.Trace("TerminalStatusBar", "Home button clicked");
                        HomeRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (type == "hudToggle")
                    {
                        _debugLogService?.Trace("TerminalStatusBar", "HUD toggle clicked");
                        HudToggleRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (type == "openFolder")
                    {
                        if (root.TryGetProperty("path", out var pathEl))
                        {
                            string folderPath = pathEl.GetString();
                            _debugLogService?.Trace("TerminalStatusBar", $"Open folder requested: {folderPath}");
                            OpenFolderRequested?.Invoke(this, folderPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalStatusBar", $"Error processing message: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the status bar with new data.
        /// </summary>
        /// <param name="name">Terminal/member name</param>
        /// <param name="avatarUrl">Avatar URL (or null for initials)</param>
        /// <param name="activityDescription">Current activity description</param>
        /// <param name="taskTitle">Current task title</param>
        /// <param name="taskId">Current task ID</param>
        /// <param name="status">Activity status (active, idle, offline)</param>
        /// <param name="projectName">Project name (or null if no project matched)</param>
        /// <param name="projectDescription">Project description (or null)</param>
        public void UpdateStatus(string name, string avatarUrl, string activityDescription, string taskTitle, string taskId, string status, string projectName = null, string projectDescription = null)
        {
            _debugLogService?.Trace("TerminalStatusBar", $"UpdateStatus called:");
            _debugLogService?.Trace("TerminalStatusBar", $"  - name: '{name}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - avatarUrl: '{avatarUrl}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - activityDescription: '{activityDescription}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - taskTitle: '{taskTitle}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - taskId: '{taskId}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - status: '{status}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - projectName: '{projectName}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - _isInitialized: {_isInitialized}");

            var data = new
            {
                name = name ?? "Terminal",
                avatarUrl = avatarUrl ?? "",
                activityDescription = activityDescription ?? "",
                taskTitle = taskTitle ?? "",
                taskId = taskId ?? "",
                status = status ?? "idle",
                projectName = projectName ?? "",
                projectDescription = projectDescription ?? ""
            };

            string json = JsonSerializer.Serialize(data);
            _debugLogService?.Trace("TerminalStatusBar", $"Serialized JSON: {json}");

            _lastUpdateJson = json;
            if (_isInitialized)
            {
                _debugLogService?.Trace("TerminalStatusBar", "Sending update to JS (initialized)");
                SendMessage($"update:{json}");
            }
            else
            {
                _debugLogService?.Trace("TerminalStatusBar", "Stored update; will replay on ready (not initialized yet)");
            }
        }

        /// <summary>
        /// Updates the status line (rows 2 and 3) with Claude Code session data.
        /// </summary>
        public void UpdateStatusLine(string model, string folder, int? contextPct, int? quota5h = null, int? quota7d = null, int? pace5h = null, int? pace7d = null, string resetIn5h = null)
        {
            var data = new
            {
                model = model ?? "claude",
                folder = folder ?? "",
                contextPct = contextPct,
                quota5h = quota5h,
                quota7d = quota7d,
                pace5h = pace5h,
                pace7d = pace7d,
                resetIn5h = resetIn5h
            };

            string json = JsonSerializer.Serialize(data);

            _lastStatusLineJson = json;
            _lastStatusLineFolderNonEmpty = !string.IsNullOrEmpty(folder);
            if (_isInitialized)
            {
                _debugLogService?.Trace("TerminalStatusBar", $"Sending statusline to JS (folder='{folder ?? ""}', model='{model ?? ""}')");
                SendStatusLine(json);
            }
            else
            {
                _debugLogService?.Trace("TerminalStatusBar", "Stored statusline; will replay on ready (not initialized yet)");
            }
        }

        /// <summary>
        /// Posts a statusline payload with a fresh monotonic seq id and re-arms the render confirmation
        /// (_statusLineRendered = false). Used by both <see cref="UpdateStatusLine"/> (live) and the
        /// 'ready' replay so every delivery is seq-correlated: only the ack echoing this seq will mark it
        /// rendered, and each send re-arms the poll's awaitingRender retry until that ack arrives. A send
        /// whose post silently fails (SendMessage swallows) is simply never acked → the retry re-delivers.
        /// </summary>
        private void SendStatusLine(string json)
        {
            int seq = ++_statusLineSeq;
            _expectedAckSeq = seq;
            _statusLineRendered = false;
            SendMessage($"statusline:{seq}:{json}");
        }

        /// <summary>
        /// Updates the token meter on the status line (task f2702f69): cumulative session tokens,
        /// estimated cost, and burn rate. Sent as a separate message so it never disturbs the
        /// context%/quota push in <see cref="UpdateStatusLine"/>. Null fields render as empty.
        /// </summary>
        public void UpdateTokenMeter(long? tokensTotal, decimal? costUsd, bool costIsEstimate, bool costIsLowerBound, double? tokensPerMinute, long? subagentTokens, long? cacheTokens)
        {
            var data = new
            {
                tokensTotal,
                costUsd,
                costIsEstimate,
                costIsLowerBound,
                tokensPerMinute,
                subagentTokens,
                cacheTokens,
            };

            string json = JsonSerializer.Serialize(data);

            _lastTokenMeterJson = json;
            if (_isInitialized)
            {
                _debugLogService?.Trace("TerminalStatusBar", $"Sending tokenmeter to JS (tokens={tokensTotal?.ToString() ?? "null"})");
                SendMessage($"tokenmeter:{json}");
            }
        }

        /// <summary>
        /// Updates the HUD toggle visual state in the status bar.
        /// </summary>
        public void SetHudToggleState(bool isVisible)
        {
            if (_isInitialized)
            {
                SendMessage($"hudState:{(isVisible ? "true" : "false")}");
            }
        }

        /// <summary>
        /// Updates the "Input: Local/Remote" pill in the status bar. Stored as last-known and replayed
        /// on the next WebView 'ready' if called before the renderer is initialized (or after a re-navigation).
        /// </summary>
        public void SetRemoteMode(bool enabled)
        {
            _lastRemoteMode = enabled;
            if (_isInitialized)
            {
                SendMessage($"remoteMode:{(enabled ? "true" : "false")}");
            }
        }

        /// <summary>
        /// Sets the theme (dark or light).
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized)
            {
                SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
            }
        }

        private void SendMessage(string message)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalStatusBar", $"Error sending message: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                        _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                    }
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
