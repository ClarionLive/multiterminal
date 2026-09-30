using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services.Startup;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Identity/health endpoint used by the startup self-probe (task 4fec40e2).
    /// <para>
    /// A minimal <c>GET /health</c> already exists (status + port), but it is too weak
    /// a signal for contention classification — a foreign process could coincidentally
    /// answer 200. This endpoint returns a distinctive <see cref="HealthIdentity"/>
    /// carrying <see cref="HealthIdentity.ServiceMarker"/>, so the probe can positively
    /// distinguish "another MultiTerminal holds :5050" from "a foreign process holds it".
    /// </para>
    /// <para>
    /// Since ticket 9a731cda item 2 it also reports <see cref="HealthIdentity.Capabilities"/>,
    /// collected from every registered <see cref="ICapabilityProvider"/> on each request.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("api/health")]
    public class HealthController : ControllerBase
    {
        private readonly IEnumerable<ICapabilityProvider> _capabilityProviders;

        /// <summary>
        /// Creates the controller. The DI container supplies every registered provider, or an empty
        /// sequence when none is registered.
        /// </summary>
        public HealthController(IEnumerable<ICapabilityProvider> capabilityProviders)
        {
            _capabilityProviders = capabilityProviders;
        }

        /// <summary>
        /// Return the running host's identity fingerprint. The reported port is the
        /// connection's actual local port, so it stays correct regardless of config.
        /// </summary>
        [HttpGet]
        public IActionResult Health()
        {
            int port = HttpContext?.Connection?.LocalPort ?? 0;
            HealthIdentity identity = HealthIdentity.Current(port);
            identity.Capabilities = HostCapabilities.Collect(_capabilityProviders);
            return Ok(identity);
        }
    }
}
