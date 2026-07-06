// SECURITY (Eval P2 item 2, task c522764d): this whole file is gated behind #if DEBUG — the
// controller is test-only surface (state-mutating POST /toggle + prompt injectors). In Release
// builds (the shipped installer) nothing here is compiled, so AddControllers() assembly scanning
// never discovers it and none of its routes exist. Available in local Debug builds for testing.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    // Smoke-test harness for the Cloudflare Permission Relay. Exercises each of the 4
    // new request_types (elicitation, choice, plan_approval, notification) end-to-end
    // through PermissionRelayService without depending on hook/controller wiring that
    // hasn't shipped yet. Intended for integration testing only.
    //
    // All 4 test endpoints bail with HTTP 503 when the relay is disabled so callers
    // see the real reason instead of a misleading "sent: true" / "answered: false".
    // GET /status and POST /toggle let test scripts flip the enabled flag without
    // restarting the app. (Whole file is #if DEBUG-gated — see the header comment.)
    [ApiController]
    [Route("api/permission-relay/test")]
    public class PermissionRelayTestController : ControllerBase
    {
        private const string SettingEnabled = "permissionRelay.enabled";
        private const string SettingBaseUrl = "permissionRelay.baseUrl";
        private const string SettingApiKey = "permissionRelay.apiKey";

        private readonly MessageBroker _broker;
        private readonly PermissionRelayService _permissionRelay;
        private readonly SettingsService _settings;

        public PermissionRelayTestController(MessageBroker broker, PermissionRelayService permissionRelay, SettingsService settings)
        {
            _broker = broker;
            _permissionRelay = permissionRelay;
            _settings = settings;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            var enabled = _permissionRelay.IsRelayEnabled();
            var baseUrl = _settings.Get(SettingBaseUrl);
            var apiKey = _settings.Get(SettingApiKey);

            return Ok(new
            {
                enabled,
                baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl,
                hasApiKey = !string.IsNullOrWhiteSpace(apiKey)
            });
        }

        [HttpPost("toggle")]
        public IActionResult Toggle([FromBody] ToggleRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "body required: {\"enabled\": true|false}" });

            _settings.Set(SettingEnabled, request.Enabled ? "1" : "0");
            var enabled = _permissionRelay.IsRelayEnabled();
            return Ok(new { enabled });
        }

        [HttpPost("elicitation")]
        public async Task<IActionResult> TestElicitation([FromBody] ElicitationTestRequest request, CancellationToken ct)
        {
            if (!_permissionRelay.IsRelayEnabled())
                return StatusCode(503, new { error = "relay disabled", hint = "POST /api/permission-relay/test/toggle {\"enabled\":true}" });

            var id = Guid.NewGuid().ToString("N");
            var elicitation = new ElicitationRequest
            {
                ElicitationId = id,
                AgentName = string.IsNullOrWhiteSpace(request?.AgentName) ? "Alice" : request.AgentName,
                McpServerName = "smoke-test",
                Message = request?.Prompt ?? "Smoke test: please enter any text",
                SchemaJson = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}"
            };

            _broker.StoreElicitation(elicitation);
            _permissionRelay.Bridge(elicitation);

            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var response = _broker.GetElicitationResponse(id);
                if (response != null)
                {
                    return Ok(new
                    {
                        elicitationId = id,
                        answered = true,
                        action = response.Action,
                        contentJson = response.ContentJson
                    });
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            _permissionRelay.Cancel(id);
            return Ok(new { elicitationId = id, answered = false });
        }

        [HttpPost("choice")]
        public async Task<IActionResult> TestChoice([FromBody] ChoiceTestRequest request, CancellationToken ct)
        {
            if (!_permissionRelay.IsRelayEnabled())
                return StatusCode(503, new { error = "relay disabled", hint = "POST /api/permission-relay/test/toggle {\"enabled\":true}" });

            var opts = (request?.Options ?? new List<ChoiceOption>())
                .Select(o => (o?.Label ?? string.Empty, o?.Value ?? string.Empty));

            var value = await _permissionRelay.BridgeChoiceAsync(
                string.IsNullOrWhiteSpace(request?.AgentName) ? "Alice" : request.AgentName,
                request?.Prompt ?? string.Empty,
                opts,
                request?.Description,
                ct).ConfigureAwait(false);

            return Ok(new { value });
        }

        [HttpPost("plan-approval")]
        public async Task<IActionResult> TestPlanApproval([FromBody] PlanApprovalTestRequest request, CancellationToken ct)
        {
            if (!_permissionRelay.IsRelayEnabled())
                return StatusCode(503, new { error = "relay disabled", hint = "POST /api/permission-relay/test/toggle {\"enabled\":true}" });

            var result = await _permissionRelay.BridgePlanApprovalAsync(
                string.IsNullOrWhiteSpace(request?.AgentName) ? "Alice" : request.AgentName,
                request?.Markdown ?? string.Empty,
                request?.Description,
                ct).ConfigureAwait(false);

            if (result == null)
                return Ok(new { decision = (string)null, comment = (string)null });

            return Ok(new { decision = result.Decision, comment = result.Comment });
        }

        [HttpPost("notification")]
        public IActionResult TestNotification([FromBody] NotificationTestRequest request)
        {
            if (!_permissionRelay.IsRelayEnabled())
                return StatusCode(503, new { error = "relay disabled", hint = "POST /api/permission-relay/test/toggle {\"enabled\":true}" });

            _permissionRelay.Notify(
                string.IsNullOrWhiteSpace(request?.AgentName) ? "Alice" : request.AgentName,
                request?.Description ?? string.Empty);
            return Ok(new { sent = true });
        }

        public class ToggleRequest
        {
            public bool Enabled { get; set; }
        }

        public class ElicitationTestRequest
        {
            public string AgentName { get; set; }
            public string Prompt { get; set; }
        }

        public class ChoiceOption
        {
            public string Label { get; set; }
            public string Value { get; set; }
        }

        public class ChoiceTestRequest
        {
            public string AgentName { get; set; }
            public string Prompt { get; set; }
            public List<ChoiceOption> Options { get; set; }
            public string Description { get; set; }
        }

        public class PlanApprovalTestRequest
        {
            public string AgentName { get; set; }
            public string Markdown { get; set; }
            public string Description { get; set; }
        }

        public class NotificationTestRequest
        {
            public string AgentName { get; set; }
            public string Description { get; set; }
        }
    }
}
#endif
