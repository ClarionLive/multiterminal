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
    }

    public class TerminalInjectRequest
    {
        public string AgentName { get; set; }
        public string SessionId { get; set; }
        public string Text { get; set; }
    }
}
