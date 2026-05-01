using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Controls.Shared;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based Git tab — per-project HUD pane showing branch / uncommitted
    /// count / ahead-behind / last-fetch in the header strip, plus scaffolded body
    /// sections (working changes / commits / branches) that items [8]-[10] flesh
    /// out. Subscribes to <see cref="GitRepoService.RepoStateChanged"/> for
    /// refresh-on-<c>.git/</c>-mutation; falls back to a manual Refresh button.
    ///
    /// <para>Multi-repo aware via <see cref="GitRepoManager.DetectLayout"/>:</para>
    /// <list type="bullet">
    ///   <item><description><c>Standard</c> — full header + scaffolded body.</description></item>
    ///   <item><description><c>LinkedGitDir</c> — empty-state for worktree/submodule (.git is a file).</description></item>
    ///   <item><description><c>NotARepo</c> — empty-state inviting <c>git init</c>.</description></item>
    /// </list>
    /// </summary>
    public class HudGitRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectPath;

        // _currentService is owned by GitRepoManager (the broker DI'd cache),
        // not by this panel. Disposal is the manager's responsibility — this
        // panel only unsubscribes its handler in UnsubscribeCurrent / Dispose.
#pragma warning disable CA2213
        private GitRepoService _currentService;
#pragma warning restore CA2213

        private string _pendingJson;

        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Raised when the user picks "Open in pop-up diff editor" from the
        /// file-row context menu in the Git tab. The string argument is the
        /// repo-relative path (forward-slashes, as LibGit2Sharp emits) for
        /// which the inline diff was already loadable. The host
        /// (<see cref="Docking.TerminalDocument"/>) routes this to the
        /// existing standalone <c>ShowDiffPopup</c> with persisted bounds.
        /// </summary>
        public event EventHandler<string> OpenDiffPopupRequested;

        public HudGitRenderer()
        {
            SuspendLayout();
            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudGitRenderer";
            Visible = false;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "gitWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += (s, e) =>
            {
                if (Visible && !_isInitialized && !_isInitializing) InitializeWebView();
            };
        }

        public void Initialize(MessageBroker broker) { _broker = broker; }

        /// <summary>
        /// Bind this panel to a project root. Detects git layout, subscribes to
        /// <see cref="GitRepoService.RepoStateChanged"/> for live refresh on
        /// Standard repos, or posts the appropriate empty-state message for
        /// LinkedGitDir / NotARepo paths.
        /// </summary>
        public void SetProject(string projectPath)
        {
            if (_projectPath == projectPath) return;

            UnsubscribeCurrent();
            _projectPath = projectPath;

            if (_isInitialized) ApplyProject();
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized) PostJson(new { type = "theme", isDark });
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null) _webView.ZoomFactor = zoom;
        }

        // -------------------------------------------------------------------------
        // WebView lifecycle
        // -------------------------------------------------------------------------

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
                string htmlPath = FindHtml("Controls/HudGitPanel/hud-git.html", "HudGitPanel/hud-git.html");
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                else
                    _isInitializing = false;
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
                if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString();

                switch (type)
                {
                    case "ready":
                        _isInitialized = true;
                        _isInitializing = false;
                        PostJson(new { type = "theme", isDark = _isDarkTheme });
                        if (_pendingJson != null)
                        {
                            PostRaw(_pendingJson);
                            _pendingJson = null;
                        }
                        else
                        {
                            ApplyProject();
                        }
                        _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                        if (Math.Abs(_pendingZoom - 1.0) > 0.01) _webView.ZoomFactor = _pendingZoom;
                        break;

                    case "refresh":
                        _ = RefreshAsync();
                        break;

                    case "fetch":
                        // Fetch wiring deferred — header button stub only in v1.
                        // Future item adds GitRepoService.Fetch + progress UI.
                        break;

                    case "select_file":
                        if (doc.RootElement.TryGetProperty("path", out var pathProp))
                        {
                            string path = pathProp.GetString();
                            if (!string.IsNullOrEmpty(path)) _ = LoadDiffAsync(path);
                        }
                        break;

                    case "select_commit":
                        if (doc.RootElement.TryGetProperty("sha", out var shaProp))
                        {
                            string sha = shaProp.GetString();
                            string displayName = null;
                            if (doc.RootElement.TryGetProperty("displayName", out var nameProp))
                                displayName = nameProp.GetString();
                            if (!string.IsNullOrEmpty(sha)) _ = LoadCommitDiffAsync(sha, displayName);
                        }
                        break;

                    case "open_diff_popup":
                        // Right-click → "Open in pop-up diff editor" from the
                        // file-row context menu (smoke-1 polish [15] fix #7).
                        // Forwards the repo-relative path; the host resolves
                        // the GitRepoService and shows the standalone popup.
                        if (doc.RootElement.TryGetProperty("path", out var popupPathProp))
                        {
                            string popupPath = popupPathProp.GetString();
                            if (!string.IsNullOrEmpty(popupPath))
                                OpenDiffPopupRequested?.Invoke(this, popupPath);
                        }
                        break;
                }
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Project binding
        // -------------------------------------------------------------------------

        private void ApplyProject()
        {
            if (string.IsNullOrEmpty(_projectPath))
            {
                Send(new { type = "empty_no_project" });
                return;
            }

            var manager = _broker?.GitRepos;
            if (manager == null)
            {
                Send(new { type = "empty_no_project" });
                return;
            }

            var layout = manager.DetectLayout(_projectPath);
            switch (layout)
            {
                case GitRepoLayout.LinkedGitDir:
                    Send(new { type = "empty_linked" });
                    return;
                case GitRepoLayout.NotARepo:
                    Send(new { type = "empty_no_repo" });
                    return;
                case GitRepoLayout.Standard:
                default:
                    break;
            }

            _currentService = manager.GetOrCreate(_projectPath);
            if (_currentService == null)
            {
                // Race: layout said Standard but GetOrCreate failed (e.g., the
                // .git dir was deleted between calls). Fall back to no-repo
                // empty-state rather than leaving the header in a half-rendered
                // state.
                Send(new { type = "empty_no_repo" });
                return;
            }
            _currentService.RepoStateChanged += OnRepoStateChanged;
            _ = RefreshAsync();
        }

        private void UnsubscribeCurrent()
        {
            if (_currentService != null)
            {
                _currentService.RepoStateChanged -= OnRepoStateChanged;
                _currentService = null;
            }
        }

        private void OnRepoStateChanged(object sender, RepoChangedEventArgs args)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(() => _ = RefreshAsync()));
                else _ = RefreshAsync();
            }
            catch (Exception ex)
            {
                // Most likely ObjectDisposedException from BeginInvoke if Dispose
                // ran between the IsDisposed check above and the BeginInvoke call.
                // Benign (UnsubscribeCurrent in Dispose ensures this is the last
                // event we'll see) but log so a real issue isn't lost.
                System.Diagnostics.Debug.WriteLine(
                    $"[HudGitRenderer.OnRepoStateChanged] {ex.GetType().Name}: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // State refresh
        // -------------------------------------------------------------------------

        private async Task RefreshAsync()
        {
            var svc = _currentService;
            if (svc == null) return;

            // Capture broker-owned services into locals BEFORE entering
            // Task.Run — debugger SERIOUS finding from item [11]: reading
            // _broker.GitAttribution inside the lambda races with broker swap
            // (auto-property has no volatile or lock). Locals freeze the
            // reference for the duration of this background pass.
            var attributionSvc = _broker?.GitAttribution;
            string repoRoot = svc.RepoRoot;

            try
            {
                var snapshot = await Task.Run(() =>
                {
                    string branch = svc.CurrentBranch ?? "";
                    var status = svc.GetWorkingTreeStatus();
                    int changes = status?.Count ?? 0;
                    bool hasRemote = svc.HasRemote;
                    int ahead = 0;
                    int behind = 0;
                    string lastFetch = null;

                    if (hasRemote)
                    {
                        var ab = svc.GetAheadBehind();
                        if (ab != null)
                        {
                            ahead = ab.Ahead;
                            behind = ab.Behind;
                        }
                        var t = svc.GetLastFetchTime();
                        if (t.HasValue) lastFetch = FormatRelativeTime(t.Value);
                    }

                    var statusList = status ?? (System.Collections.Generic.IReadOnlyList<GitFileStatus>)Array.Empty<GitFileStatus>();

                    // Phase 2 overlays — fetch attribution for each uncommitted file
                    // (agent + task + pipeline status). Falls back to empty fields on
                    // any failure so the working-changes panel still renders.
                    // attributionSvc captured into local before this Task.Run; stable.
                    System.Collections.Generic.IReadOnlyList<GitFileAttribution> attributions = Array.Empty<GitFileAttribution>();
                    var fileList = statusList.Select(f => f.Path ?? "").ToList();
                    if (attributionSvc != null && statusList.Count > 0)
                    {
                        try
                        {
                            attributions = attributionSvc.GetAttributionForFiles(repoRoot, fileList);
                        }
                        catch
                        {
                            attributions = Array.Empty<GitFileAttribution>();
                        }
                    }

                    // Index attribution by file path. Ordinal (case-sensitive) per
                    // debugger SERIOUS finding: case-only-different paths from
                    // LibGit2Sharp's RetrieveStatus would collide under
                    // OrdinalIgnoreCase and silently swap the chips. Paths
                    // round-trip case-exact through GitFileAttribution.FilePath.
                    var attrByPath = new System.Collections.Generic.Dictionary<string, GitFileAttribution>(StringComparer.Ordinal);
                    foreach (var a in attributions)
                    {
                        if (!string.IsNullOrEmpty(a?.FilePath))
                            attrByPath[a.FilePath] = a;
                    }

                    var workingTree = statusList
                        .Select(f =>
                        {
                            attrByPath.TryGetValue(f.Path ?? "", out var a);
                            return new
                            {
                                path = f.Path ?? "",
                                kind = f.Kind.ToString(),
                                linesAdded = f.LinesAdded,
                                linesDeleted = f.LinesDeleted,
                                agent = a?.Agent ?? "",
                                taskId = a?.TaskId ?? "",
                                taskTitle = a?.TaskTitle ?? "",
                                pipelineStatus = a?.PipelineStatus ?? "",
                            };
                        })
                        .ToArray();

                    // Cross-task contamination: distinct active task IDs across ALL
                    // active claims (multi-claim aware). Uses a separate query
                    // (GetCrossTaskActiveTaskIds) instead of the dedup'd attribution
                    // set, since the per-file attribution above shows only one
                    // primary task per file and would otherwise hide the very
                    // multi-claim case the banner exists to flag (adversary HIGH).
                    string[] contamTaskIds = Array.Empty<string>();
                    if (attributionSvc != null && fileList.Count > 0)
                    {
                        try
                        {
                            contamTaskIds = attributionSvc
                                .GetCrossTaskActiveTaskIds(repoRoot, fileList)
                                .ToArray();
                        }
                        catch
                        {
                            contamTaskIds = Array.Empty<string>();
                        }
                    }

                    var commits = svc.GetRecentCommits(30);
                    var recentCommits = (commits ?? (System.Collections.Generic.IReadOnlyList<GitCommitInfo>)Array.Empty<GitCommitInfo>())
                        .Select(c => new
                        {
                            shortSha = c.ShortSha ?? "",
                            fullSha = c.FullSha ?? "",
                            subject = c.Subject ?? "",
                            authorName = c.AuthorName ?? "",
                            coAuthors = c.CoAuthors ?? (System.Collections.Generic.IReadOnlyList<string>)Array.Empty<string>(),
                            when = c.When != DateTimeOffset.MinValue
                                ? c.When.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                                : null,
                        })
                        .ToArray();

                    var branchInfos = svc.GetBranches();
                    var branches = (branchInfos ?? (System.Collections.Generic.IReadOnlyList<GitBranchInfo>)Array.Empty<GitBranchInfo>())
                        .Select(b => new
                        {
                            name = b.Name ?? "",
                            isRemote = b.IsRemote,
                            isCurrent = b.IsCurrent,
                            when = b.LastCommitTime.HasValue
                                ? b.LastCommitTime.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                                : null,
                        })
                        .ToArray();

                    return new
                    {
                        type = "git_state",
                        // repoRoot ships native-separator filesystem path so JS
                        // can compose absolute-path tooltips on file rows
                        // (smoke-1 polish [15] fix #3). Already stable inside
                        // this Task.Run via the captured local.
                        repoRoot,
                        branch,
                        changes,
                        hasRemote,
                        ahead,
                        behind,
                        lastFetch,
                        workingTree,
                        recentCommits,
                        branches,
                        contamTaskIds,
                    };
                });
                Send(snapshot);
            }
            catch { }
        }

        /// <summary>
        /// Loads a commit's diff vs its first parent and ships the parsed
        /// structured form to the WebView (same path as <see cref="LoadDiffAsync"/>
        /// so JS uses one renderer for both working-tree and commit diffs —
        /// no duplicate implementation).
        /// </summary>
        private async Task LoadCommitDiffAsync(string sha, string displayName)
        {
            var svc = _currentService;
            if (svc == null || string.IsNullOrEmpty(sha)) return;

            try
            {
                var data = await Task.Run(() =>
                {
                    string diff = svc.GetCommitDiff(sha);
                    var parsed = DiffRenderer.ParseUnifiedDiff(diff);
                    var lines = parsed
                        .Select(p => new { kind = p.Kind.ToString(), text = p.Text ?? "" })
                        .ToArray();
                    // selectionKey echoes the originating SHA so the JS-side
                    // stale-click guard can compare on an exact identifier
                    // instead of parsing the displayName format. Adversary
                    // MEDIUM finding: short-SHA prefix matching could accept
                    // a different commit's diff after rebase/cherry-pick.
                    return new
                    {
                        type = "diff_loaded",
                        path = string.IsNullOrEmpty(displayName) ? sha : displayName,
                        selectionKey = sha,
                        lines,
                    };
                });
                Send(data);
            }
            catch { }
        }

        /// <summary>
        /// Loads the diff for a single working-tree file and ships the parsed
        /// structured form to the WebView so the JS side renders per-line spans
        /// via textContent (no pre-rendered HTML crosses the boundary).
        /// </summary>
        private async Task LoadDiffAsync(string relativePath)
        {
            var svc = _currentService;
            if (svc == null || string.IsNullOrEmpty(relativePath)) return;

            try
            {
                var data = await Task.Run(() =>
                {
                    string diff = svc.GetFileDiff(relativePath);
                    var parsed = DiffRenderer.ParseUnifiedDiff(diff);
                    var lines = parsed
                        .Select(p => new { kind = p.Kind.ToString(), text = p.Text ?? "" })
                        .ToArray();
                    // selectionKey echoes the originating relativePath so JS
                    // stale-click guard can match on exact identifier.
                    return new
                    {
                        type = "diff_loaded",
                        path = relativePath,
                        selectionKey = relativePath,
                        lines,
                    };
                });
                Send(data);
            }
            catch { }
        }

        private static string FormatRelativeTime(DateTimeOffset t)
        {
            // Buckets aligned with the JS `formatRelativeTime` in hud-git.html so
            // the no-remote stale-repo header (`fetched X mo ago`) and the
            // commits-list relative times use the same vocabulary instead of
            // diverging at 30+ days.
            var delta = DateTimeOffset.UtcNow - t.ToUniversalTime();
            if (delta < TimeSpan.Zero) return "just now"; // clock skew
            long total = (long)delta.TotalSeconds;
            if (total < 60) return "just now";
            if (total < 3600) return (total / 60) + " min ago";
            if (total < 86400) return (total / 3600) + " hr ago";
            if (total < 30L * 86400) return (total / 86400) + " d ago";
            if (total < 365L * 86400) return (total / (30L * 86400)) + " mo ago";
            return (total / (365L * 86400)) + " yr ago";
        }

        // -------------------------------------------------------------------------
        // WebView messaging helpers
        // -------------------------------------------------------------------------

        private void Send(object data)
        {
            string json = JsonSerializer.Serialize(data);
            if (_isInitialized) PostRaw(json);
            else _pendingJson = json;
        }

        private void PostJson(object d)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d)); }
            catch { }
        }

        private void PostRaw(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(json); }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnsubscribeCurrent();
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
