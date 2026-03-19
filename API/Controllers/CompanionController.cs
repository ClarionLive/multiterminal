using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/companions")]
    public class CompanionController : ControllerBase
    {
        private readonly CompanionProcessManager _companionManager;

        public CompanionController(CompanionProcessManager companionManager)
        {
            _companionManager = companionManager;
        }

        /// <summary>
        /// GET /api/companions/status — Returns the status of all configured companion processes.
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            var statuses = _companionManager.GetStatus();
            return Ok(statuses);
        }
    }
}
