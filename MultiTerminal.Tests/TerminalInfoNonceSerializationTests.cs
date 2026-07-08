using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiTerminal.MCPServer.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Security regression guard for the launch-nonce proof-of-origin (task fd3437e6).
    /// <para><see cref="TerminalInfo.LaunchNonce"/> is a SECRET: MT injects it into a terminal's child
    /// env and the adoption gate requires a matching echo before letting a registration claim an
    /// "Unassigned" placeholder. If the nonce were serialized out of <c>GetTerminals()</c> /
    /// <c>GET /api/messaging/terminals</c> / the gateway <c>/api/terminals</c> / the MCP
    /// <c>list_terminals</c> tool, any agent that can list terminals could read it and replay it,
    /// collapsing proof-of-origin into a bearer token (codex-security-auditor A01/CWE-200). These
    /// tests fail loudly if a future edit drops the <see cref="JsonIgnoreAttribute"/>.</para>
    /// </summary>
    public class TerminalInfoNonceSerializationTests
    {
        private const string Secret = "NONCE-SENTINEL-3c7f9a12b4e6d8f0";

        [Fact]
        public void LaunchNonce_property_is_JsonIgnored()
        {
            var prop = typeof(TerminalInfo).GetProperty(
                nameof(TerminalInfo.LaunchNonce),
                BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(prop);
            Assert.NotNull(prop.GetCustomAttribute<JsonIgnoreAttribute>());
        }

        [Fact]
        public void Serializing_a_terminal_never_leaks_the_launch_nonce()
        {
            var terminal = new TerminalInfo
            {
                Id = "abc123",
                Name = "Alice",
                DocId = "doc12345",
                LaunchNonce = Secret,
            };

            // Serialize the way the REST API does (System.Text.Json). The secret value AND the
            // property name must both be absent from the wire payload.
            string json = JsonSerializer.Serialize(terminal);

            Assert.DoesNotContain(Secret, json);
            Assert.DoesNotContain("LaunchNonce", json);
            Assert.DoesNotContain("launchNonce", json);

            // Sanity: non-secret fields ARE serialized, so this test proves suppression, not a no-op.
            Assert.Contains("doc12345", json);
        }
    }
}
