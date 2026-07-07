using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// PWA verb-compatibility shims (task ca6c5344, pipeline Run-1 cross-model HIGH).
    /// Mounting MT's real controllers exposes their NATIVE verbs, but the shipped phone
    /// client predates them on one route: the PWA calls <c>PUT /api/tasks/{id}/status</c>
    /// (wwwroot/js/tasks.js) while MT's TasksController declares <c>PATCH</c>. Without a
    /// shim the phone's task-status changes 405 even though listing/detail work — a silent
    /// partial-success. This maps the PWA's PUT onto the same broker call the PATCH handler
    /// uses (TasksController.UpdateStatus), so behaviour + response shape are identical and
    /// the PATCH route still works for any caller that uses it. (All other PWA verbs on
    /// mounted routes — assign POST, create POST, digest/regenerate POST, remote-mode POST —
    /// already match their controllers; status is the only mismatch.)
    /// </summary>
    public static class GatewayCompatEndpoints
    {
        public static void MapMultiRemoteCompatEndpoints(this WebApplication app)
        {
            app.MapMethods("/api/tasks/{taskId}/status", new[] { "PUT" },
                (string taskId, UpdateStatusRequest request, MessageBroker broker) =>
            {
                var result = broker.UpdateTaskStatus(taskId, request.Status);
                if (!result.Success)
                    return Results.Problem(detail: result.Error, statusCode: 400);
                // Non-empty body matching TasksController.UpdateStatus's { status } shape
                // (7ce19175): the phone PWA's api() calls res.json() on 2xx, so an empty ack
                // would throw and be read as failure. Shape stays identical to the native route.
                return Results.Ok(new { status = request.Status });
            });
        }
    }
}
