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
                return Problem(detail: "agentName is required", statusCode: 400);

            var (success, agent, error) = await _spawnService.SpawnAgentAsync(
                request.AgentName,
                request.WorkingDir,
                request.InitialPrompt,
                request.McpConfigPath,
                request.SpawnerName ?? "Unknown",
                request.TaskDescription,
                request.SubagentType);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new
            {
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
                return Problem(detail: "agentName is required", statusCode: 400);

            // Oracle is always-on — managed by OracleService, not spawnable via API
            if (request.AgentName.Equals(OracleService.OracleName, System.StringComparison.OrdinalIgnoreCase))
                return Problem(detail: "Oracle is always-on and managed by OracleService. Send messages to Oracle directly.", statusCode: 400);

            string workingDir = request.WorkingDir;

            // If projectId provided, look up project source path
            if (!string.IsNullOrWhiteSpace(request.ProjectId))
            {
                var project = _projectDatabase.GetRichProject(request.ProjectId);
                if (project == null)
                    return Problem(detail: $"Project '{request.ProjectId}' not found", statusCode: 404);

                if (string.IsNullOrWhiteSpace(project.SourcePath))
                    return Problem(detail: $"Project '{project.Name}' has no source path configured", statusCode: 400);

                workingDir = project.SourcePath;
            }

            // Attribute the spawn to whoever actually asked (task 77d1182f). The child reads
            // this as MULTITERMINAL_SPAWNER; the SessionStart hook keys isSpawnedAgent on it and
            // a helper fleet needs to know who spawned whom. "ClaudeRemote" was hardcoded here,
            // so a terminal spawned by Alice reported the phone app as its parent. The fallback
            // is kept for callers that send no name — the phone app's behaviour is unchanged.
            string spawnerName = string.IsNullOrWhiteSpace(request.SpawnerName)
                ? "ClaudeRemote"
                : request.SpawnerName.Trim();

            // The job travels WITH the spawn (task 77d1182f). It used to be hardcoded null here
            // and ignored in MainForm, so a spawner had to spawn and then drive the terminal via
            // /api/terminals/{name}/submit — which chunks above 500 bytes and was observed
            // cutting a command in half. Blank means "no prompt", not an empty prompt.
            string initialPrompt = string.IsNullOrWhiteSpace(request.InitialPrompt)
                ? null
                : request.InitialPrompt;

            // Size cap (pipeline Run 1, Codex security): the prompt is typed into the helper one
            // character at a time and trace-logged by the terminal control, so an unbounded value is
            // both a cheap local DoS and a way to push a large secret-bearing blob into the debug log.
            // 16k chars is far above the 500-byte /submit chunking threshold the tool exists to avoid
            // and well past any prose job description; anything larger belongs in a file.
            if (initialPrompt != null && initialPrompt.Length > MaxInitialPromptChars)
            {
                return Problem(
                    detail: $"initialPrompt is {initialPrompt.Length} chars; the limit is {MaxInitialPromptChars}. It is typed into the helper character by character — send a prose brief, and put anything longer in a file the helper can read.",
                    statusCode: 400);
            }

            var (success, docId, error, terminalName) = await _spawnService.SpawnTeammateAsync(
                request.AgentName,
                agentType: null,
                workingDir,
                initialPrompt: initialPrompt,
                spawnerName: spawnerName);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            // What "success" means here (pipeline Run 1, cross-model adversary): the PANE exists and
            // MT has pre-registered the identity. The helper itself has NOT booted — claude has not
            // started, /session-start has not run, and there is no channel port yet. Saying so is
            // the contract; a caller that needs "ready" watches list_terminals for a channel port.
            // terminalName is the identity ACTUALLY registered — a held name comes back suffixed.
            return Ok(new
            {
                terminalName,
                requestedName = request.AgentName,
                docId,
                ready = false,
                readiness = "pane created; the helper boots, runs /session-start and registers itself in ~10-30s — it is messageable once list_terminals shows it with a channel port",
            });
        }

        /// <summary>Upper bound on <see cref="SpawnTerminalRequest.InitialPrompt"/>, in characters.</summary>
        internal const int MaxInitialPromptChars = 16_000;
    }

    public class SpawnTerminalRequest
    {
        public string ProjectId { get; set; }
        public string AgentName { get; set; }
        public string WorkingDir { get; set; }

        /// <summary>
        /// Name of the agent requesting the spawn. Surfaces in the child as
        /// MULTITERMINAL_SPAWNER. Optional; omitted means "ClaudeRemote" (the phone app).
        /// </summary>
        public string SpawnerName { get; set; }

        /// <summary>
        /// The helper's job, delivered to it as ONE prompt once it has booted and its
        /// /session-start menu is up. Optional. Line breaks are collapsed to spaces on delivery
        /// (the typing path would submit on each one), so write it as prose, not a script.
        /// </summary>
        public string InitialPrompt { get; set; }
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
