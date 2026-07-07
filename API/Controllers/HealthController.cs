using Microsoft.AspNetCore.Mvc;
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
    /// </summary>
    [ApiController]
    [Route("api")]
    public class HealthController : ControllerBase
    {
        /// <summary>
        /// Return the running host's identity fingerprint. The reported port is the
        /// connection's actual local port, so it stays correct regardless of config.
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            int port = HttpContext?.Connection?.LocalPort ?? 0;
            return Ok(HealthIdentity.Current(port));
        }
    }
}
