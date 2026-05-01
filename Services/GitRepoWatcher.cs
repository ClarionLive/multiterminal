using System;
using System.IO;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Set of bits indicating which parts of a repository's <c>.git/</c> directory
    /// changed since the last <see cref="GitRepoWatcher.RepoStateChanged"/> event.
    /// Subscribers can use the bits to do partial refreshes (e.g. only re-read the
    /// branch list when <see cref="RefsChanged"/> is set).
    /// </summary>
    [Flags]
    public enum RepoChangeReason
    {
        None = 0,

        /// <summary>The <c>.git/HEAD</c> file changed (branch switch or detached-HEAD update).</summary>
        HeadChanged = 1 << 0,

        /// <summary>The <c>.git/index</c> file changed (staged content updated).</summary>
        IndexChanged = 1 << 1,

        /// <summary>Anything under <c>.git/refs/heads/</c> or <c>.git/refs/remotes/</c> changed.</summary>
        RefsChanged = 1 << 2,
    }

    /// <summary>
    /// Event payload for <see cref="GitRepoWatcher.RepoStateChanged"/>.
    /// </summary>
    public class RepoChangedEventArgs : EventArgs
    {
        public RepoChangeReason Reasons { get; }

        public RepoChangedEventArgs(RepoChangeReason reasons)
        {
            Reasons = reasons;
        }
    }

    /// <summary>
    /// Watches a single repository's <c>.git/</c> directory for changes and raises a
    /// debounced <see cref="RepoStateChanged"/> event with reason flags. Designed as
    /// a standalone service so consumers (notably <c>GitRepoService</c>) can subscribe
    /// without knowing the watch mechanics, and so the watcher can be unit-tested
    /// independently of the git read layer.
    ///
    /// Watch paths:
    /// <list type="bullet">
    ///   <item><description><c>.git/HEAD</c> — branch switches and detached-HEAD updates.</description></item>
    ///   <item><description><c>.git/index</c> — staged content changes.</description></item>
    ///   <item><description><c>.git/refs/heads/</c> — local branch updates.</description></item>
    ///   <item><description><c>.git/refs/remotes/</c> — remote-tracking branch updates (post-fetch).</description></item>
    /// </list>
    ///
    /// Events from any watcher are coalesced into one <see cref="RepoStateChanged"/>
    /// fire per ~1 s of quiet, with the union of reason flags accumulated during the
    /// burst. This avoids 50 redraws on a bulk operation like <c>git reset</c>.
    ///
    /// Known gap (deferred): does not currently watch <c>.git/packed-refs</c>, so
    /// some bulk fetch/pull operations that pack refs without touching individual
    /// loose-ref files will not raise <see cref="RepoChangeReason.RefsChanged"/>.
    /// Tracked as a v2 enhancement.
    /// </summary>
    public sealed class GitRepoWatcher : IDisposable
    {
        private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

        private readonly string _gitDir;
        private readonly TimeSpan _debounce;
        private readonly object _lock = new object();

        private FileSystemWatcher _headWatcher;
        private FileSystemWatcher _indexWatcher;
        private FileSystemWatcher _refsHeadsWatcher;
        private FileSystemWatcher _refsRemotesWatcher;
        private Timer _debounceTimer;
        private RepoChangeReason _pendingReasons;
        // Volatile so the early-return _disposed check at the top of public
        // entry points is observed promptly across cores even outside the lock.
        private volatile bool _started;
        private volatile bool _disposed;

        /// <summary>
        /// Raised on a thread-pool thread after the debounce window expires. Subscribers
        /// must marshal to the UI thread before touching UI state.
        /// </summary>
        public event EventHandler<RepoChangedEventArgs> RepoStateChanged;

        /// <summary>
        /// Repository root (parent of <c>.git/</c>) that this watcher was created for.
        /// </summary>
        public string RepoRoot { get; }

        /// <param name="repoRoot">Filesystem path to the repository root (the directory that contains <c>.git/</c>).</param>
        /// <param name="debounce">Debounce window. Defaults to 1 s if not specified.</param>
        public GitRepoWatcher(string repoRoot, TimeSpan? debounce = null)
        {
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentNullException(nameof(repoRoot));

            RepoRoot = repoRoot;
            _gitDir = Path.Combine(repoRoot, ".git");
            _debounce = debounce ?? DefaultDebounce;

            if (!Directory.Exists(_gitDir))
            {
                // .git can be a file (submodule / worktree pointer) — out of scope for v1.
                throw new InvalidOperationException(
                    $"GitRepoWatcher: '{repoRoot}' is not a standard git repo (no .git/ directory).");
            }
        }

        /// <summary>
        /// Begin watching. Idempotent — calling multiple times is a no-op after the first.
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (_started) return;
                _started = true;

                _debounceTimer = new Timer(OnDebounceFired, null, Timeout.Infinite, Timeout.Infinite);

                _headWatcher = MakeFileWatcher(_gitDir, "HEAD", RepoChangeReason.HeadChanged);
                _indexWatcher = MakeFileWatcher(_gitDir, "index", RepoChangeReason.IndexChanged);

                string refsHeads = Path.Combine(_gitDir, "refs", "heads");
                if (Directory.Exists(refsHeads))
                    _refsHeadsWatcher = MakeDirectoryWatcher(refsHeads, RepoChangeReason.RefsChanged);

                string refsRemotes = Path.Combine(_gitDir, "refs", "remotes");
                if (Directory.Exists(refsRemotes))
                    _refsRemotesWatcher = MakeDirectoryWatcher(refsRemotes, RepoChangeReason.RefsChanged);
            }
        }

        /// <summary>
        /// Stop watching and dispose all underlying FileSystemWatchers and the debounce timer.
        /// Idempotent.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_started) return;
                _started = false;

                DisposeWatcher(ref _headWatcher);
                DisposeWatcher(ref _indexWatcher);
                DisposeWatcher(ref _refsHeadsWatcher);
                DisposeWatcher(ref _refsRemotesWatcher);

                try { _debounceTimer?.Dispose(); } catch { }
                _debounceTimer = null;
                _pendingReasons = RepoChangeReason.None;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        private FileSystemWatcher MakeFileWatcher(string dir, string fileName, RepoChangeReason reason)
        {
            // Construct disabled, wire handlers, then enable last — mirrors the
            // commit 6c6dd2a hardening on TeamWatcherService that closed the race
            // where events fired between ctor and += were lost.
            var w = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = false,
            };
            w.Changed += (s, e) => OnEvent(reason);
            w.Created += (s, e) => OnEvent(reason);
            w.Deleted += (s, e) => OnEvent(reason);
            w.Renamed += (s, e) => OnEvent(reason);
            w.EnableRaisingEvents = true;
            return w;
        }

        private FileSystemWatcher MakeDirectoryWatcher(string dir, RepoChangeReason reason)
        {
            // Same construct-disabled / enable-last pattern as MakeFileWatcher.
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = false,
            };
            w.Changed += (s, e) => OnEvent(reason);
            w.Created += (s, e) => OnEvent(reason);
            w.Deleted += (s, e) => OnEvent(reason);
            w.Renamed += (s, e) => OnEvent(reason);
            w.EnableRaisingEvents = true;
            return w;
        }

        private void OnEvent(RepoChangeReason reason)
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (!_started || _debounceTimer == null) return;
                _pendingReasons |= reason;
                // Reset the debounce: fire OnDebounceFired in _debounce ms; never repeat.
                _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnDebounceFired(object state)
        {
            RepoChangeReason reasons;
            bool stillAlive;
            lock (_lock)
            {
                reasons = _pendingReasons;
                _pendingReasons = RepoChangeReason.None;
                stillAlive = !_disposed && _started;
            }
            if (reasons != RepoChangeReason.None && stillAlive)
            {
                try
                {
                    RepoStateChanged?.Invoke(this, new RepoChangedEventArgs(reasons));
                }
                catch
                {
                    // Subscriber threw — swallow to keep the watcher alive.
                }
            }
        }

        private static void DisposeWatcher(ref FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
            watcher = null;
        }
    }
}
