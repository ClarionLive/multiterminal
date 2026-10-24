using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Watches each project's GitHub repository for new issues and pull requests (task 92eeeb47).
    /// MT does the polling, not an agent: no tokens are spent and no session is interrupted. Per
    /// project (the project form's Watch GitHub settings): check when a terminal for it starts and/or
    /// on an interval; on new items, notify the Owner and, in Board mode, add one Suggestion card per
    /// item. The first check for a repo only records a baseline, so turning watching on does not
    /// dump the existing backlog on the board. MT never writes to GitHub.
    /// </summary>
    public sealed class GitHubWatchService : IDisposable
    {
        /// <summary>How often the timer looks for projects whose interval has come round.</summary>
        internal static readonly TimeSpan TickPeriod = TimeSpan.FromMinutes(1);

        /// <summary>A terminal start checks again only after this, so opening several panes at once is one check.</summary>
        internal static readonly TimeSpan OnStartDebounce = TimeSpan.FromMinutes(5);

        private readonly ProjectDatabase _db;
        private readonly Func<IReadOnlyList<Project>> _projects;
        private readonly Func<string, string> _gitHubTokenForAccount;
        private readonly GitHubIssueFeed _feed;
        private readonly Func<string, string, string, string> _createSuggestion;
        private readonly Action<string, string> _push;
        private readonly Action<string> _log;

        private readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _lastOnStartUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private Timer _timer;
        private int _disposed;

        /// <param name="gitHubTokenForAccount">Source control account id to its token, or null when
        /// the account is missing, not a GitHub account, or has no token.</param>
        /// <param name="createSuggestion">(title, description, projectId) to the new task's id, or null.</param>
        /// <param name="push">(title, body) to the Owner's phone.</param>
        public GitHubWatchService(
            ProjectDatabase db,
            Func<IReadOnlyList<Project>> projects,
            Func<string, string> gitHubTokenForAccount,
            GitHubIssueFeed feed,
            Func<string, string, string, string> createSuggestion,
            Action<string, string> push,
            Action<string> log)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _projects = projects ?? throw new ArgumentNullException(nameof(projects));
            _gitHubTokenForAccount = gitHubTokenForAccount ?? throw new ArgumentNullException(nameof(gitHubTokenForAccount));
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _createSuggestion = createSuggestion ?? throw new ArgumentNullException(nameof(createSuggestion));
            _push = push ?? throw new ArgumentNullException(nameof(push));
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (_timer != null || Volatile.Read(ref _disposed) != 0) return;
            // First tick after a short delay, so startup is not slowed by network calls.
            _timer = new Timer(_ => { _ = TickAsync(); }, null, TimeSpan.FromSeconds(90), TickPeriod);
        }

        /// <summary>A terminal started in <paramref name="projectId"/>: check now if the project asks for it.</summary>
        public void NotifyTerminalStarted(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId) || Volatile.Read(ref _disposed) != 0) return;
            var project = FindProject(projectId);
            if (project == null || project.GitHubWatchMode == GitHubWatchMode.Off || !project.GitHubCheckOnStart) return;

            var now = DateTime.UtcNow;
            if (_lastOnStartUtc.TryGetValue(projectId, out var last) && now - last < OnStartDebounce) return;
            _lastOnStartUtc[projectId] = now;

            _ = CheckProjectAsync(project, "terminal start");
        }

        private async Task TickAsync()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var now = DateTime.UtcNow;
                foreach (var project in _projects() ?? Array.Empty<Project>())
                {
                    if (project == null || project.GitHubWatchMode == GitHubWatchMode.Off) continue;
                    var repo = GitHubRepoRef.TryParse(project.GitRepoUrl);
                    if (repo == null) continue;

                    var (_, lastChecked) = _db.GetGitHubWatchState(project.Id, repo.FullName);
                    if (IsDue(project.GitHubCheckInterval, lastChecked, now))
                        await CheckProjectAsync(project, "interval").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log("Tick failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One check of one project. Single flight per project; never throws. Returns how many new
        /// items were reported (0 for a first, baseline-only check or a failure).
        /// </summary>
        internal async Task<int> CheckProjectAsync(Project project, string reason)
        {
            if (project == null || !_inFlight.TryAdd(project.Id, 0)) return 0;
            try
            {
                var repo = GitHubRepoRef.TryParse(project.GitRepoUrl);
                if (repo == null)
                {
                    _log($"{project.Name}: Repository URL is not a github.com repository; not watched.");
                    return 0;
                }

                string token = string.IsNullOrWhiteSpace(project.SourceControlAccountId)
                    ? null : _gitHubTokenForAccount(project.SourceControlAccountId);
                if (string.IsNullOrWhiteSpace(token))
                {
                    _db.RecordGitHubWatchCheck(project.Id, repo.FullName, BaselineOrZero(project, repo),
                        "No GitHub source control account with a token is linked to this project.");
                    _log($"{project.Name}: no GitHub account token; not checked.");
                    return 0;
                }

                var result = await _feed.FetchNewestAsync(repo, token).ConfigureAwait(false);
                var (baseline, _) = _db.GetGitHubWatchState(project.Id, repo.FullName);
                if (!result.Ok)
                {
                    _db.RecordGitHubWatchCheck(project.Id, repo.FullName, baseline ?? 0, result.Error);
                    _log($"{project.Name} ({repo.FullName}, {reason}): {result.Error}");
                    return 0;
                }

                var plan = PlanCheck(result.Items, baseline);
                var reported = new List<GitHubItem>();
                foreach (var item in plan.NewItems)
                {
                    // The claim is the one decision of "new": an overlapping check cannot report it too.
                    if (!_db.TryClaimGitHubItem(project.Id, repo.FullName, item.Number, item.IsPullRequest)) continue;
                    reported.Add(item);

                    if (project.GitHubWatchMode == GitHubWatchMode.Board)
                    {
                        string taskId = _createSuggestion(CardTitle(item), CardDescription(item, repo), project.Id);
                        if (!string.IsNullOrEmpty(taskId))
                            _db.SetGitHubItemTask(project.Id, repo.FullName, item.Number, taskId);
                    }
                }

                _db.RecordGitHubWatchCheck(project.Id, repo.FullName, plan.NewBaseline, null);

                if (baseline == null)
                    _log($"{project.Name} ({repo.FullName}): first check; baseline #{plan.NewBaseline}, nothing added.");
                else if (reported.Count > 0)
                {
                    var (title, body) = PushText(project.Name, repo, reported, plan.MayHaveMissed, project.GitHubWatchMode);
                    _push(title, body);
                    _log($"{project.Name} ({repo.FullName}, {reason}): {reported.Count} new.");
                }

                return reported.Count;
            }
            catch (Exception ex)
            {
                _log($"{project?.Name}: check failed: {ex.Message}");
                return 0;
            }
            finally
            {
                _inFlight.TryRemove(project.Id, out _);
            }
        }

        private int BaselineOrZero(Project project, GitHubRepoRef repo) =>
            _db.GetGitHubWatchState(project.Id, repo.FullName).Baseline ?? 0;

        private Project FindProject(string projectId) =>
            (_projects() ?? Array.Empty<Project>()).FirstOrDefault(p => p != null && string.Equals(p.Id, projectId, StringComparison.Ordinal));

        // ── Pure decisions ───────────────────────────────────────────────────────

        internal sealed class CheckPlan
        {
            public IReadOnlyList<GitHubItem> NewItems { get; set; } = Array.Empty<GitHubItem>();
            public int NewBaseline { get; set; }

            /// <summary>The page was full of new items, so there may be more than it holds.</summary>
            public bool MayHaveMissed { get; set; }
        }

        /// <summary>
        /// Which fetched items are new, oldest first, and the baseline after this check. With no
        /// baseline yet (the first check) nothing is new and the newest number becomes the baseline.
        /// The baseline never goes down.
        /// </summary>
        internal static CheckPlan PlanCheck(IReadOnlyList<GitHubItem> items, int? baseline)
        {
            var list = (items ?? Array.Empty<GitHubItem>()).Where(i => i != null && i.Number > 0).ToList();
            int newest = list.Count == 0 ? 0 : list.Max(i => i.Number);

            if (baseline == null)
                return new CheckPlan { NewBaseline = newest };

            var fresh = list.Where(i => i.Number > baseline.Value)
                            .GroupBy(i => i.Number).Select(g => g.First())
                            .OrderBy(i => i.Number).ToList();
            return new CheckPlan
            {
                NewItems = fresh,
                NewBaseline = Math.Max(baseline.Value, newest),
                MayHaveMissed = fresh.Count >= GitHubIssueFeed.PageSize,
            };
        }

        /// <summary>Whether a timed check is due. No interval: never. Never checked: now.</summary>
        internal static bool IsDue(GitHubCheckInterval interval, DateTime? lastCheckedUtc, DateTime nowUtc)
        {
            var period = GitHubWatchSettings.PeriodOf(interval);
            if (period == null) return false;
            if (lastCheckedUtc == null) return true;
            return nowUtc - lastCheckedUtc.Value >= period.Value;
        }

        internal static string Kind(GitHubItem item) => item.IsPullRequest ? "PR" : "Issue";

        internal static string CardTitle(GitHubItem item) =>
            $"[GitHub {(item.IsPullRequest ? "PR" : "issue")} #{item.Number}] {item.Title}";

        internal static string CardDescription(GitHubItem item, GitHubRepoRef repo)
        {
            string who = string.IsNullOrEmpty(item.Author) ? "someone" : "@" + item.Author;
            string when = item.CreatedAtUtc.HasValue ? " on " + item.CreatedAtUtc.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
            string link = item.Url ?? $"https://github.com/{repo.FullName}/{(item.IsPullRequest ? "pull" : "issues")}/{item.Number}";
            return $"{Kind(item)} #{item.Number} in {repo.FullName}, opened by {who}{when}.\n{link}\n\n"
                   + "Added by MultiTerminal's GitHub watch. The title and author come from GitHub and are not checked. "
                   + "Move the card to To Do to work on it; nothing has been posted to GitHub.";
        }

        /// <summary>One push per check: the count, and up to three items by number and title.</summary>
        internal static (string Title, string Body) PushText(string projectName, GitHubRepoRef repo, IReadOnlyList<GitHubItem> items, bool mayHaveMissed, GitHubWatchMode mode)
        {
            int n = items.Count;
            string title = $"{projectName}: {n}{(mayHaveMissed ? "+" : "")} new on GitHub";
            var lines = items.Take(3).Select(i => $"{Kind(i)} #{i.Number}: {i.Title}").ToList();
            if (n > 3) lines.Add($"and {n - 3} more");
            if (mayHaveMissed) lines.Add($"There may be more; see github.com/{repo.FullName}.");
            if (mode == GitHubWatchMode.Board) lines.Add("Added to the board's Suggestion column.");
            return (title, string.Join("\n", lines));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
