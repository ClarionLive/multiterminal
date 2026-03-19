using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using System.Threading.Tasks;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/messaging")]
    public class MessagingController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public MessagingController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// Register a terminal with the messaging system
        /// </summary>
        [HttpPost("register")]
        public IActionResult RegisterTerminal([FromBody] RegisterTerminalRequest request)
        {
            var result = _broker.RegisterTerminal(request.Name, request.DocId);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { terminalId = result.TerminalId });
        }

        /// <summary>
        /// Send a message to another terminal
        /// </summary>
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            var result = await _broker.SendMessage(request.FromTerminalId, request.To, request.Message, request.Priority);
            return Ok(result);
        }

        /// <summary>
        /// Broadcast a message to all terminals
        /// </summary>
        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest request)
        {
            var result = await _broker.Broadcast(request.FromTerminalId, request.Message);
            return Ok(result);
        }

        /// <summary>
        /// Get pending messages for a terminal
        /// </summary>
        [HttpGet("messages/{terminalId}")]
        public IActionResult GetMessages(string terminalId)
        {
            var messages = _broker.GetMessages(terminalId);
            return Ok(messages);
        }

        /// <summary>
        /// List all registered terminals
        /// </summary>
        [HttpGet("terminals")]
        public IActionResult ListTerminals()
        {
            var terminals = _broker.GetTerminals();
            return Ok(terminals);
        }

        /// <summary>
        /// Disconnect a terminal by name (used by session-end hooks)
        /// </summary>
        [HttpPost("disconnect")]
        public IActionResult DisconnectTerminal([FromBody] DisconnectTerminalRequest request)
        {
            if (string.IsNullOrEmpty(request?.Name))
                return BadRequest(new { error = "Name is required" });

            _broker.DisconnectTerminalByName(request.Name);
            return Ok(new { success = true, name = request.Name });
        }
    }

    // Request models
    public class RegisterTerminalRequest
    {
        public string Name { get; set; }
        public string DocId { get; set; }
    }

    public class SendMessageRequest
    {
        public string FromTerminalId { get; set; }
        public string To { get; set; }
        public string Message { get; set; }
        /// <summary>
        /// Message priority: "low", "normal" (default), "high", "critical".
        /// </summary>
        public string Priority { get; set; }
    }

    public class BroadcastRequest
    {
        public string FromTerminalId { get; set; }
        public string Message { get; set; }
    }

    public class DisconnectTerminalRequest
    {
        public string Name { get; set; }
    }
}
