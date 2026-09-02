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
        /// Project this session is working in, as the hook resolved it (task 2289bb8a item 3).
        /// </summary>
        /// <remarks>
        /// <see cref="Models.TerminalInfo"/> carries no project, so this is learned from the
        /// notification payload — the hook already reads <c>.claude/project.json</c> from the
        /// session's cwd and sends the name. Null until a notification has been seen for the
        /// session; the caller falls back to resolving from <see cref="Cwd"/>.
        /// </remarks>
        public string Project { get; set; }

        /// <summary>Working directory the session reported. Fallback source for <see cref="Project"/>.</summary>
        public string Cwd { get; set; }

        /// <summary>
        /// The pending call this block is about, when the notification supplied one. Null is
        /// normal and must be tolerated — see <see cref="AgentAttentionService"/> remarks.
        /// </summary>
        public string PendingToolUseId { get; set; }

        /// <summary>
        /// The last thing this agent was OBSERVED doing, e.g. "Edit: MainForm.cs" (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// This is what the card's headline detail should show. It replaces the previous behaviour
        /// of rendering <see cref="Detail"/> — the notification message, captured at block time and
        /// never regenerated — in the position of a live observation. The owner saw
        /// "Alice is waiting for your input" sitting there 27 minutes after it stopped being true.
        /// <para>
        /// Null until something has been observed. Null must render as "nothing observed", never as
        /// idle or as fine.
        /// </para>
        /// </remarks>
        public string LastActivity { get; set; }

        /// <summary>
        /// When <see cref="LastActivity"/> was observed (UTC), or null if nothing has been.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="EnteredAtUtc"/>, which is the age of the STATE. A live line that
        /// has silently stopped updating is the same lie the frozen notification text was, in a new
        /// position — so the line carries its own age and can be shown to have gone quiet.
        /// </remarks>
        public DateTime? LastActivityAtUtc { get; set; }

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

                // Learned, never unlearned: a later payload that omits these must not blank out a
                // project we already know. The hook reads project.json from the cwd and can
                // legitimately come back empty (a directory with no .claude/project.json), which
                // would otherwise make the card's project name flicker away mid-session.
                e.Project = NullIfBlank(Str(payload, "project_name")) ?? e.Project;
                e.Cwd = NullIfBlank(Str(payload, "cwd")) ?? e.Cwd;
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
            string toolUseId = null,
            string activitySummary = null)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;

            // Subagent activity proves nothing about the parent and must never clear (see remarks).
            // It does not update the display line either: attributing a subagent's tool call to its
            // parent would put a confident, wrong sentence where the live observation goes, which is
            // the exact failure this ticket exists to remove.
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
                        RecordActivityLine(e, activitySummary, observedAtUtc);
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
                    RecordActivityLine(e, activitySummary, observedAtUtc);
                });
            }
        }

        /// <summary>
        /// Update ONLY the live activity line, without touching the attention state (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what a <c>TOOL_START</c> row gets. That row is written on <c>PreToolUse</c>, and
        /// <c>safety-hook.js</c> is itself a PreToolUse hook returning
        /// <c>permissionDecision: 'ask'</c> — so it runs BEFORE the prompt it causes. Routing it
        /// through <see cref="NoteObservedActivity"/> would clear a block at the instant of its
        /// creation, which is finding 1 of the 2289bb8a spike.
        /// </para>
        /// <para>
        /// So the two concerns are split at the API rather than behind a flag: "what is this agent
        /// doing" can be answered by an event that is NOT evidence the owner has been attended to.
        /// A blocked card keeps pulsing while its activity line moves — which is correct, and is
        /// what an agent draining a queue of already-approved calls actually looks like.
        /// </para>
        /// </remarks>
        /// <param name="sessionKey">Session id, or agent name if that is how the entry was keyed.</param>
        /// <param name="activitySummary">The observed line, e.g. "Edit: MainForm.cs".</param>
        /// <param name="observedAtUtc">When it was observed.</param>
        /// <returns>True if the line changed.</returns>
        public bool NoteActivityLineOnly(string sessionKey, string activitySummary, DateTime observedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            if (string.IsNullOrWhiteSpace(activitySummary)) return false;

            return Upsert(sessionKey, e => RecordActivityLine(e, activitySummary, observedAtUtc));
        }

        /// <summary>
        /// Record that a session's TURN ENDED — the clear-edge for a block the owner DISMISSED
        /// rather than answered (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 2289bb8a spike named <c>UserPromptSubmit</c> as the unblock signal and that is not
        /// sufficient: pressing <b>Escape</b> on a permission prompt submits no prompt, so nothing
        /// fired and the alert stayed lit. The owner reported a card pulsing "needs permission" 49
        /// minutes after they had dismissed it.
        /// </para>
        /// <para>
        /// Escape ends the turn, so the <c>Stop</c> hook fires. Turn-ended is
        /// <see cref="AttentionState.Idle"/>: finished, and explicitly NOT a block — the same state
        /// an <c>idle_prompt</c> notification produces, and for the same reason. It must not pulse.
        /// </para>
        /// <para>
        /// Subagents get their own <c>Stop</c>, so <paramref name="isSubagent"/> is honoured here for
        /// the same reason it is on the activity path: a subagent finishing says nothing about
        /// whether its parent is still waiting on the owner.
        /// </para>
        /// </remarks>
        /// <param name="sessionKey">Session id, or agent name if that is how the entry was keyed.</param>
        /// <param name="observedAtUtc">When the turn ended.</param>
        /// <param name="isSubagent">True when a SUBAGENT's turn ended rather than the main thread's.</param>
        /// <returns>True if the state changed.</returns>
        public bool NoteTurnEnded(string sessionKey, DateTime observedAtUtc, bool isSubagent)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            if (isSubagent) return false;

            lock (_lock)
            {
                if (_entries.TryGetValue(sessionKey, out var existing)
                    && existing.IsBlocking
                    && observedAtUtc <= existing.EnteredAtUtc)
                {
                    // A turn that ended BEFORE the block was raised proves nothing — it is an older
                    // event arriving late, not the owner dismissing this prompt.
                    return false;
                }

                return UpsertLocked(sessionKey, e =>
                {
                    e.State = AttentionState.Idle;
                    e.Detail = null;
                    e.PendingToolUseId = null;
                    RecordActivityLine(e, "Turn ended", observedAtUtc);
                });
            }
        }

        /// <summary>
        /// The tracked entry for an agent, or null (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// <c>activity_feed.actor</c> is the agent NAME, while this cache is keyed by session id, so
        /// a consumer of activity rows has to resolve one to the other. When more than one entry
        /// carries the name — a rotated session whose predecessor has not been superseded — the most
        /// recently entered wins, because that is the live terminal and the other is a corpse.
        /// </remarks>
        public AgentAttentionEntry GetByAgent(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return null;

            lock (_lock)
            {
                AgentAttentionEntry best = null;
                foreach (var e in _entries.Values)
                {
                    if (!string.Equals(e.AgentName, agentName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || e.EnteredAtUtc > best.EnteredAtUtc) best = e;
                }

                return best == null ? null : Clone(best);
            }
        }

        /// <summary>
        /// Sets the live activity line, keeping the newest observation.
        /// </summary>
        /// <remarks>
        /// Out-of-order rows are possible — the writer is a separate Node process with its own clock
        /// — and letting an older one overwrite a newer would make the line go backwards, which
        /// looks exactly like the agent repeating itself.
        /// </remarks>
        private static void RecordActivityLine(AgentAttentionEntry entry, string summary, DateTime observedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(summary)) return;
            if (entry.LastActivityAtUtc is DateTime known && known > observedAtUtc) return;

            entry.LastActivity = summary;
            entry.LastActivityAtUtc = observedAtUtc;
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
            var beforeProject = entry.Project;

            // The live activity line counts as a change (task edcdcdd5). Without this, an agent that
            // is already Working produces no event as it moves from tool to tool — which is the one
            // thing the owner asked for: "each of those cards ALWAYS updating with what's happening".
            var beforeActivity = entry.LastActivity;

            mutate(entry);

            bool changed = entry.State != beforeState
                           || !string.Equals(entry.Detail, beforeDetail, StringComparison.Ordinal)
                           || !string.Equals(entry.PendingToolUseId, beforePending, StringComparison.Ordinal)
                           || !string.Equals(entry.Project, beforeProject, StringComparison.Ordinal)
                           || !string.Equals(entry.LastActivity, beforeActivity, StringComparison.Ordinal);

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
            Project = e.Project,
            Cwd = e.Cwd,
            LastActivity = e.LastActivity,
            LastActivityAtUtc = e.LastActivityAtUtc,
        };

        private static string Str(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
