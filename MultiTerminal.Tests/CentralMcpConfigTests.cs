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
    /// rewrote the file on every startup) is <see cref="Ensure_leaves_a_healthy_file_byte_identical"/>:
    /// making <c>Ensure</c> rewrite unconditionally turns exactly that fact red (run, not assumed).
    /// <see cref="A_working_file_naming_a_build_the_resolver_would_not_pick_is_left_alone"/> does NOT
    /// discriminate — it asks <c>WhyUnhealthy</c> only, which a rewrite-always <c>Ensure</c> never
    /// consults. It pins the health RULE for the dev-box shape, not the write behaviour.</para>
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

        /// <summary>
        /// Pipeline Run 1 (debugger, reproduced on .NET 8): <c>JsonNode.Parse</c> accepts a repeated key,
        /// and the resulting <c>JsonObject</c> throws <see cref="ArgumentException"/> — not a
        /// <c>JsonException</c> — on first access. Claude Code's <c>JSON.parse</c> accepts the same file
        /// (last key wins), so a hand edit that works there reached MultiTerminal's constructor as an
        /// unhandled exception and stopped the app opening. Both depths are covered because they throw
        /// from different accesses: the root lookup and the per-server lookup.
        /// </summary>
        [Theory]
        [InlineData("""{ "mcpServers": { "multiterminal": {}, "multiterminal": {} } }""")]
        [InlineData("""{ "mcpServers": {}, "mcpServers": {} }""")]
        public void A_file_with_a_repeated_key_needs_healing_rather_than_throwing(string json)
        {
            string? reason = CentralMcpConfig.WhyUnhealthy(json, Both, Existing());

            Assert.NotNull(reason);
            Assert.StartsWith("unparseable", reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// The end-to-end form of the fact above: the file is replaced by a healthy one, not left for
        /// the next startup to trip over again.
        /// </summary>
        [Fact]
        public void Ensure_heals_a_file_with_a_repeated_key()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            File.WriteAllText(path, """{ "mcpServers": { "multiterminal": {}, "multiterminal": {} } }""");

            var outcome = CentralMcpConfig.Ensure(path, Both, Existing(InstalledExe, IndexJs), log: null);

            Assert.Equal(CentralMcpConfig.Outcome.Written, outcome);
            Assert.Null(CentralMcpConfig.WhyUnhealthy(File.ReadAllText(path), Both, Existing(InstalledExe, IndexJs)));
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

        /// <summary>
        /// Task a796e5f9: the launch path used to heal only when the file was ABSENT, so a file that broke
        /// after startup was handed to every terminal until MultiTerminal restarted. The heal now runs
        /// before every launch, including when a file is there.
        /// </summary>
        [Fact]
        public void PathForLaunch_heals_even_when_a_file_exists()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            File.WriteAllText(path, Config(PublishExe, IndexJs));
            int heals = 0;

            string? result = CentralMcpConfig.PathForLaunch(path, () => { heals++; return CentralMcpConfig.Outcome.Healthy; }, log: null);

            Assert.Equal(1, heals);
            Assert.Equal(path, result);
        }

        /// <summary>The end-to-end shape: the gateway build the file named is gone, and the launch gets a repaired file.</summary>
        [Fact]
        public void PathForLaunch_hands_out_a_repaired_file_when_a_named_path_has_gone()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            File.WriteAllText(path, Config(PublishExe, IndexJs));
            var exists = Existing(InstalledExe, IndexJs);

            string? result = CentralMcpConfig.PathForLaunch(path, () => CentralMcpConfig.Ensure(path, Both, exists, log: null), log: null);

            Assert.Equal(path, result);
            Assert.Null(CentralMcpConfig.WhyUnhealthy(File.ReadAllText(path), Both, exists));
        }

        [Fact]
        public void PathForLaunch_does_not_fail_a_launch_when_the_heal_throws()
        {
            Directory.CreateDirectory(_tempDir);
            string path = Path.Combine(_tempDir, ".mcp.json");
            File.WriteAllText(path, Config(PublishExe, IndexJs));
            var logged = new List<string>();

            string? result = CentralMcpConfig.PathForLaunch(path, () => throw new InvalidOperationException("boom"), logged.Add);

            Assert.Equal(path, result);
            Assert.Single(logged);
        }

        [Fact]
        public void PathForLaunch_returns_null_when_there_is_still_no_file()
        {
            string path = Path.Combine(_tempDir, ".mcp.json");

            Assert.Null(CentralMcpConfig.PathForLaunch(path, () => CentralMcpConfig.Outcome.NothingToWrite, log: null));
        }
    }
}
