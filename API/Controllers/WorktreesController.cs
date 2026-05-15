using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Read-only endpoints exposing the broker's view of per-agent task worktrees.
    /// Backs the <c>mcp__multiterminal__get_active_worktree</c> MCP tool. AC6 of
    /// task c6ed236c: ships the live consumer surface so agents (e.g. the
    /// session-start skill) can query a fresh worktree path rather than rely on
    /// the launch-time <c>MULTITERMINAL_TASK_WORKTREE</c> env var, which goes
    /// stale across task switches inside a long-running shell.
    /// </summary>
    [ApiController]
    [Route("api/worktrees")]
    public class WorktreesController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public WorktreesController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// Return the broker's view of the active task's worktree for
        /// <paramref name="agentName"/>. Always returns 200 — a null
        /// <c>worktreePath</c> means "no active worktree" (no active task,
        /// worktree mode off, or project unregistered). Callers use the null
        /// path as the "no-op" signal in the auto-cd protocol.
        /// </summary>
        [HttpGet("active/{agentName}")]
        public IActionResult GetActiveWorktree(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName))
                return BadRequest(new { error = "agentName is required" });

            var activeTask = _broker.GetMyActiveTask(agentName);
            string worktreePath = _broker.ResolveTaskWorktreePath(agentName);
            string repoRoot = _broker.ResolveTaskRepoRoot(agentName);
            string branchName = null;

            if (activeTask != null)
            {
                try
                {
                    var record = _broker.TaskDb?.GetWorktreeForTask(activeTask.Id);
                    branchName = record?.BranchName;
                }
                catch
                {
                    // Branch lookup is informational; fall through with null on any failure.
                }
            }

            return Ok(new
            {
                agentName,
                taskId = activeTask?.Id,
                taskTitle = activeTask?.Title,
                worktreePath,
                repoRoot,
                branchName,
            });
        }
    }
}
