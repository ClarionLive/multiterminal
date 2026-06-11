using Microsoft.AspNetCore.Mvc;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api")]
    public class ToolsController : ControllerBase
    {
        /// <summary>
        /// List all available API endpoints with descriptions
        /// </summary>
        [HttpGet("tools")]
        public IActionResult ListTools()
        {
            var endpoints = new[]
            {
                new { method = "GET", path = "/", description = "Health check - API is running" },
                new { method = "GET", path = "/health", description = "Health check with port info" },
                new { method = "GET", path = "/api/tools", description = "List all available API endpoints (this endpoint)" },

                // Messaging endpoints
                new { method = "POST", path = "/api/messaging/register", description = "Register a terminal (name, docId)" },
                new { method = "POST", path = "/api/messaging/send", description = "Send message to another terminal (fromTerminalId, to, message)" },
                new { method = "POST", path = "/api/messaging/broadcast", description = "Broadcast message to all terminals (fromTerminalId, message)" },
                new { method = "GET", path = "/api/messaging/messages/{terminalId}", description = "Get messages for a terminal" },
                new { method = "GET", path = "/api/messaging/terminals", description = "List all registered terminals" },
                new { method = "POST", path = "/api/messaging/disconnect", description = "Disconnect a terminal by name (name)" },

                // Task endpoints
                new { method = "GET", path = "/api/tasks", description = "List tasks (query: ?status=all|todo|in_progress|done|suggestion)" },
                new { method = "GET", path = "/api/tasks/{taskId}", description = "Get a specific task by ID" },
                new { method = "POST", path = "/api/tasks", description = "Create a new task (title, description, createdBy, status, priority)" },
                new { method = "PATCH", path = "/api/tasks/{taskId}/status", description = "Update task status (status, updatedBy)" },
                new { method = "POST", path = "/api/tasks/{taskId}/assign", description = "Assign/claim a task (assignee)" },
                new { method = "POST", path = "/api/tasks/{taskId}/helpers", description = "Add a helper to a task (helper)" },
                new { method = "DELETE", path = "/api/tasks/{taskId}/helpers/{helperName}", description = "Remove a helper from a task" },
                new { method = "DELETE", path = "/api/tasks/{taskId}", description = "Delete a task (query: ?deletedBy=name)" },

                // Office Panel endpoints
                new { method = "POST", path = "/api/office/agents", description = "Notify agent spawned - triggers walk-in animation (name, spawnedBy)" },
                new { method = "DELETE", path = "/api/office/agents/{name}", description = "Notify agent departed - triggers exit animation" },
                new { method = "GET", path = "/api/office/agents", description = "List all active office agents" },

                // Agent Process endpoints
                new { method = "POST", path = "/api/spawn/agent", description = "Spawn a headless AgentProcess with conversation panel (agentName, workingDir, initialPrompt, spawnerName)" },

                // Debug endpoints
                new { method = "GET", path = "/api/debug/logs", description = "Get debug log entries (query: ?count=50&offset=0&source=MessageBroker&level=Info&search=delivery)" },
                new { method = "DELETE", path = "/api/debug/logs", description = "Clear all debug log entries" },
                new { method = "POST", path = "/api/debug/pause", description = "Pause debug logging (new entries silently discarded)" },
                new { method = "POST", path = "/api/debug/resume", description = "Resume debug logging" },
                new { method = "GET", path = "/api/debug/status", description = "Get debug log status (count, isPaused, maxCapacity)" }
            };

            return Ok(new
            {
                baseUrl = "http://localhost:5050",
                endpoints = endpoints
            });
        }
    }
}
