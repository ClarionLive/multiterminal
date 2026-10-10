using System;
using System.Collections.Generic;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec item 7 (GitHub #21): where a launch picks up its Claude profile. Two pure pieces
    /// are covered: <see cref="ConPtyTerminal.BuildClaudeEnvSetup"/> (the tail of a pane's launch
    /// environment) and <see cref="AgentProcess.ApplyEnvironment"/> (a headless agent's environment).
    /// Prologues and environments come from the resolver's context overloads, so nothing reads the real
    /// settings.txt or process environment.
    /// <para>NOT covered: that <c>ConPtyTerminal.StartProcess</c> and <c>AgentProcess.SpawnAsync</c> call
    /// these with the launch's profile, or that any caller in MainForm passes the right profile. No
    /// process is started here.</para>
    /// </summary>
    public sealed class ClaudeProfileLaunchTests
    {
        private const string Home = @"C:\Users\Dev";
        private const string WorkDir = Home + @"\.claude-work";
        private const string NoFlicker = "$env:CLAUDE_CODE_NO_FLICKER = '1'; ";
        private const string SetWorkLine = "$env:CLAUDE_CONFIG_DIR = '" + WorkDir + "'; ";
        private const string RemoveLine = "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; ";
        private const string TeamsLine = "$env:CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = '1'; ";
        private const string ClaudeLaunch = "claude --mcp-config 'C:\\x\\.mcp.json'; exit";

        private static readonly ClaudeProfile Work = new ClaudeProfile("Work", WorkDir);

        private static ClaudeProfileContext Ctx() =>
            new ClaudeProfileContext(new[] { Work }, null, Home, _ => true);

        // ── Pane launch: the tail of envSetup ────────────────────────────────

        [Fact]
        public void A_Claude_launch_under_a_profile_sets_its_folder_right_after_the_flicker_flag()
        {
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(ClaudeLaunch, ClaudeProfileResolver.GetPrologue(Work, Ctx()));

            Assert.Equal(NoFlicker + SetWorkLine + TeamsLine, envSetup);
        }

        [Fact]
        public void A_Claude_launch_under_Default_removes_the_variable()
        {
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(ClaudeLaunch, ClaudeProfileResolver.GetPrologue(null, Ctx()));

            Assert.Equal(NoFlicker + RemoveLine + TeamsLine, envSetup);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_plain_shell_gets_the_profile_too(string autoRunCommand)
        {
            // No auto-run command: Open PowerShell and the pool panes. A `claude` typed there later
            // then starts under the same profile.
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(autoRunCommand, ClaudeProfileResolver.GetPrologue(Work, Ctx()));

            Assert.Equal(NoFlicker + SetWorkLine + TeamsLine, envSetup);
        }

        [Theory]
        [InlineData("& 'C:\\Users\\Dev\\AppData\\Roaming\\multiterminal\\codex\\launch.ps1'; exit")]
        [InlineData("codex")]
        [InlineData("claudette --help")]
        [InlineData("& 'C:\\tools\\claude\\other.exe'")]
        public void Another_auto_run_command_gets_no_prologue(string autoRunCommand)
        {
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(autoRunCommand, ClaudeProfileResolver.GetPrologue(Work, Ctx()));

            Assert.Equal(NoFlicker, envSetup);
        }

        [Fact]
        public void A_missing_prologue_leaves_only_the_flicker_flag()
        {
            // What StartProcess passes when the resolver could not build a prologue.
            Assert.Equal(NoFlicker, ConPtyTerminal.BuildClaudeEnvSetup(ClaudeLaunch, null));
        }

        // ── Headless agent: ProcessStartInfo.Environment ─────────────────────

        [Fact]
        public void A_headless_agent_under_a_profile_gets_its_folder()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AgentProcess.ApplyEnvironment(env, null, ClaudeProfileResolver.GetEnvironment(Work, Ctx()));

            Assert.Equal(WorkDir, env["CLAUDE_CONFIG_DIR"]);
            Assert.Equal("1", env["CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"]);
        }

        [Fact]
        public void A_headless_agent_under_Default_has_the_variable_removed()
        {
            // The child environment starts as a copy of MT's own; model one that carries a value.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CLAUDE_CONFIG_DIR"] = WorkDir,
                ["PATH"] = @"C:\Windows",
            };

            AgentProcess.ApplyEnvironment(env, null, ClaudeProfileResolver.GetEnvironment(null, Ctx()));

            Assert.False(env.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.Equal("1", env["CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"]);
            Assert.Equal(@"C:\Windows", env["PATH"]);
        }

        [Fact]
        public void The_profile_is_applied_after_the_callers_variables()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var callerVars = new Dictionary<string, string>
            {
                ["CLAUDE_CONFIG_DIR"] = @"C:\Users\Dev\.claude-personal",
                ["MULTITERMINAL_TASK_WORKTREE"] = @"C:\repo\.claude\worktrees\abc",
            };

            AgentProcess.ApplyEnvironment(env, callerVars, ClaudeProfileResolver.GetEnvironment(Work, Ctx()));

            Assert.Equal(WorkDir, env["CLAUDE_CONFIG_DIR"]);
            Assert.Equal(@"C:\repo\.claude\worktrees\abc", env["MULTITERMINAL_TASK_WORKTREE"]);
        }
    }
}
