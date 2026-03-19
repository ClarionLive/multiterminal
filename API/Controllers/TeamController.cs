using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/team")]
    public class TeamController : ControllerBase
    {
        private readonly MessageBroker _broker;
        private readonly ProjectService _projectService;
        private readonly ProjectDatabase _projectDb;

        public TeamController(MessageBroker broker, ProjectService projectService, ProjectDatabase projectDb)
        {
            _broker = broker;
            _projectService = projectService;
            _projectDb = projectDb;
        }

        /// <summary>
        /// GET /api/team/profiles — All team member profiles with online status.
        /// Used by ClaudeRemote to populate the agent picker when launching terminals.
        /// </summary>
        [HttpGet("profiles")]
        public IActionResult GetAllProfiles()
        {
            var summaries = _projectDb.GetAllProfileSummaries();
            var activeTerminals = _broker.GetTerminals();
            var onlineNames = new System.Collections.Generic.HashSet<string>(
                activeTerminals
                    .Where(t => t.IsConnected)
                    .Select(t => t.Name),
                System.StringComparer.OrdinalIgnoreCase);

            var profiles = summaries.Select(s =>
            {
                var profileResult = _broker.GetProfile(s.DisplayName ?? s.Id);
                var isLead = profileResult.Success && profileResult.Profile != null && profileResult.Profile.IsTeamLead;
                return new
                {
                    name = s.DisplayName ?? s.Id,
                    role = s.Role ?? "",
                    preferredModel = s.PreferredModel ?? "sonnet",
                    isOnline = onlineNames.Contains(s.DisplayName ?? s.Id),
                    isTeamLead = isLead
                };
            }).ToList();

            return Ok(new { count = profiles.Count, profiles });
        }

        /// <summary>
        /// Get the team roster for a project, merged with profile data.
        /// Returns each agent's name, preferred_model, agent_instructions, role, and skills.
        /// </summary>
        [HttpGet("roster")]
        public IActionResult GetTeamRoster([FromQuery] string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return BadRequest(new { error = "projectPath query parameter is required" });

            var project = _projectService.LoadProject(projectPath);
            if (project == null)
                return NotFound(new { error = $"No project found at path: {projectPath}" });

            if (project.TeamAgents == null || project.TeamAgents.Count == 0)
                return Ok(new { projectName = project.Name, agents = new List<object>() });

            var roster = new List<object>();
            foreach (var agentName in project.TeamAgents)
            {
                var profileResult = _broker.GetProfile(agentName);
                if (profileResult.Success && profileResult.Profile != null)
                {
                    var profile = profileResult.Profile;
                    roster.Add(new
                    {
                        name = agentName,
                        hasProfile = true,
                        preferredModel = profile.PreferredModel ?? "sonnet",
                        agentInstructions = profile.AgentInstructions,
                        role = profile.Role,
                        skills = profile.GetSkills(),
                        isOnline = profile.IsOnline,
                        isTeamLead = profile.IsTeamLead
                    });
                }
                else
                {
                    roster.Add(new
                    {
                        name = agentName,
                        hasProfile = false,
                        preferredModel = "sonnet",
                        agentInstructions = (string)null,
                        role = (string)null,
                        skills = new List<string>(),
                        isOnline = false,
                        isTeamLead = false
                    });
                }
            }

            return Ok(new
            {
                projectName = project.Name,
                projectPath = project.Path,
                agents = roster
            });
        }
    }
}
