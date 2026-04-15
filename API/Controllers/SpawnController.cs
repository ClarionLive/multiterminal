using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/spawn")]
    public class SpawnController : ControllerBase
    {
        private readonly SpawnService _spawnService;
        private readonly ProjectDatabase _projectDatabase;

        public SpawnController(SpawnService spawnService, ProjectDatabase projectDatabase)
        {
            _spawnService = spawnService;
            _projectDatabase = projectDatabase;
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

        /// <summary>
        /// Spawn a new ConPTY terminal with Claude Code.
        /// If projectId is provided, resolves the project's source path as working directory.
        /// Used by ClaudeRemote to launch terminals from the phone app.
        /// </summary>
        [HttpPost("terminal")]
        public async Task<IActionResult> SpawnTerminal([FromBody] SpawnTerminalRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.AgentName))
                return BadRequest(new { success = false, error = "agentName is required" });

            // Oracle is always-on — managed by OracleService, not spawnable via API
            if (request.AgentName.Equals(OracleService.OracleName, System.StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { success = false, error = "Oracle is always-on and managed by OracleService. Send messages to Oracle directly." });

            string workingDir = request.WorkingDir;

            // If projectId provided, look up project source path
            if (!string.IsNullOrWhiteSpace(request.ProjectId))
            {
                var project = _projectDatabase.GetRichProject(request.ProjectId);
                if (project == null)
                    return NotFound(new { success = false, error = $"Project '{request.ProjectId}' not found" });

                if (string.IsNullOrWhiteSpace(project.SourcePath))
                    return BadRequest(new { success = false, error = $"Project '{project.Name}' has no source path configured" });

                workingDir = project.SourcePath;
            }

            var (success, docId, error) = await _spawnService.SpawnTeammateAsync(
                request.AgentName,
                agentType: null,
                workingDir,
                initialPrompt: null,
                spawnerName: "ClaudeRemote");

            if (!success)
                return BadRequest(new { success = false, error });

            return Ok(new
            {
                success = true,
                terminalName = request.AgentName,
                docId
            });
        }
    }

    public class SpawnTerminalRequest
    {
        public string ProjectId { get; set; }
        public string AgentName { get; set; }
        public string WorkingDir { get; set; }
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
