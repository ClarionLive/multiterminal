using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST endpoints for querying MCP Gateway server status and discovered tools.
    /// Reads persisted metadata from the gateway's SQLite database — no IPC required.
    /// </summary>
    [ApiController]
    [Route("api/gateway")]
    public class GatewayController : ControllerBase
    {
        private readonly GatewayIntegrationService _gateway;

        public GatewayController(GatewayIntegrationService gateway)
        {
            _gateway = gateway;
        }

        /// <summary>
        /// List all gateway backend servers with live connection status and tool counts.
        /// GET /api/gateway/servers
        /// </summary>
        [HttpGet("servers")]
        public IActionResult GetServers()
        {
            if (!_gateway.IsGatewayInstalled())
                return Problem(detail: "MCP Gateway is not installed", statusCode: 503);

            var servers = _gateway.GetAllGatewayServers();
            return Ok(new
            {
                servers,
                summary = new
                {
                    total = servers.Count,
                    connected = servers.Count(s => s.Connected),
                    enabled = servers.Count(s => s.Enabled),
                    totalTools = servers.Sum(s => s.ToolCount)
                }
            });
        }

        /// <summary>
        /// List all discovered tools across all connected gateway backends.
        /// GET /api/gateway/tools?server=sqlite&profile=default
        /// </summary>
        [HttpGet("tools")]
        public IActionResult GetTools(
            [FromQuery] string server = null,
            [FromQuery] string profile = null)
        {
            if (!_gateway.IsGatewayInstalled())
                return Problem(detail: "MCP Gateway is not installed", statusCode: 503);

            if (!string.IsNullOrEmpty(server))
            {
                var tools = _gateway.GetToolsForServer(server);
                return Ok(new
                {
                    server,
                    toolCount = tools.Count,
                    tools
                });
            }
            else
            {
                var tools = _gateway.GetAllDiscoveredTools(profile);
                var grouped = tools
                    .GroupBy(t => t.ServerName)
                    .Select(g => new { server = g.Key, toolCount = g.Count() })
                    .ToList();

                return Ok(new
                {
                    profile = profile ?? "(all)",
                    totalTools = tools.Count,
                    serverBreakdown = grouped,
                    tools
                });
            }
        }

        /// <summary>
        /// Get a specific tool's full schema by namespaced name (server__toolname).
        /// GET /api/gateway/tools/sqlite__query
        /// </summary>
        [HttpGet("tools/{namespacedName}")]
        public IActionResult GetToolSchema(string namespacedName)
        {
            if (!_gateway.IsGatewayInstalled())
                return Problem(detail: "MCP Gateway is not installed", statusCode: 503);

            // Parse "server__toolname" format
            int sepIdx = namespacedName.IndexOf("__");
            if (sepIdx < 0)
                return Problem(detail: "Tool name must be in 'server__toolname' format", statusCode: 400);

            string serverName = namespacedName.Substring(0, sepIdx);
            string toolName = namespacedName.Substring(sepIdx + 2);

            var tools = _gateway.GetToolsForServer(serverName);
            var tool = tools.FirstOrDefault(t => t.ToolName == toolName);

            if (tool == null)
                return Problem(detail: $"Tool '{toolName}' not found on server '{serverName}'", statusCode: 404);

            return Ok(tool);
        }
    }
}
