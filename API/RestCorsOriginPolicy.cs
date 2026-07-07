using System;

namespace MultiTerminal.API
{
    /// <summary>
    /// CORS origin allowlist for the :5050 REST surface. Introduced two-tier in Eval P2
    /// (task c522764d); redesigned in Eval P2c (task f9697aac) to a least-privilege scheme after two
    /// rounds of adversarial hardening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default policy: deny ALL cross-origin</b> (<see cref="DenyAllOrigins"/>). No controller needs
    /// a browser origin by default — the only browser reader of :5050 is tasks-panel.html, and it reads
    /// exactly one controller. Every other controller (including the loopback-gated PAT/secret GETs on
    /// OwnerProfile / SourceControlAccounts / MultiConnect) therefore exposes NO
    /// <c>Access-Control-Allow-Origin</c> to any browser origin.
    /// </para>
    /// <para>
    /// <b>Scoped read policy</b> (<see cref="PanelReadPolicyName"/>, applied via <c>[EnableCors]</c> to
    /// <c>TaskReportsController</c>'s report GETs only) — <see cref="IsTrustedBrowserOrigin"/>: admits the
    /// per-process panel virtual-host origin (<see cref="PanelHosting.Origin"/>) and nothing else. This is
    /// the READ boundary for the one controller the panel actually fetches: a disallowed origin gets no
    /// ACAO, so the browser refuses to expose the response to the calling page's JS.
    /// </para>
    /// <para>
    /// <b>Why this shape (task f9697aac, pipeline Run 1 HIGH + LOW).</b> Two earlier iterations trusted a
    /// CLASS or a NAMEABLE origin: (1) "any loopback origin" trusted every local process's page on every
    /// port; (2) a fixed <c>mt-panels.local</c> was a guessable/claimable name, so any browser page loaded
    /// under it inherited trust. The fix restores the retired guard's real property — trust NO nameable
    /// browser origin — by making <see cref="PanelHosting.Origin"/> a per-process CSPRNG-random,
    /// non-resolvable host, and by scoping even that origin to the single controller that needs it
    /// (least privilege — the secret controllers have no browser consumer). Any FUTURE browser consumer
    /// adds its origin as a new enumerated member here PLUS a scoped <c>[EnableCors]</c> on the specific
    /// controller it reads — never a class, never the global default.
    /// </para>
    /// <para>
    /// CORS is a READ boundary. Blind cross-site CSRF WRITES are handled by
    /// <see cref="SecFetchSiteWriteGuardMiddleware"/> (which shares <see cref="IsTrustedBrowserOrigin"/> as
    /// its trusted-origin test, so read-trust and write-trust cannot desync). Non-browser callers (Node
    /// MCP, hooks, curl, HttpClient) send no Origin header, so CORS never applies to them.
    /// </para>
    /// </remarks>
    internal static class RestCorsOriginPolicy
    {
        /// <summary>
        /// Name of the scoped read policy applied (via <c>[EnableCors]</c>) to TaskReportsController's
        /// report GETs — the only browser-read surface on :5050.
        /// </summary>
        public const string PanelReadPolicyName = "PanelReports";

        /// <summary>
        /// Default-policy predicate: denies EVERY origin. Used by the default CORS policy so any
        /// controller that does not opt into <see cref="PanelReadPolicyName"/> exposes no ACAO to any
        /// browser origin. (A named method rather than an inline lambda so it is unit-testable.)
        /// </summary>
        public static bool DenyAllOrigins(string origin) => false;

        /// <summary>
        /// Scoped-read predicate: true ONLY for the per-process panel virtual-host origin
        /// (<see cref="PanelHosting.Origin"/> — a CSPRNG-random <c>.invalid</c> host, so unforgeable and
        /// non-resolvable). Rejects "null", loopback (any port), remote, empty, and malformed origins
        /// (fail closed). Also the trusted-origin test the <see cref="SecFetchSiteWriteGuardMiddleware"/>
        /// shares.
        /// </summary>
        public static bool IsTrustedBrowserOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return false;

            return string.Equals(origin, PanelHosting.Origin, StringComparison.OrdinalIgnoreCase);
        }
    }
}
