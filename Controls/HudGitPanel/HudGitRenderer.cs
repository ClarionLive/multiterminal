using System;
using System.Collections.Generic;
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
using MultiTerminal.TaskLifecycleBoard;
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
    ///   <item><description><c>Worktree</c> — full header + scaffolded body, identical to Standard. The switcher (rendered above the header) lists parent + sibling worktrees.</description></item>
    ///   <item><description><c>Submodule</c> — empty-state pointing the user at the parent repo.</description></item>
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

        // Project ID resolved from _originalProjectPath via ProjectService at
        // SetProject() time. Used by OnBranchOutcomeUpdated to filter event
        // fires to this panel's active project. Null if the path is not in
        // the registry (events are ignored in that case — safe fallback).
        private string _projectId;

        /// <summary>
        /// The project root the panel was originally bound to via
        /// <see cref="SetProject"/>. Used as the stable identity for the
        /// per-project "last selected repo" persistence key — switching
        /// between parent + linked worktrees updates <see cref="_projectPath"/>
        /// but leaves this field alone, so a future <see cref="SetProject"/>
        /// to a different project doesn't load the wrong saved choice.
        /// </summary>
        private string _originalProjectPath;

        /// <summary>
        /// Membership set of canonical paths that the most recent
        /// <c>RefreshWorktreeListAsync</c> reported as legitimate worktrees of
        /// the current project. <see cref="SwitchToRepo"/> rejects targets not
        /// in this set so a compromised webview message or poisoned settings
        /// value can't repoint the panel at an unrelated repository.
        /// Populated post-fetch; cleared in <see cref="SetProject"/> so a
        /// stale entry from the prior project cannot authorize a cross-project
        /// switch during the transition window.
        /// Access is single-threaded (UI thread only) so no synchronisation.
        /// </summary>
        private readonly HashSet<string> _worktreeAllowlist
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Monotonic counter bumped on every <see cref="SetProject"/> call.
        /// A <see cref="RefreshWorktreeListAsync"/> continuation captures the
        /// generation at start and ignores its own results if the generation
        /// has moved on by the time the await completes — defeats the race
        /// where an old project's list-fetch returns AFTER a new
        /// <see cref="SetProject"/> has cleared the allowlist for the next
        /// project.
        /// </summary>
        private int _allowlistGeneration;

        /// <summary>
        /// Restored selection from <see cref="SettingsService"/> waiting to be
        /// applied. We deliberately do NOT bind <see cref="_projectPath"/> to
        /// the restored value during <see cref="SetProject"/> — doing so
        /// would let a poisoned settings entry render an unrelated repo's
        /// data before the allowlist can validate it. Instead the restore is
        /// applied only after <see cref="RefreshWorktreeListAsync"/> proves
        /// the saved value is a member of the validated worktree set.
        /// </summary>
        private string _pendingRestoredRepo;

        // _currentService is owned by GitRepoManager (the broker DI'd cache),
        // not by this panel. Disposal is the manager's responsibility — this
        // panel only unsubscribes its handler in UnsubscribeCurrent / Dispose.
#pragma warning disable CA2213
        private GitRepoService _currentService;
#pragma warning restore CA2213

        private string _pendingJson;

        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Raised when the user picks "Open Code Review" from the file-row
        /// context menu in the Git tab. The string argument is the
        /// repo-relative path (forward-slashes, as LibGit2Sharp emits) for
        /// which the inline diff was already loadable. The host
        /// (<see cref="Docking.TerminalDocument"/>) routes this directly to
        /// <see cref="Dialogs.CodeReviewPopupManager.OpenOrFocus"/> when the
        /// file is linked to an active task.
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

        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            if (_broker != null)
            {
                _broker.BranchOutcomeUpdated += OnBranchOutcomeUpdated;
            }
        }

        private void OnBranchOutcomeUpdated(object sender, BranchOutcomeUpdatedEventArgs args)
        {
            if (IsDisposed) return;
            if (args == null) return;
            if (string.IsNullOrEmpty(_projectId)) return;
            if (!string.Equals(_projectId, args.ProjectId, StringComparison.Ordinal)) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => { _ = RefreshAsync(); }));
                }
                else
                {
                    _ = RefreshAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.OnBranchOutcomeUpdated] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ResolveProjectIdFromPath(string projectPath)
        {
            _projectId = null;
            if (string.IsNullOrEmpty(projectPath)) return;

            var svc = _broker?.ProjectService;
            if (svc == null) return;

            try
            {
                foreach (var entry in svc.GetAllRegisteredProjects())
                {
                    if (entry == null) continue;
                    if (string.Equals(entry.Path, projectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _projectId = entry.Id;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.ResolveProjectIdFromPath] {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy single-arg overload. Resolves the project id by path against
        /// <see cref="ProjectService.GetAllRegisteredProjects"/>. Use the
        /// two-arg overload from callers that already know the project id —
        /// the path-only resolver fails for worktree subdirectories (which
        /// aren't registered as projects in their own right) and silently
        /// disables the per-(project, branch) features (outcome edits, fit
        /// suggestions) for terminals opened in a worktree subfolder.
        /// </summary>
        public void SetProject(string projectPath)
        {
            SetProject(null, projectPath);
        }

        /// <summary>
        /// Bind this panel to a project root with an explicit project id.
        /// Preferred overload: callers that already resolved the project id
        /// (e.g. <c>TerminalDocument</c>) should pass it through so the path
        /// vs registry mismatch on worktree subdirectories doesn't strand
        /// <see cref="_projectId"/> as null. When <paramref name="projectId"/>
        /// is null/empty, falls back to <see cref="ResolveProjectIdFromPath"/>
        /// for backwards compatibility.
        /// </summary>
        public void SetProject(string projectId, string projectPath)
        {
            // The "project" identity changes only when SetProject moves us to a
            // genuinely different root. Switcher-driven moves go through
            // SwitchToRepo and preserve _originalProjectPath.
            if (string.Equals(_originalProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnsubscribeCurrent();
            _originalProjectPath = projectPath;

            // Cross-project safety: clear the prior project's allowlist and
            // bump the generation token so any in-flight RefreshWorktreeListAsync
            // continuation from the previous project ignores its own result.
            // Without this, a stale switcher click during the transition
            // window can submit a path that was valid for the OLD project but
            // not the new one and pass IsAllowedWorktree.
            _worktreeAllowlist.Clear();
            _allowlistGeneration++;

            // Defer the persisted-selection restore until after the allowlist
            // is validated. Binding _projectPath to a poisoned settings value
            // here would let an attacker show an unrelated repo's data for
            // one render cycle before the post-fetch revert fires.
            _pendingRestoredRepo = TryRestoreSelectedRepo(projectPath);
            _projectPath = projectPath;

            if (!string.IsNullOrEmpty(projectId))
            {
                // Caller-provided project id wins. Skips the registry lookup
                // entirely — required for worktree subdirectories whose paths
                // aren't registered as projects.
                _projectId = projectId;
            }
            else
            {
                ResolveProjectIdFromPath(projectPath);
            }

            if (_isInitialized) ApplyProject();
        }

        /// <summary>
        /// Switches the panel to a different repo path from the worktree
        /// switcher (parent or a linked worktree). Idempotent for the
        /// already-displayed path. Persists the choice so the next bind
        /// restores it.
        ///
        /// <para>Validates the target against the most recent worktree
        /// allowlist — a compromised webview message or poisoned settings
        /// value claiming an arbitrary path is silently rejected so the panel
        /// cannot be repointed at an unrelated repository. The allowlist is
        /// populated by <see cref="RefreshWorktreeListAsync"/>, so the very
        /// first switch attempt before any list fetch will be rejected; that
        /// edge is acceptable because the switcher UI is only rendered after
        /// the list arrives.</para>
        /// </summary>
        private void SwitchToRepo(string newPath)
        {
            if (string.IsNullOrEmpty(newPath)) return;
            if (string.Equals(_projectPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(newPath)) return;

            if (!IsAllowedWorktree(newPath))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.SwitchToRepo] Rejected non-allowlisted path: {newPath}");
                return;
            }

            UnsubscribeCurrent();
            _projectPath = newPath;
            PersistSelectedRepo(_originalProjectPath, newPath);

            if (_isInitialized) ApplyProject();
        }

        /// <summary>
        /// Replaces the membership set used by <see cref="SwitchToRepo"/> and
        /// <see cref="IsAllowedWorktree"/>. Called from
        /// <see cref="RefreshWorktreeListAsync"/> with the validated entries
        /// returned by <see cref="MultiTerminal.Services.WorktreeListService"/>
        /// (already filtered for safe local paths AND verified to share the
        /// same common gitdir as the source repo).
        /// </summary>
        private void UpdateWorktreeAllowlist(System.Collections.Generic.IReadOnlyList<MultiTerminal.Services.WorktreeEntry> entries)
        {
            _worktreeAllowlist.Clear();
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                _worktreeAllowlist.Add(CanonicalizeForAllowlist(entry.Path));
            }

            // Defence-in-depth for a previously-valid switcher selection that
            // turned out to no longer be a member — e.g. another agent pruned
            // the worktree between our last refresh and this one. Revert to
            // the original project and clear the persisted choice.
            // Note: the persisted-selection restore is handled separately in
            // RefreshWorktreeListAsync's deferred path, so on first bind this
            // branch never fires (_projectPath == _originalProjectPath).
            if (!string.IsNullOrEmpty(_projectPath)
                && !string.IsNullOrEmpty(_originalProjectPath)
                && !string.Equals(_projectPath, _originalProjectPath, StringComparison.OrdinalIgnoreCase)
                && !IsAllowedWorktree(_projectPath))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.UpdateWorktreeAllowlist] Current path '{_projectPath}' is no longer in the validated worktree set — reverting to original '{_originalProjectPath}'.");
                PersistSelectedRepo(_originalProjectPath, _originalProjectPath); // removes key
                UnsubscribeCurrent();
                _projectPath = _originalProjectPath;
                if (_isInitialized) ApplyProject();
            }
        }

        /// <summary>
        /// True when <paramref name="path"/> matches one of the entries from
        /// the most recent worktree list. Comparison is canonical
        /// (full-path + forward-slash + trim trailing slash) to absorb
        /// `\` vs `/`, trailing slashes, and short-form vs long-form
        /// differences.
        /// </summary>
        private bool IsAllowedWorktree(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return _worktreeAllowlist.Contains(CanonicalizeForAllowlist(path));
        }

        private static string CanonicalizeForAllowlist(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            }
            catch
            {
                return path.Replace('\\', '/').TrimEnd('/');
            }
        }

        /// <summary>
        /// Returns the persisted "selected repo" for this project, or
        /// <c>null</c> if none / the value is no longer on disk. The
        /// key is hashed off the canonical original project path so that
        /// switching between projects doesn't cross-contaminate.
        /// </summary>
        private static string TryRestoreSelectedRepo(string originalProjectPath)
        {
            if (string.IsNullOrEmpty(originalProjectPath)) return null;
            try
            {
                string key = MakeSelectedRepoKey(originalProjectPath);
                string saved = SettingsService.Default.Get(key);
                if (string.IsNullOrEmpty(saved)) return null;
                if (!Directory.Exists(saved)) return null;
                return saved;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.TryRestoreSelectedRepo] {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Persists the user's switcher choice for the given project. When
        /// <paramref name="selectedRepoPath"/> equals
        /// <paramref name="originalProjectPath"/>, the key is removed so the
        /// "no override" default is restored cleanly.
        /// </summary>
        private static void PersistSelectedRepo(string originalProjectPath, string selectedRepoPath)
        {
            if (string.IsNullOrEmpty(originalProjectPath)) return;
            try
            {
                string key = MakeSelectedRepoKey(originalProjectPath);
                if (string.IsNullOrEmpty(selectedRepoPath)
                    || string.Equals(selectedRepoPath, originalProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsService.Default.Remove(key);
                }
                else
                {
                    SettingsService.Default.Set(key, selectedRepoPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.PersistSelectedRepo] {ex.Message}");
            }
        }

        private static string MakeSelectedRepoKey(string originalProjectPath)
        {
            // Canonical form: full path, forward slashes, no trailing slash.
            // Stable across `\` vs `/` and ad-hoc trailing-slash variations.
            string canonical;
            try { canonical = Path.GetFullPath(originalProjectPath); }
            catch { canonical = originalProjectPath; }
            canonical = canonical.Replace('\\', '/').TrimEnd('/');
            return $"hudGit.selectedRepo.{canonical}";
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
                        // Right-click → "Open Code Review" from the file-row
                        // context menu. Forwards the repo-relative path; the
                        // host resolves the task linkage and opens the
                        // standalone Code Review popup via
                        // CodeReviewPopupManager.OpenOrFocus.
                        if (doc.RootElement.TryGetProperty("path", out var popupPathProp))
                        {
                            string popupPath = popupPathProp.GetString();
                            if (!string.IsNullOrEmpty(popupPath))
                                OpenDiffPopupRequested?.Invoke(this, popupPath);
                        }
                        break;

                    case "open_lifecycle_board":
                        HandleOpenLifecycle(doc.RootElement);
                        break;

                    case "switch_repo":
                        // Posted from the parent/worktrees switcher above the
                        // header. Re-points the panel at the selected repo
                        // path and persists the choice for next session.
                        if (doc.RootElement.TryGetProperty("path", out var switchPathProp))
                        {
                            string switchPath = switchPathProp.GetString();
                            if (!string.IsNullOrEmpty(switchPath)) SwitchToRepo(switchPath);
                        }
                        break;

                    case "set_branch_outcome":
                        // Posted from the inspector's outcome-edit input (item 8 of
                        // task 6a56f56c). Routes the user's edit through the
                        // BranchMetadataService — which fires BranchOutcomeUpdated,
                        // and OnBranchOutcomeUpdated re-fires RefreshAsync, which
                        // ships the new outcome on the next git_state_tree, which
                        // re-renders both the row label and the Details tab.
                        HandleSetBranchOutcome(doc.RootElement);
                        break;
                }
            }
            catch { }
        }

        private void HandleSetBranchOutcome(JsonElement root)
        {
            string branchName = null;
            if (root.TryGetProperty("branchName", out var nameEl))
                branchName = nameEl.GetString();

            // Echo a failure to the WebView so the editor's "Saving…" stuck
            // state can be cleared and the user gets feedback. Three failure
            // shapes get an explicit reason; the catch below sends a generic
            // 'exception' shape with the exception message.
            void NotifyFailed(string reason)
            {
                try
                {
                    PostJson(new
                    {
                        type = "set_branch_outcome_failed",
                        branchName = branchName ?? string.Empty,
                        reason = reason ?? "unknown",
                    });
                }
                catch { /* non-fatal — UI has its own timeout fallback */ }
            }

            if (string.IsNullOrEmpty(_projectId))
            {
                NotifyFailed("project not registered (per-project outcomes unavailable from this terminal)");
                return;
            }
            if (_broker?.BranchMetadata == null)
            {
                NotifyFailed("outcome service unavailable");
                return;
            }
            if (string.IsNullOrWhiteSpace(branchName))
            {
                NotifyFailed("missing branchName");
                return;
            }

            string outcome = null;
            if (root.TryGetProperty("outcome", out var outcomeEl))
            {
                outcome = outcomeEl.ValueKind == JsonValueKind.String ? outcomeEl.GetString() : null;
            }

            try
            {
                // Empty/whitespace outcome is treated as "clear" — the service
                // upserts NULL when null is passed; we trim and pass null
                // explicitly so DB sees a single canonical "no outcome" value.
                string normalized = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
                _broker.BranchMetadata.SetOutcome(_projectId, branchName, normalized, "user");
                // Success path: BranchOutcomeUpdated fires from the service,
                // OnBranchOutcomeUpdated kicks RefreshAsync, the new state pass
                // re-renders the Details tab — UI re-enters read-only display
                // automatically. No explicit success echo needed.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HudGitRenderer.HandleSetBranchOutcome] Failed for branch='{branchName}': {ex.Message}");
                NotifyFailed(ex.Message ?? ex.GetType().Name);
            }
        }

        private void HandleOpenLifecycle(JsonElement root)
        {
            if (!root.TryGetProperty("taskId", out var idEl)) return;
            string taskId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(taskId)) return;
            if (_broker == null) return;

            try
            {
                TaskLifecycleBoardForm.OpenForTask(taskId, _broker, _isDarkTheme);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HudGitRenderer.HandleOpenLifecycle] Failed for taskId='{taskId}': {ex.Message}");
            }
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
                // Worktrees are fully inspectable repos — LibGit2Sharp opens them
                // directly via their worktree path. Fall through to the standard
                // init path so the header + body render normally; the switcher
                // (item 4) will surface the parent + sibling worktrees on top.
                case GitRepoLayout.Worktree:
                case GitRepoLayout.Standard:
                    break;
                case GitRepoLayout.Submodule:
                case GitRepoLayout.UnsupportedLink:
                    // UnsupportedLink shares the empty-state UX with Submodule
                    // for now (both are "your .git is a gitlink we can't open
                    // for direct inspection"). A future ticket could give
                    // UnsupportedLink its own distinct hint message that
                    // suggests running `git rev-parse --git-common-dir` to
                    // diagnose, but the gating behavior is the same: don't
                    // pretend to support this layout.
                    Send(new { type = "empty_linked" });
                    return;
                case GitRepoLayout.NotARepo:
                    Send(new { type = "empty_no_repo" });
                    return;
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
                // RefreshAsync now ships the unified git_state_tree payload —
                // single fire keeps the tree's per-worktree dirty counts, the
                // selected-worktree subtree, and the branches-with-worktree-join
                // section consistent (no two-message tearing between the prior
                // git_state + worktrees pair).
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => { _ = RefreshAsync(); }));
                }
                else
                {
                    _ = RefreshAsync();
                }
            }
            catch (Exception ex)
            {
                // Most likely ObjectDisposedException from BeginInvoke if Dispose
                // ran between the IsDisposed check above and the BeginInvoke call.
                // Benign (UnsubscribeCurrent in Dispose ensures this is the last
                // event we'll see) but log so a real issue isn't lost.
                System.Diagnostics.Trace.WriteLine(
                    $"[HudGitRenderer.OnRepoStateChanged] {ex.GetType().Name}: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // State refresh
        // -------------------------------------------------------------------------

        /// <summary>
        /// Unified-payload refresh — fetches the worktree list, computes
        /// per-worktree working-changes + recent-commits via a transient
        /// <see cref="GitRepoService"/> per worktree, builds the
        /// branch→worktree join from the worktree entries, and ships one
        /// <c>git_state_tree</c> message. Replaces the prior
        /// <c>git_state</c> + <c>worktrees</c> pair.
        ///
        /// <para>Allowlist + deferred-restore work (formerly in the dropped
        /// <c>RefreshWorktreeListAsync</c>) lives here too — the
        /// authoritative worktree-set fetch happens at the top of this method
        /// and feeds both the security allowlist and the payload assembly.
        /// Single fetch keeps the two consumers in lock-step (no race window
        /// where the allowlist trails the payload by one fire).</para>
        /// </summary>
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
            var worktreeSvc = _broker?.WorktreeList;
            string repoRoot = svc.RepoRoot;

            string projectPath = _projectPath;
            if (string.IsNullOrEmpty(projectPath)) return;

            // Capture the generation at start. SetProject increments the
            // counter; if it moves while we're awaiting git, our result
            // belongs to a stale project and must not touch the new
            // project's allowlist (otherwise a switcher click could be
            // authorised against the wrong repo's set).
            int generationAtStart = _allowlistGeneration;

            try
            {
                // No ConfigureAwait(false) here — the continuation must resume
                // on the captured WinForms SynchronizationContext so the
                // subsequent allowlist update + SwitchToRepo + Send all land
                // on the UI thread. WebView2 COM proxies are STA-bound;
                // calling them from a thread-pool continuation throws
                // RPC_E_WRONG_THREAD. Matches LoadDiffAsync / LoadCommitDiffAsync
                // which deliberately omit ConfigureAwait for the same reason.
                System.Collections.Generic.IReadOnlyList<MultiTerminal.Services.WorktreeEntry> worktrees;
                if (worktreeSvc != null)
                {
                    var fetched = await worktreeSvc.GetWorktreesForRepoAsync(projectPath);
                    worktrees = fetched ?? (System.Collections.Generic.IReadOnlyList<MultiTerminal.Services.WorktreeEntry>)Array.Empty<MultiTerminal.Services.WorktreeEntry>();
                }
                else
                {
                    worktrees = Array.Empty<MultiTerminal.Services.WorktreeEntry>();
                }

                // Drop stale results: project switched, OR generation token
                // moved (a SetProject call superseded us even if the path is
                // somehow identical — covers the project-bounces-A→B→A edge).
                if (!string.Equals(projectPath, _projectPath, StringComparison.OrdinalIgnoreCase)) return;
                if (generationAtStart != _allowlistGeneration) return;

                // Cache the validated worktree set BEFORE shipping the payload
                // so SwitchToRepo can reject paths the user didn't actually
                // see in the rendered tree.
                UpdateWorktreeAllowlist(worktrees);

                // Deferred restore: SetProject parked the persisted-selection
                // value in _pendingRestoredRepo without binding _projectPath
                // to it. Now that we have an authenticated worktree set, apply
                // the restore only if the saved value is a member; otherwise
                // clear the now-invalid setting and stay on the original.
                if (!string.IsNullOrEmpty(_pendingRestoredRepo))
                {
                    string pending = _pendingRestoredRepo;
                    _pendingRestoredRepo = null;
                    if (IsAllowedWorktree(pending)
                        && !string.Equals(pending, _projectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // SwitchToRepo rebinds _projectPath, persists, and
                        // re-fires ApplyProject which kicks a fresh RefreshAsync
                        // against the restored repo. Bail out of this pass —
                        // the data we just fetched is for the wrong worktree.
                        SwitchToRepo(pending);
                        return;
                    }
                    if (!IsAllowedWorktree(pending))
                    {
                        // Saved selection no longer valid (worktree pruned,
                        // settings poisoned, or project moved on disk). Clear
                        // the stale key so we don't keep trying every session.
                        PersistSelectedRepo(_originalProjectPath, _originalProjectPath);
                    }
                }

                // Snapshot for the background pass. _projectPath can mutate
                // under us on the UI thread; the inner builder reads only
                // the captured locals.
                var worktreesSnapshot = worktrees;
                var attributionSvcCaptured = attributionSvc;
                string selectedWorktreePath = _projectPath;

                // Broker-owned services for per-branch enrichment. Captured into
                // locals BEFORE Task.Run for the same reason as attributionSvc:
                // the broker properties are auto-properties without volatile
                // and could race with a broker swap mid-pass.
                var branchMetaCaptured = _broker?.BranchMetadata;
                var taskDbCaptured = _broker?.TaskDb;
                string projectIdSnap = _projectId;

                var payload = await Task.Run(() =>
                {
                    // Branches enumerate from the main service handle — every
                    // worktree of a repo shares the same branch namespace, so
                    // one query is canonical.
                    var branchInfos = svc.GetBranches()
                        ?? (System.Collections.Generic.IReadOnlyList<GitBranchInfo>)Array.Empty<GitBranchInfo>();

                    // branch-name → worktree-path join, built from the
                    // worktree-list output. Sentinel branches ((detached),
                    // (bare), (unknown)) are skipped — they correspond to
                    // worktrees not pointing at a named branch, so there's
                    // nothing to match against the branches[] array.
                    var branchToWorktree = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var wt in worktreesSnapshot)
                    {
                        if (wt == null || string.IsNullOrEmpty(wt.Branch) || string.IsNullOrEmpty(wt.Path)) continue;
                        if (wt.Branch.Length > 0 && wt.Branch[0] == '(') continue;
                        branchToWorktree[wt.Branch] = wt.Path;
                    }

                    // Pre-fetch outcomes for this project in a single query so
                    // the per-branch loop below is O(1) lookup instead of O(N)
                    // round-trips. Empty map when project is unregistered or
                    // service unavailable — outcome falls back to null per branch.
                    var outcomesByBranch = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
                    if (branchMetaCaptured != null && !string.IsNullOrEmpty(projectIdSnap))
                    {
                        try
                        {
                            var outcomes = branchMetaCaptured.GetOutcomes(projectIdSnap);
                            if (outcomes != null)
                            {
                                foreach (var o in outcomes)
                                {
                                    if (o == null || string.IsNullOrEmpty(o.BranchName)) continue;
                                    if (!string.IsNullOrEmpty(o.Outcome))
                                        outcomesByBranch[o.BranchName] = o.Outcome;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.WriteLine($"[HudGitRenderer.RefreshAsync] outcome pre-fetch: {ex.Message}");
                        }
                    }

                    var branches = branchInfos
                        .Select(b =>
                        {
                            string wtPath = null;
                            if (!string.IsNullOrEmpty(b.Name) && !b.IsRemote)
                                branchToWorktree.TryGetValue(b.Name, out wtPath);

                            // Outcome lookup (project-scoped, pre-fetched).
                            string outcome = null;
                            if (!string.IsNullOrEmpty(b.Name))
                                outcomesByBranch.TryGetValue(b.Name, out outcome);

                            // Linked tasks: tasks whose task_worktrees row
                            // points at this branch. Skipped for remotes
                            // (refs/remotes/* never own a worktree). Per-branch
                            // query is acceptable: branch counts are small
                            // (typically <30) and the join hits the indexed
                            // task_id PK on tasks.
                            object[] linkedTasks = Array.Empty<object>();
                            // GetTasksLinkedToBranch is now project-scoped (Run 2 security
                            // fix). When _projectId is null (worktree subfolder + legacy
                            // SetProject(path) path), the helper returns empty rather than
                            // leaking cross-project tasks.
                            if (taskDbCaptured != null
                                && !string.IsNullOrEmpty(projectIdSnap)
                                && !string.IsNullOrEmpty(b.Name)
                                && !b.IsRemote)
                            {
                                try
                                {
                                    var tasks = taskDbCaptured.GetTasksLinkedToBranch(projectIdSnap, b.Name);
                                    if (tasks != null && tasks.Count > 0)
                                    {
                                        var arr = new object[tasks.Count];
                                        for (int i = 0; i < tasks.Count; i++)
                                            arr[i] = new { id = tasks[i].Id ?? "", title = tasks[i].Title ?? "" };
                                        linkedTasks = arr;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Trace.WriteLine($"[HudGitRenderer.RefreshAsync] linkedTasks lookup for branch='{b.Name}': {ex.Message}");
                                }
                            }

                            return new
                            {
                                name = b.Name ?? "",
                                tipSha = b.TipSha ?? "",
                                tipSubject = b.TipSubject ?? "",
                                worktreePath = wtPath,
                                checkedOut = !string.IsNullOrEmpty(wtPath),
                                isRemote = b.IsRemote,
                                outcome,
                                linkedTasks,
                                when = b.LastCommitTime.HasValue
                                    ? b.LastCommitTime.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                                    : null,
                            };
                        })
                        .ToArray();

                    // Per-worktree state via transient GitRepoService. Each
                    // worktree opens a fresh LibGit2Sharp Repository handle
                    // for working-tree + recent-commits, then disposes —
                    // bounded by the worktree-list count (and capped further
                    // by WorktreeListService's 16-worktree dirty-count gate
                    // for sanity).
                    var folders = new System.Collections.Generic.List<object>(worktreesSnapshot.Count);
                    foreach (var wt in worktreesSnapshot)
                    {
                        if (wt == null || string.IsNullOrEmpty(wt.Path)) continue;

                        object workingChanges;
                        object[] recentCommits;
                        try
                        {
                            using var perSvc = new GitRepoService(wt.Path);
                            workingChanges = BuildWorkingChanges(perSvc, wt.Path, attributionSvcCaptured);
                            recentCommits = BuildRecentCommits(perSvc);
                        }
                        catch
                        {
                            // Worktree path didn't open as a valid repo (pruned,
                            // permission, transient I/O). Degrade to empty so
                            // the rest of the tree still renders rather than
                            // failing the whole refresh.
                            workingChanges = EmptyWorkingChanges();
                            recentCommits = Array.Empty<object>();
                        }

                        folders.Add(new
                        {
                            path = wt.Path,
                            branch = wt.Branch ?? "",
                            isMain = wt.IsMain,
                            dirtyCount = wt.DirtyCount,
                            linkedTaskId = wt.LinkedTaskId,
                            linkedTaskTitle = wt.LinkedTaskTitle,
                            workingChanges,
                            recentCommits,
                        });
                    }

                    // Project name = parent worktree's folder name. The current
                    // worktree's folder can be a hash-y task id (e.g.
                    // `MultiTerminal-worktrees\4363577a`); the parent worktree
                    // path ends with the actual repo name (`MultiTerminal`),
                    // which is what a git-naive user expects to see at the
                    // tree root.
                    string repoName = string.Empty;
                    try
                    {
                        string sourcePath = null;
                        foreach (var wt in worktreesSnapshot)
                        {
                            if (wt != null && wt.IsMain && !string.IsNullOrEmpty(wt.Path))
                            {
                                sourcePath = wt.Path;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(sourcePath)) sourcePath = repoRoot;
                        string trimmed = sourcePath?.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
                        if (!string.IsNullOrEmpty(trimmed)) repoName = Path.GetFileName(trimmed) ?? string.Empty;
                    }
                    catch
                    {
                        repoName = string.Empty;
                    }

                    return new
                    {
                        type = "git_state_tree",
                        // repoRoot ships native-separator filesystem path so JS
                        // can compose absolute-path tooltips on file rows
                        // (smoke-1 polish [15] fix #3). Stable via captured local.
                        repoRoot,
                        repoName,
                        selectedWorktreePath,
                        branches,
                        folders = folders.ToArray(),
                    };
                });
                Send(payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HudGitRenderer.RefreshAsync] {ex.Message}");
            }
        }

        /// <summary>
        /// Per-worktree working-changes projection — same shape the old
        /// <c>git_state</c> payload carried at the top level (workingTree
        /// flat list + groupedByTask + unlinked + contamTaskIds), now scoped
        /// to a single worktree. Called inside the unified-payload
        /// <see cref="RefreshAsync"/> pass for each worktree.
        /// </summary>
        private static object BuildWorkingChanges(
            GitRepoService svc,
            string svcRepoRoot,
            GitAttributionService attributionSvc)
        {
            var status = svc.GetWorkingTreeStatus();
            var statusList = status ?? (System.Collections.Generic.IReadOnlyList<GitFileStatus>)Array.Empty<GitFileStatus>();

            // Phase 2 overlays — fetch attribution for each uncommitted file
            // (agent + task + pipeline status). Falls back to empty fields on
            // any failure so the working-changes panel still renders.
            System.Collections.Generic.IReadOnlyList<GitFileAttribution> attributions = Array.Empty<GitFileAttribution>();
            var fileList = statusList.Select(f => f.Path ?? "").ToList();
            if (attributionSvc != null && statusList.Count > 0)
            {
                try
                {
                    attributions = attributionSvc.GetAttributionForFiles(svcRepoRoot, fileList);
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
                        // "active" | "shipped" | "none" — drives the
                        // muted chip variant for files whose owning
                        // task has shipped but the file isn't committed yet.
                        linkageState = a?.LinkageState ?? "none",
                    };
                })
                .ToArray();

            // a401e082: gated on WorktreeConfig.IsEnabled. In mode=OFF,
            // stale task_file_links rows from prior cycles surface as
            // linkageState='shipped' under done-task groups and
            // misattribute fresh trunk edits to old shipped tasks.
            // When the gate is OFF, the linkage filter below matches
            // nothing and groupedByTask collapses to an empty array.
            var groupedByTask = workingTree
                .Where(f => WorktreeConfig.IsEnabled
                    && (f.linkageState == "active" || f.linkageState == "shipped"))
                .Where(f => !string.IsNullOrEmpty(f.taskId))
                .GroupBy(f => f.taskId)
                .Select(g =>
                {
                    var first = g.First();
                    return new
                    {
                        taskId = g.Key,
                        taskIdShort = g.Key.Length >= 8 ? g.Key.Substring(0, 8) : g.Key,
                        taskTitle = first.taskTitle,
                        agent = first.agent,
                        linkageState = first.linkageState,
                        fileCount = g.Count(),
                        files = g.ToArray(),
                    };
                })
                .OrderByDescending(g => g.linkageState == "active")
                .ThenBy(g => g.taskTitle, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var unlinked = workingTree
                .Where(f => WorktreeConfig.IsEnabled && f.linkageState == "none")
                .ToArray();

            // Cross-task contamination: distinct active task IDs across ALL
            // active claims (multi-claim aware). Separate query from the
            // dedup'd attribution set so the multi-claim case the banner
            // exists to flag isn't hidden (adversary HIGH).
            string[] contamTaskIds = Array.Empty<string>();
            if (attributionSvc != null && fileList.Count > 0)
            {
                try
                {
                    contamTaskIds = attributionSvc
                        .GetCrossTaskActiveTaskIds(svcRepoRoot, fileList)
                        .ToArray();
                }
                catch
                {
                    contamTaskIds = Array.Empty<string>();
                }
            }

            return new
            {
                workingTree,
                groupedByTask,
                unlinked,
                contamTaskIds,
            };
        }

        /// <summary>
        /// Per-worktree recent-commits projection. 30 commits, newest-first,
        /// matches the prior top-level <c>recentCommits</c> shape.
        /// </summary>
        private static object[] BuildRecentCommits(GitRepoService svc)
        {
            var commits = svc.GetRecentCommits(30);
            return (commits ?? (System.Collections.Generic.IReadOnlyList<GitCommitInfo>)Array.Empty<GitCommitInfo>())
                .Select(c => (object)new
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
        }

        /// <summary>
        /// Empty-but-shaped working-changes object for the degraded path
        /// (transient <see cref="GitRepoService"/> ctor threw). JS consumes
        /// the same field names regardless of state.
        /// </summary>
        private static object EmptyWorkingChanges() => new
        {
            workingTree = Array.Empty<object>(),
            groupedByTask = Array.Empty<object>(),
            unlinked = Array.Empty<object>(),
            contamTaskIds = Array.Empty<string>(),
        };

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
                // UnsubscribeCurrent only releases _currentService.RepoStateChanged.
                // The broker-owned BranchOutcomeUpdated event was wired in Initialize
                // and must be released here too — otherwise the broker holds a strong
                // reference to this disposed renderer for its (app-long) lifetime.
                if (_broker != null)
                {
                    try { _broker.BranchOutcomeUpdated -= OnBranchOutcomeUpdated; } catch { /* non-fatal */ }
                }
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
