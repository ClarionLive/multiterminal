using System;
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
        /// <summary>The header a helper presents its launch nonce in: the same header name the GitHub token mint reads.</summary>
        internal const string LaunchNonceHeader = "X-MultiTerminal-Launch-Nonce";

        private readonly SpawnService _spawnService;
        private readonly ProjectDatabase _projectDatabase;
        private readonly MessageBroker _broker;

        public SpawnController(SpawnService spawnService, ProjectDatabase projectDatabase, MessageBroker broker)
        {
            _spawnService = spawnService;
            _projectDatabase = projectDatabase;
            _broker = broker;
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

            // Canonical identity, once, at the top (task c28e6177) — and echoed back below, so the
            // caller is told the name that was actually used rather than the one it typed.
            string agentName = request.AgentName.Trim();

            var (success, agent, error) = await _spawnService.SpawnAgentAsync(
                agentName,
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
                agentName,
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

            // ⚠️ CANONICALISE BEFORE ANY OTHER CHECK READS IT (task c28e6177). This endpoint is the
            // phone app's spawn route, and a mobile keyboard's auto-space after a word is the most
            // plausible real source of a stray trailing space in the whole system.
            //
            // The ORDER is load-bearing, not tidiness: the Oracle guard below compares this name, and
            // an untrimmed "Oracle " would sail past an ordinal-ignore-case equality check and spawn a
            // second Oracle — the exact class of defect this ticket is about, in the guard that exists
            // to prevent it.
            string agentName = request.AgentName.Trim();

            // Oracle is always-on — managed by OracleService, not spawnable via API
            if (agentName.Equals(OracleService.OracleName, System.StringComparison.OrdinalIgnoreCase))
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

            // Size cap (pipeline Run 1, Codex security). Set when the prompt was TYPED one character at
            // a time and trace-logged; since task 8b270b37 the helper collects it in one call instead, so
            // the typing-time DoS is gone. The cap stays: an unbounded job is still an unbounded message
            // into another agent's context, and 16k chars is well past any prose job description —
            // anything larger belongs in a file the helper can read.
            if (initialPrompt != null && initialPrompt.Length > MaxInitialPromptChars)
            {
                return Problem(
                    detail: $"initialPrompt is {initialPrompt.Length} chars; the limit is {MaxInitialPromptChars}. Send a prose brief, and put anything longer in a file the helper can read.",
                    statusCode: 400);
            }

            var (success, docId, error, terminalName) = await _spawnService.SpawnTeammateAsync(
                agentName,
                agentType: null,
                workingDir,
                initialPrompt: initialPrompt,
                spawnerName: spawnerName);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            // What "success" means here (pipeline Run 1, cross-model adversary): the PANE exists and
            // MT has pre-registered the identity. The helper itself has NOT booted — claude has not
            // started, the plugin's SessionStart hook has not run, and there is no channel port yet.
            // Saying so is the contract; a caller that needs "ready" watches list_terminals for a
            // channel port. terminalName is the identity ACTUALLY registered — a held name comes
            // back suffixed.
            //
            // Do NOT reinstate "runs /session-start" here (live test 2026-09-14). A SPAWNED helper
            // never runs that skill: the plugin's SessionStart hook short-circuits on
            // MULTITERMINAL_SPAWNER (session-status-hook.js, "Skip kanban/plan context for spawned
            // agents") and returns a spawned-agent briefing INSTEAD of the auto-run instruction.
            // What actually registers the helper — and opens its channel — is that hook, not the
            // skill. The claim was invisible before this ticket because the flag-less spawn path
            // loaded no plugin at all, so the branch had never once executed for a spawned pane.
            return Ok(new
            {
                terminalName,

                // The CANONICAL requested name, not the raw one. Callers compare these two to decide
                // whether the broker suffixed a held name — mcp/index.js renders "X was already held,
                // so the helper is registered as Y" off exactly this. Echoing the untrimmed input would
                // make "Alice " vs "Alice" look like a collision and print that sentence about a name
                // nothing was holding.
                requestedName = agentName,
                docId,
                ready = false,
                readiness = "pane created; the helper boots and the plugin's SessionStart hook registers it in ~10-30s — it is messageable once list_terminals shows it with a channel port",
            });
        }

        /// <summary>
        /// Whether a pane has a job waiting: <c>pending</c>, <c>collected</c> or <c>no_job</c> (task 8b270b37).
        /// <para>Read-only, and it NEVER returns the job text. It exists for the plugin's SessionStart hook,
        /// which decides whether to tell a spawned helper to call <c>get_my_spawn_job</c> without consuming
        /// anything. Deliberately not nonce-checked: it discloses only whether a job is waiting, and the
        /// hook calls it without a nonce.</para>
        /// <para>⚠️ <b>The hook and the app can NOT be updated in either order.</b> Only one mix is safe.
        /// An old app with the new hook works: this route 404s and the hook falls back to its old wording.
        /// A NEW app with the OLD hook delivers no job at all, because nothing tells the helper to collect;
        /// every job goes uncollected and is reported after 120s. Ship the hook first, or together
        /// (pipeline Run 1, task 8b270b37).</para>
        /// </summary>
        [HttpGet("job/{docId}")]
        public IActionResult GetJobStatus(string docId)
        {
            var entry = _spawnService.Jobs.Get(docId);
            string status = entry == null ? "no_job" : entry.IsCollected ? "collected" : "pending";
            return Ok(new { status, docId });
        }

        /// <summary>
        /// A spawned helper collects its own job (task 8b270b37). Called by the helper's
        /// <c>get_my_spawn_job</c> tool with its <c>MULTITERMINAL_DOC_ID</c>.
        /// <para><b>Only the pane's own helper may collect.</b> The caller presents its launch nonce in the
        /// <c>X-MultiTerminal-Launch-Nonce</c> header, and the collect goes ahead only if that nonce belongs to
        /// a connected terminal whose docId is exactly <paramref name="docId"/>. Otherwise any local caller
        /// holding a docId could take the job, and MT would log a receipt for a job the helper never got
        /// and skip the 120s report (pipeline Run 1: Codex security HIGH, adversary MEDIUM). The API is
        /// loopback and unauthenticated, so this is an integrity check on the receipt, not a wall against a
        /// hostile local process, which could read the nonce from the helper's environment.</para>
        /// <para><c>status</c> is <c>collected</c> (with <c>job</c>; the receipt), <c>refetched</c> (with
        /// <c>job</c> and <c>collectedUtc</c>; NOT a receipt: a repeat within the re-fetch window, e.g. after
        /// a lost response), <c>already_collected</c> (with <c>collectedUtc</c>, no job), or <c>no_job</c>. All four
        /// are 200. A caller that fails the nonce check gets 401 with one message for every reason, and
        /// nothing in the store is touched, not even to say whether a job exists.</para>
        /// <para>POST, not GET, because the call changes state: a GET could be replayed by anything that
        /// treats GETs as safe, and that replay would consume the job.</para>
        /// </summary>
        [HttpPost("job/{docId}/collect")]
        public IActionResult CollectJob(string docId, [FromHeader(Name = LaunchNonceHeader)] string launchNonce)
        {
            var caller = _broker?.GetConnectedTerminalByLaunchNonce(launchNonce);
            if (caller == null || string.IsNullOrEmpty(docId) || !string.Equals(caller.DocId, docId, StringComparison.Ordinal))
            {
                // Identical for no nonce, a wrong nonce, and another pane's nonce: the difference only helps
                // someone probing. Checked before the store so a refused caller learns nothing about the job.
                return StatusCode(401, new
                {
                    error = "This request did not present the launch nonce of the pane it names. "
                          + "Only the helper MultiTerminal launched in that pane can collect its job.",
                });
            }

            var outcome = _spawnService.Jobs.TryCollect(docId, out var entry, out string job);
            return outcome switch
            {
                SpawnJobCollectOutcome.Collected => Ok(new
                {
                    status = "collected",
                    docId,
                    agentName = entry.AgentName,
                    spawnerName = entry.SpawnerName,
                    job,
                }),
                SpawnJobCollectOutcome.Refetched => Ok(new
                {
                    status = "refetched",
                    docId,
                    agentName = entry.AgentName,
                    spawnerName = entry.SpawnerName,
                    collectedUtc = entry.CollectedUtc,
                    job,
                }),
                SpawnJobCollectOutcome.AlreadyCollected => Ok(new
                {
                    status = "already_collected",
                    docId,
                    agentName = entry.AgentName,
                    spawnerName = entry.SpawnerName,
                    collectedUtc = entry.CollectedUtc,
                }),
                _ => Ok(new { status = "no_job", docId }),
            };
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
        /// The helper's job. Optional. MT holds it and the helper COLLECTS it with <c>get_my_spawn_job</c> as its
        /// first action (task 8b270b37); nothing is typed or pushed, so line breaks are kept. A job not collected
        /// within 120s is reported to the spawner as <c>spawn_failed</c>.
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
