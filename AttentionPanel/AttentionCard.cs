using System;
using System.Text.Json.Serialization;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// One session card, as the panel's view consumes it (task 2289bb8a item 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ EVERY <see cref="JsonPropertyNameAttribute"/> HERE IS A CONTRACT NO COMPILER CHECKS.
    /// The view reads these names as JavaScript property names; rename one side only and the field
    /// silently arrives as <c>undefined</c>, which renders as an empty string rather than an error.
    /// A PascalCase/camelCase mismatch of exactly this kind once produced a CRITICAL on a clean
    /// build with 442 green tests. They are explicit rather than left to a serializer policy so
    /// the contract is readable in one place, and pinned by the cross-file test in item 9.
    /// </para>
    /// <para>
    /// The split between <see cref="ObservedVerb"/> and <see cref="TicketId"/> is the design rule
    /// of the whole feature: observed state is what the hooks SAW, the ticket is what the agent
    /// last CLAIMED, and they are rendered separately at different weights because the second goes
    /// stale. <see cref="ClaimAgeSeconds"/> exists so the card can say how stale rather than
    /// implying the claim is current.
    /// </para>
    /// </remarks>
    public sealed class AttentionCard
    {
        /// <summary>Session key. Round-trips back on focus_session.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Agent/terminal name shown as the card title.</summary>
        [JsonPropertyName("agent")]
        public string Agent { get; set; }

        /// <summary>Avatar colour, from the terminal registration.</summary>
        [JsonPropertyName("color")]
        public string Color { get; set; }

        /// <summary>Project name, learned from the notification payload.</summary>
        [JsonPropertyName("project")]
        public string Project { get; set; }

        /// <summary>
        /// <see cref="MCPServer.Services.AttentionState"/> as a string. The view keys its stripe,
        /// pip and pulse off this exact spelling, so the enum names ARE part of the contract.
        /// </summary>
        [JsonPropertyName("state")]
        public string State { get; set; }

        /// <summary>Observed headline, e.g. "Needs permission". High confidence: from hooks.</summary>
        [JsonPropertyName("observedVerb")]
        public string ObservedVerb { get; set; }

        /// <summary>Observed detail, e.g. the permission or question text. May be null.</summary>
        [JsonPropertyName("observedDetail")]
        public string ObservedDetail { get; set; }

        /// <summary>Seconds in the current state. The view ticks this locally between pushes.</summary>
        /// <remarks>
        /// DISPLAY ONLY. Never infer block identity from this: it is whole seconds, and the view
        /// increments its own copy between pushes, so it is neither precise nor authoritative. Use
        /// <see cref="BlockSeq"/> to tell one block from the next.
        /// </remarks>
        [JsonPropertyName("sinceSeconds")]
        public long SinceSeconds { get; set; }

        /// <summary>
        /// Which blocked episode this card is showing, counting from 0. The block's IDENTITY
        /// (task 42052f0c, pipeline run 2).
        /// </summary>
        /// <remarks>
        /// The view acknowledges a block to silence its alarm's motion, and that acknowledgement
        /// must cover ONE block — a card that blocks again has to shout again, or one early click
        /// mutes that terminal for good. A calm card is indistinguishable from nobody needing you,
        /// which is the one failure this rail exists to prevent.
        /// <para>
        /// Identity is a counter rather than a clock, after two clocks in a row proved unable to
        /// carry it. Expiring on a DROP in <see cref="SinceSeconds"/> failed at the second boundary
        /// (ack at 0, re-block also samples 0, so `0 &lt; 0` is false). Replacing that with an
        /// epoch-millisecond stamp failed too, in two ways: the producer only restamps on a state
        /// CHANGE, so a second prompt on an already-blocked card reused the first one's stamp; and
        /// two notifications genuinely can land inside the same millisecond. Every clock is a bet
        /// on resolution. A counter has none to lose.
        /// </para>
        /// <para>
        /// It is also a separate field from the age ON PURPOSE. <c>EnteredAtUtc</c> must NOT move
        /// when a blocked card's detail is rewritten, or the number telling the owner who has been
        /// stuck longest resets — an invariant this codebase already holds under test. Identity
        /// must move on exactly that event. One field cannot satisfy both, which is what made the
        /// timestamp attempt fail.
        /// </para>
        /// </remarks>
        [JsonPropertyName("blockSeq")]
        public long BlockSeq { get; set; }

        /// <summary>
        /// How long ago <see cref="ObservedDetail"/> was observed, or -1 when nothing has been
        /// (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// The live activity line needs its OWN age, separate from <see cref="SinceSeconds"/> which
        /// is the age of the state. A "live" line that has silently stopped updating is the same lie
        /// the frozen notification text was, just in a new position — so the view can show it going
        /// quiet rather than presenting a stale string as current.
        /// <para>-1 means "no observation", which must not render as "0 seconds ago".</para>
        /// </remarks>
        [JsonPropertyName("activityAgeSeconds")]
        public long ActivityAgeSeconds { get; set; } = -1;

        /// <summary>
        /// True when <see cref="ObservedDetail"/> is a LIVE observation rather than the message a
        /// notification carried when the block was raised (task edcdcdd5).
        /// </summary>
        /// <remarks>
        /// The two read identically and mean very different things. "Edit: MainForm.cs" observed
        /// eight seconds ago is information; "Alice is waiting for your input", captured at block
        /// time and never regenerated, is a fossil that looks like information. The owner spent 27
        /// minutes looking at the second kind believing it was the first.
        /// </remarks>
        [JsonPropertyName("detailIsLive")]
        public bool DetailIsLive { get; set; }

        /// <summary>Ticket the agent claims to be on, or null. Rendered quietly, as a claim.</summary>
        [JsonPropertyName("ticketId")]
        public string TicketId { get; set; }

        /// <summary>Human label for the claimed checklist position, e.g. "item 2 of 7".</summary>
        [JsonPropertyName("ticketItem")]
        public string TicketItem { get; set; }

        /// <summary>
        /// Age of the ticket claim. The view turns this amber past a threshold — the point being
        /// that a stale claim is visibly stale rather than quietly presented as fact.
        /// </summary>
        [JsonPropertyName("claimAgeSeconds")]
        public long ClaimAgeSeconds { get; set; }

        /// <summary>
        /// Context-window fill for this terminal, 0–100 USED, or null when nothing was read
        /// (task 85af4635).
        /// </summary>
        /// <remarks>
        /// USED rather than remaining, and that asymmetry with the header's quota is deliberate:
        /// this is a FILL GAUGE — "how full is this conversation, do I need to hand off?" — while
        /// the account quota beside the panel title is a BUDGET — "how much can I still do today?".
        /// Two different questions, so two different framings, which is why the header labels its
        /// numbers as remaining rather than leaving the reader to infer a shared polarity.
        /// <para>
        /// ⚠️ NULL IS NOT ZERO. Null means no reading was available, and must render as "--%".
        /// Rendered as 0% it would say "plenty of room" at the exact moment it knows nothing —
        /// the same class of confident falsehood as a card reading "Finished and idle" in front
        /// of a waiting agent. This is not an edge case: an installed machine with no statusline
        /// file shows exactly this state for every terminal at once.
        /// </para>
        /// </remarks>
        [JsonPropertyName("contextPercent")]
        public int? ContextPercent { get; set; }

        /// <summary>
        /// Age of the context reading in seconds, or -1 when there is no reading.
        /// </summary>
        /// <remarks>
        /// Its own age, deliberately separate from <see cref="ActivityAgeSeconds"/> and
        /// <see cref="SinceSeconds"/>. A percentage that stopped updating is the same lie the
        /// frozen notification text was, and worse here because the owner ACTS on this number when
        /// deciding whether to hand off.
        /// </remarks>
        [JsonPropertyName("contextAgeSeconds")]
        public long ContextAgeSeconds { get; set; } = -1;

        /// <summary>
        /// True when the context reading is old enough that the reader no longer vouches for it.
        /// </summary>
        /// <remarks>
        /// Distinct from the header quota's staleness on purpose — <c>StatusLineStatsReader</c>
        /// keeps the two apart because "a terminal can have a fresh context fill but a stale
        /// rate-cap reading". Collapsing them into one flag would mark a good number bad, or a bad
        /// number good.
        /// </remarks>
        [JsonPropertyName("contextStale")]
        public bool ContextStale { get; set; }
    }

    /// <summary>What the board says an agent is working on. A claim, not an observation.</summary>
    public sealed class AttentionTicketClaim
    {
        /// <summary>Task id.</summary>
        public string TaskId { get; set; }

        /// <summary>Label for the checklist position, e.g. "item 2 of 7".</summary>
        public string ItemLabel { get; set; }

        /// <summary>When the board row was last updated (UTC).</summary>
        public DateTime UpdatedAtUtc { get; set; }
    }
}
