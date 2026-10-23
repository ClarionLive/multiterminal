#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using RepoWatchState = MultiTerminal.MCPServer.Models.RepoWatchState;
using RepoWatchStatus = MultiTerminal.MCPServer.Models.RepoWatchStatus;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What <see cref="RepoActivityService"/> needs from the app (in production, MainForm). Every member is
    /// called from thread-pool threads, so an implementation must be safe to call off the UI thread.
    /// </summary>
    public interface IRepoActivityHost
    {
        /// <summary>The projects with <see cref="Project.IsRepoWatchEnabled"/>.</summary>
        IReadOnlyList<Project> GetWatchedProjects();

        /// <summary>The project's current settings, or null when it no longer exists.</summary>
        Project? GetProject(string projectId);

        /// <summary>True while a terminal pane is open on this project.</summary>
        bool HasOpenPane(string projectId);

        /// <summary>
        /// The token of the project's source control account when that account is a GitHub one, else null.
        /// Never logged, never put in a message.
        /// </summary>
        string? GetGitHubToken(Project project);
    }

    /// <summary>The one GitHub call the service makes; <see cref="GitHubRestClient"/> in production.</summary>
    public interface IRepoIssuesClient
    {
        /// <summary>See <see cref="GitHubRestClient.GetOpenIssuesAsync"/>.</summary>
        Task<RepoIssuesResult> GetOpenIssuesAsync(string owner, string repo, string? token, string? etag, CancellationToken ct = default);
    }

    /// <summary>
    /// Decides WHEN each watched project's GitHub repositories are checked, and WHAT counts as new
    /// (task df58b35d, GitHub #53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which repositories.</b> The project's "Repositories" setting when it lists any; otherwise every
    /// github.com remote of the project folder (a fork has two); otherwise the project's Git URL when it
    /// is a github.com one; otherwise none, shown as NoRepos.
    /// </para>
    /// <para>
    /// <b>When.</b> Once for every watched project at <see cref="Start"/>, and again whenever a project is
    /// launched (<see cref="NotifyProjectLaunched"/>) or its settings are saved (<see cref="NotifySettingsChanged"/>,
    /// which queues behind a running check instead of joining it). After that a 60s tick checks a project only while a
    /// pane is open on it and only when its interval has passed since its last check. Interval 0 means
    /// "startup and launch only", never on the tick. Without a token GitHub allows 60 requests an hour, so
    /// an unauthenticated project is never checked on the tick more often than every 15 minutes.
    /// <see cref="CheckNowAsync"/> (the user's "Check now") ignores both the interval and that floor.
    /// </para>
    /// <para>
    /// <b>Rate limits.</b> A rate-limited repository is not asked again before the time GitHub gave, by
    /// any path, "Check now" included: asking early only extends the penalty. The backoff is held in
    /// memory; after a restart the startup sweep asks once and learns it again.
    /// </para>
    /// <para>
    /// <b>No overlap.</b> At most one check per project is in flight; a second request for the same
    /// project joins the one running. Projects are checked independently, so a slow one does not hold up
    /// another, and a tick that finds the previous one still scheduling does nothing.
    /// </para>
    /// <para>
    /// <b>What is new.</b> Issue and PR numbers share one increasing sequence per repository, so "seen" is a
    /// high-water mark: an open item is new when its number is above it. The first successful check of a
    /// repository sets the mark to the current maximum, so a newly watched repository reports nothing as
    /// new; <see cref="MarkAllSeen"/> raises it. A 304 keeps the stored list; a failure keeps the previous
    /// list too, so the last known items stay visible next to the error.
    /// </para>
    /// <para>
    /// Nothing escapes the timer or a fire-and-forget launch check: failures are logged through the
    /// injected logger and the next check tries again. The token is never logged.
    /// <c>MULTITERMINAL_REPO_WATCH</c> = <c>0</c>/<c>false</c>/<c>off</c>/<c>no</c>/<c>disabled</c> turns
    /// the whole service off.
    /// </para>
    /// </remarks>
    public sealed class RepoActivityService : IDisposable
    {
        /// <summary>How often the tick runs.</summary>
        internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

        /// <summary>The shortest tick interval for a project with no token (GitHub allows 60 requests an hour).</summary>
        internal const int UnauthenticatedFloorMinutes = 15;

        private const string EnvVar = "MULTITERMINAL_REPO_WATCH";

        private readonly IRepoActivityHost _host;
        private readonly TaskDatabase _db;
        private readonly IRepoIssuesClient _client;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<GitRemoteRepo>>> _readRemotes;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Action<string>? _log;

        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _stopToken;

        // Guards every dictionary below and every ProjectRuntime's fields. Held only briefly, never across an await.
        private readonly object _gate = new();
        private readonly Dictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _lastCheckStarted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProjectRuntime> _runtime = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _backoffUntil = new(StringComparer.Ordinal);

        private Timer? _timer;
        private int _ticking;
        private volatile bool _disposed;

        public RepoActivityService(
            IRepoActivityHost host,
            TaskDatabase db,
            IRepoIssuesClient client,
            Func<string, CancellationToken, Task<IReadOnlyList<GitRemoteRepo>>>? readRemotes = null,
            Func<DateTimeOffset>? utcNow = null,
            Action<string>? log = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _readRemotes = readRemotes ?? GitRemoteReader.GetGitHubRemotesAsync;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _log = log;
            _stopToken = _stop.Token;
        }

        /// <summary>
        /// Raised after any check completes (success or failure), when a check starts (Checking), and on
        /// <see cref="MarkAllSeen"/>. Raised on whatever thread did the work: subscribers that touch UI
        /// must marshal to the UI thread themselves.
        /// </summary>
        public event EventHandler<RepoActivityChangedEventArgs>? RepoActivityChanged;

        /// <summary>True while the tick timer is armed: after a <see cref="Start"/> that was not refused, before <see cref="Dispose"/>.</summary>
        public bool IsRunning => !_disposed && Volatile.Read(ref _timer) != null;

        /// <summary>True when <c>MULTITERMINAL_REPO_WATCH</c> turns the service off.</summary>
        public static bool IsDisabled()
        {
            string? raw = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "0":
                case "false":
                case "off":
                case "no":
                case "disabled":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Runs the startup sweep and arms the 60s tick. Safe to call more than once; later calls are ignored.</summary>
        public void Start()
        {
            if (_disposed || Volatile.Read(ref _timer) != null) return;

            if (IsDisabled())
            {
                Log($"RepoActivityService disabled by {EnvVar}.");
                return;
            }

            var timer = new Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Interlocked.CompareExchange(ref _timer, timer, null) != null)
            {
                timer.Dispose();
                return;
            }

            Log("RepoActivityService started.");
            _ = Task.Run(StartupSweepAsync);
            timer.Change(TickInterval, TickInterval);
        }

        /// <summary>A project was launched: check it now (fire and forget; joins a check already running for it).</summary>
        public void NotifyProjectLaunched(string projectId)
        {
            _ = ScheduleLaunchCheck(projectId);
        }

        /// <summary>
        /// The project's settings were saved (fire and forget): check it now under the new settings. A check
        /// already running was started under the old ones, so instead of joining it, one more check is queued
        /// to start when it ends. Like a launch, it ignores the interval and the unauthenticated floor but not
        /// a rate-limit backoff.
        /// </summary>
        public void NotifySettingsChanged(string projectId)
        {
            _ = ScheduleSettingsCheck(projectId);
        }

        /// <summary>
        /// The user's "Check now": checks the project immediately, ignoring its interval and the
        /// unauthenticated floor, but not a rate-limit backoff. Joins a check already running for it.
        /// Never throws for a check failure (the outcome is in <see cref="GetSummary"/>); only
        /// <paramref name="ct"/> cancels the wait, not the check.
        /// </summary>
        public Task CheckNowAsync(string projectId, CancellationToken ct = default)
        {
            if (_disposed || IsDisabled() || string.IsNullOrWhiteSpace(projectId)) return Task.CompletedTask;
            return StartCheck(projectId).WaitAsync(ct);
        }

        /// <summary>The project's activity from memory and the database. Never touches the network or git.</summary>
        public RepoActivitySummary GetSummary(string projectId)
        {
            Project? project = string.IsNullOrWhiteSpace(projectId) ? null : SafeGetProject(projectId);
            bool disabled = IsDisabled();
            if (project == null || !project.IsRepoWatchEnabled || disabled)
            {
                return new RepoActivitySummary
                {
                    ProjectId = projectId ?? string.Empty,
                    Watching = false,
                    State = RepoActivityState.Disabled,
                    Reason = disabled
                        ? $"Repository watching is turned off by {EnvVar}."
                        : project == null
                            ? "The project was not found."
                            : "Watching issues and pull requests is off for this project.",
                };
            }

            string settingsKey = ResolutionKey(project.RepoWatchRepos);
            bool checking;
            HashSet<string>? pending;
            IReadOnlyList<GitHubRepoRef>? resolved;
            bool resolvedThisRun;
            lock (_gate)
            {
                if (_runtime.TryGetValue(projectId, out var rt))
                {
                    checking = rt.Checking;
                    pending = rt.PendingRepos == null ? null : new HashSet<string>(rt.PendingRepos, StringComparer.Ordinal);
                    resolvedThisRun = rt.ResolvedRepos != null;
                    // A resolution made under other Repositories settings says nothing about these ones.
                    resolved = string.Equals(rt.ResolvedFrom, settingsKey, StringComparison.Ordinal) ? rt.ResolvedRepos : null;
                }
                else
                {
                    checking = false;
                    pending = null;
                    resolved = null;
                    resolvedThisRun = false;
                }
            }

            var states = _db.GetRepoWatchStates(projectId)
                .ToDictionary(s => s.Repo, StringComparer.Ordinal);

            // The settings list is cheap and always current. The folder's remotes are known only from a check
            // made under the CURRENT settings: one made under an older list is stale, and shows as not checked
            // (or Checking) until the next check resolves again. Before any check this run, whatever an earlier
            // run stored: a successful resolution prunes the rows of repositories it no longer includes (see
            // RunCheckAsync), so those are the last resolution's repositories.
            IReadOnlyList<string> names = RepoWatchSettings.ParseRepos(project.RepoWatchRepos);
            bool noRepos = false;
            if (names.Count == 0)
            {
                if (resolved != null)
                {
                    names = resolved.Select(r => r.FullName).ToList();
                    noRepos = names.Count == 0;
                }
                else if (!resolvedThisRun)
                {
                    names = states.Keys.ToList();
                }
            }

            var repos = new List<RepoActivityRepo>(names.Count);
            foreach (string name in names)
            {
                string key = TaskDatabase.NormalizeWatchedRepo(name);
                states.TryGetValue(key, out var state);
                bool repoChecking = checking && (pending == null || pending.Contains(key));
                repos.Add(BuildRepo(projectId, name, key, state, repoChecking));
            }

            var entries = repos.SelectMany(r => r.Items).ToList();
            var (aggregate, reason) = Aggregate(repos, noRepos, checking);
            return new RepoActivitySummary
            {
                ProjectId = projectId,
                Watching = true,
                State = aggregate,
                Reason = reason,
                OpenIssues = entries.Count(e => !e.Item.IsPullRequest),
                OpenPullRequests = entries.Count(e => e.Item.IsPullRequest),
                NewCount = entries.Count(e => e.IsNew),
                Truncated = repos.Any(r => r.Truncated),
                LastCheckedUtc = repos.Max(r => r.LastCheckedUtc),
                Repos = repos,
            };
        }

        /// <summary>
        /// The user has seen everything listed now: raises each repository's seen mark to the highest number
        /// currently known for it, then raises <see cref="RepoActivityChanged"/>.
        /// </summary>
        public void MarkAllSeen(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId)) return;
            try
            {
                foreach (var repo in GetSummary(projectId).Repos)
                {
                    if (repo.Items.Count > 0)
                    {
                        _db.MarkRepoSeen(projectId, repo.FullName, repo.Items.Max(e => e.Item.Number));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService could not mark project {projectId} as seen: {ex.Message}");
            }

            RaiseChanged(projectId);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Interlocked.Exchange(ref _timer, null)?.Dispose();
            _stop.Cancel();
            _stop.Dispose();
        }

        /// <summary>
        /// One tick: starts a check of every watched project that is due (see the class summary) and
        /// returns a task that completes when those checks do. Never throws. Internal so tests drive it
        /// instead of the timer.
        /// </summary>
        internal Task TickAsync()
        {
            if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1) return Task.CompletedTask;

            var started = new List<Task>();
            try
            {
                DateTimeOffset now = _utcNow();
                var projects = (_host.GetWatchedProjects() ?? Array.Empty<Project>())
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id) && p.IsRepoWatchEnabled)
                    .ToList();
                ForgetUnwatched(projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal), now.UtcDateTime);

                foreach (var project in projects)
                {

                    int interval = project.EffectiveRepoWatchIntervalMinutes;
                    if (interval <= 0) continue; // startup and launch only
                    if (!_host.HasOpenPane(project.Id)) continue;
                    if (SafeGetToken(project) == null) interval = Math.Max(interval, UnauthenticatedFloorMinutes);

                    bool due;
                    lock (_gate)
                    {
                        due = !_lastCheckStarted.TryGetValue(project.Id, out var last)
                              || now - last >= TimeSpan.FromMinutes(interval);
                    }

                    if (due) started.Add(StartCheck(project.Id));
                }
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService tick failed, will retry next tick: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }

            return Task.WhenAll(started);
        }

        /// <summary>Checks every watched project once, whether or not a pane is open. Never throws.</summary>
        internal Task StartupSweepAsync()
        {
            var started = new List<Task>();
            try
            {
                foreach (var project in _host.GetWatchedProjects() ?? Array.Empty<Project>())
                {
                    if (project != null && !string.IsNullOrWhiteSpace(project.Id) && project.IsRepoWatchEnabled)
                    {
                        started.Add(StartCheck(project.Id));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService startup sweep failed: {ex.Message}");
            }

            return Task.WhenAll(started);
        }

        /// <summary><see cref="NotifyProjectLaunched"/>, returning the check so tests can wait for it.</summary>
        internal Task ScheduleLaunchCheck(string projectId)
        {
            if (_disposed || IsDisabled() || string.IsNullOrWhiteSpace(projectId)) return Task.CompletedTask;
            return StartCheck(projectId);
        }

        /// <summary>
        /// <see cref="NotifySettingsChanged"/>, returning a task that completes when the check under the new
        /// settings does (after the running one and its queued successor, when one was running).
        /// </summary>
        internal Task ScheduleSettingsCheck(string projectId)
        {
            if (_disposed || IsDisabled() || string.IsNullOrWhiteSpace(projectId)) return Task.CompletedTask;

            // Watching was just turned off (or the project is gone): nothing to check. The next tick
            // forgets its in-memory state.
            if (SafeGetProject(projectId) is not { IsRepoWatchEnabled: true }) return Task.CompletedTask;
            Task running;
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;
                if (!_inFlight.TryGetValue(projectId, out running!)) return StartCheck(projectId);
                GetRuntime(projectId).RecheckWhenDone = true;
            }

            // RunCheckAsync's finally swaps in the queued check under _gate before the running task completes,
            // so by the time this continuation runs the in-flight entry is that check (or it already ended).
            return running.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        return _inFlight.TryGetValue(projectId, out var next) ? next : Task.CompletedTask;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }

        private void OnTimer()
        {
            // The checks the tick starts finish on their own; RunCheckAsync never faults.
            _ = TickAsync();
        }

        /// <summary>Starts a check of the project, or returns the one already in flight.</summary>
        private Task StartCheck(string projectId)
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;
                if (_inFlight.TryGetValue(projectId, out var running)) return running;

                _lastCheckStarted[projectId] = _utcNow();
                // RunCheckAsync's finally takes _gate to remove this entry, so it cannot run before the add below.
                var task = Task.Run(() => RunCheckAsync(projectId));
                _inFlight[projectId] = task;
                return task;
            }
        }

        private async Task RunCheckAsync(string projectId)
        {
            bool raise = false;
            try
            {
                var project = _host.GetProject(projectId);
                if (project == null || !project.IsRepoWatchEnabled) return;

                SetChecking(projectId, checking: true, pending: null);
                raise = true;
                RaiseChanged(projectId);

                var (repos, definitive) = await ResolveReposAsync(project).ConfigureAwait(false);
                lock (_gate)
                {
                    var rt = GetRuntime(projectId);
                    rt.ResolvedRepos = repos;
                    rt.ResolvedFrom = ResolutionKey(project.RepoWatchRepos);
                    rt.PendingRepos = new HashSet<string>(repos.Select(r => TaskDatabase.NormalizeWatchedRepo(r.FullName)), StringComparer.Ordinal);
                }

                if (repos.Count == 0) return;

                // Forget repositories this project no longer watches (removed from the list, or a remote that
                // is gone), so neither a restart nor "Mark all seen" brings them back. Only after a DEFINITIVE
                // resolution: see ResolveReposAsync.
                if (definitive) PruneStates(project, repos);

                string? token = SafeGetToken(project);
                foreach (var repo in repos)
                {
                    _stopToken.ThrowIfCancellationRequested();
                    string key = TaskDatabase.NormalizeWatchedRepo(repo.FullName);
                    try
                    {
                        if (!IsBackedOff(projectId, key))
                        {
                            await CheckRepoAsync(project, repo, key, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // An exception message is not ours to vouch for: it may echo a request header.
                        Log($"RepoActivityService check of {repo.FullName} for project {project.Name} failed: {GitHubRestClient.Scrub(ex.Message, token)}");
                        TrySave(projectId, repo.FullName, RepoWatchStatus.Error, "The check failed inside MultiTerminal; see the debug log.");
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            GetRuntime(projectId).PendingRepos?.Remove(key);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
            {
                // Disposed while checking.
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService check of project {projectId} failed: {ex.Message}");
            }
            finally
            {
                SetChecking(projectId, checking: false, pending: null);
                lock (_gate)
                {
                    _inFlight.Remove(projectId);

                    // The settings changed while this check ran under the old ones (ScheduleSettingsCheck).
                    var rt = GetRuntime(projectId);
                    if (rt.RecheckWhenDone)
                    {
                        rt.RecheckWhenDone = false;
                        if (!_disposed) StartCheck(projectId);
                    }
                }

                if (raise && !_disposed) RaiseChanged(projectId);
            }
        }

        private async Task CheckRepoAsync(Project project, GitHubRepoRef repo, string key, string? token)
        {
            var stored = _db.GetRepoWatchState(project.Id, repo.FullName);
            var result = await _client.GetOpenIssuesAsync(repo.Owner, repo.Name, token, stored?.ETag, _stopToken).ConfigureAwait(false)
                         ?? new RepoIssuesResult { Status = RepoIssuesStatus.Failed, Message = "No answer from the GitHub client." };
            DateTime now = _utcNow().UtcDateTime;

            // Stored and shown to the user, so scrubbed here whatever the client did.
            string? message = result.Message == null ? null : GitHubRestClient.Scrub(result.Message, token);

            switch (result.Status)
            {
                case RepoIssuesStatus.Ok:
                    var items = result.Items ?? Array.Empty<RepoActivityItem>();
                    _db.SaveRepoWatchFetch(project.Id, repo.FullName, result.ETag, now, RepoWatchStatus.Ok, null, JsonSerializer.Serialize(items));
                    _db.InitializeRepoSeenIfUnset(project.Id, repo.FullName, items.Count == 0 ? 0 : items.Max(i => i.Number));
                    ClearBackoff(project.Id, key);
                    break;

                case RepoIssuesStatus.NotModified:
                    _db.SaveRepoWatchFetch(project.Id, repo.FullName, result.ETag, now, RepoWatchStatus.Ok, null, null);
                    ClearBackoff(project.Id, key);
                    break;

                case RepoIssuesStatus.RateLimited:
                    DateTime until = BackoffUntil(result.RetryAfterUtc, now);
                    lock (_gate)
                    {
                        _backoffUntil[BackoffKey(project.Id, key)] = until;
                    }

                    _db.SaveRepoWatchFetch(project.Id, repo.FullName, null, now, RepoWatchStatus.RateLimited, message, null);
                    Log($"RepoActivityService: {repo.FullName} rate-limited until {until:u}.");
                    break;

                case RepoIssuesStatus.Unauthorized:
                case RepoIssuesStatus.NotFound:
                    _db.SaveRepoWatchFetch(project.Id, repo.FullName, null, now, RepoWatchStatus.NoAccess, message, null);
                    Log($"RepoActivityService: {repo.FullName}: {message}");
                    break;

                default:
                    _db.SaveRepoWatchFetch(project.Id, repo.FullName, null, now, RepoWatchStatus.Error, message, null);
                    Log($"RepoActivityService: {repo.FullName}: {message}");
                    break;
            }
        }

        /// <summary>
        /// The settings list, else the folder's github.com remotes, else the Git URL, else none.
        /// <c>Definitive</c> says whether the set may be used to forget other repositories: true for a
        /// non-empty settings list and for a non-empty remotes read. False for the Git URL fallback and for
        /// an empty set, because <see cref="GitRemoteReader"/> answers a failed or timed-out read with an
        /// empty list, so "no remotes" cannot be told from "could not read them", and the Git URL is used
        /// exactly then; forgetting a fork's other remote on a slow git would reset its seen mark.
        /// </summary>
        private async Task<(IReadOnlyList<GitHubRepoRef> Repos, bool Definitive)> ResolveReposAsync(Project project)
        {
            var listed = new List<GitHubRepoRef>();
            foreach (string name in RepoWatchSettings.ParseRepos(project.RepoWatchRepos))
            {
                if (GitHubRepoRef.TryParseFullName(name, out var parsed)) listed.Add(parsed);
            }

            if (listed.Count > 0) return (listed, true);

            string? folder = string.IsNullOrWhiteSpace(project.SourcePath) ? project.Path : project.SourcePath;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                IReadOnlyList<GitRemoteRepo>? remotes = null;
                try
                {
                    remotes = await _readRemotes(folder, _stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"RepoActivityService could not read the git remotes of project {project.Name}: {ex.Message}");
                }

                var seen = new HashSet<GitHubRepoRef>();
                var fromRemotes = (remotes ?? Array.Empty<GitRemoteRepo>()).Select(r => r.Repo).Where(seen.Add).ToList();
                if (fromRemotes.Count > 0) return (fromRemotes, true);
            }

            return GitHubRepoRef.TryParse(project.GitRepoUrl, out var fromUrl)
                ? (new[] { fromUrl }, false)
                : (Array.Empty<GitHubRepoRef>(), false);
        }

        /// <summary>
        /// The Repositories setting as a comparable key: the parsed names, lower-cased, in order. Empty for
        /// no list (the remotes are used). What a resolution was made under, so a later change is seen.
        /// </summary>
        private static string ResolutionKey(string? repoWatchRepos)
            => string.Join(",", RepoWatchSettings.ParseRepos(repoWatchRepos).Select(TaskDatabase.NormalizeWatchedRepo));

        private void PruneStates(Project project, IReadOnlyList<GitHubRepoRef> repos)
        {
            try
            {
                int removed = _db.DeleteRepoWatchStatesExcept(project.Id, repos.Select(r => r.FullName).ToList());
                if (removed > 0) Log($"RepoActivityService: forgot {removed} repository state(s) project {project.Name} no longer watches.");
            }
            catch (Exception ex)
            {
                // Best effort: a leftover row can only reappear after a restart, until the next successful prune.
                Log($"RepoActivityService could not forget unwatched repositories of project {project.Name}: {ex.Message}");
            }
        }

        private RepoActivityRepo BuildRepo(string projectId, string fullName, string key, RepoWatchState? state, bool checking)
        {
            var items = ReadSnapshot(state?.SnapshotJson, fullName);
            long? seen = state?.SeenMaxNumber;
            var entries = items
                .Select(i => new RepoActivityEntry(i, seen.HasValue && i.Number > seen.Value, UrlFor(fullName, i)))
                .OrderByDescending(e => e.IsNew)
                .ThenByDescending(e => e.Item.Number)
                .ToList();

            RepoActivityState repoState;
            string? reason;
            if (checking)
            {
                repoState = RepoActivityState.Checking;
                reason = RepoActivityReasons.For(repoState);
            }
            else if (state?.LastCheckedAt == null)
            {
                repoState = RepoActivityState.NeverChecked;
                reason = RepoActivityReasons.For(repoState);
            }
            else
            {
                repoState = state.LastStatus switch
                {
                    RepoWatchStatus.Ok => RepoActivityState.Ok,
                    RepoWatchStatus.RateLimited => RepoActivityState.RateLimited,
                    RepoWatchStatus.NoAccess => RepoActivityState.NoAccess,
                    _ => RepoActivityState.Error,
                };
                reason = repoState == RepoActivityState.Ok ? null : state.LastMessage ?? RepoActivityReasons.For(repoState);
                if (repoState == RepoActivityState.RateLimited && GetBackoff(projectId, key) is DateTime until)
                    reason += $" Next try after {until.ToLocalTime():HH:mm}.";
            }

            return new RepoActivityRepo
            {
                FullName = fullName,
                State = repoState,
                Reason = reason,
                LastCheckedUtc = state?.LastCheckedAt,
                Truncated = items.Count >= GitHubRestClient.PageSize,
                Items = entries,
            };
        }

        private static (RepoActivityState State, string? Reason) Aggregate(List<RepoActivityRepo> repos, bool noRepos, bool checking)
        {
            if (repos.Count == 0)
            {
                var none = noRepos ? RepoActivityState.NoRepos
                    : checking ? RepoActivityState.Checking
                    : RepoActivityState.NeverChecked;
                return (none, RepoActivityReasons.For(none));
            }

            var problems = repos.Where(r => ProblemRank(r.State) > 0).OrderByDescending(r => ProblemRank(r.State)).ToList();
            if (problems.Count > 0)
            {
                var worst = problems[0];
                string reason = $"{worst.FullName}: {worst.Reason}";
                if (problems.Count > 1) reason += $" (+{problems.Count - 1} more)";
                return (worst.State, reason);
            }

            if (repos.Any(r => r.State == RepoActivityState.Ok)) return (RepoActivityState.Ok, null);
            var idle = repos.Any(r => r.State == RepoActivityState.Checking) ? RepoActivityState.Checking : RepoActivityState.NeverChecked;
            return (idle, RepoActivityReasons.For(idle));
        }

        /// <summary>How bad a failed state is; 0 for a state that is not a failure.</summary>
        private static int ProblemRank(RepoActivityState state) => state switch
        {
            RepoActivityState.NoAccess => 3,
            RepoActivityState.Error => 2,
            RepoActivityState.RateLimited => 1,
            _ => 0,
        };

        // Empty (a disabled menu row) only for a name that does not parse, which ReadSnapshot has already excluded.
        private static string UrlFor(string fullName, RepoActivityItem item)
            => item.HtmlUrl
               ?? (GitHubRepoRef.TryParseFullName(fullName, out var repo) ? repo.ItemUrl(item.Number, item.IsPullRequest) : string.Empty);

        /// <summary>
        /// The stored list, each item cleaned again (<see cref="GitHubRestClient.CleanStored"/>): the
        /// column is not trusted just because this service wrote it.
        /// </summary>
        private IReadOnlyList<RepoActivityItem> ReadSnapshot(string? json, string fullName)
        {
            if (string.IsNullOrEmpty(json) || !GitHubRepoRef.TryParseFullName(fullName, out var repo))
                return Array.Empty<RepoActivityItem>();
            try
            {
                var items = JsonSerializer.Deserialize<List<RepoActivityItem>>(json);
                return items == null
                    ? Array.Empty<RepoActivityItem>()
                    : items.Where(i => i != null).Select(i => GitHubRestClient.CleanStored(i, repo)).ToList();
            }
            catch (JsonException ex)
            {
                Log($"RepoActivityService: the stored list for {fullName} could not be read: {ex.Message}");
                return Array.Empty<RepoActivityItem>();
            }
        }

        private void SetChecking(string projectId, bool checking, HashSet<string>? pending)
        {
            lock (_gate)
            {
                var rt = GetRuntime(projectId);
                rt.Checking = checking;
                rt.PendingRepos = pending;
            }
        }

        /// <summary>Caller holds <see cref="_gate"/>.</summary>
        private ProjectRuntime GetRuntime(string projectId)
        {
            if (!_runtime.TryGetValue(projectId, out var rt))
            {
                rt = new ProjectRuntime();
                _runtime[projectId] = rt;
            }

            return rt;
        }

        private static string BackoffKey(string projectId, string repoKey) => projectId + "\n" + repoKey;

        /// <summary>The longest a rate limit may pause a repository. GitHub's own reset window is an hour.</summary>
        internal static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

        /// <summary>
        /// When a rate-limited repository may be tried again: the time GitHub gave, or
        /// <see cref="GitHubRestClient.FallbackRetryDelay"/> when it gave none, but never later than
        /// <see cref="MaxBackoff"/> from now. A wrong or far-future reset (the backoff lives only in memory)
        /// would otherwise stop that repository's checks until MT restarts.
        /// </summary>
        internal static DateTime BackoffUntil(DateTime? retryAfterUtc, DateTime nowUtc)
        {
            DateTime until = retryAfterUtc ?? nowUtc + GitHubRestClient.FallbackRetryDelay;
            DateTime latest = nowUtc + MaxBackoff;
            return until > latest ? latest : until;
        }

        /// <summary>
        /// Drops in-memory state that can no longer matter, so the maps do not grow with every project
        /// ever watched: the last-check time and runtime of any project not in <paramref name="watchedIds"/>
        /// (deleted, or watching turned off) unless a check of it is still running, and every rate-limit
        /// backoff that has expired. An unexpired backoff is kept even for an unwatched project, so turning
        /// watching off and on again cannot call GitHub inside a rate limit.
        /// </summary>
        internal void ForgetUnwatched(IReadOnlySet<string> watchedIds, DateTime nowUtc)
        {
            lock (_gate)
            {
                foreach (var id in _lastCheckStarted.Keys.Concat(_runtime.Keys).Distinct(StringComparer.Ordinal).ToList())
                {
                    if (watchedIds.Contains(id) || _inFlight.ContainsKey(id)) continue;
                    _lastCheckStarted.Remove(id);
                    _runtime.Remove(id);
                }

                foreach (var entry in _backoffUntil.Where(b => b.Value <= nowUtc).ToList())
                    _backoffUntil.Remove(entry.Key);
            }
        }

        /// <summary>For tests: how many projects and backoffs the service still remembers.</summary>
        internal (int Projects, int Backoffs) RememberedCounts()
        {
            lock (_gate)
            {
                return (_lastCheckStarted.Keys.Concat(_runtime.Keys).Distinct(StringComparer.Ordinal).Count(), _backoffUntil.Count);
            }
        }

        private bool IsBackedOff(string projectId, string repoKey)
        {
            DateTime? until = GetBackoff(projectId, repoKey);
            return until.HasValue && until.Value > _utcNow().UtcDateTime;
        }

        private DateTime? GetBackoff(string projectId, string repoKey)
        {
            lock (_gate)
            {
                return _backoffUntil.TryGetValue(BackoffKey(projectId, repoKey), out var until) ? until : null;
            }
        }

        private void ClearBackoff(string projectId, string repoKey)
        {
            lock (_gate)
            {
                _backoffUntil.Remove(BackoffKey(projectId, repoKey));
            }
        }

        private void TrySave(string projectId, string repo, string status, string message)
        {
            try
            {
                _db.SaveRepoWatchFetch(projectId, repo, null, _utcNow().UtcDateTime, status, message, null);
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService could not record the failure for {repo}: {ex.Message}");
            }
        }

        private Project? SafeGetProject(string projectId)
        {
            try
            {
                return _host.GetProject(projectId);
            }
            catch (Exception ex)
            {
                Log($"RepoActivityService could not read project {projectId}: {ex.Message}");
                return null;
            }
        }

        private string? SafeGetToken(Project project)
        {
            try
            {
                string? token = _host.GetGitHubToken(project);
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch (Exception ex)
            {
                // The exception type only: a credential-store message is not worth the risk of quoting.
                Log($"RepoActivityService could not read the GitHub token for project {project.Name} ({ex.GetType().Name}); checking without one.");
                return null;
            }
        }

        private void RaiseChanged(string projectId)
        {
            var handlers = RepoActivityChanged;
            if (handlers == null) return;

            var args = new RepoActivityChangedEventArgs(projectId);
            foreach (EventHandler<RepoActivityChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    Log($"RepoActivityService: a RepoActivityChanged subscriber failed: {ex.Message}");
                }
            }
        }

        private void Log(string message)
        {
            // Runs on timer and pool threads; a logger torn down at shutdown must not take the process with it.
            try
            {
                _log?.Invoke(message);
            }
            catch
            {
                // Nothing useful can be done with a failure to log.
            }
        }

        /// <summary>A project's in-memory check state. Every field is guarded by <see cref="_gate"/>.</summary>
        private sealed class ProjectRuntime
        {
            public bool Checking;

            /// <summary>The repositories of the running check not finished yet; null = all of them (still resolving).</summary>
            public HashSet<string>? PendingRepos;

            /// <summary>The repositories the last check resolved; null = not resolved this run.</summary>
            public IReadOnlyList<GitHubRepoRef>? ResolvedRepos;

            /// <summary>
            /// The Repositories setting (as <see cref="ResolutionKey"/>) <see cref="ResolvedRepos"/> was resolved
            /// under. When it differs from the project's current setting, the resolution is stale.
            /// </summary>
            public string? ResolvedFrom;

            /// <summary>The settings changed during the running check: start another when it ends.</summary>
            public bool RecheckWhenDone;
        }
    }
}
