using System.Net;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST surface for the multi-account source control store. METADATA ONLY — no endpoint here
    /// returns a credential (task ea7d9cf9).
    /// </summary>
    /// <remarks>
    /// This controller used to dispense raw PATs: <c>GET /api/source-accounts/{id}/token</c> returned
    /// one outright, and the per-project resolve returned another alongside its metadata, both
    /// "for an agent's git push/publish operation". Both were added speculatively by f1e586b and NO
    /// consumer was ever written — MultiTerminal performs no authenticated git or GitHub write
    /// anywhere, and the one legitimate reader (SourceControlValidator, behind the account editor's
    /// "Test" button) calls <see cref="SourceControlAccountService.GetToken"/> in-process rather than
    /// over HTTP. Meanwhile the loopback gate does not distinguish MT's own agents from arbitrary
    /// local code, and CORS does not apply to non-browser clients, so any local process could read
    /// every stored PAT — and every fetch landed the secret in an agent's session transcript on disk,
    /// which is how a live token leaked and had to be rotated.
    ///
    /// So the dispensing was removed rather than authenticated: there was nothing to authenticate FOR.
    /// A future feature that genuinely needs to push (see task c071ab40) must perform the operation
    /// in-process, NOT reintroduce an endpoint that hands the credential out.
    /// scripts/verify-no-secret-serialization.mjs fails the build if one is.
    /// </remarks>
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
        /// GET /api/projects/{projectId}/source-account — resolve the project's assigned account.
        /// METADATA ONLY: <c>hasToken</c> reports whether a credential is stored, but the credential
        /// itself is never returned (see the class remarks). Mirrors ClarionAssistant's
        /// solution_repos -> github_accounts join, using projects.source_control_account_id instead.
        /// </summary>
        /// <remarks>
        /// The loopback gate is RETAINED even though the response no longer carries a secret. It costs
        /// nothing, and which account a project is bound to is still local-only information — a
        /// security ticket is the wrong place to widen a surface. It is now defense-in-depth rather
        /// than the load-bearing protection it was when this returned a PAT.
        /// </remarks>
        [HttpGet("/api/projects/{projectId}/source-account")]
        public IActionResult GetProjectSourceAccount(string projectId)
        {
            if (!IsLoopback())
                return Problem(detail: "Token access is restricted to local callers", statusCode: 403);

            var project = _projectDb.GetRichProject(projectId);
            if (project == null)
                return Problem(detail: $"Project '{projectId}' not found", statusCode: 404);

            if (string.IsNullOrEmpty(project.SourceControlAccountId))
                return Problem(detail: "Project has no source control account assigned", statusCode: 404);

            var account = _accountService.Get(project.SourceControlAccountId);
            if (account == null)
                return Problem(detail: "Assigned source control account no longer exists", statusCode: 404);

            return Ok(new
            {
                accountId = account.Id,
                displayName = account.DisplayName,
                provider = account.Provider,
                username = account.Username,
                hasToken = account.HasToken
            });
        }

        /// <summary>
        /// True when the request originated from the loopback interface (127.0.0.1 / ::1).
        /// A null remote address (in-process / test host) is treated as local.
        /// </summary>
        /// <remarks>
        /// Historically this gate was load-bearing: these GETs handed back a raw PAT. Since ea7d9cf9
        /// removed the credential from every response it is defense-in-depth over project metadata.
        /// Cross-origin browser reads are separately denied by the strict CORS allowlist (task
        /// f9697aac, which retired the per-controller CrossOriginBrowserGuard): a non-allowlisted
        /// browser origin — including another loopback-port page — gets no ACAO. Note that neither
        /// protection ever constrained a non-browser local client, which is exactly why the secret
        /// had to stop being served rather than merely be gated.
        /// </remarks>
        private bool IsLoopback()
        {
            var remote = HttpContext?.Connection?.RemoteIpAddress;
            return remote == null || IPAddress.IsLoopback(remote);
        }
    }
}
