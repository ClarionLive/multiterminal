using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Standalone popup window that hosts the extracted Code Review editor
    /// (Dialogs/code-review.html). One instance per taskId — coordinated by
    /// <see cref="CodeReviewPopupManager"/>. Communicates with C# in-proc via
    /// <see cref="CodeReviewService"/> (no HTTP).
    /// </summary>
    public sealed class CodeReviewPopupForm : Form
    {
        private const string SettingsKeyX = "codeReviewPopup.X";
        private const string SettingsKeyY = "codeReviewPopup.Y";
        private const string SettingsKeyW = "codeReviewPopup.W";
        private const string SettingsKeyH = "codeReviewPopup.H";
        private const string SettingsKeyZoom = "codeReviewPopup.zoom";
        private const int DefaultWidth = 1100;
        private const int DefaultHeight = 750;

        private readonly string _taskId;
        private readonly string _taskTitle;
        private readonly string _initialFilePath;
        private readonly WebView2 _webView;
        private MessageBroker _broker;
        private CodeReviewService _crService;
        private bool _isDark;
        private bool _initialized;
        private bool _jsReady;
        private bool _initSent;
        private string _pendingPreselectFilePath;
        private EventHandler<CoreWebView2WebMessageReceivedEventArgs> _webMsgHandler;

        public string TaskId => _taskId;

        public CodeReviewPopupForm(string taskId, string taskTitle, string initialFilePath)
        {
            _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _taskTitle = taskTitle ?? string.Empty;
            _initialFilePath = initialFilePath;

            Text = $"Code Review: {(_taskTitle.Length > 0 ? _taskTitle : _taskId)}";
            StartPosition = FormStartPosition.CenterParent;
            Width = DefaultWidth;
            Height = DefaultHeight;
            BackColor = Color.FromArgb(26, 26, 46);

            ApplyPersistedBounds();

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            FormClosed += OnFormClosed;
        }

        /// <summary>
        /// Wire dependencies + bring up the WebView2. Must be awaited before
        /// the popup is usable. Manager calls this once per instance.
        /// </summary>
        public async Task Initialize(MessageBroker broker, CodeReviewService crService, bool isDarkTheme)
        {
            if (_initialized) return;
            _initialized = true;

            _broker = broker;
            _crService = crService;
            _isDark = isDarkTheme;
            BackColor = _isDark
                ? Color.FromArgb(26, 26, 46)
                : Color.FromArgb(240, 240, 240);

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] EnsureCoreWebView2Async failed: {ex.Message}");
                return;
            }

            _webView.DefaultBackgroundColor = _isDark
                ? Color.FromArgb(26, 26, 46)
                : Color.FromArgb(240, 240, 240);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            // F-R2-3 defense-in-depth: deny window.open / target=_blank attempts
            // from the asset. The Code Review popup is a single-purpose surface;
            // a compromised or maliciously-crafted code-review.html should not be
            // able to spawn a second WebView2 window with no security context.
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            double persistedZoom = LoadPersistedZoom();
            if (Math.Abs(persistedZoom - 1.0) > 0.01)
                _webView.ZoomFactor = persistedZoom;

            _webMsgHandler = OnWebMessageReceived;
            _webView.CoreWebView2.WebMessageReceived += _webMsgHandler;

            string htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
            {
                var uri = new Uri(htmlPath).AbsoluteUri;
                _webView.CoreWebView2.Navigate(uri);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] code-review.html not found at {htmlPath}");
                _webView.CoreWebView2.NavigateToString(BuildMissingAssetFallback(htmlPath));
            }
        }

        private static string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string p = Path.Combine(assemblyDir, "Dialogs", "code-review.html");
                if (File.Exists(p)) return p;

                p = Path.Combine(assemblyDir, "code-review.html");
                if (File.Exists(p)) return p;

                string parentDir = Path.GetDirectoryName(assemblyDir);
                if (parentDir != null)
                {
                    p = Path.Combine(parentDir, "Dialogs", "code-review.html");
                    if (File.Exists(p)) return p;
                }
            }

            string baseDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dialogs", "code-review.html");
            if (File.Exists(baseDirPath)) return baseDirPath;

            return Path.Combine(assemblyDir ?? AppDomain.CurrentDomain.BaseDirectory,
                "Dialogs", "code-review.html");
        }

        private static string BuildMissingAssetFallback(string searchedPath)
        {
            string esc = System.Net.WebUtility.HtmlEncode(searchedPath ?? string.Empty);
            return "<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI;color:#eee;background:#1a1a2e;padding:24px\">" +
                "<h2>Code Review asset not found</h2>" +
                "<p>Expected at: <code>" + esc + "</code></p>" +
                "<p>Asset is produced by task d29512ef item [0] (Alice).</p>" +
                "<script>setTimeout(function(){try{window.chrome.webview.postMessage({type:'ready'});}catch(e){}},50);</script>" +
                "</body></html>";
        }

        /// <summary>
        /// Called by the manager when OpenOrFocus is invoked for an already-open
        /// popup with a (possibly new) filePath. Activates the window and tells
        /// the JS to switch to that file's tab. If the JS hasn't fired "ready"
        /// yet the preselect is buffered until it does.
        /// </summary>
        public void PreselectFile(string filePath)
        {
            try { Activate(); } catch { }
            if (string.IsNullOrEmpty(filePath)) return;

            if (!_jsReady)
            {
                _pendingPreselectFilePath = filePath;
                return;
            }

            _ = PostToWebViewAsync(new
            {
                type = "preselect_file",
                filePath
            });
        }

        /// <summary>
        /// Update theme without re-navigating — fires "theme_changed" so the
        /// page's JS updates CSS variables on :root in place. Preserves any
        /// in-progress comment state.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            BackColor = _isDark
                ? Color.FromArgb(26, 26, 46)
                : Color.FromArgb(240, 240, 240);
            if (_webView?.CoreWebView2 != null)
            {
                _webView.DefaultBackgroundColor = _isDark
                    ? Color.FromArgb(26, 26, 46)
                    : Color.FromArgb(240, 240, 240);
            }
            if (!_jsReady) return;
            _ = PostToWebViewAsync(new
            {
                type = "theme_changed",
                theme = _isDark ? "dark" : "light"
            });
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = args.WebMessageAsJson;
                if (string.IsNullOrEmpty(json)) return;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                string type = typeEl.GetString();
                switch (type)
                {
                    case "ready":
                        _jsReady = true;
                        SendInitMessage();
                        if (!string.IsNullOrEmpty(_pendingPreselectFilePath))
                        {
                            var pending = _pendingPreselectFilePath;
                            _pendingPreselectFilePath = null;
                            _ = PostToWebViewAsync(new
                            {
                                type = "preselect_file",
                                filePath = pending
                            });
                        }
                        break;
                    case "fetch_data":
                        // F-R2-3: bind to the form's own taskId — ignore any
                        // inbound taskId in the message so a compromised JS
                        // payload can't redirect this form's fetches to a
                        // different task. The form is per-taskId by construction.
                        _ = HandleFetchDataAsync(_taskId);
                        break;
                    case "code_review_verdict":
                        if (root.TryGetProperty("verdict", out var crVerdictEl))
                        {
                            string reviewNotesJson = null;
                            if (root.TryGetProperty("reviewNotes", out var rnEl) &&
                                rnEl.ValueKind == JsonValueKind.Array)
                            {
                                reviewNotesJson = rnEl.GetRawText();
                            }
                            // F-R2-3: bind verdict to the form's own taskId. Inbound
                            // taskId on the message is intentionally ignored.
                            HandleVerdict(_taskId, crVerdictEl.GetString(), reviewNotesJson);
                        }
                        break;
                    case "close":
                        BeginInvoke((Action)Close);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] WebMessage parse failed: {ex.Message}");
            }
        }

        private static void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            try
            {
                args.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] OnNewWindowRequested failed: {ex.Message}");
            }
        }

        private void SendInitMessage()
        {
            if (_initSent) return;
            _initSent = true;
            _ = PostToWebViewAsync(new
            {
                type = "init",
                taskId = _taskId,
                taskTitle = _taskTitle,
                filePath = _initialFilePath,
                theme = _isDark ? "dark" : "light"
            });
        }

        private async Task HandleFetchDataAsync(string taskId)
        {
            string filesJson = null;
            string agentReportJson = null;
            try
            {
                if (_crService != null)
                {
                    var data = await _crService.GetCodeReviewData(taskId);
                    if (data != null)
                    {
                        filesJson = data.FilesJson;
                        agentReportJson = data.AgentReportJson;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] GetCodeReviewData failed: {ex.Message}");
            }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"data\",\"payload\":{\"files\":");
            sb.Append(string.IsNullOrWhiteSpace(filesJson) ? "null" : filesJson);
            sb.Append(",\"agentReport\":");
            sb.Append(string.IsNullOrWhiteSpace(agentReportJson) ? "null" : agentReportJson);
            sb.Append("}}");
            await PostRawJsonToWebViewAsync(sb.ToString());
        }

        private void HandleVerdict(string taskId, string verdict, string reviewNotesJson)
        {
            bool ok = false;
            string error = null;
            try
            {
                if (_crService == null)
                {
                    error = "CodeReviewService not wired";
                }
                else
                {
                    var result = _crService.HandleVerdict(taskId, verdict, reviewNotesJson);
                    ok = result?.Ok == true;
                    error = result?.Error;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"submit_result\",\"ok\":");
            sb.Append(ok ? "true" : "false");
            if (!string.IsNullOrEmpty(error))
            {
                sb.Append(",\"error\":");
                sb.Append(JsonSerializer.Serialize(error));
            }
            sb.Append('}');
            _ = PostRawJsonToWebViewAsync(sb.ToString());
        }

        private Task PostToWebViewAsync(object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload);
                return PostRawJsonToWebViewAsync(json);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        private async Task PostRawJsonToWebViewAsync(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() =>
                    {
                        try { _webView?.CoreWebView2?.PostWebMessageAsJson(json); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CodeReviewPopupForm] PostWebMessageAsJson failed: {ex.Message}");
                        }
                    }));
                }
                else
                {
                    _webView?.CoreWebView2?.PostWebMessageAsJson(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] PostWebMessageAsJson failed: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private void ApplyPersistedBounds()
        {
            try
            {
                var s = SettingsService.Default;
                string x = s.Get(SettingsKeyX);
                string y = s.Get(SettingsKeyY);
                string w = s.Get(SettingsKeyW);
                string h = s.Get(SettingsKeyH);
                if (int.TryParse(x, out int ix) &&
                    int.TryParse(y, out int iy) &&
                    int.TryParse(w, out int iw) &&
                    int.TryParse(h, out int ih) &&
                    iw > 200 && ih > 200)
                {
                    var bounds = new Rectangle(ix, iy, iw, ih);
                    bool onScreen = false;
                    foreach (var screen in Screen.AllScreens)
                    {
                        if (screen.WorkingArea.IntersectsWith(bounds))
                        {
                            onScreen = true;
                            break;
                        }
                    }
                    if (onScreen)
                    {
                        StartPosition = FormStartPosition.Manual;
                        Location = new Point(ix, iy);
                        Width = iw;
                        Height = ih;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] ApplyPersistedBounds failed: {ex.Message}");
            }
        }

        private static double LoadPersistedZoom()
        {
            try
            {
                string z = SettingsService.Default.Get(SettingsKeyZoom);
                if (double.TryParse(z, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double zoom) &&
                    zoom >= 0.25 && zoom <= 5.0)
                {
                    return zoom;
                }
            }
            catch { }
            return 1.0;
        }

        private void SaveBoundsAndZoom()
        {
            try
            {
                var s = SettingsService.Default;
                s.BeginBatch();
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                s.Set(SettingsKeyX, b.X.ToString());
                s.Set(SettingsKeyY, b.Y.ToString());
                s.Set(SettingsKeyW, b.Width.ToString());
                s.Set(SettingsKeyH, b.Height.ToString());
                if (_webView?.CoreWebView2 != null)
                {
                    s.Set(SettingsKeyZoom,
                        _webView.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                s.EndBatch();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupForm] SaveBoundsAndZoom failed: {ex.Message}");
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            SaveBoundsAndZoom();
            try
            {
                if (_webView?.CoreWebView2 != null)
                {
                    if (_webMsgHandler != null)
                        _webView.CoreWebView2.WebMessageReceived -= _webMsgHandler;
                    _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _webView?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
