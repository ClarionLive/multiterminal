using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Project notes for agents (task 5d94ac91): the tabs of the HUD Notes pane. Agents add notes for
    /// the Owner (restart steps, test steps) instead of the Owner copying them out of chat by hand.
    /// Writes go through <see cref="MessageBroker.WriteAgentProjectNote"/>, which never modifies a tab
    /// the Owner created. Its own route (not under <c>api/projects</c>) so nothing can collide with
    /// <c>api/projects/{projectId}</c>.
    /// </summary>
    [ApiController]
    [Route("api/project-notes")]
    public class ProjectNotesController : ControllerBase
    {
        internal const int MaxTabNameLength = 80;
        internal const int MaxContentLength = TaskDatabase.AgentNoteMaxChars;

        private readonly MessageBroker _broker;

        public ProjectNotesController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// GET /api/project-notes?projectId=X (or projectPath=Y): every tab with its authorship, size, pin and
        /// category, without the content, plus the shared category list (task 9a4a9d5e). A null createdBy
        /// means the Owner wrote the tab.
        /// </summary>
        [HttpGet]
        public IActionResult ListNotes([FromQuery] string projectId, [FromQuery] string projectPath)
        {
            var (key, error) = ResolveKey(projectId, projectPath);
            if (error != null) return error;

            var tabs = _broker.TaskDb.GetProjectNoteTabDetails(key).Select(t => new
            {
                name = t.Name,
                isDefault = t.IsDefault,
                order = t.Order,
                createdBy = t.CreatedBy,
                updatedBy = t.UpdatedBy,
                updatedAt = t.UpdatedAt,
                length = t.Content?.Length ?? 0,
                category = t.Category,
                pinned = t.IsPinned,
            }).ToList();
            var categories = _broker.TaskDb.ListNoteCategories().Select(c => c.Name).ToList();
            return Ok(new { projectKey = key, count = tabs.Count, tabs, categories });
        }

        /// <summary>GET /api/project-notes/tab?projectId=X&amp;tabName=Y: one tab including its content.</summary>
        [HttpGet("tab")]
        public IActionResult GetNote([FromQuery] string projectId, [FromQuery] string projectPath, [FromQuery] string tabName)
        {
            if (string.IsNullOrWhiteSpace(tabName))
                return Problem(detail: "tabName is required", statusCode: 400);
            var (key, error) = ResolveKey(projectId, projectPath);
            if (error != null) return error;

            var tabs = _broker.TaskDb.GetProjectNoteTabDetails(key);
            string wanted = tabName.Trim();
            var tab = tabs.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.Ordinal))
                      ?? tabs.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
                return Problem(detail: $"No note tab named '{wanted}' in this project", statusCode: 404);

            return Ok(new
            {
                projectKey = key,
                name = tab.Name,
                isDefault = tab.IsDefault,
                createdBy = tab.CreatedBy,
                updatedBy = tab.UpdatedBy,
                updatedAt = tab.UpdatedAt,
                category = tab.Category,
                content = tab.Content,
            });
        }

        /// <summary>
        /// POST /api/project-notes: an agent writes a note.
        /// Body: { projectId | projectPath, tabName, content, author (required), mode: new|append|replace,
        /// category (optional) }.
        /// 200 { outcome, tabName, projectKey, category, categoryNote } (tabName may differ from the request
        /// when "new" collided with someone else's tab; categoryNote says why a requested category was not
        /// used); 409 with the reason when the ownership rules refuse the write.
        /// </summary>
        [HttpPost]
        public IActionResult WriteNote([FromBody] WriteProjectNoteRequest request)
        {
            if (request == null) return Problem(detail: "A request body is required", statusCode: 400);
            string tabName = request.TabName?.Trim();
            if (string.IsNullOrEmpty(tabName))
                return Problem(detail: "tabName is required", statusCode: 400);
            if (tabName.Length > MaxTabNameLength || tabName.Any(char.IsControl))
                return Problem(detail: $"tabName must be one line of at most {MaxTabNameLength} characters", statusCode: 400);
            if (string.IsNullOrWhiteSpace(request.Author))
                return Problem(detail: "author (the writing agent's name) is required", statusCode: 400);
            if ((request.Content ?? "").Length > MaxContentLength)
                return Problem(detail: $"content is limited to {MaxContentLength} characters", statusCode: 400);
            if (!TryParseMode(request.Mode, out var mode))
                return Problem(detail: $"mode must be new, append or replace (got '{request.Mode}')", statusCode: 400);
            // Optional, and never refused for being unknown: it lands in General and categoryNote says so.
            // Only a malformed value (too long, control characters) is a bad request.
            string category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
            if (category != null && !TaskDatabase.IsValidNoteCategoryName(category))
                return Problem(detail: $"category must be one line of at most {TaskDatabase.NoteCategoryMaxLength} characters", statusCode: 400);

            var (key, error) = ResolveKey(request.ProjectId, request.ProjectPath);
            if (error != null) return error;

            var result = _broker.WriteAgentProjectNote(key, tabName, request.Content ?? "", request.Author, mode, category);
            if (!result.Succeeded)
                return Problem(detail: result.Reason, statusCode: 409);

            return Ok(new
            {
                outcome = result.Outcome.ToString().ToLowerInvariant(),
                tabName = result.TabName,
                projectKey = key,
                category = result.Category,
                categoryNote = result.CategoryNote,
            });
        }

        internal static bool TryParseMode(string value, out AgentNoteWriteMode mode)
        {
            mode = AgentNoteWriteMode.New;
            if (string.IsNullOrWhiteSpace(value)) return true;
            switch (value.Trim().ToLowerInvariant())
            {
                case "new": mode = AgentNoteWriteMode.New; return true;
                case "append": mode = AgentNoteWriteMode.Append; return true;
                case "replace": mode = AgentNoteWriteMode.Replace; return true;
                default: return false;
            }
        }

        /// <summary>
        /// The note-tab key, resolved exactly as the Notes pane resolves it (<see cref="ProjectNoteKeys"/>).
        /// A projectId must name a registered project. A projectPath maps to its registered project when
        /// it matches one; a path inside a task worktree is refused rather than keyed raw, because the
        /// pane never shows worktree-keyed notes; an unregistered folder keys raw, as the pane does.
        /// </summary>
        private (string Key, IActionResult Error) ResolveKey(string projectId, string projectPath)
        {
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                string byId = ProjectNoteKeys.TryResolve(_broker.ProjectDatabase, projectId.Trim(), null);
                return byId != null
                    ? (byId, null)
                    : (null, Problem(detail: $"Project '{projectId}' not found", statusCode: 404));
            }

            if (string.IsNullOrWhiteSpace(projectPath))
                return (null, Problem(detail: "projectId or projectPath is required", statusCode: 400));
            if (!System.IO.Path.IsPathRooted(projectPath))
                return (null, Problem(detail: "projectPath must be an absolute path", statusCode: 400));

            string byPath = ProjectNoteKeys.TryResolve(_broker.ProjectDatabase, null, projectPath);
            if (byPath != null) return (byPath, null);
            if (WorktreeLayout.LooksLikeWorktreePath(projectPath))
                return (null, Problem(detail: "projectPath is a task worktree; pass the projectId or the project's root folder", statusCode: 400));
            return (projectPath, null);
        }
    }

    public sealed class WriteProjectNoteRequest
    {
        public string ProjectId { get; set; }

        public string ProjectPath { get; set; }

        public string TabName { get; set; }

        public string Content { get; set; }

        public string Author { get; set; }

        public string Mode { get; set; }

        /// <summary>Optional category from the shared list (task 9a4a9d5e); unknown lands in General.</summary>
        public string Category { get; set; }
    }
}
