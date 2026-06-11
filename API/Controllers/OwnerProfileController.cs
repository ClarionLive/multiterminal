using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/owner-profile")]
    public class OwnerProfileController : ControllerBase
    {
        private readonly OwnerProfileService _ownerProfileService;
        private readonly SourceControlAccountService _accountService;

        public OwnerProfileController(OwnerProfileService ownerProfileService, SourceControlAccountService accountService)
        {
            _ownerProfileService = ownerProfileService;
            _accountService = accountService;
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
        /// Back-compat: once the one-time migration moves the legacy token into
        /// source_control_accounts, the owner-profile token is gone, so fall back to the
        /// first GitHub source control account's token. Returns 404 if neither is present.
        /// </summary>
        [HttpGet("github-token")]
        public IActionResult GetGitHubToken()
        {
            var token = _ownerProfileService.GetGitHubToken();
            if (string.IsNullOrEmpty(token))
                token = ResolveDefaultGitHubAccountToken();

            if (string.IsNullOrEmpty(token))
                return NotFound(new { error = "No GitHub token configured" });

            return Ok(new { token });
        }

        /// <summary>
        /// Returns the token of the first GitHub source control account that has one,
        /// or null if none exist. Used as the back-compat fallback after migration.
        /// </summary>
        private string ResolveDefaultGitHubAccountToken()
        {
            foreach (var account in _accountService.GetAll())
            {
                if (!account.HasToken) continue;
                if (account.Provider != null &&
                    !account.Provider.Equals("github", StringComparison.OrdinalIgnoreCase))
                    continue;

                var token = _accountService.GetToken(account.Id);
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
            return null;
        }
    }
}
