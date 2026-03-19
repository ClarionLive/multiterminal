using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/owner-profile")]
    public class OwnerProfileController : ControllerBase
    {
        private readonly OwnerProfileService _ownerProfileService;

        public OwnerProfileController(OwnerProfileService ownerProfileService)
        {
            _ownerProfileService = ownerProfileService;
        }

        /// <summary>
        /// Get the owner profile (git identity and GitHub config).
        /// </summary>
        [HttpGet]
        public IActionResult GetProfile()
        {
            var profile = _ownerProfileService.GetProfile();
            if (profile == null)
                return Ok(new { configured = false });

            return Ok(new
            {
                configured = _ownerProfileService.IsConfigured(),
                fullName = profile.FullName,
                email = profile.Email,
                gitHubUsername = profile.GitHubUsername,
                hasGitHubToken = profile.HasGitHubToken,
                createdAt = profile.CreatedAt,
                updatedAt = profile.UpdatedAt
            });
        }

        /// <summary>
        /// Get the GitHub token (for agent use during git operations).
        /// Returns 404 if no token is stored.
        /// </summary>
        [HttpGet("github-token")]
        public IActionResult GetGitHubToken()
        {
            var token = _ownerProfileService.GetGitHubToken();
            if (string.IsNullOrEmpty(token))
                return NotFound(new { error = "No GitHub token configured" });

            return Ok(new { token });
        }
    }
}
