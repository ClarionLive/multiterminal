using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/tasks/{taskId}/reports")]
    // tasks-panel.html fetch()es the two GET report endpoints below. It used to be served over
    // file:// (Origin "null"), which forced a scoped null-tolerant CORS carve-out here; task
    // f9697aac migrated it onto the virtual-host origin (http://mt-panels.local), which the strict
    // default CORS policy now allowlists, so these actions no longer need any [EnableCors] override.
    // Every controller (incl. this one) is on the single strict default policy. See RestCorsOriginPolicy.
    public class TaskReportsController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public TaskReportsController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// GET /api/tasks/{taskId}/reports — List reports for a task (metadata only, no content)
        /// </summary>
        [HttpGet]
        public IActionResult GetReports(string taskId, [FromQuery] string agentName = null, [FromQuery] int limit = 50)
        {
            var reports = _broker.GetTaskReports(taskId, agentName, limit);
            return Ok(new { taskId, count = reports.Count, reports });
        }

        /// <summary>
        /// GET /api/tasks/{taskId}/reports/{reportId} — Get full report content
        /// </summary>
        [HttpGet("{reportId}")]
        public IActionResult GetReport(string taskId, string reportId)
        {
            var report = _broker.GetTaskReport(reportId);
            if (report == null)
                return Problem(detail: "Report not found", statusCode: 404);

            if (report["task_id"]?.ToString() != taskId)
                return Problem(detail: "Report not found for this task", statusCode: 404);

            return Ok(report);
        }

        /// <summary>
        /// POST /api/tasks/{taskId}/reports — Save a new agent report
        /// </summary>
        [HttpPost]
        public IActionResult SaveReport(string taskId, [FromBody] SaveReportRequest request)
        {
            if (request == null)
                return Problem(detail: "Request body is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(request.AgentName))
                return Problem(detail: "agentName is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(request.ReportContent))
                return Problem(detail: "reportContent is required", statusCode: 400);

            var id = request.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8);

            // Broker persists AND fires ReportSaved so kanban card badges refresh.
            _broker.SaveTaskReport(
                id,
                taskId,
                request.InvocationId,
                request.AgentName,
                request.ReportType ?? "html",
                request.ReportContent,
                request.Verdict,
                request.Score,
                request.CreatedBy);

            return Ok(new { reportId = id });
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
