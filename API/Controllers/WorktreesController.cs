using System;
using System.Threading.Tasks;
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

            // Worktree-purpose resolution (helper-aware): a helper is never the
            // task assignee, so the strict GetMyActiveTask would miss and the tool
            // would wrongly report "no active worktree" for a helper that owns one
            // (task bab81a92, acceptance scenario 2b).
            var activeTask = _broker.ResolveActiveTaskForAgent(agentName);
            string worktreePath = _broker.ResolveTaskWorktreePath(agentName);
            string repoRoot = _broker.ResolveTaskRepoRoot(agentName);
            string branchName = null;

            if (activeTask != null)
            {
                try
                {
                    // Per-agent isolation: resolve THIS agent's worktree row so the
                    // branch reflects their own (canonical or helper) branch. Falls
                    // back to the canonical row if the agent has no per-agent row.
                    var record = _broker.TaskDb?.GetWorktreeForTask(activeTask.Id, agentName)
                                 ?? _broker.TaskDb?.GetWorktreeForTask(activeTask.Id);
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

        /// <summary>
        /// Read-only point-in-time view of de-registered-but-on-disk worktree
        /// strands (empty dirs git no longer tracks). Surfaces the teardown-
        /// reliability signal (task 248cc2ce) so strand accumulation is observable
        /// instead of discovered by stumbling on an orphan dir. Shells
        /// <c>git worktree list</c> per repo group — call on a refresh cadence, not
        /// per UI repaint.
        /// <para>SCOPE: coverage is limited to worktree parent dirs derivable from
        /// <c>task_worktrees</c> history (plus their legacy sibling parents). A repo
        /// with no surviving <c>task_worktrees</c> rows is outside this scope and is
        /// not scanned — its strands won't be reported. This matches the janitor's
        /// own Pass-3 sweep scope; it is not broker-wide repo coverage.</para>
        /// <para>Always HTTP 200, but the payload carries an explicit
        /// <c>status</c> so a caller can NEVER mistake "couldn't tell" for "none":
        /// <c>ok</c> = complete scan, <c>count</c> is authoritative; <c>partial</c>
        /// = at least one repo group was skipped (its <c>git worktree list</c>
        /// failed — see <c>skippedGroups</c>), so <c>count</c> is a lower bound;
        /// <c>unavailable</c> = janitor missing or the scan threw, <c>count=0</c>
        /// is NOT a "no strands" signal. This is the whole point of the feature —
        /// a silently-wrong zero is the failure mode it exists to prevent.</para>
        /// </summary>
        [HttpGet("stranded")]
        public async Task<IActionResult> GetStrandedDirs()
        {
            var janitor = _broker?.WorktreeJanitor;
            if (janitor == null)
            {
                // 'unavailable' — NOT a healthy-looking count=0 (Adversary HIGH).
                return Ok(new { status = "unavailable", count = 0, skippedGroups = 0, dirs = Array.Empty<string>() });
            }

            try
            {
                var scan = await janitor.ScanStrandedDirsAsync().ConfigureAwait(false);
                return Ok(new
                {
                    status = scan.Complete ? "ok" : "partial",
                    count = scan.Dirs.Count,
                    skippedGroups = scan.SkippedGroups,
                    dirs = scan.Dirs,
                });
            }
            catch (Exception ex)
            {
                // Best-effort scan: a transient git/DB failure surfaces as
                // 'unavailable', never as a healthy-looking zero, and never as a 500
                // (honours the documented contract; Debugger MEDIUM).
                System.Diagnostics.Debug.WriteLine($"[WorktreesController] stranded scan failed: {ex.Message}");
                return Ok(new { status = "unavailable", count = 0, skippedGroups = 0, dirs = Array.Empty<string>() });
            }
        }
    }
}
