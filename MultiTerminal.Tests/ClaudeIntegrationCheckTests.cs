#nullable enable
using System;
using System.Collections.Generic;
using MultiTerminal.Services;
using Xunit;
using Problem = MultiTerminal.Services.ClaudeIntegrationCheck.Problem;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="ClaudeIntegrationCheck"/> (task cb4883b6): the startup warning for terminals that would
    /// start without MultiTerminal's agent tools. Everything here is injected; no real profile or PATH
    /// is read.
    /// </summary>
    public class ClaudeIntegrationCheckTests
    {
        private const string IndexJs = @"C:\Users\Dev\AppData\Roaming\multiterminal\mcp\index.js";
        private const string PluginDir = @"C:\Users\Dev\.claude\plugins\marketplaces\multiterminal-marketplace\plugins\multiterminal";
        private const string NodeDir = @"C:\Program Files\nodejs";
        private const string NodePath = NodeDir + @"\node.exe";

        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        /// <summary>
        /// The pipeline adversary's case: an elevated install put the MCP server and plugin into ANOTHER
        /// account's profile, so this account has neither. Before this check, MT wrote a valid gateway-only
        /// config and said nothing.
        /// </summary>
        [Fact]
        public void The_elevated_install_shape_is_reported_with_its_cause()
        {
            var problems = ClaudeIntegrationCheck.Find(IndexJs, PluginDir, NodeDir, null, Existing(NodePath), Existing());

            Assert.Equal(new[] { Problem.McpServerMissing, Problem.PluginMissing }, problems);

            string message = ClaudeIntegrationCheck.BuildMessage(problems, IndexJs, PluginDir);
            Assert.Contains(IndexJs, message, StringComparison.Ordinal);
            Assert.Contains(PluginDir, message, StringComparison.Ordinal);
            Assert.Contains("DIFFERENT Windows account", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_complete_install_raises_nothing()
        {
            var problems = ClaudeIntegrationCheck.Find(IndexJs, PluginDir, NodeDir, null, Existing(IndexJs, NodePath), Existing(PluginDir));

            Assert.Empty(problems);
            Assert.False(ClaudeIntegrationCheck.ShouldWarn(problems, suppressedFingerprint: null));
        }

        /// <summary>A missing Node is not an elevation problem, so the message must not blame one.</summary>
        [Fact]
        public void Missing_node_alone_does_not_blame_the_installing_account()
        {
            var problems = ClaudeIntegrationCheck.Find(IndexJs, PluginDir, @"C:\Windows", null, Existing(IndexJs), Existing(PluginDir));

            Assert.Equal(new[] { Problem.NodeMissing }, problems);
            Assert.DoesNotContain("DIFFERENT Windows account", ClaudeIntegrationCheck.BuildMessage(problems, IndexJs, PluginDir), StringComparison.Ordinal);
        }

        /// <summary>
        /// "Don't show again" silences THAT problem set only. A deliberate custom install without the plugin
        /// stays quiet; a later, different failure (Node uninstalled) is still shown.
        /// </summary>
        [Fact]
        public void Suppression_is_keyed_to_the_exact_problem_set()
        {
            string suppressed = ClaudeIntegrationCheck.Fingerprint(new[] { Problem.PluginMissing });

            Assert.False(ClaudeIntegrationCheck.ShouldWarn(new[] { Problem.PluginMissing }, suppressed));
            Assert.True(ClaudeIntegrationCheck.ShouldWarn(new[] { Problem.PluginMissing, Problem.NodeMissing }, suppressed));
            Assert.True(ClaudeIntegrationCheck.ShouldWarn(new[] { Problem.NodeMissing }, suppressed));
        }

        [Fact]
        public void The_fingerprint_does_not_depend_on_order()
        {
            Assert.Equal(
                ClaudeIntegrationCheck.Fingerprint(new[] { Problem.NodeMissing, Problem.McpServerMissing }),
                ClaudeIntegrationCheck.Fingerprint(new[] { Problem.McpServerMissing, Problem.NodeMissing }));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"C:\Windows;;C:\Tools")]
        public void Node_is_missing_when_no_PATH_entry_holds_it(string? path)
        {
            Assert.Null(ClaudeIntegrationCheck.FindCommandOnPath("node", path, null, Existing(NodePath)));
        }

        /// <summary>
        /// Pipeline Run 2 (adversary): a version manager can put a <c>node.cmd</c> shim on PATH with no
        /// <c>node.exe</c> anywhere. Checking node.exe alone warned falsely on such a box — and a warning
        /// that is wrong on dev machines gets dismissed forever. Discriminates: under the node.exe-only
        /// check this set reports NodeMissing.
        /// </summary>
        [Fact]
        public void A_node_cmd_shim_on_PATH_counts_as_node()
        {
            const string ShimDir = @"C:\Users\Dev\AppData\Roaming\nvm-shims";
            string shim = ShimDir + @"\node.cmd";

            var problems = ClaudeIntegrationCheck.Find(
                IndexJs, PluginDir, ShimDir, ".COM;.EXE;.BAT;.CMD", Existing(IndexJs, shim), Existing(PluginDir));

            Assert.Empty(problems);
        }

        /// <summary>
        /// PATHEXT order decides which match wins within one directory, as it does for the shell. Compared
        /// ignoring case: the extension comes from PATHEXT (conventionally upper case) and Windows paths are
        /// case-insensitive.
        /// </summary>
        [Fact]
        public void PATHEXT_order_is_honoured_within_a_directory()
        {
            string cmd = NodeDir + @"\node.cmd";

            Assert.Equal(cmd, ClaudeIntegrationCheck.FindCommandOnPath("node", NodeDir, ".CMD;.EXE", Existing(NodePath, cmd)), ignoreCase: true);
            Assert.Equal(NodePath, ClaudeIntegrationCheck.FindCommandOnPath("node", NodeDir, ".EXE;.CMD", Existing(NodePath, cmd)), ignoreCase: true);
        }

        /// <summary>Quoted and padded PATH entries are real on Windows; the shell accepts them, so must we.</summary>
        [Fact]
        public void A_quoted_padded_PATH_entry_is_found()
        {
            string path = @"C:\Windows; ""C:\Program Files\nodejs"" ;C:\Tools";

            Assert.Equal(NodePath, ClaudeIntegrationCheck.FindCommandOnPath("node", path, null, Existing(NodePath)), ignoreCase: true);
        }
    }
}
