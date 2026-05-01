using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibGit2Sharp;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Working-tree change kind for a single file. Maps LibGit2Sharp's flag-set
    /// <see cref="FileStatus"/> down to a single, render-friendly category for the
    /// HUD Git tab and the dashboard widget.
    /// <c>Unknown</c> covers any future LibGit2Sharp flag we haven't classified
    /// — surfaced explicitly so the UI shows it as such instead of silently
    /// mislabeling as <c>Modified</c>.
    /// </summary>
    public enum GitFileChangeKind
    {
        Modified,
        Added,
        Deleted,
        Untracked,
        Renamed,
        TypeChanged,
        Conflicted,
        Unknown,
    }

    /// <summary>
    /// Single working-tree change entry. Path is repo-relative and forward-slash
    /// (LibGit2Sharp's convention).
    /// </summary>
    public sealed class GitFileStatus
    {
        public string Path { get; set; }
        public GitFileChangeKind Kind { get; set; }
        public int LinesAdded { get; set; }
        public int LinesDeleted { get; set; }
    }

    /// <summary>
    /// Single commit summary for the recent-commits log. <c>Subject</c>,
    /// <c>AuthorName</c>, and <c>CoAuthors</c> are sanitized at the producer
    /// (control chars stripped, bidi-override codepoints removed, length-capped)
    /// so downstream renderers don't have to repeat the work.
    /// </summary>
    public sealed class GitCommitInfo
    {
        public string ShortSha { get; set; }
        public string FullSha { get; set; }
        public string Subject { get; set; }
        public string AuthorName { get; set; }
        public string AuthorEmail { get; set; }
        public DateTimeOffset When { get; set; }
        public IReadOnlyList<string> CoAuthors { get; set; }
    }

    /// <summary>
    /// Single branch entry for the branches panel.
    /// </summary>
    public sealed class GitBranchInfo
    {
        public string Name { get; set; }
        public bool IsRemote { get; set; }
        public bool IsCurrent { get; set; }
        public DateTimeOffset? LastCommitTime { get; set; }
    }

    /// <summary>
    /// Ahead/behind counts vs the upstream-tracked remote branch.
    /// Returns <c>null</c> from <see cref="GitRepoService.GetAheadBehind"/> when the
    /// current branch has no upstream (e.g., the MT repo today which has no remote).
    /// </summary>
    public sealed class GitAheadBehind
    {
        public int Ahead { get; set; }
        public int Behind { get; set; }
    }

    /// <summary>
    /// Per-repository git read service. Opens the repo via LibGit2Sharp, exposes
    /// status / file diff / recent-commits / branches / ahead-behind / last-fetch,
    /// and forwards <see cref="RepoStateChanged"/> events from an inner
    /// <see cref="GitRepoWatcher"/> so consumers can refresh on .git/ mutations
    /// without polling.
    ///
    /// Shared between the new HUD Git tab (per-project drill-down) and the existing
    /// HudDashboardRenderer git widget (glance summary). Both read through the same
    /// instance per project, so the two surfaces can't drift out of sync.
    ///
    /// Thread safety: LibGit2Sharp <see cref="Repository"/> is NOT thread-safe and
    /// holds native handles. All public read methods, the property accessors, and
    /// <see cref="Dispose"/> acquire <c>_repoLock</c> so concurrent readers can't
    /// be inside a Repository call when the native handle is freed. <c>_disposed</c>
    /// is checked inside the lock for the same reason. Marked <c>volatile</c> so
    /// the disposal flag is observed promptly across cores. The
    /// <see cref="RepoStateChanged"/> event fires on a thread-pool thread —
    /// UI subscribers must marshal to the UI thread.
    /// </summary>
    public sealed class GitRepoService : IDisposable
    {
        private const int MaxUserTextLength = 500;

        private readonly Repository _repo;
        private readonly GitRepoWatcher _watcher;
        private readonly object _repoLock = new object();
        private volatile bool _disposed;

        /// <summary>Repository root (absolute path).</summary>
        public string RepoRoot { get; }

        /// <summary>True if the repo has at least one configured remote.</summary>
        public bool HasRemote
        {
            get
            {
                lock (_repoLock)
                {
                    if (_disposed) return false;
                    return _repo.Network.Remotes.Any();
                }
            }
        }

        /// <summary>Friendly name of the current branch, or "(detached)" if HEAD is detached.</summary>
        public string CurrentBranch
        {
            get
            {
                lock (_repoLock)
                {
                    if (_disposed) return null;
                    if (_repo.Info.IsHeadDetached) return "(detached)";
                    return _repo.Head?.FriendlyName ?? "(unknown)";
                }
            }
        }

        /// <summary>True if HEAD points at a SHA rather than a ref (detached HEAD).</summary>
        public bool IsHeadDetached
        {
            get
            {
                lock (_repoLock)
                {
                    if (_disposed) return false;
                    return _repo.Info.IsHeadDetached;
                }
            }
        }

        /// <summary>
        /// Raised after a 1 s quiet window when something under .git/ changed.
        /// Fires on a thread-pool thread; UI subscribers must marshal.
        /// </summary>
        public event EventHandler<RepoChangedEventArgs> RepoStateChanged;

        public GitRepoService(string repoRoot)
        {
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentNullException(nameof(repoRoot));
            if (!Repository.IsValid(repoRoot))
                throw new InvalidOperationException(
                    $"GitRepoService: '{repoRoot}' is not a valid git repository.");

            RepoRoot = repoRoot;
            _repo = new Repository(repoRoot);
            _watcher = new GitRepoWatcher(repoRoot);
            _watcher.RepoStateChanged += OnInnerWatcherFired;
            _watcher.Start();
        }

        /// <summary>
        /// Returns one entry per file in the working tree that differs from HEAD
        /// (modified, added, deleted, untracked, renamed, type-changed, conflicted).
        /// Excludes ignored and unaltered entries. Lines added/deleted are computed
        /// per file; for untracked files both counts are 0 in v1.
        ///
        /// Implementation note: computes ONE full <see cref="Patch"/> for the whole
        /// working tree and indexes by path, instead of N per-file
        /// <see cref="Diff.Compare"/> calls under the lock. Saves catastrophic
        /// starvation when the working tree has many changed files (e.g., post
        /// branch-switch).
        /// </summary>
        public IReadOnlyList<GitFileStatus> GetWorkingTreeStatus()
        {
            lock (_repoLock)
            {
                if (_disposed) return Array.Empty<GitFileStatus>();

                var statusOptions = new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                    DetectRenamesInIndex = true,
                    DetectRenamesInWorkDir = true,
                };
                var status = _repo.RetrieveStatus(statusOptions);
                var headTree = _repo.Head?.Tip?.Tree;

                Patch fullPatch = null;
                if (headTree != null)
                {
                    try
                    {
                        fullPatch = _repo.Diff.Compare<Patch>(
                            headTree,
                            DiffTargets.Index | DiffTargets.WorkingDirectory);
                    }
                    catch
                    {
                        fullPatch = null;
                    }
                }

                try
                {
                    var result = new List<GitFileStatus>();
                    foreach (var entry in status)
                    {
                        if (entry.State == FileStatus.Ignored) continue;
                        if (entry.State == FileStatus.Unaltered) continue;

                        var kind = MapFileStatus(entry.State);
                        int adds = 0;
                        int dels = 0;

                        if (kind != GitFileChangeKind.Untracked && fullPatch != null)
                        {
                            try
                            {
                                var pe = fullPatch[entry.FilePath];
                                if (pe != null)
                                {
                                    adds = pe.LinesAdded;
                                    dels = pe.LinesDeleted;
                                }
                            }
                            catch
                            {
                                // Symlink / binary / unreadable — leave 0/0.
                            }
                        }

                        result.Add(new GitFileStatus
                        {
                            Path = entry.FilePath,
                            Kind = kind,
                            LinesAdded = adds,
                            LinesDeleted = dels,
                        });
                    }
                    return result;
                }
                finally
                {
                    fullPatch?.Dispose();
                }
            }
        }

        /// <summary>
        /// Returns the unified diff text for a single file's uncommitted changes
        /// (staged + unstaged combined, against HEAD). Returns empty string for
        /// untracked files in v1 — the working changes panel synthesises an
        /// "all-added" view for that case at render time.
        ///
        /// <para>Path safety: <paramref name="relativePath"/> must be a forward-slash
        /// relative path inside the repo. Absolute paths, paths containing
        /// <c>..</c> or <c>:</c>, control characters, paths whose segments resolve
        /// through reparse points (junctions/symlinks), and paths that escape the
        /// repo root are rejected (returns empty string). Defense-in-depth —
        /// typically callers pass paths that already came from
        /// <see cref="GetWorkingTreeStatus"/> output, but agent input (via MCP
        /// tools) flows through here too.</para>
        /// </summary>
        public string GetFileDiff(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return string.Empty;
            if (!IsSafeRelativePath(relativePath)) return string.Empty;

            lock (_repoLock)
            {
                if (_disposed) return string.Empty;
                try
                {
                    var headTree = _repo.Head?.Tip?.Tree;
                    if (headTree == null) return string.Empty;
                    using var patch = _repo.Diff.Compare<Patch>(
                        headTree,
                        DiffTargets.Index | DiffTargets.WorkingDirectory,
                        new[] { relativePath },
                        null,
                        new CompareOptions { ContextLines = 3 });
                    return patch.Content ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Returns the unified diff for a single commit against its first parent
        /// (i.e., what changed in that commit). Returns empty string for the
        /// initial commit (no parent) — v1 doesn't synthesise an "all-added"
        /// view for that case.
        ///
        /// <para>Path safety: <paramref name="sha"/> must look like a git SHA
        /// (hex chars, 7-64 length). Anything else is rejected before reaching
        /// LibGit2Sharp's lookup. Defense-in-depth — typical callers pass
        /// strings sourced from <see cref="GetRecentCommits"/>, but agent input
        /// (via MCP tools) flows here too.</para>
        /// </summary>
        public string GetCommitDiff(string sha)
        {
            if (string.IsNullOrEmpty(sha) || !IsValidSha(sha)) return string.Empty;
            lock (_repoLock)
            {
                if (_disposed) return string.Empty;
                try
                {
                    var commit = _repo.Lookup<Commit>(sha);
                    if (commit == null) return string.Empty;
                    var parent = commit.Parents.FirstOrDefault();
                    if (parent == null) return string.Empty;
                    using var patch = _repo.Diff.Compare<Patch>(
                        parent.Tree,
                        commit.Tree,
                        new CompareOptions { ContextLines = 3 });
                    return patch.Content ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Returns the most recent commits on the current branch ordered
        /// newest-first. Default is 30 commits. Uses an explicit
        /// <see cref="CommitFilter"/> with time-sorted ordering — default
        /// LibGit2Sharp ordering can be slow on wide DAGs.
        /// </summary>
        public IReadOnlyList<GitCommitInfo> GetRecentCommits(int max = 30)
        {
            if (max <= 0) return Array.Empty<GitCommitInfo>();
            lock (_repoLock)
            {
                if (_disposed) return Array.Empty<GitCommitInfo>();
                var head = _repo.Head;
                if (head?.Tip == null) return Array.Empty<GitCommitInfo>();

                var filter = new CommitFilter
                {
                    IncludeReachableFrom = head.Tip,
                    SortBy = CommitSortStrategies.Time,
                };

                var result = new List<GitCommitInfo>(max);
                foreach (var c in _repo.Commits.QueryBy(filter).Take(max))
                {
                    result.Add(new GitCommitInfo
                    {
                        ShortSha = c.Sha != null && c.Sha.Length >= 7 ? c.Sha.Substring(0, 7) : c.Sha,
                        FullSha = c.Sha,
                        Subject = SanitizeUserText(c.MessageShort, MaxUserTextLength),
                        AuthorName = SanitizeUserText(c.Author?.Name, MaxUserTextLength),
                        AuthorEmail = c.Author?.Email ?? string.Empty,
                        When = c.Author?.When ?? DateTimeOffset.MinValue,
                        CoAuthors = ExtractCoAuthors(c.Message),
                    });
                }
                return result;
            }
        }

        /// <summary>
        /// Returns local + remote branches with the current branch flagged.
        /// Last-commit time is derived from the branch tip's author date.
        /// </summary>
        public IReadOnlyList<GitBranchInfo> GetBranches()
        {
            lock (_repoLock)
            {
                if (_disposed) return Array.Empty<GitBranchInfo>();
                var current = _repo.Head;
                var currentCanonical = current?.CanonicalName;

                return _repo.Branches.Select(b => new GitBranchInfo
                {
                    Name = b.FriendlyName,
                    IsRemote = b.IsRemote,
                    IsCurrent = currentCanonical != null && b.CanonicalName == currentCanonical,
                    LastCommitTime = b.Tip?.Author?.When,
                }).ToList();
            }
        }

        /// <summary>
        /// Ahead/behind counts vs the upstream-tracked remote branch. Returns null
        /// when the current branch has no upstream (e.g., the MT repo today which
        /// is configured local-only).
        /// </summary>
        public GitAheadBehind GetAheadBehind()
        {
            lock (_repoLock)
            {
                if (_disposed) return null;
                var head = _repo.Head;
                var td = head?.TrackingDetails;
                if (td == null) return null;
                if (td.AheadBy == null && td.BehindBy == null) return null;
                return new GitAheadBehind
                {
                    Ahead = td.AheadBy ?? 0,
                    Behind = td.BehindBy ?? 0,
                };
            }
        }

        /// <summary>
        /// Last-fetch time, derived from <c>FETCH_HEAD</c> mtime. Uses
        /// LibGit2Sharp's resolved gitdir (<c>_repo.Info.Path</c>) so this still
        /// works for worktree/submodule layouts where <c>.git</c> is a file
        /// pointing elsewhere. Returns UTC <see cref="DateTimeOffset"/> to
        /// avoid DST/local-time ambiguity. Returns null if FETCH_HEAD does not
        /// exist (repo never fetched).
        /// </summary>
        public DateTimeOffset? GetLastFetchTime()
        {
            lock (_repoLock)
            {
                if (_disposed) return null;
                string gitDir = _repo.Info?.Path;
                if (string.IsNullOrEmpty(gitDir)) return null;
                string fetchHead = Path.Combine(gitDir, "FETCH_HEAD");
                try
                {
                    if (!File.Exists(fetchHead)) return null;
                    return new DateTimeOffset(File.GetLastWriteTimeUtc(fetchHead), TimeSpan.Zero);
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Dispose()
        {
            // Trim the critical section: hold _repoLock only across the
            // _repo.Dispose() (load-bearing — protects readers inside
            // Repository.RetrieveStatus / Diff.Compare from a freed native
            // handle). The watcher dispose is moved outside the lock since
            // GitRepoWatcher has its own internal lock and never touches
            // _repo, so holding _repoLock across its native I/O (FileSystemWatcher
            // CloseHandle, possibly waiting on pending I/O completion) would
            // unnecessarily lengthen UI-thread shutdown without any safety
            // benefit. OnInnerWatcherFired stays safe because it re-checks
            // _disposed inside _repoLock before invoking subscribers.
            GitRepoWatcher watcherToDispose;
            lock (_repoLock)
            {
                if (_disposed) return;
                _disposed = true;
                watcherToDispose = _watcher;
                try { _repo?.Dispose(); } catch { }
            }

            try
            {
                if (watcherToDispose != null)
                {
                    watcherToDispose.RepoStateChanged -= OnInnerWatcherFired;
                    watcherToDispose.Dispose();
                }
            }
            catch { }
        }

        private void OnInnerWatcherFired(object sender, RepoChangedEventArgs args)
        {
            // Read disposed flag under the lock so a tear-down racing in parallel
            // with the watcher's debounce timer can't see us re-fire after Dispose.
            bool stillAlive;
            lock (_repoLock) { stillAlive = !_disposed; }
            if (!stillAlive) return;

            try
            {
                RepoStateChanged?.Invoke(this, args);
            }
            catch
            {
                // Subscriber threw — swallow so a buggy listener can't kill the watcher.
            }
        }

        /// <summary>
        /// Returns true iff the relative path is safe — not absolute, no <c>..</c>
        /// or <c>.</c> segments, no control chars, no <c>:</c> (NTFS Alternate
        /// Data Stream syntax on Windows), no segments that resolve through a
        /// reparse point (junction/symlink — which could escape the repo root
        /// even after canonicalisation), and final canonical resolution stays
        /// inside the repository root.
        /// </summary>
        private bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            if (Path.IsPathRooted(relativePath)) return false;

            foreach (char ch in relativePath)
            {
                if (ch < 0x20) return false;       // control chars
                if (ch == ':') return false;       // ADS / drive separator
            }
            if (relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

            string norm = relativePath.Replace('\\', '/');
            foreach (var seg in norm.Split('/'))
            {
                if (seg == "..") return false;
                if (seg == ".") return false;
                if (string.IsNullOrEmpty(seg)) return false;
            }

            string combined;
            try { combined = Path.GetFullPath(Path.Combine(RepoRoot, norm)); }
            catch { return false; }

            string root = Path.GetFullPath(RepoRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            // Walk each segment and reject if any is a reparse point (junction/symlink).
            // A reparse point inside the repo can redirect to anywhere on disk —
            // canonical-prefix check alone isn't enough.
            try
            {
                string current = root.TrimEnd(Path.DirectorySeparatorChar);
                foreach (var seg in norm.Split('/'))
                {
                    current = Path.Combine(current, seg);
                    if (!File.Exists(current) && !Directory.Exists(current))
                    {
                        // Not yet on disk — that's fine. Stop walking.
                        break;
                    }
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static GitFileChangeKind MapFileStatus(FileStatus state)
        {
            if (state.HasFlag(FileStatus.Conflicted)) return GitFileChangeKind.Conflicted;
            if (state.HasFlag(FileStatus.NewInWorkdir)) return GitFileChangeKind.Untracked;
            if (state.HasFlag(FileStatus.NewInIndex)) return GitFileChangeKind.Added;
            if (state.HasFlag(FileStatus.DeletedFromIndex) || state.HasFlag(FileStatus.DeletedFromWorkdir))
                return GitFileChangeKind.Deleted;
            if (state.HasFlag(FileStatus.RenamedInIndex) || state.HasFlag(FileStatus.RenamedInWorkdir))
                return GitFileChangeKind.Renamed;
            if (state.HasFlag(FileStatus.TypeChangeInIndex) || state.HasFlag(FileStatus.TypeChangeInWorkdir))
                return GitFileChangeKind.TypeChanged;
            if (state.HasFlag(FileStatus.ModifiedInIndex) || state.HasFlag(FileStatus.ModifiedInWorkdir))
                return GitFileChangeKind.Modified;
            // Future LibGit2Sharp flags we haven't classified — surface as
            // Unknown so QA notices instead of silently mislabeling as Modified.
            return GitFileChangeKind.Unknown;
        }

        /// <summary>
        /// Strips control characters (except <c>\t</c>) and Unicode bidi-override
        /// codepoints (which can flip displayed-vs-stored ordering and are
        /// invisible to a casual reader) from arbitrary user-controlled text
        /// (commit messages and author names). Caps at <paramref name="maxLen"/>.
        /// </summary>
        private static string SanitizeUserText(string raw, int maxLen)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(Math.Min(raw.Length, maxLen));
            foreach (char ch in raw)
            {
                if (ch == '\t')
                {
                    sb.Append(ch);
                }
                else if (ch < 0x20)
                {
                    // drop control char
                }
                else if (ch == '‪' || ch == '‫' || ch == '‬'
                      || ch == '‭' || ch == '‮'
                      || ch == '⁦' || ch == '⁧'
                      || ch == '⁨' || ch == '⁩')
                {
                    // drop bidi-override
                }
                else
                {
                    sb.Append(ch);
                }

                if (sb.Length >= maxLen) break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns true iff the input looks like a git SHA — hex characters only,
        /// length 7 (short) to 64 (covers both SHA-1 40-char and future SHA-256
        /// 64-char). Used to reject agent-controllable input before it reaches
        /// LibGit2Sharp's commit-lookup APIs.
        /// </summary>
        private static bool IsValidSha(string sha)
        {
            if (string.IsNullOrEmpty(sha)) return false;
            if (sha.Length < 7 || sha.Length > 64) return false;
            foreach (var ch in sha)
            {
                bool isHex = (ch >= '0' && ch <= '9')
                          || (ch >= 'a' && ch <= 'f')
                          || (ch >= 'A' && ch <= 'F');
                if (!isHex) return false;
            }
            return true;
        }

        private static IReadOnlyList<string> ExtractCoAuthors(string commitMessage)
        {
            if (string.IsNullOrEmpty(commitMessage)) return Array.Empty<string>();
            var coAuthors = new List<string>();
            foreach (var raw in commitMessage.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                const string Marker = "Co-Authored-By:";
                int idx = line.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string after = line.Substring(idx + Marker.Length).Trim();
                if (after.Length == 0) continue;
                // Take everything before the LAST '<' so 'Foo <bar> Baz <baz@x>'
                // returns 'Foo <bar> Baz' rather than just 'Foo'.
                int emailStart = after.LastIndexOf('<');
                string name = emailStart > 0 ? after.Substring(0, emailStart).Trim() : after;
                if (name.Length == 0) continue;
                coAuthors.Add(SanitizeUserText(name, MaxUserTextLength));
            }
            return coAuthors.Count == 0 ? Array.Empty<string>() : coAuthors;
        }
    }
}
