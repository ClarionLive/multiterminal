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

        /// <summary>
        /// Which blocked episode this is, counting from 0. Bumped once per blocking notification.
        /// </summary>
        /// <remarks>
        /// The block's IDENTITY, and deliberately not a timestamp (task 42052f0c, pipeline run 2).
        /// The panel silences an acknowledged alarm's motion and must expire that acknowledgement
        /// when a NEW block starts, or one early click mutes a terminal for good. It cannot use
        /// <see cref="State"/> (a second permission prompt leaves it unchanged) and it cannot use
        /// <see cref="EnteredAtUtc"/>, which deliberately does NOT move on a detail rewrite so the
        /// age keeps ranking who has been stuck longest. Every clock is also a resolution bet: an
        /// earlier attempt keyed identity to epoch milliseconds and two notifications still landed
        /// in the same one. A counter cannot collide.
        /// </remarks>
        public long BlockSeq { get; set; }

        /// <summary>True when the state is one the owner is actually being waited on for.</summary>
        public bool IsBlocking =>
            State == AttentionState.BlockedQuestion ||
            State == AttentionState.BlockedPermission ||
            State == AttentionState.BlockedUnknown;
    }

    /// <summary>Whether a state is one of the blocked-on-the-owner states.</summary>
    /// <remarks>
    /// The static twin of <see cref="AgentAttentionEntry.IsBlocking"/>, for deciding about a state
    /// BEFORE it has been written to an entry (task 42052f0c, pipeline run 2). Kept beside it so
    /// the two lists cannot drift apart unnoticed.
    /// </remarks>
    internal static class AttentionStates
    {
        internal static bool IsBlocking(AttentionState state) =>
            state == AttentionState.BlockedQuestion ||
            state == AttentionState.BlockedPermission ||
            state == AttentionState.BlockedUnknown;

        /// <summary>
        /// How certain a blocking state's PROVENANCE is. Higher means the flavour was reported by
        /// something that could not have meant anything else. Non-blocking states rank 0.
        /// </summary>
        /// <remarks>
        /// This is a ranking of how much the SIGNAL knew, not of how urgent the block is — a
        /// permission request and a question are equally "the owner must act".
        /// <para>
        /// <c>ask_user_question</c> is emitted by a PreToolUse hook that saw the tool name. It
        /// cannot describe anything else, so it ranks highest.
        /// </para>
        /// <para>
        /// <c>permission_prompt</c> ranks below it because Claude Code emits that same type as its
        /// GENERIC "waiting for your input" notification — measured live on 2026-09-03, one landed
        /// 6.2s and 6.1s after two <c>AskUserQuestion</c>s that were not permission requests at
        /// all. So the type is a real signal about a real block, but it is not reliable evidence of
        /// the FLAVOUR.
        /// </para>
        /// <para>
        /// <c>BlockedUnknown</c> ranks lowest by definition: it is the flattened
        /// <c>permission_request</c> with no <c>raw_type</c> at all.
        /// </para>
        /// </remarks>
        internal static int BlockCertainty(AttentionState state)
        {
            switch (state)
            {
                case AttentionState.BlockedQuestion: return 3;
                case AttentionState.BlockedPermission: return 2;
                case AttentionState.BlockedUnknown: return 1;
                default: return 0;
            }
        }
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

        /// <summary>
        /// Raised when a session is dropped from the cache entirely — it no longer exists, as
        /// opposed to having changed state (task cafd47b9).
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="AttentionChanged"/> on purpose. Today's only subscriber rebuilds
        /// from <see cref="Snapshot"/> and so cannot tell the two apart, but announcing a removal on
        /// a "this entry changed" event hands a future incremental subscriber an entry that is not
        /// there any more. The argument is the state the entry held when it was dropped, for
        /// logging; it must not be treated as live.
        /// </remarks>
        public event EventHandler<AgentAttentionEntry> AttentionRemoved;

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

            lock (_lock)
            {
                // AN idle_prompt MUST NOT CLEAR A LIVE BLOCK (task ee17f42d).
                //
                // idle_prompt maps to Idle, correctly, for the case it was written for: the agent
                // finished its turn and nobody is waiting. But Claude Code also emits it on an idle
                // TIMER, so one can land while the owner simply has not answered yet — and the
                // assignment below is unconditional, so it would overwrite BlockedQuestion or
                // BlockedPermission with "Finished and idle".
                //
                // That is not a smaller lie than silence, it is the OPPOSITE of the truth, asserted
                // in the one place built to answer "who needs me?". An idle_prompt arriving on a
                // blocked card CORROBORATES the block — the agent is still waiting — so it is
                // dropped rather than applied.
                //
                // Deliberately narrow: only Idle, and only over a state that is already blocking.
                // A real clear still comes from observed activity or TURN_END, which are evidence
                // that the agent MOVED. Nothing here can keep a stale block alive on its own.
                if (state == AttentionState.Idle
                    && _entries.TryGetValue(key, out var blocked)
                    && blocked.IsBlocking)
                {
                    return false;
                }

                // A VAGUER BLOCK MUST NOT TALK AWAY A PRECISE ONE (task ee17f42d, live test 1).
                //
                // The guard above stops Idle from clearing a block. It does not stop one blocking
                // state from overwriting another, because `e.State = state` below is unconditional
                // — and that is the SAME failure one door over.
                //
                // Measured, not assumed: Claude Code emits its own `permission_prompt` Notification
                // ~6s after every AskUserQuestion (2026-09-03, 15:09:58.101 and 15:11:38.518,
                // 6.2s and 6.1s after the two questions that preceded them). It lands second and
                // wins, so a card raised honestly as "Asked you a question" reverts to "Needs
                // permission" while the question is still on screen. The owner watched exactly that
                // happen and reported it in those words.
                //
                // Wrong-flavour is not a cosmetic failure here. "Needs permission" tells the owner
                // to go and approve something; the agent is in fact holding a multiple-choice
                // question that approving nothing will ever answer.
                //
                // NARROW, and in the same shape as the idle guard: only a DOWNGRADE, and only over
                // a state that is already blocking. An upgrade still applies, a block on a calm
                // card still applies, and a repeat at the same certainty still restamps (which is
                // what keeps the "every blocking notification is a new block" rule below intact).
                //
                // ACCEPTED COST, stated rather than hidden: if the owner answers a question and the
                // agent's very next act needs permission with no observed activity in between, that
                // permission block is suppressed and the card keeps reading "Asked you a question".
                // The owner is still summoned — it is the right alarm with the wrong word, which is
                // the side of this file's governing asymmetry we are meant to fall on. The clear
                // edge (observed activity, TURN_END) closes that window as soon as the agent moves.
                if (AttentionStates.IsBlocking(state)
                    && _entries.TryGetValue(key, out var live)
                    && live.IsBlocking
                    && AttentionStates.BlockCertainty(state) < AttentionStates.BlockCertainty(live.State))
                {
                    return false;
                }

                bool changed = UpsertLocked(key, e =>
                {
                    e.SessionId = sessionId;

                    // Learned, never unlearned (like Project/Cwd below). A payload that omits
                    // agent_name must not blank a name we already know: with it blanked, the
                    // supersede below finds nothing to retire, the name-keyed placeholder lives on,
                    // and every later activity row routes to the placeholder while the real
                    // session-keyed card pulses forever — two cards for one terminal.
                    e.AgentName = NullIfBlank(agentName) ?? e.AgentName;
                    e.State = state;
                    e.Detail = Str(payload, "message");
                    e.PendingToolUseId = NullIfBlank(Str(payload, "tool_use_id"));

                    // Learned, never unlearned: a later payload that omits these must not blank out
                    // a project we already know. The hook reads project.json from the cwd and can
                    // legitimately come back empty (a directory with no .claude/project.json), which
                    // would otherwise make the card's project name flicker away mid-session.
                    e.Project = NullIfBlank(Str(payload, "project_name")) ?? e.Project;
                    e.Cwd = NullIfBlank(Str(payload, "cwd")) ?? e.Cwd;
                },
                // EVERY blocking notification is a new block, including one that arrives while the
                // card already reads blocked. Claude Code does not supply a tool_use_id on a
                // Notification payload — settled empirically, not assumed: the presence-only
                // diagnostic added for task 2289bb8a item 0 logged toolUseId=(absent) on every
                // permission_prompt and idle_prompt observed live. So there is no id to tell a
                // second prompt from a repeat of the first, and the two failures are not
                // symmetrical. Restamping a repeat costs a reset age and an alarm that shouts
                // again, which the owner sees and can dismiss. NOT restamping a genuinely new
                // prompt lets an earlier acknowledgement silence it, rendering a calm card while an
                // agent waits — indistinguishable from nobody needing you. This file already states
                // that asymmetry as its governing rule, so the tie breaks toward shouting.
                startsNewBlock: AttentionStates.IsBlocking(state));

                // A rotated session (/clear mints a new session id) must not leave its predecessor
                // behind. Superseding is reported as a change even when the survivor itself did not
                // move, because the card list DID.
                bool superseded = SupersedeAgentLocked(agentName, key);
                return changed || superseded;
            }
        }

        /// <summary>
        /// Drop every other entry belonging to <paramref name="agentName"/>, keeping only
        /// <paramref name="keepKey"/> (task cafd47b9).
        /// </summary>
        /// <remarks>
        /// <para>
        /// One terminal is one agent name in MultiTerminal, and subagents inherit
        /// <c>MULTITERMINAL_NAME</c> without registering sessions of their own — so the agent name
        /// is a sound stand-in for "which terminal", and a terminal may only ever own one card.
        /// </para>
        /// <para>
        /// This exists because <c>/clear</c> mints a NEW session id. The cache keys on session id,
        /// so without this the pre-clear entry is orphaned: no living agent is left under that key
        /// to ever move it, and it freezes in whatever state it was last observed in. The owner saw
        /// three cards for two terminals, one of them pulsing "needs permission" 49 minutes after
        /// that session had ceased to exist.
        /// </para>
        /// <para>
        /// Eviction happens at the moment of the write rather than in a later sweep, so the ghost is
        /// never renderable at all. A blank agent name evicts NOTHING — an entry that arrived
        /// keyed only by session id has no established terminal identity, and letting it clear the
        /// board would turn unattributable input into a delete-everything primitive.
        /// </para>
        /// </remarks>
        /// <param name="agentName">The agent whose other sessions are stale. Blank is a no-op.</param>
        /// <param name="keepKey">The cache key that survives.</param>
        /// <returns>True if anything was evicted.</returns>
        private bool SupersedeAgentLocked(string agentName, string keepKey)
            => EvictByAgentLocked(agentName, keepKey);

        /// <summary>
        /// Whether an entry belongs to <paramref name="agentName"/>: by its recorded name, OR by
        /// its cache key.
        /// </summary>
        /// <remarks>
        /// The key half is not redundant. <see cref="NoteActivityLineOnly"/> and
        /// <see cref="NoteTurnEnded"/> create entries through <see cref="UpsertLocked"/>, which
        /// sets only the key — so an entry keyed by an agent's NAME can carry a null
        /// <see cref="AgentAttentionEntry.AgentName"/>. Matching on the name alone made such an
        /// entry invisible to eviction: a card that outlived its terminal, the exact bug
        /// <see cref="NoteTerminalGone"/> exists to fix (pipeline run 1, code review).
        /// </remarks>
        private static bool OwnedBy(string key, AgentAttentionEntry entry, string agentName)
            => string.Equals(key, agentName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(entry?.AgentName, agentName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Drop every entry owned by <paramref name="agentName"/> except <paramref name="keepKey"/>
        /// (which may be null: drop them all). The one eviction loop, shared by supersede and by
        /// terminal-gone so the ownership predicate lives in exactly one place.
        /// </summary>
        /// <returns>True if anything was evicted.</returns>
        private bool EvictByAgentLocked(string agentName, string keepKey)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return false;

            List<string> stale = null;
            foreach (var kvp in _entries)
            {
                if (keepKey != null && string.Equals(kvp.Key, keepKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!OwnedBy(kvp.Key, kvp.Value, agentName)) continue;

                (stale ??= new List<string>()).Add(kvp.Key);
            }

            if (stale == null) return false;

            AgentAttentionEntry survivor = null;
            if (keepKey != null) _entries.TryGetValue(keepKey, out survivor);
            bool carried = false;

            foreach (var key in stale)
            {
                if (!_entries.TryGetValue(key, out var dead)) continue;
                _entries.Remove(key);

                // The predecessor's live line moves to the survivor (task edcdcdd5). The usual
                // predecessor is the name-keyed placeholder created when the terminal opened, which
                // has been collecting tool activity for the 10-15s before the first notification
                // carried a session id; dropping that line would blank the card at the exact
                // moment it becomes interesting. The line keeps its own timestamp, so its age
                // stays honest.
                if (survivor != null && dead.LastActivityAtUtc is DateTime seen)
                {
                    string before = survivor.LastActivity;
                    RecordActivityLine(survivor, dead.LastActivity, seen);
                    carried |= !string.Equals(before, survivor.LastActivity, StringComparison.Ordinal);
                }

                AttentionRemoved?.Invoke(this, Clone(dead));
            }

            // The survivor changed after UpsertLocked already announced it. Today's subscriber
            // rebuilds from Snapshot on any event, so the removal above would carry the news — but
            // an incremental subscriber (the reason AttentionRemoved is a separate event) would
            // render the survivor with the line it had BEFORE the carry-over. Say it explicitly.
            if (carried) AttentionChanged?.Invoke(this, Clone(survivor));

            return true;
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
            string agentName = null,
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
                    bool created = UpsertLocked(sessionKey, e =>
                    {
                        e.AgentName = NullIfBlank(agentName) ?? e.AgentName;
                        e.State = AttentionState.Working;
                        e.Detail = null;
                        e.PendingToolUseId = null;
                        RecordActivityLine(e, activitySummary, observedAtUtc);
                    });

                    // A rotated session can announce itself through activity rather than a
                    // notification — a terminal that is /cleared and then simply gets back to work
                    // never blocks, so ApplyNotification is never reached. Superseding on this path
                    // too is what stops that terminal's predecessor lingering.
                    bool supersededOnCreate = SupersedeAgentLocked(
                        NullIfBlank(agentName) ?? _entries[sessionKey].AgentName, sessionKey);

                    return created || supersededOnCreate;
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

                bool changed = UpsertLocked(sessionKey, e =>
                {
                    e.AgentName = NullIfBlank(agentName) ?? e.AgentName;
                    e.State = AttentionState.Working;
                    e.Detail = null;
                    e.PendingToolUseId = null;
                    RecordActivityLine(e, activitySummary, observedAtUtc);
                });

                bool superseded = SupersedeAgentLocked(
                    NullIfBlank(agentName) ?? existing.AgentName, sessionKey);

                return changed || superseded;
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
                foreach (var kvp in _entries)
                {
                    if (!OwnedBy(kvp.Key, kvp.Value, agentName)) continue;
                    if (best == null || kvp.Value.EnteredAtUtc > best.EnteredAtUtc) best = kvp.Value;
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

            // A row that cannot say WHEN it happened (the feed reader's fallback for an unparseable
            // timestamp) must not become the timestamped live line — it would render as "quiet for
            // 2000 years" until the next row overwrote it. Its clear-path safety is handled by the
            // caller; here it is simply not a line.
            if (observedAtUtc == DateTime.MinValue) return;
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
        /// A terminal has been created for <paramref name="agentName"/>: give it a card NOW, in
        /// <see cref="AttentionState.Unknown"/>, keyed by name (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Before this, an entry came into being only on the first notification — in practice the
        /// session-start menu prompt, 10-15 seconds after the terminal appeared. The owner asked
        /// for the card to arrive with the terminal. MultiTerminal pre-registers the agent name
        /// before the shell launches, so the name is the earliest identity there is.
        /// </para>
        /// <para>
        /// Keyed by NAME because no session id exists yet. That is the same fallback key
        /// <see cref="ApplyNotification"/> uses for a payload with no session id, and
        /// <c>AgentActivityWatcher</c> resolves rows to it through <see cref="GetByAgent"/>, so tool
        /// activity lands on this card from the first hook. When the first session-keyed
        /// notification arrives, <see cref="SupersedeAgentLocked"/> retires this placeholder and
        /// carries its live line across.
        /// </para>
        /// <para>
        /// The state is <see cref="AttentionState.Unknown"/>, not Working: nothing has been observed,
        /// and the projector renders that as "Nothing observed yet". Claiming Working here would
        /// state as fact the one thing the panel does not know.
        /// </para>
        /// </remarks>
        /// <returns>True if a card was created; false if the agent already has one.</returns>
        public bool NoteTerminalStarted(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return false;

            lock (_lock)
            {
                foreach (var kvp in _entries)
                {
                    // Already has a card under any key — a re-registration must not add a second,
                    // and must not clobber a name-keyed entry that has been collecting activity.
                    if (OwnedBy(kvp.Key, kvp.Value, agentName)) return false;
                }

                // Deliberately NOT UpsertLocked: that path reports "changed" only when a field
                // moved, and a fresh entry that starts Unknown and stays Unknown would raise nothing
                // — the card would exist in the cache and never reach the panel until something
                // else happened to fire. Creation IS the event here.
                var entry = new AgentAttentionEntry
                {
                    SessionId = agentName,
                    AgentName = agentName,
                    State = AttentionState.Unknown,
                    EnteredAtUtc = DateTime.UtcNow,
                };
                _entries[agentName] = entry;
                AttentionChanged?.Invoke(this, Clone(entry));
                return true;
            }
        }

        /// <summary>
        /// The terminal for <paramref name="agentName"/> is gone: drop every card it owned, under
        /// whatever key (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// By agent name rather than session key because the caller — the broker's
        /// <c>TerminalDisconnected</c> — knows the terminal, not the Claude Code session inside
        /// it. Until this had a caller, a closed terminal's card stayed on the rail forever.
        /// </remarks>
        /// <returns>True if anything was removed.</returns>
        public bool NoteTerminalGone(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return false;

            lock (_lock)
            {
                return EvictByAgentLocked(agentName, keepKey: null);
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
                case "ask_user_question":
                    // The agent asked the owner a multiple-choice question (task ee17f42d). Its own
                    // raw type rather than a reuse of elicitation_dialog: that one is the MCP
                    // elicitation path with its own relay hook, and this file treats notification
                    // flavours as load-bearing rather than interchangeable. Both are a question, so
                    // both map to BlockedQuestion — the distinction is in provenance, not rendering.
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

        /// <param name="startsNewBlock">
        /// Forces the state clock to restart even when the state string did not change, because
        /// this mutation is known to be a NEW blocking episode (task 42052f0c, pipeline run 2).
        /// </param>
        private bool UpsertLocked(string key, Action<AgentAttentionEntry> mutate, bool startsNewBlock = false)
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

            // Block IDENTITY is a counter, deliberately NOT the clock below (task 42052f0c,
            // pipeline run 2). The panel needs to tell one blocked episode from the next so an
            // acknowledgement cannot carry over; the age needs to survive a detail rewrite so the
            // owner can still see who has been stuck longest. Those two requirements conflict
            // whenever a second prompt arrives on an already-blocked card — which is the common
            // case, because the SET edge is synchronous while the CLEAR edge is polled. Deriving
            // identity from any clock also inherits that clock's resolution: an earlier attempt
            // used epoch MILLISECONDS and still collided, because two notifications really can land
            // in the same millisecond. A counter has no resolution to run out of.
            //
            // This MUST run before the `changed` gate below, and a new block MUST itself count as a
            // change (run 3). Two prompts for the same tool are byte-identical — same message, both
            // tool_use_ids null, same project, and while the polled clear edge is still outstanding
            // the same state and activity line too. Every field the diff inspects compares equal, so
            // an increment placed after the early return is simply never reached for the exact
            // repeat this counter exists to catch.
            if (startsNewBlock) entry.BlockSeq++;

            bool changed = startsNewBlock
                           || entry.State != beforeState
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
            BlockSeq = e.BlockSeq,
        };

        private static string Str(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
