using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly SettingsService _settings;

        public SettingsController(SettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// GET /api/settings/pipeline-topology — returns the provider assigned to each
        /// /pipeline role. Values: "claude", "codex", or "off". Unset keys are resolved
        /// to role defaults (Claude for the four standard gates, Off for cross-model adversary).
        /// </summary>
        [HttpGet("pipeline-topology")]
        public IActionResult GetPipelineTopology()
        {
            return Ok(new
            {
                verifier = _settings.GetPipelineVerifier(),
                codeReviewer = _settings.GetPipelineCodeReviewer(),
                securityAuditor = _settings.GetPipelineSecurityAuditor(),
                debugger = _settings.GetPipelineDebugger(),
                crossModelAdversary = _settings.GetPipelineCrossModelAdversary()
            });
        }
    }
}
