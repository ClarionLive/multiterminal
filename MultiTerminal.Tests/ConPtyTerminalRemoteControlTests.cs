using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="ConPtyTerminal.ApplyRemoteControl"/> and its setting (ticket d096464f): with "Remote
    /// Control for every Claude terminal" on, a Claude launch gets a bare <c>--remote-control</c>, so the
    /// session is reachable from claude.ai/code and the Claude app under the name <c>-n</c> gives it.
    ///
    /// <para>The property worth protecting is that the flag never takes a word that is not a name as the
    /// session's name. <c>--remote-control [name]</c> has an OPTIONAL argument, so a bare word straight after
    /// it would become the remote title and vanish from the command. Hence the checks on what follows it.</para>
    ///
    /// <para>Not covered: that <c>StartProcess</c> reads the setting and calls this (wiring no test runs), and
    /// that the CLI accepts the result. Both are the live test.</para>
    /// </summary>
    public class ConPtyTerminalRemoteControlTests
    {
        private const string ClaudeLaunch = "claude --mcp-config 'x.json' --dangerously-skip-permissions; exit";

        [Fact]
        public void Off_leaves_the_command_unchanged()
        {
            Assert.Equal(ClaudeLaunch, ConPtyTerminal.ApplyRemoteControl(ClaudeLaunch, enabled: false));
        }

        [Fact]
        public void On_adds_a_bare_flag_straight_after_the_claude_token()
        {
            string result = ConPtyTerminal.ApplyRemoteControl(ClaudeLaunch, enabled: true);

            Assert.Equal("claude --remote-control --mcp-config 'x.json' --dangerously-skip-permissions; exit", result);
        }

        /// <summary>The order StartProcess applies them in: the remote title comes from -n, the terminal's name.</summary>
        [Fact]
        public void With_the_session_name_the_name_comes_first_and_the_flag_is_followed_by_a_flag()
        {
            string result = ConPtyTerminal.ApplySessionName(ConPtyTerminal.ApplyRemoteControl(ClaudeLaunch, true), "Alice");

            Assert.StartsWith("claude -n 'Alice' --remote-control --mcp-config", result);
        }

        [Fact]
        public void A_resume_with_a_model_keeps_every_argument()
        {
            const string resume = "claude --mcp-config 'x.json' --model opus --resume 3f2b8c1e-0000-4000-8000-000000000001 --dangerously-skip-permissions; exit";

            string result = ConPtyTerminal.ApplyRemoteControl(resume, true);

            Assert.Equal(resume.Replace("claude --mcp-config", "claude --remote-control --mcp-config"), result);
        }

        [Theory]
        [InlineData("claude")]
        [InlineData("claude; exit")]
        [InlineData("claude -c")]
        public void Short_commands_get_the_flag_without_a_word_after_it(string command)
        {
            string result = ConPtyTerminal.ApplyRemoteControl(command, true);

            Assert.StartsWith("claude --remote-control", result);
            string after = result.Substring("claude --remote-control".Length).TrimStart();
            Assert.True(after.Length == 0 || after[0] == '-' || after[0] == ';', $"'{after}' would be taken as the session name");
        }

        /// <summary>A hand-written command whose first argument is a word: the flag goes last instead, so the
        /// word stays the prompt rather than becoming the remote session's name.</summary>
        [Fact]
        public void A_leading_bare_word_is_never_swallowed_as_the_name()
        {
            Assert.Equal("claude hello --remote-control; exit", ConPtyTerminal.ApplyRemoteControl("claude hello; exit", true));
            Assert.Equal("claude hello --remote-control", ConPtyTerminal.ApplyRemoteControl("claude hello", true));
        }

        [Theory]
        [InlineData("claude --remote-control; exit")]
        [InlineData("claude --rc 'Mine'; exit")]
        [InlineData("claude -n 'A' --remote-control --dangerously-skip-permissions; exit")]
        public void A_command_that_already_asks_for_it_is_left_alone(string command)
        {
            Assert.Equal(command, ConPtyTerminal.ApplyRemoteControl(command, true));
        }

        [Theory]
        [InlineData("& 'C:\\tools\\codex.cmd' --config x; exit")]
        [InlineData("claudette --x")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_but_a_claude_launch_is_untouched(string command)
        {
            Assert.Equal(command, ConPtyTerminal.ApplyRemoteControl(command, true));
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData(" TRUE ", true)]
        [InlineData("false", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("yes", false)]
        public void The_setting_is_on_only_for_a_stored_true(string raw, bool expected)
        {
            Assert.Equal(expected, SettingsService.ParseRemoteControlAllTerminals(raw));
        }
    }
}
