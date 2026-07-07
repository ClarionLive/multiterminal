using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MultiTerminal.API
{
    /// <summary>
    /// Global CSRF write-guard for the :5050 REST surface (Eval P2c, task f9697aac). Generalizes the
    /// retired per-endpoint <c>CrossOriginBrowserGuard</c> to the whole API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CORS is a READ boundary only. The browser sends a cross-site request regardless of the CORS
    /// policy and merely withholds the <i>response</i> from the caller's JavaScript when the origin is
    /// not allowlisted. So a blind cross-site CSRF form-POST / fetch WRITE still executes server-side —
    /// CORS never rejected it. This middleware closes that write boundary by rejecting browser-issued
    /// cross-site / same-site WRITES (unsafe HTTP methods) using the Fetch Metadata
    /// <c>Sec-Fetch-Site</c> header, with <c>Origin</c> as a fallback signal.
    /// </para>
    /// <para>
    /// <b>Header-absent = ALLOW</b> is load-bearing: the entire non-browser control plane —
    /// the Node MCP server, Claude Code hooks, PowerShell, curl, and in-process/test callers — talks to
    /// :5050 with no <c>Sec-Fetch-Site</c> and no <c>Origin</c> header, and must never be gated. Only a
    /// real browser sets these headers, so their presence (with a cross-site/same-site value or a
    /// non-trusted Origin) is what distinguishes a hostile cross-origin write from legitimate tooling.
    /// </para>
    /// <para>
    /// Scope: only state-changing methods (POST/PUT/PATCH/DELETE) are guarded. GET/HEAD are reads
    /// (gated by CORS); OPTIONS is the CORS preflight and must pass. The guard runs on :5050 only —
    /// the :5100 phone gateway has its own auth layer (RemoteMode's Origin gate + gateway phone-auth),
    /// so these are independent, additive layers, not a double-gate conflict.
    /// </para>
    /// <para>
    /// Rejections are emitted as RFC 7807 <c>application/problem+json</c> 403s via the registered
    /// <c>IProblemDetailsService</c>, matching the response contract standardized in task 7ce19175.
    /// </para>
    /// </remarks>
    internal sealed class SecFetchSiteWriteGuardMiddleware
    {
        private readonly RequestDelegate _next;

        public SecFetchSiteWriteGuardMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
        {
            if (IsCrossSiteBrowserWrite(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = "Cross-site request blocked",
                        Detail = "Cross-site browser writes are not permitted on this API.",
                    },
                }).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
        }

        /// <summary>
        /// True when the request is a state-changing method issued by an untrusted browser context.
        /// A caller sending neither <c>Origin</c> nor a cross-site/same-site <c>Sec-Fetch-Site</c> —
        /// every non-browser client — returns false (allowed). Safe methods (GET/HEAD/OPTIONS) are
        /// never guarded here (reads are CORS's job; OPTIONS is preflight).
        /// </summary>
        internal static bool IsCrossSiteBrowserWrite(HttpRequest request)
        {
            if (request == null)
                return false;

            if (!IsUnsafeMethod(request.Method))
                return false;

            // Trust-first, mirroring the CORS read allowlist: an Origin from a trusted origin (the
            // panel virtual host) may write. Checking trust BEFORE Sec-Fetch-Site matters because the
            // panel is served cross-site to :5050 (its writes would carry Sec-Fetch-Site: cross-site),
            // so a Sec-Fetch-Site-first check would wrongly reject a trusted write. (No panel writes
            // today — the panel only GETs — but this keeps the read and write allowlists consistent.)
            string origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
                return !RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin);

            // No Origin header. Fall back to Fetch Metadata: "cross-site" / "same-site" means a
            // different-origin browsing context issued this write (the CSRF form-POST path some
            // browsers send without an Origin) — refuse. "same-origin" / "none" (address bar) and a
            // missing header are non-browser or same-origin callers — allow.
            string fetchSite = request.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(fetchSite) &&
                (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase) ||
                 fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private static bool IsUnsafeMethod(string method)
        {
            return HttpMethods.IsPost(method)
                || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method)
                || HttpMethods.IsDelete(method);
        }
    }

    /// <summary>Pipeline registration helper for <see cref="SecFetchSiteWriteGuardMiddleware"/>.</summary>
    internal static class SecFetchSiteWriteGuardMiddlewareExtensions
    {
        public static IApplicationBuilder UseSecFetchSiteWriteGuard(this IApplicationBuilder app)
            => app.UseMiddleware<SecFetchSiteWriteGuardMiddleware>();
    }
}
