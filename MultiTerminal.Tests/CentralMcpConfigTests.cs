#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="CentralMcpConfig"/> (task cb4883b6, GitHub #8/#25): the <c>--mcp-config</c> file is
    /// healed when missing or broken and NEVER rewritten when healthy.
    ///
    /// <para>The fact that separates this from the first version of the fix (msarson's 31fcc69, which
    /// rewrote the file on every startup) is <see cref="A_working_file_naming_a_build_the_resolver_would_not_pick_is_left_alone"/>.
    /// Under the rewrite-always version that file is replaced; here it must not be.</para>
    ///
    /// <para>Paths are fictional and existence is injected, except in the <see cref="CentralMcpConfig.Ensure"/>
    /// facts, which write to a temp directory. None of these touch the real <c>%APPDATA%</c>.</para>
    /// </summary>
    public sealed class CentralMcpConfigTests : IDisposable
    {
        private const string InstalledExe = @"C:\Program Files\MultiTerminal\mcp-gateway\McpGateway.exe";
        private const string PublishExe = @"D:\dev\McpGateway\bin\publish\win-x64\McpGateway.exe";
        private const string IndexJs = @"C:\Users\Someone\AppData\Roaming\multiterminal\mcp\index.js";
        private const string GatewayProject = @"D:\dev\McpGateway";

        private static readonly CentralMcpConfig.Sources Both =
            new(GatewayExe: InstalledExe, GatewayProjectDir: null, McpIndexJs: IndexJs);

        private static readonly CentralMcpConfig.Sources NoSources = new(null, null, null);

        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mt-centralmcp-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            GC.SuppressFinalize(this);
        }

        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        private static string Config(string gatewayCommand, string indexJs) => $$"""
            {
              "mcpServers": {
                "mcp-gateway": { "type": "stdio", "command": {{JsonValue.Create(gatewayCommand)!.ToJsonString()}}, "args": [] },
                "multiterminal": { "type": "stdio", "command": "node", "args": [ {{JsonValue.Create(indexJs)!.ToJsonString()}} ] }
              }
            }
            """;

        [Fact]
        public void A_missing_file_needs_healing()
        {
            Assert.Equal("missing", CentralMcpConfig.WhyUnhealthy(null, Both, Existing()));
        }

        /// <summary>
        /// The dev box's real shape: the gateway is a hand-set publish build, while the resolver would
        /// have chosen a different exe. Both exist. This file works and must be reported healthy.
        /// </summary>
        [Fact]
        public void A_working_file_naming_a_build_the_resolver_would_not_pick_is_left_alone()
        {
            string json = Config(PublishExe, IndexJs);

            Assert.Null(CentralMcpConfig.WhyUnhealthy(json, Both, Existing(PublishExe, InstalledExe, IndexJs)));
        }

        [Fact]
        public void A_path_that_no_longer_exists_needs_healing()
        {
            string json = Config(PublishExe, IndexJs);

            string? reason = CentralMcpConfig.WhyUnhealthy(json, Both, Existing(IndexJs));

            Assert.NotNull(reason);
            Assert.Contains(PublishExe, reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_missing_core_entry_needs_healing_only_when_it_could_be_generated()
        {
            const string OnlyGateway = """{ "mcpServers": { "mcp-gateway": { "command": "dotnet", "args": ["run"] } } }""";

            Assert.Equal("no 'multiterminal' entry", CentralMcpConfig.WhyUnhealthy(OnlyGateway, Both, Existing()));
            Assert.Null(CentralMcpConfig.WhyUnhealthy(
                OnlyGateway, new CentralMcpConfig.Sources(InstalledExe, null, McpIndexJs: null), Existing()));
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("""{ "servers": {} }""")]
        [InlineData("[]")]
        public void An_unparseable_or_wrongly_shaped_file_needs_healing(string json)
        {
            Assert.NotNull(CentralMcpConfig.WhyUnhealthy(json, Both, Existing()));
        }

        /// <summary><c>node</c> and <c>dotnet</c> resolve through PATH, so only absolute paths are checked.</summary>
        [Fact]
        public void Bare_commands_are_not_checked_for_existence()
        {
            string json = Config("dotnet", IndexJs);

            Assert.Null(CentralMcpConfig.WhyUnhealthy(json, Both, Existing(IndexJs)));
        }

        [Fact]
        public void Generate_prefers_a_built_exe_and_falls_back_to_dotnet_run()
        {
            JsonNode built = JsonNode.Parse(CentralMcpConfig.Generate(Both)!)!;
            Assert.Equal(InstalledExe, (string?)built["mcpServers"]!["mcp-gateway"]!["command"]);
            Assert.Equal(IndexJs, (string?)built["mcpServers"]!["multiterminal"]!["args"]![0]);

            JsonNode fallback = JsonNode.Parse(CentralMcpConfig.Generate(new CentralMcpConfig.Sources(null, GatewayProject, null))!)!;
            Assert.Equal("dotnet", (string?)fallback["mcpServers"]!["mcp-gateway"]!["command"]);
            Assert.Equal(GatewayProject, (string?)fallback["mcpServers"]!["mcp-gateway"]!["args"]![2]);
            Assert.Null(fallback["mcpServers"]!["multiterminal"]);
        }

        /// <summary>
        /// The old generator escaped backslashes by hand and nothing else, so a quote in a path produced
        /// invalid JSON. A user name can contain one.
        /// </summary>
        [Fact]
        public void Generated_json_round_trips_a_path_containing_a_quote()
        {
            const string OddIndexJs = @"C:\Users\O""Brien\AppData\Roaming\multiterminal\mcp\index.js";

            string json = CentralMcpConfig.Generate(new CentralMcpConfig.Sources(null, null, OddIndexJs))!;

            Assert.Equal(OddIndexJs, (string?)JsonNode.Parse(json)!["mcpServers"]!["multiterminal"]!["args"]![0]);
        }

        [Fact]
        public void Generate_returns_null_when_nothing_can_be_generated()
        {
            Assert.Null(CentralMcpConfig.Generate(NoSources));
        }

        [Fact]
        public void Ensure_writes_a_missing_file()
        {
            string path = Path.Combine(_tempDir, ".mcp.json");

            var outcome = CentralMcpConfig.Ensure(path, Both, Existing(InstalledExe, IndexJs), log: null);

            Assert.Equal(CentralMcpConfig.Outcome.Written, outcome);
            Assert.Null(CentralMcpConfig.WhyUnhealthy(File.ReadAllText(path), Both, Existing(InstalledExe, IndexJs)));
        }

        /// <summary>
        /// Byte-identical, including a server a person added by hand. Content comparison, not a timestamp:
        /// a rewrite with identical bytes would be invisible to this fact, but also harmless.
        /// </summary>
        [Fact]
        public void Ensure_leaves_a_healthy_file_byte_identical()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            string original = Config(PublishExe, IndexJs).Replace(
                "\"mcpServers\": {", "\"mcpServers\": {\n    \"hand-added\": { \"command\": \"python\" },", StringComparison.Ordinal);
            File.WriteAllText(path, original);

            var outcome = CentralMcpConfig.Ensure(path, Both, Existing(PublishExe, InstalledExe, IndexJs), log: null);

            Assert.Equal(CentralMcpConfig.Outcome.Healthy, outcome);
            Assert.Equal(original, File.ReadAllText(path));
        }

        [Fact]
        public void Ensure_does_not_replace_a_broken_file_with_nothing()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            File.WriteAllText(path, "{ broken");

            var outcome = CentralMcpConfig.Ensure(path, NoSources, Existing(), log: null);

            Assert.Equal(CentralMcpConfig.Outcome.NothingToWrite, outcome);
            Assert.Equal("{ broken", File.ReadAllText(path));
        }

        /// <summary>
        /// The launch builder heals through <see cref="CentralMcpConfig.EnsureDefault"/>, and tests call the
        /// launch builder. Under a test host it must stand down rather than write this machine's real file.
        /// </summary>
        [Fact]
        public void EnsureDefault_never_touches_the_real_file_under_a_test_host()
        {
            Assert.Equal(CentralMcpConfig.Outcome.SkippedUnderTestHost, CentralMcpConfig.EnsureDefault(log: null));
        }
    }
}
