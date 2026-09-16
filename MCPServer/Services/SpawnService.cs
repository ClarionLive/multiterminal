using System;
using System.Threading.Tasks;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Service for spawning new terminals programmatically.
    /// Provides callback mechanism for MainForm to handle actual terminal creation.
    /// Supports both ConPTY terminals and headless AgentProcess instances.
    /// </summary>
    public class SpawnService
    {
        /// <summary>
        /// Callback to spawn a new ConPTY terminal.
        /// Parameters: (agentName, agentType, workingDir, initialPrompt, spawnerName)
        /// Returns: (success, docId, errorMessage, terminalName) — terminalName is the identity the
        /// broker ACTUALLY registered (task 77d1182f): a requested name that was already held comes
        /// back suffixed ("Name-2"), so callers must report and address the returned name, not the
        /// one they asked for.
        /// </summary>
        public Func<string, string, string, string, string, Task<(bool success, string docId, string error, string terminalName)>> OnSpawnRequested { get; set; }

        /// <summary>
        /// Callback to spawn a headless AgentProcess.
        /// Parameters: (agentName, workingDir, initialPrompt, mcpConfigPath, spawnerName, taskDescription, subagentType)
        /// Returns: (success, agentProcess, errorMessage)
        /// </summary>
        public Func<string, string, string, string, string, string, string, Task<(bool success, AgentProcess agent, string error)>> OnSpawnAgentRequested { get; set; }

        /// <summary>
        /// Spawned helpers' jobs, held until each helper collects its own (task 8b270b37). Lives here
        /// because this service is the singleton MainForm (which stores a job) and SpawnController (which
        /// serves the collect and status routes) already share. The MultiRemote gateway has no job route.
        /// </summary>
        public SpawnJobStore Jobs { get; } = new SpawnJobStore();

        /// <summary>
        /// Request to spawn a new teammate terminal (ConPTY mode).
        /// </summary>
        public async Task<(bool success, string docId, string error, string terminalName)> SpawnTeammateAsync(
            string agentName,
            string agentType,
            string workingDir,
            string initialPrompt,
            string spawnerName)
        {
            if (OnSpawnRequested == null)
            {
                return (false, null, "Spawn callback not registered. MainForm not initialized.", null);
            }

            if (string.IsNullOrWhiteSpace(agentName))
            {
                return (false, null, "Agent name is required", null);
            }

            // ⚠️ THE CANONICAL IDENTITY IS MINTED HERE (task c28e6177). Every comparison downstream —
            // the broker's uniqueness scan and row lookup, the plugin channel server's roster lookup and
            // isAddressedToMe — is untrimmed, deliberately. So " Alice" and "Alice" are two different
            // terminals from the moment a row exists, and the ONLY safe place to reconcile them is
            // before one does. Normalising here cannot merge two existing identities, because there is
            // nothing to merge yet; normalising in any of those comparisons would, which is why this
            // fix lives at the boundary and not there (task c28e6177 records the refusal).
            //
            // This is the choke point, not the only trim: all three HTTP surfaces (SpawnController's
            // two endpoints and the phone gateway's /api/spawn) trim before calling, because they echo
            // a requestedName back to the caller and it must be the name that was actually used. Those
            // are the same idempotent operation, not a second rule that could disagree — by the time
            // execution reaches here the value is usually already canonical. This line is what makes it
            // true for a caller that has not been written yet.
            agentName = agentName.Trim();

            try
            {
                var result = await OnSpawnRequested(agentName, agentType, workingDir, initialPrompt, spawnerName);
                return result;
            }
            catch (Exception ex)
            {
                return (false, null, $"Spawn failed: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Request to spawn a headless AgentProcess (piped stdin/stdout mode).
        /// This is 100% reliable message delivery — no ConPTY injection needed.
        /// </summary>
        public async Task<(bool success, AgentProcess agent, string error)> SpawnAgentAsync(
            string agentName,
            string workingDir,
            string initialPrompt,
            string mcpConfigPath,
            string spawnerName,
            string taskDescription = null,
            string subagentType = null)
        {
            if (OnSpawnAgentRequested == null)
            {
                return (false, null, "Agent spawn callback not registered. MainForm not initialized.");
            }

            if (string.IsNullOrWhiteSpace(agentName))
            {
                return (false, null, "Agent name is required");
            }

            // Same canonical-identity rule as SpawnTeammateAsync above — a headless agent is registered
            // with the broker too, so it can hold a whitespace-variant name just as a pane can.
            agentName = agentName.Trim();

            try
            {
                var result = await OnSpawnAgentRequested(agentName, workingDir, initialPrompt, mcpConfigPath, spawnerName, taskDescription, subagentType);
                return result;
            }
            catch (Exception ex)
            {
                return (false, null, $"Agent spawn failed: {ex.Message}");
            }
        }
    }
}
