using System;
using System.Net;

namespace MultiTerminal.API
{
    /// <summary>
    /// CORS origin allowlist for the :5050 REST surface (Eval P2 item 3, task c522764d).
    /// Replaces the previous <c>AllowAnyOrigin()</c> policy. Wired via
    /// <c>AddCors(... SetIsOriginAllowed(RestCorsOriginPolicy.IsAllowedOrigin) ...)</c>.
    /// </summary>
    /// <remarks>
    /// Allows loopback origins (localhost / 127.0.0.1 / [::1], any scheme or port) and the
    /// literal "null" origin; blocks everything else — notably a drive-by remote web page
    /// (e.g. https://evil.example), which is the CSRF/exfil threat <c>AllowAnyOrigin</c> left open.
    /// <para>
    /// Why "null" is tolerated: the only WebView2 panel that fetch()es :5050 is
    /// TasksPanel/tasks-panel.html, loaded via file:// so its fetch Origin serializes to "null".
    /// No WebView2 virtual-host origins exist today (no SetVirtualHostNameToFolderMapping
    /// anywhere), so a strict hostname allowlist would break that panel. Tolerating "null" keeps
    /// it working while still blocking real remote origins (a remote page cannot forge
    /// Origin: null). This tolerance — and the <see cref="Controllers.CrossOriginBrowserGuard"/>
    /// sprinkles that still defend the 6 token endpoints against null-origin local pages — are
    /// retired together in ticket f9697aac, which first migrates tasks-panel.html to a real
    /// allowlistable virtual-host origin.
    /// </para>
    /// <para>
    /// Non-browser callers (Node MCP, PowerShell, curl) send no Origin header, so CORS never
    /// applies to them — they are unaffected by this predicate. Deliberately NOT combined with
    /// AllowCredentials (null-origin + credentials is a CORS footgun; the panels are
    /// credential-less). Mirrors <see cref="Controllers.CrossOriginBrowserGuard"/>'s loopback
    /// check for consistency.
    /// </para>
    /// </remarks>
    internal static class RestCorsOriginPolicy
    {
        /// <summary>
        /// True when <paramref name="origin"/> is a loopback origin or the literal "null" origin.
        /// A null/empty or unparseable origin (other than the literal "null") is rejected.
        /// </summary>
        public static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            // file:// / data: / sandboxed-iframe contexts send the literal "null" origin.
            if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
        }
    }
}
