using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Owner profile (git identity and GitHub config). METADATA ONLY — no endpoint here returns a
    /// credential (task ea7d9cf9); <c>hasGitHubToken</c> reports presence without disclosing the value.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/owner-profile/github-token</c> lived here and returned a raw PAT "for agent use
    /// during git operations", falling back to the sole source-control account's token post-migration.
    /// No consumer was ever written for it, and every fetch wrote the secret into an agent's session
    /// transcript on disk — the leak that motivated ea7d9cf9. It was removed along with its
    /// ambiguity guard and single-account resolver, which existed only to serve it. The
    /// <c>SourceControlAccountService</c> constructor dependency went with them.
    ///
    /// A feature that genuinely needs to authenticate to GitHub must do so in-process (see the
    /// remarks on <see cref="SourceControlAccountsController"/>); reintroducing a token-returning
    /// endpoint fails scripts/verify-no-secret-serialization.mjs.
    /// </remarks>
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
    }
}
