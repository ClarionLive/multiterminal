using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Injects text into a live terminal's prompt. The SessionStart(<c>source=clear</c>) hook
    /// POSTs here after a /clear to type "initializing..." so the cleared Claude session gets a
    /// turn and auto-runs <c>/multiterminal:session-start</c> — the reliable replacement for
    /// sniffing the keystroke stream for "/clear" (task be599e08).
    /// </summary>
    [ApiController]
    [Route("api/terminals")]
    public class TerminalInjectController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public TerminalInjectController(MessageBroker broker)
        {
            _broker = broker;
        }

        [HttpPost("inject")]
        public IActionResult Inject([FromBody] TerminalInjectRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, error = "request body is required" });

            var (success, error) = _broker.RequestTerminalInject(
                request.AgentName, request.SessionId, request.Text);

            if (!success)
                return BadRequest(new { success = false, error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// POST /api/terminals/{name}/submit — type <c>text</c> into the named agent's own
        /// terminal and press Enter, as a normal prompt submission (Kind="submit"). The
        /// self-clear MCP tool (<c>clear_my_context</c>) POSTs here with <c>text="/clear"</c>
        /// so an agent can clear its own context at a clean continuation point; the existing
        /// SessionStart(source=clear) recovery loop then rebuilds it from continuation notes +
        /// session summary (task 1d6e599d). Distinct from <c>inject</c>, which routes through
        /// the deduped post-/clear trigger.
        /// </summary>
        [HttpPost("{name}/submit")]
        public IActionResult Submit(string name, [FromBody] TerminalSubmitRequest request)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { success = false, error = "terminal name is required" });
            if (request == null || string.IsNullOrEmpty(request.Text))
                return BadRequest(new { success = false, error = "text is required" });

            var (success, error) = _broker.RequestTerminalInject(
                name, null, request.Text, "submit");

            if (!success)
                return BadRequest(new { success = false, error });

            return Ok(new { success = true });
        }
    }

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
