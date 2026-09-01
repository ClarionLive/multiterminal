using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// What an agent session is observed to be doing, at the confidence the hooks actually support.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from the kanban board. The board records what an agent last
    /// <em>claimed</em>; this records what was <em>observed</em>. The attention rail renders the two
    /// separately and never merges them — see the design rule in task 2289bb8a.
    /// </remarks>
    public enum AttentionState
    {
        /// <summary>Nothing observed yet for this session. Not the same as "not blocked".</summary>
        Unknown = 0,

        /// <summary>Running: tools observed since the last block cleared.</summary>
        Working,

        /// <summary>Blocked on the owner with a real question (raw type elicitation_dialog).</summary>
        BlockedQuestion,

        /// <summary>Blocked on the owner for a yes/no (raw type permission_prompt).</summary>
        BlockedPermission,

        /// <summary>
        /// Blocked on the owner, flavour unknown — the notification arrived carrying only the
        /// flattened <c>permission_request</c> type, with no <c>raw_type</c> to disambiguate.
        /// </summary>
        BlockedUnknown,

        /// <summary>Turn ended, nothing pending. NOT a block: the owner is not being waited on.</summary>
        Idle,

        /// <summary>The host process is gone.</summary>
        Offline,
    }

    /// <summary>One session's observed attention state.</summary>
    public sealed class AgentAttentionEntry
    {
        /// <summary>Claude Code session id the notification carried.</summary>
        public string SessionId { get; set; }

        /// <summary>Agent/terminal name, as the hook reported it.</summary>
        public string AgentName { get; set; }

        /// <summary>Current observed state.</summary>
        public AttentionState State { get; set; }

        /// <summary>When the session entered <see cref="State"/> (UTC).</summary>
        public DateTime EnteredAtUtc { get; set; }

        /// <summary>Human-readable detail for the card, e.g. the permission or question text.</summary>
        public string Detail { get; set; }

        /// <summary>
        /// The pending call this block is about, when the notification supplied one. Null is
        /// normal and must be tolerated — see <see cref="AgentAttentionService"/> remarks.
        /// </summary>
        public string PendingToolUseId { get; set; }

        /// <summary>True when the state is one the owner is actually being waited on for.</summary>
        public bool IsBlocking =>
            State == AttentionState.BlockedQuestion ||
            State == AttentionState.BlockedPermission ||
            State == AttentionState.BlockedUnknown;
    }

    /// <summary>
    /// Tracks which agent sessions are blocked on the owner, and for how long.
    /// Single owner of the attention cache; nothing else may hold this state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SET comes from the Notification hook via <c>MessageBroker.NotificationReceived</c>.
    /// CLEAR comes from observed activity — see <see cref="NoteObservedActivity"/>.
    /// </para>
    /// <para>
    /// <b>Why the clear path takes pre-parsed arguments instead of reading hooks itself.</b>
    /// The logic and the transport are split on purpose. The transport is the weak part: tool
    /// events reach MultiTerminal only as direct SQLite writes into <c>activity_feed</c> from the
    /// Node activity hook, which bypasses the broker entirely, so there is no live event to
    /// subscribe to and the caller has to poll. Keeping that out of here means the state machine
    /// is testable without a database, and a better transport later changes the caller, not this
    /// class.
    /// </para>
    /// <para>
    /// <b>Three findings from the item 0 spike are encoded here. Each one, if ignored, produces a
    /// rail that looks correct and is wrong:</b>
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>PreToolUse must never clear.</b> A PreToolUse hook can return
    /// <c>permissionDecision: 'ask'</c> and so runs BEFORE the prompt it causes. Feeding it here
    /// would clear a block at the instant it was created. Callers must pass only
    /// PostToolUse/PostToolUseFailure/UserPromptSubmit-shaped observations.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Subagent activity must never clear.</b> Subagent tool calls are logged under the PARENT's
    /// session id — measured at 12,544 of 64,444 PreToolUse events, 19.5% — so "any activity means
    /// unblocked" is wrong about one time in five. A parent waiting on the owner would be cleared by
    /// its own background worker. Hence <c>isSubagent</c>, and hence it defaults to the SAFE value.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>A denied permission still clears.</b> Denial produces PostToolUseFailure, not PostToolUse.
    /// A clear path that only accepted success would leave every denied prompt pulsing forever.
    /// This class does not distinguish them — both are "the owner answered".
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public sealed class AgentAttentionService
    {
        private readonly Dictionary<string, AgentAttentionEntry> _entries =
            new Dictionary<string, AgentAttentionEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();

        /// <summary>Raised whenever a session's state changes. Never raised for a no-op.</summary>
        public event EventHandler<AgentAttentionEntry> AttentionChanged;

        /// <summary>Number of sessions currently blocking the owner.</summary>
        public int BlockingCount
        {
            get { lock (_lock) { return _entries.Values.Count(e => e.IsBlocking); } }
        }

        /// <summary>Point-in-time copy of every tracked session.</summary>
        public IReadOnlyList<AgentAttentionEntry> Snapshot()
        {
            lock (_lock)
            {
                return _entries.Values.Select(Clone).ToList();
            }
        }

        /// <summary>Current state for one session, or null if nothing has been observed.</summary>
        public AgentAttentionEntry Get(string sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return null;
            lock (_lock)
            {
                return _entries.TryGetValue(sessionKey, out var e) ? Clone(e) : null;
            }
        }

        /// <summary>
        /// Apply a <c>NotificationReceived</c> payload. This is the SET edge.
        /// </summary>
        /// <param name="payload">The broker payload. Unknown/missing keys are tolerated.</param>
        /// <returns>True if the state changed.</returns>
        public bool ApplyNotification(IDictionary<string, object> payload)
        {
            if (payload == null) return false;

            string sessionId = Str(payload, "session_id");
            string agentName = Str(payload, "agent_name");

            // Key on session id when present, else the agent name. A notification with neither is
            // unattributable and is dropped rather than merged into a bogus shared bucket.
            string key = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : agentName;
            if (string.IsNullOrWhiteSpace(key)) return false;

            // raw_type is the honest one. notification_type is the flattened value ClaudeRemote
            // needs and cannot tell the three apart; falling back to it yields BlockedUnknown,
            // which is deliberately its own state rather than a guess at one of the real ones.
            string rawType = Str(payload, "raw_type");
            if (string.IsNullOrWhiteSpace(rawType)) rawType = Str(payload, "notification_type");

            AttentionState state = MapState(rawType);

            // An unrecognised notification type is not evidence of anything. Dropping it leaves the
            // previous observed state intact, which beats overwriting a real block with a shrug.
            if (state == AttentionState.Unknown) return false;

            return Upsert(key, e =>
            {
                e.SessionId = sessionId;
                e.AgentName = agentName;
                e.State = state;
                e.Detail = Str(payload, "message");
                e.PendingToolUseId = NullIfBlank(Str(payload, "tool_use_id"));
            });
        }

        /// <summary>
        /// Record that a session was observed doing something. This is the CLEAR edge.
        /// </summary>
        /// <param name="sessionKey">Session id, or agent name if that is how the entry was keyed.</param>
        /// <param name="observedAtUtc">When the activity happened.</param>
        /// <param name="isSubagent">
        /// True when the observation came from a SUBAGENT rather than the main thread. Subagent
        /// activity never clears a block (see remarks). Callers that cannot yet tell must pass
        /// <c>true</c> — refusing to clear leaves a stale pulse the owner can dismiss, whereas
        /// clearing wrongly hides an agent that is genuinely waiting, and hides it silently.
        /// </param>
        /// <param name="toolUseId">
        /// The resolved call's id when known. When it and the pending id are both present they must
        /// match; when either is absent this degrades to "something happened after the block", which
        /// is weaker but still sound once subagents are excluded.
        /// </param>
        /// <returns>True if the state changed.</returns>
        public bool NoteObservedActivity(
            string sessionKey,
            DateTime observedAtUtc,
            bool isSubagent,
            string toolUseId = null)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            if (isSubagent) return false;

            lock (_lock)
            {
                if (!_entries.TryGetValue(sessionKey, out var existing))
                {
                    // First thing ever seen for this session: it is working, not blocked.
                    return UpsertLocked(sessionKey, e =>
                    {
                        e.State = AttentionState.Working;
                        e.Detail = null;
                        e.PendingToolUseId = null;
                    });
                }

                if (existing.IsBlocking)
                {
                    // Identity match when both sides have an id; otherwise fall back to ordering.
                    // Activity that predates the block proves nothing — it is the queue draining,
                    // not the owner answering.
                    bool identityKnown = !string.IsNullOrWhiteSpace(existing.PendingToolUseId)
                                         && !string.IsNullOrWhiteSpace(toolUseId);

                    bool resolves = identityKnown
                        ? string.Equals(existing.PendingToolUseId, toolUseId, StringComparison.Ordinal)
                        : observedAtUtc > existing.EnteredAtUtc;

                    if (!resolves) return false;
                }

                return UpsertLocked(sessionKey, e =>
                {
                    e.State = AttentionState.Working;
                    e.Detail = null;
                    e.PendingToolUseId = null;
                });
            }
        }

        /// <summary>Mark a session offline (host process gone). Clears any pending block.</summary>
        public bool MarkOffline(string sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            return Upsert(sessionKey, e =>
            {
                e.State = AttentionState.Offline;
                e.PendingToolUseId = null;
            });
        }

        /// <summary>Forget a session entirely (terminal closed).</summary>
        public bool Remove(string sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            lock (_lock)
            {
                return _entries.Remove(sessionKey);
            }
        }

        /// <summary>
        /// Map a raw Claude Code notification type to a state.
        /// </summary>
        /// <remarks>
        /// <c>idle_prompt</c> maps to <see cref="AttentionState.Idle"/> and NOT to a blocking state.
        /// The agent has finished its turn; nobody is being waited on. Treating it as a block —
        /// which the flattened <c>permission_request</c> mapping invites — would make the rail
        /// pulse for every agent that ever finished work, and an alert that is always on is an
        /// alert nobody reads.
        /// </remarks>
        internal static AttentionState MapState(string rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType)) return AttentionState.Unknown;

            switch (rawType.Trim().ToLowerInvariant())
            {
                case "elicitation_dialog":
                    return AttentionState.BlockedQuestion;
                case "permission_prompt":
                    return AttentionState.BlockedPermission;
                case "idle_prompt":
                    return AttentionState.Idle;
                case "permission_request":
                    // The flattened value: genuinely blocking, flavour unrecoverable.
                    return AttentionState.BlockedUnknown;
                default:
                    return AttentionState.Unknown;
            }
        }

        private bool Upsert(string key, Action<AgentAttentionEntry> mutate)
        {
            lock (_lock)
            {
                return UpsertLocked(key, mutate);
            }
        }

        private bool UpsertLocked(string key, Action<AgentAttentionEntry> mutate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new AgentAttentionEntry
                {
                    SessionId = key,
                    State = AttentionState.Unknown,
                    EnteredAtUtc = DateTime.UtcNow,
                };
                _entries[key] = entry;
            }

            var beforeState = entry.State;
            var beforeDetail = entry.Detail;
            var beforePending = entry.PendingToolUseId;

            mutate(entry);

            bool changed = entry.State != beforeState
                           || !string.Equals(entry.Detail, beforeDetail, StringComparison.Ordinal)
                           || !string.Equals(entry.PendingToolUseId, beforePending, StringComparison.Ordinal);

            if (!changed) return false;

            // Only a STATE change restarts the clock. A card that re-stamped its age every time the
            // detail text was rewritten would reset "waiting 6m" to "waiting 0s" and quietly destroy
            // the one number that tells the owner which agent has been stuck longest.
            if (entry.State != beforeState) entry.EnteredAtUtc = DateTime.UtcNow;

            var copy = Clone(entry);
            AttentionChanged?.Invoke(this, copy);
            return true;
        }

        private static AgentAttentionEntry Clone(AgentAttentionEntry e) => new AgentAttentionEntry
        {
            SessionId = e.SessionId,
            AgentName = e.AgentName,
            State = e.State,
            EnteredAtUtc = e.EnteredAtUtc,
            Detail = e.Detail,
            PendingToolUseId = e.PendingToolUseId,
        };

        private static string Str(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
