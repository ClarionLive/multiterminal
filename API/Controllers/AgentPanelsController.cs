using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/agent-panels")]
    public class AgentPanelsController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public AgentPanelsController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// Close an agent panel by transcript path.
        /// Called by the SubagentStop hook when a subagent finishes.
        /// </summary>
        [HttpPost("close")]
        public IActionResult ClosePanel([FromBody] ClosePanelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TranscriptPath))
                return Problem(detail: "transcriptPath is required", statusCode: 400);

            _broker.RequestAgentPanelClose(request.TranscriptPath);

            return Ok(new { transcriptPath = request.TranscriptPath });
        }
    }

    public class ClosePanelRequest
    {
        public string TranscriptPath { get; set; }
    }
}
