using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// On-disk layout of a project's git directory.
    /// </summary>
    public enum GitRepoLayout
    {
        /// <summary><c>.git</c> is a directory — a standard repository, openable for direct inspection.</summary>
        Standard,

        /// <summary><c>.git</c> is a file — worktree gitlink or submodule pointer. Not openable for direct inspection in v1; the HUD Git tab renders a friendly empty-state and points the user at the parent repo.</summary>
        LinkedGitDir,

        /// <summary>No <c>.git</c> file or directory exists at the project root. The HUD Git tab renders an empty-state inviting <c>git init</c>.</summary>
        NotARepo,
    }

    /// <summary>
    /// Process-wide cache of <see cref="GitRepoService"/> instances keyed by
    /// canonical project root. Lets the dashboard widget and the HUD Git tab
    /// share one git read layer per project — they're typically children of
    /// the same TerminalDocument so this avoids duplicate Repository handles
    /// and duplicate file watchers.
    ///
    /// Wired onto <see cref="MCPServer.Services.MessageBroker"/> as a DI property
    /// so consumers can call <c>broker.GitRepos.GetOrCreate(projectRoot)</c>.
    ///
    /// Returns <c>null</c> from <see cref="GetOrCreate"/> if the path is not a
    /// valid git repository — callers must handle the no-repo case (e.g., a
    /// project folder that hasn't been <c>git init</c>'d).
    /// </summary>
    public sealed class GitRepoManager : IDisposable
    {
        private readonly Dictionary<string, GitRepoService> _byPath
            = new Dictionary<string, GitRepoService>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedLinkedDirs
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// Classifies a project root by its on-disk git layout. Cheap (just two
        /// filesystem stats) and side-effect-free except for an info-level log
        /// the first time a <see cref="GitRepoLayout.LinkedGitDir"/> case fires
        /// for a given project — useful telemetry to gauge how often the
        /// worktree/submodule case shows up in practice without spamming the log
        /// on every UI refresh.
        ///
        /// <para>Used by <c>HudGitRenderer</c> to choose between the normal
        /// header-and-body rendering, the worktree/submodule empty-state, and
        /// the no-repo empty-state — without going through the throwing
        /// <see cref="GitRepoService"/> constructor on the unsupported cases.</para>
        /// </summary>
        public GitRepoLayout DetectLayout(string projectRoot)
        {
            if (_disposed || string.IsNullOrEmpty(projectRoot)) return GitRepoLayout.NotARepo;
            string canonical = CanonicalizeProjectRoot(projectRoot);
            if (canonical == null) return GitRepoLayout.NotARepo;

            string gitPath = Path.Combine(canonical, ".git");
            if (Directory.Exists(gitPath)) return GitRepoLayout.Standard;
            if (File.Exists(gitPath))
            {
                lock (_lock)
                {
                    if (_loggedLinkedDirs.Add(canonical))
                    {
                        Debug.WriteLine(
                            $"[GitRepoManager] Detected linked .git for '{canonical}' (worktree/submodule). HUD Git tab will render empty-state.");
                    }
                }
                return GitRepoLayout.LinkedGitDir;
            }
            return GitRepoLayout.NotARepo;
        }

        /// <summary>
        /// Returns a cached or freshly-opened <see cref="GitRepoService"/> for the
        /// given project root, or <c>null</c> if the path is not a valid git repo.
        /// </summary>
        public GitRepoService GetOrCreate(string projectRoot)
        {
            if (_disposed || string.IsNullOrEmpty(projectRoot)) return null;
            string key = CanonicalizeProjectRoot(projectRoot);
            if (key == null) return null;

            lock (_lock)
            {
                if (_byPath.TryGetValue(key, out var existing)) return existing;
                GitRepoService svc = null;
                try
                {
                    svc = new GitRepoService(key);
                    _byPath[key] = svc;
                    var transferred = svc;
                    svc = null; // ownership transferred to _byPath; finally must not dispose.
                    return transferred;
                }
                catch
                {
                    // Not a valid git repo, or LibGit2Sharp couldn't open it.
                    return null;
                }
                finally
                {
                    svc?.Dispose();
                }
            }
        }

        /// <summary>
        /// Returns the cached <see cref="GitRepoService"/> for the given project root,
        /// or <c>null</c> if none has been created yet.
        /// </summary>
        public GitRepoService TryGet(string projectRoot)
        {
            if (_disposed || string.IsNullOrEmpty(projectRoot)) return null;
            string key = CanonicalizeProjectRoot(projectRoot);
            if (key == null) return null;

            lock (_lock)
            {
                return _byPath.TryGetValue(key, out var existing) ? existing : null;
            }
        }

        /// <summary>
        /// Disposes the cached service for a given project root and removes it
        /// from the cache. Synonym for <see cref="Release"/>; provided as a
        /// readability alias when the call site is reacting to a known
        /// invalidation event (e.g. project rename, <c>git init</c> rerun, repo
        /// move) rather than a deliberate unload.
        /// </summary>
        public void Invalidate(string projectRoot) => Release(projectRoot);

        /// <summary>
        /// Disposes the cached service for a given project root and removes it
        /// from the cache. Use when a project is being unloaded.
        /// </summary>
        public void Release(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;
            string key = CanonicalizeProjectRoot(projectRoot);
            if (key == null) return;

            GitRepoService toDispose = null;
            lock (_lock)
            {
                if (_byPath.TryGetValue(key, out var svc))
                {
                    toDispose = svc;
                    _byPath.Remove(key);
                }
            }
            if (toDispose != null)
            {
                try { toDispose.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            List<GitRepoService> snapshot;
            lock (_lock)
            {
                snapshot = new List<GitRepoService>(_byPath.Values);
                _byPath.Clear();
            }
            foreach (var svc in snapshot)
            {
                try { svc.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Canonicalises a project root path for cache-key purposes. Resolves
        /// symlinks and junctions to their final target via
        /// <see cref="DirectoryInfo.ResolveLinkTarget"/> so two callers passing
        /// aliased paths (e.g. one via a junction, one direct) hit the same
        /// cache entry instead of opening two Repository handles + two watchers
        /// on the same .git directory.
        ///
        /// <para>Limitation: 8.3 short names (<c>PROGRA~1</c> vs <c>Program Files</c>)
        /// are not expanded — that requires <c>GetLongPathName</c> Win32. In
        /// practice 8.3 names rarely appear in MT's project paths; if they
        /// become an issue, swap to a Win32 P/Invoke here.</para>
        /// </summary>
        private static string CanonicalizeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string full;
            try { full = Path.GetFullPath(projectRoot); }
            catch { return null; }

            try
            {
                var dirInfo = new DirectoryInfo(full);
                if (dirInfo.Exists)
                {
                    var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is DirectoryInfo resolvedDir && resolvedDir.Exists)
                    {
                        full = resolvedDir.FullName;
                    }
                }
            }
            catch
            {
                // Fall back to plain Path.GetFullPath — better than throwing
                // if the path is on a network share that doesn't support link
                // queries, etc.
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
