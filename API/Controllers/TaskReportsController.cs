using System;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/tasks/{taskId}/reports")]
    // tasks-panel.html (served from the per-process panel virtual host) fetch()es the two GET report
    // endpoints below — the ONLY browser-read surface on :5050. They opt into the scoped PanelReads
    // CORS policy (task f9697aac) so the panel origin can read them; every OTHER controller stays on
    // the deny-all default and exposes no ACAO to any browser origin (least privilege — the secret
    // GETs have no browser consumer). SaveReport (POST) is agent-only (HttpClient, no Origin) and is
    // deliberately NOT annotated, so it too falls to the deny-all default. See RestCorsOriginPolicy.
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
        [EnableCors(RestCorsOriginPolicy.PanelReadPolicyName)] // panel virtual-host origin reads this
        public IActionResult GetReports(string taskId, [FromQuery] string agentName = null, [FromQuery] int limit = 50)
        {
            var reports = _broker.GetTaskReports(taskId, agentName, limit);
            return Ok(new { taskId, count = reports.Count, reports });
        }

        /// <summary>
        /// GET /api/tasks/{taskId}/reports/{reportId} — Get full report content
        /// </summary>
        [HttpGet("{reportId}")]
        [EnableCors(RestCorsOriginPolicy.PanelReadPolicyName)] // panel virtual-host origin reads this
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
