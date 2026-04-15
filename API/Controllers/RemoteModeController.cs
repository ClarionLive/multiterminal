using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/remote-mode")]
    public class RemoteModeController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public RemoteModeController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// GET /api/remote-mode — Check if remote mode is enabled.
        /// </summary>
        [HttpGet]
        public IActionResult GetRemoteMode()
        {
            return Ok(new { remote_mode = _broker.IsRemoteMode });
        }

        /// <summary>
        /// POST /api/remote-mode — Toggle remote mode on/off.
        /// When on, hooks relay AskUserQuestion/Elicitation/Stop to ClaudeRemote.
        /// </summary>
        [HttpPost]
        public IActionResult SetRemoteMode([FromBody] RemoteModeRequest request)
        {
            _broker.SetRemoteMode(request.Enabled);
            return Ok(new { success = true, remote_mode = request.Enabled });
        }
    }

    public class RemoteModeRequest
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
