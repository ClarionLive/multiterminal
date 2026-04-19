using System;
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
        private readonly PermissionRelayService _permissionRelay;

        public ElicitationsController(MessageBroker broker, PermissionRelayService permissionRelay = null)
        {
            _broker = broker;
            _permissionRelay = permissionRelay;
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

            // Fire-and-forget bridge to Cloudflare Worker permission relay.
            // No-op if the relay is disabled or not configured (see PermissionRelayService).
            _permissionRelay?.Bridge(elicitation);

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

            InferRemoteModeFromSource();

            var response = new ElicitationResponse
            {
                Action = request.Action,
                ContentJson = request.ContentJson ?? "{}"
            };

            var success = _broker.SubmitElicitationResponse(id, response);
            if (!success)
                return NotFound(new { error = "Elicitation not found or expired" });

            // Cancel any in-flight Worker poll for this elicitation — we already have the answer
            _permissionRelay?.Cancel(id);

            return Ok(new { success = true });
        }

        // X-Source header presence-inference. The signal must be EXPLICIT so ambient
        // traffic doesn't thrash the flag.
        //   X-Source: phone    → remote mode on
        //   X-Source: desktop  → remote mode off
        //   absent / other     → leave state unchanged
        // Short-circuits when the inferred value already matches current state — avoids
        // rewriting settings.txt on every HTTP hit and keeps the audit log meaningful.
        private void InferRemoteModeFromSource()
        {
            if (!Request.Headers.TryGetValue("X-Source", out var v)) return;
            var src = v.ToString();

            bool intended;
            if (string.Equals(src, "phone", StringComparison.OrdinalIgnoreCase))
                intended = true;
            else if (string.Equals(src, "desktop", StringComparison.OrdinalIgnoreCase))
                intended = false;
            else
                return;

            if (intended == _broker.IsRemoteMode) return;

            var callerIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            _broker.DebugLogService?.Info("RemoteMode",
                $"{(intended ? "desktop→phone" : "phone→desktop")} (X-Source={src}, caller={callerIp})");
            _broker.SetRemoteMode(intended);
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
