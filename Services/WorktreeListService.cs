using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One entry in the HUD Git tab's repo-switcher list. Describes either the
    /// main checkout of a repository (<see cref="IsMain"/>=true) or one of its
    /// linked worktrees. <see cref="LinkedTaskId"/> and
    /// <see cref="LinkedTaskTitle"/> are populated when an entry's path matches
    /// an <c>active</c> row in the <c>task_worktrees</c> table — gives the
    /// switcher a human-readable "which task lives here" label.
    /// </summary>
    public sealed class WorktreeEntry
    {
        /// <summary>Absolute path to the working tree (forward-slash normalized).</summary>
        public string Path { get; set; }

        /// <summary>Friendly branch name, or <c>(detached)</c> / <c>(bare)</c> sentinels.</summary>
        public string Branch { get; set; }

        /// <summary><c>true</c> when this is the main checkout of the repository.
        /// <c>git worktree list --porcelain</c> emits the main checkout as the
        /// first entry, before any linked worktrees.</summary>
        public bool IsMain { get; set; }

        /// <summary>Count of modified/untracked/etc. files in this working tree.
        /// Computed via <c>git status --porcelain</c> at list time (independent
        /// of <see cref="GitRepoService"/> so the list works even when the
        /// per-worktree git read-layer hasn't been opened yet).</summary>
        public int DirtyCount { get; set; }

        /// <summary>Task ID this worktree is linked to, or <c>null</c> when no
        /// <c>task_worktrees</c> row matches. Join is by canonical path
        /// comparison.</summary>
        public string LinkedTaskId { get; set; }

        /// <summary>Title of the linked task, or <c>null</c> when no link.
        /// Resolved via <c>MessageBroker.GetTask</c> for the cached title.</summary>
        public string LinkedTaskTitle { get; set; }

        /// <summary>Agent that owns this worktree row (per-agent isolation,
        /// task bab81a92), or <c>null</c> when no link. The assignee owns the
        /// canonical worktree; helpers own <c>task/&lt;id&gt;--&lt;slug&gt;</c>
        /// worktrees. Lets the HUD group/label multiple worktrees under one task
        /// by agent.</summary>
        public string LinkedAgent { get; set; }

        /// <summary><c>true</c> when this is the task's canonical (assignee)
        /// worktree; <c>false</c> for a helper worktree. Mirrors
        /// <c>task_worktrees.is_canonical</c>.</summary>
        public bool LinkedIsCanonical { get; set; }
    }

    /// <summary>
    /// Lists the worktrees of a git repository (the main checkout plus every
    /// linked worktree from <c>git worktree list --porcelain</c>) and joins
    /// each entry with the <c>task_worktrees</c> table so the HUD Git tab's
    /// switcher can label each entry with its owning task title.
    ///
    /// <para>Stateless. Holds no caches — call sites are infrequent (panel
    /// open / refresh / switcher click) so the cost of one git invocation per
    /// list is negligible. Each list call runs <c>git worktree list</c> once
    /// plus one <c>git status --porcelain</c> per worktree to compute dirty
    /// counts.</para>
    ///
    /// <para>Independent of <see cref="GitRepoService"/> by design — works for
    /// worktrees even when LibGit2Sharp's worktree handling hasn't been
    /// exercised yet (or fails). Shells out via plain <c>git</c> on PATH.</para>
    /// </summary>
    public sealed class WorktreeListService
    {
        private readonly TaskDatabase _db;
        private readonly MessageBroker _broker;

        /// <summary>Maximum milliseconds to wait for a single git invocation
        /// before giving up. Keeps a hung git from stalling the HUD Git tab
        /// indefinitely on a flaky drive.</summary>
        private const int GitTimeoutMs = 5000;

        public WorktreeListService(TaskDatabase db, MessageBroker broker)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        }

        /// <summary>
        /// Returns the worktrees of the repository containing
        /// <paramref name="workingDir"/>. The main checkout is the first
        /// element; linked worktrees follow in the order git emits them.
        /// Returns an empty list when <paramref name="workingDir"/> is not
        /// inside a git repository, when git isn't on PATH, or when the
        /// invocation times out — callers should treat empty as "no switcher
        /// to render."
        /// </summary>
        public async Task<List<WorktreeEntry>> GetWorktreesForRepoAsync(string workingDir)
        {
            // CA3003: workingDir originates from broker._projects (trusted, populated by
            // ProjectService) or from HudGitRenderer's SetProject(path) call. REST callers
            // never supply this path directly — they pass a task id which the broker resolves
            // through its internal _tasks → _projects dictionaries. Taint analysis can't see
            // through those lookups, so suppress at the sink.
#pragma warning disable CA3003
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
                return new List<WorktreeEntry>();
#pragma warning restore CA3003

            var (exitCode, stdout, _) = await RunGitAsync(
                workingDir, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (exitCode != 0 || string.IsNullOrEmpty(stdout))
                return new List<WorktreeEntry>();

            var entries = ParsePorcelain(stdout);
            if (entries.Count == 0) return entries;

            // First entry from git worktree list is the main checkout.
            entries[0].IsMain = true;

            // Path safety pass: git worktree list trusts the on-disk admin
            // metadata, which a hostile repository can forge to point at
            // arbitrary local paths or UNC shares. Reject anything that isn't
            // a canonical, local, non-reparse-point directory before we feed
            // it back into git (GetDirtyCountAsync) or surface it to the
            // webview (where SwitchToRepo would happily bind to it).
            // Mirrors the GitRepoService.IsSafeRelativePath posture (which
            // hardens repo-relative paths the same way).
            entries.RemoveAll(e => e == null || !IsSafeWorktreePath(e.Path));
            if (entries.Count == 0) return entries;

            // Repository-identity membership pass: IsSafeWorktreePath only
            // checks that a path is local/canonical/safe. A hostile repo can
            // still forge `git worktree list --porcelain` output to claim an
            // arbitrary local path as a worktree of THIS repo. Authoritative
            // ownership is determined by the common gitdir — every legitimate
            // worktree of a repo shares the same `--git-common-dir`. Resolve
            // the source repo's common-dir once, then per-entry, and drop any
            // entry whose common-dir doesn't match.
            string sourceCommonDir = await ResolveCommonGitDirAsync(workingDir).ConfigureAwait(false);
            if (string.IsNullOrEmpty(sourceCommonDir))
            {
                // Can't establish source identity — refuse to surface any
                // entries. Empty list = no switcher to render. Better to lose
                // the chrome than to vouch for paths we can't authenticate.
                return new List<WorktreeEntry>();
            }
            var entryCommonDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var validEntries = new List<WorktreeEntry>(entries.Count);
            foreach (var entry in entries)
            {
                string candidateCommon = await ResolveCommonGitDirAsync(entry.Path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(candidateCommon)) continue;
                if (!string.Equals(candidateCommon, sourceCommonDir, StringComparison.OrdinalIgnoreCase)) continue;
                entryCommonDirs[entry.Path] = candidateCommon;
                validEntries.Add(entry);
            }
            entries = validEntries;
            if (entries.Count == 0) return entries;

            // Re-mark main after the filter — the first surviving entry from
            // git's emission order is the main checkout (git lists main first;
            // a filter pass can't change the relative order).
            for (int i = 0; i < entries.Count; i++) entries[i].IsMain = (i == 0);

            // Dirty counts: run one git status per entry. Capped to keep the
            // switcher snappy on repos with many worktrees — beyond the cap
            // the count stays at its sentinel value (-1) and the UI can show
            // "?" instead of a number.
            //
            // TOCTOU revalidation: re-check IsSafeWorktreePath right before
            // re-feeding the path to git. Closes the window where the
            // directory could be swapped for a junction between the original
            // safety check and this use. Entries that fail are sentinelled to
            // -1 rather than passed to git.
            const int dirtyCountCap = 16;
            int counted = 0;
            foreach (var entry in entries)
            {
                if (counted >= dirtyCountCap)
                {
                    entry.DirtyCount = -1;
                    continue;
                }
                if (!IsSafeWorktreePath(entry.Path))
                {
                    entry.DirtyCount = -1;
                    continue;
                }
                entry.DirtyCount = await GetDirtyCountAsync(entry.Path).ConfigureAwait(false);
                counted++;
            }

            // Linked-task join: pull active task_worktrees rows and match by
            // canonical path. Match in-memory; active set is small (typically
            // a handful of in-flight tasks).
            var activeWorktrees = _db.ListActiveWorktrees();
            foreach (var entry in entries)
            {
                string entryCanonical = CanonicalizePath(entry.Path);
                foreach (var record in activeWorktrees)
                {
                    if (string.IsNullOrEmpty(record.WorktreePath)) continue;
                    if (!string.Equals(
                        CanonicalizePath(record.WorktreePath),
                        entryCanonical,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    entry.LinkedTaskId = record.TaskId;
                    entry.LinkedAgent = record.AgentName;
                    entry.LinkedIsCanonical = record.IsCanonical;
                    try
                    {
                        var task = _broker.GetTask(record.TaskId);
                        entry.LinkedTaskTitle = task?.Title;
                    }
                    catch
                    {
                        // Broker lookup failed — leave the title null; the
                        // task id is still useful for the switcher.
                    }
                    break;
                }
            }

            return entries;
        }

        /// <summary>
        /// Parses the output of <c>git worktree list --porcelain</c>. Format:
        /// each entry is a block of <c>key value</c> lines (<c>worktree</c>,
        /// <c>HEAD</c>, <c>branch</c> | <c>detached</c> | <c>bare</c>),
        /// separated by blank lines. The first <c>worktree</c> line of each
        /// block is the absolute path; the <c>branch</c> line is the full ref
        /// (<c>refs/heads/&lt;name&gt;</c>) which we trim to the friendly name.
        /// </summary>
        private static List<WorktreeEntry> ParsePorcelain(string stdout)
        {
            var entries = new List<WorktreeEntry>();
            WorktreeEntry current = null;

            using var reader = new StringReader(stdout);
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (current != null) { entries.Add(current); current = null; }
                    continue;
                }

                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    if (current != null) entries.Add(current);
                    current = new WorktreeEntry
                    {
                        Path = line.Substring("worktree ".Length).Trim(),
                        Branch = "(unknown)",
                        DirtyCount = -1,
                    };
                }
                else if (current != null && line.StartsWith("branch ", StringComparison.Ordinal))
                {
                    string fullRef = line.Substring("branch ".Length).Trim();
                    const string Prefix = "refs/heads/";
                    current.Branch = fullRef.StartsWith(Prefix, StringComparison.Ordinal)
                        ? fullRef.Substring(Prefix.Length)
                        : fullRef;
                }
                else if (current != null && line.Equals("detached", StringComparison.Ordinal))
                {
                    current.Branch = "(detached)";
                }
                else if (current != null && line.Equals("bare", StringComparison.Ordinal))
                {
                    current.Branch = "(bare)";
                }
            }
            if (current != null) entries.Add(current);

            return entries;
        }

        /// <summary>
        /// Counts working-tree changes via <c>git -C &lt;path&gt; status --porcelain</c>.
        /// Includes modified, added, deleted, renamed, type-changed, and
        /// untracked entries — anything git would print. Returns <c>-1</c> on
        /// failure / timeout so the UI can distinguish "0 changes" from
        /// "unknown."
        /// </summary>
        private async Task<int> GetDirtyCountAsync(string worktreePath)
        {
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return -1;

            var (exitCode, stdout, _) = await RunGitAsync(
                worktreePath, "status", "--porcelain").ConfigureAwait(false);
            if (exitCode != 0) return -1;
            if (string.IsNullOrEmpty(stdout)) return 0;

            int count = 0;
            foreach (var line in stdout.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line)) count++;
            }
            return count;
        }

        /// <summary>
        /// Runs <c>git rev-parse --git-common-dir</c> in <paramref name="workingDir"/>
        /// and returns the canonical path to the shared git common directory
        /// (the parent repository's <c>.git</c> directory). Two worktrees of
        /// the same repository return identical values; worktrees of
        /// different repositories return different values. Used as the
        /// authoritative identity check for "is this entry actually a
        /// worktree of THIS repo, or did porcelain metadata forge it?"
        ///
        /// <para>Returns empty on any failure (git not on PATH, not a repo,
        /// timeout, hostile output) — callers must treat empty as "unknown
        /// identity, reject the entry."</para>
        /// </summary>
        private async Task<string> ResolveCommonGitDirAsync(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return string.Empty;
            // CA3003: see suppression rationale on GetWorktreesForRepoAsync above.
#pragma warning disable CA3003
            if (!Directory.Exists(workingDir)) return string.Empty;
#pragma warning restore CA3003

            var (exitCode, stdout, _) = await RunGitAsync(
                workingDir, "rev-parse", "--git-common-dir").ConfigureAwait(false);
            if (exitCode != 0) return string.Empty;
            string raw = (stdout ?? string.Empty).Trim();
            if (raw.Length == 0) return string.Empty;

            // git may emit a relative path (relative to workingDir). Resolve
            // against workingDir before canonicalising, so two callers from
            // different cwds inside the same repo still hash identically.
            string resolved;
            try
            {
                resolved = Path.IsPathRooted(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(Path.Combine(workingDir, raw));
            }
            catch
            {
                return string.Empty;
            }

            return resolved.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// Returns <c>true</c> iff <paramref name="path"/> is a safe target
        /// for re-feeding to git as a working directory and for emitting to
        /// the webview as a switcher entry. Rejects:
        /// <list type="bullet">
        /// <item>null / empty</item>
        /// <item>UNC paths (<c>\\server\share</c>, <c>//server/share</c>) —
        /// would trigger outbound SMB/NTLM authentication when git or
        /// FileSystemWatcher binds to them. Hostile-repo concern.</item>
        /// <item>non-rooted paths — porcelain emits absolute paths; a relative
        /// path here means the admin data was tampered with.</item>
        /// <item>paths that don't resolve to a real on-disk directory after
        /// canonicalisation</item>
        /// <item>paths whose canonical form passes through a reparse-point
        /// directory (junction/symlink) — same defense as
        /// <see cref="GitRepoService.IsSafeRelativePath"/>; a reparse point
        /// inside a "worktree" path is a redirect to anywhere on disk.</item>
        /// </list>
        ///
        /// <para>Trusts that git is on PATH and emits forward-slash absolute
        /// paths under normal operation — same trust assumption already in
        /// place for every other git invocation in this codebase.</para>
        /// </summary>
        private static bool IsSafeWorktreePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // UNC rejection (covers both `\\` and `//` prefixes — git often
            // emits forward slashes).
            string trimmed = path.TrimStart();
            if (trimmed.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;

            // Must be rooted (`C:\...`, `C:/...`). Git's --porcelain always
            // emits absolute paths.
            if (!Path.IsPathRooted(path)) return false;

            string canonical;
            try { canonical = Path.GetFullPath(path); }
            catch { return false; }

            // Re-check UNC after canonicalisation in case Path.GetFullPath
            // resolved a relative path against an unexpected base.
            if (canonical.StartsWith("\\\\", StringComparison.Ordinal)) return false;

            try
            {
                if (!Directory.Exists(canonical)) return false;

                // Walk the path and reject if any segment is a reparse point.
                // A junction or symlink under the canonical path can redirect
                // to anywhere on disk, defeating prefix-based trust checks.
                var info = new DirectoryInfo(canonical);
                while (info != null)
                {
                    if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) return false;
                    info = info.Parent;
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Normalises a filesystem path for cross-entry equality comparison.
        /// Resolves to its full path, normalises separators, and trims a
        /// trailing slash so <c>C:/foo</c>, <c>C:/foo/</c>, and
        /// <c>C:\foo</c> all hash to the same key.
        /// </summary>
        private static string CanonicalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                return path.Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Runs git as a child process with a wall-clock timeout. Returns
        /// <c>(-1, "", "")</c> on timeout or process-start failure — caller
        /// must treat non-zero exit codes as "no data." Process gets killed
        /// on timeout to avoid orphan hangs.
        /// </summary>
        private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(
            string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var exitTask = proc.WaitForExitAsync();

                var winner = await Task.WhenAny(exitTask, Task.Delay(GitTimeoutMs)).ConfigureAwait(false);
                if (winner != exitTask)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return (-1, string.Empty, string.Empty);
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                return (proc.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WorktreeListService] git invocation failed: {ex.Message}");
                return (-1, string.Empty, string.Empty);
            }
        }
    }
}
