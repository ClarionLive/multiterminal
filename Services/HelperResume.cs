using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The outcome of checking one helper before it is resumed with its PM (task 7d20e94c item 4):
    /// either the candidate and the validated folder to resume it from, or why it is skipped.
    /// </summary>
    public sealed class HelperResumeCheck
    {
        private HelperResumeCheck(LaunchSessionCandidate candidate, string cwd, string skipReason)
        {
            Candidate = candidate;
            Cwd = cwd;
            SkipReason = skipReason;
        }

        public LaunchSessionCandidate Candidate { get; }

        /// <summary>The validated, existing folder the session ran in; null when skipped.</summary>
        public string Cwd { get; }

        /// <summary>Why the helper is not resumed, as the tail of a sentence; null when it may be.</summary>
        public string SkipReason { get; }

        public bool CanResume => SkipReason == null;

        internal static HelperResumeCheck Skip(string reason) => new HelperResumeCheck(null, null, reason);

        internal static HelperResumeCheck Ok(LaunchSessionCandidate candidate, string cwd) => new HelperResumeCheck(candidate, cwd, null);
    }

    /// <summary>
    /// The pure decisions of resuming a PM's helpers (task 7d20e94c items 4 and 6): whether one helper
    /// may be resumed, and the one summary the Owner sees afterwards. MainForm supplies the live set,
    /// the catalog entry and the filesystem probes; nothing here touches a pane or the disk.
    /// </summary>
    public static class HelperResume
    {
        /// <summary>
        /// Whether a pane created for <paramref name="requestedName"/> came up as that helper:
        /// <paramref name="resolvedName"/> is the name or a suffixed form of it (a held name),
        /// ordinal-ignore-case. Anything else is a pool placeholder, which is not the helper and is not
        /// made closable by its spawner. The one rule for the spawn path and the helper resume
        /// (pipeline Run 1, task 7d20e94c).
        /// </summary>
        public static bool IsHelperName(string requestedName, string resolvedName)
            => !string.IsNullOrEmpty(requestedName)
               && !string.IsNullOrEmpty(resolvedName)
               && resolvedName.StartsWith(requestedName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="helper"/> may be resumed now, applying the same rules as the PM's own
        /// resume, in this order: it has a recorded name; it is a Claude session (only Claude has
        /// <c>--resume</c>); the catalog found it (<paramref name="entry"/> null means
        /// <see cref="SessionCatalog.TryGetEntry"/> refused it: live, transcript gone, worktree pruned or
        /// cwd invalid); the model built from <paramref name="livePanes"/> and <paramref name="prior"/>
        /// still offers it and does not flag it "may be open elsewhere" (there is no dialog to confirm
        /// with, so a flagged helper is skipped). <paramref name="prior"/> is the launch record's word on
        /// its last process: <see cref="PriorLaunchState.DiedWithPreviousRun"/> waives the recent-write
        /// flag (that process died with the last MT, which is the restart this feature exists for), and so
        /// does <see cref="PriorLaunchState.ClosedWithPm"/> (item 8: MT closed it with its PM, maybe
        /// seconds ago), and
        /// <see cref="PriorLaunchState.OpenInCurrentRun"/> skips it as already open, and
        /// <see cref="PriorLaunchState.OpenElsewhere"/> (a previous run left it open and that MT is not
        /// provably gone) skips it as "may still be open elsewhere"; <see cref="LaunchSessionChoiceModel.RecheckResume"/> passes; and its cwd validates
        /// against the project root and still exists.
        /// </summary>
        public static HelperResumeCheck Check(
            OpenHelper helper,
            SessionCatalogEntry entry,
            IEnumerable<PaneSessionState> livePanes,
            string projectRoot,
            Func<string, DateTime?> transcriptLastWriteUtc,
            Func<string, bool> directoryExists,
            PriorLaunchState prior = PriorLaunchState.NoRecord)
        {
            if (helper == null) return HelperResumeCheck.Skip("nothing was recorded for it");
            if (string.IsNullOrWhiteSpace(helper.TerminalName)) return HelperResumeCheck.Skip("it has no recorded name");
            if (!string.IsNullOrEmpty(helper.Kind) && !string.Equals(helper.Kind, "claude", StringComparison.OrdinalIgnoreCase))
                return HelperResumeCheck.Skip("it is not a Claude session, and only Claude sessions can be resumed");
            if (entry == null)
                return HelperResumeCheck.Skip("its session cannot be resumed: it is already open, or its folder or transcript is gone");

            var panes = (livePanes ?? Enumerable.Empty<PaneSessionState>()).ToList();
            var model = new LaunchSessionChoiceModel(new[] { entry }, panes, _ => prior);
            var candidate = model.Candidates.FirstOrDefault();
            if (candidate == null || prior == PriorLaunchState.OpenInCurrentRun)
                return HelperResumeCheck.Skip("its session is already open in another pane");

            // Fail-closed (pipeline Run 1): the record says a previous MT run left it open and that MT
            // cannot be shown to have exited. With no dialog to confirm in, that is a skip whatever the
            // transcript's age.
            if (prior == PriorLaunchState.OpenElsewhere)
                return HelperResumeCheck.Skip("its session may still be open elsewhere (the MultiTerminal that last ran it cannot be shown to have exited)");
            if (candidate.MayBeOpenElsewhere)
                return HelperResumeCheck.Skip($"its session may still be open elsewhere ({candidate.MayBeOpenReason.TrimEnd('.')})");

            string recheck = LaunchSessionChoiceModel.RecheckResume(candidate, panes, transcriptLastWriteUtc?.Invoke(entry.TranscriptPath));
            if (recheck != null)
                return HelperResumeCheck.Skip(recheck.TrimEnd('.'));

            string cwd = SessionCatalog.ValidateResumeCwd(projectRoot, entry.Cwd);
            if (cwd == null)
                return HelperResumeCheck.Skip("it ran in a folder that is not this project's folder or one of its worktrees");
            if (directoryExists == null || !directoryExists(cwd))
                return HelperResumeCheck.Skip($"the folder it ran in no longer exists ({cwd})");

            return HelperResumeCheck.Ok(candidate, cwd);
        }
    }

    /// <summary>What resuming a PM's helpers did, for the one summary (task 7d20e94c item 6).</summary>
    public sealed class HelperResumeReport
    {
        private readonly List<string> _resumed = new List<string>();
        private readonly List<(string Name, string Reason)> _skipped = new List<(string, string)>();
        private readonly List<(string Requested, string Actual)> _renamed = new List<(string, string)>();

        public IReadOnlyList<string> Resumed => _resumed;

        public IReadOnlyList<(string Name, string Reason)> Skipped => _skipped;

        public IReadOnlyList<(string Requested, string Actual)> Renamed => _renamed;

        public void AddResumed(string name) => _resumed.Add(name ?? "(unnamed)");

        public void AddSkipped(string name, string reason) => _skipped.Add((string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name, reason ?? "unknown reason"));

        /// <summary>Records a helper that came back under another name than it had (a held name is suffixed).</summary>
        public void AddRenamed(string requested, string actual) => _renamed.Add((requested, actual));

        /// <summary>Only a skip or a rename is worth interrupting the Owner for.</summary>
        public bool HasAnythingToReport => _skipped.Count > 0 || _renamed.Count > 0;

        /// <summary>The one message box text, or null when every helper came back as it was.</summary>
        public string BuildSummary(string pmName)
        {
            if (!HasAnythingToReport) return null;
            int total = _resumed.Count + _skipped.Count;
            var sb = new StringBuilder();
            sb.Append($"Resumed {_resumed.Count} of {total} helper{(total == 1 ? string.Empty : "s")} of {pmName}.");
            if (_skipped.Count > 0)
            {
                sb.Append("\n\nNot resumed:");
                foreach (var (name, reason) in _skipped)
                    sb.Append($"\n- {name}: {reason}.");
            }

            if (_renamed.Count > 0)
            {
                sb.Append($"\n\nCame back under another name (the name was held). {pmName}'s conversation still uses the old one:");
                foreach (var (requested, actual) in _renamed)
                    sb.Append($"\n- {requested} came back as {actual}.");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// The question MT asks when the Owner closes a PM pane whose helper panes are still open (task
    /// 7d20e94c item 8). Pure, so its wording is tested; MainForm decides when to ask and acts on it.
    /// </summary>
    public static class HelperCloseWithPm
    {
        /// <summary>
        /// "Also close Alice's 2 helpers (TestA, TestB)? ...", with what each button does. Blank names
        /// read "(unnamed)", so the question never lists an empty name.
        /// </summary>
        public static string Question(string pmName, IReadOnlyList<string> helperNames)
        {
            string pm = string.IsNullOrWhiteSpace(pmName) ? "this terminal" : pmName.Trim();
            var names = (helperNames ?? Array.Empty<string>())
                .Select(n => string.IsNullOrWhiteSpace(n) ? "(unnamed)" : n.Trim())
                .ToList();
            string helpers = names.Count == 1
                ? $"helper {names[0]}"
                : $"{names.Count} helpers ({string.Join(", ", names)})";
            string them = names.Count == 1 ? "It" : "They";
            return $"Also close {pm}'s {helpers}? {them}'ll come back when you resume {pm}.\n\n" +
                   $"Yes: close {(names.Count == 1 ? "it" : "them")} too.\n" +
                   $"No: close only {pm}; {(names.Count == 1 ? "the helper keeps" : "the helpers keep")} running.\n" +
                   $"Cancel: keep {pm} open.";
        }
    }

    /// <summary>
    /// The order and the skips of a batch close, the tab menu's Close All Tabs and Close all but this
    /// (task 7d20e94c item 8 review fix). Pure: MainForm closes each pane <see cref="Next"/> returns and
    /// reports what happened.
    /// <list type="bullet">
    /// <item>A pane comes after every PM in the batch that lists it as an open helper, so the PM's
    /// "also close helpers?" is asked before the helper is closed as an ordinary <c>pane_closed</c>
    /// (which would make it unresumable). Otherwise tab order is kept; a cycle (not possible with one
    /// level of helpers) falls back to tab order.</item>
    /// <item>A pane whose close was cancelled (Cancel on the question, or any other veto) STOPS the
    /// batch: nothing after it is closed, its helpers included.</item>
    /// <item>Helpers closed with their PM (Yes) are not closed again.</item>
    /// <item>"No" removes that PM's helpers from the batch: the Owner said not to close them, so they
    /// keep running even though "Close All" named them.</item>
    /// </list>
    /// </summary>
    public sealed class PaneBatchClose
    {
        private readonly List<string> _order;
        private readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);
        private int _index;

        /// <param name="batchInTabOrder">DocIds of the panes to close, in tab order; blanks and duplicates are dropped.</param>
        /// <param name="openHelpersOf">A pane's open helper DocIds (<c>PaneLaunchTracker.GetOpenHelperDocIds</c>); null means none.</param>
        public PaneBatchClose(IEnumerable<string> batchInTabOrder, Func<string, IEnumerable<string>> openHelpersOf)
        {
            var batch = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in batchInTabOrder ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                    batch.Add(id);
            }

            // pmsOf[h] = the panes IN THE BATCH that list h as an open helper.
            var pmsOf = batch.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var pm in batch)
            {
                foreach (var helper in openHelpersOf?.Invoke(pm) ?? Enumerable.Empty<string>())
                {
                    if (helper != null && !string.Equals(helper, pm, StringComparison.Ordinal) && pmsOf.TryGetValue(helper, out var set))
                        set.Add(pm);
                }
            }

            // Stable: each step takes the first remaining pane (in tab order) whose PMs are all placed.
            _order = new List<string>(batch.Count);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var remaining = new List<string>(batch);
            while (remaining.Count > 0)
            {
                int pick = remaining.FindIndex(id => pmsOf[id].All(placed.Contains));
                if (pick < 0)
                    pick = 0;
                _order.Add(remaining[pick]);
                placed.Add(remaining[pick]);
                remaining.RemoveAt(pick);
            }
        }

        /// <summary>The planned close order.</summary>
        public IReadOnlyList<string> Order => _order;

        /// <summary>True once a close was cancelled; <see cref="Next"/> then returns null.</summary>
        public bool Stopped { get; private set; }

        /// <summary>The next pane to close, or null when the batch is finished or stopped.</summary>
        public string Next()
        {
            while (!Stopped && _index < _order.Count)
            {
                var id = _order[_index++];
                if (_done.Add(id))
                    return id;
            }

            return null;
        }

        /// <summary>The last pane's close was cancelled: stop the batch.</summary>
        public void ReportCancelled() => Stopped = true;

        /// <summary>
        /// The last pane closed, and these helpers were either closed with it (Yes) or are to keep
        /// running (No). Either way none of them is closed by the rest of the batch.
        /// </summary>
        public void ReportHelpersHandled(IEnumerable<string> helperDocIds)
        {
            foreach (var id in helperDocIds ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(id))
                    _done.Add(id);
            }
        }
    }
}
