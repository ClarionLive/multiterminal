using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// The one-click GitHub App registration flow (task b42b1883, item 3).
    ///
    /// <para>Three endpoints, all GET, because the whole flow is a browser journey:
    /// <c>/register</c> renders a page that posts MT's manifest to GitHub, the Owner clicks "Create
    /// GitHub App" once, GitHub redirects back to <c>/callback</c> with a temporary code, and
    /// <c>/status</c> answers "is the bot set up?" without touching key material.</para>
    ///
    /// <para><b>Why a page and not a redirect.</b> GitHub's manifest flow requires the manifest to
    /// arrive as a form POST; a plain redirect cannot carry one. So MT serves a tiny page whose only
    /// job is to submit that form. It does NOT auto-submit — see <see cref="Register"/>.</para>
    ///
    /// <para><b>⚠️ Nothing here may render or return key material.</b> The conversion response contains
    /// the App's private key. It goes straight into DPAPI storage inside the service; the callback page
    /// shows only the App slug, and <c>/status</c> reports booleans. A key rendered into a browser page
    /// is a key in the browser's history, cache and any screenshot of it.</para>
    /// </summary>
    [ApiController]
    [Route("api/github/app")]
    public class GitHubAppController : ControllerBase
    {
        private readonly GitHubAppManifestService _manifest;
        private readonly SettingsService _settings;
        private readonly GitHubAppTokenService _tokens;
        private readonly MessageBroker _broker;

        public GitHubAppController(
            GitHubAppManifestService manifest,
            SettingsService settings,
            GitHubAppTokenService tokens,
            MessageBroker broker)
        {
            _manifest = manifest;
            _settings = settings;
            _tokens = tokens;
            _broker = broker;
        }

        /// <summary>
        /// Renders the hand-off page: MT's manifest in a form, pointed at GitHub, with a button the
        /// Owner presses.
        ///
        /// <para><b>The form is deliberately NOT auto-submitted.</b> Auto-posting would mean a link
        /// anyone could get MT to open silently starts creating a credential with repository write
        /// access. The ticket's position is that a click the Owner actually makes is the right
        /// boundary, and that argument applies here as much as on GitHub's own confirmation page.</para>
        /// </summary>
        /// <param name="account">
        /// Optional org/account to create the App under. Omitted means the signed-in personal account.
        /// Both repos in play live under ClarionLive, so that is the expected value.
        /// </param>
        [HttpGet("register")]
        public IActionResult Register([FromQuery] string account = null)
        {
            string appName = _settings.GetGitHubAppSlug();
            if (string.IsNullOrWhiteSpace(appName))
                appName = "clarionlive-agent";      // Owner decision, 2026-09-08

            // Derived from the request so it stays correct if MT is not on the default port. GitHub
            // permits http for localhost redirect targets.
            string redirectUrl = $"{Request.Scheme}://{Request.Host}/api/github/app/callback";
            string manifestJson = GitHubAppManifestService.BuildManifest(
                appName,
                redirectUrl,
                homepageUrl: "https://github.com/ClarionLive/multiterminal");

            string state = _manifest.IssueState();

            string action = string.IsNullOrWhiteSpace(account)
                ? $"https://github.com/settings/apps/new?state={WebUtility.UrlEncode(state)}"
                : $"https://github.com/organizations/{WebUtility.UrlEncode(account)}/settings/apps/new?state={WebUtility.UrlEncode(state)}";

            // HtmlEncode is doing real work: the manifest is JSON full of quotes, and an unencoded
            // value attribute would terminate early and produce a broken form (or, with a hostile app
            // name, injected markup).
            string html = $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>Create the MultiTerminal GitHub App</title>
<style>
 body{{font:14px system-ui,-apple-system,Segoe UI,sans-serif;max-width:44rem;margin:3rem auto;padding:0 1rem;color:#111}}
 h1{{font-size:1.3rem}} code{{background:#f3f3f3;padding:.1rem .3rem;border-radius:3px}}
 button{{font:inherit;padding:.6rem 1.1rem;border:0;border-radius:6px;background:#1f6feb;color:#fff;cursor:pointer}}
 ul{{line-height:1.6}} .muted{{color:#555}}
</style></head><body>
<h1>Create the MultiTerminal GitHub App</h1>
<p>This creates a GitHub App named <code>{WebUtility.HtmlEncode(appName)}</code>. Agents will comment
and open pull requests as <code>{WebUtility.HtmlEncode(appName)}[bot]</code> instead of as you.</p>
<p>It will request:</p>
<ul>
 <li><strong>Issues</strong> — read &amp; write</li>
 <li><strong>Pull requests</strong> — read &amp; write</li>
 <li><strong>Contents</strong> — read &amp; write <span class=""muted"">(lets agents push commits as the bot)</span></li>
 <li><strong>Metadata</strong> — read</li>
 <li>No webhook, no events</li>
</ul>
<p class=""muted"">MultiTerminal keeps the App's private key encrypted on this machine and never gives
it to an agent. Agents receive tokens that expire within the hour.</p>
<form method=""post"" action=""{WebUtility.HtmlEncode(action)}"">
  <input type=""hidden"" name=""manifest"" value=""{WebUtility.HtmlEncode(manifestJson)}"">
  <button type=""submit"">Continue to GitHub</button>
</form>
</body></html>";

            return Content(html, "text/html; charset=utf-8");
        }

        /// <summary>
        /// GitHub's redirect target. Validates the state, exchanges the code, stores the credentials,
        /// and reports the outcome as a page the Owner is looking at.
        /// </summary>
        [HttpGet("callback")]
        public async Task<IActionResult> Callback(
            [FromQuery] string code,
            [FromQuery] string state,
            CancellationToken ct)
        {
            try
            {
                string slug = await _manifest.CompleteRegistrationAsync(code, state, ct).ConfigureAwait(false);
                return Content(Page(
                    "GitHub App created",
                    $"MultiTerminal is now configured to act as <code>{WebUtility.HtmlEncode(slug)}[bot]</code>.",
                    "Next: install the App on the account that owns the repositories, then close this tab."),
                    "text/html; charset=utf-8");
            }
            catch (InvalidOperationException ex)
            {
                // The message is ours and describes the shape of the failure (bad state, no code,
                // GitHub refused). It never contains key material — see GitHubAppManifestService.
                return Content(Page("Registration failed", WebUtility.HtmlEncode(ex.Message), "Nothing was stored."),
                    "text/html; charset=utf-8");
            }
        }

        /// <summary>
        /// Whether the bot is configured. Booleans and a slug only — never key material, so this is safe
        /// for a settings panel or an agent to read.
        /// </summary>
        [HttpGet("status")]
        public IActionResult Status() => Ok(new
        {
            configured = _settings.IsGitHubAppConfigured(),
            hasPrivateKey = _settings.HasGitHubAppPrivateKey(),
            appId = _settings.GetGitHubAppId(),
            slug = _settings.GetGitHubAppSlug(),
            defaultInstallationId = _settings.GetGitHubAppDefaultInstallationId(),
        });

        /// <summary>
        /// Mints a short-lived installation token for the terminal that presents its launch nonce
        /// (task b42b1883, item 2). This is what the git credential helper and the <c>gh</c> shim call,
        /// once per operation — nothing stores the result.
        ///
        /// <para><b>Why this needs a gate at all.</b> MT's REST API is loopback and unauthenticated by
        /// design (established by task c9285d2a's security audit). Without one, any process on this
        /// machine could ask for a token that can comment and push code as the bot. The launch nonce is
        /// already per-launch, already injected into the terminal's environment, and already redacted
        /// from the debug log, so it is the gate that adds no new secret.</para>
        ///
        /// <para><b>What that does and does not buy — stated plainly so nobody oversells it.</b> An
        /// AGENT can still mint tokens: it can read its own environment, and it is legitimately allowed
        /// to act as the bot — any design where it can run <c>gh</c> gives it that. What the gate bounds
        /// is OTHER local processes, and it makes every mint attributable to a named terminal. The
        /// security of this ticket rests on the private key being unreachable and the token expiring in
        /// an hour, not on pretending the agent is walled off from the identity it is meant to use.</para>
        ///
        /// <para>POST rather than GET on purpose: minting creates a credential at GitHub, so it is not a
        /// safe method, and POST additionally puts it behind
        /// <see cref="SecFetchSiteWriteGuardMiddleware"/> — a browser-driven cross-site call is refused
        /// before it reaches here, while a credential helper (not a browser) passes.</para>
        /// </summary>
        [HttpPost("/api/github/token")]
        public async Task<IActionResult> MintToken(
            [FromBody] MintTokenRequest request,
            CancellationToken ct)
        {
            // Header rather than body: it keeps the secret out of anything that logs request bodies,
            // and it is what a credential helper can set most simply.
            string nonce = Request.Headers["X-MultiTerminal-Launch-Nonce"].ToString();

            TerminalInfo terminal = _broker?.GetConnectedTerminalByLaunchNonce(nonce);
            if (terminal == null)
            {
                // Deliberately identical for "no nonce", "wrong nonce" and "nonce of a terminal that has
                // since disconnected" — the distinction is only useful to someone probing.
                return StatusCode(401, new
                {
                    error = "This request did not present a valid MultiTerminal launch nonce. "
                          + "Token minting is available to MT-launched terminals only.",
                });
            }

            try
            {
                string token = await _tokens
                    .GetInstallationTokenAsync(request?.InstallationId, ct)
                    .ConfigureAwait(false);

                // The token is the point of the response, so it is in the body — but nothing here logs
                // it, and the caller (helper or shim) uses it for one command and discards it.
                return Ok(new { token, terminal = terminal.Name });
            }
            catch (InvalidOperationException ex)
            {
                // Configuration and GitHub-side failures. GitHubAppTokenService guarantees these
                // messages describe the shape of the problem and never carry key material.
                return StatusCode(503, new { error = ex.Message, terminal = terminal.Name });
            }
        }

        /// <summary>Body of a mint request. The nonce travels in a header, not here.</summary>
        public sealed class MintTokenRequest
        {
            /// <summary>
            /// Which installation to mint for. Omitted means the configured default, which is the
            /// normal case while one installation covers both repositories.
            /// </summary>
            public string InstallationId { get; set; }
        }

        private static string Page(string title, string body, string footer) => $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>{WebUtility.HtmlEncode(title)}</title>
<style>body{{font:14px system-ui,-apple-system,Segoe UI,sans-serif;max-width:44rem;margin:3rem auto;padding:0 1rem;color:#111}}
h1{{font-size:1.3rem}} code{{background:#f3f3f3;padding:.1rem .3rem;border-radius:3px}} .muted{{color:#555}}</style>
</head><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p><p class=""muted"">{WebUtility.HtmlEncode(footer)}</p></body></html>";
    }
}
