using System;
using System.Security.Cryptography;

namespace MultiTerminal.API
{
    /// <summary>
    /// Single source of truth for the WebView2 virtual-host origin that serves the in-app panels
    /// which fetch the :5050 REST API (currently only <c>TasksPanel/tasks-panel.html</c>).
    /// </summary>
    /// <remarks>
    /// Task f9697aac migrated tasks-panel.html off <c>file://</c> onto a virtual host via
    /// <c>CoreWebView2.SetVirtualHostNameToFolderMapping</c>. The host is a <b>per-process,
    /// cryptographically-random, non-resolvable</b> name — this is a security requirement, not a
    /// cosmetic one (pipeline Run 1, both Codex gates, HIGH):
    /// <list type="bullet">
    ///   <item><b>Random (128-bit CSPRNG).</b> The origin is the CAPABILITY that the CORS scoped-read
    ///   policy and the write-guard trust. A fixed, guessable host (e.g. a static <c>mt-panels.local</c>)
    ///   is a nameable trust token: any browser page an attacker can get loaded under that origin would
    ///   inherit the panel's read/write trust. An unguessable per-process host cannot be claimed or
    ///   served by an attacker, so the origin string once again proves provenance.</item>
    ///   <item><b><c>.invalid</c> TLD (RFC 6761 — guaranteed NXDOMAIN).</b> DELIBERATE: no external DNS
    ///   or mDNS resolver will ever resolve it, so a normal browser outside this WebView2 process cannot
    ///   even load the origin. Do NOT "helpfully" change this to <c>.local</c> — <c>.local</c> is real
    ///   mDNS resolution space and an attacker on the LAN could claim the name. WebView2 intercepts the
    ///   host in-process via the folder mapping, so a non-resolvable TLD costs nothing internally.</item>
    /// </list>
    /// Three consumers must share the ONE runtime value or the panel silently breaks / a hole reopens:
    /// the panel loader (<see cref="MultiTerminal.TasksPanel.TasksPanelControl"/>) maps the host and
    /// navigates to it; the CORS scoped-read policy (<see cref="RestCorsOriginPolicy.IsTrustedBrowserOrigin"/>)
    /// permits this origin to READ TaskReportsController; the write-guard
    /// (<see cref="SecFetchSiteWriteGuardMiddleware"/>) treats it as trusted for WRITES. They all read the
    /// static fields below, so there is exactly one value per process and no drift.
    /// <para>
    /// The <c>http</c> scheme is deliberate: the panel fetches <c>http://localhost:5050</c>, so an
    /// <c>http</c> origin keeps that request http→http and sidesteps browser mixed-content blocking.
    /// </para>
    /// </remarks>
    internal static class PanelHosting
    {
        // 128-bit token from a CSPRNG (RandomNumberGenerator, NOT Guid/Math.Random), hex-encoded so the
        // value is a valid DNS label (0-9a-f only). Generated once per process at type load.
        private static readonly string Token = GenerateToken();

        /// <summary>
        /// Per-process virtual hostname mapped to the panel's folder via
        /// <c>CoreWebView2.SetVirtualHostNameToFolderMapping</c>. Random + <c>.invalid</c>: unguessable
        /// and guaranteed-non-resolvable outside this WebView2 process. WebView2 intercepts it in-process,
        /// so no DNS/mDNS resolution occurs for the legitimate panel.
        /// </summary>
        public static readonly string VirtualHostName = "mt-panels-" + Token + ".invalid";

        /// <summary>
        /// The serialized <c>Origin</c> panels served from <see cref="VirtualHostName"/> send on their
        /// cross-origin fetches to :5050. Compared (ordinal, case-insensitive) by the CORS scoped-read
        /// predicate and the write-guard trust check.
        /// </summary>
        public static readonly string Origin = "http://" + VirtualHostName;

        private static string GenerateToken()
        {
            byte[] bytes = new byte[16]; // 128 bits
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
