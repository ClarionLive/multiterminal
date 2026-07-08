using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// POST /api/ask-owner — synchronous "ask the owner a question" endpoint wrapping
    /// <see cref="PermissionRelayService.BridgeAskOwnerAsync"/> (task 7da88ea0 item 2). Blocks until
    /// the owner answers on the phone, the caller-supplied <c>timeout_seconds</c> elapses (honoring
    /// <c>default_choice</c>), or remote mode is OFF / the relay is unconfigured (returns
    /// <c>source="local"</c> so the agent falls back to a chat-side question). This is the REST half of
    /// the <c>ask_owner</c> MCP tool (item 3, Node side). Additive — the existing push channel and
    /// <c>send_push_notification</c> are unchanged. The controller stays async all the way down
    /// (BridgeAskOwnerAsync → BridgeChoiceAsync), so there is no thread-pool blocking.
    /// </summary>
    [ApiController]
    [Route("api/ask-owner")]
    public class AskOwnerController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly PermissionRelayService _permissionRelay;

        public AskOwnerController(MessageBroker broker, PermissionRelayService permissionRelay = null)
        {
            _broker = broker;
            _permissionRelay = permissionRelay;
        }

        [HttpPost]
        public async Task<IActionResult> AskOwner([FromBody] AskOwnerRequest request, CancellationToken ct)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Question))
                return Problem(detail: "question is required", statusCode: 400);

            // Relay not wired (no Cloudflare Worker configured) — report "local" so the caller asks in
            // chat instead of hanging. Mirrors the service's own remote-mode gate, which likewise
            // returns SourceLocal when remote mode is OFF.
            if (_permissionRelay == null)
                return Ok(new { answer = (string)null, source = AskOwnerResult.SourceLocal });

            // Map the request options ({label,value}[]) to the service's (label,value) tuples. A caller
            // may send value-only (or label-only) options; fall back one to the other so neither is null.
            var options = (request.Options ?? new List<AskOwnerOption>())
                .Where(o => o != null && !(string.IsNullOrWhiteSpace(o.Label) && string.IsNullOrWhiteSpace(o.Value)))
                .Select(o => (label: o.Label ?? o.Value, value: o.Value ?? o.Label))
                .ToList();

            try
            {
                var result = await _permissionRelay.BridgeAskOwnerAsync(
                    agentName: string.IsNullOrWhiteSpace(request.AgentName) ? "unknown" : request.AgentName,
                    prompt: request.Question,
                    options: options,
                    description: request.Context,
                    timeoutSeconds: request.TimeoutSeconds,
                    defaultOnTimeout: request.DefaultChoice,
                    ct: ct).ConfigureAwait(false);

                return Ok(new { answer = result.Answer, source = result.Source });
            }
            catch (OperationCanceledException)
            {
                // The caller (ask_owner MCP tool) disconnected before the owner answered. Treat as a
                // timeout: honor default_choice if provided, else report "timeout" so the agent decides.
                return Ok(new
                {
                    answer = request.DefaultChoice,
                    source = request.DefaultChoice != null ? AskOwnerResult.SourceDefault : AskOwnerResult.SourceTimeout
                });
            }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Error("AskOwner", $"ask_owner bridge failed: {ex.Message}");
                return Problem(detail: "ask_owner relay error", statusCode: 503);
            }
        }
    }

    public class AskOwnerRequest
    {
        public string Question { get; set; }
        public string Context { get; set; }

        /// <summary>Name of the agent asking (set by the ask_owner MCP tool). Optional; defaults to "unknown".</summary>
        public string AgentName { get; set; }

        public List<AskOwnerOption> Options { get; set; }

        [JsonPropertyName("timeout_seconds")]
        public int? TimeoutSeconds { get; set; }

        [JsonPropertyName("default_choice")]
        public string DefaultChoice { get; set; }
    }

    public class AskOwnerOption
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }
}
