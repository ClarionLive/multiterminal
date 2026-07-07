using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Wraps <c>git worktree add</c> and <c>git worktree remove</c> via process
    /// invocation. Phase 1 of per-task worktree isolation (task e1a5c579) — gated
    /// by <c>MULTITERMINAL_WORKTREE_MODE</c> at the call site.
    ///
    /// <para>Stateless service; takes a <see cref="TaskDatabase"/> reference at
    /// construction. Each public method takes <c>repoRoot</c> per call so the
    /// same manager can serve multiple projects in one MT instance.</para>
    ///
    /// <para>Path conventions: a worktree for task <c>abcd1234</c> lives at
    /// <c>{repoRoot}/.claude/worktrees/abcd1234/</c> on branch <c>task/abcd1234</c>,
    /// forked from the current HEAD of <c>repoRoot</c> at create time. The
    /// <c>.claude/worktrees/</c> dir is a descendant of the repo root (gitignored)
    /// so that (a) every worktree is a descendant of the main checkout — important
    /// for Claude Code's permission scope, harness cwd pinning, AND the
    /// <c>EnterWorktree(path=...)</c> enter-existing form, which requires the target
    /// be a worktree under <c>.claude/worktrees/</c> for cwd-pinned-at-launch agents
    /// (task 0134ec2f) — and (b) all per-task scratch space is contained inside the
    /// project folder rather than floating in the parent dir.</para>
    /// </summary>
    public class WorktreeManager
    {
        private readonly TaskDatabase _db;
        private readonly WorktreeActivityCallback _activitySink;

        /// <summary>
        /// Activity-log callback signature: <c>(action, content, relatedId)</c>.
        /// Lets <see cref="WorktreeManager"/> surface notable events (e.g. a
        /// partial-prune strand) to the activity feed without taking a hard
        /// dependency on the broker / MCPServer namespace — same decoupling
        /// pattern as <see cref="WorktreeJanitorService.JanitorActivityCallback"/>.
        /// The wire-up site (the broker) routes this to its RecordActivity.
        /// </summary>
        public delegate void WorktreeActivityCallback(string action, string content, string relatedId);

        /// <summary>
        /// Creates a new WorktreeManager backed by the supplied
        /// <see cref="TaskDatabase"/> for record persistence.
        /// </summary>
        /// <param name="db">Task database for worktree-record persistence.</param>
        /// <param name="activitySink">Optional sink for surfacing notable
        /// worktree events to the activity feed (e.g. a partial-prune strand,
        /// task 248cc2ce). Pass null to suppress (headless / test contexts).</param>
        public WorktreeManager(TaskDatabase db, WorktreeActivityCallback activitySink = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _activitySink = activitySink;
        }

        /// <summary>
        /// Materializes a git worktree for the given task and persists a record.
        /// Idempotent: if an <c>active</c> record exists AND its worktree path is
        /// on disk, the existing record is returned unchanged.
        /// </summary>
        /// <param name="taskId">Kanban task id (any length; the first 8 chars
        /// drive the path + branch name).</param>
        /// <param name="repoRoot">Filesystem path to the main checkout of the
        /// repository. Must exist and be a valid git repo.</param>
        /// <exception cref="InvalidOperationException">Thrown when
        /// <c>git worktree add</c> exits non-zero. The exception message
        /// includes the captured stderr.</exception>
        public Task<TaskWorktree> CreateForTaskAsync(string taskId, string repoRoot)
        {
            // Back-compat shim for callers that don't yet thread the agent name.
            // Resolves the task's assignee and creates the canonical worktree.
            // New call sites (task activation) should use the agent-aware overload
            // directly so helpers get their own worktree.
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            string assignee = _db.GetTask(taskId)?.Assignee;
            return CreateForTaskAsync(taskId, assignee, isAssignee: true, repoRoot);
        }

        /// <summary>
        /// Materializes a git worktree for a specific agent on a task and persists a
        /// per-agent record. Idempotent per <c>(taskId, agentName)</c>: if that
        /// agent already has an <c>active</c> worktree on disk it is returned unchanged.
        ///
        /// <para>When <paramref name="isAssignee"/> is true this is the canonical
        /// worktree — branch <c>task/&lt;idShort&gt;</c> at
        /// <c>.claude/worktrees/&lt;idShort&gt;</c> (byte-identical to the pre-isolation
        /// layout). Otherwise it is a helper worktree — branch
        /// <c>task/&lt;idShort&gt;--&lt;slug&gt;</c> at
        /// <c>.claude/worktrees/&lt;idShort&gt;--&lt;slug&gt;</c>, forked from the
        /// canonical branch's tip (the canonical branch is created first if absent).</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when a git command
        /// (branch / worktree add) exits non-zero. Message includes captured stderr.</exception>
        public async Task<TaskWorktree> CreateForTaskAsync(string taskId, string agentName, bool isAssignee, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            // For the canonical worktree the owning agent IS the assignee; fall back
            // to the legacy sentinel only when the name is genuinely unknown.
            string ownerAgent = string.IsNullOrEmpty(agentName) ? WorktreeNaming.LegacyAgent : agentName;

            // CA3003: taskId is an app-generated GUID, repoRoot is a registered
            // Project.Path, agentName is sanitized to a slug for path/branch use, and
            // existing.WorktreePath is read from task_worktrees (written only by this
            // class). No untrusted user-supplied path text flows into the path ops.
#pragma warning disable CA3003
            if (!Directory.Exists(repoRoot))
                throw new DirectoryNotFoundException($"Repo root does not exist: {repoRoot}");

            // Idempotent per (task, agent): an existing active worktree on disk wins.
            var existing = _db.GetWorktreeForTask(taskId, ownerAgent);
            if (existing != null
                && string.Equals(existing.Status, "active", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(existing.WorktreePath))
            {
                return existing;
            }

            // Normalize to the MAIN checkout root before building any path or running
            // any git op. The caller passes the registered Project.Path as repoRoot; if
            // that has drifted to (or is) a linked worktree, building
            // {repoRoot}/.claude/worktrees/<id> would NEST the new worktree inside it
            // (task 25020dfa — observed live as .../worktrees/0a2ac0cb/.claude/worktrees/<id>).
            // rev-parse --git-common-dir collapses both the main checkout and any linked
            // worktree to the same <mainRoot>/.git, so this is always the true main root.
            // Secondary win: routing the git ops below through mainRoot makes trunk
            // resolution read the main checkout's branch, not a linked worktree's task/ HEAD.
            string mainRoot = await ResolveMainCheckoutRootAsync(repoRoot).ConfigureAwait(false);
            string trimmed = mainRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Worktrees live under .claude/worktrees/ (was <repoRoot>/worktrees/).
            // Required by Claude Code's EnterWorktree(path=...) enter-existing form:
            // for cwd-pinned-at-launch agents (= MT terminals) the target MUST be a
            // worktree under .claude/worktrees/ of the same repo. Still a descendant
            // of the main checkout, so the permission-scope invariant is preserved.
            // (Task 0134ec2f.)
            string worktreesParent = Path.Combine(trimmed, ".claude", "worktrees");
            string canonicalBranch = WorktreeNaming.CanonicalBranch(taskId);
            string worktreePath = Path.Combine(
                worktreesParent, WorktreeNaming.DirNameFor(taskId, ownerAgent, isAssignee));
            string branchName = WorktreeNaming.BranchFor(taskId, ownerAgent, isAssignee);

            if (!Directory.Exists(worktreesParent))
            {
                Directory.CreateDirectory(worktreesParent);
            }
#pragma warning restore CA3003

            if (isAssignee)
            {
                // Canonical worktree on task/<idShort>. The branch may already exist
                // if a helper created it first (EnsureCanonicalBranchAsync) — in that
                // case check it out instead of re-creating it with -b. When creating
                // it fresh, root it explicitly at trunk (NOT at whatever HEAD points
                // to) and fail loudly on a detached/task-branch HEAD — bab81a92
                // pipeline fix. Behaviour is unchanged in the normal case (HEAD on
                // trunk), since `-b <name> <path> <trunk>` == `-b <name> <path>` there.
                bool canonicalExists = await BranchExistsAsync(mainRoot, canonicalBranch).ConfigureAwait(false);
                string startPoint = canonicalExists ? null : await ResolveTrunkAsync(mainRoot).ConfigureAwait(false);
                await AddWorktreeAsync(mainRoot, worktreePath, branchName, createNewBranch: !canonicalExists, startPoint: startPoint)
                    .ConfigureAwait(false);
            }
            else
            {
                // Helper worktree: ensure the canonical branch exists, then fork the
                // helper branch from its tip.
                await EnsureCanonicalBranchAsync(mainRoot, canonicalBranch).ConfigureAwait(false);
                await AddWorktreeAsync(mainRoot, worktreePath, branchName, createNewBranch: true, startPoint: canonicalBranch)
                    .ConfigureAwait(false);
            }

            // Phase 1 smoke-test finding: a fresh worktree contains only tracked
            // files, but some build-required artifacts in this repo are vendored
            // and gitignored (notably tools/rg.exe, excluded by the user's global
            // *.exe rule). Without them dotnet build fails on a post-build copy
            // step. Seed the known vendored directories from the main checkout.
            // Phase 2 should generalize this into a per-project post-create hook
            // (e.g. a script in .claude/ that runs after worktree add).
            SeedVendoredArtifacts(mainRoot, worktreePath);

            _db.SaveWorktreeRecord(taskId, ownerAgent, worktreePath, branchName, isAssignee);
            return _db.GetWorktreeForTask(taskId, ownerAgent);
        }

        /// <summary>True when <paramref name="branch"/> exists as a local head.</summary>
        private static async Task<bool> BranchExistsAsync(string repoRoot, string branch)
        {
            var (exitCode, _, _) = await GitExec.RunAsync(
                repoRoot, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}").ConfigureAwait(false);
            return exitCode == 0;
        }

        /// <summary>
        /// Resolve the trunk branch (the main checkout's current branch) that a new
        /// canonical task branch must be rooted at. Pipeline finding (bab81a92,
        /// cross-model adversary): a bare <c>git branch &lt;name&gt;</c> roots at
        /// whatever HEAD currently points to. If the main checkout is detached
        /// (e.g. the eea2c533 cwd-footgun left it so) or sitting on another
        /// <c>task/</c> branch, that silently roots the canonical branch at the wrong
        /// commit and pollutes the eventual trunk merge. Fail loudly instead.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when HEAD is detached or
        /// on a task branch — i.e. not a usable trunk to fork from.</exception>
        private static async Task<string> ResolveTrunkAsync(string repoRoot)
        {
            var (exitCode, stdout, stderr) = await GitExec.RunAsync(
                repoRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git rev-parse --abbrev-ref HEAD failed (exit {exitCode}). stderr: {stderr.Trim()}");
            }
            string trunk = stdout.Trim();
            if (string.Equals(trunk, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Main checkout is in detached HEAD; refusing to create the canonical task branch from an unknown base. Check out trunk first.");
            }
            if (trunk.StartsWith("task/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Main checkout is on a task branch ({trunk}), not trunk; refusing to root the canonical task branch there. Check out trunk first.");
            }
            return trunk;
        }

        /// <summary>
        /// Resolve the MAIN checkout root for a repo path that may itself be a linked
        /// worktree. The caller passes the registered <c>Project.Path</c> as
        /// <paramref name="repoRoot"/>; if that path has drifted to (or is) a linked
        /// worktree, building <c>{repoRoot}/.claude/worktrees/&lt;id&gt;</c> would NEST
        /// the new worktree inside it (task 25020dfa — observed live as
        /// <c>.../worktrees/0a2ac0cb/.claude/worktrees/&lt;id&gt;</c>).
        /// <para><c>git rev-parse --git-common-dir</c> returns the SHARED <c>.git</c>
        /// directory (<c>&lt;mainRoot&gt;/.git</c>) for the main checkout AND every
        /// linked worktree, so its parent is always the true main root.
        /// <c>--path-format=absolute</c> (git ≥ 2.31) forces an absolute path so the
        /// parent walk is reliable regardless of the subprocess cwd.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when
        /// <paramref name="repoRoot"/> is not inside a git repo / the common dir can't
        /// be resolved (fail loud — never silently fall through to a nesting path), or
        /// when the resolved root still sits under a <c>.claude/worktrees/</c> segment
        /// (defensive regression guard).</exception>
        // No CA3003 pragma needed (unlike the path-touching helpers below): this method
        // runs a git subprocess and string-manipulates its stdout — it performs no
        // filesystem path operation on caller-supplied input.
        private static async Task<string> ResolveMainCheckoutRootAsync(string repoRoot)
        {
            var (exitCode, stdout, stderr) = await GitExec.RunAsync(
                repoRoot, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git rev-parse --git-common-dir failed for '{repoRoot}' (exit {exitCode}); cannot resolve the main checkout root. stderr: {stderr.Trim()}");
            }

            string commonDir = stdout.Trim();
            if (string.IsNullOrEmpty(commonDir))
            {
                throw new InvalidOperationException(
                    $"git rev-parse --git-common-dir returned empty for '{repoRoot}'; cannot resolve the main checkout root.");
            }

            // commonDir is <mainRoot>/.git (a real dir for the main checkout; the shared
            // gitdir that every linked worktree points at). The main checkout root is its
            // parent — trim any trailing separator first so the parent walk is reliable.
            string mainRoot = Path.GetDirectoryName(
                commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(mainRoot))
            {
                throw new InvalidOperationException(
                    $"Could not derive the main checkout root from git common dir '{commonDir}' (repoRoot '{repoRoot}').");
            }

            // Defensive: --git-common-dir already guarantees the main root, but if a
            // future change ever regressed this we fail loud rather than nest. A real
            // main checkout is never itself under a .claude/worktrees/ path segment.
            // (This can theoretically false-positive on a repo absurdly checked out at a
            // path literally containing .claude/worktrees/<name>; acceptable, since the
            // guard is pure belt-and-suspenders behind the --git-common-dir resolution.)
            string sep = Path.DirectorySeparatorChar.ToString();
            string needle = sep + ".claude" + sep + "worktrees" + sep;
            string probe = mainRoot.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) + sep;
            if (probe.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    $"Resolved main checkout root '{mainRoot}' is itself under a .claude/worktrees/ path; refusing to create a nested worktree (task 25020dfa).");
            }

            return mainRoot;
        }

        /// <summary>
        /// Ensure the canonical task branch exists so helper branches can fork from
        /// it. Creates it from <b>trunk</b> (the main checkout's branch, via
        /// <see cref="ResolveTrunkAsync"/>) when absent — NOT from whatever HEAD
        /// happens to be (bab81a92 pipeline fix). Just the ref, no worktree. No-op
        /// when it already exists.
        /// </summary>
        private static async Task EnsureCanonicalBranchAsync(string repoRoot, string canonicalBranch)
        {
            if (await BranchExistsAsync(repoRoot, canonicalBranch).ConfigureAwait(false)) return;
            string trunk = await ResolveTrunkAsync(repoRoot).ConfigureAwait(false);
            var (exitCode, stdout, stderr) = await GitExec.RunAsync(
                repoRoot, "branch", canonicalBranch, trunk).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git branch {canonicalBranch} {trunk} failed (exit {exitCode}). stderr: {stderr.Trim()}; stdout: {stdout.Trim()}");
            }
        }

        /// <summary>
        /// Runs <c>git worktree add</c>, optionally creating a new branch (<c>-b</c>)
        /// and/or forking from <paramref name="startPoint"/>. When
        /// <paramref name="createNewBranch"/> is false the existing
        /// <paramref name="branchName"/> is checked out into the new worktree.
        /// </summary>
        // CA3003: worktreePath/branchName are app-generated (taskId GUID prefix +
        // sanitized agent slug) — same trust model as the rest of this class.
#pragma warning disable CA3003
        private static async Task AddWorktreeAsync(
            string repoRoot, string worktreePath, string branchName, bool createNewBranch, string startPoint)
        {
            var args = new System.Collections.Generic.List<string> { "worktree", "add", worktreePath };
            if (createNewBranch)
            {
                args.Add("-b");
                args.Add(branchName);
                if (!string.IsNullOrEmpty(startPoint)) args.Add(startPoint);
            }
            else
            {
                args.Add(branchName);
            }

            var (exitCode, stdout, stderr) = await GitExec.RunAsync(repoRoot, GitExec.SlowOpTimeoutMs, args.ToArray()).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git worktree add failed (exit {exitCode}). stderr: {stderr.Trim()}; stdout: {stdout.Trim()}");
            }
        }
#pragma warning restore CA3003

        /// <summary>
        /// Removes the worktree associated with the given task and marks the
        /// record as <c>pruned</c>. Phase 1 does NOT pass <c>--force</c>, so
        /// uncommitted changes in the worktree will block the operation —
        /// callers should commit or stash first (Phase 2 will auto-commit).
        /// Branch deletion is deferred to Phase 3 auto-merge.
        /// </summary>
        /// <returns><c>true</c> on successful removal; <c>false</c> when no
        /// active record exists for this task (already pruned or never
        /// created).</returns>
        /// <exception cref="InvalidOperationException">Thrown when
        /// <c>git worktree remove</c> exits non-zero (typically uncommitted
        /// changes). Message includes captured stderr.</exception>
        public async Task<bool> PruneForTaskAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            // Task-scoped: prunes the canonical (representative) worktree record.
            var record = _db.GetWorktreeForTask(taskId);
            if (record == null) return false;
            return await PruneRecordAsync(record, repoRoot).ConfigureAwait(false);
        }

        /// <summary>
        /// Prune a single agent's worktree on a task (one row of the composite key).
        /// Returns <c>false</c> when that agent has no active worktree record.
        /// </summary>
        public async Task<bool> PruneForTaskAsync(string taskId, string agentName, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            var record = _db.GetWorktreeForTask(taskId, agentName);
            if (record == null) return false;
            return await PruneRecordAsync(record, repoRoot).ConfigureAwait(false);
        }

        /// <summary>
        /// Prune EVERY agent worktree for a task (assignee canonical + all helpers).
        /// Used by the task-done teardown. Returns <c>true</c> if at least one active
        /// worktree was pruned. Individual prune failures propagate as exceptions.
        /// </summary>
        public async Task<bool> PruneAllForTaskAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            bool anyPruned = false;
            foreach (var record in _db.ListWorktreesForTask(taskId))
            {
                if (!string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (await PruneRecordAsync(record, repoRoot).ConfigureAwait(false))
                    anyPruned = true;
            }
            return anyPruned;
        }

        /// <summary>
        /// Core prune for one worktree record: removes the worktree from git and
        /// marks that agent's row <c>pruned</c>. Handles the already-gone-directory
        /// case and the Windows partial-prune fallback. Returns <c>false</c> when the
        /// record is already pruned.
        /// </summary>
        private async Task<bool> PruneRecordAsync(MCPServer.Models.TaskWorktree record, string repoRoot)
        {
            if (string.Equals(record.Status, "pruned", StringComparison.OrdinalIgnoreCase))
                return false;

            // If the worktree directory is already gone (manual removal, etc.),
            // still need to tell git to forget it — `git worktree prune` cleans
            // up dangling administrative records.
            // CA3003: record.WorktreePath comes from task_worktrees, written only
            // by this class; not user-supplied.
#pragma warning disable CA3003
            if (!Directory.Exists(record.WorktreePath))
#pragma warning restore CA3003
            {
                await GitExec.RunAsync(repoRoot, "worktree", "prune").ConfigureAwait(false);
                _db.MarkWorktreePruned(record.TaskId, record.AgentName);
                return true;
            }

            var (exitCode, stdout, stderr) = await GitExec.RunAsync(
                repoRoot, "worktree", "remove", record.WorktreePath).ConfigureAwait(false);

            if (exitCode != 0)
            {
                // Windows partial-prune fallback. When an agent's terminal has
                // its cwd inside the worktree, `git worktree remove` wipes
                // contents + unregisters from .git/worktrees/, but cannot
                // rmdir the directory because the OS holds an open handle
                // through the child process's cwd. Git then exits non-zero
                // even though the meaningful work is done — the only residue
                // is an empty directory shell. This is the *common* case
                // when worktree mode is on (any agent working in the spawned
                // shell is naturally cwd'd inside the worktree at task-done
                // time). Detect by re-querying `git worktree list`; if the
                // path is gone from git's view, treat as partial success.
#pragma warning disable CA3003
                if (await IsWorktreeUnregisteredAsync(repoRoot, record.WorktreePath).ConfigureAwait(false))
                {
                    try
                    {
                        if (Directory.Exists(record.WorktreePath))
                        {
                            Directory.Delete(record.WorktreePath, recursive: false);
                        }
                    }
                    catch (Exception rmdirEx) when (rmdirEx is IOException || rmdirEx is UnauthorizedAccessException)
                    {
                        Debug.WriteLine(
                            $"[WorktreeManager] Worktree '{record.WorktreePath}' unregistered from git but rmdir failed (likely held by child cwd): {rmdirEx.Message}");

                        // Loud strand signal (task 248cc2ce). git has unregistered
                        // the worktree but the OS won't let us rmdir the empty
                        // shell — almost always because a process still holds a cwd
                        // / handle inside it (commonly an Agent-tool subagent, which
                        // cannot ExitWorktree; or AV / an editor). Without this the
                        // strand is invisible until the janitor's Pass-3 sweep
                        // removes it up to ~5 min later. Surface it now, attributed
                        // to the task, so it's observable in real time. The janitor
                        // still performs the actual cleanup.
                        string strandShortId = record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length));
                        // Minimize the path disclosed in the broadcast/persisted activity
                        // content to just the dir name — the full absolute path (which
                        // leaks drive/user/repo layout) stays in the Debug.WriteLine
                        // above (security finding, task 248cc2ce).
                        string strandDirName = Path.GetFileName(
                            record.WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        try
                        {
                            _activitySink?.Invoke(
                                "worktree_strand",
                                $"Worktree dir stranded for task {strandShortId}: git unregistered it but the directory '{strandDirName}' could not be removed (likely a held cwd — e.g. a subagent — or an AV/editor handle). Left as an empty shell; the janitor will sweep it on its next pass.",
                                record.TaskId);
                        }
                        catch (Exception sinkEx)
                        {
                            // Observability must NEVER disturb the prune state machine:
                            // a throwing activity subscriber must not prevent the
                            // MarkWorktreePruned below, or a tolerated partial prune
                            // becomes a failed teardown (Adversary HIGH, task 248cc2ce).
                            Debug.WriteLine($"[WorktreeManager] strand activity sink threw (ignored): {sinkEx.Message}");
                        }
                    }
#pragma warning restore CA3003

                    _db.MarkWorktreePruned(record.TaskId, record.AgentName);
                    return true;
                }

                throw new InvalidOperationException(
                    $"git worktree remove failed (exit {exitCode}). stderr: {stderr.Trim()}; stdout: {stdout.Trim()}");
            }

            _db.MarkWorktreePruned(record.TaskId, record.AgentName);
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="worktreePath"/> is no
        /// longer listed by <c>git worktree list --porcelain</c> against
        /// <paramref name="repoRoot"/>. Used as a partial-success signal in
        /// <see cref="PruneForTaskAsync"/> on Windows when git wiped the
        /// worktree but couldn't rmdir the empty shell. Returns <c>false</c>
        /// when the list query itself fails — caller must treat that as a
        /// real failure.
        /// </summary>
        private static async Task<bool> IsWorktreeUnregisteredAsync(string repoRoot, string worktreePath)
        {
            var (exitCode, stdout, _) = await GitExec.RunAsync(
                repoRoot, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (exitCode != 0) return false;

            string canonicalTarget = NormalizePath(worktreePath);
            using var reader = new StringReader(stdout ?? string.Empty);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
                string p = line.Substring("worktree ".Length).Trim();
                if (string.Equals(NormalizePath(p), canonicalTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException)
            {
                return path.Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Returns the absolute path to the active worktree for this task, or
        /// <c>null</c> if no active record exists. Read-only DB lookup;
        /// performs no filesystem check.
        /// </summary>
        public string GetWorktreePathForTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            var record = _db.GetWorktreeForTask(taskId);
            if (record == null) return null;
            return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                ? record.WorktreePath
                : null;
        }

        /// <summary>
        /// Returns the absolute path to a specific agent's active worktree on the
        /// task, or <c>null</c> if that agent has no active worktree. Agent-aware
        /// counterpart of <see cref="GetWorktreePathForTask(string)"/>.
        /// </summary>
        public string GetWorktreePathForTask(string taskId, string agentName)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            var record = _db.GetWorktreeForTask(taskId, agentName);
            if (record == null) return null;
            return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                ? record.WorktreePath
                : null;
        }

        /// <summary>
        /// Repo-root-relative paths (forward-slash) of every uncommitted change —
        /// tracked, staged, or untracked — in <paramref name="repoRoot"/>'s main
        /// checkout, via <c>git status --porcelain</c>. Used by the backfill
        /// migration guard (task 4bcd1e24, item [2]) to detect repo-root work that a
        /// fresh worktree would split off.
        ///
        /// <para>Three-state contract so the guard can fail CLOSED on uncertainty
        /// (pipeline Run-1 hardening — Codex security/adversary HIGH): an
        /// <b>empty list</b> means the tree is verified clean; a <b>non-empty list</b>
        /// is the verified dirty set; <b>null</b> means the state could NOT be
        /// determined (missing repoRoot, non-zero git exit, or an exception). The
        /// caller must treat null as "indeterminate → refuse to backfill", NOT as
        /// "clean" — a guard that fails open defeats its own purpose.</para>
        /// </summary>
        public async Task<List<string>> GetDirtyRepoRelativePathsAsync(string repoRoot)
        {
            if (string.IsNullOrEmpty(repoRoot)) return null;
            try
            {
                var (exitCode, stdout, _) = await GitExec.RunAsync(repoRoot, "status", "--porcelain").ConfigureAwait(false);
                if (exitCode != 0) return null;
                return WorktreeMergeService.ParsePorcelainPaths(stdout);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WorktreeManager] GetDirtyRepoRelativePathsAsync('{repoRoot}') failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Recursively copy gitignored-but-build-required artifacts from the
        /// main checkout into a fresh worktree. Phase 1 hardcodes a small
        /// allowlist (just "tools/" today); Phase 2 should replace this with a
        /// project-defined hook so each repo can declare its own seed list.
        /// Failures are surfaced as exceptions — a build that would silently
        /// fail later is worse than a loud failure here.
        /// </summary>
        // CA3003: repoRoot is a project-registered Project.Path field and worktreePath
        // is computed from it + an app-generated taskId GUID prefix. The seed-dir
        // names are a hardcoded literal allowlist. Path.Combine outputs are not
        // user-supplied path text. Same trust model as CreateForTaskAsync above.
#pragma warning disable CA3003
        private static void SeedVendoredArtifacts(string repoRoot, string worktreePath)
        {
            string[] seedDirs = { "tools" };
            foreach (string dir in seedDirs)
            {
                string source = Path.Combine(repoRoot, dir);
                if (!Directory.Exists(source)) continue;
                string dest = Path.Combine(worktreePath, dir);
                CopyDirectoryRecursive(source, dest);
            }
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
            }
            foreach (string sub in Directory.GetDirectories(source))
            {
                string subDest = Path.Combine(dest, Path.GetFileName(sub));
                CopyDirectoryRecursive(sub, subDest);
            }
        }
#pragma warning restore CA3003

    }
}
