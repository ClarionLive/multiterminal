using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Full CRUD for projects and their associations (agents, MCP servers, specialists, paths, prompts, skills).
    /// Also provides the "everything an agent needs" /context endpoint.
    /// </summary>
    [ApiController]
    [Route("api/projects")]
    public class ProjectContextController : ControllerBase
    {
        private readonly ProjectContextService _contextService;
        private readonly ProjectDatabase _projectDb;

        public ProjectContextController(ProjectContextService contextService, ProjectDatabase projectDb)
        {
            _contextService = contextService;
            _projectDb = projectDb;
        }

        #region Projects

        /// <summary>
        /// GET /api/projects — List all projects (rich model).
        /// </summary>
        [HttpGet]
        public IActionResult ListProjects()
        {
            var projects = _projectDb.GetAllRichProjects();
            return Ok(new { count = projects.Count, projects });
        }

        /// <summary>
        /// GET /api/projects/{projectId} — Get a single project (rich model).
        /// </summary>
        [HttpGet("{projectId}")]
        public IActionResult GetProject(string projectId)
        {
            // Don't match the literal segments "contexts"
            if (projectId == "contexts") return GetAllProjectContexts();

            var project = _projectDb.GetRichProject(projectId);
            if (project == null)
                return NotFound(new { error = $"Project '{projectId}' not found" });

            return Ok(project);
        }

        /// <summary>
        /// PATCH /api/projects/{projectId} — Update individual project fields.
        /// Body: { "fields": { "name": "...", "deploy_path": "..." } }
        /// </summary>
        [HttpPatch("{projectId}")]
        public IActionResult UpdateProject(string projectId, [FromBody] UpdateProjectRequest request)
        {
            if (request?.Fields == null || request.Fields.Count == 0)
                return BadRequest(new { error = "No fields provided" });

            var updated = new List<string>();
            var rejected = new List<string>();

            foreach (var kvp in request.Fields)
            {
                if (_projectDb.UpdateProjectField(projectId, kvp.Key, kvp.Value))
                    updated.Add(kvp.Key);
                else
                    rejected.Add(kvp.Key);
            }

            return Ok(new { updated, rejected });
        }

        /// <summary>
        /// GET /api/projects/{projectId}/context — Full context with all associations.
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
        /// GET /api/projects/contexts — All projects with full association data.
        /// </summary>
        [HttpGet("contexts")]
        public IActionResult GetAllProjectContexts()
        {
            var contexts = _contextService.GetAllProjectContexts();
            return Ok(new { count = contexts.Count, contexts });
        }

        #endregion

        #region Agents

        /// <summary>
        /// POST /api/projects/{projectId}/agents
        /// Body: { "agentName": "...", "role": "...", "preferredModel": "..." }
        /// </summary>
        [HttpPost("{projectId}/agents")]
        public IActionResult AddAgent(string projectId, [FromBody] AddAgentRequest request)
        {
            _projectDb.SaveProjectAgent(new MultiTerminal.Models.ProjectAgent
            {
                ProjectId = projectId,
                AgentName = request.AgentName,
                Role = request.Role,
                PreferredModel = request.PreferredModel
            });
            return Ok(new { success = true });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/agents/{agentName}
        /// </summary>
        [HttpDelete("{projectId}/agents/{agentName}")]
        public IActionResult RemoveAgent(string projectId, string agentName)
        {
            var deleted = _projectDb.DeleteProjectAgent(projectId, agentName);
            if (!deleted)
                return NotFound(new { error = $"Agent '{agentName}' not found on project '{projectId}'" });

            return Ok(new { success = true });
        }

        #endregion

        #region MCP Servers

        /// <summary>
        /// POST /api/projects/{projectId}/mcp-servers
        /// Body: { "serverName": "...", "isEnabled": true }
        /// </summary>
        [HttpPost("{projectId}/mcp-servers")]
        public IActionResult AddMcpServer(string projectId, [FromBody] AddMcpServerRequest request)
        {
            _projectDb.SaveProjectMcpServer(new MultiTerminal.Models.ProjectMcpServer
            {
                ProjectId = projectId,
                ServerName = request.ServerName,
                IsEnabled = request.IsEnabled
            });
            return Ok(new { success = true });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/mcp-servers/{serverName}
        /// </summary>
        [HttpDelete("{projectId}/mcp-servers/{serverName}")]
        public IActionResult RemoveMcpServer(string projectId, string serverName)
        {
            var deleted = _projectDb.DeleteProjectMcpServer(projectId, serverName);
            if (!deleted)
                return NotFound(new { error = $"MCP server '{serverName}' not found on project '{projectId}'" });

            return Ok(new { success = true });
        }

        #endregion

        #region Specialist Agents

        /// <summary>
        /// POST /api/projects/{projectId}/specialists
        /// Body: { "agentType": "...", "isEnabled": true, "customPrompt": "..." }
        /// </summary>
        [HttpPost("{projectId}/specialists")]
        public IActionResult AddSpecialist(string projectId, [FromBody] AddSpecialistRequest request)
        {
            _projectDb.SaveProjectSpecialistAgent(new MultiTerminal.Models.ProjectSpecialistAgent
            {
                ProjectId = projectId,
                AgentType = request.AgentType,
                IsEnabled = request.IsEnabled,
                CustomPrompt = request.CustomPrompt
            });
            return Ok(new { success = true });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/specialists/{agentType}
        /// </summary>
        [HttpDelete("{projectId}/specialists/{agentType}")]
        public IActionResult RemoveSpecialist(string projectId, string agentType)
        {
            var deleted = _projectDb.DeleteProjectSpecialistAgent(projectId, agentType);
            if (!deleted)
                return NotFound(new { error = $"Specialist '{agentType}' not found on project '{projectId}'" });

            return Ok(new { success = true });
        }

        #endregion

        #region Paths

        /// <summary>
        /// POST /api/projects/{projectId}/paths
        /// Body: { "pathType": "...", "pathValue": "...", "description": "..." }
        /// </summary>
        [HttpPost("{projectId}/paths")]
        public IActionResult AddPath(string projectId, [FromBody] AddPathRequest request)
        {
            var id = _projectDb.SaveProjectPath(new MultiTerminal.Models.ProjectPath
            {
                ProjectId = projectId,
                PathType = request.PathType,
                PathValue = request.PathValue,
                Description = request.Description
            });
            return Ok(new { success = true, id });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/paths/{pathId}
        /// </summary>
        [HttpDelete("{projectId}/paths/{pathId:int}")]
        public IActionResult RemovePath(string projectId, int pathId)
        {
            var deleted = _projectDb.DeleteProjectPath(pathId);
            if (!deleted)
                return NotFound(new { error = $"Path {pathId} not found" });

            return Ok(new { success = true });
        }

        #endregion

        #region Prompts

        /// <summary>
        /// POST /api/projects/{projectId}/prompts
        /// Body: { "promptType": "...", "promptText": "...", "displayOrder": 0 }
        /// </summary>
        [HttpPost("{projectId}/prompts")]
        public IActionResult AddPrompt(string projectId, [FromBody] AddPromptRequest request)
        {
            var id = _projectDb.SaveProjectPrompt(new MultiTerminal.Models.ProjectPromptEntry
            {
                ProjectId = projectId,
                PromptType = request.PromptType,
                PromptText = request.PromptText,
                DisplayOrder = request.DisplayOrder
            });
            return Ok(new { success = true, id });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/prompts/{promptId}
        /// </summary>
        [HttpDelete("{projectId}/prompts/{promptId:int}")]
        public IActionResult RemovePrompt(string projectId, int promptId)
        {
            var deleted = _projectDb.DeleteProjectPrompt(promptId);
            if (!deleted)
                return NotFound(new { error = $"Prompt {promptId} not found" });

            return Ok(new { success = true });
        }

        #endregion

        #region Skills

        /// <summary>
        /// POST /api/projects/{projectId}/skills
        /// Body: { "skillName": "...", "isEnabled": true }
        /// </summary>
        [HttpPost("{projectId}/skills")]
        public IActionResult AddSkill(string projectId, [FromBody] AddSkillRequest request)
        {
            _projectDb.SaveProjectSkill(new MultiTerminal.Models.ProjectSkill
            {
                ProjectId = projectId,
                SkillName = request.SkillName,
                IsEnabled = request.IsEnabled
            });
            return Ok(new { success = true });
        }

        /// <summary>
        /// DELETE /api/projects/{projectId}/skills/{skillName}
        /// </summary>
        [HttpDelete("{projectId}/skills/{skillName}")]
        public IActionResult RemoveSkill(string projectId, string skillName)
        {
            var deleted = _projectDb.DeleteProjectSkill(projectId, skillName);
            if (!deleted)
                return NotFound(new { error = $"Skill '{skillName}' not found on project '{projectId}'" });

            return Ok(new { success = true });
        }

        #endregion

        #region Request DTOs

        public class UpdateProjectRequest
        {
            public Dictionary<string, string> Fields { get; set; }
        }

        public class AddAgentRequest
        {
            public string AgentName { get; set; }
            public string Role { get; set; }
            public string PreferredModel { get; set; }
        }

        public class AddMcpServerRequest
        {
            public string ServerName { get; set; }
            public bool IsEnabled { get; set; } = true;
        }

        public class AddSpecialistRequest
        {
            public string AgentType { get; set; }
            public bool IsEnabled { get; set; } = true;
            public string CustomPrompt { get; set; }
        }

        public class AddPathRequest
        {
            public string PathType { get; set; }
            public string PathValue { get; set; }
            public string Description { get; set; }
        }

        public class AddPromptRequest
        {
            public string PromptType { get; set; }
            public string PromptText { get; set; }
            public int DisplayOrder { get; set; }
        }

        public class AddSkillRequest
        {
            public string SkillName { get; set; }
            public bool IsEnabled { get; set; } = true;
        }

        #endregion
    }
}
