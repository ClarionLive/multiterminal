using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Exposes the per-terminal usage stats the HUD shows (task e855c051) so agents
    /// can self-check via an MCP tool whether it's a good moment to wrap up / clear.
    /// Reads the same statusline temp files the HUD polls — see
    /// <see cref="StatusLineStatsReader"/>. The reader is stateless and cheap, so it's
    /// constructed per-request rather than DI-registered.
    /// </summary>
    [ApiController]
    [Route("api/terminals")]
    public class TerminalStatsController : ControllerBase
    {
        /// <summary>
        /// GET /api/terminals/{name}/stats[?docId=...] — context-window % (the
        /// clear/handoff signal) and 5h/7d quota (the rate-cap signal) for a terminal,
        /// with staleness info. <c>docId</c> is optional: when omitted the newest
        /// stats file for the name is used. Always 200 — a not-yet-reporting terminal
        /// returns <c>available=false</c> rather than an error.
        /// </summary>
        [HttpGet("{name}/stats")]
        public IActionResult GetStats(string name, [FromQuery] string docId = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "terminal name is required" });

            TerminalUsageStats stats = new StatusLineStatsReader().ReadFor(name, docId);
            return Ok(stats);
        }
    }
}
