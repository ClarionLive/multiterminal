using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression evidence for the :5050 CORS allowlist. Introduced two-tier in Eval P2
    /// (task c522764d) and tightened to a SINGLE strict policy in Eval P2c (task f9697aac)
    /// once tasks-panel.html migrated off file:// onto the virtual-host origin. The default
    /// policy (<see cref="RestCorsOriginPolicy.IsTrustedBrowserOrigin"/>) now admits loopback
    /// origins PLUS the panel virtual host (<see cref="PanelHosting.Origin"/>) and rejects
    /// everything else — critically the literal "null" that file:// / opaque-origin sandboxed
    /// iframes send, which is no longer tolerated anywhere (the f9697aac read-boundary close).
    /// </summary>
    public class RestCorsOriginPolicyTests
    {
        // Loopback origins — accepted by IsLoopbackOrigin AND IsTrustedBrowserOrigin.
        public static readonly object[][] LoopbackOrigins =
        {
            new object[] { "http://localhost:5050" },
            new object[] { "http://localhost" },
            new object[] { "https://localhost:7000" },
            new object[] { "http://127.0.0.1:5050" },
            new object[] { "http://127.0.0.1" },
            new object[] { "https://127.0.0.1:9999" },
            new object[] { "http://[::1]:5050" },
            new object[] { "http://[::1]" },
            new object[] { "HTTP://LOCALHOST:5050" }, // case-insensitive host
        };

        // Remote / non-loopback / malformed origins rejected by BOTH predicates (the CSRF win + fail-closed).
        public static readonly object[][] BlockedOrigins =
        {
            new object[] { "https://evil.example" },
            new object[] { "https://evil.example:443" },
            new object[] { "http://attacker.test" },
            new object[] { "https://tasks.google.com" },
            new object[] { "http://192.168.1.50" },   // LAN, non-loopback
            new object[] { "http://10.0.0.5:5050" },  // private, non-loopback
            new object[] { "http://169.254.1.1" },    // link-local, non-loopback
            new object[] { "http://localhost.evil.example" },  // suffix trick, host != localhost
            new object[] { "http://127.0.0.1.evil.example" },  // prefix trick, host != loopback
            new object[] { "http://mt-panels.local.evil.example" }, // virtual-host suffix trick
            new object[] { "https://mt-panels.local" },        // right host, WRONG scheme (origin is http)
            new object[] { "" },
            new object[] { null },
            new object[] { "not-a-uri" },
            new object[] { "localhost" },        // bare host, not an absolute origin
            new object[] { "//localhost:5050" }, // scheme-relative, not absolute
        };

        // ---- Default policy: IsTrustedBrowserOrigin (loopback + panel virtual host, NO null) ----

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        public void Trusted_origin_allows_loopback(string origin)
        {
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Fact]
        public void Trusted_origin_allows_panel_virtual_host()
        {
            // tasks-panel.html now fetches :5050 from this origin — CORS must expose responses to it.
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin(PanelHosting.Origin));
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin("HTTP://MT-PANELS.LOCAL")); // case-insensitive
        }

        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        [InlineData("Null")]
        public void Trusted_origin_REJECTS_null(string origin)
        {
            // The whole point of f9697aac's read-boundary close: after the virtual-host migration
            // NO caller sends "null", so the strict policy rejects it everywhere. A file:// page or
            // an allow-scripts sandboxed iframe (opaque origin → "null") can no longer read responses.
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(BlockedOrigins))]
        public void Trusted_origin_blocks_remote_and_malformed(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        // ---- Pure loopback helper: IsLoopbackOrigin (loopback ONLY — NOT the virtual host) ----

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        public void Loopback_helper_allows_loopback(string origin)
        {
            Assert.True(RestCorsOriginPolicy.IsLoopbackOrigin(origin));
        }

        [Fact]
        public void Loopback_helper_does_NOT_allow_panel_virtual_host()
        {
            // The virtual host is not loopback; only IsTrustedBrowserOrigin admits it. Keeping the
            // helper pure lets the write-guard reason about "loopback" vs "trusted" independently.
            Assert.False(RestCorsOriginPolicy.IsLoopbackOrigin(PanelHosting.Origin));
        }

        [Theory]
        [InlineData("null")]
        [MemberData(nameof(BlockedOrigins))]
        public void Loopback_helper_blocks_null_remote_and_malformed(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsLoopbackOrigin(origin));
        }
    }
}
