using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MultiTerminal.Services;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// The account's rate-cap headroom, shown once beside the panel title (task 85af4635).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ EVERY <see cref="JsonPropertyNameAttribute"/> HERE IS A CONTRACT NO COMPILER CHECKS —
    /// the same hazard documented at length on <see cref="AttentionCard"/>, and the reason
    /// <c>AttentionPanelContractTests</c> sweeps the card's names against the view in both
    /// directions. Those sweeps read <see cref="AttentionCard"/> only, so this type needs its own.
    /// </para>
    /// <para>
    /// <b>Why this is not a card field.</b> <c>StatusLineStatsReader</c> reads two files with two
    /// scopes: the context fill is per-terminal, but the 5h/7d numbers come from a SHARED quota
    /// file "written by whichever terminal rendered most recently so every terminal shows identical
    /// account usage". Hanging them off a card would repeat one value on every card and imply a
    /// per-agent fact that does not exist.
    /// </para>
    /// <para>
    /// <b>Why REMAINING rather than used.</b> The owner's framing, and it is the actionable one:
    /// "I rarely even look at how much I've used, so what's left is the one to highlight." A budget
    /// is read as headroom. The cards' context fill stays USED because it answers a different
    /// question, so the view must LABEL these as remaining rather than let a reader assume both
    /// numbers on screen share a polarity.
    /// </para>
    /// </remarks>
    public sealed class AttentionQuota
    {
        /// <summary>5-hour rolling window REMAINING, 0–100, or null when no reading was available.</summary>
        /// <remarks>Null renders as "--%", never as 0 — see the remarks on <see cref="AttentionCard.ContextPercent"/>.</remarks>
        [JsonPropertyName("fiveHourLeft")]
        public int? FiveHourLeft { get; set; }

        /// <summary>7-day rolling window REMAINING, 0–100, or null when no reading was available.</summary>
        [JsonPropertyName("sevenDayLeft")]
        public int? SevenDayLeft { get; set; }

        /// <summary>Age of the quota reading in seconds, or -1 when there is no reading.</summary>
        [JsonPropertyName("ageSeconds")]
        public long AgeSeconds { get; set; } = -1;

        /// <summary>True when the reader no longer vouches for the quota numbers.</summary>
        [JsonPropertyName("stale")]
        public bool Stale { get; set; }

        /// <summary>
        /// True when the numbers came from the authoritative shared account file rather than one
        /// terminal's private copy.
        /// </summary>
        /// <remarks>
        /// False means second-hand: the shared file was missing or unusable and the reader fell
        /// back to a per-terminal snapshot. Still worth showing — it is the same account either
        /// way — but the view should be able to say so rather than presenting a fallback as the
        /// authoritative figure.
        /// </remarks>
        [JsonPropertyName("fromSharedFile")]
        public bool FromSharedFile { get; set; }

        /// <summary>
        /// Picks the account quota to display from every terminal's reading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every terminal reports the SAME account numbers, so this is not an aggregation — it is a
        /// choice of which copy to trust. Preference order, best first:
        /// </para>
        /// <list type="number">
        ///   <item>a reading from the shared account file that is not stale;</item>
        ///   <item>any non-stale reading;</item>
        ///   <item>the freshest stale reading, clearly marked as stale.</item>
        /// </list>
        /// <para>
        /// Falling back to a stale reading rather than showing nothing is deliberate, and is the
        /// opposite of the rule for a MISSING reading. A stale number that says so is still
        /// information — the owner can weigh it. A missing number rendered as 0 would be an
        /// invention. So absence yields null here, and age yields a flag, and the two are never
        /// conflated.
        /// </para>
        /// </remarks>
        /// <param name="stats">Every terminal's reading; nulls and unavailable entries are ignored.</param>
        /// <returns>The chosen quota, or an all-null instance when nothing was readable.</returns>
        public static AttentionQuota From(IEnumerable<TerminalUsageStats> stats)
        {
            if (stats == null) return new AttentionQuota();

            TerminalUsageStats best = null;
            foreach (var s in stats)
            {
                if (s == null || !s.Available) continue;

                // A reading with neither number is not a quota reading at all. Skipping it stops an
                // empty-but-fresh entry from outranking a real one that happens to be older.
                if (s.FiveHourPercent == null && s.SevenDayPercent == null) continue;

                if (best == null || Prefer(s, best)) best = s;
            }

            if (best == null) return new AttentionQuota();

            return new AttentionQuota
            {
                // Stored as USED; shown as LEFT. Converted once, here, rather than in the view —
                // a subtraction repeated at each render site is a subtraction one site will forget.
                FiveHourLeft = Remaining(best.FiveHourPercent),
                SevenDayLeft = Remaining(best.SevenDayPercent),
                AgeSeconds = best.QuotaAgeSeconds is double age && age >= 0 ? (long)age : -1,
                Stale = best.QuotaStale,
                FromSharedFile = string.Equals(best.QuotaSource, "shared", StringComparison.OrdinalIgnoreCase),
            };
        }

        /// <summary>Whether <paramref name="candidate"/> is a better copy than <paramref name="incumbent"/>.</summary>
        private static bool Prefer(TerminalUsageStats candidate, TerminalUsageStats incumbent)
        {
            // Fresh beats stale, whatever the source: a stale shared reading is no more true than a
            // stale private one.
            if (candidate.QuotaStale != incumbent.QuotaStale) return !candidate.QuotaStale;

            bool candidateShared = string.Equals(candidate.QuotaSource, "shared", StringComparison.OrdinalIgnoreCase);
            bool incumbentShared = string.Equals(incumbent.QuotaSource, "shared", StringComparison.OrdinalIgnoreCase);
            if (candidateShared != incumbentShared) return candidateShared;

            return Age(candidate) < Age(incumbent);
        }

        /// <summary>Reading age, with "unknown" sorting last rather than first.</summary>
        /// <remarks>
        /// A null age means the file carried no timestamp. Treating that as 0 would make an
        /// unverifiable reading outrank every measurable one.
        /// </remarks>
        private static double Age(TerminalUsageStats s) => s.QuotaAgeSeconds ?? double.MaxValue;

        /// <summary>Turns a used percentage into a remaining one, clamped to 0–100.</summary>
        /// <remarks>
        /// Clamped because the source is a rolling-window estimate and can report over 100 when a
        /// window closes mid-read. "-7% left" would look like a bug in the panel rather than a
        /// quirk of the meter, and negative headroom means the same thing as none.
        /// </remarks>
        private static int? Remaining(int? usedPercent)
        {
            if (usedPercent == null) return null;
            int left = 100 - usedPercent.Value;
            return left < 0 ? 0 : (left > 100 ? 100 : left);
        }
    }
}
