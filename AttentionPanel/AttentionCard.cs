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
        /// <see cref="EnteredAtEpochMs"/> to tell one block from the next.
        /// </remarks>
        [JsonPropertyName("sinceSeconds")]
        public long SinceSeconds { get; set; }

        /// <summary>
        /// When the agent entered the current state, as Unix epoch milliseconds. This is the
        /// block's IDENTITY (task 42052f0c, pipeline run 1).
        /// </summary>
        /// <remarks>
        /// The view acknowledges a block to silence its alarm's motion, and that acknowledgement
        /// must cover ONE block — a card that blocks again has to shout again. Acknowledgement was
        /// originally expired by watching for a DROP in <see cref="SinceSeconds"/>, on the reasoning
        /// that re-entering a state resets the age. That is unsound at the second boundary: if the
        /// owner clicks inside the first second the stored age is 0, and a fresh block also samples
        /// 0, so `0 &lt; 0` is false, no state change is seen, and the new block inherits the old
        /// click — rendering a live alarm permanently calm. A calm card is indistinguishable from
        /// nobody needing you, which is the one failure this rail exists to prevent.
        /// <para>
        /// So the block carries an explicit identity instead of one inferred from a rounded clock.
        /// Two gates (the proactive debugger and the cross-model adversary) found that hole
        /// independently, which is why the fix is an identity rather than a wider comparison:
        /// relaxing `&lt;` to `&lt;=` would drop the acknowledgement on every steady-state push,
        /// trading a silent alarm for one that will not stay quiet.
        /// </para>
        /// </remarks>
        [JsonPropertyName("enteredAtMs")]
        public long EnteredAtEpochMs { get; set; }

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
