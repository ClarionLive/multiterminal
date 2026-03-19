using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2 wrapper for browser tabs inside HudTabContainer.
    /// Supports URL navigation and raw HTML content with lazy WebView2 initialization.
    /// </summary>
    public class BrowserTabPage : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        // Queued navigation for before WebView2 is ready
        private string _pendingUrl;
        private string _pendingHtml;

        /// <summary>
        /// Unique identifier for this tab.
        /// </summary>
        public string TabId { get; }

        /// <summary>
        /// Display title for the tab strip.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Raised when Title changes from a page navigation.
        /// </summary>
        public event EventHandler TitleChanged;

        public BrowserTabPage(string tabId, string title)
        {
            TabId = tabId ?? throw new ArgumentNullException(nameof(tabId));
            Title = title ?? tabId;

            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 30);
            Name = "BrowserTabPage_" + tabId;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView_" + tabId
            };
            Controls.Add(_webView);
            ResumeLayout(false);

            // Lazy init: start WebView2 when first made visible
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
                    ? Color.FromArgb(26, 26, 46)
                    : Color.FromArgb(245, 245, 245);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Inject console capture script after each navigation
                _webView.CoreWebView2.DOMContentLoaded += OnDomContentLoaded;

                _isInitialized = true;
                _isInitializing = false;

                // Apply pending zoom
                if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                    _webView.ZoomFactor = _pendingZoom;

                // Flush queued navigation
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    _webView.CoreWebView2.Navigate(_pendingUrl);
                    _pendingUrl = null;
                    _pendingHtml = null;
                }
                else if (!string.IsNullOrEmpty(_pendingHtml))
                {
                    _webView.CoreWebView2.NavigateToString(_pendingHtml);
                    _pendingHtml = null;
                }
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess && _webView?.CoreWebView2 != null)
            {
                // Update title from page title if we don't have a custom one
                var pageTitle = _webView.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrEmpty(pageTitle) && Title == TabId)
                {
                    Title = pageTitle.Length > 30 ? pageTitle.Substring(0, 27) + "..." : pageTitle;
                    TitleChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Navigate to a URL.
        /// </summary>
        public void NavigateToUrl(string url)
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Navigate(url);
            }
            else
            {
                _pendingUrl = url;
                _pendingHtml = null;
            }
        }

        /// <summary>
        /// Load raw HTML content.
        /// </summary>
        public void LoadHtmlContent(string html)
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NavigateToString(html);
            }
            else
            {
                _pendingHtml = html;
                _pendingUrl = null;
            }
        }

        /// <summary>
        /// Apply theme (sets WebView2 background color).
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            if (_webView != null)
            {
                _webView.DefaultBackgroundColor = isDark
                    ? Color.FromArgb(26, 26, 46)
                    : Color.FromArgb(245, 245, 245);
            }
        }

        /// <summary>
        /// Set the zoom factor for the WebView2 control.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
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

        // -----------------------------------------------------------------
        // PostMessage bridge (bidirectional page <-> host communication)
        // -----------------------------------------------------------------

        private readonly List<WebMessageEntry> _receivedMessages = new List<WebMessageEntry>();
        private readonly object _messageLock = new object();

        /// <summary>
        /// Raised when the page sends a message via window.chrome.webview.postMessage().
        /// </summary>
        public event EventHandler<WebMessageEntry> WebMessageReceived;

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var rawJson = e.WebMessageAsJson ?? e.TryGetWebMessageAsString();

            // Intercept internal async script callbacks — don't buffer these
            if (TryHandleAsyncScriptCallback(rawJson))
                return;

            var entry = new WebMessageEntry
            {
                Timestamp = DateTime.UtcNow,
                Data = rawJson
            };

            lock (_messageLock)
            {
                _receivedMessages.Add(entry);
                if (_receivedMessages.Count > 1000)
                    _receivedMessages.RemoveRange(0, _receivedMessages.Count - 1000);
            }

            WebMessageReceived?.Invoke(this, entry);
        }

        /// <summary>
        /// Gets buffered messages sent from the page via postMessage.
        /// </summary>
        public List<WebMessageEntry> GetReceivedMessages(int? limit)
        {
            lock (_messageLock)
            {
                if (limit.HasValue && limit.Value > 0 && limit.Value < _receivedMessages.Count)
                    return _receivedMessages.GetRange(_receivedMessages.Count - limit.Value, limit.Value).ToList();
                return _receivedMessages.ToList();
            }
        }

        /// <summary>
        /// Clears the received message buffer.
        /// </summary>
        public void ClearReceivedMessages()
        {
            lock (_messageLock)
            {
                _receivedMessages.Clear();
            }
        }

        /// <summary>
        /// Sends a JSON message to the page. Page receives via:
        /// window.chrome.webview.addEventListener('message', e => { e.data ... })
        /// </summary>
        public void PostMessageToPage(string jsonData)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
                return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(jsonData);
            }
            catch { }
        }

        // -----------------------------------------------------------------
        // Console log capture (JS-based, works across all WebView2 versions)
        // -----------------------------------------------------------------

        private const string ConsoleCaptureScipt = @"
            if (!window.__mtConsoleCaptured) {
                window.__mtConsoleCaptured = true;
                window.__mtConsoleLogs = [];
                ['log','warn','error','info'].forEach(function(level) {
                    var orig = console[level];
                    console[level] = function() {
                        var args = Array.prototype.slice.call(arguments);
                        var msg = args.map(function(a) {
                            try { return typeof a === 'object' ? JSON.stringify(a) : String(a); }
                            catch(e) { return String(a); }
                        }).join(' ');
                        window.__mtConsoleLogs.push({t: new Date().toISOString(), l: level, m: msg});
                        if (window.__mtConsoleLogs.length > 1000) window.__mtConsoleLogs.splice(0, window.__mtConsoleLogs.length - 1000);
                        orig.apply(console, arguments);
                    };
                });
            }";

        /// <summary>
        /// WebMCP polyfill: implements navigator.modelContext per W3C Web Model Context Protocol spec.
        /// Allows pages to register structured tools that agents can discover and invoke via MCP.
        /// Gracefully no-ops if navigator.modelContext already exists (future native browser support).
        /// </summary>
        private const string WebMcpPolyfillScript = @"
            if (!navigator.modelContext) {
                (function() {
                    var registry = new Map();

                    function notifyToolsChanged() {
                        var tools = [];
                        registry.forEach(function(t) {
                            tools.push({
                                name: t.name,
                                description: t.description,
                                inputSchema: t.inputSchema || null,
                                annotations: t.annotations || null
                            });
                        });
                        try {
                            window.chrome.webview.postMessage({
                                type: 'webmcp:tools_changed',
                                tools: tools
                            });
                        } catch(e) {}
                    }

                    navigator.modelContext = {
                        registerTool: function(tool) {
                            if (!tool || !tool.name || typeof tool.name !== 'string' || tool.name.length === 0)
                                throw new TypeError('Tool name must be a non-empty string');
                            if (!tool.description || typeof tool.description !== 'string' || tool.description.length === 0)
                                throw new TypeError('Tool description must be a non-empty string');
                            if (registry.has(tool.name))
                                throw new DOMException('Tool already registered: ' + tool.name, 'InvalidStateError');
                            if (typeof tool.execute !== 'function')
                                throw new TypeError('Tool execute must be a function');
                            registry.set(tool.name, {
                                name: tool.name,
                                description: tool.description,
                                inputSchema: tool.inputSchema || null,
                                execute: tool.execute,
                                annotations: tool.annotations || null
                            });
                            notifyToolsChanged();
                        },
                        unregisterTool: function(name) {
                            registry.delete(name);
                            notifyToolsChanged();
                        },
                        provideContext: function(tools) {
                            registry.clear();
                            if (Array.isArray(tools)) {
                                tools.forEach(function(t) { navigator.modelContext.registerTool(t); });
                            }
                        },
                        clearContext: function() {
                            registry.clear();
                            notifyToolsChanged();
                        }
                    };

                    // Expose helper functions for agent invocation via execute_browser_script
                    window.__webmcpRegistry = registry;

                    window.__webmcpListTools = function() {
                        var tools = [];
                        registry.forEach(function(t) {
                            tools.push({
                                name: t.name,
                                description: t.description,
                                inputSchema: t.inputSchema || null,
                                annotations: t.annotations || null
                            });
                        });
                        return JSON.stringify(tools);
                    };

                    window.__webmcpInvoke = function(name, input) {
                        var tool = registry.get(name);
                        if (!tool) return Promise.reject(new Error('WebMCP tool not found: ' + name));
                        try {
                            var result = tool.execute(input || {}, {});
                            if (result && typeof result.then === 'function') {
                                return result.then(function(r) { return JSON.stringify(r); });
                            }
                            return Promise.resolve(JSON.stringify(result));
                        } catch(e) {
                            return Promise.reject(e);
                        }
                    };

                    console.log('[WebMCP] Polyfill loaded — navigator.modelContext ready');
                })();
            }";

        private void OnDomContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            _ = InjectConsoleCapture();
        }

        private async System.Threading.Tasks.Task InjectConsoleCapture()
        {
            try
            {
                if (_isInitialized && _webView?.CoreWebView2 != null)
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync(ConsoleCaptureScipt);
                    await _webView.CoreWebView2.ExecuteScriptAsync(WebMcpPolyfillScript);
                }
            }
            catch { /* page may have navigated away */ }
        }

        /// <summary>
        /// Returns captured console log entries from the page's JS buffer.
        /// </summary>
        public async Task<List<ConsoleLogEntry>> GetConsoleLogsAsync(int? limit)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
                return new List<ConsoleLogEntry>();

            try
            {
                var limitClause = limit.HasValue && limit.Value > 0
                    ? $".slice(-{limit.Value})"
                    : "";
                var json = await _webView.CoreWebView2.ExecuteScriptAsync(
                    $"JSON.stringify((window.__mtConsoleLogs || []){limitClause})");

                // ExecuteScriptAsync returns a JSON-encoded string, so it's double-quoted
                if (string.IsNullOrEmpty(json) || json == "null")
                    return new List<ConsoleLogEntry>();

                // Unescape the outer JSON string wrapper
                var inner = System.Text.Json.JsonSerializer.Deserialize<string>(json);
                if (string.IsNullOrEmpty(inner))
                    return new List<ConsoleLogEntry>();

                var entries = System.Text.Json.JsonSerializer.Deserialize<List<JsConsoleEntry>>(inner);
                if (entries == null)
                    return new List<ConsoleLogEntry>();

                return entries.Select(e => new ConsoleLogEntry
                {
                    Timestamp = DateTime.TryParse(e.t, out var dt) ? dt : DateTime.UtcNow,
                    Level = e.l ?? "log",
                    Message = e.m ?? ""
                }).ToList();
            }
            catch
            {
                return new List<ConsoleLogEntry>();
            }
        }

        /// <summary>
        /// Clears captured console log entries in the page.
        /// </summary>
        public async System.Threading.Tasks.Task ClearConsoleLogsAsync()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                try
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync("window.__mtConsoleLogs = []");
                }
                catch { }
            }
        }

        // -----------------------------------------------------------------
        // Script execution (uses ExecuteScriptWithResultAsync for better errors)
        // -----------------------------------------------------------------

        /// <summary>
        /// Executes a JavaScript snippet in the WebView2 and returns the JSON-serialized result.
        /// Uses ExecuteScriptWithResultAsync for proper error/exception details.
        /// </summary>
        // Pending async script callbacks keyed by callback ID
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _asyncScriptCallbacks = new();

        public async Task<string> ExecuteScriptAsync(string script)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
                return "{\"error\": \"WebView2 is not initialized\"}";

            try
            {
                // Wrap the script to handle Promise results via postMessage callback
                var callbackId = "__execCb_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                var tcs = new TaskCompletionSource<string>();
                _asyncScriptCallbacks[callbackId] = tcs;

                var wrappedScript = $@"
(function() {{
    var __cbId = '{callbackId}';
    try {{
        var __r = (function() {{ return {script} }})();
        if (__r && typeof __r === 'object' && typeof __r.then === 'function') {{
            __r.then(function(v) {{
                window.chrome.webview.postMessage({{ type: '__execCallback', id: __cbId, result: JSON.stringify(v) }});
            }}).catch(function(e) {{
                window.chrome.webview.postMessage({{ type: '__execCallback', id: __cbId, error: e.message || String(e) }});
            }});
            return '__async_pending';
        }} else {{
            window.chrome.webview.postMessage({{ type: '__execCallback', id: __cbId, result: JSON.stringify(__r) }});
            return '__sync_delegated';
        }}
    }} catch(e) {{
        window.chrome.webview.postMessage({{ type: '__execCallback', id: __cbId, error: e.message || String(e) }});
        return '__error_delegated';
    }}
}})()";

                // Fire the script (don't need the direct return value — result comes via postMessage)
                await _webView.CoreWebView2.ExecuteScriptAsync(wrappedScript);

                // Wait for the callback with a timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                cts.Token.Register(() => tcs.TrySetResult("{\"error\": \"Script execution timed out (15s)\"}"));

                return await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return $"{{\"error\": \"{ex.Message.Replace("\"", "\\\"").Replace("\n", "\\n")}\"}}";
            }
        }

        /// <summary>
        /// Handles internal async script execution callbacks. Called from OnWebMessageReceived.
        /// Returns true if the message was an internal callback (and should not be buffered).
        /// </summary>
        private bool TryHandleAsyncScriptCallback(string json)
        {
            try
            {
                // Quick check before parsing
                if (json == null || !json.Contains("__execCallback"))
                    return false;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "__execCallback")
                    return false;

                if (!root.TryGetProperty("id", out var idProp))
                    return false;

                var id = idProp.GetString();
                if (id == null || !_asyncScriptCallbacks.TryRemove(id, out var tcs))
                    return false;

                if (root.TryGetProperty("error", out var errorProp))
                {
                    var errMsg = (errorProp.GetString() ?? "Unknown error").Replace("\"", "\\\"").Replace("\n", "\\n");
                    tcs.TrySetResult($"{{\"error\": \"{errMsg}\"}}");
                }
                else if (root.TryGetProperty("result", out var resultProp))
                {
                    tcs.TrySetResult(resultProp.GetString() ?? "null");
                }
                else
                {
                    tcs.TrySetResult("null");
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // -----------------------------------------------------------------
        // Screenshot capture
        // -----------------------------------------------------------------

        /// <summary>
        /// Captures a PNG screenshot of the current browser tab content.
        /// Returns the image as a byte array.
        /// </summary>
        public async Task<byte[]> CaptureScreenshotAsync()
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
                return null;

            try
            {
                using (var stream = new System.IO.MemoryStream())
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, stream);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets content from a DOM element by CSS selector.
        /// </summary>
        /// <param name="selector">CSS selector for the element.</param>
        /// <param name="property">Property to read: textContent, innerHTML, outerHTML, or value. Defaults to textContent.</param>
        public async Task<string> GetElementContentAsync(string selector, string property)
        {
            if (string.IsNullOrEmpty(property))
                property = "textContent";

            // Escape the selector for embedding in JS string
            var escapedSelector = selector.Replace("\\", "\\\\").Replace("'", "\\'");
            var escapedProperty = property.Replace("\\", "\\\\").Replace("'", "\\'");

            var script = $@"(function() {{
    var el = document.querySelector('{escapedSelector}');
    if (!el) return JSON.stringify({{error: 'Element not found: {escapedSelector}'}});
    return JSON.stringify({{value: el['{escapedProperty}'] || ''}});
}})()";

            return await ExecuteScriptAsync(script);
        }

        // -----------------------------------------------------------------
        // Dispose
        // -----------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                        _webView.CoreWebView2.DOMContentLoaded -= OnDomContentLoaded;
                    }
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Represents a single console log entry captured from WebView2.
    /// </summary>
    public class ConsoleLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } // "log", "warn", "error", "info"
        public string Message { get; set; }
    }

    /// <summary>
    /// Internal class for deserializing JS console capture entries.
    /// </summary>
    internal class JsConsoleEntry
    {
        public string t { get; set; }
        public string l { get; set; }
        public string m { get; set; }
    }

    /// <summary>
    /// Represents a message received from a page via window.chrome.webview.postMessage().
    /// </summary>
    public class WebMessageEntry
    {
        public DateTime Timestamp { get; set; }
        public string Data { get; set; }
    }
}
