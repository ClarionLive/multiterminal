using System;
using System.IO;
using System.Linq;
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
    /// <para>Worktree support: if <c>.git</c> is a file containing <c>gitdir: …/worktrees/&lt;name&gt;</c>,
    /// HEAD and index are watched in the per-worktree admin dir while refs/heads and refs/remotes
    /// are watched in the main gitdir (resolved via the admin dir's <c>commondir</c> file). A commit
    /// inside a worktree only writes its index locally; the branch tip lands in the main gitdir, so
    /// without the second refs watch we would miss every ref-tip update. Submodule pointers
    /// (<c>.git</c> file with no <c>commondir</c>, typically <c>gitdir: …/modules/&lt;name&gt;</c>) remain
    /// rejected — callers should detect them via <see cref="GitRepoManager.DetectLayout"/> and
    /// render the empty-state instead.</para>
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

        // Where HEAD + index live. For a standard repo this is <repoRoot>/.git.
        // For a worktree this is the per-worktree admin dir under the main
        // gitdir's worktrees/<name>/ — the place that owns this worktree's HEAD
        // and index.
        private readonly string _adminDir;

        // Where refs/heads + refs/remotes live. For a standard repo this equals
        // _adminDir. For a worktree this is the MAIN gitdir resolved via the
        // admin dir's commondir file — that's where branch-tip files for every
        // worktree (including this one) actually get rewritten on commit.
        private readonly string _commonGitDir;
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
            _debounce = debounce ?? DefaultDebounce;

            string gitPath = Path.Combine(repoRoot, ".git");

            if (Directory.Exists(gitPath))
            {
                _adminDir = gitPath;
                _commonGitDir = gitPath;
                return;
            }

            if (File.Exists(gitPath))
            {
                ResolveLinkedGitDir(repoRoot, gitPath, out _adminDir, out _commonGitDir);
                return;
            }

            throw new InvalidOperationException(
                $"GitRepoWatcher: '{repoRoot}' is not a git repo (no .git file or directory).");
        }

        // Resolves a worktree's .git file into (adminDir, commonGitDir).
        //
        // Threat model: the .git file and the admin dir's commondir file are
        // both on-disk text whose contents control where we will install
        // FileSystemWatchers. A hostile repo (e.g. a clone the user opened
        // from an untrusted source) can write any string into them, including:
        //   * `gitdir: \\attacker.example.com\share` — triggers outbound SMB/NTLM
        //     auth on Windows the moment a FileSystemWatcher binds to it.
        //   * a path that resolves through a junction/symlink to somewhere
        //     attacker-controlled, defeating any naïve string-prefix check.
        //   * a `commondir` pointing somewhere unrelated to the supposed parent
        //     repo, so the panel ends up showing branch state from a different
        //     repo entirely.
        // Defenses (mirror the posture of GitRepoService.IsSafeRelativePath at
        // Services/GitRepoService.cs:503-564, adapted from relative-file inputs
        // to absolute-directory inputs):
        //   1. Canonicalize each path: Path.GetFullPath, then DirectoryInfo
        //      .ResolveLinkTarget(returnFinalTarget:true) so the final leaf's
        //      symlink/junction (if any) is resolved to its real target.
        //   2. Reject UNC roots (`\\server\share` or `//server/share`).
        //   3. Walk every path component from the filesystem root and reject
        //      if any directory along the way is a reparse point — catches
        //      intermediate junctions that step 1 didn't resolve.
        //   4. Enforce hierarchy: adminDir's structural shape must be
        //      `<X>/.git/worktrees/<name>` and commonGitDir must canonically
        //      equal `<X>/.git` for the same X. This pins us to the standard
        //      git worktree layout and prevents commondir from pointing at
        //      an unrelated gitdir.
        //
        // Submodule pointers (no commondir file in the linked dir) are rejected
        // here — the manager-level layout check is supposed to keep them out,
        // but we double-check so a misdirected caller fails loudly instead of
        // installing watchers that will never fire useful events.
        private static void ResolveLinkedGitDir(
            string repoRoot, string gitFilePath, out string adminDir, out string commonGitDir)
        {
            string firstLine;
            try
            {
                firstLine = File.ReadLines(gitFilePath).FirstOrDefault();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: failed to read '{gitFilePath}'.", ex);
            }

            const string prefix = "gitdir:";
            if (string.IsNullOrEmpty(firstLine) ||
                !firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: '{gitFilePath}' is not a recognized .git pointer (expected 'gitdir: <path>').");
            }

            string linkedRaw = firstLine.Substring(prefix.Length).Trim();
            // Git writes forward slashes on Windows in this file; both work for
            // Path APIs but normalize for cleanliness.
            linkedRaw = linkedRaw.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(linkedRaw))
                linkedRaw = Path.Combine(repoRoot, linkedRaw);

            string linked = CanonicalizeAndValidateDir(linkedRaw, $"linked gitdir from '{gitFilePath}'");

            string commondirFile = Path.Combine(linked, "commondir");
            if (!File.Exists(commondirFile))
            {
                // No commondir → submodule layout (.git/modules/<name>) or some
                // other non-worktree linked-gitdir. Out of scope.
                throw new InvalidOperationException(
                    $"GitRepoWatcher: '{repoRoot}' looks like a submodule or unsupported linked gitdir " +
                    $"(no 'commondir' under '{linked}'). Use GitRepoManager.DetectLayout to filter these out before constructing a watcher.");
            }

            string commonRaw;
            try
            {
                commonRaw = File.ReadAllText(commondirFile).Trim();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: failed to read '{commondirFile}'.", ex);
            }

            if (string.IsNullOrEmpty(commonRaw))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: empty commondir at '{commondirFile}'.");
            }

            commonRaw = commonRaw.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(commonRaw))
                commonRaw = Path.Combine(linked, commonRaw);

            string common = CanonicalizeAndValidateDir(commonRaw, $"commondir from '{commondirFile}'");

            // Hierarchy check (defense 4 in the threat-model summary above):
            // adminDir's structural shape must be `<X>/.git/worktrees/<name>`,
            // and commonGitDir must canonically equal `<X>/.git`. This pins the
            // resolved paths to git's standard worktree layout and prevents a
            // hostile commondir from steering refs watchers at an unrelated
            // gitdir on the same machine.
            string adminWorktreesDir = Path.GetDirectoryName(linked);                 // <X>/.git/worktrees
            string adminParentGitDir = adminWorktreesDir != null
                ? Path.GetDirectoryName(adminWorktreesDir)                            // <X>/.git
                : null;
            if (adminWorktreesDir == null || adminParentGitDir == null
                || !string.Equals(Path.GetFileName(adminWorktreesDir), "worktrees", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(adminParentGitDir), ".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: linked gitdir '{linked}' is not in the expected '<repo>/.git/worktrees/<name>' shape.");
            }

            string adminParentNorm = adminParentGitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string commonNorm = common.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(adminParentNorm, commonNorm, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: commondir '{common}' does not match expected parent gitdir '{adminParentGitDir}' derived from admin dir '{linked}'.");
            }

            adminDir = linked;
            commonGitDir = common;
        }

        // Canonicalizes an absolute directory path and rejects it if it (a)
        // is a UNC root, (b) doesn't exist, or (c) traverses a reparse point
        // (junction/symlink) at any segment. Returns the canonical path with
        // any trailing separator stripped.
        //
        // Modelled on GitRepoService.IsSafeRelativePath but for absolute
        // directory inputs rather than repo-relative file inputs. The reparse-
        // point walk catches intermediate junctions that DirectoryInfo
        // .ResolveLinkTarget (which only resolves the final leaf) leaves
        // behind in the resolved path.
        private static string CanonicalizeAndValidateDir(string path, string contextDescription)
        {
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException(
                    $"GitRepoWatcher: empty path ({contextDescription}).");

            // Reject UNC before any filesystem touch. Path.GetFullPath happily
            // accepts UNC roots and would let an attacker trigger outbound
            // SMB/NTLM auth as soon as we bind a FileSystemWatcher.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: refusing UNC path '{path}' ({contextDescription}).");
            }

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: failed to canonicalize '{path}' ({contextDescription}).", ex);
            }

            // Re-check UNC after Path.GetFullPath in case the input was a
            // device path or something else that resolves to a UNC root.
            if (full.StartsWith(@"\\", StringComparison.Ordinal)
                || full.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: refusing UNC path '{full}' (canonical of '{path}', {contextDescription}).");
            }

            if (!Directory.Exists(full))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: directory '{full}' does not exist ({contextDescription}).");
            }

            // Resolve final-leaf symlinks/junctions to their target so we
            // watch the actual content directory. Intermediate reparse points
            // are still in the resolved path and are caught by the walk below.
            try
            {
                var dirInfo = new DirectoryInfo(full);
                var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target is DirectoryInfo resolvedDir && resolvedDir.Exists)
                {
                    full = resolvedDir.FullName;
                    // Recheck UNC on the resolved target — a junction can point at one.
                    if (full.StartsWith(@"\\", StringComparison.Ordinal)
                        || full.StartsWith("//", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"GitRepoWatcher: refusing UNC path '{full}' (final-leaf target of '{path}', {contextDescription}).");
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch
            {
                // ResolveLinkTarget can throw on permission errors etc.;
                // proceed with the un-resolved path — the reparse-point walk
                // below is the load-bearing check.
            }

            // Walk every component and reject any reparse point along the way.
            // Mirrors GitRepoService.IsSafeRelativePath:546-555 but anchored at
            // the filesystem root since this path is absolute.
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: '{full}' has no recognizable root ({contextDescription}).");
            }
            try
            {
                string current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string tail = full.Substring(root.Length);
                foreach (var seg in tail.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current.Length == 0 ? root : current, seg);
                    if (!Directory.Exists(current) && !File.Exists(current))
                    {
                        // Shouldn't happen — we already confirmed `full`
                        // exists as a directory. Stop walking rather than
                        // throw so a transient unlink doesn't trip a false
                        // alarm.
                        break;
                    }
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            $"GitRepoWatcher: refusing path traversing reparse point '{current}' ({contextDescription}).");
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"GitRepoWatcher: failed to scan path components of '{full}' ({contextDescription}).", ex);
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

                // TOCTOU defense (audit MEDIUM, Run 3): the constructor's
                // CanonicalizeAndValidateDir checks happened at construction
                // time. Between then and Start() (or between Start()s after a
                // Stop()), an attacker with write access to a parent directory
                // could swap _adminDir or _commonGitDir to a junction pointing
                // somewhere hostile (UNC share, attacker-controlled local
                // path) — the FileSystemWatcher would happily bind to whatever
                // is there at bind time. Re-running the canonicalize + UNC
                // reject + reparse-walk RIGHT BEFORE we install each watcher
                // closes that gap. If the path's canonical form changed
                // (something snuck a reparse point in, the directory got
                // moved/replaced, etc.) we throw and abort Start, leaving
                // _started false so the next caller fails the same way
                // instead of binding to half a watch surface.
                string adminDirNow = CanonicalizeAndValidateDir(_adminDir, "Start() revalidation of admin dir");
                string commonGitDirNow = CanonicalizeAndValidateDir(_commonGitDir, "Start() revalidation of common gitdir");
                if (!string.Equals(adminDirNow, _adminDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"GitRepoWatcher.Start: admin dir canonical path changed since construction (was '{_adminDir}', now '{adminDirNow}'). Aborting watch.");
                }
                if (!string.Equals(commonGitDirNow, _commonGitDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"GitRepoWatcher.Start: common gitdir canonical path changed since construction (was '{_commonGitDir}', now '{commonGitDirNow}'). Aborting watch.");
                }

                _started = true;

                _debounceTimer = new Timer(OnDebounceFired, null, Timeout.Infinite, Timeout.Infinite);

                // HEAD + index live in the per-worktree admin dir (or .git for a
                // standard repo — same value in that case).
                _headWatcher = MakeFileWatcher(_adminDir, "HEAD", RepoChangeReason.HeadChanged);
                _indexWatcher = MakeFileWatcher(_adminDir, "index", RepoChangeReason.IndexChanged);

                // refs/heads and refs/remotes always live in the COMMON gitdir —
                // for worktrees that's the main gitdir resolved via commondir, not
                // the per-worktree admin dir's empty refs/.
                string refsHeads = Path.Combine(_commonGitDir, "refs", "heads");
                if (Directory.Exists(refsHeads))
                    _refsHeadsWatcher = MakeDirectoryWatcher(refsHeads, RepoChangeReason.RefsChanged);

                string refsRemotes = Path.Combine(_commonGitDir, "refs", "remotes");
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
