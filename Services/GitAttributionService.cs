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

            // Cache verdicts per task to avoid duplicate SQL when many files
            // belong to the same task.
            var verdictCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                    if (!string.IsNullOrEmpty(link.TaskId))
                    {
                        if (!verdictCache.TryGetValue(link.TaskId, out var verdict))
                        {
                            try { verdict = _taskDb.GetLatestVerdictForTask(link.TaskId); }
                            catch { verdict = null; }
                            verdictCache[link.TaskId] = verdict;
                        }
                        attr.PipelineStatus = verdict;
                    }
                }
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
