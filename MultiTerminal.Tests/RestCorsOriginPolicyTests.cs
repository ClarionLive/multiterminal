using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression evidence for the :5050 CORS allowlist after the Eval P2c (task f9697aac) redesign.
    /// The scoped-read predicate <see cref="RestCorsOriginPolicy.IsTrustedBrowserOrigin"/> trusts ONLY
    /// the per-process, CSPRNG-random, non-resolvable panel origin (<see cref="PanelHosting.Origin"/>).
    /// A STATIC/guessable host (e.g. http://mt-panels.local) is rejected — that is the proof that the
    /// nameable-origin vector (pipeline Run 1 HIGH) is closed. The default policy
    /// (<see cref="RestCorsOriginPolicy.DenyAllOrigins"/>) denies every origin so controllers that don't
    /// opt into the scoped policy expose no ACAO.
    /// </summary>
    public class RestCorsOriginPolicyTests
    {
        // Loopback origins — REJECTED (a "class" of origins is never trusted; only the enumerated,
        // unguessable per-process origin is). Includes the same-site loopback-port regression case.
        public static readonly object[][] LoopbackOrigins =
        {
            new object[] { "http://localhost:5050" },
            new object[] { "http://localhost" },
            new object[] { "http://localhost:9999" }, // malicious other-loopback-port page
            new object[] { "http://127.0.0.1:5050" },
            new object[] { "http://[::1]:5050" },
        };

        // Remote / malformed / STATIC-guessable origins — all rejected (nameable-origin vector closed).
        public static readonly object[][] OtherBlockedOrigins =
        {
            new object[] { "https://evil.example" },
            new object[] { "http://attacker.test" },
            new object[] { "http://192.168.1.50" },              // LAN
            new object[] { "http://mt-panels.local" },           // NEG TEST 1: static/guessable — REJECTED
            new object[] { "https://mt-panels.local" },
            new object[] { "http://mt-panels.invalid" },         // right TLD, MISSING the random token
            new object[] { "http://mt-panels-.invalid" },        // empty token
            new object[] { "http://mt-panels.local.evil.example" },
            new object[] { "" },
            new object[] { null },
            new object[] { "not-a-uri" },
        };

        [Fact]
        public void Trusted_origin_allows_ONLY_the_per_process_panel_origin()
        {
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin(PanelHosting.Origin));
            Assert.True(RestCorsOriginPolicy.IsTrustedBrowserOrigin(PanelHosting.Origin.ToUpperInvariant())); // case-insensitive
        }

        [Fact]
        public void Panel_origin_is_random_and_non_resolvable()
        {
            // Guards the security-critical shape: unguessable token + .invalid TLD (RFC 6761 NXDOMAIN).
            Assert.StartsWith("http://mt-panels-", PanelHosting.Origin);
            Assert.EndsWith(".invalid", PanelHosting.Origin);
            Assert.DoesNotContain("mt-panels.local", PanelHosting.Origin);
            // 128-bit token hex-encoded = 32 hex chars between the "mt-panels-" prefix and ".invalid".
            var token = PanelHosting.VirtualHostName.Replace("mt-panels-", "").Replace(".invalid", "");
            Assert.Equal(32, token.Length);
            Assert.Matches("^[0-9a-f]{32}$", token);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        public void Trusted_origin_REJECTS_null(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        [MemberData(nameof(OtherBlockedOrigins))]
        public void Trusted_origin_rejects_everything_but_the_panel_origin(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin(origin));
        }

        [Fact]
        public void Wrong_scheme_of_the_panel_host_is_rejected()
        {
            // Right (random) host, wrong scheme — origin is http, not https.
            Assert.False(RestCorsOriginPolicy.IsTrustedBrowserOrigin("https://" + PanelHosting.VirtualHostName));
        }

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        [MemberData(nameof(OtherBlockedOrigins))]
        public void Default_policy_denies_every_origin(string origin)
        {
            Assert.False(RestCorsOriginPolicy.DenyAllOrigins(origin));
        }

        [Fact]
        public void Default_policy_denies_even_the_panel_origin()
        {
            // The panel origin is trusted ONLY via the scoped [EnableCors] policy on TaskReportsController;
            // the DEFAULT policy (all other controllers, incl. the secret GETs) denies it too.
            Assert.False(RestCorsOriginPolicy.DenyAllOrigins(PanelHosting.Origin));
        }
    }
}
