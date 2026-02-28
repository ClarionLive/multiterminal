using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/spawn")]
    public class SpawnController : ControllerBase
    {
        private readonly SpawnService _spawnService;

        public SpawnController(SpawnService spawnService)
        {
            _spawnService = spawnService;
        }

        /// <summary>
        /// Spawn a headless AgentProcess with piped stdin/stdout.
        /// The agent runs Claude Code in stream-json mode and its conversation
        /// is displayed in the Agent Panel for observation and interaction.
        /// </summary>
        [HttpPost("agent")]
        public async Task<IActionResult> SpawnAgent([FromBody] SpawnAgentProcessRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.AgentName))
                return BadRequest(new { error = "agentName is required" });

            var (success, agent, error) = await _spawnService.SpawnAgentAsync(
                request.AgentName,
                request.WorkingDir,
                request.InitialPrompt,
                request.McpConfigPath,
                request.SpawnerName ?? "Unknown",
                request.TaskDescription,
                request.SubagentType);

            if (!success)
                return BadRequest(new { error });

            return Ok(new
            {
                success = true,
                agentName = request.AgentName,
                processId = agent?.ProcessId ?? -1,
                sessionId = agent?.SessionId
            });
        }
    }

    public class SpawnAgentProcessRequest
    {
        public string AgentName { get; set; }
        public string WorkingDir { get; set; }
        public string InitialPrompt { get; set; }
        public string McpConfigPath { get; set; }
        public string SpawnerName { get; set; }
        public string TaskDescription { get; set; }
        public string SubagentType { get; set; }
    }
}
