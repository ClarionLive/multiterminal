using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task <c>c28e6177</c>, fix B. A requested agent name is canonicalised at the spawn boundary, so
    /// the system has ONE identity rule instead of two that merely usually agree.
    ///
    /// <para><b>Why the boundary and nowhere else.</b> Every comparison downstream is deliberately
    /// untrimmed — the broker's <c>FindUniqueCandidate</c> and <c>GetTerminal</c>, and the plugin
    /// channel server's roster lookup and <c>isAddressedToMe</c>. Trimming in any of THOSE would merge
    /// two rows that already exist, inside the comparison that decides which agent receives a message.
    /// Trimming here cannot merge anything, because nothing has been created yet. That asymmetry is the
    /// whole design and <see cref="MultiTerminal.Services.HelperReadinessTrigger"/> carries the matching
    /// refusal comment.</para>
    ///
    /// <para>Fix B does not replace fix A. It removes the likeliest SOURCE of a whitespace-variant name;
    /// fix A makes the readiness trigger correct even when one exists, which it still can via
    /// <c>register_terminal</c> from a session whose <c>MULTITERMINAL_NAME</c> carries a space.</para>
    /// </summary>
    public sealed class SpawnIdentityCanonicalizationTests
    {
        /// <summary>
        /// The choke point. All three HTTP surfaces trim before calling, but this is what makes the rule
        /// true for a caller nobody has written yet.
        /// </summary>
        [Theory]
        [InlineData("Alice ")]
        [InlineData(" Alice")]
        [InlineData("  Alice  ")]
        [InlineData("\tAlice\r\n")]
        public async Task SpawnTeammateAsync_hands_on_a_canonical_name(string requested)
        {
            string seen = null;
            var service = new SpawnService
            {
                OnSpawnRequested = (name, _, _, _, _) =>
                {
                    seen = name;
                    return Task.FromResult((true, "DOC", (string)null, name));
                },
            };

            await service.SpawnTeammateAsync(requested, null, null, null, "Tester");

            Assert.Equal("Alice", seen);
        }

        /// <summary>
        /// A headless agent is registered with the broker too, so it can hold a whitespace-variant name
        /// exactly as a pane can.
        /// </summary>
        [Fact]
        public async Task SpawnAgentAsync_hands_on_a_canonical_name()
        {
            string seen = null;
            var service = new SpawnService
            {
                OnSpawnAgentRequested = (name, _, _, _, _, _, _) =>
                {
                    seen = name;
                    return Task.FromResult((true, (MultiTerminal.Services.AgentProcess)null, (string)null));
                },
            };

            await service.SpawnAgentAsync(" Bob\t", null, null, null, "Tester");

            Assert.Equal("Bob", seen);
        }

        /// <summary>
        /// Canonicalisation must not turn a blank name into a spawn. A whitespace-only name is still
        /// refused, and refused BEFORE the trim could reduce it to the empty string.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_blank_name_is_still_refused(string requested)
        {
            bool called = false;
            var service = new SpawnService
            {
                OnSpawnRequested = (name, _, _, _, _) =>
                {
                    called = true;
                    return Task.FromResult((true, "DOC", (string)null, name));
                },
            };

            var (success, _, error, _) = await service.SpawnTeammateAsync(requested, null, null, null, "Tester");

            Assert.False(success);
            Assert.False(called, "A blank name reached the spawn callback.");
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        /// <summary>
        /// ⚠️ ORDERING, and it is a real guard rather than a style point. Both spawn endpoints refuse to
        /// spawn a second "Oracle" by comparing the requested name ordinal-ignore-case. That comparison
        /// does not trim, so <c>"Oracle "</c> would sail straight past it — the guard that exists to stop
        /// a duplicate Oracle being defeated by the exact defect class this ticket is about.
        ///
        /// <para>Asserted as a source census because both sites are HTTP handlers: the controller needs
        /// an <c>HttpContext</c> for <c>Problem()</c>, and the gateway route is a minimal-API lambda with
        /// no seam to call. Same technique as <c>InitialPromptTriggerWiringTests</c>.</para>
        /// </summary>
        [Theory]
        [InlineData("API/Controllers/SpawnController.cs", "SpawnTerminal")]
        [InlineData("API/Gateway/GatewayServiceEndpoints.cs", "MapMultiRemoteSpawnEndpoints")]
        public void The_name_is_canonicalised_before_the_oracle_guard_reads_it(string relativePath, string anchor)
        {
            string src = ReadRepoFile(relativePath);

            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find '{anchor}' in {relativePath}; this census is now vacuous.");

            string body = src[start..];

            int trim = body.IndexOf("request.AgentName.Trim()", StringComparison.Ordinal);
            int guard = body.IndexOf("OracleService.OracleName", StringComparison.Ordinal);

            Assert.True(
                trim >= 0,
                $"{relativePath} no longer canonicalises the requested name. The broker never trims one, "
                + "so this surface can mint a terminal whose name differs from an existing one by an "
                + "invisible character (task c28e6177).");
            Assert.True(guard >= 0, $"The Oracle guard is gone from {relativePath}.");
            Assert.True(
                trim < guard,
                $"{relativePath} compares against Oracle BEFORE trimming. \"Oracle \" would pass the "
                + "always-on guard and spawn a second Oracle — the guard defeated by the whitespace "
                + "defect it is standing next to.");
        }

        /// <summary>
        /// The MCP tool trims too, and — the part that is easy to get wrong — guards first. Forwarding
        /// <c>String(args.agentName).trim()</c> without a presence check turns a missing name into the
        /// literal string "undefined", which the API would accept and spawn.
        /// </summary>
        [Fact]
        public void The_mcp_tool_guards_the_name_before_trimming_it()
        {
            string src = ReadRepoFile("mcp/index.js");

            int handler = src.IndexOf("case \"spawn_helper\":", StringComparison.Ordinal);
            Assert.True(handler >= 0, "The spawn_helper handler is gone; this census is vacuous.");

            int nextCase = src.IndexOf("case \"", handler + 20, StringComparison.Ordinal);
            string body = nextCase > handler ? src[handler..nextCase] : src[handler..];

            int guard = body.IndexOf("!args.agentName", StringComparison.Ordinal);
            int trim = body.IndexOf("String(args.agentName).trim()", StringComparison.Ordinal);

            Assert.True(
                trim >= 0,
                "spawn_helper forwards agentName without trimming it, so an agent's trailing space "
                + "still mints a whitespace-variant terminal (task c28e6177).");
            Assert.True(
                guard >= 0,
                "spawn_helper trims agentName with no presence check. String(undefined) is the literal "
                + "\"undefined\", so a missing name would spawn a terminal called undefined instead of "
                + "returning the API's 400.");
            Assert.True(guard < trim, "The presence check must run before the value is stringified.");
        }

        /// <summary>Reads a file from the repo root, which is two directories above the test binary's project.</summary>
        private static string ReadRepoFile(string relativePath)
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = Path.GetFullPath(Path.Combine(here, "..", relativePath));
            Assert.True(File.Exists(path), $"Could not locate '{relativePath}' at '{path}'.");
            return File.ReadAllText(path);
        }

        private static string ThisFile([CallerFilePath] string path = null) => path;
    }
}
