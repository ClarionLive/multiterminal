using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/tasks/{taskId}/reports")]
    public class TaskReportsController : ControllerBase
    {
        private readonly TaskDatabase _taskDb;
        private readonly MessageBroker _broker;

        public TaskReportsController(TaskDatabase taskDb, MessageBroker broker)
        {
            _taskDb = taskDb;
            _broker = broker;
        }

        /// <summary>
        /// GET /api/tasks/{taskId}/reports — List reports for a task (metadata only, no content)
        /// </summary>
        [HttpGet]
        public IActionResult GetReports(string taskId, [FromQuery] string agentName = null, [FromQuery] int limit = 50)
        {
            var reports = _taskDb.GetTaskReports(taskId, agentName, limit);
            return Ok(new { taskId, count = reports.Count, reports });
        }

        /// <summary>
        /// GET /api/tasks/{taskId}/reports/{reportId} — Get full report content
        /// </summary>
        [HttpGet("{reportId}")]
        public IActionResult GetReport(string taskId, string reportId)
        {
            var report = _taskDb.GetTaskReport(reportId);
            if (report == null)
                return NotFound(new { error = "Report not found" });

            if (report["task_id"]?.ToString() != taskId)
                return NotFound(new { error = "Report not found for this task" });

            return Ok(report);
        }

        /// <summary>
        /// POST /api/tasks/{taskId}/reports — Save a new agent report
        /// </summary>
        [HttpPost]
        public IActionResult SaveReport(string taskId, [FromBody] SaveReportRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body is required" });

            if (string.IsNullOrWhiteSpace(request.AgentName))
                return BadRequest(new { error = "agentName is required" });

            if (string.IsNullOrWhiteSpace(request.ReportContent))
                return BadRequest(new { error = "reportContent is required" });

            var id = request.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8);

            _taskDb.SaveTaskReport(
                id,
                taskId,
                request.InvocationId,
                request.AgentName,
                request.ReportType ?? "html",
                request.ReportContent,
                request.Verdict,
                request.Score,
                request.CreatedBy);

            // Notify UI so kanban card badges refresh
            _broker.NotifyReportSaved(taskId, id, request.AgentName, request.Verdict);

            return Ok(new { success = true, reportId = id });
        }
    }

    public class SaveReportRequest
    {
        public string Id { get; set; }
        public string AgentName { get; set; }
        public string InvocationId { get; set; }
        public string ReportType { get; set; }
        public string ReportContent { get; set; }
        public string Verdict { get; set; }
        public int? Score { get; set; }
        public string CreatedBy { get; set; }
    }
}
