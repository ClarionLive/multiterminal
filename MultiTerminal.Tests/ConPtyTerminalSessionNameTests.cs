using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="ConPtyTerminal.ApplySessionName"/> (task 0ff1b520): an MT terminal launches with
    /// <c>-n '&lt;AgentName&gt;'</c> so the name on the board IS the address other Claude Code sessions
    /// use with <c>SendMessage</c>/<c>ListAgents</c>.
    ///
    /// <para>THE PROPERTY WORTH PROTECTING is not "a flag is present" — it is that the emitted name and
    /// <c>MULTITERMINAL_NAME</c> are the SAME STRING. A terminal answering to an address its board identity
    /// does not use routes real messages to the wrong agent, and does it silently. That is why the
    /// unusable-name cases below assert the flag is ABSENT rather than asserting some repaired form: the
    /// method must refuse, never truncate or rewrite.</para>
    /// </summary>
    public class ConPtyTerminalSessionNameTests
    {
        private const string ClaudeLaunch = "claude --mcp-config 'x.json' --dangerously-skip-permissions; exit";

        [Fact]
        public void Names_the_session_after_the_agent()
        {
            string result = ConPtyTerminal.ApplySessionName(ClaudeLaunch, "Alice");

            Assert.Contains("-n 'Alice'", result);
            Assert.StartsWith("claude -n 'Alice' --mcp-config", result);
        }

        /// <summary>
        /// The flag must land on the CLAUDE token, not merely somewhere in the string — an argument
        /// appended after <c>; exit</c>, or inside a quoted path, would never reach the CLI.
        /// </summary>
        [Fact]
        public void Flag_lands_immediately_after_the_claude_token_and_before_its_other_flags()
        {
            string result = ConPtyTerminal.ApplySessionName(ClaudeLaunch, "Alice");

            Assert.True(
                result.IndexOf("-n 'Alice'", System.StringComparison.Ordinal)
                    < result.IndexOf("--dangerously-skip-permissions", System.StringComparison.Ordinal),
                "the name flag must precede the other flags, i.e. sit on the claude token");
            Assert.True(
                result.IndexOf("-n 'Alice'", System.StringComparison.Ordinal)
                    < result.IndexOf("; exit", System.StringComparison.Ordinal),
                "the name flag must be part of the claude invocation, not appended after it");
        }

        /// <summary>The exact escape <c>MULTITERMINAL_NAME</c> uses, so both survive PowerShell identically.</summary>
        [Fact]
        public void Escapes_a_single_quote_the_same_way_the_env_var_does()
        {
            string result = ConPtyTerminal.ApplySessionName(ClaudeLaunch, "O'Brien");

            Assert.Contains("-n 'O''Brien'", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_name_means_no_flag(string terminalName)
        {
            Assert.Equal(ClaudeLaunch, ConPtyTerminal.ApplySessionName(ClaudeLaunch, terminalName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_command_is_returned_unchanged(string autoRunCommand)
        {
            Assert.Equal(autoRunCommand, ConPtyTerminal.ApplySessionName(autoRunCommand, "Alice"));
        }

        /// <summary>
        /// A Codex launch must not receive a Claude-only flag. This is the case a
        /// <c>Contains("claude")</c> implementation gets wrong, which is why the path below carries the
        /// word: the guard tests the FIRST TOKEN, so a Claude-shaped substring anywhere else is ignored.
        /// </summary>
        [Fact]
        public void Codex_launch_is_left_alone_even_when_its_path_contains_the_word_claude()
        {
            const string codex = "codex --config 'C:\\Users\\dev\\.claude\\codex.toml'; exit";

            Assert.Equal(codex, ConPtyTerminal.ApplySessionName(codex, "Alice"));
        }

        [Fact]
        public void A_command_that_merely_starts_with_claude_as_a_prefix_is_not_a_claude_launch()
        {
            const string notClaude = "claudette --do-something; exit";

            Assert.Equal(notClaude, ConPtyTerminal.ApplySessionName(notClaude, "Alice"));
        }

        [Fact]
        public void A_resume_launch_is_still_named()
        {
            string result = ConPtyTerminal.ApplySessionName("claude -r abc123; exit", "Diana");

            Assert.Equal("claude -n 'Diana' -r abc123; exit", result);
        }

        /// <summary>
        /// REFUSE, DO NOT TRUNCATE. A 201-character name is over the CLI's cap, so passing it would either
        /// fail or be silently shortened by the CLI — either way the address stops matching
        /// <c>MULTITERMINAL_NAME</c>. No flag is the honest outcome.
        /// </summary>
        [Fact]
        public void An_over_long_name_is_refused_rather_than_truncated()
        {
            string tooLong = new string('a', ConPtyTerminal.MaxSessionNameLength + 1);

            string result = ConPtyTerminal.ApplySessionName(ClaudeLaunch, tooLong);

            Assert.Equal(ClaudeLaunch, result);
            Assert.DoesNotContain("-n ", result);
        }

        [Fact]
        public void A_name_exactly_at_the_cap_is_still_used()
        {
            string atCap = new string('a', ConPtyTerminal.MaxSessionNameLength);

            Assert.Contains($"-n '{atCap}'", ConPtyTerminal.ApplySessionName(ClaudeLaunch, atCap));
        }

        /// <summary>
        /// A leading '/' made a session unaddressable and shown as "(untitled)" before CLI 2.1.239. Asking
        /// for such a name would not make it the address, so it is refused rather than requested-and-hoped.
        /// </summary>
        [Fact]
        public void A_name_starting_with_a_slash_is_refused()
        {
            Assert.Equal(ClaudeLaunch, ConPtyTerminal.ApplySessionName(ClaudeLaunch, "/Alice"));
        }

        /// <summary>
        /// THE ANTI-DRIFT FACT. Whatever name reaches the CLI must be the name
        /// <see cref="ConPtyTerminal.StartProcess"/> puts in <c>MULTITERMINAL_NAME</c>, escaped identically.
        /// This is the agreement that makes the board identity and the peer address one value rather than
        /// two that merely look alike today — the same discipline as
        /// <c>TerminalRoleTests.The_tab_label_and_the_launch_variable_never_disagree</c>.
        /// </summary>
        [Theory]
        [InlineData("Alice")]
        [InlineData("Diana")]
        [InlineData("O'Brien")]
        [InlineData("Agent 7")]
        [InlineData("naïve-agent")]
        public void The_peer_address_and_MULTITERMINAL_NAME_are_the_same_string(string terminalName)
        {
            // Exactly the escape StartProcess applies when it writes $env:MULTITERMINAL_NAME = '<name>'.
            string envVarForm = terminalName.Replace("'", "''");

            string result = ConPtyTerminal.ApplySessionName(ClaudeLaunch, terminalName);

            Assert.Contains($"-n '{envVarForm}'", result);
        }
    }
}
