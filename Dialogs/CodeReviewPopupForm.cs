using System;
using System.Collections.Generic;
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
    /// (Dialogs/code-review.html). One instance per taskId (or per
    /// working-tree key for the ad-hoc Phase 3b path) — coordinated by
    /// <see cref="CodeReviewPopupManager"/>. Communicates with C# in-proc via
    /// <see cref="CodeReviewService"/> (no HTTP).
    ///
    /// Two modes:
    ///   - Task mode: bound to a kanban task, fetches diffs via
    ///     <see cref="CodeReviewService.GetCodeReviewData(string)"/>, supports
    ///     Pass/Submit-Notes verdict flow.
    ///   - Working-tree mode (Phase 3b): bound to a (repoRoot, filePaths[])
    ///     pair, fetches via
    ///     <see cref="CodeReviewService.GetCodeReviewData(string, IList{string})"/>,
    ///     read-only — Pass and Submit-Notes are hidden in the HTML because
    ///     there is no task to transition. Phase 3c replaces them with a
    ///     "Wrap in quick task" affordance.
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

        // Task-mode fields. Null/empty when in working-tree mode.
        private readonly string _taskId;
        private readonly string _taskTitle;
        // Working-tree mode fields. Null when in task mode.
        private readonly string _workingTreeKey;
        private readonly string _repoRoot;
        private readonly IList<string> _filePaths;
        private readonly bool _isWorkingTreeMode;

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

        /// <summary>Task ID this popup is bound to, or null in working-tree mode.</summary>
        public string TaskId => _taskId;

        /// <summary>Working-tree key this popup is bound to, or null in task mode.</summary>
        public string WorkingTreeKey => _workingTreeKey;

        /// <summary>True when the popup was constructed in working-tree mode (no task).</summary>
        public bool IsWorkingTreeMode => _isWorkingTreeMode;

        /// <summary>
        /// Fires after a successful "Wrap in quick task" round-trip in
        /// working-tree mode. <see cref="CodeReviewPopupManager"/> bridges this
        /// to a caller-supplied callback so the host panel (HUD Git) can
        /// auto-refresh its tree and move the wrapped files out of the
        /// "Needs a quick task" bucket without the user clicking Refresh.
        /// The string arg is the newly-created task ID (may be null if the
        /// wrap reported success without one — defensive, shouldn't happen).
        /// </summary>
        public event EventHandler<string> QuickTaskCreated;

        public CodeReviewPopupForm(string taskId, string taskTitle, string initialFilePath)
        {
            _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _taskTitle = taskTitle ?? string.Empty;
            _initialFilePath = initialFilePath;
            _isWorkingTreeMode = false;

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
        /// Working-tree-mode constructor (Phase 3b). No task is associated;
        /// diffs are fetched against the working tree of <paramref name="repoRoot"/>
        /// for <paramref name="filePaths"/>. <paramref name="workingTreeKey"/>
        /// is the registry key used by <see cref="CodeReviewPopupManager"/> to
        /// deduplicate concurrent opens against the same (repo, file) target —
        /// callers should derive it via the manager's helper, not freelance it.
        /// </summary>
        public CodeReviewPopupForm(
            string workingTreeKey,
            string repoRoot,
            IList<string> filePaths,
            string initialFilePath)
        {
            _workingTreeKey = workingTreeKey ?? throw new ArgumentNullException(nameof(workingTreeKey));
            _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
            _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
            _initialFilePath = initialFilePath;
            _isWorkingTreeMode = true;

            // Title bar surfaces the leaf file name (or count) so users with
            // multiple ad-hoc popups open can tell them apart. Falls back to
            // repo root leaf when no initial file is provided.
            string titleSubject;
            if (!string.IsNullOrEmpty(initialFilePath))
            {
                titleSubject = Path.GetFileName(initialFilePath);
            }
            else if (filePaths.Count == 1)
            {
                titleSubject = Path.GetFileName(filePaths[0]);
            }
            else
            {
                titleSubject = $"{Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar))} ({filePaths.Count} files)";
            }
            Text = $"Code Review: {titleSubject} (working tree)";
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
                        // F-R2-3: bind to the form's own taskId/working-tree
                        // identity — ignore any inbound id in the message so a
                        // compromised JS payload can't redirect this form's
                        // fetches elsewhere. The form is per-target by construction.
                        _ = HandleFetchDataAsync();
                        break;
                    case "wrap_in_quick_task":
                        // Phase 3c: working-tree-only path. Replaces the Phase-3b
                        // placeholder hint with a real "create quick-task + link
                        // these files" round-trip. A wrap message in task mode
                        // is either a stale handler or hostile JS — drop it.
                        if (!_isWorkingTreeMode) break;
                        if (root.TryGetProperty("title", out var wrapTitleEl))
                        {
                            string wrapTitle = wrapTitleEl.GetString();
                            HandleWrapInQuickTask(wrapTitle);
                        }
                        break;
                    case "code_review_verdict":
                        // Working-tree mode has no task to transition — Phase 3c
                        // replaces Pass/Submit-Notes with the wrap_in_quick_task
                        // affordance handled above. A verdict message arriving
                        // here in working-tree mode is either a stale handler
                        // firing or hostile JS; either way, drop it.
                        if (_isWorkingTreeMode) break;
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
                // mode lets the JS branch its post-init fetch shape and hide
                // verdict UI in working-tree mode (Phase 3b). The JS still
                // never sees the form's taskId / repoRoot as authoritative —
                // C# binds those off form state when fetch_data lands.
                mode = _isWorkingTreeMode ? "workingTree" : "task",
                taskId = _taskId,
                taskTitle = _taskTitle,
                filePath = _initialFilePath,
                theme = _isDark ? "dark" : "light"
            });
        }

        private async Task HandleFetchDataAsync()
        {
            string filesJson = null;
            string agentReportJson = null;
            string reviewBaseError = null;
            try
            {
                if (_crService != null)
                {
                    CodeReviewData data;
                    if (_isWorkingTreeMode)
                    {
                        data = await _crService.GetCodeReviewData(_repoRoot, _filePaths);
                    }
                    else
                    {
                        data = await _crService.GetCodeReviewData(_taskId);
                    }
                    if (data != null)
                    {
                        filesJson = data.FilesJson;
                        agentReportJson = data.AgentReportJson;
                        reviewBaseError = data.ReviewBaseError;
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
            sb.Append(",\"reviewBaseError\":");
            sb.Append(string.IsNullOrEmpty(reviewBaseError) ? "null" : JsonSerializer.Serialize(reviewBaseError));
            sb.Append("}}");
            await PostRawJsonToWebViewAsync(sb.ToString());
        }

        private void HandleWrapInQuickTask(string title)
        {
            bool ok = false;
            string taskId = null;
            string error = null;
            try
            {
                if (_crService == null)
                {
                    error = "CodeReviewService not wired";
                }
                else
                {
                    // F-R2-3 (parity with HandleVerdict): bind to the form's own
                    // working-tree identity. Inbound title is the only field
                    // taken from JS; repoRoot + filePaths come from form state
                    // so a compromised JS payload can't re-aim the wrap.
                    var result = _crService.WrapInQuickTask(
                        title,
                        _repoRoot,
                        _filePaths,
                        createdBy: "CodeReview",
                        projectId: null);
                    ok = result?.Ok == true;
                    taskId = result?.TaskId;
                    error = result?.Error;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"wrap_result\",\"ok\":");
            sb.Append(ok ? "true" : "false");
            if (!string.IsNullOrEmpty(taskId))
            {
                sb.Append(",\"taskId\":");
                sb.Append(JsonSerializer.Serialize(taskId));
            }
            if (!string.IsNullOrEmpty(error))
            {
                sb.Append(",\"error\":");
                sb.Append(JsonSerializer.Serialize(error));
            }
            sb.Append('}');
            _ = PostRawJsonToWebViewAsync(sb.ToString());

            // Fire cross-panel signal AFTER the JS ack is dispatched. Wrapped
            // in try/catch so a misbehaving subscriber can't mask the wrap
            // success from JS (the popup-side state is the user-visible source
            // of truth; HUD Git auto-refresh is convenience on top).
            if (ok)
            {
                try { QuickTaskCreated?.Invoke(this, taskId); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[CodeReviewPopupForm] QuickTaskCreated subscriber threw: {ex.Message}");
                }
            }
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
