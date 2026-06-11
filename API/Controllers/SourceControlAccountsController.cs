using System.Net;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST surface for the multi-account source control store.
    /// Listing is metadata-only (no secrets); token endpoints are gated to loopback callers
    /// since they hand back the raw PAT for an agent's git push/publish operation.
    /// </summary>
    [ApiController]
    [Route("api/source-accounts")]
    public class SourceControlAccountsController : ControllerBase
    {
        private readonly SourceControlAccountService _accountService;
        private readonly ProjectDatabase _projectDb;

        public SourceControlAccountsController(SourceControlAccountService accountService, ProjectDatabase projectDb)
        {
            _accountService = accountService;
            _projectDb = projectDb;
        }

        /// <summary>
        /// GET /api/source-accounts — list all accounts WITHOUT tokens.
        /// </summary>
        [HttpGet]
        public IActionResult ListAccounts()
        {
            var accounts = _accountService.GetAll();
            return Ok(new { count = accounts.Count, accounts });
        }

        /// <summary>
        /// GET /api/source-accounts/{id}/token — return the stored token for an account.
        /// Loopback-only: the token is a credential, so remote callers are refused.
        /// </summary>
        [HttpGet("{id}/token")]
        public IActionResult GetAccountToken(string id)
        {
            if (!IsLoopback())
                return StatusCode(403, new { error = "Token access is restricted to local callers" });

            if (_accountService.Get(id) == null)
                return NotFound(new { error = $"Source control account '{id}' not found" });

            var token = _accountService.GetToken(id);
            if (string.IsNullOrEmpty(token))
                return NotFound(new { error = "No token stored for this account" });

            return Ok(new { token });
        }

        /// <summary>
        /// GET /api/projects/{projectId}/source-account — resolve the project's assigned
        /// account plus its token (for push). Loopback-only because it returns the token.
        /// Mirrors ClarionAssistant's solution_repos -> github_accounts join, using
        /// projects.source_control_account_id instead.
        /// </summary>
        [HttpGet("/api/projects/{projectId}/source-account")]
        public IActionResult GetProjectSourceAccount(string projectId)
        {
            if (!IsLoopback())
                return StatusCode(403, new { error = "Token access is restricted to local callers" });

            var project = _projectDb.GetRichProject(projectId);
            if (project == null)
                return NotFound(new { error = $"Project '{projectId}' not found" });

            if (string.IsNullOrEmpty(project.SourceControlAccountId))
                return NotFound(new { error = "Project has no source control account assigned" });

            var account = _accountService.Get(project.SourceControlAccountId);
            if (account == null)
                return NotFound(new { error = "Assigned source control account no longer exists" });

            return Ok(new
            {
                accountId = account.Id,
                displayName = account.DisplayName,
                provider = account.Provider,
                username = account.Username,
                hasToken = account.HasToken,
                token = _accountService.GetToken(account.Id)
            });
        }

        /// <summary>
        /// True when the request originated from the loopback interface (127.0.0.1 / ::1).
        /// A null remote address (in-process / test host) is treated as local.
        /// </summary>
        private bool IsLoopback()
        {
            var remote = HttpContext?.Connection?.RemoteIpAddress;
            return remote == null || IPAddress.IsLoopback(remote);
        }
    }
}
