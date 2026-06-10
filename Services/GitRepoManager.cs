using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

        // ---------------------------------------------------------------------
        // libgit2 owner-validation bypass (process-wide, one-time)
        // ---------------------------------------------------------------------

        /// <summary>
        /// libgit2's <c>git_libgit2_opt_t</c> enum value for
        /// <c>SET_OWNER_VALIDATION</c>. Stable at 36 since libgit2 1.5.0 (new
        /// opts are appended, never reordered) — verified against the bundled
        /// native (2.0.322) by an empirical round-trip: <c>opts(36, 0)</c> makes
        /// <c>Repository.IsValid</c> succeed on an Administrators-owned repo that
        /// otherwise throws "not owned by current user". Task 309adb48.
        /// </summary>
        private const int GIT_OPT_SET_OWNER_VALIDATION = 36;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GitLibgit2OptsInt(int option, int value);

        // Runs once, in the type initializer, before any instance method
        // (DetectLayout / GetOrCreate) can open a repository.
        static GitRepoManager()
        {
            TryDisableLibGit2OwnerValidation();
        }

        /// <summary>
        /// Disables libgit2's "dubious ownership" guard for this process so the
        /// HUD Git tab can read repositories whose folder is owned by a different
        /// principal than the MultiTerminal process user — most commonly a repo
        /// created by an elevated process (owner <c>BUILTIN\Administrators</c>).
        ///
        /// <para>The git CLI opens such repos via a Windows-specific special case
        /// (current user is an Administrator + dir owned by Administrators =
        /// treated as owned), which is why the header strip (git CLI via
        /// <c>WorktreeList</c>) shows a dirty count while the Git tab body
        /// (LibGit2Sharp) renders "No git repository": <see cref="GitRepoService"/>'s
        /// ctor calls <c>Repository.IsValid</c>, libgit2 throws
        /// <c>repository path … is not owned by current user</c>, and
        /// <see cref="GetOrCreate"/> swallows it to <c>null</c>. libgit2 does NOT
        /// implement the CLI's admin-group special-case, so we relax the check.</para>
        ///
        /// <para>Security: libgit2 is used here strictly read-only (status / diff /
        /// log / branches) and never runs hooks or fsmonitor, so the
        /// code-execution risk owner-validation guards against (git CLI running a
        /// planted repo's hooks/config) does not apply. The user already opens
        /// these repos via the git CLI. Scope is the MT process only — no change
        /// to the user's global gitconfig / <c>safe.directory</c>.</para>
        ///
        /// <para>LibGit2Sharp 0.30.0 doesn't wrap <c>GIT_OPT_SET_OWNER_VALIDATION</c>,
        /// so we P/Invoke the bundled native <c>git_libgit2_opts</c> directly. The
        /// native library is resolved dynamically (its name carries a per-build
        /// hash, e.g. <c>git2-a418d9d.dll</c>) so a native-package bump doesn't
        /// silently break the call. Best-effort: any failure is logged and
        /// swallowed — MT degrades to the prior behavior (admin-owned repos show
        /// the empty-state) rather than failing to construct the manager.</para>
        /// </summary>
        private static bool TryDisableLibGit2OwnerValidation()
        {
            try
            {
                // Force LibGit2Sharp to load its native library before we resolve
                // the export. Touching GlobalSettings triggers the native load.
                _ = LibGit2Sharp.GlobalSettings.Version;

                IntPtr handle = ResolveLibGit2NativeHandle();
                if (handle == IntPtr.Zero)
                {
                    Debug.WriteLine("[GitRepoManager] Could not locate the libgit2 native module; owner-validation left enabled.");
                    return false;
                }

                if (!NativeLibrary.TryGetExport(handle, "git_libgit2_opts", out IntPtr fn) || fn == IntPtr.Zero)
                {
                    Debug.WriteLine("[GitRepoManager] git_libgit2_opts export not found; owner-validation left enabled.");
                    return false;
                }

                var opts = Marshal.GetDelegateForFunctionPointer<GitLibgit2OptsInt>(fn);
                int rc = opts(GIT_OPT_SET_OWNER_VALIDATION, 0);
                if (rc != 0)
                {
                    Debug.WriteLine($"[GitRepoManager] git_libgit2_opts(SET_OWNER_VALIDATION, 0) returned {rc}; owner-validation may still be enabled.");
                    return false;
                }

                Debug.WriteLine("[GitRepoManager] Disabled libgit2 owner validation for this process (admin-owned repos are now readable).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GitRepoManager] Could not disable libgit2 owner validation: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Resolves a handle to the already-loaded libgit2 native module
        /// (<c>git2-*.dll</c>). Prefers the module the CLR has already mapped (so
        /// we operate on the exact instance LibGit2Sharp uses), falling back to a
        /// scan of the app base directory. Returns <see cref="IntPtr.Zero"/> if no
        /// candidate can be loaded.
        /// </summary>
        private static IntPtr ResolveLibGit2NativeHandle()
        {
            // 1) Already-mapped module — NativeLibrary.TryLoad on its full path
            //    returns the existing handle (Windows LoadLibrary refcounts the
            //    same module) rather than mapping a second copy.
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    string name = m.ModuleName ?? string.Empty;
                    if (name.StartsWith("git2-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && NativeLibrary.TryLoad(m.FileName, out IntPtr h))
                    {
                        return h;
                    }
                }
            }
            catch
            {
                // Module enumeration is best-effort; fall through to the dir scan.
            }

            // 2) Fallback: scan the app base dir (the native dll ships next to the
            //    managed assemblies for the win-x64 RID).
            try
            {
                foreach (string path in Directory.EnumerateFiles(
                             AppContext.BaseDirectory, "git2-*.dll", SearchOption.AllDirectories))
                {
                    if (NativeLibrary.TryLoad(path, out IntPtr h))
                        return h;
                }
            }
            catch
            {
                // Fallback scan is best-effort.
            }

            return IntPtr.Zero;
        }

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
            string repoRoot = ResolveRepoRoot(projectRoot);
            if (repoRoot == null) return GitRepoLayout.NotARepo;

            string gitPath = Path.Combine(repoRoot, ".git");
            if (Directory.Exists(gitPath)) return GitRepoLayout.Standard;
            if (File.Exists(gitPath))
            {
                var classification = ClassifyGitlink(gitPath);
                lock (_lock)
                {
                    if (_loggedLinkedDirs.Add(repoRoot))
                    {
                        Debug.WriteLine(
                            $"[GitRepoManager] Detected linked .git for '{repoRoot}' (classified as {classification}).");
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
        /// True when <paramref name="gitFilePath"/> is a <c>.git</c> gitlink file
        /// whose <c>gitdir:</c> target directory no longer exists on disk — a
        /// worktree (or submodule) whose admin directory was pruned out from
        /// under it, leaving a dangling pointer. <see cref="ResolveRepoRoot"/>
        /// walks PAST such a gitlink to the enclosing common repo, because
        /// LibGit2Sharp's <c>Repository.IsValid</c> rejects a dangling-gitlink
        /// path and <see cref="GetOrCreate"/> would otherwise return <c>null</c>
        /// (rendering the HUD Git tab's "No git repository" empty-state even
        /// though a real repo sits just above the stranded worktree dir).
        ///
        /// <para>Conservative by design: returns <c>false</c> on any read error
        /// or malformed metadata, so only a POSITIVELY-confirmed missing target
        /// triggers the walk-past. Healthy gitlinks (target exists) and
        /// unreadable ones preserve the prior "stop here" behavior.</para>
        /// </summary>
        private static bool IsDanglingGitlink(string gitFilePath)
        {
            try
            {
                string firstLine = null;
                using (var stream = new FileStream(
                    gitFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    char[] buf = new char[1024];
                    int read = reader.Read(buf, 0, buf.Length);
                    if (read <= 0) return false;
                    string head = new string(buf, 0, read);
                    foreach (var raw in head.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r').Trim();
                        if (line.Length == 0) continue;
                        firstLine = line;
                        break;
                    }
                }
                if (firstLine == null) return false;

                const string Prefix = "gitdir:";
                int idx = firstLine.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                string gitDir = firstLine.Substring(idx + Prefix.Length).Trim();
                if (gitDir.Length == 0) return false;

                // `gitdir:` is usually absolute on Windows but may be written
                // relative to the worktree dir that holds the .git file. Resolve
                // both shapes (forward-slash separators are fine for GetFullPath
                // / Directory.Exists on Windows).
                string baseDir = Path.GetDirectoryName(gitFilePath);
                string resolved = Path.IsPathRooted(gitDir)
                    ? Path.GetFullPath(gitDir)
                    : Path.GetFullPath(Path.Combine(baseDir ?? string.Empty, gitDir));

                return !Directory.Exists(resolved);
            }
            catch
            {
                // Can't read/parse the gitlink — don't claim it's dangling; let
                // the caller keep its prior "this is a valid stopping point"
                // behavior rather than over-eagerly walking up.
                return false;
            }
        }

        /// <summary>
        /// Returns a cached or freshly-opened <see cref="GitRepoService"/> for the
        /// given project root, or <c>null</c> if the path is not a valid git repo.
        /// </summary>
        public GitRepoService GetOrCreate(string projectRoot)
        {
            if (_disposed || string.IsNullOrEmpty(projectRoot)) return null;
            string key = ResolveRepoRoot(projectRoot);
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
            string key = ResolveRepoRoot(projectRoot);
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
        ///
        /// <para>Eviction tries <see cref="ResolveRepoRoot"/> first (the same key
        /// derivation as <see cref="GetOrCreate"/>) and, if that returns null
        /// because the <c>.git</c> has disappeared, falls back to scanning
        /// <see cref="_byPath"/> for an entry whose key is the canonicalised
        /// input or an ancestor of it. The fallback preserves the documented
        /// invalidation use cases (project rename, <c>git init</c> rerun, repo
        /// move) — exactly the cases where the caller invalidates because the
        /// filesystem state has changed.</para>
        /// </summary>
        public void Release(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return;
            string resolvedKey = ResolveRepoRoot(projectRoot);
            // Always compute the canonical form too. We need it as a FALLBACK
            // when resolvedKey is non-null but doesn't match the cache — which
            // happens after a worktree directory has been deleted: walking up
            // for .git now finds the MAIN repo's .git instead of the worktree's
            // own gitlink, so resolvedKey is the main-repo path even though the
            // cache entry we want to evict is keyed by the worktree path. The
            // ancestor-fallback scan on `canonical` recovers it.
            string canonical = CanonicalizeProjectRoot(projectRoot);

            GitRepoService toDispose = null;
            lock (_lock)
            {
                string keyToRemove = null;
                // 1. Exact match on the canonical input path. The cache key
                //    that GetOrCreate created for a worktree is the worktree's
                //    own resolved path; once the worktree directory is deleted
                //    ResolveRepoRoot can no longer reach that key (its walk-up
                //    lands on the main repo's .git instead), but the canonical
                //    input string still matches the original key character-for-
                //    character. Check this FIRST so the deleted-worktree case
                //    doesn't fall into branch 2 and evict the wrong cache.
                if (canonical != null && _byPath.ContainsKey(canonical))
                {
                    keyToRemove = canonical;
                }
                else if (resolvedKey != null && _byPath.ContainsKey(resolvedKey))
                {
                    keyToRemove = resolvedKey;
                }
                else if (canonical != null)
                {
                    // Pick the LONGEST matching ancestor — when nested repos are
                    // cached (e.g. both `C:\outer` and `C:\outer\inner`), the
                    // most specific root is the one the caller actually meant
                    // to invalidate. First-match-wins would pick whichever the
                    // dictionary enumerated first, which is not guaranteed and
                    // could dispose an unrelated live service while leaving
                    // the intended stale entry stranded.
                    foreach (var k in _byPath.Keys)
                    {
                        if (IsSameOrAncestor(k, canonical) &&
                            (keyToRemove == null || k.Length > keyToRemove.Length))
                        {
                            keyToRemove = k;
                        }
                    }
                }

                if (keyToRemove != null && _byPath.TryGetValue(keyToRemove, out var svc))
                {
                    toDispose = svc;
                    _byPath.Remove(keyToRemove);
                }
            }
            if (toDispose != null)
            {
                try { toDispose.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Canonicalises an external path for prefix/equality comparisons.
        /// Exposes <see cref="CanonicalizeProjectRoot"/> to other components
        /// (e.g. <c>HudGitRenderer</c>) so they don't need their own (and
        /// inevitably drifting) canonicalisation routine before calling
        /// <see cref="IsSameOrAncestor"/>. Returns <c>null</c> on malformed
        /// input.
        /// </summary>
        internal static string Canonicalize(string path) => CanonicalizeProjectRoot(path);

        /// <summary>
        /// Returns true if <paramref name="descendant"/> is the same path as
        /// <paramref name="ancestor"/> or sits underneath it. Both inputs are
        /// expected to be canonical (trailing separators trimmed, full paths)
        /// — see <see cref="CanonicalizeProjectRoot"/> / <see cref="Canonicalize"/>.
        /// Case-insensitive to match the rest of the cache-key comparisons
        /// on this class.
        /// </summary>
        internal static bool IsSameOrAncestor(string ancestor, string descendant)
        {
            if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant)) return false;
            if (string.Equals(ancestor, descendant, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = ancestor + Path.DirectorySeparatorChar;
            return descendant.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
        /// Resolves a caller-supplied project path to the enclosing repository
        /// root by walking the parent chain until an ancestor containing a
        /// <c>.git</c> (directory or gitlink file) is found. Returns the canonical
        /// repository-root path, or <c>null</c> if no <c>.git</c> is found before
        /// the drive root.
        ///
        /// <para>This is what lets the HUD Git tab, dashboard, and per-terminal
        /// git status work when the project path is a subdir of a repo — e.g.
        /// a worktree subdir whose <c>.git</c> gitlink sits at the worktree root
        /// rather than at the subdir level. Without walk-up, those callers
        /// would classify the path as <see cref="GitRepoLayout.NotARepo"/> and
        /// render the empty-state.</para>
        ///
        /// <para>Routing all cache-key derivation through this method also
        /// consolidates two subdirs of the same repo onto a single cached
        /// <see cref="GitRepoService"/> entry instead of opening two Repository
        /// handles for the same underlying git directory.</para>
        ///
        /// <para>The walk is bounded (<c>MaxDepth</c>) to avoid pathological
        /// loops on unusual filesystems. 64 levels is comfortably past any
        /// realistic project depth.</para>
        /// </summary>
        private static string ResolveRepoRoot(string projectRoot)
        {
            string canonical = CanonicalizeProjectRoot(projectRoot);
            if (canonical == null) return null;

            const int MaxDepth = 64;
            string cursor = canonical;
            for (int i = 0; i < MaxDepth && !string.IsNullOrEmpty(cursor); i++)
            {
                string gitPath = Path.Combine(cursor, ".git");
                try
                {
                    if (Directory.Exists(gitPath))
                        return cursor;

                    // A `.git` gitlink FILE normally marks a worktree/submodule
                    // root and is a valid stopping point. But if its `gitdir:`
                    // target has been pruned out from under it (the admin dir
                    // `.git/worktrees/<id>` removed when the task completed),
                    // the gitlink is DANGLING: Repository.IsValid rejects it and
                    // GetOrCreate would return null, leaving the HUD Git tab
                    // stuck on its "No git repository" empty-state even though an
                    // enclosing common repo sits right above. Walk PAST a
                    // dangling gitlink to that parent; stop on a healthy one.
                    if (File.Exists(gitPath) && !IsDanglingGitlink(gitPath))
                        return cursor;
                }
                catch
                {
                    // I/O issue probing this level — keep walking; a parent
                    // may still be readable.
                }

                try
                {
                    cursor = Directory.GetParent(cursor)?.FullName;
                }
                catch
                {
                    // Unreadable parent (permissions, malformed path) — stop
                    // here rather than spin.
                    return null;
                }
            }
            return null;
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
