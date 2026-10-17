using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>What the user picked in the launch prompt (task ec2ed636).</summary>
    public enum LaunchSessionChoice
    {
        /// <summary>Start a fresh Claude session.</summary>
        NewSession = 0,

        /// <summary>Resume the newest resumable session (<see cref="LaunchSessionChoiceModel.ContinueCandidate"/>).</summary>
        ContinueLast = 1,

        /// <summary>Resume a session picked from the list.</summary>
        PickedSession = 2,
    }

    /// <summary>What a project launch does about sessions, before any UI is shown (task ec2ed636).</summary>
    public enum LaunchSessionAction
    {
        /// <summary>Launch a new session, no prompt.</summary>
        New = 0,

        /// <summary>Resume <see cref="LaunchSessionDecision.Candidate"/>, no prompt.</summary>
        Resume = 1,

        /// <summary>Show the launch dialog.</summary>
        Prompt = 2,
    }

    /// <summary>
    /// One open pane as the launch prompt sees it (task ec2ed636): whether a launch is running in it,
    /// the Claude session id it reports (<c>TerminalDocument.CurrentClaudeSessionId</c>), and when its
    /// current launch started.
    /// </summary>
    public sealed class PaneSessionState
    {
        public PaneSessionState(bool isStarted, string sessionId, DateTime? launchStartedUtc)
        {
            IsStarted = isStarted;
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
            LaunchStartedUtc = launchStartedUtc;
        }

        public bool IsStarted { get; }

        public string SessionId { get; }

        public DateTime? LaunchStartedUtc { get; }

        /// <summary>A running pane that has not (yet, or ever) reported which session it holds.</summary>
        public bool IsUnknown => IsStarted && SessionId == null;
    }

    /// <summary>One session the prompt can offer, with whether it needs a confirmation first.</summary>
    public sealed class LaunchSessionCandidate
    {
        internal LaunchSessionCandidate(SessionCatalogEntry entry, string mayBeOpenReason)
        {
            Entry = entry;
            MayBeOpenReason = mayBeOpenReason;
        }

        public SessionCatalogEntry Entry { get; }

        /// <summary>True when resuming needs a confirmation: something may still be writing this session.</summary>
        public bool MayBeOpenElsewhere => MayBeOpenReason != null;

        /// <summary>Why it may be open elsewhere, as one sentence for the confirm box; null when it is not.</summary>
        public string MayBeOpenReason { get; }
    }

    /// <summary>The pre-UI outcome of <see cref="LaunchSessionChoiceModel.Decide"/>.</summary>
    public sealed class LaunchSessionDecision
    {
        private LaunchSessionDecision(LaunchSessionAction action, LaunchSessionCandidate candidate, LaunchSessionChoiceModel model)
        {
            Action = action;
            Candidate = candidate;
            Model = model;
        }

        public LaunchSessionAction Action { get; }

        /// <summary>The session to resume when <see cref="Action"/> is <see cref="LaunchSessionAction.Resume"/>.</summary>
        public LaunchSessionCandidate Candidate { get; }

        /// <summary>The loaded choices; null when the decision was made without reading the catalog.</summary>
        public LaunchSessionChoiceModel Model { get; }

        internal static LaunchSessionDecision New(LaunchSessionChoiceModel model = null) => new LaunchSessionDecision(LaunchSessionAction.New, null, model);

        internal static LaunchSessionDecision Resume(LaunchSessionCandidate c, LaunchSessionChoiceModel model) => new LaunchSessionDecision(LaunchSessionAction.Resume, c, model);

        internal static LaunchSessionDecision Prompt(LaunchSessionChoiceModel model) => new LaunchSessionDecision(LaunchSessionAction.Prompt, null, model);
    }

    /// <summary>
    /// The decision logic of the project-launch session prompt (task ec2ed636), kept out of the Form
    /// so it is unit-testable: what is offered, what is excluded, what needs a confirm, what a choice
    /// persists, and which name a choice launches under.
    ///
    /// <para><b>Live sessions.</b> A session id reported by an open pane
    /// (<see cref="PaneSessionState.SessionId"/>) is never offered: two Claude processes appending
    /// to one transcript corrupt it.</para>
    ///
    /// <para><b>The null-session pane rule.</b> A started pane that reports no session id may be
    /// holding ANY session: for the first 1-3 s after launch, and permanently when a project-level
    /// statusLine bypasses MT's statusline (also Codex and plain-shell panes, which never report one).
    /// Hiding every session in that case would empty the prompt whenever one such pane is open, so
    /// instead every session whose transcript was written at or after that pane's launch started
    /// (less <see cref="UnknownPaneSlack"/>) is flagged "may be open elsewhere" and needs a confirm.
    /// A pane with no known launch time flags every session. The rule cannot see a session that was
    /// RESUMED into such a pane and has not been written since; that residual gap is accepted, since
    /// the flag is a confirm, not a lock.</para>
    ///
    /// <para>The catalog's own <see cref="SessionCatalogStatus.PossiblyOpenElsewhere"/> (written in the
    /// last <see cref="SessionCatalog.RecentWriteWindow"/>, i.e. maybe in a CA tab or a plain terminal)
    /// is flagged the same way.</para>
    /// </summary>
    public sealed class LaunchSessionChoiceModel
    {
        /// <summary>Clock slack for the null-session pane rule (launch time vs transcript write time).</summary>
        public static readonly TimeSpan UnknownPaneSlack = TimeSpan.FromSeconds(5);

        public LaunchSessionChoiceModel(IEnumerable<SessionCatalogEntry> catalog, IEnumerable<PaneSessionState> panes)
        {
            var paneList = (panes ?? Enumerable.Empty<PaneSessionState>()).Where(p => p != null).ToList();
            LiveSessionIds = LiveSessionIdsOf(paneList);

            var unknownLaunches = paneList.Where(p => p.IsUnknown).Select(p => p.LaunchStartedUtc ?? DateTime.MinValue).ToList();
            HasUnknownPane = unknownLaunches.Count > 0;
            DateTime? earliestUnknown = HasUnknownPane ? unknownLaunches.Min() : (DateTime?)null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<LaunchSessionCandidate>();
            foreach (var entry in (catalog ?? Enumerable.Empty<SessionCatalogEntry>())
                         .Where(e => e != null && !string.IsNullOrWhiteSpace(e.SessionId))
                         .OrderByDescending(e => e.LastActivityUtc))
            {
                if (LiveSessionIds.Contains(entry.SessionId) || !seen.Add(entry.SessionId))
                    continue;
                candidates.Add(new LaunchSessionCandidate(entry, MayBeOpenReasonFor(entry, earliestUnknown)));
            }

            Candidates = candidates;
        }

        /// <summary>The resumable sessions, newest first, with live ones removed.</summary>
        public IReadOnlyList<LaunchSessionCandidate> Candidates { get; }

        /// <summary>What "Continue last" resumes: the newest candidate, or null when there is none.</summary>
        public LaunchSessionCandidate ContinueCandidate => Candidates.Count > 0 ? Candidates[0] : null;

        public bool HasCandidates => Candidates.Count > 0;

        /// <summary>The session ids held by open panes (ordinal-ignore-case: they are GUIDs).</summary>
        public ISet<string> LiveSessionIds { get; }

        /// <summary>At least one running pane has not reported a session id.</summary>
        public bool HasUnknownPane { get; }

        /// <summary>The set to pass to <see cref="SessionCatalog.List"/>: every session id an open pane reports.</summary>
        public static ISet<string> LiveSessionIdsOf(IEnumerable<PaneSessionState> panes) =>
            new HashSet<string>(
                (panes ?? Enumerable.Empty<PaneSessionState>()).Where(p => p?.SessionId != null).Select(p => p.SessionId),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Decides, before any UI, what a project launch does. <paramref name="loadModel"/> reads the
        /// session catalog from disk and is called only when the answer depends on it (not for a
        /// non-Claude kind, Shift, or launch mode New).
        /// </summary>
        public static LaunchSessionDecision Decide(bool isClaudeKind, bool shiftHeld, ProjectLaunchMode mode, Func<LaunchSessionChoiceModel> loadModel)
        {
            if (!isClaudeKind || shiftHeld || mode == ProjectLaunchMode.New || loadModel == null)
                return LaunchSessionDecision.New();

            var model = loadModel();
            if (model == null || !model.HasCandidates)
                return LaunchSessionDecision.New(model); // nothing to resume: never prompt for an empty list

            if (mode == ProjectLaunchMode.Continue)
            {
                var newest = model.ContinueCandidate;
                // Continue mode has no UI to confirm with, and silently resuming an OLDER session
                // would not be "continue". A newest session that may be open elsewhere asks instead.
                return newest.MayBeOpenElsewhere
                    ? LaunchSessionDecision.Prompt(model)
                    : LaunchSessionDecision.Resume(newest, model);
            }

            return LaunchSessionDecision.Prompt(model);
        }

        /// <summary>
        /// The launch mode a "Don't ask again" tick persists, or null for no change. New and Continue
        /// last map to their modes; a picked session leaves the setting alone, because "always resume
        /// this one session" is not a mode.
        /// </summary>
        public static ProjectLaunchMode? ModeToPersist(LaunchSessionChoice choice, bool dontAskAgain)
        {
            if (!dontAskAgain)
                return null;
            switch (choice)
            {
                case LaunchSessionChoice.NewSession: return ProjectLaunchMode.New;
                case LaunchSessionChoice.ContinueLast: return ProjectLaunchMode.Continue;
                default: return null;
            }
        }

        /// <summary>
        /// The name a choice launches under before the broker uniquifies it: the session's original
        /// agent name (or <paramref name="newSessionName"/> when it is unknown) for a resume,
        /// <paramref name="newSessionName"/> for a new session. A non-blank name the user typed wins.
        /// </summary>
        public static string ResolveName(LaunchSessionChoice choice, LaunchSessionCandidate candidate, string newSessionName, string userEditedName)
        {
            if (!string.IsNullOrWhiteSpace(userEditedName))
                return userEditedName.Trim();
            return DefaultNameFor(choice, candidate, newSessionName);
        }

        /// <summary>The prefill for the name field when <paramref name="choice"/> is the one in view.</summary>
        public static string DefaultNameFor(LaunchSessionChoice choice, LaunchSessionCandidate candidate, string newSessionName)
        {
            if (choice != LaunchSessionChoice.NewSession && !string.IsNullOrWhiteSpace(candidate?.Entry?.AgentName))
                return candidate.Entry.AgentName.Trim();
            return newSessionName;
        }

        /// <summary>"just now", "12 min ago", "3 h ago", "5 d ago".</summary>
        public static string FormatRelativeAge(DateTime thenUtc, DateTime nowUtc)
        {
            var age = nowUtc - thenUtc;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
            return $"{(int)age.TotalDays} d ago";
        }

        private static string MayBeOpenReasonFor(SessionCatalogEntry entry, DateTime? earliestUnknownLaunch)
        {
            if (entry.Status == SessionCatalogStatus.PossiblyOpenElsewhere)
                return "Its transcript was written in the last few minutes, so it may still be open in another window (a ClarionAssistant tab or a plain terminal).";

            if (earliestUnknownLaunch.HasValue &&
                (earliestUnknownLaunch.Value == DateTime.MinValue ||
                 entry.LastActivityUtc >= earliestUnknownLaunch.Value - UnknownPaneSlack))
            {
                return "A MultiTerminal pane that was running when this session was last written has not reported which session it holds, so it may be this one.";
            }

            return null;
        }
    }
}
