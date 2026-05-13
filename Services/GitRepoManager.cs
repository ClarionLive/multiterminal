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

        /// <summary><c>.git</c> is a file whose first line is <c>gitdir: …/worktrees/&lt;name&gt;</c>. A linked worktree of some parent repo; LibGit2Sharp opens it fine via the worktree path. The HUD Git tab treats this as a fully inspectable repo (its own branch + working tree) and surfaces a switcher to its parent + sibling worktrees.</summary>
        Worktree,

        /// <summary><c>.git</c> is a file whose first line is <c>gitdir: …/.git/modules/&lt;name&gt;</c>. A submodule pointer. Not directly inspectable in v1 — the HUD Git tab renders an empty-state pointing the user at the parent repo.</summary>
        Submodule,

        /// <summary><c>.git</c> is a file whose <c>gitdir:</c> target matches neither the worktree nor submodule shape. Covers legitimate but non-default layouts (e.g. <c>git init --separate-git-dir</c>, externally-relocated gitdirs) as well as malformed metadata. Treated as unsupported in v1 — HUD Git renders the same empty-state as <see cref="Submodule"/> for now; a future ticket could promote these by deriving identity from <c>git rev-parse --git-common-dir</c> instead of relying on the literal path shape.</summary>
        UnsupportedLink,

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
        /// Classifies a project root by its on-disk git layout. Cheap (one or two
        /// filesystem stats plus a small read of the <c>.git</c> file when it's a
        /// gitlink) and side-effect-free except for an info-level log the first
        /// time a <see cref="GitRepoLayout.Worktree"/> or
        /// <see cref="GitRepoLayout.Submodule"/> case fires for a given project —
        /// useful telemetry to gauge how often the gitlink cases show up in
        /// practice without spamming the log on every UI refresh.
        ///
        /// <para>Discriminates worktree vs submodule by reading the first line of
        /// the <c>.git</c> file (<c>gitdir: …/.git/worktrees/&lt;name&gt;</c> vs
        /// <c>gitdir: …/.git/modules/&lt;name&gt;</c>). Any other gitlink shape
        /// falls back to <see cref="GitRepoLayout.Submodule"/> as the safer
        /// classification — keeps the HUD Git tab's empty-state behavior intact
        /// for unknown layouts.</para>
        ///
        /// <para>Used by <c>HudGitRenderer</c> to choose between the normal
        /// header-and-body rendering, the worktree switcher-enabled rendering,
        /// the submodule empty-state, and the no-repo empty-state — without
        /// going through the throwing <see cref="GitRepoService"/> constructor
        /// on the unsupported cases.</para>
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
                var classification = ClassifyGitlink(gitPath);
                lock (_lock)
                {
                    if (_loggedLinkedDirs.Add(canonical))
                    {
                        Debug.WriteLine(
                            $"[GitRepoManager] Detected linked .git for '{canonical}' (classified as {classification}).");
                    }
                }
                return classification;
            }
            return GitRepoLayout.NotARepo;
        }

        /// <summary>
        /// Reads the first non-empty line of a <c>.git</c> gitlink file and
        /// classifies it as <see cref="GitRepoLayout.Worktree"/> (gitdir points
        /// inside a <c>worktrees/</c> admin subdir) or
        /// <see cref="GitRepoLayout.Submodule"/> (gitdir points inside a
        /// <c>modules/</c> admin subdir, or shape is unrecognized). Read is
        /// bounded so a malformed/huge <c>.git</c> file doesn't blow up the
        /// caller — only the first 1 KB matters, the rest is git's business.
        ///
        /// <para>On any I/O failure returns <see cref="GitRepoLayout.Submodule"/>
        /// as the conservative default — keeps the empty-state UX intact rather
        /// than accidentally letting an unreadable gitlink flow through the
        /// worktree path.</para>
        /// </summary>
        private static GitRepoLayout ClassifyGitlink(string gitFilePath)
        {
            try
            {
                using var stream = new FileStream(
                    gitFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                char[] buf = new char[1024];
                int read = reader.Read(buf, 0, buf.Length);
                if (read <= 0) return GitRepoLayout.Submodule;
                string head = new string(buf, 0, read);

                // First non-empty line. The git format is `gitdir: <path>` but be
                // tolerant of a leading BOM or whitespace.
                string firstLine = null;
                foreach (var raw in head.Split('\n'))
                {
                    string line = raw.TrimEnd('\r').Trim();
                    if (line.Length == 0) continue;
                    firstLine = line;
                    break;
                }
                if (firstLine == null) return GitRepoLayout.Submodule;

                const string Prefix = "gitdir:";
                int idx = firstLine.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return GitRepoLayout.Submodule;

                string gitDir = firstLine.Substring(idx + Prefix.Length).Trim();
                if (gitDir.Length == 0) return GitRepoLayout.Submodule;

                // Normalize separators so we can match against canonical
                // segments regardless of how git wrote the path (forward- or
                // back-slashes).
                string normalized = gitDir.Replace('\\', '/');
                if (normalized.IndexOf("/worktrees/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GitRepoLayout.Worktree;
                if (normalized.IndexOf("/modules/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GitRepoLayout.Submodule;

                // Unknown gitlink shape — neither worktree (/worktrees/) nor
                // submodule (/modules/). Could be a legitimate non-default
                // layout (separate-git-dir, externally-relocated gitdir) or
                // malformed metadata. Return UnsupportedLink so the caller
                // can render a distinct empty-state instead of misclassifying
                // as a submodule.
                return GitRepoLayout.UnsupportedLink;
            }
            catch
            {
                // I/O failure — be conservative and treat as submodule. Same
                // empty-state UX as UnsupportedLink today, but signals that
                // we couldn't read the metadata at all.
                return GitRepoLayout.Submodule;
            }
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
