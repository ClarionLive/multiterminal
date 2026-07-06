using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression evidence for the two-tier CORS allowlist (Eval P2 item 3, task c522764d).
    /// The default policy (IsLoopbackOrigin) is strict loopback-only; the scoped null-tolerant
    /// policy (IsAllowedOrigin, applied only to TaskReportsController) additionally allows the
    /// file://-panel "null" origin. Proves both admit loopback + reject drive-by remote origins
    /// (the CSRF/exfil win), and — critically — that the DEFAULT policy rejects "null" so the
    /// null-origin read-window is scoped to the one controller. See ticket f9697aac.
    /// </summary>
    public class RestCorsOriginPolicyTests
    {
        // Loopback origins accepted by BOTH policies.
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

        // Remote / non-loopback / malformed origins rejected by BOTH policies (the CSRF win + fail-closed).
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
            new object[] { "" },
            new object[] { null },
            new object[] { "not-a-uri" },
            new object[] { "localhost" },        // bare host, not an absolute origin
            new object[] { "//localhost:5050" }, // scheme-relative, not absolute
        };

        // ---- DEFAULT policy: IsLoopbackOrigin (strict — NO null) ----

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        public void Default_policy_allows_loopback(string origin)
        {
            Assert.True(RestCorsOriginPolicy.IsLoopbackOrigin(origin));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        [InlineData("Null")]
        public void Default_policy_REJECTS_null(string origin)
        {
            // The strict default must NOT accept the file:// "null" origin — that tolerance is
            // scoped to TaskReportsController via the named policy only. This is the whole point
            // of the two-tier design (shrinks the interim null read-window to one controller).
            Assert.False(RestCorsOriginPolicy.IsLoopbackOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(BlockedOrigins))]
        public void Default_policy_blocks_remote_and_malformed(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsLoopbackOrigin(origin));
        }

        // ---- SCOPED policy: IsAllowedOrigin (loopback + null) ----

        [Theory]
        [MemberData(nameof(LoopbackOrigins))]
        public void Scoped_policy_allows_loopback(string origin)
        {
            Assert.True(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        [InlineData("Null")]
        public void Scoped_policy_allows_null_for_file_panel(string origin)
        {
            // tasks-panel.html is loaded via file:// so its fetch Origin serializes to "null".
            // Tolerated ONLY on the scoped (TaskReportsController) policy until f9697aac.
            Assert.True(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }

        [Theory]
        [MemberData(nameof(BlockedOrigins))]
        public void Scoped_policy_blocks_remote_and_malformed(string origin)
        {
            // The scoped policy tolerates "null" but must STILL reject drive-by remote origins.
            Assert.False(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }
    }
}
