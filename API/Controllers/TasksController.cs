using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/tasks")]
    public class TasksController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly TaskDatabase _taskDb;

        public TasksController(MessageBroker broker, TaskDatabase taskDb)
        {
            _broker = broker;
            _taskDb = taskDb;
        }

        /// <summary>
        /// List all tasks, optionally filtered by status
        /// </summary>
        [HttpGet]
        public IActionResult ListTasks([FromQuery] string status = "all")
        {
            var allTasks = _broker.GetTasks(projectId: null);

            if (status != "all")
            {
                allTasks = allTasks.Where(t => t.Status == status).ToList();
            }

            return Ok(allTasks);
        }

        /// <summary>
        /// Get a specific task by ID
        /// </summary>
        [HttpGet("{taskId}")]
        public IActionResult GetTask(string taskId)
        {
            var task = _taskDb.GetTask(taskId);
            if (task == null)
                return NotFound(new { error = $"Task {taskId} not found" });

            return Ok(task);
        }

        /// <summary>
        /// Get the active in-progress task for a specific agent.
        /// Returns full task detail with checklist summary, same format as /detail.
        /// </summary>
        [HttpGet("active/{agentName}")]
        public IActionResult GetMyActiveTask(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return BadRequest(new { error = "agentName is required" });

            var task = _broker.GetMyActiveTask(agentName);
            if (task == null)
                return Ok(new { task = (object)null, message = $"No active task for {agentName}" });

            // Load helpers
            var helpers = _taskDb.LoadTaskHelpers(task.Id);
            task.Helpers = helpers.ConvertAll(h => h.HelperName);

            // Parse and normalize checklist
            var checklist = task.GetChecklist();

            // Build checklist summary
            var totalItems = checklist.Count;
            var doneItems = checklist.Count(i => i.Status == "done");
            var codingItems = checklist.Count(i => i.Status == "coding");
            var testingItems = checklist.Count(i => i.Status == "testing");
            var pendingItems = checklist.Count(i => i.Status == "pending");

            return Ok(new
            {
                task,
                checklistSummary = new
                {
                    total = totalItems,
                    done = doneItems,
                    coding = codingItems,
                    testing = testingItems,
                    pending = pendingItems
                },
                checklist
            });
        }

        /// <summary>
        /// Create a new task
        /// </summary>
        [HttpPost]
        public IActionResult CreateTask([FromBody] CreateTaskRequest request)
        {
            var result = _broker.CreateTask(
                request.Title,
                request.Description,
                request.CreatedBy,
                request.Status ?? "todo",
                request.Priority ?? "normal",
                request.ProjectId
            );

            if (!result.Success)
                return BadRequest(new { error = result.Error });

            var task = _taskDb.GetTask(result.TaskId);
            return Ok(new { taskId = result.TaskId, task });
        }

        /// <summary>
        /// Update task status
        /// </summary>
        [HttpPatch("{taskId}/status")]
        public IActionResult UpdateStatus(string taskId, [FromBody] UpdateStatusRequest request)
        {
            var result = _broker.UpdateTaskStatus(taskId, request.Status);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Delete a task
        /// </summary>
        [HttpDelete("{taskId}")]
        public IActionResult DeleteTask(string taskId, [FromQuery] string deletedBy)
        {
            _broker.DeleteTask(taskId, deletedBy);
            return Ok(new { success = true });
        }

        /// <summary>
        /// Assign/claim a task
        /// </summary>
        [HttpPost("{taskId}/assign")]
        public IActionResult AssignTask(string taskId, [FromBody] AssignTaskRequest request)
        {
            var result = _broker.ClaimTask(taskId, request.Assignee);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Add a helper to a task
        /// </summary>
        [HttpPost("{taskId}/helpers")]
        public async Task<IActionResult> AddHelper(string taskId, [FromBody] AddHelperRequest request)
        {
            var result = await _broker.AddHelper(taskId, request.Helper, request.AddedBy ?? "API");
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true, helperCount = result.HelperCount });
        }

        /// <summary>
        /// Remove a helper from a task
        /// </summary>
        [HttpDelete("{taskId}/helpers/{helperName}")]
        public IActionResult RemoveHelper(string taskId, string helperName)
        {
            var result = _broker.RemoveHelper(taskId, helperName);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true, helperCount = result.HelperCount });
        }
        // =============================================
        // Kanban Workflow: Enhanced Checklist Endpoints
        // =============================================

        /// <summary>
        /// Replace all checklist items (for setting up or editing the checklist).
        /// </summary>
        [HttpPatch("{taskId}/checklist")]
        public IActionResult UpdateChecklist(string taskId, [FromBody] UpdateChecklistRequest request)
        {
            var result = _broker.UpdateTaskChecklist(taskId, request.ChecklistJson);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Transition a checklist item status with notes (state machine enforced).
        /// </summary>
        [HttpPost("{taskId}/checklist/{itemIndex}/transition")]
        public IActionResult TransitionChecklistItem(string taskId, int itemIndex, [FromBody] TransitionChecklistRequest request)
        {
            var result = _broker.TransitionChecklistItem(taskId, itemIndex, request.NewStatus, request.Notes, request.UpdatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new
            {
                success = true,
                itemName = result.ItemName,
                previousStatus = result.PreviousStatus,
                newStatus = result.NewStatus,
                cycleCount = result.CycleCount,
                escalationTriggered = result.EscalationTriggered
            });
        }

        /// <summary>
        /// Assign a checklist item to a specific agent.
        /// </summary>
        [HttpPost("{taskId}/checklist/{itemIndex}/assign")]
        public IActionResult AssignChecklistItem(string taskId, int itemIndex, [FromBody] AssignChecklistRequest request)
        {
            var result = _broker.AssignChecklistItem(taskId, itemIndex, request.Assignee);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Update continuation notes for session handoff.
        /// </summary>
        [HttpPatch("{taskId}/continuation")]
        public IActionResult UpdateContinuation(string taskId, [FromBody] UpdateContinuationRequest request)
        {
            var result = _broker.UpdateTaskContinuation(taskId, request.ContinuationNotes, request.UpdatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Update task plan.
        /// </summary>
        [HttpPatch("{taskId}/plan")]
        public IActionResult UpdatePlan(string taskId, [FromBody] UpdatePlanRequest request)
        {
            var result = _broker.UpdateTaskPlan(taskId, request.Plan, request.UpdatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Update task implementation summary and/or test results.
        /// </summary>
        [HttpPatch("{taskId}/summary")]
        public IActionResult UpdateSummary(string taskId, [FromBody] UpdateSummaryRequest request)
        {
            var result = _broker.UpdateTaskSummaryFields(taskId, request.ImplementationSummary, request.TestResults, request.UpdatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Set a task as active, auto-pausing other active tasks for the same assignee.
        /// </summary>
        [HttpPost("{taskId}/activate")]
        public IActionResult SetTaskActive(string taskId, [FromBody] SetActiveRequest request)
        {
            var result = _broker.SetTaskActive(taskId, request.UpdatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new
            {
                success = true,
                pausedTaskIds = result.PausedTaskIds,
                pausedTaskTitles = result.PausedTaskTitles
            });
        }

        /// <summary>
        /// Get tasks relevant to a specific agent: their assigned tasks + unassigned todo tasks.
        /// Returns a compact summary (no full descriptions, plans, or checklist JSON).
        /// </summary>
        [HttpGet("pickable/{agentName}")]
        public IActionResult GetPickableTasks(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return BadRequest(new { error = "agentName is required" });

            var allTasks = _broker.GetTasks(projectId: null);

            var results = new List<object>();
            foreach (var task in allTasks)
            {
                // Skip done and suggestion tasks
                if (task.Status == "done" || task.Status == "suggestion")
                    continue;

                bool isAssignedToMe = task.Assignee != null &&
                    task.Assignee.Equals(agentName, StringComparison.OrdinalIgnoreCase);
                bool isUnassignedTodo = task.Status == "todo" &&
                    string.IsNullOrEmpty(task.Assignee);
                bool isHelper = task.Helpers != null &&
                    task.Helpers.Any(h => h.Equals(agentName, StringComparison.OrdinalIgnoreCase));

                if (!isAssignedToMe && !isUnassignedTodo && !isHelper)
                    continue;

                // Build compact checklist summary
                var checklist = task.GetChecklist();
                var checklistSummary = checklist.Count > 0 ? new
                {
                    total = checklist.Count,
                    done = checklist.Count(i => i.Status == "done"),
                    coding = checklist.Count(i => i.Status == "coding"),
                    testing = checklist.Count(i => i.Status == "testing"),
                    pending = checklist.Count(i => i.Status == "pending")
                } : null;

                string relation = isAssignedToMe ? "assigned" : isHelper ? "helper" : "available";

                results.Add(new
                {
                    id = task.Id,
                    title = task.Title,
                    status = task.Status,
                    subStatus = task.SubStatus,
                    assignee = task.Assignee,
                    priority = task.Priority ?? "normal",
                    relation,
                    checklistSummary
                });
            }

            return Ok(new { agentName, tasks = results, count = results.Count });
        }

        /// <summary>
        /// Get full task detail with checklist and notes history.
        /// </summary>
        [HttpGet("{taskId}/detail")]
        public IActionResult GetTaskDetail(string taskId)
        {
            var task = _taskDb.GetTask(taskId);
            if (task == null)
                return NotFound(new { error = $"Task {taskId} not found" });

            // Load helpers
            var helpers = _taskDb.LoadTaskHelpers(taskId);
            task.Helpers = helpers.ConvertAll(h => h.HelperName);

            // Parse and normalize checklist
            var checklist = task.GetChecklist();

            // Build checklist summary
            var totalItems = checklist.Count;
            var doneItems = checklist.Count(i => i.Status == "done");
            var codingItems = checklist.Count(i => i.Status == "coding");
            var testingItems = checklist.Count(i => i.Status == "testing");
            var pendingItems = checklist.Count(i => i.Status == "pending");

            // Load relationships
            var relationships = _broker.GetRelationships(taskId);
            var relationshipList = relationships.Success ? relationships.Relationships : new List<TaskRelationship>();

            // Load file links
            var fileLinks = _broker.GetTaskFiles(taskId);
            var fileLinkList = fileLinks.Success ? fileLinks.Files : new List<TaskFileLink>();

            return Ok(new
            {
                task,
                checklistSummary = new
                {
                    total = totalItems,
                    done = doneItems,
                    coding = codingItems,
                    testing = testingItems,
                    pending = pendingItems
                },
                checklist,
                relationships = relationshipList,
                fileLinks = fileLinkList
            });
        }

        // =============================================
        // Relationship Endpoints
        // =============================================

        /// <summary>
        /// Add a relationship between two tasks.
        /// </summary>
        [HttpPost("{taskId}/relationships")]
        public IActionResult AddRelationship(string taskId, [FromBody] AddRelationshipRequest request)
        {
            var result = _broker.AddRelationship(taskId, request.TargetTaskId, request.Type, request.CreatedBy);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Remove a relationship between two tasks (removes both directions).
        /// </summary>
        [HttpDelete("{taskId}/relationships/{relatedTaskId}")]
        public IActionResult RemoveRelationship(string taskId, string relatedTaskId)
        {
            var result = _broker.RemoveRelationship(taskId, relatedTaskId);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Get all relationships for a task (from this task's perspective).
        /// </summary>
        [HttpGet("{taskId}/relationships")]
        public IActionResult GetRelationships(string taskId)
        {
            var result = _broker.GetRelationships(taskId);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true, relationships = result.Relationships });
        }

        // =============================================
        // File Link Endpoints
        // =============================================

        /// <summary>
        /// Link a file to a task.
        /// </summary>
        [HttpPost("{taskId}/files")]
        public IActionResult LinkFile(string taskId, [FromBody] LinkFileRequest request)
        {
            var result = _broker.LinkFile(taskId, request.FilePath, request.Description, request.LineStart, request.LineEnd, request.AddedBy, request.ChecklistItemIndex);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true, fileCount = result.FileCount });
        }

        /// <summary>
        /// Unlink a file from a task.
        /// </summary>
        [HttpPost("{taskId}/files/unlink")]
        public IActionResult UnlinkFile(string taskId, [FromBody] UnlinkFileRequest request)
        {
            var result = _broker.UnlinkFile(taskId, request.FilePath);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }

        /// <summary>
        /// Get all files linked to a task.
        /// </summary>
        [HttpGet("{taskId}/files")]
        public IActionResult GetTaskFiles(string taskId)
        {
            var result = _broker.GetTaskFiles(taskId);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true, files = result.Files });
        }

        // =============================================
        // Inbox Endpoints
        // =============================================

        /// <summary>
        /// Get inbox messages for a user.
        /// </summary>
        [HttpGet("inbox/{userId}")]
        public IActionResult GetInbox(string userId, [FromQuery] bool unreadOnly = false, [FromQuery] int limit = 50)
        {
            var result = _broker.GetInbox(userId, unreadOnly, limit);
            return Ok(result);
        }

        /// <summary>
        /// Mark a single inbox message as read.
        /// </summary>
        [HttpPost("inbox/{messageId}/read")]
        public IActionResult MarkInboxRead(string messageId)
        {
            _broker.MarkInboxRead(messageId);
            return Ok(new { success = true });
        }

        /// <summary>
        /// Mark all inbox messages as read for a user.
        /// </summary>
        [HttpPost("inbox/{userId}/read-all")]
        public IActionResult MarkAllInboxRead(string userId)
        {
            _broker.MarkAllInboxRead(userId);
            return Ok(new { success = true });
        }

        /// <summary>
        /// Reply to an inbox message.
        /// </summary>
        [HttpPost("inbox/{messageId}/reply")]
        public IActionResult ReplyToInbox(string messageId, [FromBody] ReplyToInboxRequest request)
        {
            _broker.ReplyToInbox(messageId, request.ReplyText);
            return Ok(new { success = true });
        }

        /// <summary>
        /// Get unread inbox message count for a user.
        /// </summary>
        [HttpGet("inbox/{userId}/unread-count")]
        public IActionResult GetUnreadCount(string userId)
        {
            var result = _broker.GetInbox(userId, unreadOnly: true);
            return Ok(new { count = result.UnreadCount });
        }

        // =============================================
        // Attachment Endpoints
        // =============================================

        /// <summary>
        /// Get attachments for a task, optionally filtered by checklist item index.
        /// </summary>
        [HttpGet("{taskId}/attachments")]
        public IActionResult GetAttachments(string taskId, [FromQuery] int? itemIndex = null)
        {
            var attachments = _broker.GetAttachments(taskId, itemIndex);
            return Ok(attachments);
        }

        /// <summary>
        /// Get attachment image file by attachment ID. Returns the raw binary image data.
        /// </summary>
        [HttpGet("attachments/{attachmentId}/image")]
        public IActionResult GetAttachmentImage(string attachmentId)
        {
            var data = _broker.GetAttachmentData(attachmentId);
            if (data == null)
                return NotFound(new { error = "Attachment not found" });

            return File(data.Value.Data, data.Value.MimeType, data.Value.FileName);
        }

        /// <summary>
        /// Get attachment image as base64-encoded string with metadata.
        /// </summary>
        [HttpGet("attachments/{attachmentId}/base64")]
        public IActionResult GetAttachmentBase64(string attachmentId)
        {
            var data = _broker.GetAttachmentData(attachmentId);
            if (data == null)
                return NotFound(new { error = "Attachment not found" });

            return Ok(new
            {
                base64 = Convert.ToBase64String(data.Value.Data),
                mimeType = data.Value.MimeType,
                fileName = data.Value.FileName
            });
        }

        /// <summary>
        /// Add an image attachment to a task or checklist item.
        /// </summary>
        [HttpPost("{taskId}/attachments")]
        public IActionResult AddAttachment(string taskId, [FromBody] AddAttachmentRequest request)
        {
            byte[] imageBytes;
            try
            {
                imageBytes = Convert.FromBase64String(request.Base64Data);
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Invalid base64 data" });
            }

            var result = _broker.AddAttachment(
                taskId,
                request.ChecklistItemIndex,
                request.FileName,
                request.MimeType,
                imageBytes,
                request.AddedBy ?? "api");

            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { attachmentId = result.AttachmentId });
        }

        /// <summary>
        /// Delete an attachment by ID.
        /// </summary>
        [HttpDelete("attachments/{attachmentId}")]
        public IActionResult DeleteAttachment(string attachmentId)
        {
            var result = _broker.DeleteAttachment(attachmentId);
            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new { success = true });
        }
    }

    // Request models
    public class CreateTaskRequest
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string CreatedBy { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public string ProjectId { get; set; }
    }

    public class UpdateStatusRequest
    {
        public string Status { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class AssignTaskRequest
    {
        public string Assignee { get; set; }
    }

    public class AddHelperRequest
    {
        public string Helper { get; set; }
        public string AddedBy { get; set; }
    }

    public class UpdateChecklistRequest
    {
        public string ChecklistJson { get; set; }
    }

    public class TransitionChecklistRequest
    {
        public string NewStatus { get; set; }
        public string Notes { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class AssignChecklistRequest
    {
        public string Assignee { get; set; }
    }

    public class UpdateContinuationRequest
    {
        public string ContinuationNotes { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class UpdatePlanRequest
    {
        public string Plan { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class UpdateSummaryRequest
    {
        public string ImplementationSummary { get; set; }
        public string TestResults { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class SetActiveRequest
    {
        public string UpdatedBy { get; set; }
    }

    public class ReplyToInboxRequest
    {
        public string ReplyText { get; set; }
    }

    public class AddAttachmentRequest
    {
        public int ChecklistItemIndex { get; set; }
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public string Base64Data { get; set; }
        public string AddedBy { get; set; }
    }

    public class AddRelationshipRequest
    {
        public string TargetTaskId { get; set; }
        public string Type { get; set; }
        public string CreatedBy { get; set; }
    }

    public class LinkFileRequest
    {
        public string FilePath { get; set; }
        public string Description { get; set; }
        public int? LineStart { get; set; }
        public int? LineEnd { get; set; }
        public string AddedBy { get; set; }
        // Optional: 0-based checklist item index to scope the link to a single
        // item. NULL (default) means task-scoped — applies to all items.
        public int? ChecklistItemIndex { get; set; }
    }

    public class UnlinkFileRequest
    {
        public string FilePath { get; set; }
    }
}

