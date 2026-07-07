using System;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/remote-mode")]
    public class RemoteModeController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly IConfiguration _config;

        public RemoteModeController(MessageBroker broker, IConfiguration config)
        {
            _broker = broker;
            _config = config;
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
            // Reject untrusted origins so a malicious same-host page can't flip remote_mode.
            // Trusted = loopback OR a host in the configured AllowedHosts allowlist (which
            // includes the *.ts.net Tailscale origin the phone PWA posts from). Pre-Phase-2
            // the standalone gateway proxied this POST server-to-server, stripping the browser
            // Origin so it passed; the in-process gateway (fa1101db R1) mounts this controller
            // directly, so the real .ts.net Origin now reaches the check and must be allowed.
            // Absent Origin = server-to-server / curl / hook — allowed.
            var origin = Request.Headers["Origin"].FirstOrDefault();
            if (!string.IsNullOrEmpty(origin) && !IsTrustedOrigin(origin))
                return Problem(detail: "Origin not allowed", statusCode: 403);

            _broker.SetRemoteMode(request.Enabled);
            return Ok(new { remote_mode = request.Enabled });
        }

        private bool IsTrustedOrigin(string origin)
        {
            // Require explicit http(s) scheme and non-empty host — Uri.IsLoopback returns true
            // for URIs with empty Host (e.g. file:///), which would otherwise bypass this gate.
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;
            if (string.IsNullOrEmpty(uri.Host))
                return false;
            if (uri.IsLoopback)
                return true;

            // Accept origins whose host is in the server's AllowedHosts allowlist. This is the
            // same trust boundary HostFiltering uses, so it can't widen the attack surface beyond
            // hosts the server already serves. Supports the "*.suffix" wildcard form and "*".
            return MatchesAllowedHosts(uri.Host);
        }

        private bool MatchesAllowedHosts(string host)
        {
            var allowed = _config?["AllowedHosts"];
            if (string.IsNullOrWhiteSpace(allowed))
                return false;

            foreach (var raw in allowed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = raw;
                if (pattern == "*")
                    return true;
                if (pattern.StartsWith("*.", StringComparison.Ordinal))
                {
                    // "*.ts.net" matches any sub-label of ts.net (e.g. desktop.tail51f56.ts.net)
                    // but not the bare apex — mirrors ASP.NET HostFiltering wildcard semantics.
                    var suffix = pattern.Substring(1); // ".ts.net"
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public class RemoteModeRequest
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
