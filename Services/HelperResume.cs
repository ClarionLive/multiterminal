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
        /// Whether <paramref name="helper"/> may be resumed now, applying the same rules as the PM's own
        /// resume, in this order: it has a recorded name; it is a Claude session (only Claude has
        /// <c>--resume</c>); the catalog found it (<paramref name="entry"/> null means
        /// <see cref="SessionCatalog.TryGetEntry"/> refused it: live, transcript gone, worktree pruned or
        /// cwd invalid); the model built from <paramref name="livePanes"/> still offers it and does not
        /// flag it "may be open elsewhere" (there is no dialog to confirm with, so a flagged helper is
        /// skipped); <see cref="LaunchSessionChoiceModel.RecheckResume"/> passes; and its cwd validates
        /// against the project root and still exists.
        /// </summary>
        public static HelperResumeCheck Check(
            OpenHelper helper,
            SessionCatalogEntry entry,
            IEnumerable<PaneSessionState> livePanes,
            string projectRoot,
            Func<string, DateTime?> transcriptLastWriteUtc,
            Func<string, bool> directoryExists)
        {
            if (helper == null) return HelperResumeCheck.Skip("nothing was recorded for it");
            if (string.IsNullOrWhiteSpace(helper.TerminalName)) return HelperResumeCheck.Skip("it has no recorded name");
            if (!string.IsNullOrEmpty(helper.Kind) && !string.Equals(helper.Kind, "claude", StringComparison.OrdinalIgnoreCase))
                return HelperResumeCheck.Skip("it is not a Claude session, and only Claude sessions can be resumed");
            if (entry == null)
                return HelperResumeCheck.Skip("its session cannot be resumed: it is already open, or its folder or transcript is gone");

            var panes = (livePanes ?? Enumerable.Empty<PaneSessionState>()).ToList();
            var model = new LaunchSessionChoiceModel(new[] { entry }, panes);
            var candidate = model.Candidates.FirstOrDefault();
            if (candidate == null)
                return HelperResumeCheck.Skip("its session is already open in another pane");
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
}
