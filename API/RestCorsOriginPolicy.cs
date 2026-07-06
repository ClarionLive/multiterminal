using System;
using System.Net;

namespace MultiTerminal.API
{
    /// <summary>
    /// CORS origin allowlists for the :5050 REST surface (Eval P2 item 3, task c522764d).
    /// Replaces the previous <c>AllowAnyOrigin()</c> policy with a two-tier scheme:
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default policy</b> (all controllers) — <see cref="IsLoopbackOrigin"/>: loopback origins only
    /// (localhost / 127.0.0.1 / [::1], any scheme or port). The literal "null" origin is NOT allowed.
    /// This blocks the drive-by remote-web-page CSRF/exfil READ threat that <c>AllowAnyOrigin</c> left
    /// open (e.g. https://evil.example, and — critically — a hostile sandboxed iframe whose opaque
    /// origin serializes to "null").
    /// </para>
    /// <para>
    /// <b>Scoped null-tolerant policy</b> (<see cref="FilePanelPolicyName"/>, applied via
    /// <c>[EnableCors]</c> to <c>TaskReportsController</c> only) — <see cref="IsAllowedOrigin"/>:
    /// loopback origins PLUS the literal "null" origin. This exists solely because the one WebView2
    /// panel that fetch()es :5050 — <c>TasksPanel/tasks-panel.html</c> — is loaded via file:// so its
    /// fetch Origin serializes to "null", and the endpoints it hits live on TaskReportsController
    /// (GET /api/tasks/{id}/reports[/{reportId}]). No WebView2 virtual-host origins exist today
    /// (no SetVirtualHostNameToFolderMapping anywhere), so a strict hostname allowlist would break
    /// that panel. Scoping "null" to this ONE controller (per PM ruling A on c522764d) shrinks the
    /// interim null-origin read-window from the whole ~20-endpoint API down to the reports controller.
    /// </para>
    /// <para>
    /// A hostile page CAN reach a null-origin fetch via a sandboxed iframe, so even this scoped
    /// tolerance is a residual read exposure on TaskReportsController until ticket f9697aac lands:
    /// f9697aac migrates tasks-panel.html to a real virtual-host origin, drops "null" entirely, adds a
    /// global Sec-Fetch-Site write-guard, and retires the <see cref="Controllers.CrossOriginBrowserGuard"/>
    /// sprinkles. CORS is a READ boundary, not a write boundary — blind cross-origin writes are handled
    /// by that f9697aac Sec-Fetch-Site guard, not here.
    /// </para>
    /// <para>
    /// Neither policy sets AllowCredentials (null-origin + credentials is a CORS footgun; the panels
    /// are credential-less). Non-browser callers (Node MCP, PowerShell, curl) send no Origin header,
    /// so CORS never applies to them — they are unaffected by either predicate. The loopback check
    /// mirrors <see cref="Controllers.CrossOriginBrowserGuard"/> for consistency (both retired jointly
    /// in f9697aac).
    /// </para>
    /// </remarks>
    internal static class RestCorsOriginPolicy
    {
        /// <summary>
        /// Name of the scoped, null-tolerant CORS policy. Applied via <c>[EnableCors]</c> to the one
        /// controller the file:// tasks-panel actually fetches (TaskReportsController).
        /// </summary>
        public const string FilePanelPolicyName = "FilePanelNullTolerant";

        /// <summary>
        /// Default-policy predicate: true only for loopback origins (localhost / 127.0.0.1 / [::1],
        /// any scheme or port). Rejects "null", empty, and unparseable origins (fail closed).
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

        /// <summary>
        /// Scoped-policy predicate: loopback origins (via <see cref="IsLoopbackOrigin"/>) PLUS the
        /// literal "null" origin (the file:// tasks-panel caller). See the class remarks for why
        /// "null" is tolerated here and only here, and its retirement path (f9697aac).
        /// </summary>
        public static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            // file:// / data: / sandboxed-iframe contexts send the literal "null" origin.
            if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                return true;

            return IsLoopbackOrigin(origin);
        }
    }
}
