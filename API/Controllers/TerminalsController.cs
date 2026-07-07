using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Unified terminal surface under <c>api/terminals</c> (task 7ce19175 item 4). Merges the
    /// former TerminalStatsController, TerminalInjectController, and TerminalStreamController into
    /// one controller. Stats + inject/submit already lived at <c>api/terminals</c>; the WebSocket
    /// stream and stream-list moved from the singular <c>api/terminal/...</c> paths to
    /// <c>api/terminals/...</c>, with the old singular paths kept as DEPRECATED aliases for one
    /// release (the second [HttpGet] on each stream action). Responses follow API/CONVENTIONS.md:
    /// success = raw resource, errors = ProblemDetails.
    /// </summary>
    [ApiController]
    [Route("api/terminals")]
    public class TerminalsController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly TerminalStreamService _streamService;

        public TerminalsController(MessageBroker broker, TerminalStreamService streamService)
        {
            _broker = broker;
            _streamService = streamService;
        }

        // ─── Stats (was TerminalStatsController) ──────────────────────────────

        /// <summary>
        /// GET /api/terminals/{name}/stats[?docId=...] — context-window % (the clear/handoff
        /// signal) and 5h/7d quota (the rate-cap signal) for a terminal, with staleness info,
        /// plus live token total / cost / burn (task f2702f69). <c>docId</c> is optional: when
        /// omitted the newest stats file for the name is used. Always 200 — a not-yet-reporting
        /// terminal returns <c>available=false</c>.
        /// </summary>
        [HttpGet("{name}/stats")]
        public IActionResult GetStats(string name, [FromQuery] string docId = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Problem(detail: "terminal name is required", statusCode: 400);

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

        // ─── Inject / submit (was TerminalInjectController) ───────────────────

        /// <summary>
        /// POST /api/terminals/inject — inject text into a live terminal's prompt. The
        /// SessionStart(<c>source=clear</c>) hook POSTs here after a /clear so the cleared Claude
        /// session gets a turn and auto-runs <c>/multiterminal:session-start</c> (task be599e08).
        /// </summary>
        [HttpPost("inject")]
        public IActionResult Inject([FromBody] TerminalInjectRequest request)
        {
            if (request == null)
                return Problem(detail: "request body is required", statusCode: 400);

            var (success, error) = _broker.RequestTerminalInject(
                request.AgentName, request.SessionId, request.Text);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok();
        }

        /// <summary>
        /// POST /api/terminals/{name}/submit — type <c>text</c> into the named agent's own
        /// terminal and press Enter, as a normal prompt submission (Kind="submit"). The
        /// self-clear MCP tool (<c>clear_my_context</c>) POSTs here with <c>text="/clear"</c>
        /// so an agent can clear its own context at a clean continuation point (task 1d6e599d).
        /// Distinct from <c>inject</c>, which routes through the deduped post-/clear trigger.
        /// </summary>
        [HttpPost("{name}/submit")]
        public IActionResult Submit(string name, [FromBody] TerminalSubmitRequest request)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Problem(detail: "terminal name is required", statusCode: 400);
            if (request == null || string.IsNullOrEmpty(request.Text))
                return Problem(detail: "text is required", statusCode: 400);

            var (success, error) = _broker.RequestTerminalInject(
                name, null, request.Text, "submit");

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok();
        }

        // ─── Stream (was TerminalStreamController) ────────────────────────────
        //
        // WebSocket endpoint for streaming terminal I/O. Upgrades HTTP → WebSocket.
        //   Binary frames: raw terminal I/O (VT/ANSI escape sequences)
        //   Text frames:   JSON control messages (resize, disconnect, status)

        /// <summary>
        /// WebSocket terminal I/O stream.
        /// GET /api/terminals/{id}/stream  (canonical)
        /// GET /api/terminal/{id}/stream   (DEPRECATED alias — old singular path)
        /// </summary>
        [HttpGet("{id}/stream")]
        // DEPRECATED (7ce19175): the pre-merge singular path. Kept one release so any un-migrated
        // caller keeps working; remove once consumers move to the plural api/terminals path.
        [HttpGet("/api/terminal/{id}/stream")]
        public async Task Stream(string id)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await HttpContext.Response.WriteAsync("WebSocket connection required");
                return;
            }

            var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await _streamService.HandleConnectionAsync(id, webSocket, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("TerminalsController", $"Stream error: {ex.Message}");

                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InternalServerError,
                            "Server error",
                            default);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// List active terminal streams.
        /// GET /api/terminals/streams  (canonical)
        /// GET /api/terminal/streams   (DEPRECATED alias — old singular path)
        /// </summary>
        [HttpGet("streams")]
        // DEPRECATED (7ce19175): pre-merge singular path; kept one release. See Stream() above.
        [HttpGet("/api/terminal/streams")]
        public IActionResult GetActiveStreams()
        {
            var streams = _streamService.GetActiveStreams();
            var result = new List<object>();

            foreach (var terminalId in streams)
            {
                result.Add(new
                {
                    terminalId,
                    subscriberCount = _streamService.GetSubscriberCount(terminalId)
                });
            }

            return Ok(new { streams = result });
        }

        // ─── Request DTOs ─────────────────────────────────────────────────────

        public class TerminalInjectRequest
        {
            public string AgentName { get; set; }
            public string SessionId { get; set; }
            public string Text { get; set; }
        }

        public class TerminalSubmitRequest
        {
            public string Text { get; set; }
        }
    }
}
