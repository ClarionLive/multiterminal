using System;
using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Routes ACTIONABLE worktree-janitor findings to people instead of leaving them
    /// in the passive activity feed (task 94356803). Before this service, a failed
    /// auto-merge (conflict, timeout, half-merged checkout) surfaced only as a
    /// deduped activity line — discovery relied on the Owner happening to read the
    /// feed. Now each delivered actionable line also produces an inbox message to
    /// the affected project's team lead (fallback: the broker's default inbox
    /// recipient), and the severe tier additionally fires a phone push.
    ///
    /// <para><b>Dedup contract:</b> this service performs NO dedup of its own. It is
    /// invoked from MainForm's janitor <c>recordActivity</c> hook strictly AFTER the
    /// janitor's cross-sweep dedup has let a line through AND the feed write
    /// persisted — so alerts inherit the janitor's once-per-condition semantics
    /// (notify on appearance, on content change, and on recur-after-clear; task
    /// 7d140c8b) for free. Known limitation, accepted in planning: that dedup memory
    /// is in-process, so an app restart re-notifies once per still-broken condition.</para>
    ///
    /// <para><b>Decoupling:</b> like the janitor's own callbacks, all collaborators
    /// are injected as plain delegates (task/lead resolution, inbox send, push send),
    /// so the service has no broker/DB dependency and the routing rules are unit
    /// testable in isolation.</para>
    /// </summary>
    internal sealed class JanitorAlertService
    {
        /// <summary>Inbox message type stamped on every janitor alert (rendered by InboxPanel).</summary>
        public const string InboxType = "janitor_alert";

        /// <summary>CreatedBy stamped on every janitor alert inbox message.</summary>
        public const string SenderName = "Janitor";

        // The janitor action names that warrant a directed alert. Everything else the
        // janitor logs (recovered merges, orphan removals, plain sweep summaries) is
        // housekeeping and stays feed-only. Keep in sync with the action names emitted
        // in WorktreeJanitorService.SweepCoreAsync.
        private static readonly HashSet<string> ActionableActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "janitor_pending_merge",      // done-task branch alive; merge failed/refused (reason attached)
            "janitor_merge_timeout",      // merge timed out and was rolled back; retry next sweep
            "janitor_merge_indeterminate", // HALF-MERGED checkout — manual cleanup needed (severe)
            "janitor_sweep_attention",    // sweep-level errors / rmdir-blocked strands
        };

        private readonly Func<string, (string Title, string ProjectId)> _resolveTask;
        private readonly Func<string, string> _resolveTeamLead;
        private readonly Func<string> _defaultRecipient;
        private readonly InboxSender _sendInbox;
        private readonly Action<string, string> _sendSeverePush;

        /// <summary>
        /// Inbox delivery delegate. Returns true when the message was durably created.
        /// Wired to <c>MessageBroker.CreateInboxNotification(...)</c> in production.
        /// </summary>
        public delegate bool InboxSender(string recipient, string taskId, string taskTitle, string type, string summary, string createdBy);

        /// <param name="resolveTask">taskId → (task title, projectId); either component may
        /// be null when unknown. Wired to the broker's task cache.</param>
        /// <param name="resolveTeamLead">projectId → team-lead display name, or null when the
        /// project has no assigned lead. Wired to <c>ProjectDatabase.GetRichProject</c> (the
        /// broker's cached project DTO does not carry TeamLead).</param>
        /// <param name="defaultRecipient">Fallback recipient (PM/tester) when no team lead is
        /// resolvable — also the recipient for task-less findings like sweep_attention.</param>
        /// <param name="sendInbox">Inbox delivery primitive.</param>
        /// <param name="sendSeverePush">(title, body) push primitive for the severe tier only.
        /// Owner-approved to bypass the remote-mode gate (forcePush): a half-merged checkout
        /// is rare and needs manual cleanup, so it may buzz the phone even at the desk.
        /// Pass null to disable push (tests).</param>
        public JanitorAlertService(
            Func<string, (string Title, string ProjectId)> resolveTask,
            Func<string, string> resolveTeamLead,
            Func<string> defaultRecipient,
            InboxSender sendInbox,
            Action<string, string> sendSeverePush)
        {
            _resolveTask = resolveTask ?? throw new ArgumentNullException(nameof(resolveTask));
            _resolveTeamLead = resolveTeamLead ?? throw new ArgumentNullException(nameof(resolveTeamLead));
            _defaultRecipient = defaultRecipient ?? throw new ArgumentNullException(nameof(defaultRecipient));
            _sendInbox = sendInbox ?? throw new ArgumentNullException(nameof(sendInbox));
            _sendSeverePush = sendSeverePush; // optional
        }

        /// <summary>True when this janitor action warrants a directed alert.</summary>
        public static bool IsActionable(string action)
            => action != null && ActionableActions.Contains(action);

        /// <summary>True for the severe tier (manual cleanup needed) that also pushes.</summary>
        public static bool IsSevere(string action)
            => string.Equals(action, "janitor_merge_indeterminate", StringComparison.Ordinal);

        /// <summary>
        /// Route one DELIVERED janitor activity line. Non-actionable actions are a no-op.
        /// Returns true when an inbox alert was created. Resolution failures (task gone,
        /// project DB hiccup) degrade to the default recipient rather than dropping the
        /// alert — mis-routed beats silent, which is the whole point of this ticket.
        /// </summary>
        public bool TryAlert(string action, string content, string relatedTaskId)
        {
            if (!IsActionable(action)) return false;
            if (string.IsNullOrWhiteSpace(content)) return false;

            string taskTitle = null;
            string projectId = null;
            if (!string.IsNullOrEmpty(relatedTaskId))
            {
                try
                {
                    (taskTitle, projectId) = _resolveTask(relatedTaskId);
                }
                catch
                {
                    // Task lookup failed — fall through to the default recipient.
                }
            }

            string lead = null;
            if (!string.IsNullOrEmpty(projectId))
            {
                try
                {
                    lead = _resolveTeamLead(projectId);
                }
                catch
                {
                    // Lead lookup failed — fall through to the default recipient.
                }
            }

            string recipient = !string.IsNullOrWhiteSpace(lead) ? lead : _defaultRecipient();
            if (string.IsNullOrWhiteSpace(recipient)) return false;

            bool sent = _sendInbox(recipient, relatedTaskId, taskTitle, InboxType, content, SenderName);

            if (IsSevere(action))
            {
                _sendSeverePush?.Invoke("Janitor: manual cleanup needed", content);
            }

            return sent;
        }
    }
}
