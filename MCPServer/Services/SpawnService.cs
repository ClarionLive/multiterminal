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
        /// Returns: (success, docId, errorMessage)
        /// </summary>
        public Func<string, string, string, string, string, Task<(bool success, string docId, string error)>> OnSpawnRequested { get; set; }

        /// <summary>
        /// Callback to spawn a headless AgentProcess.
        /// Parameters: (agentName, workingDir, initialPrompt, mcpConfigPath, spawnerName, taskDescription, subagentType)
        /// Returns: (success, agentProcess, errorMessage)
        /// </summary>
        public Func<string, string, string, string, string, string, string, Task<(bool success, AgentProcess agent, string error)>> OnSpawnAgentRequested { get; set; }

        /// <summary>
        /// Request to spawn a new teammate terminal (ConPTY mode).
        /// </summary>
        public async Task<(bool success, string docId, string error)> SpawnTeammateAsync(
            string agentName,
            string agentType,
            string workingDir,
            string initialPrompt,
            string spawnerName)
        {
            if (OnSpawnRequested == null)
            {
                return (false, null, "Spawn callback not registered. MainForm not initialized.");
            }

            if (string.IsNullOrWhiteSpace(agentName))
            {
                return (false, null, "Agent name is required");
            }

            try
            {
                var result = await OnSpawnRequested(agentName, agentType, workingDir, initialPrompt, spawnerName);
                return result;
            }
            catch (Exception ex)
            {
                return (false, null, $"Spawn failed: {ex.Message}");
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
