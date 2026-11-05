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
        /// against the project root (or the entry's repo-root worktree base, ticket 3fd3c9a7) and still exists.
        /// <paramref name="catalogMissReason"/> is <see cref="SessionCatalog.LastMissReason"/>, shown when the catalog refused it.
        /// </summary>
        public static HelperResumeCheck Check(
            OpenHelper helper,
            SessionCatalogEntry entry,
            IEnumerable<PaneSessionState> livePanes,
            string projectRoot,
            Func<string, DateTime?> transcriptLastWriteUtc,
            Func<string, bool> directoryExists,
            PriorLaunchState prior = PriorLaunchState.NoRecord,
            string catalogMissReason = null)
        {
            if (helper == null) return HelperResumeCheck.Skip("nothing was recorded for it");
            if (string.IsNullOrWhiteSpace(helper.TerminalName)) return HelperResumeCheck.Skip("it has no recorded name");
            if (!string.IsNullOrEmpty(helper.Kind) && !string.Equals(helper.Kind, "claude", StringComparison.OrdinalIgnoreCase))
                return HelperResumeCheck.Skip("it is not a Claude session, and only Claude sessions can be resumed");
            if (entry == null)
                return HelperResumeCheck.Skip("its session cannot be resumed: " + (catalogMissReason ?? "the session catalog did not find it"));

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

            string cwd = SessionCatalog.ValidateResumeCwd(projectRoot, entry.Cwd, entry.WorktreeBase);
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
    /// <item>The questions are asked before anything closes (<see cref="PaneBatchCloseQuestions"/>,
    /// GH #74), so a Cancel there closes nothing. A pane whose close is still vetoed once the closing
    /// has started (anything other than the question) STOPS the batch: nothing after it is closed, its
    /// helpers included.</item>
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

    /// <summary>
    /// Asks every "also close its helpers?" question of a batch close BEFORE anything closes (GH #74,
    /// phase 3). Closing a floating window, Close All Tabs and Close all but this all close several
    /// panes; asked one pane at a time, a Cancel on the second PM came after the first was already
    /// closed, leaving the batch half done. Asked up front, a Cancel closes nothing. Pure: the caller
    /// shows the questions and closes the panes, reusing these answers.
    ///
    /// <para><b>The questions are repeated until nothing new is asked.</b> A question is modal and pumps
    /// messages, so an agent can spawn a helper for a pane that was already passed over while it is on
    /// screen (pipeline adversary). Each pass asks only panes not yet answered; the questions end when a
    /// pass asks nobody. The caller must then ask nothing while closing: a pane with no recorded answer
    /// keeps its helpers running, which loses nothing.</para>
    ///
    /// <para><b>A pane an earlier answer settled is not asked.</b> A helper with helpers of its own is
    /// closed with its PM (Yes) or left running (No); asking it as well could cancel the batch over a
    /// pane that answer already decided, or record an answer nothing reads (pipeline debugger).</para>
    ///
    /// <para><b>An answer covers the helpers its question named.</b> <see cref="Recorded.Helpers"/>
    /// keeps them, so a helper spawned after the question is not closed by a Yes it was never part
    /// of.</para>
    /// </summary>
    public static class PaneBatchCloseQuestions
    {
        /// <summary>One PM's answer.</summary>
        public enum Answer
        {
            /// <summary>Yes: close its helpers too.</summary>
            CloseHelpersToo,

            /// <summary>No: close only the PM; its helpers keep running.</summary>
            KeepHelpers,

            /// <summary>Cancel: close nothing at all.</summary>
            Cancel,
        }

        /// <summary>An answer and the helpers the question named.</summary>
        public sealed record Recorded(Answer Answer, IReadOnlyCollection<string> Helpers);

        /// <summary>
        /// Asks, in <paramref name="closeOrder"/>, each pane with open helpers that no earlier answer
        /// settled, pass after pass until a pass asks nobody.
        /// </summary>
        /// <param name="closeOrder">The batch's close order (<see cref="PaneBatchClose.Order"/>).</param>
        /// <param name="openHelpersOf">A pane's open helper panes right now; null or empty means none.</param>
        /// <param name="ask">Shows the question for a pane, naming these helpers.</param>
        /// <returns>The answers by pane; null as soon as one is <see cref="Answer.Cancel"/>.</returns>
        public static Dictionary<string, Recorded> AskAll(
            IEnumerable<string> closeOrder,
            Func<string, IReadOnlyCollection<string>> openHelpersOf,
            Func<string, IReadOnlyCollection<string>, Answer> ask)
        {
            if (openHelpersOf == null) throw new ArgumentNullException(nameof(openHelpersOf));
            if (ask == null) throw new ArgumentNullException(nameof(ask));

            var order = (closeOrder ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
            var answers = new Dictionary<string, Recorded>(StringComparer.Ordinal);

            // Each pass that asks anyone records a new pane, so there are at most order.Count passes
            // that ask, and the one after asks nobody.
            for (int pass = 0; pass <= order.Count; pass++)
            {
                bool askedAnyone = false;
                foreach (var id in order)
                {
                    if (answers.ContainsKey(id) || SettledByAnEarlierAnswer(id, answers))
                        continue;

                    var helpers = (openHelpersOf(id) ?? Array.Empty<string>())
                        .Where(h => !string.IsNullOrEmpty(h) && !string.Equals(h, id, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (helpers.Count == 0)
                        continue;

                    var answer = ask(id, helpers);
                    if (answer == Answer.Cancel)
                        return null;
                    answers[id] = new Recorded(answer, helpers);
                    askedAnyone = true;
                }

                if (!askedAnyone)
                    break;
            }

            return answers;
        }

        private static bool SettledByAnEarlierAnswer(string id, Dictionary<string, Recorded> answers)
            => answers.Values.Any(r => r.Helpers.Contains(id, StringComparer.Ordinal));
    }
}
