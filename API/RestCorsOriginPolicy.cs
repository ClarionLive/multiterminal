using System;

namespace MultiTerminal.API
{
    /// <summary>
    /// CORS origin allowlist for the :5050 REST surface. Introduced in Eval P2 (task c522764d) as a
    /// two-tier scheme with a null-tolerant carve-out; tightened in Eval P2c (task f9697aac) to a
    /// single, strict, ENUMERATED allowlist once the one browser fetch caller (tasks-panel.html) was
    /// migrated off <c>file://</c> onto a real virtual-host origin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single default policy</b> (all controllers) — <see cref="IsTrustedBrowserOrigin"/>: the panel
    /// virtual-host origin <see cref="PanelHosting.Origin"/> and NOTHING else. This is the READ
    /// boundary: a disallowed origin receives no <c>Access-Control-Allow-Origin</c> header, so the
    /// browser refuses to expose any response body (including token-returning GETs) to the calling
    /// page's JavaScript.
    /// </para>
    /// <para>
    /// <b>Why enumerated, not a loopback class (task f9697aac, PM ruling Z; finding report 87db18a7).</b>
    /// The earlier design allowed any loopback origin. But "loopback" is a CLASS — it trusts every
    /// local process's pages on every port (e.g. a malicious <c>http://localhost:9999</c> page the user
    /// visits), which is exactly the <c>same-site</c> read the retired <c>CrossOriginBrowserGuard</c>
    /// blocked on the token/config GETs. A census of :5050's browser callers found EXACTLY ONE
    /// legitimate origin — the tasks-panel virtual host — so the allowlist enumerates that one member.
    /// The literal "null" that <c>file://</c> / opaque-origin sandboxed iframes send is likewise not a
    /// member and is rejected. Any future browser consumer adds its origin HERE (a new enumerated
    /// member) plus a test — NEVER a class.
    /// </para>
    /// <para>
    /// CORS is a READ boundary, not a write boundary — the browser sends the request regardless of the
    /// policy and only gates the caller's ability to READ the response. Blind cross-site CSRF WRITES are
    /// handled separately by <see cref="SecFetchSiteWriteGuardMiddleware"/> (which shares this predicate
    /// as its trusted-origin test, so the read and write allowlists cannot desync). Non-browser callers
    /// (Node MCP, hooks, PowerShell, curl, HttpClient) send no Origin header, so CORS never applies to
    /// them; they are unaffected by this predicate.
    /// </para>
    /// </remarks>
    internal static class RestCorsOriginPolicy
    {
        /// <summary>
        /// The sole trusted browser origin on :5050: the tasks-panel virtual host
        /// (<see cref="PanelHosting.Origin"/>). Rejects "null", loopback origins (any port), remote,
        /// empty, and malformed origins (fail closed). Also the trusted-origin test the
        /// <see cref="SecFetchSiteWriteGuardMiddleware"/> shares.
        /// </summary>
        // ENUMERATED ALLOWLIST — virtual host only, by census (report 87db18a7). Any future browser
        // consumer of :5050 adds its origin HERE (as a new enumerated member) plus a test, NEVER a
        // class like "any loopback origin" — a class trusts every local process's pages, not just the
        // one member that is actually legitimate.
        public static bool IsTrustedBrowserOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            return string.Equals(origin, PanelHosting.Origin, StringComparison.OrdinalIgnoreCase);
        }
    }
}
