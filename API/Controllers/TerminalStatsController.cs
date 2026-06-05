using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Exposes the per-terminal usage stats the HUD shows (task e855c051) so agents
    /// can self-check via an MCP tool whether it's a good moment to wrap up / clear.
    /// Reads the same statusline temp files the HUD polls — see
    /// <see cref="StatusLineStatsReader"/>. The reader is stateless and cheap, so it's
    /// constructed per-request; the broker is DI-injected for the shared token meter.
    /// </summary>
    [ApiController]
    [Route("api/terminals")]
    public class TerminalStatsController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public TerminalStatsController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// GET /api/terminals/{name}/stats[?docId=...] — context-window % (the
        /// clear/handoff signal) and 5h/7d quota (the rate-cap signal) for a terminal,
        /// with staleness info, plus live token total / cost / burn (task f2702f69).
        /// <c>docId</c> is optional: when omitted the newest stats file for the name is
        /// used. Always 200 — a not-yet-reporting terminal returns <c>available=false</c>.
        /// </summary>
        [HttpGet("{name}/stats")]
        public IActionResult GetStats(string name, [FromQuery] string docId = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "terminal name is required" });

            TerminalUsageStats stats = new StatusLineStatsReader().ReadFor(name, docId);

            // Overlay live token totals if the meter has counted anything for this session.
            // The terminal poll loop feeds the same broker.TokenMeter, so this reads what the
            // HUD banner shows. Plan is unknown here → present cost as an estimate.
            TokenMeterSnapshot snap = _broker?.TokenMeter?.GetSnapshot(stats.SessionId);
            if (snap != null)
            {
                stats.TokensTotal = snap.TotalTokens;
                stats.SubagentTokens = snap.SubagentTokens;
                stats.TokensPerMinute = snap.TokensPerMinute;

                // No reliable API-vs-subscription signal at this layer; default to an estimate rather
                // than risk labeling a subscription's cost as exact metered spend. [codex-adversary]
                CostEstimate cost = PricingTable.Estimate(snap.ByModel, PricingPlan.Subscription);
                stats.CostUsd = cost.TotalUsd;
                stats.CostIsEstimate = cost.IsEstimate;
                stats.CostIsLowerBound = cost.HasUnpricedTokens;
            }

            return Ok(stats);
        }
    }
}
