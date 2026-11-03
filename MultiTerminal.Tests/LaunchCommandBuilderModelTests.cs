using System;
using System.Collections.Generic;
using System.IO;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins how a decided model reaches a launch (ticket b0d2f090): the <c>--model</c> flag in
    /// <see cref="LaunchCommandBuilder"/>, the subagent variable for a pane and a headless agent.
    /// The decision itself is <see cref="ClaudeModelLaunchPolicyTests"/>'s. Not covered: that each MainForm
    /// site passes the decision on (wiring no test executes; see the final report's list of sites).
    /// </summary>
    public class LaunchCommandBuilderModelTests
    {
        private const string Id = "0f8fad5b-d9cb-469f-a165-70867728950e";
        private const string Skip = " --dangerously-skip-permissions";

        private static void WithTempDir(Action<string> body)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt_model_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { body(dir); }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [Fact]
        public void No_model_gives_exactly_the_command_built_without_the_parameter()
        {
            WithTempDir(dir =>
            {
                string before = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                Assert.Equal(before, LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, null).AutoRunCommand);
                Assert.Equal(before, LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, "  ").AutoRunCommand);
                Assert.DoesNotContain("--model", before);
            });
        }

        [Fact]
        public void A_model_is_inserted_once_before_skip_permissions_and_the_command_still_starts_with_claude()
        {
            WithTempDir(dir =>
            {
                string fresh = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                string withModel = LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, "opus").AutoRunCommand;

                Assert.Equal(fresh.Replace(Skip, " --model opus" + Skip), withModel);
                Assert.StartsWith("claude", withModel);
                Assert.Equal(1, CountOf(withModel, "--model"));
            });
        }

        [Fact]
        public void With_a_resume_the_model_comes_before_resume()
        {
            WithTempDir(dir =>
            {
                string fresh = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                string resumed = LaunchCommandBuilder.BuildClaudeCommand(null, dir, Id, "sonnet").AutoRunCommand;

                Assert.Equal(fresh.Replace(Skip, $" --model sonnet --resume {Id}{Skip}"), resumed);
            });
        }

        [Fact]
        public void The_session_name_splices_after_the_bare_claude_token_with_a_model_present()
        {
            WithTempDir(dir =>
            {
                string cmd = LaunchCommandBuilder.BuildClaudeCommand(null, dir, Id, "fable").AutoRunCommand;
                string named = ConPtyTerminal.ApplySessionName(cmd, "Bob");

                Assert.NotEqual(cmd, named);
                Assert.StartsWith("claude -n ", named);
                Assert.Contains(" --model fable", named);
                Assert.Contains($" --resume {Id}", named);
            });
        }

        [Fact]
        public void A_bracket_suffix_is_carried_through()
        {
            WithTempDir(dir =>
                Assert.Contains(" --model opus[1m] ", LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, "opus[1m]").AutoRunCommand));
        }

        [Theory]
        [InlineData("opus; calc")]
        [InlineData("opus'")]
        [InlineData("a b")]
        [InlineData("$(calc)")]
        [InlineData("\"x\"")]
        [InlineData("opus&calc")]
        public void A_hostile_name_never_reaches_the_command_line(string model)
        {
            WithTempDir(dir =>
            {
                Assert.Throws<ArgumentException>(() => LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, model));
                Assert.Throws<ArgumentException>(() => LaunchCommandBuilder.BuildCommand(TerminalKind.ClaudeCode, null, dir, null, model));
                Assert.Throws<ArgumentException>(() => LaunchCommandBuilder.WithModel("claude" + Skip + "; exit", model));
            });
        }

        [Fact]
        public void WithModel_gives_the_same_command_as_building_with_the_model()
        {
            WithTempDir(dir =>
            {
                string fresh = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                Assert.Equal(
                    LaunchCommandBuilder.BuildClaudeCommand(null, dir, null, "haiku").AutoRunCommand,
                    LaunchCommandBuilder.WithModel(fresh, "haiku"));
                Assert.Equal(fresh, LaunchCommandBuilder.WithModel(fresh, null));
            });
        }

        [Fact]
        public void The_subagent_assignment_is_quoted_and_only_for_a_valid_name()
        {
            Assert.Equal("$env:CLAUDE_CODE_SUBAGENT_MODEL = 'sonnet'; ", ConPtyTerminal.BuildSubagentModelAssignment("sonnet"));
            Assert.Equal("", ConPtyTerminal.BuildSubagentModelAssignment(null));
            Assert.Equal("", ConPtyTerminal.BuildSubagentModelAssignment("x'; calc; '"));
            Assert.Equal("", ConPtyTerminal.BuildSubagentModelAssignment("$(calc)"));
        }

        [Fact]
        public void A_headless_agent_gets_the_subagent_variable_only_for_a_valid_name()
        {
            var env = new Dictionary<string, string>();
            AgentProcess.ApplyEnvironment(env, null, null, "sonnet");
            Assert.Equal("sonnet", env[AgentProcess.SubagentModelVariable]);

            var none = new Dictionary<string, string>();
            AgentProcess.ApplyEnvironment(none, null, null, null);
            AgentProcess.ApplyEnvironment(none, null, null, "a b; c");
            Assert.DoesNotContain(AgentProcess.SubagentModelVariable, none.Keys);
        }

        [Fact]
        public void A_caller_cannot_move_the_subagent_model_through_its_own_variables()
        {
            var env = new Dictionary<string, string>();
            var callerVars = new Dictionary<string, string> { [AgentProcess.SubagentModelVariable] = "fable" };
            AgentProcess.ApplyEnvironment(env, callerVars, null, "sonnet");
            Assert.Equal("sonnet", env[AgentProcess.SubagentModelVariable]);
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }
    }
}
