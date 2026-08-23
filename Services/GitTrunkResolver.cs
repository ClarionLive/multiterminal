using System;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// THE single trunk resolution for a repository, shared by the auto-merge path and
    /// the janitor's read-only pending-merge scan (task 0d7c3446).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is shared rather than copied.</b> Two resolvers already lived on
    /// <see cref="WorktreeMergeService"/>, and their comments record the traps each learned
    /// the hard way (tasks 90c2acc6, b88e7017, 36b0b9d5). A third private copy in the
    /// janitor is exactly the drift those comments were written to prevent, so the hardened
    /// one moved here and both callers reach it.</para>
    /// <para><b>Do NOT use the other resolver for a background scan.</b>
    /// <c>WorktreeMergeService.ResolveTrunkAsync</c> resolves trunk as whatever the main
    /// checkout has CHECKED OUT RIGHT NOW. Correct at merge time; wrong for a scan, which
    /// runs while the checkout may be parked on any branch — including a task branch.</para>
    /// <para><b>Fails closed, and callers must keep it that way.</b> Every path returns
    /// <c>null</c> rather than guessing, and a caller must read null as "could not look",
    /// never as a verdict. In the janitor an unresolved trunk counts the record as SKIPPED
    /// and degrades the scan to partial. Letting null mean "merged" would convert the noise
    /// bug this ticket fixes into a silence bug, which is strictly worse: the genuinely
    /// stranded branch stops being reported at all, with its worktree already pruned.</para>
    /// </remarks>
    internal static class GitTrunkResolver
    {
        /// <summary>
        /// Full resolution in the order the merge path uses it: the project's configured
        /// <c>git_default_branch</c> is authoritative, detection is the fallback. Returns
        /// <c>null</c> when neither yields an unambiguous answer.
        /// </summary>
        /// <remarks>
        /// <para>The configured value is checked FIRST and is not second-guessed. It is the
        /// documented remedy for a repo whose trunk carries an unconventional name, which
        /// <see cref="DetectDefaultBranchAsync"/> deliberately refuses to guess at.</para>
        /// <para><b>This ordering is the reason the helper exists</b> (task 0d7c3446). The
        /// ticket's plan described <c>DetectDefaultBranchAsync</c> as carrying the configured
        /// override; it never did — the override lived at the merge path's call site. A
        /// janitor wired to detection alone would resolve nothing on precisely the repos the
        /// configuration exists to serve (unconventional trunk name, or more than one
        /// non-task branch), skip every record, and report a permanently partial scan with no
        /// findings and no obvious symptom.</para>
        /// <para><see cref="WorktreeMergeService"/> still branches on the configured value
        /// itself, because its refusal text has to say WHERE the trunk came from — telling an
        /// operator their configuration is stale when they never configured anything is both
        /// false and misleading (task b88e7017).</para>
        /// </remarks>
        internal static async Task<string> ResolveAsync(string repoRoot, string configuredTrunk)
        {
            if (!string.IsNullOrWhiteSpace(configuredTrunk)) return configuredTrunk.Trim();
            return await DetectDefaultBranchAsync(repoRoot).ConfigureAwait(false);
        }

        /// <summary>
        /// Best-effort resolution of the repository's default branch, used to guard
        /// against merging a task branch into a non-trunk branch the main checkout
        /// happens to be parked on (task 90c2acc6, Suspect B). Returns <c>null</c>
        /// when the default cannot be determined UNAMBIGUOUSLY — callers treat null
        /// as "skip the assertion" so a legitimate merge is never blocked by a guess.
        /// </summary>
        internal static async Task<string> DetectDefaultBranchAsync(string repoRoot)
        {
            // (2) Remote's published default (origin/HEAD -> origin/<branch>).
            // DefaultTimeoutMs, not the short probe budget: this is the fail-CLOSED
            // choosing path, and giving up early here falls through to the
            // sole-local-branch heuristic and can let a merge proceed.
            string published = await ResolveOriginHeadAsync(repoRoot, GitExec.DefaultTimeoutMs).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(published)) return published;

            // (3) The SOLE non-task local branch, if exactly one exists AND it bears a
            // conventional trunk name. Generalizing past hard-coded main/master to
            // custom trunk names was pipeline run 2 (Codex adversary HIGH: 'develop'
            // repos were over-blocked). But accepting an ARBITRARY lone branch was
            // itself unsafe — pipeline run 3 (Codex adversary HIGH): a lone
            // 'feature/parked' would be promoted to trunk and the task branch merged
            // into it, reopening the silent wrong-branch class. So we only trust the
            // lone branch when its name is a recognized default-branch convention.
            // Anything else (a lone 'feature/*', 'release', 'stable', ...) gives no
            // trustworthy signal: return null and let the caller fail closed. A repo
            // with MORE than one non-task branch is likewise ambiguous → null. Repos
            // with a genuinely unconventional trunk name must set the project's
            // git_default_branch (the authoritative source). Task branches
            // (task/<id>[--<slug>]) are excluded — they're never trunk.
            // %(refname), NOT %(refname:short) — task b88e7017, same defect class as
            // 36b0b9d5 item ③ fixed in WorktreeJanitorService. :short emits
            // 'heads/develop' rather than 'develop' when a TAG of the same name exists,
            // so a repo carrying tag 'develop' alongside branch 'develop' would fail
            // IsConventionalTrunkName, resolve no trunk, and fail closed forever — the
            // same permanent-refusal shape this ticket is about. %(refname) is
            // unambiguous by construction; ParseBranchNames strips the refs/heads/
            // prefix and is shared rather than re-implemented so the two callers
            // cannot drift.
            var branches = await GitExec.RunAsync(repoRoot, "for-each-ref", "--format=%(refname)", "refs/heads/").ConfigureAwait(false);
            if (branches.ExitCode == 0)
            {
                string sole = null;
                int count = 0;
                foreach (var b in WorktreeJanitorService.ParseBranchNames(branches.Stdout))
                {
                    if (b.StartsWith("task/", StringComparison.Ordinal)) continue;
                    count++;
                    sole = b;
                    if (count > 1) break;
                }
                if (count == 1 && IsConventionalTrunkName(sole)) return sole;
            }

            return null;
        }

        /// <summary>
        /// The remote's published default branch (origin/HEAD -&gt; origin/&lt;branch&gt;),
        /// or <c>null</c> when it can't be resolved. Single source for both
        /// <see cref="DetectDefaultBranchAsync"/> (which uses it to CHOOSE a trunk)
        /// and <see cref="ClassifyTrunkMismatchAsync"/> (which uses it to decide
        /// whether the configured trunk is stale). They were separate verbatim
        /// copies; if one had gained a fallback, the classifier's verdict could
        /// contradict the resolver's choice about the same repository.
        /// </summary>
        /// <param name="timeoutMs">
        /// REQUIRED, and deliberately not defaulted (task b88e7017, pipeline Run 2
        /// debugger — a FAIL-OPEN regression this extraction introduced). The two
        /// callers must not share a budget:
        /// <list type="bullet">
        /// <item><b>Choosing</b> a trunk is a fail-CLOSED decision. If this probe
        /// gives up early, DetectDefaultBranchAsync falls through to the
        /// sole-local-branch heuristic, which can resolve a trunk origin/HEAD would
        /// have contradicted — and then the merge PROCEEDS. Sharing the short probe
        /// budget silently cut that path from 30s to 5s, turning a timeout into a
        /// merge instead of a refusal. It passes GitExec.DefaultTimeoutMs.</item>
        /// <item><b>Reporting</b> a refusal is already fail-closed — the merge is
        /// refused either way, and the outcome has to reach the caller inside the
        /// MCP client's 15s budget. It passes the short ProbeTimeoutMs.</item>
        /// </list>
        /// </param>
        internal static async Task<string> ResolveOriginHeadAsync(string repoRoot, int timeoutMs)
        {
            var remote = await GitExec.RunAsync(
                repoRoot, timeoutMs, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").ConfigureAwait(false);
            if (remote.TimedOut || remote.ExitCode != 0) return null;

            string r = remote.Stdout.Trim();
            const string prefix = "origin/";
            if (r.StartsWith(prefix, StringComparison.Ordinal)) r = r.Substring(prefix.Length);
            return string.IsNullOrEmpty(r) ? null : r;
        }

        /// <summary>
        /// Recognized default-branch naming conventions. A lone local branch is only
        /// trusted as the trunk when it matches one of these — an arbitrary branch
        /// name carries no signal that it is actually the repository default (task
        /// 90c2acc6, pipeline run 3). Repos whose trunk is named otherwise must set
        /// the project's git_default_branch.
        /// </summary>
        private static readonly string[] ConventionalTrunkNames = { "main", "master", "develop", "trunk" };

        internal static bool IsConventionalTrunkName(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch)) return false;
            foreach (var name in ConventionalTrunkNames)
            {
                if (string.Equals(branch, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
