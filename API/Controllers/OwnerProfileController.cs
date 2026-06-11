using System;
using System.Net;
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
            // This hands back a raw PAT; gate it like the other token endpoints so a browser
            // cross-origin fetch (which AllowAnyOrigin would otherwise let read) is refused
            // while a local agent using HttpClient still passes.
            if (!IsLocalNonBrowserCaller())
                return StatusCode(403, new { error = "Token access is restricted to local callers" });

            var token = _ownerProfileService.GetGitHubToken();
            if (string.IsNullOrEmpty(token))
                token = ResolveDefaultGitHubAccountToken();

            if (string.IsNullOrEmpty(token))
                return NotFound(new { error = "No GitHub token configured" });

            return Ok(new { token });
        }

        /// <summary>
        /// Returns the token of the sole GitHub source control account, or null.
        /// Used as the back-compat fallback after migration. To avoid a confused-deputy
        /// (handing back an arbitrary account's PAT for a project that isn't bound to it),
        /// this only resolves when EXACTLY ONE github-provider account exists — with zero
        /// or multiple, there is no unambiguous default, so it returns null.
        /// </summary>
        private string ResolveDefaultGitHubAccountToken()
        {
            MultiTerminal.Models.SourceControlAccount only = null;
            foreach (var account in _accountService.GetAll())
            {
                if (account.Provider != null &&
                    !account.Provider.Equals("github", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (only != null)
                    return null; // more than one github account — ambiguous, don't pick.
                only = account;
            }

            if (only == null || !only.HasToken)
                return null;

            var token = _accountService.GetToken(only.Id);
            return string.IsNullOrEmpty(token) ? null : token;
        }

        /// <summary>
        /// True when the caller is a local, non-browser client (e.g. an agent using HttpClient).
        /// Loopback alone is insufficient under the global AllowAnyOrigin CORS policy: a malicious
        /// web page could fetch this token URL cross-origin and read the PAT. A null remote address
        /// (in-process / test host) is treated as local; the cross-origin browser fetch is rejected
        /// via the shared <see cref="CrossOriginBrowserGuard"/>.
        /// </summary>
        private bool IsLocalNonBrowserCaller()
        {
            var remote = HttpContext?.Connection?.RemoteIpAddress;
            if (remote != null && !IPAddress.IsLoopback(remote))
                return false;

            return CrossOriginBrowserGuard.IsLocalNonBrowserCaller(Request);
        }
    }
}
