using System;
using System.Net;

namespace MultiTerminal.API
{
    /// <summary>
    /// CORS origin allowlist for the :5050 REST surface. Originally introduced in Eval P2
    /// (task c522764d) as a two-tier scheme with a null-tolerant carve-out; tightened to a
    /// single strict allowlist in Eval P2c (task f9697aac) once the one browser fetch caller
    /// (tasks-panel.html) was migrated off <c>file://</c> onto a real virtual-host origin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single default policy</b> (all controllers) — <see cref="IsTrustedBrowserOrigin"/>: loopback
    /// origins (localhost / 127.0.0.1 / [::1], any scheme or port) PLUS the panel virtual-host origin
    /// <see cref="PanelHosting.Origin"/>. Every other origin — including the literal "null" that
    /// <c>file://</c> and sandboxed (opaque-origin) iframes send — is rejected. This is the READ
    /// boundary: a disallowed origin receives no <c>Access-Control-Allow-Origin</c> header, so the
    /// browser refuses to expose any response body (including token-returning GETs) to the calling
    /// page's JavaScript.
    /// </para>
    /// <para>
    /// The previous scoped null-tolerant policy (<c>FilePanelNullTolerant</c>) and its
    /// <c>IsAllowedOrigin</c> predicate were removed in f9697aac: the sole reason "null" was tolerated
    /// was the <c>file://</c> tasks-panel, which now loads from <see cref="PanelHosting.Origin"/> and so
    /// sends a real, allowlisted Origin on its report fetches. Tolerating "null" was a residual read
    /// exposure (reachable by any local <c>file://</c> page or an <c>allow-scripts</c> sandboxed iframe),
    /// and it is now closed.
    /// </para>
    /// <para>
    /// CORS is a READ boundary, not a write boundary — the browser sends the request regardless of the
    /// policy and only gates the caller's ability to READ the response. Blind cross-site CSRF WRITES are
    /// therefore handled separately by <see cref="SecFetchSiteWriteGuardMiddleware"/>, not here.
    /// </para>
    /// <para>
    /// Neither the policy nor the guard sets AllowCredentials — the panels are credential-less.
    /// Non-browser callers (Node MCP, PowerShell, curl, HttpClient) send no Origin header, so CORS never
    /// applies to them; they are unaffected by this predicate.
    /// </para>
    /// </remarks>
    internal static class RestCorsOriginPolicy
    {
        /// <summary>
        /// Default-policy predicate: true for loopback origins (via <see cref="IsLoopbackOrigin"/>) and
        /// for the panel virtual-host origin (<see cref="PanelHosting.Origin"/>). Rejects "null", empty,
        /// unparseable, and every other origin (fail closed). Also the trusted-origin test the
        /// <see cref="SecFetchSiteWriteGuardMiddleware"/> shares, so the READ allowlist and the WRITE
        /// allowlist can never desync.
        /// </summary>
        public static bool IsTrustedBrowserOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            if (string.Equals(origin, PanelHosting.Origin, StringComparison.OrdinalIgnoreCase))
                return true;

            return IsLoopbackOrigin(origin);
        }

        /// <summary>
        /// True only for loopback origins (localhost / 127.0.0.1 / [::1], any scheme or port).
        /// Rejects "null", empty, and unparseable origins (fail closed).
        /// </summary>
        public static bool IsLoopbackOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
        }
    }
}
