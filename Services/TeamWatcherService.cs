using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Event args for when a native Claude Code teammate is discovered.
    /// </summary>
    public class TeammateDiscoveredEventArgs : EventArgs
    {
        public string TeamName { get; set; }
        public string MemberName { get; set; }
        public IAgentMessageSource Tailer { get; set; }
        public string SpawnerName { get; set; }
        public string TaskDescription { get; set; }
        public string SubagentType { get; set; }
        /// <summary>
        /// The team lead's working directory, used as fallback for spawner
        /// resolution when SpawnerName is null (match against terminal cwds).
        /// </summary>
        public string LeadCwd { get; set; }
        /// <summary>
        /// The parent session ID extracted from the subagent's transcript path.
        /// Used to resolve the spawner terminal when SpawnerName is unknown.
        /// </summary>
        public string ParentSessionId { get; set; }
        /// <summary>
        /// The Claude config directory (canonical, trailing separator) this team or transcript was
        /// found under. Roots come only from <see cref="ClaudeProfileResolver.GetAllRoots()"/>.
        /// </summary>
        public string ConfigRoot { get; set; }
        /// <summary>
        /// The team's identity across profiles (see <see cref="TeamWatcherService.BuildTeamKey"/>):
        /// the bare team name under the Default root, name plus root under any other. Subscribers
        /// that key anything on a team must use this, never <see cref="TeamName"/>, because the same
        /// team name can exist under two profiles. Null for a non-team subagent.
        /// </summary>
        public string TeamKey { get; set; }
        /// <summary>
        /// For a non-team subagent: the key of its panel
        /// (<see cref="TeamWatcherService.BuildSubagentPanelKey"/>), which carries the config root for
        /// any profile but Default, so two profiles can each have a subagent of the same name. Null
        /// for a team member.
        /// </summary>
        public string SubagentPanelKey { get; set; }
    }

    /// <summary>
    /// Event args for when a native Claude Code team is removed/deleted.
    /// </summary>
    public class TeamRemovedEventArgs : EventArgs
    {
        public string TeamName { get; set; }
        public List<string> MemberNames { get; set; } = new();
        /// <summary>
        /// All transcript paths that were tracked for this team's agents.
        /// Used by MainForm to find and clean up orphan panels by transcript path match.
        /// </summary>
        public List<string> TranscriptPaths { get; set; } = new();
        /// <summary>The Claude config directory (canonical, trailing separator) the team lived under.</summary>
        public string ConfigRoot { get; set; }
        /// <summary>True when <see cref="ConfigRoot"/> is the Default profile's directory (its subagent
        /// panel keys carry no root; see <see cref="TeamWatcherService.BuildSubagentPanelKey"/>).</summary>
        public bool IsDefaultRoot { get; set; }
        /// <summary>The team's identity across profiles; see <see cref="TeammateDiscoveredEventArgs.TeamKey"/>.</summary>
        public string TeamKey { get; set; }
    }

    /// <summary>One Claude config directory the team watcher covers, and the folders it watches there.</summary>
    /// <param name="Root">The config directory, canonical with a trailing separator.</param>
    /// <param name="TeamsDir">The root's teams folder.</param>
    /// <param name="ProjectsDir">The root's projects folder.</param>
    /// <param name="AppProjectDir">The folder under ProjectsDir for the slug of MT's own working directory.</param>
    /// <param name="IsDefault">True for the Default profile's directory.</param>
    internal sealed record TeamWatchRoot(string Root, string TeamsDir, string ProjectsDir, string AppProjectDir, bool IsDefault);

    /// <summary>What the watcher is told to cover: every configured root, and which one is Default's.</summary>
    internal sealed record TeamWatchRootSet(IReadOnlyList<string> Roots, string DefaultRoot);

    /// <summary>
    /// Watches teams/ under every configured Claude profile folder for native Claude Code agent teams and creates
    /// TranscriptTailer instances for each discovered teammate. This is the
    /// "tmux replacement" — making invisible in-process teammates visible
    /// in MultiTerminal's Agent Panel.
    ///
    /// File structure watched:
    ///   ~/.claude/teams/{team-name}/config.json     → team config with members
    ///   ~/.claude/teams/{team-name}/inboxes/{name}.json → per-agent inbox
    ///   ~/.claude/projects/{slug}/{session}/subagents/agent-{id}.jsonl → transcripts
    ///
    /// Ticket c8ee0eec item 12: "~/.claude" above stands for EACH root in
    /// <see cref="ClaudeProfileResolver.GetAllRoots()"/>. A pane running under a second profile writes
    /// its teams and projects folders under that profile's folder. Rules:
    ///   - Roots come only from the resolver; nothing else is ever watched. The set is re-read every
    ///     <see cref="DefaultRefreshInterval"/> (there is no settings-changed signal to hook), which is
    ///     also what picks up a profile folder that did not exist yet and drops a removed profile.
    ///   - A team is keyed on (root, name), never on the name alone: see <see cref="BuildTeamKey"/>.
    ///   - A team's transcripts are looked for under the SAME root its config.json was found in. The
    ///     lead's Claude Code process wrote both, so that root is the lead pane's profile by
    ///     construction; no terminal lookup is involved.
    /// </summary>
    public class TeamWatcherService : IDisposable
    {
        /// <summary>How often the configured root set is re-read from the resolver.</summary>
        internal static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(30);

        private readonly string _projectSlug;
        private readonly Func<TeamWatchRootSet> _rootsProvider;
        private readonly TimeSpan _refreshInterval;

        // Guards _roots and the add/remove of whole roots. Order: _rootsLock, then RootState.Sync,
        // then _trackedSubagentFiles. Watcher callbacks take only the last two.
        private readonly object _rootsLock = new object();
        private readonly Dictionary<string, RootState> _roots = new Dictionary<string, RootState>(StringComparer.OrdinalIgnoreCase);
        private Timer _refreshTimer;
        private volatile bool _started;

        // Keyed on BuildTeamKey(root, name), NOT the team name: two profiles can each have a team
        // called "review", and they must never share a TeamState. Concurrent because each root's
        // watchers call in on their own threads: two roots can add a team at the same instant.
        // That covers THIS MAP ONLY (adding, finding and removing a TeamState). The collections inside
        // a TeamState are plain ones, read and changed from watcher callbacks, timers and cleanup with
        // no lock of their own.
        private readonly ConcurrentDictionary<string, TeamState> _activeTeams = new ConcurrentDictionary<string, TeamState>();

        private volatile bool _disposed;

        // Project-level subagent watching (non-team agents)
        private readonly HashSet<string> _trackedSubagentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TranscriptTailer> _orphanTailers = new Dictionary<string, TranscriptTailer>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _watchedProjectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track agent names from recently-deleted teams to prevent orphan watcher from
        // creating stale panels. Entries auto-expire after 60 seconds. Keyed on RetiredKey(root, name):
        // a team deleted under one profile must not hide a same-named subagent of another.
        private readonly ConcurrentDictionary<string, DateTime> _retiredTeamAgentNames = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<TeammateDiscoveredEventArgs> TeammateDiscovered;
        public event EventHandler<TeamRemovedEventArgs> TeamRemoved;
        public event EventHandler<(string TeamName, string MemberName, string TeamKey)> MemberRemoved;
        public event EventHandler<TeammateDiscoveredEventArgs> SubagentDiscovered;
        public event EventHandler<(string TeamName, string MemberName, AgentMessage Message, string SpawnerName)> TeamMessageSent;

        /// <summary>
        /// Optional debug log service for MCP-visible logging.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        private void Log(string msg)
        {
            DebugLogService?.Trace("TeamWatcher", msg);
        }

        private void LogInfo(string msg)
        {
            DebugLogService?.Info("TeamWatcher", msg);
        }

        private void LogError(string msg)
        {
            DebugLogService?.Error("TeamWatcher", msg);
        }

        public TeamWatcherService(string projectSlug)
            : this(projectSlug, ResolverRoots, DefaultRefreshInterval)
        {
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo): the root set comes from <paramref name="rootsProvider"/>
        /// instead of the resolver, and a zero <paramref name="refreshInterval"/> means no timer (the
        /// test calls <see cref="RefreshRoots"/> itself).
        /// </summary>
        internal TeamWatcherService(string projectSlug, Func<TeamWatchRootSet> rootsProvider, TimeSpan refreshInterval)
        {
            _projectSlug = projectSlug;
            _rootsProvider = rootsProvider ?? throw new ArgumentNullException(nameof(rootsProvider));
            _refreshInterval = refreshInterval;
        }

        /// <summary>The production root set: every configured profile folder, from the resolver only.</summary>
        private static TeamWatchRootSet ResolverRoots()
        {
            string defaultDir = ClaudeProfileResolver.GetConfigDir(null);
            return new TeamWatchRootSet(
                ClaudeProfileResolver.GetAllRoots(),
                defaultDir == null ? null : defaultDir + Path.DirectorySeparatorChar);
        }

        // ── Pure decisions (unit-tested) ──────────────────────────────────────

        /// <summary>
        /// Pure: the folders to watch for a list of config roots. Blank roots are dropped, a missing
        /// trailing separator is added, and duplicates (case-insensitive) collapse to the first. Nothing
        /// is added that is not in <paramref name="roots"/>.
        /// </summary>
        internal static IReadOnlyList<TeamWatchRoot> BuildWatchPlan(IReadOnlyList<string> roots, string defaultRoot, string projectSlug)
        {
            var plan = new List<TeamWatchRoot>();
            if (roots == null) return plan;

            string normalizedDefault = WithTrailingSeparator(defaultRoot);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in roots)
            {
                string root = WithTrailingSeparator(raw);
                if (root == null || !seen.Add(root)) continue;

                string projectsDir = Path.Combine(root, "projects");
                plan.Add(new TeamWatchRoot(
                    root,
                    Path.Combine(root, "teams"),
                    projectsDir,
                    Path.Combine(projectsDir, projectSlug ?? "unknown"),
                    normalizedDefault != null && string.Equals(root, normalizedDefault, StringComparison.OrdinalIgnoreCase)));
            }
            return plan;
        }

        /// <summary>
        /// Pure: a team's identity. Under the Default root it is the bare team name, so everything
        /// keyed on it behaves exactly as it did before profiles existed. Under any other root it is
        /// <c>name|root</c>. '|' cannot appear in a Windows folder name, so a non-default key can
        /// never equal a default one.
        /// </summary>
        internal static string BuildTeamKey(string root, string teamName, bool isDefaultRoot) =>
            isDefaultRoot ? teamName : teamName + "|" + root;

        /// <summary>Prefix of every non-team subagent panel key.</summary>
        internal const string SubagentPanelPrefix = "subagent:";

        /// <summary>
        /// Pure: the panel key of a non-team subagent. The same (root, name) rule as
        /// <see cref="BuildTeamKey"/>: <c>subagent:name</c> under the Default root (as before profiles
        /// existed), <c>subagent:name|root</c> under any other, so a same-named subagent under a second
        /// profile gets its own panel instead of being taken for the first one's.
        /// </summary>
        internal static string BuildSubagentPanelKey(string root, string memberName, bool isDefaultRoot) =>
            SubagentPanelPrefix + BuildTeamKey(root, memberName, isDefaultRoot);

        /// <summary>
        /// Pure: when <paramref name="panelKey"/> is a subagent panel key that belongs to
        /// <paramref name="root"/>, its subagent name; else null. A key with no root part belongs to
        /// the Default root and to no other. The root is what follows the LAST '|': a root cannot
        /// contain one, a name could. (A Default-root subagent whose name contains '|' therefore reads
        /// as belonging to no configured root and is matched by nothing here; a removed team still
        /// finds its panel by transcript path.)
        /// </summary>
        internal static string SubagentNameIfUnderRoot(string panelKey, string root, bool isDefaultRoot)
        {
            if (panelKey == null || !panelKey.StartsWith(SubagentPanelPrefix, StringComparison.Ordinal))
                return null;

            string rest = panelKey.Substring(SubagentPanelPrefix.Length);
            int bar = rest.LastIndexOf('|');
            if (bar < 0)
                return isDefaultRoot ? rest : null;

            return !isDefaultRoot && string.Equals(rest.Substring(bar + 1), root, StringComparison.OrdinalIgnoreCase)
                ? rest.Substring(0, bar)
                : null;
        }

        /// <summary>
        /// Pure: which orphan subagent panels a removed team takes with it. Only panels under the
        /// team's OWN root (<see cref="SubagentNameIfUnderRoot"/>) are candidates, so removing one
        /// profile's team never closes another profile's subagent panels. Within the root a panel
        /// matches a member by name, in either direction and ignoring case ("Agent Alice" for member
        /// "Alice", or the reverse), as it did before profiles existed.
        /// </summary>
        internal static List<string> SelectOrphanSubagentPanels(IEnumerable<string> panelKeys, IEnumerable<string> memberNames, string root, bool isDefaultRoot)
        {
            var selected = new List<string>();
            var names = (memberNames ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (names.Count == 0 || panelKeys == null)
                return selected;

            foreach (string key in panelKeys)
            {
                string orphanName = SubagentNameIfUnderRoot(key, root, isDefaultRoot);
                if (string.IsNullOrEmpty(orphanName))
                    continue;

                if (names.Any(name => orphanName.Contains(name, StringComparison.OrdinalIgnoreCase)
                                      || name.Contains(orphanName, StringComparison.OrdinalIgnoreCase)))
                {
                    selected.Add(key);
                }
            }

            return selected;
        }

        /// <summary>
        /// Pure: where the transcripts of a session started in <paramref name="leadCwd"/> live for the
        /// profile whose config directory is <paramref name="root"/>.
        /// </summary>
        internal static string GetLeadProjectDir(string root, string leadCwd) =>
            Path.Combine(root, "projects", GetClaudeProjectSlug(leadCwd));

        /// <summary>
        /// The config root (trailing separator) of the profile a pane was launched under, for comparing
        /// with an event's ConfigRoot. Null or blank = Default. Returns null when that profile cannot be
        /// resolved (removed from Settings, folder gone): the caller must then treat the pane as
        /// belonging to no known root rather than to Default.
        /// <para>The pane-to-folder answer is <see cref="ClaudeProfileDataRoots.ForProfileName(string)"/>,
        /// the one resolver every session-data reader uses (it also requires the folder to exist and
        /// logs a skip once); this only adds the separator the watcher's roots carry.</para>
        /// </summary>
        public static string GetConfigRootForProfileName(string profileName) =>
            GetConfigRootForProfileName(profileName, ClaudeProfileResolver.CurrentContext());

        internal static string GetConfigRootForProfileName(string profileName, ClaudeProfileContext ctx)
        {
            var root = ClaudeProfileDataRoots.ForProfileName(profileName, ctx);
            return root == null ? null : root.ConfigDir + Path.DirectorySeparatorChar;
        }

        private static string WithTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        }

        private static string RetiredKey(RootState root, string agentName) => root.Plan.Root + "\n" + agentName;

        private bool Gone(RootState root) => _disposed || root.Stopped;

        private static bool IsUnder(RootState root, string path) =>
            path != null && path.StartsWith(root.Plan.Root, StringComparison.OrdinalIgnoreCase);

        // ── Root lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Start watching for native agent teams under every configured root, and keep the root set
        /// current. Only NEW team directories are reacted to; nothing from a previous session is restored.
        /// </summary>
        public void StartWatching()
        {
            if (_disposed) return;
            _started = true;

            RefreshRoots();

            if (_refreshInterval > TimeSpan.Zero && _refreshTimer == null)
                _refreshTimer = new Timer(_ => RefreshRootsFromTimer(), null, _refreshInterval, _refreshInterval);
        }

        /// <summary>
        /// The timer's entry point. An exception that leaves a timer callback ends the process, so
        /// whatever a refresh throws (a subscriber of TeamRemoved, a watcher that will not dispose) is
        /// logged here and the next tick tries again.
        /// </summary>
        private void RefreshRootsFromTimer()
        {
            try
            {
                RefreshRoots();
            }
#pragma warning disable CA1031 // Timer thread: any escape terminates MultiTerminal.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogError($"Refreshing the Claude profile folders failed; will retry: {ex.Message}");
            }
        }

        /// <summary>
        /// Raise TeamRemoved for teams taken down while <c>_rootsLock</c> was held, now that it is not:
        /// a subscriber must not run inside the lock. One failing subscriber does not stop the rest.
        /// </summary>
        private void RaiseTeamsRemoved(List<TeamRemovedEventArgs> removed)
        {
            foreach (var args in removed)
            {
                try
                {
                    TeamRemoved?.Invoke(this, args);
                }
#pragma warning disable CA1031 // A subscriber's failure must not leave the other removed teams' panels open, nor reach the timer thread.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogError($"A TeamRemoved subscriber failed for team '{args.TeamName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Re-read the configured roots and reconcile: stop watching a root that is no longer
        /// configured (its teams are removed and its panels closed), start a new one, and retry the
        /// parts of a known root whose folders did not exist last time. Runs on a timer; safe to call
        /// directly (e.g. from a future settings-changed signal). A run that overlaps another is skipped.
        /// </summary>
        public void RefreshRoots()
        {
            if (_disposed || !_started) return;
            if (!Monitor.TryEnter(_rootsLock)) return;
            var removed = new List<TeamRemovedEventArgs>();
            try
            {
                if (_disposed || !_started) return;

                IReadOnlyList<TeamWatchRoot> plan;
                try
                {
                    var set = _rootsProvider();
                    plan = BuildWatchPlan(set?.Roots, set?.DefaultRoot, _projectSlug);
                }
                catch (Exception ex)
                {
                    // Keep what we have: a settings read that failed says nothing about which roots exist.
                    LogError($"Could not read the Claude profile folders; keeping the current set: {ex.Message}");
                    return;
                }

                var wanted = new HashSet<string>(plan.Select(p => p.Root), StringComparer.OrdinalIgnoreCase);
                foreach (var stale in _roots.Values.Where(r => !wanted.Contains(r.Plan.Root)).ToList())
                {
                    _roots.Remove(stale.Plan.Root);
                    try
                    {
                        StopRoot(stale, removed);
                        LogInfo($"Stopped watching (no longer a configured Claude profile folder): {stale.Plan.Root}");
                    }
#pragma warning disable CA1031 // One root that will not stop cleanly must not keep the others from being reconciled (or reach the timer thread).
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogError($"Error while stopping the watchers for '{stale.Plan.Root}': {ex.Message}");
                    }
                }

                foreach (var entry in plan)
                {
                    if (!_roots.TryGetValue(entry.Root, out var root))
                    {
                        root = new RootState(entry);
                        _roots[entry.Root] = root;
                        Log($"Teams dir: {entry.TeamsDir}");
                        Log($"Project dir: {entry.AppProjectDir}");
                    }
                    StartRoot(root);
                }
            }
            finally
            {
                Monitor.Exit(_rootsLock);
                RaiseTeamsRemoved(removed);
            }
        }

        /// <summary>
        /// Idempotent: start whatever part of this root is not running yet. A root whose folder does not
        /// exist (a profile never used) is skipped and logged once; the next refresh tries again.
        /// </summary>
        private void StartRoot(RootState root)
        {
            try
            {
                lock (root.Sync)
                {
                    if (Gone(root)) return;

                    if (!Directory.Exists(root.Plan.Root))
                    {
                        root.TeamsSeenMissing = true;
                        if (!root.MissingLogged)
                        {
                            root.MissingLogged = true;
                            LogInfo($"Claude profile folder does not exist yet; it will be watched once it appears: {root.Plan.Root}");
                        }
                        return;
                    }
                    root.MissingLogged = false;

                    EnsureTeamsWatching(root);
                    EnsureProjectSubagentWatching(root);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error starting watchers for '{root.Plan.Root}': {ex.Message}");
            }
        }

        /// <summary>Caller holds <c>root.Sync</c>.</summary>
        private void EnsureTeamsWatching(RootState root)
        {
            if (root.TeamsStarted) return;
            string teamsDir = root.Plan.TeamsDir;

            if (!Directory.Exists(teamsDir))
            {
                root.TeamsSeenMissing = true;
                if (root.TeamsRootWatcher != null) return; // already waiting for it

                Log($" Teams directory does not exist yet: {teamsDir}");

                // Watch for the directory to be created
                var pending = new FileSystemWatcher(Path.TrimEndingDirectorySeparator(root.Plan.Root))
                {
                    Filter = "teams",
                    NotifyFilter = NotifyFilters.DirectoryName
                };
                pending.Created += (s, e) =>
                {
                    if (Gone(root)) return;
                    try
                    {
                        lock (root.Sync)
                        {
                            if (!Gone(root)) EnsureTeamsWatching(root);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error starting teams watcher for '{teamsDir}': {ex.Message}");
                    }
                };
                root.TeamsRootWatcher = pending;
                pending.EnableRaisingEvents = true;

                // The folder may have appeared between the check and the watcher going live.
                if (!Directory.Exists(teamsDir)) return;
            }

            // The placeholder (if any) has done its job.
            root.TeamsRootWatcher?.Dispose();
            root.TeamsRootWatcher = null;

            // Watch for new team subdirectories
            var watcher = new FileSystemWatcher(teamsDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName
            };
            watcher.Created += (s, e) => OnTeamDirectoryCreated(root, e);
            watcher.Deleted += (s, e) => OnTeamDirectoryDeleted(root, e);
            root.TeamsRootWatcher = watcher;
            root.TeamsStarted = true;
            watcher.EnableRaisingEvents = true;

            LogInfo($"Started watching: {teamsDir}");

            // Never restore old agent panels from previous sessions.
            // Only react to NEW team directories created during this session via the FileSystemWatcher.
            //
            // One exception, which is not a restore: a teams folder this service SAW MISSING (or whose
            // profile folder it saw missing) and that exists now was created during this session, and
            // so was everything in it. Claude Code makes teams\ and the first team's folder in one go,
            // so that team is already inside by the time this watcher goes live and no Created event
            // will ever come for it. Look once.
            if (root.TeamsSeenMissing)
            {
                root.TeamsSeenMissing = false;
                ScanTeamsCreatedBeforeWatching(root, teamsDir);
            }
        }

        /// <summary>How long the one-off scan waits before processing what it found: longer than the
        /// 200 ms a Created event waits, so a folder that got BOTH (it appeared in the instant between
        /// the watcher going live and the listing below) has its state from the event by then and is
        /// skipped here.</summary>
        private static readonly TimeSpan TeamScanDelay = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Process the team folders that are in <paramref name="teamsDir"/> at the moment its watcher
        /// goes live. The listing is taken NOW, on the caller's thread (anything created later gets a
        /// Created event); the processing happens off it, after <see cref="TeamScanDelay"/>.
        /// </summary>
        private void ScanTeamsCreatedBeforeWatching(RootState root, string teamsDir)
        {
            string[] present;
            try
            {
                present = Directory.GetDirectories(teamsDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogError($"Could not list '{teamsDir}' for teams created before it was watched: {ex.Message}");
                return;
            }

            if (present.Length == 0) return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TeamScanDelay);

                    foreach (string teamDir in present)
                    {
                        if (Gone(root)) return;
                        string teamName = Path.GetFileName(teamDir);
                        if (!Directory.Exists(teamDir) ||
                            _activeTeams.ContainsKey(BuildTeamKey(root.Plan.Root, teamName, root.Plan.IsDefault)))
                        {
                            continue;
                        }

                        LogInfo($"Team directory found in a teams folder created this session: {teamName} ({root.Plan.Root})");
                        ProcessTeamDirectory(root, teamName);
                    }
                }
#pragma warning disable CA1031 // Unobserved task: log, as ProcessTeamDirectory does for a single team.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogError($"Error scanning '{teamsDir}' for teams created before it was watched: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Stop everything under one root: its watchers, its teams (TeamRemoved is raised for each, so
        /// their panels close) and its orphan tailers. Caller holds <c>_rootsLock</c> and has already
        /// taken the root out of <c>_roots</c>.
        /// <para>TeamRemoved is NOT raised here: each removed team's event is added to
        /// <paramref name="removed"/>, and the caller raises them (<see cref="RaiseTeamsRemoved"/>)
        /// after it has released <c>_rootsLock</c>.</para>
        /// </summary>
        private void StopRoot(RootState root, List<TeamRemovedEventArgs> removed)
        {
            // Set BEFORE the sweep below: a delayed callback that adds a TeamState after the sweep
            // re-checks this flag in GetOrAddTeam and takes its own entry out again.
            root.Stopped = true;

            lock (root.Sync)
            {
                try { root.TeamsRootWatcher?.Dispose(); } catch { }
                root.TeamsRootWatcher = null;
                root.TeamsStarted = false;
                root.ProjectsStarted = false;
            }

            DisposeProjectWatchers(root);

            foreach (var kvp in _activeTeams.Where(k => ReferenceEquals(k.Value.Root, root)).ToList())
            {
                if (!_activeTeams.TryRemove(kvp)) continue;
                try
                {
                    CleanupTeam(kvp.Value.TeamName, kvp.Value, removed);
                }
#pragma warning disable CA1031 // One team that will not clean up must not leave the root's other teams and tailers running.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogError($"Error cleaning up team '{kvp.Value.TeamName}' under '{root.Plan.Root}': {ex.Message}");
                }
            }

            var orphans = new List<TranscriptTailer>();
            lock (_trackedSubagentFiles)
            {
                foreach (var key in _orphanTailers.Keys.Where(k => IsUnder(root, k)).ToList())
                {
                    orphans.Add(_orphanTailers[key]);
                    _orphanTailers.Remove(key);
                }

                // Includes the "<dir>:watching" markers, so a root that comes back is watched afresh.
                _trackedSubagentFiles.RemoveWhere(p => IsUnder(root, p));
            }
            foreach (var tailer in orphans)
            {
                try { tailer.Dispose(); } catch { }
            }

            lock (_watchedProjectDirs)
            {
                _watchedProjectDirs.RemoveWhere(p => IsUnder(root, p));
            }
        }

        private void OnTeamDirectoryCreated(RootState root, FileSystemEventArgs e)
        {
            if (!Directory.Exists(e.FullPath)) return;
            string teamName = Path.GetFileName(e.FullPath);
            LogInfo($"New team directory detected: {teamName} ({root.Plan.Root})");

            // Async delay to let config.json be written, without blocking FSW thread
            Task.Run(async () =>
            {
                await Task.Delay(200);
                if (Gone(root)) return;
                ProcessTeamDirectory(root, teamName);
            });
        }

        private void OnTeamDirectoryDeleted(RootState root, FileSystemEventArgs e)
        {
            string teamName = Path.GetFileName(e.FullPath);
            LogInfo($"Team directory deleted: {teamName} ({root.Plan.Root})");

            string teamKey = BuildTeamKey(root.Plan.Root, teamName, root.Plan.IsDefault);
            if (_activeTeams.TryRemove(teamKey, out var state))
            {
                CleanupTeam(teamName, state);
            }
        }

        /// <summary>
        /// The state for (root, team name), created on first use; NULL when <paramref name="root"/> is
        /// no longer watched (every caller returns on null).
        /// <para>Callers are delayed callbacks that checked <see cref="Gone"/> some time ago. StopRoot
        /// may have swept the root's teams since, and an entry added after that sweep would never be
        /// cleaned up: worse, if the same folder is configured again it gets a NEW RootState but the
        /// same key, and would be handed this entry, bound for good to the stopped one. So the stopped
        /// flag is checked again AFTER the add (StopRoot sets it before it sweeps, so one of the two
        /// always sees the other), and an entry that belongs to another RootState than the live one
        /// asking is replaced.</para>
        /// <para>What this does not cover: a caller that got its state just before the sweep goes on
        /// using it after the sweep cleaned it up. See the note on <c>_activeTeams</c>.</para>
        /// </summary>
        private TeamState GetOrAddTeam(RootState root, string teamName)
        {
            string teamKey = BuildTeamKey(root.Plan.Root, teamName, root.Plan.IsDefault);
            if (Gone(root)) return null;

            TeamState state;
            while (true)
            {
                state = _activeTeams.GetOrAdd(teamKey, key => new TeamState { TeamName = teamName, TeamKey = key, Root = root });
                if (ReferenceEquals(state.Root, root)) break;

                // Same key, another RootState. The caller's root is live (checked above and below), so
                // the entry is a leftover of a stopped one. Swap it out; if somebody else changed the
                // entry first, look again.
                var fresh = new TeamState { TeamName = teamName, TeamKey = teamKey, Root = root };
                if (_activeTeams.TryUpdate(teamKey, fresh, state))
                {
                    LogInfo($"Replaced the state of team '{teamName}' left by a stopped watcher of '{root.Plan.Root}'");
                    CleanupTeam(state.TeamName, state);
                    state = fresh;
                    break;
                }
            }

            if (Gone(root))
            {
                // Only this exact entry: a concurrent replacement is not ours to remove.
                if (_activeTeams.TryRemove(new KeyValuePair<string, TeamState>(teamKey, state)))
                    CleanupTeam(state.TeamName, state);
                return null;
            }

            return state;
        }


        /// <summary>
        /// Process a team directory: read config.json, discover members, create tailers.
        /// </summary>
        private void ProcessTeamDirectory(RootState root, string teamName)
        {
            try
            {
                if (Gone(root)) return;
                ProcessTeamHook?.Invoke("start", teamName);
                string configPath = Path.Combine(root.Plan.TeamsDir, teamName, "config.json");

                if (!File.Exists(configPath))
                {
                    // Watch for config.json creation
                    WatchForConfigFile(root, teamName);
                    return;
                }

                ReadAndProcessConfig(root, teamName, configPath);

                // Watch config.json for changes (new members added)
                var state = GetOrAddTeam(root, teamName);
                if (state == null) return; // the root stopped being watched meanwhile
                if (state.ConfigWatcher == null)
                {
                    string teamDir = Path.Combine(root.Plan.TeamsDir, teamName);
                    state.ConfigWatcher = new FileSystemWatcher(teamDir)
                    {
                        Filter = "config.json",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    state.ConfigWatcher.Changed += (s, e) =>
                    {
                        // Debounce
                        var now = DateTime.Now;
                        if ((now - state.LastConfigChange).TotalMilliseconds < 500) return;
                        state.LastConfigChange = now;

                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            if (Gone(root)) return;
                            ReadAndProcessConfig(root, teamName, configPath);
                        });
                    };

                    // The root may have been stopped, and this state swept, after GetOrAddTeam handed
                    // it over: nothing would dispose a watcher attached to it now.
                    if (Gone(root))
                    {
                        try { state.ConfigWatcher?.Dispose(); } catch { }
                        state.ConfigWatcher = null;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing team '{teamName}': {ex.Message}");
            }
            finally
            {
                ProcessTeamHook?.Invoke("end", teamName);
            }
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo), null in production: called with "start" when a team folder
        /// is about to be processed (after the stopped check) and with "end" when that pass is over,
        /// on the processing thread. It lets a test stop the root at exactly the point a delayed
        /// callback has got past its check, which no amount of waiting can arrange.
        /// </summary>
        internal Action<string, string> ProcessTeamHook { get; set; }

        /// <summary>Test seam (InternalsVisibleTo): how many teams the service is holding state for.</summary>
        internal int ActiveTeamCount => _activeTeams.Count;

        private void WatchForConfigFile(RootState root, string teamName)
        {
            string teamDir = Path.Combine(root.Plan.TeamsDir, teamName);
            if (!Directory.Exists(teamDir)) return;

            // Ensure the TeamState exists so the watcher is owned by state.Watchers
            // and CleanupTeam/StopWatching dispose it deterministically — even if
            // the Created event never fires (no config.json ever appears).
            var state = GetOrAddTeam(root, teamName);
            if (state == null) return; // the root stopped being watched meanwhile

            // Construct with events OFF so we can wire the Created handler before
            // any event can fire. This closes the construct-to-subscribe race
            // where an event could be raised and lost between construction and
            // `watcher.Created += handler`.
            var watcher = new FileSystemWatcher(teamDir)
            {
                Filter = "config.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };
            state.Watchers.Add(watcher);

            // Interlocked guard: FileSystemWatcher can raise Created multiple times
            // for one file, and we must not Dispose() twice or re-enqueue processing.
            // The guard is also what makes the post-enable File.Exists recheck safe —
            // whichever of (real event, synchronous re-entry) wins, the other is a no-op.
            int handled = 0;
            FileSystemEventHandler handler = null;
            handler = (s, e) =>
            {
                if (Interlocked.Exchange(ref handled, 1) != 0) return;

                // Stop events and unsubscribe before Dispose so a queued duplicate
                // cannot re-enter this handler on another thread. Do NOT remove from
                // state.Watchers here — mutation races CleanupTeam's foreach on a
                // different thread. CleanupTeam's try/catch tolerates the already-
                // disposed watcher (FileSystemWatcher.Dispose is idempotent).
                watcher.Created -= handler;
                try { watcher.EnableRaisingEvents = false; } catch { }
                try { watcher.Dispose(); } catch { }

                if (Gone(root)) return;

                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    if (Gone(root)) return;
                    ProcessTeamDirectory(root, teamName);
                });
            };
            watcher.Created += handler;

            // Subscription is wired — now turn events on. Any activation failure here
            // (directory gone, permissions, OS refusal) should propagate to the caller's
            // catch (ProcessTeamDirectory wraps us in try/catch and logs), rather than
            // leaving a silently-disabled watcher in state.Watchers.
            watcher.EnableRaisingEvents = true;

            // Close the residual TOCTOU window: if config.json appeared between
            // Directory.Exists and EnableRaisingEvents=true, FSW will never raise
            // Created for it. Drive the handler synchronously; the Interlocked guard
            // ensures it only runs once whether or not a real event also arrives.
            if (File.Exists(Path.Combine(teamDir, "config.json")))
            {
                handler(null, null);
            }
        }

        /// <summary>
        /// Read config.json and discover new team members.
        /// For each new member, watch for their transcript JSONL file and create a TranscriptTailer.
        /// </summary>
        private void ReadAndProcessConfig(RootState root, string teamName, string configPath)
        {
            try
            {
                string json;
                using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json)) return;

                using var doc = JsonDocument.Parse(json);
                var config = doc.RootElement;

                string leadSessionId = config.TryGetProperty("leadSessionId", out var lsProp) ? lsProp.GetString() : null;
                if (string.IsNullOrEmpty(leadSessionId))
                {
                    LogError($"No leadSessionId in config for team '{teamName}'");
                    return;
                }

                var state = GetOrAddTeam(root, teamName);
                if (state == null) return; // the root stopped being watched meanwhile
                state.LeadSessionId = leadSessionId;

                if (!config.TryGetProperty("members", out var membersArr)) return;

                // Derive the correct project directory from the lead member's cwd
                // (MultiTerminal's own CWD may differ from the Claude Code project path)
                string projectDir = root.Plan.AppProjectDir;
                foreach (var member in membersArr.EnumerateArray())
                {
                    string agentType = member.TryGetProperty("agentType", out var tp) ? tp.GetString() : null;
                    if (agentType == "team-lead")
                    {
                        string leadCwd = member.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
                        if (!string.IsNullOrEmpty(leadCwd))
                        {
                            state.LeadCwd = leadCwd;
                            // The lead's transcripts are under the SAME config root as this team's
                            // config.json (one Claude Code process wrote both), never another profile's.
                            projectDir = GetLeadProjectDir(root.Plan.Root, leadCwd);
                            Log($" Derived project dir from lead cwd: {projectDir}");

                            // Also start orphan subagent watching on this project dir
                            // (the app's CWD may differ from where terminals are running)
                            StartProjectSubagentWatchingForDir(root, projectDir);
                        }
                        break;
                    }
                }

                // Resolve and cache the spawner terminal name once for the team.
                // Used by CreateTailerForMember and message routing.
                if (string.IsNullOrEmpty(state.SpawnerName))
                {
                    state.SpawnerName = LookupTeamSpawner(state);
                    if (!string.IsNullOrEmpty(state.SpawnerName))
                        LogInfo($"Cached spawner for team '{teamName}': {state.SpawnerName}");
                }

                // Collect ALL non-lead members into state.KnownMembers (for watcher closure)
                // and identify which are new (not yet tracked)
                var allMembers = new List<(string name, long joinedAt)>();
                var newMembers = new List<(string name, long joinedAt)>();
                foreach (var member in membersArr.EnumerateArray())
                {
                    string memberName = member.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    string agentType = member.TryGetProperty("agentType", out var typeProp) ? typeProp.GetString() : null;

                    if (string.IsNullOrEmpty(memberName)) continue;
                    if (agentType == "team-lead") continue;

                    long joinedAt = member.TryGetProperty("joinedAt", out var jProp) ? jProp.GetInt64() : 0;
                    allMembers.Add((memberName, joinedAt));

                    if (!state.TrackedMembers.Contains(memberName))
                        newMembers.Add((memberName, joinedAt));
                }
                state.KnownMembers = allMembers;

                // Detect members that were removed from config (agent shutdown)
                var allMemberNames = new HashSet<string>(allMembers.Select(m => m.name));
                var removedMembers = state.TrackedMembers.Where(m => !allMemberNames.Contains(m)).ToList();
                foreach (var removed in removedMembers)
                {
                    LogInfo($"Member '{removed}' removed from team '{teamName}' config — cleaning up");
                    state.TrackedMembers.Remove(removed);
                    if (state.MemberTailers.TryGetValue(removed, out var tailer))
                    {
                        try { tailer.Dispose(); } catch { }
                        state.MemberTailers.Remove(removed);
                    }
                    MemberRemoved?.Invoke(this, (teamName, removed, state.TeamKey));
                }

                // Build subagents directory path (needed for watching even if no new members)
                string subagentsDir = Path.Combine(projectDir, leadSessionId, "subagents");

                if (newMembers.Count > 0)
                {
                    LogInfo($"Config has {newMembers.Count} new member(s): {string.Join(", ", newMembers.Select(m => m.name))}. Subagents dir: {subagentsDir}");

                    // Sort new members by joinedAt to match with files in order
                    newMembers.Sort((a, b) => a.joinedAt.CompareTo(b.joinedAt));

                    // PRE-CLAIM: Lock all recent JSONL files in the subagents dir BEFORE
                    // the orphan watcher's sync scan can grab them. The orphan watcher checks
                    // _trackedSubagentFiles and skips claimed files. We'll release unclaimed
                    // files after matching if they don't belong to any team member.
                    var preClaimedFiles = new List<string>();
                    if (Directory.Exists(subagentsDir))
                    {
                        var cutoff = DateTime.UtcNow.AddSeconds(-30);
                        try
                        {
                            foreach (var file in Directory.GetFiles(subagentsDir, "agent-*.jsonl"))
                            {
                                var fi = new FileInfo(file);
                                if (fi.CreationTimeUtc > cutoff)
                                {
                                    lock (_trackedSubagentFiles)
                                    {
                                        if (_trackedSubagentFiles.Add(file))
                                            preClaimedFiles.Add(file);
                                    }
                                }
                            }
                            if (preClaimedFiles.Count > 0)
                                Log($" Pre-claimed {preClaimedFiles.Count} recent JSONL file(s) to block orphan watcher");
                        }
                        catch (Exception ex)
                        {
                            Log($" Pre-claim scan error: {ex.Message}");
                        }

                        // Try to match existing files to new members
                        MatchExistingTranscripts(teamName, subagentsDir, newMembers, state);
                    }

                    // Release pre-claimed files that weren't matched to any team member,
                    // so orphan watcher can adopt them (e.g., Explore/Plan subagents).
                    foreach (var file in preClaimedFiles)
                    {
                        if (!state.MatchedTranscripts.Contains(file))
                        {
                            lock (_trackedSubagentFiles) { _trackedSubagentFiles.Remove(file); }
                            Log($" Released unmatched pre-claim: {Path.GetFileName(file)}");
                        }
                    }
                }

                // Always ensure the subagents directory watcher is running.
                // This picks up new JSONL files created when agents wake up for subsequent turns,
                // even after all members have been initially matched.
                WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
            }
            catch (Exception ex)
            {
                LogError($"Error reading config for team '{teamName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Match existing transcript files to new members by creation time.
        /// </summary>
        private void MatchExistingTranscripts(string teamName, string subagentsDir,
            List<(string name, long joinedAt)> newMembers, TeamState state)
        {
            var files = Directory.GetFiles(subagentsDir, "agent-*.jsonl")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            Log($" MatchExistingTranscripts: {files.Count} files in {subagentsDir}, {newMembers.Count} new members");

            foreach (var member in newMembers)
            {
                if (state.TrackedMembers.Contains(member.name)) continue;

                // Find a transcript file created around the member's joinedAt time
                var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(member.joinedAt).UtcDateTime;

                Log($"   Member '{member.name}' joinedAt={memberJoinedUtc:HH:mm:ss.fff}");

                // Look for files created within 30 seconds of the member joining
                var matchingFile = files.FirstOrDefault(f =>
                    Math.Abs((f.CreationTimeUtc - memberJoinedUtc).TotalSeconds) < 30
                    && !state.MatchedTranscripts.Contains(f.FullName));

                if (matchingFile != null)
                {
                    LogInfo($"MATCHED '{member.name}' -> {matchingFile.Name} (created={matchingFile.CreationTimeUtc:HH:mm:ss.fff}, delta={Math.Abs((matchingFile.CreationTimeUtc - memberJoinedUtc).TotalSeconds):F1}s)");
                    state.MatchedTranscripts.Add(matchingFile.FullName);
                    CreateTailerForMember(teamName, member.name, matchingFile.FullName, state);
                }
                else
                {
                    LogError($"NO MATCH for '{member.name}' - closest files:");
                    foreach (var f in files.Where(f => !state.MatchedTranscripts.Contains(f.FullName)).Take(5))
                    {
                        var delta = (f.CreationTimeUtc - memberJoinedUtc).TotalSeconds;
                        Log($"     {f.Name} created={f.CreationTimeUtc:HH:mm:ss.fff} delta={delta:F1}s");
                    }
                }
            }
        }

        /// <summary>
        /// Watch the subagents directory for new transcript files.
        /// Initially matches files to untracked members by timing, then continues watching
        /// permanently to pick up new JSONL files created when agents wake up for subsequent turns.
        /// </summary>
        private void WatchForNewTranscripts(string teamName, string subagentsDir,
            List<(string name, long joinedAt)> newMembers, TeamState state)
        {
            // Ensure directory exists
            if (!Directory.Exists(subagentsDir))
            {
                // Watch for the subagents directory to be created
                string parentDir = Path.GetDirectoryName(subagentsDir);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    // Session dir doesn't exist yet — watch the project dir (grandparent)
                    // for it to appear, then chain into the subagents watcher.
                    // Without this, the orphan watcher races us and creates a panel
                    // with a generic "Subagent {hash}" name instead of the team member name.
                    string grandparentDir = Path.GetDirectoryName(parentDir);
                    string sessionDirName = Path.GetFileName(parentDir);
                    if (grandparentDir != null && Directory.Exists(grandparentDir))
                    {
                        Log($" Session dir not yet created: {parentDir} — watching {grandparentDir} for '{sessionDirName}'");
                        var sessionDirWatcher = new FileSystemWatcher(grandparentDir)
                        {
                            Filter = sessionDirName,
                            NotifyFilter = NotifyFilters.DirectoryName,
                            EnableRaisingEvents = true
                        };
                        state.Watchers.Add(sessionDirWatcher);
                        sessionDirWatcher.Created += (s, e) =>
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                if (Gone(state.Root)) return;
                                LogInfo($"Session dir appeared: {e.FullPath} — chaining to subagents watcher");
                                WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
                            });
                        };
                    }
                    else
                    {
                        Log($" Session dir not yet created: {parentDir} (no valid grandparent to watch)");
                    }
                    return;
                }

                if (parentDir != null && Directory.Exists(parentDir))
                {
                    var dirWatcher = new FileSystemWatcher(parentDir)
                    {
                        Filter = "subagents",
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    state.Watchers.Add(dirWatcher);
                    dirWatcher.Created += (s, e) =>
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            if (Gone(state.Root)) return;
                            WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
                        });
                    };
                    return;
                }
                return;
            }

            // Don't add duplicate watchers for the same directory
            if (state.HasSubagentsDirWatcher)
            {
                // Watcher already running, but scan for files that may have been created
                // before the watcher started (e.g., directory and file created simultaneously).
                // This closes the race where WatchForNewTranscripts re-enters after dir creation
                // but the transcript file already exists, so the FSW never fires Created.
                var untrackedMembers = state.KnownMembers
                    .Where(m => !state.TrackedMembers.Contains(m.name))
                    .ToList();
                if (untrackedMembers.Count > 0)
                {
                    LogInfo($"Re-scanning for {untrackedMembers.Count} untracked member(s) in team '{teamName}'");
                    MatchExistingTranscripts(teamName, subagentsDir, untrackedMembers, state);
                }
                return;
            }

            var watcher = new FileSystemWatcher(subagentsDir)
            {
                Filter = "agent-*.jsonl",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            state.Watchers.Add(watcher);
            state.HasSubagentsDirWatcher = true;

            watcher.Created += (s, e) =>
            {
                var fullPath = e.FullPath;

                // Immediately claim the file BEFORE async work, so orphan watcher
                // can't race us and create a duplicate panel.
                lock (_trackedSubagentFiles) { _trackedSubagentFiles.Add(fullPath); }

                // Async delay to let file be initialized, without blocking FSW thread
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    if (Gone(state.Root)) return;

                if (state.MatchedTranscripts.Contains(fullPath))
                    return;

                // First: try to match to an untracked member from the latest config
                // (use state.KnownMembers which is updated on each config read, not the captured newMembers list)
                // Validate timing to avoid greedily claiming non-team subagent files (Explore, Plan)
                var nextMember = state.KnownMembers.FirstOrDefault(m => !state.TrackedMembers.Contains(m.name));
                if (nextMember.name != null)
                {
                    var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(nextMember.joinedAt).UtcDateTime;
                    var fileCreatedUtc = new FileInfo(fullPath).CreationTimeUtc;
                    var timingDelta = Math.Abs((fileCreatedUtc - memberJoinedUtc).TotalSeconds);

                    if (timingDelta < 30)
                    {
                        LogInfo($"Watcher matched new file to untracked member '{nextMember.name}': {Path.GetFileName(fullPath)} (delta={timingDelta:F1}s)");
                        state.MatchedTranscripts.Add(fullPath);
                        CreateTailerForMember(teamName, nextMember.name, fullPath, state);
                        return;
                    }
                    else
                    {
                        Log($"Timing mismatch for untracked member '{nextMember.name}': delta={timingDelta:F1}s, skipping greedy match for {Path.GetFileName(fullPath)}");
                    }
                }

                // All members already tracked (or timing didn't match) — check if this
                // is actually a team agent transcript before trying heuristic matching.
                // Non-team subagents (Explore, Plan) won't have teammate-message markers.
                if (!LooksLikeTeamTranscript(fullPath))
                {
                    // Not a team transcript — release our early claim so orphan watcher can adopt it
                    lock (_trackedSubagentFiles) { _trackedSubagentFiles.Remove(fullPath); }
                    Log($"Non-team subagent transcript (no team markers), released claim: {Path.GetFileName(fullPath)}");
                    return;
                }

                // This is a subsequent wake-up file for a team agent.
                // Identify which member it belongs to and add to their existing tailer.
                string ownerName = IdentifyFileOwner(fullPath, state);

                // Fallback: if content matching failed, try inbox-based matching.
                // The agent that just woke up is the one whose inbox was most recently modified.
                if (ownerName == null)
                {
                    ownerName = IdentifyByInboxTiming(fullPath, teamName, state);
                }

                // Fallback: if only one member recently sent a message via Agent Panel,
                // the wake-up file likely belongs to them.
                if (ownerName == null)
                {
                    ownerName = IdentifyByRecentSend(state);
                }

                if (ownerName != null && state.MemberTailers.TryGetValue(ownerName, out var existingTailer))
                {
                    state.MatchedTranscripts.Add(fullPath);
                    // _trackedSubagentFiles already added synchronously before Task.Run
                    existingTailer.AddTranscriptFile(fullPath);
                    LogInfo($"Added subsequent transcript to {ownerName}: {Path.GetFileName(fullPath)}");
                }
                else
                {
                    // Has team markers but couldn't identify owner — log for debugging.
                    // File already claimed in _trackedSubagentFiles (pre-Task.Run), so orphan watcher is blocked.
                    state.MatchedTranscripts.Add(fullPath);
                    Log($"Unmatched team transcript (blocked orphan adoption): {Path.GetFileName(fullPath)}");
                }
                }); // end Task.Run
            };

            Log($" Watching for new transcripts in: {subagentsDir}");

            // Scan existing files that may have been created before the watcher started.
            // This closes the race where the subagents/ dir and agent-*.jsonl file are created
            // nearly simultaneously, so the FSW never fires Created for the already-existing file.
            var untrackedAfterSetup = state.KnownMembers
                .Where(m => !state.TrackedMembers.Contains(m.name))
                .ToList();
            if (untrackedAfterSetup.Count > 0)
            {
                MatchExistingTranscripts(teamName, subagentsDir, untrackedAfterSetup, state);
            }
        }

        /// <summary>
        /// Identify which team member a new transcript JSONL file belongs to.
        /// Uses a multi-step heuristic:
        ///   1. Check if the first user message content explicitly names the target ("You are Agent X")
        ///   2. Check if the content mentions a team member name (excluding the sender)
        ///   3. Fallback: check the first assistant response for member names
        /// </summary>
        private string IdentifyFileOwner(string filePath, TeamState state)
        {
            try
            {
                string firstLine = null;
                string secondLine = null;
                string thirdLine = null;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    firstLine = reader.ReadLine();
                    secondLine = reader.ReadLine();
                    thirdLine = reader.ReadLine();
                }

                if (string.IsNullOrEmpty(firstLine)) return null;

                // Extract the sender and content from the first user message
                string sender = null;
                string content = null;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(firstLine);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("message", out var msgObj)
                        && msgObj.ValueKind == System.Text.Json.JsonValueKind.Object
                        && msgObj.TryGetProperty("content", out var contentProp))
                    {
                        if (contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            content = contentProp.GetString();
                        }
                        else if (contentProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            // Extract text from content blocks
                            foreach (var block in contentProp.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                    && block.TryGetProperty("text", out var txt))
                                {
                                    content = txt.GetString();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(content)) return null;

                // Extract sender from <teammate-message teammate_id="SENDER">
                var senderMatch = System.Text.RegularExpressions.Regex.Match(
                    content, @"<teammate-message\s+teammate_id=""([^""]+)""");
                if (senderMatch.Success)
                {
                    sender = senderMatch.Groups[1].Value;
                }

                // Strategy 1: Check for explicit naming "You are Agent X"
                var youAreMatch = System.Text.RegularExpressions.Regex.Match(
                    content, @"You are (\w[\w\s]*?)(?:,|\.|!|\n)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (youAreMatch.Success)
                {
                    string candidateName = youAreMatch.Groups[1].Value.Trim();
                    if (state.TrackedMembers.Contains(candidateName))
                    {
                        Log($" IdentifyFileOwner: matched by 'You are {candidateName}'");
                        return candidateName;
                    }
                }

                // Strategy 2: Check content for any member name (excluding sender)
                string bestMatch = FindMemberNameInText(content, sender, state);
                if (bestMatch != null)
                {
                    Log($" IdentifyFileOwner: matched by content mention → {bestMatch}");
                    return bestMatch;
                }

                // Strategy 3: Check second/third lines (first assistant response)
                // Exclude the sender — the assistant text is from the RECIPIENT's process,
                // and it often mentions the sender by name (e.g. "I received the haiku from Agent Beta")
                string assistantText = ExtractAssistantText(secondLine) ?? ExtractAssistantText(thirdLine);
                if (assistantText != null)
                {
                    bestMatch = FindMemberNameInText(assistantText, sender, state);
                    if (bestMatch != null)
                    {
                        Log($" IdentifyFileOwner: matched by assistant response → {bestMatch}");
                        return bestMatch;
                    }
                }

                // Strategy 4: If only one non-sender member exists, it must be them
                var candidates = new List<string>();
                foreach (var member in state.TrackedMembers)
                {
                    if (member != sender && member != "team-lead")
                        candidates.Add(member);
                }
                if (candidates.Count == 1)
                {
                    Log($" IdentifyFileOwner: matched by elimination → {candidates[0]}");
                    return candidates[0];
                }

                Log($" IdentifyFileOwner: no match found (sender={sender}, candidates={string.Join(",", candidates)})");
            }
            catch (Exception ex)
            {
                Log($" IdentifyFileOwner error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Find a team member name mentioned in text, excluding the sender.
        /// First tries exact name match, then falls back to partial name matching
        /// (individual significant words from multi-word names like "Agent Alpha" → "Alpha").
        /// Returns the first match found, or null.
        /// </summary>
        private static string FindMemberNameInText(string text, string excludeSender, TeamState state)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Pass 1: Exact full-name match
            foreach (var member in state.TrackedMembers)
            {
                if (member == excludeSender || member == "team-lead") continue;

                if (text.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return member;
                }
            }

            // Pass 2: Partial name match — check individual significant words
            // e.g., "Agent Alpha" → check for "Alpha" (skip generic words like "Agent")
            var genericWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "agent", "the", "a", "an", "my", "team" };

            foreach (var member in state.TrackedMembers)
            {
                if (member == excludeSender || member == "team-lead") continue;

                var words = member.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 1) continue; // Single-word names already checked in pass 1

                foreach (var word in words)
                {
                    if (word.Length < 3 || genericWords.Contains(word)) continue;

                    // Use word boundary check to avoid false matches (e.g., "Alpha" in "alphabet")
                    int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        // Verify it's a standalone word (not part of a larger word)
                        bool startOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                        bool endOk = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);
                        if (startOk && endOk)
                        {
                            return member;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Extract assistant text content from a JSONL line.
        /// Returns the text content if the line is an assistant message, null otherwise.
        /// </summary>
        private static string ExtractAssistantText(string jsonLine)
        {
            if (string.IsNullOrEmpty(jsonLine)) return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                string type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                if (type != "assistant") return null;

                if (root.TryGetProperty("message", out var msgObj)
                    && msgObj.ValueKind == System.Text.Json.JsonValueKind.Object
                    && msgObj.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var block in contentProp.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                && block.TryGetProperty("text", out var txt))
                            {
                                return txt.GetString();
                            }
                            // Also check SendMessage tool_use for identity clues
                            if (block.TryGetProperty("type", out var bt2) && bt2.GetString() == "tool_use"
                                && block.TryGetProperty("name", out var tn) && tn.GetString() == "SendMessage"
                                && block.TryGetProperty("input", out var inp)
                                && inp.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                // The summary field often contains the agent's name
                                string summary = inp.TryGetProperty("summary", out var sp) ? sp.GetString() : null;
                                string msgContent = inp.TryGetProperty("content", out var cp) ? cp.GetString() : null;
                                return summary ?? msgContent;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Identify the owner of a new transcript file by checking which agent's inbox
        /// was most recently modified. When an agent wakes up to process a message,
        /// its inbox was just read/written shortly before the transcript file was created.
        /// </summary>
        private string IdentifyByInboxTiming(string transcriptPath, string teamName, TeamState state)
        {
            try
            {
                var fileCreationUtc = new FileInfo(transcriptPath).CreationTimeUtc;
                string bestMatch = null;
                double bestDelta = double.MaxValue;

                foreach (var memberName in state.TrackedMembers)
                {
                    if (memberName == "team-lead") continue;

                    string inboxFileName = memberName.Replace(" ", "-") + ".json";
                    string inboxPath = Path.Combine(state.Root.Plan.TeamsDir, teamName, "inboxes", inboxFileName);

                    if (!File.Exists(inboxPath)) continue;

                    var inboxModUtc = new FileInfo(inboxPath).LastWriteTimeUtc;
                    // Inbox should be modified BEFORE the transcript file is created
                    var delta = (fileCreationUtc - inboxModUtc).TotalSeconds;

                    if (delta >= -2 && delta < 30 && delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMatch = memberName;
                    }
                }

                if (bestMatch != null)
                {
                    Log($" IdentifyByInboxTiming: matched '{bestMatch}' (inbox modified {bestDelta:F1}s before transcript created)");
                }

                return bestMatch;
            }
            catch (Exception ex)
            {
                Log($" IdentifyByInboxTiming error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Identify the owner by checking which TranscriptTailer recently sent a message
        /// (via Agent Panel UI). The agent that was sent a message is the one that woke up.
        /// </summary>
        private static string IdentifyByRecentSend(TeamState state)
        {
            string bestMatch = null;
            DateTime bestTime = DateTime.MinValue;
            var cutoff = DateTime.UtcNow.AddSeconds(-30);

            foreach (var kvp in state.MemberTailers)
            {
                if (kvp.Value.LastMessageSentAt.HasValue
                    && kvp.Value.LastMessageSentAt.Value > cutoff
                    && kvp.Value.LastMessageSentAt.Value > bestTime)
                {
                    bestTime = kvp.Value.LastMessageSentAt.Value;
                    bestMatch = kvp.Key;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Create a TranscriptTailer for a team member and fire the TeammateDiscovered event.
        /// </summary>
        private void CreateTailerForMember(string teamName, string memberName, string transcriptPath, TeamState state)
        {
            try
            {
                // Reclaim from orphan watcher: if an orphan tailer already exists for this
                // transcript file, dispose it so the orphan panel is auto-removed from MainForm.
                // This happens when the orphan watcher's sync scan beats the team watcher.
                ReclaimFromOrphan(transcriptPath);

                // Claude Code uses dashes in inbox filenames (e.g. "Agent-Alpha.json" not "Agent Alpha.json")
                string inboxFileName = memberName.Replace(" ", "-") + ".json";
                string inboxPath = Path.Combine(state.Root.Plan.TeamsDir, teamName, "inboxes", inboxFileName);

                var tailer = new TranscriptTailer(transcriptPath, inboxPath, memberName);
                tailer.IsTeamAgent = true; // Don't auto-stop on Result — team agents stay alive between turns
                state.TrackedMembers.Add(memberName);
                state.MemberTailers[memberName] = tailer;

                // Mark as claimed so project-level watcher skips it
                lock (_trackedSubagentFiles) { _trackedSubagentFiles.Add(transcriptPath); }

                // Start tailing (catches up on existing content)
                tailer.StartAsync();

                LogInfo($"Created tailer for '{memberName}' in team '{teamName}': {Path.GetFileName(transcriptPath)}");

                // Bridge team agent SendMessage calls to ChatPanel
                tailer.TeamMessageSent += (s, msg) =>
                {
                    TeamMessageSent?.Invoke(this, (teamName, memberName, msg, state.SpawnerName));
                };

                // Look up spawner and task metadata from the office hook tracking file
                var tracking = LookupFromTrackingByPath(transcriptPath);

                // Resolve spawner with cascading fallbacks:
                // 1. Cached spawner from TeamState (resolved once in ReadAndProcessConfig)
                // 2. Tracking file by path (mt-office-agents.json)
                // 3. Team spawner mapping (mt-team-spawners.json)
                string resolvedSpawner = state.SpawnerName;
                if (string.IsNullOrEmpty(resolvedSpawner))
                    resolvedSpawner = tracking.SpawnerName;
                if (string.IsNullOrEmpty(resolvedSpawner))
                {
                    resolvedSpawner = LookupTeamSpawner(state);
                    if (!string.IsNullOrEmpty(resolvedSpawner))
                    {
                        state.SpawnerName = resolvedSpawner;
                        LogInfo($"Resolved spawner for team '{teamName}' via team-spawner mapping: {resolvedSpawner}");
                    }
                }

                TeammateDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = teamName,
                    MemberName = memberName,
                    Tailer = tailer,
                    SpawnerName = resolvedSpawner,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType,
                    LeadCwd = state.LeadCwd,
                    ConfigRoot = state.Root.Plan.Root,
                    TeamKey = state.TeamKey
                });
            }
            catch (Exception ex)
            {
                LogError($"Error creating tailer for '{memberName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Watch one root's session directories for non-team subagent JSONL files.
        /// This catches agents spawned via Task tool that don't belong to any team.
        /// Scans all project dirs with recent activity, not just the app's CWD project.
        /// Caller holds <c>root.Sync</c>. Idempotent; a root with no projects folder yet is retried
        /// at the next refresh.
        /// </summary>
        private void EnsureProjectSubagentWatching(RootState root)
        {
            if (root.ProjectsStarted) return;

            string projectsRoot = root.Plan.ProjectsDir;
            if (!Directory.Exists(projectsRoot))
            {
                if (!root.ProjectsMissingLogged)
                {
                    root.ProjectsMissingLogged = true;
                    Log($" Projects directory does not exist yet: {projectsRoot}");
                }
                return;
            }
            root.ProjectsStarted = true;

            // Always watch the app's own project dir
            StartProjectSubagentWatchingForDir(root, root.Plan.AppProjectDir);

            // Scan all project dirs for recent session-level activity.
            // We check session directories INSIDE each project (not just the project dir's
            // own LastWriteTimeUtc, which only updates when new session dirs are added —
            // not when transcript files are written inside existing sessions).
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-2);
                foreach (var projDir in Directory.GetDirectories(projectsRoot))
                {
                    if (string.Equals(projDir, root.Plan.AppProjectDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (HasRecentSessionActivity(projDir, cutoff))
                    {
                        StartProjectSubagentWatchingForDir(root, projDir);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" Error scanning project dirs for subagent watching: {ex.Message}");
            }

            // Global catch-all: watch ALL of this root's projects/ recursively for agent-*.jsonl
            // files. This handles projects that become active after the app starts, or projects
            // the scan above missed. The _trackedSubagentFiles HashSet prevents duplicates with
            // per-project watchers.
            try
            {
                var globalWatcher = new FileSystemWatcher(projectsRoot)
                {
                    Filter = "agent-*.jsonl",
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                lock (root.ProjectWatchers) { root.ProjectWatchers.Add(globalWatcher); }
                DisposeProjectWatchersIfStopped(root);

                globalWatcher.Created += (s, e) =>
                {
                    // Async delay to let team watcher claim files first, without blocking
                    // the FileSystemWatcher thread pool thread.
                    var fullPath = e.FullPath;
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        if (Gone(root)) return;

                        if (IsFileClaimedByTeam(root, fullPath)) return;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(fullPath)) return;
                            _trackedSubagentFiles.Add(fullPath);
                        }

                        LogInfo($"Global watcher caught orphan subagent: {Path.GetFileName(fullPath)}");
                        CreateOrphanSubagentPanel(root, fullPath);
                    });
                };

                Log($" Global subagent watcher started on: {projectsRoot}");
            }
            catch (Exception ex)
            {
                Log($" Error setting up global subagent watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a project directory has any session directories with recent activity.
        /// Looks at session dir timestamps AND subagents/ dir timestamps, which are more
        /// reliable than the project dir's own LastWriteTimeUtc.
        /// </summary>
        private static bool HasRecentSessionActivity(string projectDir, DateTime cutoff)
        {
            try
            {
                foreach (var sessionDir in Directory.GetDirectories(projectDir))
                {
                    var di = new DirectoryInfo(sessionDir);
                    if (!Guid.TryParse(di.Name, out _)) continue;
                    if (di.LastWriteTimeUtc > cutoff) return true;

                    // Also check if subagents/ dir has recent activity
                    string subagentsDir = Path.Combine(sessionDir, "subagents");
                    if (Directory.Exists(subagentsDir))
                    {
                        var subDi = new DirectoryInfo(subagentsDir);
                        if (subDi.LastWriteTimeUtc > cutoff) return true;
                    }
                }
            }
            catch
            {
                // If we can't read the directory, skip it
            }
            return false;
        }

        /// <summary>
        /// Watch a specific project directory's session directories for non-team subagent JSONL files.
        /// Can be called multiple times for different projects (e.g., when team config reveals
        /// terminals running in a different project than the app's CWD).
        /// </summary>
        private void StartProjectSubagentWatchingForDir(RootState root, string projectDir)
        {
            if (Gone(root)) return;
            if (!Directory.Exists(projectDir))
            {
                Log($" Project dir not found for subagent watching: {projectDir}");
                return;
            }

            // Guard against duplicate watching of the same project dir
            lock (_watchedProjectDirs)
            {
                if (!_watchedProjectDirs.Add(projectDir))
                {
                    Log($" Already watching project dir for orphan subagents: {projectDir}");
                    return;
                }
            }

            // Watch for new session directories (UUID-named subdirectories)
            var sessionDirWatcher = new FileSystemWatcher(projectDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            lock (root.ProjectWatchers) { root.ProjectWatchers.Add(sessionDirWatcher); }
            DisposeProjectWatchersIfStopped(root);
            sessionDirWatcher.Created += (s, e) =>
            {
                var fullPath = e.FullPath;
                Task.Run(async () =>
                {
                    await Task.Delay(300);
                    if (Gone(root)) return;
                    WatchSessionSubagentsDir(root, fullPath);
                });
            };

            // Scan recent session directories on startup (most recently modified first, limit to 3)
            var recentSessions = Directory.GetDirectories(projectDir)
                .Select(d => new DirectoryInfo(d))
                .Where(d => Guid.TryParse(d.Name, out _)) // only UUID-named dirs
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(3);

            foreach (var session in recentSessions)
            {
                WatchSessionSubagentsDir(root, session.FullName);
            }

            LogInfo($"Project-level subagent watching started: {projectDir}");
        }

        /// <summary>
        /// Watch a specific session's subagents/ directory for orphan (non-team) agent JSONL files.
        /// </summary>
        private void WatchSessionSubagentsDir(RootState root, string sessionDir)
        {
            if (!Directory.Exists(sessionDir)) return;

            string subagentsDir = Path.Combine(sessionDir, "subagents");

            if (!Directory.Exists(subagentsDir))
            {
                // Watch for subagents/ directory creation
                try
                {
                    var dirWatcher = new FileSystemWatcher(sessionDir)
                    {
                        Filter = "subagents",
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    lock (root.ProjectWatchers) { root.ProjectWatchers.Add(dirWatcher); }
                    DisposeProjectWatchersIfStopped(root);
                    dirWatcher.Created += (s, e) =>
                    {
                        var fullPath = e.FullPath;
                        Task.Run(async () =>
                        {
                            await Task.Delay(200);
                            if (Gone(root)) return;
                            WatchSubagentsForOrphans(root, fullPath);
                        });
                    };
                }
                catch (Exception ex)
                {
                    Log($" Error watching session dir for subagents: {ex.Message}");
                }
                return;
            }

            WatchSubagentsForOrphans(root, subagentsDir);
        }

        /// <summary>
        /// Called right after a project-level watcher is added to its root: a watcher added by a
        /// callback that lost the race with StopRoot is disposed here instead of leaking.
        /// </summary>
        private static void DisposeProjectWatchersIfStopped(RootState root)
        {
            if (root.Stopped) DisposeProjectWatchers(root);
        }

        private static void DisposeProjectWatchers(RootState root)
        {
            List<FileSystemWatcher> watchers;
            lock (root.ProjectWatchers)
            {
                watchers = root.ProjectWatchers.ToList();
                root.ProjectWatchers.Clear();
            }
            foreach (var watcher in watchers)
            {
                try { watcher.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Watch a subagents directory and create panels for any agent-*.jsonl files
        /// not already claimed by a team.
        /// </summary>
        private void WatchSubagentsForOrphans(RootState root, string subagentsDir)
        {
            if (Gone(root)) return;
            if (!Directory.Exists(subagentsDir)) return;

            // Check if we already have a project-level watcher on this directory
            lock (_trackedSubagentFiles)
            {
                if (_trackedSubagentFiles.Contains(subagentsDir + ":watching"))
                    return;
                _trackedSubagentFiles.Add(subagentsDir + ":watching");
            }

            try
            {
                var watcher = new FileSystemWatcher(subagentsDir)
                {
                    Filter = "agent-*.jsonl",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                lock (root.ProjectWatchers) { root.ProjectWatchers.Add(watcher); }
                DisposeProjectWatchersIfStopped(root);

                watcher.Created += (s, e) =>
                {
                    // Async delay to let team watcher claim files first, without blocking
                    // the FileSystemWatcher thread pool thread.
                    var fullPath = e.FullPath;
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        if (Gone(root)) return;

                        // Skip if already tracked by a team or already tracked as orphan
                        if (IsFileClaimedByTeam(root, fullPath)) return;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(fullPath)) return;
                            _trackedSubagentFiles.Add(fullPath);
                        }

                        CreateOrphanSubagentPanel(root, fullPath);
                    });
                };

                Log($" Watching for orphan subagents in: {subagentsDir}");

                // Scan for recently-created files that may have been written before the watcher started.
                // This closes the race where subagents/ dir and agent-*.jsonl are created nearly simultaneously:
                // the dir-creation callback sleeps 200ms, during which the JSONL file is already written,
                // so the FileSystemWatcher never fires Created. We only scan files < 30s old to avoid
                // picking up dead agents from previous sessions.
                var cutoff = DateTime.UtcNow.AddSeconds(-30);
                foreach (var existingFile in Directory.GetFiles(subagentsDir, "agent-*.jsonl"))
                {
                    try
                    {
                        var fi = new FileInfo(existingFile);
                        if (fi.CreationTimeUtc < cutoff) continue;
                        if (IsFileClaimedByTeam(root, existingFile)) continue;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(existingFile)) continue;
                            _trackedSubagentFiles.Add(existingFile);
                        }

                        Log($" Found recent orphan subagent file (created {(DateTime.UtcNow - fi.CreationTimeUtc).TotalSeconds:F1}s ago): {Path.GetFileName(existingFile)}");
                        CreateOrphanSubagentPanel(root, existingFile);
                    }
                    catch (Exception scanEx)
                    {
                        Log($" Error scanning existing subagent file: {scanEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" Error watching subagents dir: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a transcript file is already claimed by any active team. The timing heuristic
        /// only consults teams under the file's own root: a team of another profile cannot be waiting
        /// for a transcript that was written here.
        /// </summary>
        private bool IsFileClaimedByTeam(RootState root, string filePath)
        {
            foreach (var state in _activeTeams.Values)
            {
                if (state.MatchedTranscripts.Contains(filePath))
                    return true;

                if (!ReferenceEquals(state.Root, root))
                    continue;

                // Also check if any active team has unmatched members whose joinedAt
                // timing matches this file. This prevents the orphan watcher from racing
                // the team watcher when the session dir appears after the team config.
                var untrackedMembers = state.KnownMembers
                    .Where(m => !state.TrackedMembers.Contains(m.name))
                    .ToList();
                if (untrackedMembers.Count > 0)
                {
                    try
                    {
                        var fileCreatedUtc = new FileInfo(filePath).CreationTimeUtc;
                        foreach (var member in untrackedMembers)
                        {
                            var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(member.joinedAt).UtcDateTime;
                            if (Math.Abs((fileCreatedUtc - memberJoinedUtc).TotalSeconds) < 30)
                            {
                                Log($" IsFileClaimedByTeam: deferring '{Path.GetFileName(filePath)}' — likely belongs to unmatched team member '{member.name}'");
                                return true;
                            }
                        }
                    }
                    catch { /* file may not exist yet */ }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a transcript JSONL file looks like it belongs to a Claude Code internal
        /// system agent (e.g., the compact/summary agent). These are spawned internally by
        /// Claude Code for housekeeping, not by user code via the Task tool, and shouldn't
        /// appear in the Agent Panel.
        /// </summary>
        private static bool LooksLikeSystemAgent(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                // Check first 5 lines for known system agent signatures
                for (int i = 0; i < 5; i++)
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) break;

                    // Compact/summary agent: has a distinctive summarization prompt
                    if (line.Contains("create a detailed summary of the conversation so far"))
                        return true;

                    // Compact agent also instructs: no tools, only summary output
                    if (line.Contains("IMPORTANT: Do NOT use any tools") && line.Contains("summary"))
                        return true;
                }
            }
            catch
            {
                // If we can't read, don't filter — err on the side of showing
            }

            return false;
        }

        /// <summary>
        /// Check if a transcript JSONL file looks like it belongs to a team agent.
        /// Team agent wakeup transcripts contain teammate-message markers in their
        /// first few lines. Non-team subagents (Explore, Plan) have plain task prompts
        /// without these markers, and should not be claimed by the team watcher.
        /// </summary>
        private static bool LooksLikeTeamTranscript(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                // Check first 3 lines for team-specific markers
                for (int i = 0; i < 3; i++)
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) break;

                    // Team agents receive messages wrapped in <teammate-message> tags
                    if (line.Contains("<teammate-message") || line.Contains("teammate_id"))
                        return true;

                    // Team agents may reference the teams inbox system
                    if (line.Contains("/teams/") && line.Contains("/inboxes/"))
                        return true;
                }
            }
            catch
            {
                // If we can't read the file, err on the side of not claiming it
            }

            return false;
        }

        /// <summary>
        /// Create a read-only TranscriptTailer for a non-team subagent and fire SubagentDiscovered.
        /// </summary>
        private void CreateOrphanSubagentPanel(RootState root, string transcriptPath)
        {
            try
            {
                if (Gone(root)) return;

                // Filter out Claude Code system agents (compact/summary, etc.)
                if (LooksLikeSystemAgent(transcriptPath))
                {
                    LogInfo($"Skipping system agent (compact/summary): {Path.GetFileName(transcriptPath)}");
                    return;
                }

                // Derive a short name from the filename: agent-a1ea2e03... → "Subagent a1ea2e03"
                string fileName = Path.GetFileNameWithoutExtension(transcriptPath); // "agent-a1ea2e03..."
                string shortHash = fileName.Length > 12 ? fileName.Substring(6, 8) : fileName;

                // Look up spawner terminal and task metadata from the office hook tracking file.
                // The subagent-office-hook.js writes an entry for every agent spawned from a
                // MultiTerminal-tracked terminal. If there's NO entry, this subagent was spawned
                // by an external Claude instance (e.g., Clarion IDE addin) and should not appear
                // in MultiTerminal's Agent Panel.
                var tracking = LookupFromTracking(fileName);
                if (string.IsNullOrEmpty(tracking.SpawnerName) && string.IsNullOrEmpty(tracking.AgentName)
                    && string.IsNullOrEmpty(tracking.TaskDescription))
                {
                    Log($"Skipping external subagent (no tracking entry): {Path.GetFileName(transcriptPath)}");
                    return;
                }

                // Prefer the Task tool's name parameter if recorded; fall back to hex-based name
                string displayName;
                if (!string.IsNullOrEmpty(tracking.AgentName))
                    displayName = tracking.AgentName;
                else
                    displayName = $"Subagent {shortHash}";

                // Filter out agents from recently-deleted teams (prevents orphan panel
                // from being created after TeamDelete when agent wakes for shutdown)
                if (_retiredTeamAgentNames.ContainsKey(RetiredKey(root, displayName)))
                {
                    LogInfo($"Skipping retired team agent: {displayName}");
                    return;
                }

                // Filter out background agents (name starts with '_')
                if (displayName.StartsWith("_"))
                {
                    LogInfo($"Skipping background agent (underscore prefix): {displayName}");
                    return;
                }

                // No inbox path — read-only panel
                var tailer = new TranscriptTailer(transcriptPath, null, displayName);

                lock (_trackedSubagentFiles)
                {
                    _orphanTailers[NormalizeTranscriptPath(transcriptPath)] = tailer;
                }

                tailer.StartAsync();

                LogInfo($"Orphan subagent detected: {displayName} ({Path.GetFileName(transcriptPath)}), spawner: {tracking.SpawnerName ?? "(unknown)"}, desc: \"{tracking.TaskDescription ?? ""}\"");

                // Extract parent session ID from transcript path:
                // .../projects/<project-hash>/<session-id>/subagents/agent-xxx.jsonl
                string parentSessionId = null;
                try
                {
                    var subagentsDir = Path.GetDirectoryName(transcriptPath); // .../subagents
                    if (subagentsDir != null)
                    {
                        var sessionDir = Path.GetDirectoryName(subagentsDir); // .../<session-id>
                        if (sessionDir != null)
                            parentSessionId = Path.GetFileName(sessionDir);
                    }
                }
                catch { /* best effort */ }

                SubagentDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = null,
                    MemberName = displayName,
                    Tailer = tailer,
                    SpawnerName = tracking.SpawnerName,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType,
                    ParentSessionId = parentSessionId,
                    ConfigRoot = root.Plan.Root,
                    SubagentPanelKey = BuildSubagentPanelKey(root.Plan.Root, displayName, root.Plan.IsDefault)
                });
            }
            catch (Exception ex)
            {
                LogError($"Error creating orphan subagent tailer: {ex.Message}");
            }
        }

        /// <summary>
        /// Look up agent metadata from the office hook tracking file.
        /// The SubagentStart hook writes { agentId: { spawnerName, taskDescription, subagentType, ... } }
        /// to mt-office-agents.json.
        /// </summary>
        private (string SpawnerName, string TaskDescription, string SubagentType, string AgentName) LookupFromTracking(string agentFileName)
        {
            try
            {
                string trackingPath = Path.Combine(Path.GetTempPath(), "mt-office-agents.json");
                if (!File.Exists(trackingPath)) return (null, null, null, null);

                // Extract agentId from filename: "agent-a7f57ee" → "a7f57ee"
                string agentId = agentFileName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
                    ? agentFileName.Substring(6)
                    : agentFileName;

                string json = File.ReadAllText(trackingPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty(agentId, out var entry) &&
                    entry.ValueKind == JsonValueKind.Object)
                {
                    string spawnerName = null;
                    string taskDescription = null;
                    string subagentType = null;
                    string agentName = null;

                    if (entry.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            spawnerName = name;
                    }
                    if (entry.TryGetProperty("taskDescription", out var desc))
                        taskDescription = desc.GetString();
                    if (entry.TryGetProperty("subagentType", out var stype))
                        subagentType = stype.GetString();
                    if (entry.TryGetProperty("agentName", out var aname))
                        agentName = aname.GetString();

                    return (spawnerName, taskDescription, subagentType, agentName);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading spawner tracking: {ex.Message}");
            }

            return (null, null, null, null);
        }

        /// <summary>
        /// Reverse-lookup agent metadata from the tracking file by matching transcriptPath.
        /// Used for team agents where we have the transcript path but not the agentId.
        /// </summary>
        private (string SpawnerName, string TaskDescription, string SubagentType, string AgentName) LookupFromTrackingByPath(string transcriptPath)
        {
            try
            {
                string trackingPath = Path.Combine(Path.GetTempPath(), "mt-office-agents.json");
                if (!File.Exists(trackingPath)) return (null, null, null, null);

                string json = File.ReadAllText(trackingPath);
                using var doc = JsonDocument.Parse(json);

                // Normalize path for comparison
                string normalizedTarget = transcriptPath.Replace('/', '\\');

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!prop.Value.TryGetProperty("transcriptPath", out var pathEl)) continue;

                    string entryPath = pathEl.GetString();
                    if (string.IsNullOrEmpty(entryPath)) continue;

                    // Match by normalized path or by substring (transcript paths may vary in prefix)
                    string normalizedEntry = entryPath.Replace('/', '\\');
                    if (!normalizedEntry.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                        && !normalizedTarget.EndsWith(Path.GetFileName(normalizedEntry), StringComparison.OrdinalIgnoreCase))
                        continue;

                    string spawnerName = null;
                    string taskDescription = null;
                    string subagentType = null;
                    string agentName = null;

                    if (prop.Value.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            spawnerName = name;
                    }
                    if (prop.Value.TryGetProperty("taskDescription", out var desc))
                        taskDescription = desc.GetString();
                    if (prop.Value.TryGetProperty("subagentType", out var stype))
                        subagentType = stype.GetString();
                    if (prop.Value.TryGetProperty("agentName", out var aname))
                        agentName = aname.GetString();

                    return (spawnerName, taskDescription, subagentType, agentName);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading tracking by path: {ex.Message}");
            }

            return (null, null, null, null);
        }

        /// <summary>
        /// Look up which terminal (spawner name) created a given team.
        /// Reads the mt-team-spawners.json mapping file written by task-to-agent-hook.js.
        /// That file is keyed on the team NAME alone (the plugin hook writes it; it knows no profile).
        /// So while the same name is active under two roots the entry is ambiguous, and it is not
        /// used for either team: returning null sends the caller to its other fallbacks rather than
        /// risk attaching one profile's team to the other profile's terminal.
        /// </summary>
        private string LookupTeamSpawner(TeamState team)
        {
            string teamName = team.TeamName;
            try
            {
                if (IsTeamNameSharedAcrossRoots(team))
                {
                    Log($" Team name '{teamName}' is active under more than one Claude profile; ignoring the name-keyed spawner mapping");
                    return null;
                }

                string mappingPath = Path.Combine(Path.GetTempPath(), "mt-team-spawners.json");
                if (!File.Exists(mappingPath)) return null;

                string json = File.ReadAllText(mappingPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty(teamName, out var entry) &&
                    entry.ValueKind == JsonValueKind.Object)
                {
                    if (entry.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            return name;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading team-spawner mapping: {ex.Message}");
            }

            return null;
        }

        private bool IsTeamNameSharedAcrossRoots(TeamState team) =>
            _activeTeams.Values.Any(other =>
                !ReferenceEquals(other, team)
                && !ReferenceEquals(other.Root, team.Root)
                && string.Equals(other.TeamName, team.TeamName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Reclaim a transcript file from the orphan watcher. If an orphan tailer already owns
        /// this file, dispose it so the orphan panel is auto-removed from MainForm (via the
        /// Stopped event → HandleEmbeddedStopped closure). The team watcher then creates its
        /// own tailer and panel with the correct team-keyed name.
        /// This fixes the race where the orphan watcher's sync scan claims a team agent's
        /// transcript before the team watcher can match it to a member.
        /// </summary>
        private void ReclaimFromOrphan(string transcriptPath)
        {
            string normalizedPath = NormalizeTranscriptPath(transcriptPath);
            TranscriptTailer orphanTailer = null;

            lock (_trackedSubagentFiles)
            {
                if (_orphanTailers.TryGetValue(normalizedPath, out orphanTailer))
                {
                    _orphanTailers.Remove(normalizedPath);
                }
            }

            if (orphanTailer != null)
            {
                LogInfo($"Reclaiming orphan tailer for team: {orphanTailer.AgentName} ({Path.GetFileName(transcriptPath)})");
                try { orphanTailer.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Stop a specific orphan subagent tailer by transcript path, triggering its Stopped event
        /// which causes the auto-close logic in MainForm to close the agent panel.
        /// Called from the REST API when the SubagentStop hook fires.
        /// </summary>
        public bool StopOrphanTailer(string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath))
                return false;

            // Both store and lookup use NormalizeTranscriptPath so keys always match
            string normalizedPath = NormalizeTranscriptPath(transcriptPath);
            TranscriptTailer tailer = null;
            lock (_trackedSubagentFiles)
            {
                if (_orphanTailers.TryGetValue(normalizedPath, out tailer))
                {
                    _orphanTailers.Remove(normalizedPath);
                }
            }

            if (tailer != null)
            {
                LogInfo($"Stopping orphan tailer via hook: {Path.GetFileName(transcriptPath)}");
                tailer.StopAsync(); // Fires Stopped event → MainForm auto-close
                return true;
            }

            LogInfo($"No orphan tailer found for: {Path.GetFileName(transcriptPath)}");
            return false;
        }

        /// <summary>
        /// Normalize a transcript path so that keys stored and looked up in _orphanTailers
        /// always use the same canonical form, regardless of separator style or relative vs absolute.
        /// </summary>
        private static string NormalizeTranscriptPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        /// <summary>
        /// Stop watching and clean up all resources.
        /// </summary>
        public void StopWatching()
        {
            _started = false;
            try { _refreshTimer?.Dispose(); } catch { }
            _refreshTimer = null;

            var removed = new List<TeamRemovedEventArgs>();
            lock (_rootsLock)
            {
                // Each root takes its own watchers, teams and orphan tailers down with it.
                foreach (var root in _roots.Values.ToList())
                {
                    try
                    {
                        StopRoot(root, removed);
                    }
#pragma warning disable CA1031 // Shutdown: one root that will not stop cleanly must not leave the others running.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogError($"Error while stopping the watchers for '{root.Plan.Root}': {ex.Message}");
                    }
                }
                _roots.Clear();

                foreach (var kvp in _activeTeams.ToArray())
                {
                    if (!_activeTeams.TryRemove(kvp)) continue;
                    try
                    {
                        CleanupTeam(kvp.Value.TeamName, kvp.Value, removed);
                    }
#pragma warning disable CA1031 // As above.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogError($"Error cleaning up team '{kvp.Value.TeamName}': {ex.Message}");
                    }
                }

                List<TranscriptTailer> leftovers;
                lock (_trackedSubagentFiles)
                {
                    leftovers = _orphanTailers.Values.ToList();
                    _orphanTailers.Clear();
                    _trackedSubagentFiles.Clear();
                }
                foreach (var tailer in leftovers)
                {
                    try { tailer.Dispose(); } catch { }
                }
            }

            RaiseTeamsRemoved(removed);
        }

        /// <param name="deferred">When given, the TeamRemoved event is added to it instead of being
        /// raised: the caller holds <c>_rootsLock</c> and raises it after releasing the lock.</param>
        private void CleanupTeam(string teamName, TeamState state, List<TeamRemovedEventArgs> deferred = null)
        {
            // Collect member names before disposal for panel cleanup and orphan prevention
            var memberNames = state.KnownMembers.Select(m => m.name).ToList();

            // Add transcript paths to tracked set so orphan watchers skip them
            lock (_trackedSubagentFiles)
            {
                foreach (var path in state.MatchedTranscripts)
                    _trackedSubagentFiles.Add(path);
            }

            // Add agent names to retired set so CreateOrphanSubagentPanel skips them
            var now = DateTime.UtcNow;
            foreach (var name in memberNames)
                _retiredTeamAgentNames[RetiredKey(state.Root, name)] = now;

            foreach (var tailer in state.MemberTailers.Values)
            {
                try { tailer.Dispose(); } catch { }
            }
            state.MemberTailers.Clear();

            // Also stop any orphan tailers that share this team's transcript paths
            // (catches orphan panels created before the team watcher claimed the file)
            lock (_trackedSubagentFiles)
            {
                var orphansToRemove = new List<string>();
                foreach (var kvp in _orphanTailers)
                {
                    if (state.MatchedTranscripts.Any(p =>
                        string.Equals(NormalizeTranscriptPath(p), kvp.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        orphansToRemove.Add(kvp.Key);
                    }
                }
                // Also match orphans by agent name (catches name-based duplicates). Only orphans under
                // this team's own root: a same-named subagent of another profile is not this team's.
                foreach (var kvp in _orphanTailers)
                {
                    if (!orphansToRemove.Contains(kvp.Key) &&
                        IsUnder(state.Root, kvp.Key) &&
                        memberNames.Any(name => string.Equals(kvp.Value.AgentName, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        orphansToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in orphansToRemove)
                {
                    // CA2000: orphanTailer retrieved from dictionary via Remove(out); disposed within this scope.
#pragma warning disable CA2000
                    if (_orphanTailers.Remove(key, out var orphanTailer))
                    {
                        LogInfo($"Stopping orphan tailer for team agent: {orphanTailer.AgentName}");
                        try { orphanTailer.Dispose(); } catch { }
                    }
#pragma warning restore CA2000
                }
            }

            try { state.ConfigWatcher?.Dispose(); } catch { }
            state.ConfigWatcher = null;

            foreach (var watcher in state.Watchers)
            {
                try { watcher.Dispose(); } catch { }
            }
            state.Watchers.Clear();

            var removedArgs = new TeamRemovedEventArgs
            {
                TeamName = teamName,
                MemberNames = memberNames,
                TranscriptPaths = state.MatchedTranscripts.ToList(),
                ConfigRoot = state.Root.Plan.Root,
                IsDefaultRoot = state.Root.Plan.IsDefault,
                TeamKey = state.TeamKey
            };
            if (deferred != null)
                deferred.Add(removedArgs);
            else
                TeamRemoved?.Invoke(this, removedArgs);

            // Prune expired retired names (older than 60 seconds)
            var cutoff = now.AddSeconds(-60);
            foreach (var kvp in _retiredTeamAgentNames)
            {
                if (kvp.Value < cutoff)
                    _retiredTeamAgentNames.TryRemove(kvp.Key, out _);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                StopWatching();
            }
        }

        /// <summary>
        /// Convert a project path to Claude's slug format.
        /// E.g., H:\DevLaptop\Foo\Bar → H--DevLaptop-Foo-Bar
        /// </summary>
        internal static string GetClaudeProjectSlug(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return "unknown";

            string normalized = projectPath.Replace('/', '\\').TrimEnd('\\');
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == ':')
                {
                    sb.Append("--");
                    if (i + 1 < normalized.Length && normalized[i + 1] == '\\')
                        i++; // Skip the backslash after colon
                }
                else if (c == '\\')
                {
                    sb.Append('-');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Internal state for tracking a single active team.
        /// </summary>
        private class TeamState
        {
            public string TeamName;
            /// <summary>BuildTeamKey(root, name): the key in _activeTeams and in raised events.</summary>
            public string TeamKey;
            /// <summary>The config root this team's config.json lives under. Never null.</summary>
            public RootState Root;
            public string LeadSessionId;
            public string SpawnerName;
            public string LeadCwd;
            public HashSet<string> TrackedMembers = new HashSet<string>();
            public HashSet<string> MatchedTranscripts = new HashSet<string>();
            public List<(string name, long joinedAt)> KnownMembers = new List<(string name, long joinedAt)>();
            public Dictionary<string, TranscriptTailer> MemberTailers = new Dictionary<string, TranscriptTailer>();
            public FileSystemWatcher ConfigWatcher;
            public List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();
            public DateTime LastConfigChange;
            public bool HasSubagentsDirWatcher;
        }

        /// <summary>Everything the service holds for one Claude config directory.</summary>
        private sealed class RootState
        {
            public RootState(TeamWatchRoot plan)
            {
                Plan = plan;
            }

            public readonly TeamWatchRoot Plan;
            public readonly object Sync = new object();

            /// <summary>Set once, when the root stops being watched; delayed callbacks check it.</summary>
            public volatile bool Stopped;

            /// <summary>The teams watcher, or (before the teams folder exists) the one waiting for it to appear.</summary>
            public FileSystemWatcher TeamsRootWatcher;
            public bool TeamsStarted;
            public bool ProjectsStarted;
            public bool MissingLogged;
            public bool ProjectsMissingLogged;

            /// <summary>True once this root's teams folder (or the root itself) was seen NOT to exist:
            /// whatever is in it when it does exist was created during this session. Guarded by Sync.</summary>
            public bool TeamsSeenMissing;
            public readonly List<FileSystemWatcher> ProjectWatchers = new List<FileSystemWatcher>();
        }
    }
}
