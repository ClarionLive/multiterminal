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
            // This hands back a raw PAT; keep it loopback-only. The cross-origin browser READ
            // protection that used to live here (retired CrossOriginBrowserGuard) is now provided
            // globally by the strict CORS allowlist (task f9697aac): a non-allowlisted browser origin
            // gets no ACAO, so its JS cannot read the token — while a local HttpClient agent passes.
            if (!IsLoopback())
                return Problem(detail: "Token access is restricted to local callers", statusCode: 403);

            // When more than one github-provider account exists there is no unambiguous global
            // default, so refuse even if the legacy owner-profile secret is still populated —
            // returning it would be a confused deputy (a project-unscoped PAT). The migration
            // leaves the legacy secret in place when accounts already exist, so this state is
            // reachable. Callers that need a specific account must use the per-project endpoint.
            if (CountGitHubAccounts() > 1)
                return Problem(detail: "No GitHub token configured", statusCode: 404);

            var token = _ownerProfileService.GetGitHubToken();
            if (string.IsNullOrEmpty(token))
                token = ResolveDefaultGitHubAccountToken();

            if (string.IsNullOrEmpty(token))
                return Problem(detail: "No GitHub token configured", statusCode: 404);

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
                if (!IsGitHubAccount(account))
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
        /// Counts github-provider source control accounts. Used to detect the ambiguous
        /// multi-account state in which no global default token may be returned.
        /// </summary>
        private int CountGitHubAccounts()
        {
            int count = 0;
            foreach (var account in _accountService.GetAll())
            {
                if (IsGitHubAccount(account))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// True when an account is a github-provider account. A null provider is treated as
        /// github (the historical default for legacy/migrated accounts).
        /// </summary>
        private static bool IsGitHubAccount(MultiTerminal.Models.SourceControlAccount account)
        {
            return account.Provider == null ||
                account.Provider.Equals("github", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the request originated from the loopback interface (127.0.0.1 / ::1).
        /// A null remote address (in-process / test host) is treated as local. Cross-origin browser
        /// READS are now blocked globally by the strict CORS allowlist (task f9697aac), so this only
        /// needs the loopback-IP check; a local agent using HttpClient passes.
        /// </summary>
        private bool IsLoopback()
        {
            var remote = HttpContext?.Connection?.RemoteIpAddress;
            return remote == null || IPAddress.IsLoopback(remote);
        }
    }
}
