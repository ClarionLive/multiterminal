using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression evidence for the :5050 CORS allowlist. Introduced two-tier in Eval P2
    /// (task c522764d); tightened to a single strict ENUMERATED policy in Eval P2c (task f9697aac,
    /// PM ruling Z) once tasks-panel.html migrated off file:// onto the virtual-host origin.
    /// <see cref="RestCorsOriginPolicy.IsTrustedBrowserOrigin"/> now admits ONLY the panel virtual
    /// host (<see cref="PanelHosting.Origin"/>) — the sole legitimate browser caller by census — and
    /// rejects everything else: the literal "null" (file:// / opaque-origin iframes) AND loopback
    /// origins of any port. That loopback rejection is the load-bearing bit: it preserves the
    /// same-site read protection the retired CrossOriginBrowserGuard gave the token/config GETs (a
    /// malicious http://localhost:9999 page must not be able to read a secret cross-origin).
    /// </summary>
    public class RestCorsOriginPolicyTests
    {
        // Loopback origins — REJECTED under ruling Z (a "class" of origins is never trusted; only the
        // enumerated virtual host is). This is the same-site-read regression Grace caught (87db18a7).
        public static readonly object[][] LoopbackOrigins =
        {
            new object[] { "http://localhost:5050" },
            new object[] { "http://localhost" },
            new object[] { "https://localhost:7000" },
            new object[] { "http://localhost:9999" }, // <-- the malicious other-loopback-port page
            new object[] { "http://127.0.0.1:5050" },
            new object[] { "http://127.0.0.1" },
            new object[] { "https://127.0.0.1:9999" },
            new object[] { "http://[::1]:5050" },
            new object[] { "http://[::1]" },
        };

        // Remote / malformed / wrong-form origins — also rejected (the CSRF/exfil win + fail-closed).
        public static readonly object[][] OtherBlockedOrigins =
        {
            new object[] { "https://evil.example" },
            new object[] { "https://evil.example:443" },
            new object[] { "http://attacker.test" },
            new object[] { "https://tasks.google.com" },
            new object[] { "http://192.168.1.50" },   // LAN
            new object[] { "http://10.0.0.5:5050" },   // private
            new object[] { "http://169.254.1.1" },     // link-local
            new object[] { "http://localhost.evil.example" },       // suffix trick
            new object[] { "http://mt-panels.local.evil.example" }, // virtual-host suffix trick
            new object[] { "https://mt-panels.local" },             // right host, WRONG scheme
            new object[] { "" },
            new object[] { null },
            new object[] { "not-a-uri" },
            new object[] { "localhost" },        // bare host, not an absolute origin
            new object[] { "//localhost:5050" }, // scheme-relative
        };

        [Fact]
        public void Trusted_origin_allows_ONLY_panel_virtual_host()
        {
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin(PanelHosting.Origin));
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin("HTTP://MT-PANELS.LOCAL")); // case-insensitive
        }

        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        [InlineData("Null")]
        public void Trusted_origin_REJECTS_null(string origin)
        {
            // file:// and allow-scripts sandboxed iframes send "null" — not an enumerated member.
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        public void Trusted_origin_REJECTS_loopback_of_any_port(string origin)
        {
            // Ruling Z: loopback is a class, not an enumerated member. A page on another loopback port
            // (http://localhost:9999) must get NO ACAO — this is the proof that retiring the guard did
            // not regress its same-site read protection on the secret-bearing GETs (finding 87db18a7).
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(OtherBlockedOrigins))]
        public void Trusted_origin_blocks_remote_and_malformed(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }
    }
}
