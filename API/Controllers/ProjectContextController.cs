using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Provides a single "everything an agent needs" context endpoint for a project.
    /// GET /api/projects/{id}/context returns project details plus all associated
    /// agents, MCP servers, specialist agents, paths, prompts, and skills.
    /// </summary>
    [ApiController]
    [Route("api/projects")]
    public class ProjectContextController : ControllerBase
    {
        private readonly ProjectContextService _contextService;

        public ProjectContextController(ProjectContextService contextService)
        {
            _contextService = contextService;
        }

        /// <summary>
        /// Get the full project context for an agent.
        /// Returns the project record joined with all association tables in one response.
        /// </summary>
        [HttpGet("{projectId}/context")]
        public IActionResult GetProjectContext(string projectId)
        {
            var context = _contextService.GetProjectContext(projectId);

            if (context == null)
                return NotFound(new { error = $"Project '{projectId}' not found" });

            return Ok(context);
        }

        /// <summary>
        /// List all project contexts.
        /// Returns all projects with their full association data.
        /// </summary>
        [HttpGet("contexts")]
        public IActionResult GetAllProjectContexts()
        {
            var contexts = _contextService.GetAllProjectContexts();
            return Ok(new { count = contexts.Count, contexts });
        }
    }
}
