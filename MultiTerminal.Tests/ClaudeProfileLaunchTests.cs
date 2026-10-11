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
    /// <para>Every pane gets the prologue whatever it runs. That is pinned by the SHAPE of
    /// <c>BuildClaudeEnvSetup</c>, not by a list of commands: it is not given the auto-run command, so
    /// it cannot skip the prologue for one. Giving it the command back stops this file compiling, at
    /// every call below.</para>
    /// <para>NOT covered: that <c>ConPtyTerminal.StartProcess</c> and <c>AgentProcess.SpawnAsync</c> call
    /// these with the launch's profile, or that any caller in MainForm passes the right profile (which
    /// profile a launch gets is <see cref="ClaudeProfileLaunchPolicyTests"/>). No process is started
    /// here.</para>
    /// </summary>
    public sealed class ClaudeProfileLaunchTests
    {
        private const string Home = @"C:\Users\Dev";
        private const string WorkDir = Home + @"\.claude-work";
        private const string NoFlicker = "$env:CLAUDE_CODE_NO_FLICKER = '1'; ";
        private const string SetWorkLine = "$env:CLAUDE_CONFIG_DIR = '" + WorkDir + "'; ";
        private const string RemoveLine = "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; ";
        private const string TeamsLine = "$env:CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = '1'; ";

        private static readonly ClaudeProfile Work = new ClaudeProfile("Work", WorkDir);

        private static ClaudeProfileContext Ctx() =>
            new ClaudeProfileContext(new[] { Work }, null, Home, _ => true);

        // ── Pane launch: the tail of envSetup ────────────────────────────────

        [Fact]
        public void A_pane_under_a_profile_sets_its_folder_right_after_the_flicker_flag()
        {
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(() => ClaudeProfileResolver.GetPrologue(Work, Ctx()));

            Assert.Equal(NoFlicker + SetWorkLine + TeamsLine, envSetup);
        }

        [Fact]
        public void A_pane_under_Default_removes_the_variable()
        {
            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(() => ClaudeProfileResolver.GetPrologue(null, Ctx()));

            Assert.Equal(NoFlicker + RemoveLine + TeamsLine, envSetup);
        }

        [Fact]
        public void Every_pane_asks_for_its_prologue_exactly_once()
        {
            // The environment tail has no way to leave the prologue out: it is asked for on every call.
            // Before, a pane whose command did not start with the token `claude` (a full path to
            // claude.exe, a wrapper script, the Codex launcher) was never asked, and ran under whatever
            // CLAUDE_CONFIG_DIR the shell already had. Proves the tail always carries what it is handed.
            // Does NOT prove StartProcess puts the tail on the command line, nor which profile it hands in.
            int asked = 0;

            string envSetup = ConPtyTerminal.BuildClaudeEnvSetup(() => { asked++; return "PROLOGUE; "; });

            Assert.Equal(1, asked);
            Assert.Equal(NoFlicker + "PROLOGUE; ", envSetup);
        }

        [Fact]
        public void The_environment_tail_cannot_see_the_command_so_it_cannot_skip_a_pane()
        {
            // The structural pin. One parameter, the prologue source: no auto-run command to branch on.
            var method = typeof(ConPtyTerminal).GetMethod(
                nameof(ConPtyTerminal.BuildClaudeEnvSetup),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            Assert.NotNull(method);
            var parameter = Assert.Single(method.GetParameters());
            Assert.Equal(typeof(Func<string>), parameter.ParameterType);
        }

        [Fact]
        public void A_prologue_that_cannot_be_built_stops_the_launch()
        {
            // Owner decision 2026-10-10. This used to be swallowed, and the pane launched on the shell's
            // own environment. Now the failure reaches StartProcess, which refuses to start. Proves the
            // exception is not swallowed HERE; that StartProcess lets it stop the launch is not exercised.
            // Since the prologue reaches every pane, this also holds for a pane that runs Codex.
            var noFolder = new ClaudeProfile("Broken", @"relative\folder");

            Assert.Throws<ArgumentException>(() =>
                ConPtyTerminal.BuildClaudeEnvSetup(() => ClaudeProfileResolver.GetPrologue(noFolder, Ctx())));
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
