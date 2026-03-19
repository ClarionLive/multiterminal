using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/agents")]
    public class AgentStatsController : ControllerBase
    {
        private readonly TaskDatabase _taskDb;

        public AgentStatsController(TaskDatabase taskDb)
        {
            _taskDb = taskDb;
        }

        /// <summary>
        /// GET /api/agents/stats — Aggregated performance stats per agent
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            var stats = _taskDb.GetAgentStats();
            return Ok(stats);
        }

        /// <summary>
        /// GET /api/agents/invocations — List recent agent invocations with optional filters
        /// </summary>
        [HttpGet("invocations")]
        public IActionResult GetInvocations(
            [FromQuery] string agentName = null,
            [FromQuery] string taskId = null,
            [FromQuery] int limit = 50)
        {
            if (limit < 1) limit = 50;
            if (limit > 500) limit = 500;
            var invocations = _taskDb.GetAgentInvocations(agentName, taskId, limit);
            return Ok(invocations);
        }

        /// <summary>
        /// POST /api/agents/invocations — Record a new agent invocation
        /// </summary>
        [HttpPost("invocations")]
        public IActionResult RecordInvocation([FromBody] RecordInvocationRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body is required" });
            if (string.IsNullOrWhiteSpace(request.AgentName))
                return BadRequest(new { error = "agentName is required" });

            var id = request.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            var invokedAt = request.InvokedAt ?? DateTime.UtcNow;

            _taskDb.SaveAgentInvocation(
                id,
                request.AgentName,
                request.TaskId,
                request.InvokedBy,
                request.ModelUsed,
                request.Verdict,
                request.Score,
                request.FindingsCount ?? 0,
                request.DurationMs,
                invokedAt,
                request.CompletedAt,
                request.ReportSummary);

            return Ok(new { success = true, invocationId = id });
        }
    }

    public class RecordInvocationRequest
    {
        public string Id { get; set; }
        public string AgentName { get; set; }
        public string TaskId { get; set; }
        public string InvokedBy { get; set; }
        public string ModelUsed { get; set; }
        public string Verdict { get; set; }
        public int? Score { get; set; }
        public int? FindingsCount { get; set; }
        public long? DurationMs { get; set; }
        public DateTime? InvokedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string ReportSummary { get; set; }
    }
}
