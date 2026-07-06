using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/office")]
    public class OfficeController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public OfficeController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// Notify that an agent has been spawned (triggers walk-in animation in Office Panel).
        /// </summary>
        [HttpPost("agents")]
        public IActionResult SpawnAgent([FromBody] SpawnAgentRequest request)
        {
            var result = _broker.NotifyAgentSpawned(request.Name, request.SpawnedBy);
            if (!result.Success)
                return Problem(detail: result.Error, statusCode: 400);

            return Ok(new { agentName = result.AgentName });
        }

        /// <summary>
        /// Notify that an agent has departed (triggers exit animation in Office Panel).
        /// </summary>
        [HttpDelete("agents/{name}")]
        public IActionResult DepartAgent(string name)
        {
            var result = _broker.NotifyAgentDeparted(name);
            if (!result.Success)
                return Problem(detail: result.Error, statusCode: 400);

            return Ok(new { agentName = result.AgentName });
        }

        /// <summary>
        /// List all currently active office agents.
        /// </summary>
        [HttpGet("agents")]
        public IActionResult ListAgents()
        {
            var agents = _broker.GetOfficeAgents();
            return Ok(agents);
        }

        /// <summary>
        /// Clean up stale/ghost office agents older than specified minutes (default 30).
        /// </summary>
        [HttpDelete("agents/cleanup")]
        public IActionResult CleanupStaleAgents([FromQuery] int? olderThanMinutes)
        {
            var removed = _broker.CleanupStaleOfficeAgents(olderThanMinutes ?? 30);
            return Ok(new
            {
                removedCount = removed.Count,
                removedAgents = removed.Select(a => a.Name).ToList()
            });
        }

        /// <summary>
        /// Force-clear all office agents (nuclear option for ghost cleanup).
        /// </summary>
        [HttpDelete("agents/clear-all")]
        public IActionResult ClearAllAgents()
        {
            var removed = _broker.ClearAllOfficeAgents();
            return Ok(new
            {
                removedCount = removed.Count,
                removedAgents = removed.Select(a => a.Name).ToList()
            });
        }
    }

    public class SpawnAgentRequest
    {
        public string Name { get; set; }
        public string SpawnedBy { get; set; }
    }
}
