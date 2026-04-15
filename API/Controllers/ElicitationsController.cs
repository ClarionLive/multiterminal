using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/elicitations")]
    public class ElicitationsController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public ElicitationsController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// POST /api/elicitations — Store a pending elicitation request (from hook).
        /// </summary>
        [HttpPost]
        public IActionResult PostElicitation([FromBody] ElicitationPostRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ElicitationId))
                return BadRequest(new { error = "elicitationId is required" });

            var elicitation = new ElicitationRequest
            {
                ElicitationId = request.ElicitationId,
                AgentName = request.AgentName ?? "unknown",
                McpServerName = request.McpServerName ?? "unknown",
                Message = request.Message ?? "",
                SchemaJson = request.SchemaJson ?? "{}"
            };

            _broker.StoreElicitation(elicitation);

            return Ok(new { success = true, elicitationId = request.ElicitationId });
        }

        /// <summary>
        /// POST /api/elicitations/{id}/respond — Submit form response (from ClaudeRemote).
        /// </summary>
        [HttpPost("{id}/respond")]
        public IActionResult SubmitResponse(string id, [FromBody] ElicitationRespondRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Action))
                return BadRequest(new { error = "action is required" });

            var validActions = new[] { "accept", "decline", "cancel" };
            if (!validActions.Contains(request.Action))
                return BadRequest(new { error = "action must be accept, decline, or cancel" });

            var response = new ElicitationResponse
            {
                Action = request.Action,
                ContentJson = request.ContentJson ?? "{}"
            };

            var success = _broker.SubmitElicitationResponse(id, response);
            if (!success)
                return NotFound(new { error = "Elicitation not found or expired" });

            return Ok(new { success = true });
        }

        /// <summary>
        /// GET /api/elicitations/{id}/response — Poll for response (hook calls this).
        /// </summary>
        [HttpGet("{id}/response")]
        public IActionResult GetResponse(string id)
        {
            var response = _broker.GetElicitationResponse(id);
            if (response == null)
                return Ok(new { answered = false });

            return Ok(new
            {
                answered = true,
                action = response.Action,
                contentJson = response.ContentJson
            });
        }
    }

    public class ElicitationPostRequest
    {
        public string ElicitationId { get; set; }
        public string AgentName { get; set; }
        public string McpServerName { get; set; }
        public string Message { get; set; }
        public string SchemaJson { get; set; }
    }

    public class ElicitationRespondRequest
    {
        public string Action { get; set; }
        public string ContentJson { get; set; }
    }
}
