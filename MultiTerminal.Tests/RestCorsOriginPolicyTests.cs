using MultiTerminal.API;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression evidence for the reshaped CORS allowlist (Eval P2 item 3, task c522764d).
    /// Proves the drive-by remote-web-page CSRF/exfil origins are rejected (the security win)
    /// while the single legitimate file://-origin panel caller ("null") and loopback origins
    /// are still allowed. See <see cref="RestCorsOriginPolicy"/> and ticket f9697aac.
    /// </summary>
    public class RestCorsOriginPolicyTests
    {
        // ---- ALLOWED: loopback origins (any scheme/port) ----
        [Theory]
        [InlineData("http://localhost:5050")]
        [InlineData("http://localhost")]
        [InlineData("https://localhost:7000")]
        [InlineData("http://127.0.0.1:5050")]
        [InlineData("http://127.0.0.1")]
        [InlineData("https://127.0.0.1:9999")]
        [InlineData("http://[::1]:5050")]
        [InlineData("http://[::1]")]
        [InlineData("HTTP://LOCALHOST:5050")] // case-insensitive host
        public void Allows_loopback_origins(string origin)
        {
            Assert.True(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }

        // ---- ALLOWED: the literal "null" origin (file:// panel = tasks-panel.html) ----
        [Theory]
        [InlineData("null")]
        [InlineData("NULL")]
        [InlineData("Null")]
        public void Allows_null_origin_for_file_scheme_panel(string origin)
        {
            // tasks-panel.html is loaded via file:// so its fetch Origin serializes to "null".
            // Tolerated until ticket f9697aac migrates it to a real virtual-host origin.
            Assert.True(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }

        // ---- BLOCKED: drive-by remote origins (the CSRF/exfil threat) — NEGATIVE CHECK ----
        [Theory]
        [InlineData("https://evil.example")]
        [InlineData("https://evil.example:443")]
        [InlineData("http://attacker.test")]
        [InlineData("https://tasks.google.com")]
        [InlineData("http://192.168.1.50")]   // LAN, non-loopback
        [InlineData("http://10.0.0.5:5050")]  // private, non-loopback
        [InlineData("http://169.254.1.1")]    // link-local, non-loopback
        [InlineData("http://localhost.evil.example")] // suffix trick, host != localhost
        [InlineData("http://127.0.0.1.evil.example")] // prefix trick, host != loopback
        public void Blocks_remote_origins(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }

        // ---- BLOCKED: empty / malformed origins (fail closed) ----
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("not-a-uri")]
        [InlineData("localhost")]        // bare host, not an absolute origin
        [InlineData("//localhost:5050")] // scheme-relative, not absolute
        public void Blocks_empty_or_malformed_origins(string origin)
        {
            Assert.False(RestCorsOriginPolicy.IsAllowedOrigin(origin));
        }
    }
}
