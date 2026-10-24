using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.DashboardHeader;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.ProjectSettings
{
    /// <summary>What the project settings form needs from the app. Built once by MainForm.</summary>
    internal sealed class ProjectSettingsContext
    {
        public ProjectDatabase ProjectDb { get; set; }
        public ProjectSettingsService Service { get; set; }
        public Func<GatewayIntegrationService> Gateway { get; set; }

        /// <summary>Syncs the project's MCP config (gateway profile, stale .mcp.json removal). Returns null on success, else why not.</summary>
        public Func<string, string, string> SyncMcpConfig { get; set; }

        /// <summary>Confirms with the user and deletes the project. Returns true when it was deleted.</summary>
        public Func<IWin32Window, string, bool> DeleteProject { get; set; }

        /// <summary>Called after a save wrote something, with the project id, so the app can refresh what it shows.</summary>
        public Action<string> ProjectSaved { get; set; }

        public DebugLogService Log { get; set; }
    }

    /// <summary>
    /// The project settings form (ticket e20e2ce0): one window per project, opened by the gear on the
    /// terminal status bar, the Home cards and the Browse All rows. Hosts ProjectSettings/project-settings.html
    /// in WebView2; settings sit in groups down a left rail and are saved together through
    /// <see cref="ProjectSettingsService"/>. It replaced EditProjectDialog, the Project Manager dialog and the
    /// Project pane's editor.
    /// </summary>
    internal sealed class ProjectSettingsForm : Form
    {
        private const string SettingsKeyX = "projectSettings.X";
        private const string SettingsKeyY = "projectSettings.Y";
        private const string SettingsKeyW = "projectSettings.W";
        private const string SettingsKeyH = "projectSettings.H";

        private static readonly Dictionary<string, ProjectSettingsForm> OpenForms = new Dictionary<string, ProjectSettingsForm>(StringComparer.Ordinal);

        private static readonly HashSet<string> IgnoreFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", "node_modules", ".git", ".vs", "packages", "__pycache__", ".idea", ".svn", ".hg" };

        private readonly string _projectId;
        private readonly ProjectSettingsContext _context;
        private readonly WebView2 _webView;
        private string _pageUri;
        private bool _isDark;
        private bool _dirty;
        private bool _closeConfirmed;
        private string _projectPath;

        // What the page was last loaded from. A save writes only what differs from it, and refuses when
        // what the user changed was also changed elsewhere since (ProjectSettingsBaseline).
        private ProjectSettingsBaseline _baseline;

        /// <summary>
        /// Opens the settings form for <paramref name="projectId"/>, or brings the one already open to the front.
        /// </summary>
        public static void Open(IWin32Window owner, string projectId, ProjectSettingsContext context, bool isDark)
        {
            if (string.IsNullOrWhiteSpace(projectId) || context?.ProjectDb == null || context.Service == null)
                return;

            if (OpenForms.TryGetValue(projectId, out var existing) && !existing.IsDisposed)
            {
                if (existing.WindowState == FormWindowState.Minimized)
                    existing.WindowState = FormWindowState.Normal;
                existing.Activate();
                return;
            }

            var form = new ProjectSettingsForm(projectId, context, isDark);
            OpenForms[projectId] = form;
            form.Show(owner);
        }

        /// <summary>Follows a theme change in every open settings form.</summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            foreach (var form in OpenForms.Values.Where(f => !f.IsDisposed).ToList())
                form.ApplyTheme(isDark);
        }

        /// <summary>Closes the open settings form for a project that was just deleted elsewhere.</summary>
        public static void CloseForDeletedProject(string projectId)
        {
            if (projectId != null && OpenForms.TryGetValue(projectId, out var form) && !form.IsDisposed)
            {
                form._closeConfirmed = true;
                form.Close();
            }
        }

        private ProjectSettingsForm(string projectId, ProjectSettingsContext context, bool isDark)
        {
            _projectId = projectId;
            _context = context;
            _isDark = isDark;

            Text = "Project Settings";
            StartPosition = FormStartPosition.CenterParent;
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(720, 480);
            ShowInTaskbar = true;
            ApplyThemeColors();
            ApplyPersistedBounds();

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            Load += async (s, e) => await InitializeWebViewAsync();
            FormClosing += OnFormClosing;
            FormClosed += OnFormClosed;
        }

        private void ApplyThemeColors() =>
            BackColor = _isDark ? Color.FromArgb(26, 26, 46) : Color.FromArgb(248, 250, 252); // the page's --bg-primary

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            ApplyThemeColors();
            Post(new { type = "theme", theme = isDark ? "dark" : "light" });
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;

                string htmlPath = Path.Combine(AppContext.BaseDirectory, "ProjectSettings", "project-settings.html");
                _pageUri = new Uri(htmlPath).AbsoluteUri;
                WebViewPagePin.Pin(_webView, _pageUri);
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.Navigate(_pageUri);
            }
            catch (Exception ex)
            {
                _context.Log?.Error("ProjectSettings", $"WebView2 init failed: {ex.Message}");
                MessageBox.Show(this, $"The project settings form could not start: {ex.Message}", "Project Settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _closeConfirmed = true;
                Close();
            }
        }

        private void Post(object message)
        {
            try
            {
                if (_webView?.CoreWebView2 != null)
                    _webView.CoreWebView2.PostWebMessageAsJson(message as string ?? JsonSerializer.Serialize(message));
            }
            catch (Exception ex)
            {
                _context.Log?.Error("ProjectSettings", $"Post failed: {ex.Message}");
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Only the form's own page may drive it (same rule as the header popups, task 4cac608c).
            if (!WebViewPagePin.IsFromPage(e.Source, _pageUri))
                return;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;
            }

            string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    SendLoad();
                    break;
                case "save":
                    Save(root);
                    break;
                case "reload":
                    _dirty = false;
                    SendLoad();
                    break;
                case "dirty":
                    _dirty = root.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True;
                    break;
                case "browseFolder":
                    BrowseFolder(root.TryGetProperty("field", out var f) ? f.GetString() : null,
                                 root.TryGetProperty("current", out var c) ? c.GetString() : null);
                    break;
                case "copyPath":
                    if (!string.IsNullOrEmpty(_projectPath))
                    {
                        try { Clipboard.SetText(_projectPath); Notice("Path copied."); }
                        catch (System.Runtime.InteropServices.ExternalException) { Notice("The clipboard is busy; try again.", "error"); }
                    }
                    break;
                case "openFolder":
                    OpenFolder();
                    break;
                case "syncMcp":
                    SyncMcp();
                    break;
                case "deleteProject":
                    DeleteProject();
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        private void Notice(string text, string kind = "info") => Post(new { type = "notice", text, kind });

        // ── Load ─────────────────────────────────────────────────────────────────

        private void SendLoad()
        {
            var project = _context.ProjectDb.GetRichProject(_projectId);
            if (project == null)
            {
                Post(new { type = "missing", text = "This project no longer exists." });
                return;
            }

            _projectPath = project.Path;
            Text = $"Project Settings: {project.Name}";

            ProjectContext lists = null;
            try { lists = new ProjectContextService(_context.ProjectDb).GetProjectContext(_projectId); }
            catch (Exception ex) { _context.Log?.Error("ProjectSettings", $"Loading project lists failed: {ex.Message}"); }

            // Lists that failed to load stay null in the baseline, and the save never writes a list whose
            // baseline is null: the page shows it empty, and saving that would delete every stored row.
            _baseline = new ProjectSettingsBaseline
            {
                Project = project,
                Agents = lists?.Agents,
                SpecialistAgents = lists?.SpecialistAgents,
                McpServers = lists?.McpServers,
                Skills = lists?.Skills,
                Paths = lists?.Paths,
                Prompts = lists?.Prompts,
            };

            Post(ProjectSettingsPayload.BuildLoad(project, lists, GatherOptions(project), _isDark));
            if (lists == null)
                Notice("The project's agents, MCP servers, skills, paths and prompts could not be loaded, so changes to them will not be saved. Close and reopen the form to try again.", "error");
            _ = SendStatsAsync(project.Path);
        }

        private async Task SendStatsAsync(string projectPath)
        {
            var (files, lines, days) = await Task.Run(() => CalculateStats(projectPath));
            if (!IsDisposed)
                Post(new { type = "stats", fileCount = files, linesOfCode = lines, daysOld = days });
        }

        // Moved from the retired Project pane: a capped scan (first 1000 source files), not a precise count.
        private (int Files, int Lines, int Days) CalculateStats(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return (0, 0, 0);
            int files = 0, lines = 0, days = 0;
            try
            {
                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".cs", ".js", ".ts", ".html", ".css", ".json", ".xml", ".md", ".py", ".clw", ".inc", ".equ" };
                var candidates = Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var dir = Path.GetDirectoryName(f);
                        return !IgnoreFolders.Any(ig => dir.Contains(Path.DirectorySeparatorChar + ig + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                                         dir.EndsWith(Path.DirectorySeparatorChar + ig, StringComparison.OrdinalIgnoreCase));
                    })
                    .Where(f => extensions.Contains(Path.GetExtension(f)))
                    .Take(1000);
                foreach (var file in candidates)
                {
                    files++;
                    try { lines += File.ReadLines(file).Count(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                var projectJson = Path.Combine(projectPath, ".claude", "project.json");
                if (File.Exists(projectJson))
                    days = (int)(DateTime.Now - File.GetCreationTime(projectJson)).TotalDays;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _context.Log?.Warning("ProjectSettings", $"Stats scan stopped early: {ex.Message}");
            }
            return (files, lines, days);
        }

        private ProjectSettingsOptions GatherOptions(Project project)
        {
            var options = new ProjectSettingsOptions();

            IReadOnlyList<ClaudeProfile> profiles = null;
            try { profiles = ClaudeProfileResolver.GetAllProfiles(); }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Claude profiles unreadable: {ex.Message}"); }
            options.ClaudeProfiles = ClaudeProfileChoices.Build(profiles, project.ClaudeProfile, out _);

            try { options.TeamLeads = _context.ProjectDb.GetTeamLeadProfiles().Select(p => (p.Id, p.DisplayName)).ToList(); }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Team leads unreadable: {ex.Message}"); }

            try
            {
                using var accounts = new SourceControlAccountService();
                var all = accounts.GetAll();
                options.SourceAccounts = all.Select(a => (a.Id, a.DisplayName)).ToList();

                // Whether the GitHub watch can run, and its last check (task 92eeeb47).
                var linked = all.FirstOrDefault(a => string.Equals(a.Id, project.SourceControlAccountId, StringComparison.Ordinal));
                var repo = GitHubRepoRef.TryParse(project.GitRepoUrl);
                var (lastChecked, lastError) = repo == null ? ((DateTime?)null, (string)null)
                    : _context.ProjectDb.GetGitHubWatchLastCheck(project.Id, repo.FullName);
                options.GitHubWatchStatus = GitHubWatchService.DescribeStatus(
                    project, linked?.Provider, linked?.HasToken == true, lastChecked, lastError);
            }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Source accounts unreadable: {ex.Message}"); }

            try { options.AvailableAgents = _context.ProjectDb.GetAllProfileSummaries().Select(p => (p.DisplayName, p.Role, p.PreferredModel)).ToList(); }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Agent profiles unreadable: {ex.Message}"); }

            try
            {
                var gateway = _context.Gateway?.Invoke();
                options.AvailableMcpServers = (gateway?.GetAllGatewayServers() ?? new List<GatewayServerDto>())
                    .Select(s => (s.Name, s.Description)).ToList();
            }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Gateway servers unreadable: {ex.Message}"); }

            // User-level skills and agents come from the Claude config folder of the profile THIS project runs
            // under (ticket c8ee0eec), never another profile's.
            var configDirs = new List<string>();
            try
            {
                var root = ClaudeProfileDataRoots.ForProject(project.Id, project.SourcePath ?? project.Path);
                if (root != null) configDirs.Add(root.ConfigDir);
            }
            catch (Exception ex) { _context.Log?.Warning("ProjectSettings", $"Claude config folder unresolved: {ex.Message}"); }

            var skills = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var specialists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in configDirs)
            {
                AddSkillNames(Path.Combine(dir, "skills"), skills);
                ScanAgentsDirectory(Path.Combine(dir, "agents"), specialists);
            }
            var projectPath = project.SourcePath ?? project.Path;
            if (!string.IsNullOrEmpty(projectPath))
                ScanAgentsDirectory(Path.Combine(projectPath, ".claude", "agents"), specialists);

            options.AvailableSkills = skills.ToList();
            options.AvailableSpecialists = specialists.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value)).ToList();
            return options;
        }

        private static void AddSkillNames(string skillsDir, SortedSet<string> into)
        {
            try
            {
                if (!Directory.Exists(skillsDir)) return;
                foreach (var dir in Directory.GetDirectories(skillsDir))
                    into.Add(Path.GetFileName(dir));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // Moved from the retired Project pane: an agent's description is the first non-blank line of its .md.
        private static void ScanAgentsDirectory(string agentsDir, Dictionary<string, string> into)
        {
            try
            {
                if (string.IsNullOrEmpty(agentsDir) || !Directory.Exists(agentsDir)) return;
                foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
                {
                    string desc = "";
                    try
                    {
                        foreach (var raw in File.ReadLines(file))
                        {
                            var line = raw.Trim().TrimStart('#').Trim();
                            if (line.Length == 0) continue;
                            desc = line.Length > 80 ? line.Substring(0, 80) + "..." : line;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    into[Path.GetFileNameWithoutExtension(file)] = desc;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // ── Actions ──────────────────────────────────────────────────────────────

        private void Save(JsonElement root)
        {
            ProjectSettingsSaveResult result;
            try
            {
                var update = ProjectSettingsPayload.ParseSave(_projectId, root);
                update.Baseline = _baseline;
                result = _context.Service.Save(update);
            }
            catch (Exception ex)
            {
                _context.Log?.Error("ProjectSettings", $"Save failed for {_projectId}: {ex}");
                result = new ProjectSettingsSaveResult { Success = false, Error = ex.Message };
            }

            if (result.Success)
                _dirty = false;

            // A save whose rename failed after the other settings committed still changed something.
            if (result.Success || result.ChangedFields.Count > 0)
            {
                try { _context.ProjectSaved?.Invoke(_projectId); }
                catch (Exception ex) { _context.Log?.Error("ProjectSettings", $"Refresh after save failed: {ex.Message}"); }
            }

            Post(new { type = "saveResult", success = result.Success, error = result.Error, warnings = result.Warnings, nextLaunch = result.AppliesAtNextLaunch, conflict = result.Conflict });

            // Reload so the page shows what is stored now (normalized values, new row ids).
            if (result.Success)
                SendLoad();
        }

        private void BrowseFolder(string field, string current)
        {
            // Only the path fields have a Browse button; anything else is ignored.
            var allowed = new[] { "sourcePath", "deployPath", "buildOutputPath" };
            if (field == null || !allowed.Contains(field, StringComparer.Ordinal))
                return;

            using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a folder" };
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dialog.SelectedPath = current;
            else if (!string.IsNullOrWhiteSpace(_projectPath) && Directory.Exists(_projectPath))
                dialog.SelectedPath = _projectPath;

            if (dialog.ShowDialog(this) == DialogResult.OK)
                Post(new { type = "folderPicked", field, path = dialog.SelectedPath });
        }

        private void OpenFolder()
        {
            if (string.IsNullOrEmpty(_projectPath) || !Directory.Exists(_projectPath))
            {
                Notice("The project folder does not exist.", "error");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_projectPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Notice($"Could not open the folder: {ex.Message}", "error");
            }
        }

        private void SyncMcp()
        {
            if (_dirty)
            {
                Notice("Save your changes first, so the sync uses the MCP servers you chose.", "error");
                return;
            }
            var project = _context.ProjectDb.GetRichProject(_projectId);
            string error = _context.SyncMcpConfig?.Invoke(_projectId, project?.SourcePath ?? project?.Path) ?? "MCP config sync is not available.";
            if (error == null) Notice("MCP config synced.");
            else Notice($"MCP config sync failed: {error}", "error");
        }

        private void DeleteProject()
        {
            if (_context.DeleteProject == null) return;
            if (_context.DeleteProject(this, _projectId))
            {
                _closeConfirmed = true;
                Close();
            }
        }

        // ── Window state ─────────────────────────────────────────────────────────

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_dirty || _closeConfirmed || e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.WindowsShutDown)
                return;

            var answer = MessageBox.Show(this, "You have unsaved changes. Close without saving them?", "Project Settings",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                e.Cancel = true;
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            SaveBounds();
            if (OpenForms.TryGetValue(_projectId, out var registered) && ReferenceEquals(registered, this))
                OpenForms.Remove(_projectId);
            try
            {
                if (_webView?.CoreWebView2 != null)
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            catch (InvalidOperationException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _webView?.Dispose();
            base.Dispose(disposing);
        }

        private void ApplyPersistedBounds()
        {
            try
            {
                var s = SettingsService.Default;
                if (int.TryParse(s.Get(SettingsKeyX), out int x) && int.TryParse(s.Get(SettingsKeyY), out int y) &&
                    int.TryParse(s.Get(SettingsKeyW), out int w) && int.TryParse(s.Get(SettingsKeyH), out int h) &&
                    w >= MinimumSize.Width && h >= MinimumSize.Height)
                {
                    var bounds = new Rectangle(x, y, w, h);
                    if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds)))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = bounds;
                    }
                }
            }
            catch (Exception ex)
            {
                _context?.Log?.Warning("ProjectSettings", $"Restoring the window position failed: {ex.Message}");
            }
        }

        private void SaveBounds()
        {
            try
            {
                var s = SettingsService.Default;
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                s.BeginBatch();
                s.Set(SettingsKeyX, b.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.Set(SettingsKeyY, b.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.Set(SettingsKeyW, b.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.Set(SettingsKeyH, b.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.EndBatch();
            }
            catch (Exception ex)
            {
                _context.Log?.Warning("ProjectSettings", $"Saving the window position failed: {ex.Message}");
            }
        }
    }
}
