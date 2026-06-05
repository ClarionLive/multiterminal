using System;
using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Whether token cost should be presented as an exact charge or a labeled estimate.
    /// On a metered API key the dollar figure is what Anthropic bills; on a Pro/Max
    /// subscription nothing is billed per-token, so the same figure is shown as the
    /// equivalent-API-cost ESTIMATE (item [2]) — never as billing truth.
    /// </summary>
    public enum PricingPlan
    {
        /// <summary>Metered API key — the cost is exact.</summary>
        Api,

        /// <summary>Pro/Max subscription — the cost is an equivalent-API estimate.</summary>
        Subscription,
    }

    /// <summary>Per-1M-token rates (USD) for one model family.</summary>
    public sealed class ModelPricing
    {
        public ModelPricing(decimal inputPer1M, decimal outputPer1M, decimal cacheWrite5mPer1M, decimal cacheReadPer1M)
        {
            this.InputPer1M = inputPer1M;
            this.OutputPer1M = outputPer1M;
            this.CacheWrite5mPer1M = cacheWrite5mPer1M;
            this.CacheReadPer1M = cacheReadPer1M;
        }

        public decimal InputPer1M { get; }

        public decimal OutputPer1M { get; }

        /// <summary>5-minute cache-write rate (1.25× input). 1-hour writes (2×) are not tracked separately — see PricingTable remarks.</summary>
        public decimal CacheWrite5mPer1M { get; }

        public decimal CacheReadPer1M { get; }
    }

    /// <summary>The dollar cost of a set of tokens, with provenance flags for honest display.</summary>
    public sealed class CostEstimate
    {
        /// <summary>Total USD across all priced models.</summary>
        public decimal TotalUsd { get; set; }

        /// <summary>True on a subscription plan — present the figure with a "~" estimate marker.</summary>
        public bool IsEstimate { get; set; }

        /// <summary>True when at least one model id matched no known family and was priced as $0 (so the total is a lower bound).</summary>
        public bool HasUnpricedTokens { get; set; }
    }

    /// <summary>
    /// Model → per-1M-token pricing for the terminal token meter (task f2702f69, item [2]).
    ///
    /// <para>
    /// Rates (USD/1M, cached 2026-05-26 from the claude-api skill): Opus 4.x $5 in / $25 out,
    /// Sonnet 4.x $3 / $15, Haiku 4.5 $1 / $5. Cache multipliers (Anthropic standard): a 5-minute
    /// cache WRITE is 1.25× input, a cache READ is 0.1× input — so cache-creation and cache-read
    /// are priced on SEPARATE lines (on long sessions cache tokens dominate volume; conflating them
    /// makes the cost wildly wrong).
    /// </para>
    ///
    /// <para>
    /// Matching is by model FAMILY (substring: opus / sonnet / haiku), not exact id. Within a
    /// family the 4.x rates are uniform, so a future "claude-opus-4-9" prices correctly without a
    /// table edit. Each message is priced by the model named on THAT message, so a mid-session
    /// <c>/model</c> switch is handled by summing per-model (the breakdown TokenMeterService keeps).
    /// </para>
    ///
    /// <remarks>
    /// Known limitation: 1-hour cache writes (priced at 2× input rather than 1.25×) are folded into
    /// the single <c>cache_creation_input_tokens</c> total the meter tracks and are therefore priced
    /// at the 5-minute rate — a small undercount on the rare 1h-cache session. Item [6] can refine
    /// this by reading the usage <c>cache_creation.ephemeral_1h_input_tokens</c> sub-field if needed.
    /// </remarks>
    /// </summary>
    public static class PricingTable
    {
        // input, output, cacheWrite5m (1.25× input), cacheRead (0.1× input).
        private static readonly ModelPricing OpusPricing = new ModelPricing(5.00m, 25.00m, 6.25m, 0.50m);
        private static readonly ModelPricing SonnetPricing = new ModelPricing(3.00m, 15.00m, 3.75m, 0.30m);
        private static readonly ModelPricing HaikuPricing = new ModelPricing(1.00m, 5.00m, 1.25m, 0.10m);

        /// <summary>
        /// Resolve pricing for a model id by family. Accepts full ids ("claude-opus-4-8") or the
        /// normalized form StatusLineStatsReader emits ("opus-4-8"). Returns null for an unknown
        /// family so the caller can flag the total as a lower bound.
        /// </summary>
        public static ModelPricing Resolve(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;

            // Order: haiku/sonnet/opus are mutually exclusive substrings, so any order is fine.
            if (model.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0) return OpusPricing;
            if (model.IndexOf("sonnet", StringComparison.OrdinalIgnoreCase) >= 0) return SonnetPricing;
            if (model.IndexOf("haiku", StringComparison.OrdinalIgnoreCase) >= 0) return HaikuPricing;
            return null;
        }

        /// <summary>USD cost of one model's token totals. Returns 0 for an unknown model family.</summary>
        public static decimal CostForModel(string model, ModelTokenTotals tokens)
        {
            if (tokens == null) return 0m;
            ModelPricing p = Resolve(model);
            if (p == null) return 0m;

            return (tokens.InputTokens / 1_000_000m * p.InputPer1M)
                + (tokens.OutputTokens / 1_000_000m * p.OutputPer1M)
                + (tokens.CacheCreationTokens / 1_000_000m * p.CacheWrite5mPer1M)
                + (tokens.CacheReadTokens / 1_000_000m * p.CacheReadPer1M);
        }

        /// <summary>
        /// Total USD across a session's per-model breakdown, summing each model at its own rate so a
        /// mid-session <c>/model</c> switch is priced correctly. <paramref name="plan"/> only sets the
        /// estimate flag — the dollar figure is identical either way.
        /// </summary>
        public static CostEstimate Estimate(IReadOnlyDictionary<string, ModelTokenTotals> byModel, PricingPlan plan)
        {
            var result = new CostEstimate { IsEstimate = plan == PricingPlan.Subscription };
            if (byModel == null) return result;

            decimal total = 0m;
            foreach (KeyValuePair<string, ModelTokenTotals> kv in byModel)
            {
                if (kv.Value == null) continue;
                if (Resolve(kv.Key) == null)
                {
                    // Unknown family: count it as unpriced so the UI can mark the total a lower bound,
                    // rather than silently dropping real spend.
                    if (kv.Value.Total > 0) result.HasUnpricedTokens = true;
                    continue;
                }

                total += CostForModel(kv.Key, kv.Value);
            }

            result.TotalUsd = total;
            return result;
        }
    }
}
