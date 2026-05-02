using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One uncommitted file's attribution overlay data — feeds the Phase 2
    /// chips and cross-task contamination banner in the HUD Git tab.
    /// All fields may be <c>null</c> / empty when no attribution data is
    /// available; consumers must handle the empty case gracefully (chips
    /// collapse via CSS <c>:empty</c>).
    /// </summary>
    public sealed class GitFileAttribution
    {
        /// <summary>Repo-relative path (forward-slash, LibGit2Sharp convention).</summary>
        public string FilePath { get; set; }

        /// <summary>Agent name from <c>tasks.assignee</c> for the active task that owns this file (file-level granularity).</summary>
        public string Agent { get; set; }

        /// <summary>Owning active task id, or <c>null</c> if no active task claims this file.</summary>
        public string TaskId { get; set; }

        /// <summary>Owning active task title.</summary>
        public string TaskTitle { get; set; }

        /// <summary>Latest verdict string from <c>task_reports</c> for the owning task (e.g. "PASS", "LGTM", "BLOCK").</summary>
        public string PipelineStatus { get; set; }

        /// <summary>
        /// Indicates which lookup tier produced this attribution:
        /// <c>"active"</c> = matched an in-progress task,
        /// <c>"shipped"</c> = no active claim but matched a done task (file is uncommitted but the work has shipped),
        /// <c>"none"</c> = no task linkage at all.
        /// Renderer uses this to pick the chip styling variant. Contamination
        /// banner ignores this — it only counts <c>"active"</c> rows via the
        /// separate <c>GetCrossTaskActiveTaskIds</c> query.
        /// </summary>
        public string LinkageState { get; set; } = "none";
    }

    /// <summary>
    /// Computes Phase 2 overlays for the HUD Git tab — per-file attribution
    /// (agent + active-task linkage + pipeline status) and cross-task
    /// contamination detection (uncommitted files spanning &gt;1 active task).
    ///
    /// <para>Backed by <see cref="TaskDatabase"/>'s active-task linkage and
    /// task-reports queries. Activity_feed integration deferred to a follow-up:
    /// <c>task_file_links</c> covers the bulk of attribution since agents call
    /// <c>link_task_file</c> while working, and the JSON-LIKE matching of
    /// <c>activity_feed.details_json</c> against Windows backslash-escaped paths
    /// is brittle enough to defer.</para>
    /// </summary>
    public sealed class GitAttributionService
    {
        private readonly TaskDatabase _taskDb;

        public GitAttributionService(TaskDatabase taskDb)
        {
            _taskDb = taskDb ?? throw new ArgumentNullException(nameof(taskDb));
        }

        /// <summary>
        /// Returns one attribution per input file. Looks up the active task
        /// linkage for each file (one batched SQL query) then resolves the
        /// pipeline verdict per distinct task (one query per task — typical
        /// uncommitted set spans 1-3 tasks).
        ///
        /// <para>Files with no active-task linkage get a result entry with
        /// only <see cref="GitFileAttribution.FilePath"/> populated — chips
        /// stay empty.</para>
        /// </summary>
        public IReadOnlyList<GitFileAttribution> GetAttributionForFiles(
            string projectRoot,
            IReadOnlyList<string> repoRelativePaths)
        {
            if (repoRelativePaths == null || repoRelativePaths.Count == 0)
                return Array.Empty<GitFileAttribution>();

            string normalizedRoot;
            try { normalizedRoot = Path.GetFullPath(projectRoot); }
            catch { normalizedRoot = projectRoot; }
            if (!string.IsNullOrEmpty(normalizedRoot))
                normalizedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Build a parallel list of absolute Windows paths for SQL matching.
            // task_file_links.file_path is stored as the absolute path passed
            // by link_task_file callers (typically Windows backslash form on
            // this platform).
            var absolutePaths = new List<string>(repoRelativePaths.Count);
            foreach (var rel in repoRelativePaths)
            {
                if (string.IsNullOrEmpty(rel))
                {
                    absolutePaths.Add(string.Empty);
                    continue;
                }
                string fwd = rel.Replace('\\', '/');
                string abs = string.IsNullOrEmpty(normalizedRoot)
                    ? fwd
                    : Path.GetFullPath(Path.Combine(normalizedRoot, fwd));
                absolutePaths.Add(abs);
            }

            Dictionary<string, (string TaskId, string Title, string Assignee)> linkage;
            try
            {
                linkage = _taskDb.GetActiveTaskLinkageForFiles(absolutePaths);
            }
            catch
            {
                // SQL failure — degrade to empty attribution rather than throw.
                linkage = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            }

            // Fallback layer: for any file with no active claim, look up its
            // most recent done-task linkage. This surfaces "shipped but
            // uncommitted" work in the Git tab — files whose owning task
            // moved testing→done minutes ago and haven't committed yet.
            // Only queries the residual file set (paths not already mapped
            // by the active-linkage pass) to keep the SQL footprint minimal.
            // Contamination logic does NOT use this — see GetCrossTaskActiveTaskIds.
            Dictionary<string, (string TaskId, string Title, string Assignee)> shippedLinkage =
                new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            var residual = new List<string>();
            foreach (var abs in absolutePaths)
            {
                if (string.IsNullOrEmpty(abs)) continue;
                if (!linkage.ContainsKey(abs)) residual.Add(abs);
            }
            if (residual.Count > 0)
            {
                try
                {
                    shippedLinkage = _taskDb.GetCompletedTaskLinkageForFiles(residual);
                }
                catch (Exception ex)
                {
                    // Bare catch turned a missing-column crash into an
                    // invisible no-op during cycle 2 development (adversary
                    // MEDIUM Run 2). Use Trace.WriteLine, NOT Debug.WriteLine
                    // — the latter is [Conditional("DEBUG")] and gets stripped
                    // from Release builds (which the installer ships per
                    // installer/build-installer.ps1). Trace.WriteLine is gated
                    // on TRACE which is defined in both Debug and Release by
                    // SDK default, so the diagnostic survives production.
                    // Existing Trace precedent: Services/RipgrepService.cs and
                    // Services/SessionIndexingService.cs. Adversary MEDIUM Run 3.
                    System.Diagnostics.Trace.WriteLine(
                        "GitAttributionService.GetCompletedTaskLinkageForFiles failed: " + ex.Message);
                    shippedLinkage = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
                }
            }

            // Cache verdicts per task to avoid duplicate SQL when many files
            // belong to the same task. Split per tier: GetLatestVerdictForTask
            // takes a requiredStatus gate ("in_progress" by default vs "done"
            // for shipped) and a single shared cache keyed only by taskId
            // would let an active-tier null mask a real done-tier hit when
            // the same task moves in_progress→done between the two linkage
            // queries (TOCTOU window — debugger LOW Run 3). Two dicts cost a
            // handful of bytes and remove the wrong-bucket cache return.
            var activeVerdictCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shippedVerdictCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var result = new List<GitFileAttribution>(repoRelativePaths.Count);
            for (int i = 0; i < repoRelativePaths.Count; i++)
            {
                string rel = repoRelativePaths[i];
                string abs = absolutePaths[i];
                var attr = new GitFileAttribution { FilePath = rel };

                if (linkage.TryGetValue(abs, out var link))
                {
                    attr.TaskId = link.TaskId;
                    attr.TaskTitle = link.Title;
                    attr.Agent = link.Assignee;
                    attr.LinkageState = "active";

                    if (!string.IsNullOrEmpty(link.TaskId))
                    {
                        if (!activeVerdictCache.TryGetValue(link.TaskId, out var verdict))
                        {
                            try { verdict = _taskDb.GetLatestVerdictForTask(link.TaskId); }
                            catch { verdict = null; }
                            activeVerdictCache[link.TaskId] = verdict;
                        }
                        attr.PipelineStatus = verdict;
                    }
                }
                else if (shippedLinkage.TryGetValue(abs, out var shipped))
                {
                    attr.TaskId = shipped.TaskId;
                    attr.TaskTitle = shipped.Title;
                    attr.Agent = shipped.Assignee;
                    attr.LinkageState = "shipped";

                    if (!string.IsNullOrEmpty(shipped.TaskId))
                    {
                        if (!shippedVerdictCache.TryGetValue(shipped.TaskId, out var verdict))
                        {
                            // Pass requiredStatus="done" — GetLatestVerdictForTask
                            // gates verdict freshness on the task's current status,
                            // and shipped-tier ids by definition have status='done'.
                            // Defaulting would silently null every shipped verdict
                            // (adversary CRITICAL Run 1).
                            try { verdict = _taskDb.GetLatestVerdictForTask(shipped.TaskId, "done"); }
                            catch { verdict = null; }
                            shippedVerdictCache[shipped.TaskId] = verdict;
                        }
                        attr.PipelineStatus = verdict;
                    }
                }
                // else: no linkage, LinkageState stays "none" from the default initializer.
                result.Add(attr);
            }
            return result;
        }

        /// <summary>
        /// Returns the distinct active task IDs that claim ANY of the given
        /// files (multi-claim aware — a file shared by tasks A AND B counts
        /// both, unlike <see cref="GetAttributionForFiles"/> which dedupes to
        /// a single primary claim per file for chip rendering).
        ///
        /// <para>This drives the cross-task contamination banner. Without the
        /// multi-claim awareness the banner under-counts when the same file is
        /// linked to two active tasks — exactly the contamination case it's
        /// supposed to flag (adversary finding from item [11] cleanup pass).</para>
        /// </summary>
        public IReadOnlyList<string> GetCrossTaskActiveTaskIds(
            string projectRoot,
            IReadOnlyList<string> repoRelativePaths)
        {
            if (repoRelativePaths == null || repoRelativePaths.Count == 0)
                return Array.Empty<string>();

            string normalizedRoot;
            try { normalizedRoot = Path.GetFullPath(projectRoot); }
            catch { normalizedRoot = projectRoot; }
            if (!string.IsNullOrEmpty(normalizedRoot))
                normalizedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var absolutePaths = new List<string>(repoRelativePaths.Count);
            foreach (var rel in repoRelativePaths)
            {
                if (string.IsNullOrEmpty(rel)) { absolutePaths.Add(string.Empty); continue; }
                string fwd = rel.Replace('\\', '/');
                string abs = string.IsNullOrEmpty(normalizedRoot)
                    ? fwd
                    : Path.GetFullPath(Path.Combine(normalizedRoot, fwd));
                absolutePaths.Add(abs);
            }

            try
            {
                return _taskDb.GetDistinctActiveTaskIdsForFiles(absolutePaths);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
