using System;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// Shared guard for token-returning REST endpoints that are otherwise loopback-gated.
    /// The global CORS policy is AllowAnyOrigin (a separate hardening ticket), so a loopback
    /// check alone lets a malicious web page fetch a 127.0.0.1 token URL cross-origin and read
    /// the PAT from the browser. A cross-site browser fetch always carries an <c>Origin</c>
    /// header (whose host is not loopback) and/or a <c>Sec-Fetch-Site</c> of
    /// <c>cross-site</c>/<c>same-site</c>; a local agent using HttpClient sends neither. This
    /// distinguishes the two so legitimate local agents pass while browser cross-origin reads
    /// are refused.
    /// </summary>
    internal static class CrossOriginBrowserGuard
    {
        /// <summary>
        /// Returns false when the request looks like a browser cross-origin/same-origin fetch
        /// (i.e. it carries an Origin header for a non-loopback host, or a Sec-Fetch-Site of
        /// cross-site/same-site). A missing Origin AND no cross-site Sec-Fetch-Site is treated
        /// as a non-browser local caller and allowed.
        /// </summary>
        public static bool IsLocalNonBrowserCaller(HttpRequest request)
        {
            if (request == null)
                return true;

            // An Origin header is only set by browsers. If present and its host is not loopback,
            // this is a cross-origin browser fetch — refuse. (Same-origin loopback pages can also
            // set Origin, but those are the host's own UI, not a remote attacker.)
            string origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
                return false;

            // Sec-Fetch-Site is a Fetch Metadata header set by modern browsers. "cross-site" and
            // "same-site" mean the request was issued by a (different-origin) browser context;
            // "same-origin" / "none" (address-bar) and a missing header are treated as allowed.
            string fetchSite = request.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(fetchSite) &&
                (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase) ||
                 fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        /// <summary>
        /// True when an Origin header value (scheme://host[:port]) resolves to a loopback host.
        /// A value that can't be parsed is treated as non-loopback (fail closed).
        /// </summary>
        private static bool IsLoopbackOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;

            string host = uri.Host;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }
    }
}
