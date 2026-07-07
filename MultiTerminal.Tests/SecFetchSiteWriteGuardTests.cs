using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Evidence for the :5050 write boundary (task f9697aac): the global
    /// <see cref="SecFetchSiteWriteGuardMiddleware"/> rejects blind cross-site browser WRITES while
    /// letting header-absent non-browser callers (Node MCP / hooks / curl) through. Covers the
    /// chartered negative tests (cross-origin form-POST REJECTED; headerless MCP call PASSES), the
    /// trust-first exemption for the panel virtual host, and the middleware's actual 403/pass-through
    /// execution.
    /// </summary>
    /// <remarks>
    /// The CORS READ boundary (incl. Alice's condition-1 same-site-loopback-port regression: a
    /// http://localhost:9999 page must get NO Access-Control-Allow-Origin on a secret GET) is proven
    /// in <see cref="RestCorsOriginPolicyTests"/>: the default CORS policy is
    /// <c>SetIsOriginAllowed(RestCorsOriginPolicy.IsTrustedBrowserOrigin)</c>, and ASP.NET emits ACAO
    /// if and only if that predicate returns true — so asserting the predicate returns false for
    /// http://localhost:9999 IS the no-ACAO proof.
    /// </remarks>
    public class SecFetchSiteWriteGuardTests
    {
        private static HttpRequest Request(string method, (string, string)[] headers = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            if (headers != null)
                foreach (var (k, v) in headers)
                    ctx.Request.Headers[k] = v;
            return ctx.Request;
        }

        // ---- Unit: IsCrossSiteBrowserWrite decision logic ----

        [Theory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        [InlineData("OPTIONS")] // CORS preflight — must never be guarded
        public void Safe_methods_are_never_guarded(string method)
        {
            Assert.False(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(
                Request(method, new[] { ("Sec-Fetch-Site", "cross-site"), ("Origin", "https://evil.example") })));
        }

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("PATCH")]
        [InlineData("DELETE")]
        public void Headerless_write_is_allowed_for_all_unsafe_methods(string method)
        {
            // Node MCP server, hooks, curl, HttpClient — no fetch-metadata, no Origin. Load-bearing.
            Assert.False(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(Request(method)));
        }

        [Theory]
        [InlineData("same-origin")]
        [InlineData("none")]
        public void Same_origin_or_navigation_write_is_allowed(string fetchSite)
        {
            Assert.False(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(
                Request("POST", new[] { ("Sec-Fetch-Site", fetchSite) })));
        }

        [Theory]
        [InlineData("cross-site")]
        [InlineData("same-site")]
        public void Cross_context_write_without_origin_is_rejected(string fetchSite)
        {
            // The CSRF form-POST path where the browser omits Origin but sends Fetch Metadata.
            Assert.True(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(
                Request("POST", new[] { ("Sec-Fetch-Site", fetchSite) })));
        }

        [Theory]
        [InlineData("https://evil.example")]
        [InlineData("http://localhost:9999")]   // same-site loopback-port write — rejected too
        [InlineData("http://mt-panels.local")]  // NEG TEST 1: static/guessable panel-ish host — rejected
        [InlineData("null")]
        public void Untrusted_origin_write_is_rejected(string origin)
        {
            Assert.True(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(
                Request("POST", new[] { ("Origin", origin), ("Sec-Fetch-Site", "cross-site") })));
        }

        [Fact]
        public void Trusted_virtual_host_write_is_allowed_even_when_cross_site()
        {
            // The panel is served cross-site to :5050, so a (hypothetical) panel write carries
            // Sec-Fetch-Site: cross-site with a trusted Origin. Trust-first must allow it.
            Assert.False(SecFetchSiteWriteGuardMiddleware.IsCrossSiteBrowserWrite(
                Request("POST", new[] { ("Origin", PanelHosting.Origin), ("Sec-Fetch-Site", "cross-site") })));
        }

        // ---- Middleware execution: real InvokeAsync (403 short-circuit vs. pass-through) ----

        private sealed class GuardRun
        {
            public bool NextCalled { get; init; }
            public int StatusCode { get; init; }
        }

        private static async Task<GuardRun> InvokeAsync(string method, (string, string)[] headers)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProblemDetails();
            using var provider = services.BuildServiceProvider();

            var ctx = new DefaultHttpContext { RequestServices = provider };
            ctx.Request.Method = method;
            ctx.Response.Body = new MemoryStream();
            if (headers != null)
                foreach (var (k, v) in headers)
                    ctx.Request.Headers[k] = v;

            bool nextCalled = false;
            var middleware = new SecFetchSiteWriteGuardMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(ctx, provider.GetRequiredService<IProblemDetailsService>());
            return new GuardRun { NextCalled = nextCalled, StatusCode = ctx.Response.StatusCode };
        }

        [Fact]
        public async Task Cross_site_form_POST_is_short_circuited_403()
        {
            var run = await InvokeAsync("POST", new[]
            {
                ("Origin", "https://evil.example"),
                ("Sec-Fetch-Site", "cross-site"),
            });

            Assert.False(run.NextCalled); // request never reached the controller
            Assert.Equal(StatusCodes.Status403Forbidden, run.StatusCode);
        }

        [Fact]
        public async Task Headerless_MCP_POST_passes_through()
        {
            var run = await InvokeAsync("POST", headers: null); // no Origin, no Sec-Fetch-Site

            Assert.True(run.NextCalled);
            Assert.NotEqual(StatusCodes.Status403Forbidden, run.StatusCode);
        }

        [Fact]
        public async Task Trusted_virtual_host_POST_passes_through()
        {
            var run = await InvokeAsync("POST", new[]
            {
                ("Origin", PanelHosting.Origin),
                ("Sec-Fetch-Site", "cross-site"),
            });

            Assert.True(run.NextCalled);
            Assert.NotEqual(StatusCodes.Status403Forbidden, run.StatusCode);
        }
    }
}
