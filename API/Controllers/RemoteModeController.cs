using System;
using System.Linq;
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
            // CSRF defense: browsers always send Origin on cross-origin POST.
            // Reject non-loopback origins so a malicious same-host page can't flip remote_mode.
            // Absent Origin = server-to-server / curl / hook — allowed.
            var origin = Request.Headers["Origin"].FirstOrDefault();
            if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
                return StatusCode(403, new { error = "Origin not allowed" });

            _broker.SetRemoteMode(request.Enabled);
            return Ok(new { success = true, remote_mode = request.Enabled });
        }

        private static bool IsLoopbackOrigin(string origin)
        {
            // Require explicit http(s) scheme and non-empty host — Uri.IsLoopback returns true
            // for URIs with empty Host (e.g. file:///), which would otherwise bypass this gate.
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;
            if (string.IsNullOrEmpty(uri.Host))
                return false;
            return uri.IsLoopback;
        }
    }

    public class RemoteModeRequest
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
