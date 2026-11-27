using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Extra Claude Code flags per project and per agent (ticket 5380b6fb): parsing, refusals, the
    /// PowerShell quoting, which agent row a name uses, and where the flags land in a command. Not covered:
    /// that each MainForm launch site applies the decision (wiring no test executes).
    /// </summary>
    public class ClaudeExtraArgsPolicyTests
    {
        private const string Skip = " --dangerously-skip-permissions";

        private static List<string> Parse(string text)
        {
            Assert.True(ClaudeExtraArgsPolicy.TryParse(text, out var tokens, out string error), error);
            return tokens;
        }

        [Fact]
        public void The_owners_example_is_two_tokens_with_the_quotes_removed()
        {
            Assert.Equal(new[] { "--plugin-dir", @"C:\scratch\cc-plugin-1123" }, Parse(@"--plugin-dir ""C:\scratch\cc-plugin-1123"""));
            Assert.Equal(new[] { "--plugin-dir", @"C:\My Plugins\x" }, Parse(@"  --plugin-dir   ""C:\My Plugins\x""  "));
            Assert.Equal(new[] { "--add-dir=C:\\a b" }, Parse(@"--add-dir=""C:\a b"""));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_is_no_flags_and_valid(string text)
        {
            Assert.Empty(Parse(text));
            Assert.Null(ClaudeExtraArgsPolicy.Validate(text));
        }

        [Theory]
        [InlineData("--resume 0f8fad5b-d9cb-469f-a165-70867728950e")]
        [InlineData("-r x")]
        [InlineData("--continue")]
        [InlineData("--MCP-CONFIG x.json")]
        [InlineData("--settings={}")]
        [InlineData("--model fable")]
        [InlineData("--permission-mode plan")]
        [InlineData("--dangerously-skip-permissions")]
        [InlineData("-p hello")]
        [InlineData("--plugin-dir C:\\x --session-id abc")]
        [InlineData("--help")]
        [InlineData("-H")]
        [InlineData("--VERSION")]
        [InlineData("-v")]
        [InlineData("--help=x")]
        public void Flags_MT_sets_itself_are_refused(string text)
        {
            Assert.False(ClaudeExtraArgsPolicy.TryParse(text, out _, out string error));
            Assert.Contains("set by MultiTerminal itself", error);
        }

        [Theory]
        [InlineData("--plugin-dir \"C:\\x", "not closed")]
        [InlineData("--plugin-dir \u2018C:\\x\u2019", "reads as a quote")]
        [InlineData("--plugin-dir \u201CC:\\x\u201D", "reads as a quote")]
        [InlineData("--a \u0007", "control character")]
        [InlineData("--a \"\"", "empty")]
        [InlineData("--plugin-dir \"C:\\my dir\\\"", "cannot end in a backslash")]
        public void Text_that_cannot_go_on_a_command_line_is_refused(string text, string expected)
        {
            Assert.False(ClaudeExtraArgsPolicy.TryParse(text, out _, out string error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void Too_long_text_is_refused()
        {
            Assert.NotNull(ClaudeExtraArgsPolicy.Validate("--x " + new string('a', ClaudeExtraArgsPolicy.MaxLength)));
        }

        [Fact]
        public void Format_writes_single_quoted_literals_and_doubles_a_single_quote()
        {
            Assert.Equal(@" '--plugin-dir' 'C:\it''s here'", ClaudeExtraArgsPolicy.Format(new[] { "--plugin-dir", @"C:\it's here" }));
            Assert.Equal(string.Empty, ClaudeExtraArgsPolicy.Format(Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => ClaudeExtraArgsPolicy.Format(new[] { "a\"b" }));
            Assert.Throws<ArgumentException>(() => ClaudeExtraArgsPolicy.Format(new[] { "a\nb" }));
            Assert.Throws<ArgumentException>(() => ClaudeExtraArgsPolicy.Format(new[] { "" }));
        }

        /// <summary>
        /// The formatted flags go through the same two layers a pane's command does: a double-quoted
        /// <c>-Command "..."</c> argument of CreateProcess, then PowerShell's own parser. Each hostile value
        /// must arrive verbatim, one argument each, and nothing in it may run. Falsified: writing the tokens
        /// unquoted (or double-quoted) runs the subexpression and prints PWNED.
        /// </summary>
        [Fact]
        public void Hostile_values_arrive_verbatim_through_PowerShell_and_nothing_runs()
        {
            string[] values =
            {
                "--plugin-dir", @"C:\My Plugins\x", @"C:\nospace\",
                "$(Write-Output PWNED)", "';Write-Output PWNED;'", "`$env:USERNAME", "a;b|c&d", "%PATH%", "@(1)",
            };
            string text = string.Join(" ", values.Select(v => v.Contains(' ') || v.Contains(';') ? "\"" + v + "\"" : v));
            var tokens = Parse(text);
            Assert.Equal(values, tokens);

            string formatted = ClaudeExtraArgsPolicy.Format(tokens);
            string shell = FindPowerShell();
            var psi = new ProcessStartInfo(shell)
            {
                // The same outer layer ConPtyTerminal.StartProcess builds: -Command "<script>".
                Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"& { foreach ($a in $args) { '[' + $a + ']' } }" + formatted + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(values.Select(v => "[" + v + "]").ToArray(), lines);
            Assert.DoesNotContain(lines, l => l == "PWNED");
        }

        private static string FindPowerShell()
        {
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            return File.Exists(sys) ? sys : "pwsh";
        }

        // ── Which flags a launch gets ────────────────────────────────────────

        private static List<ProjectAgent> Agents(params (string Name, string Extra)[] rows) =>
            rows.Select(r => new ProjectAgent { AgentName = r.Name, ExtraArgs = r.Extra }).ToList();

        [Fact]
        public void The_project_flags_come_first_then_the_agents()
        {
            var d = ClaudeExtraArgsPolicy.Decide("--verbose", Agents(("Vella", "--plugin-dir \"C:\\dolly\"")), "Vella");
            Assert.Equal(new[] { "--verbose", "--plugin-dir", @"C:\dolly" }, d.Tokens);
            Assert.Equal("project + agent 'Vella'", d.Source);
            Assert.Empty(d.Problems);
        }

        [Fact]
        public void Another_agent_gets_only_the_project_flags()
        {
            var d = ClaudeExtraArgsPolicy.Decide(null, Agents(("Vella-2", "--plugin-dir C:\\dolly")), "Quinn");
            Assert.Same(ClaudeExtraArgsDecision.None, d);
            Assert.Equal(string.Empty, d.Formatted);
        }

        [Fact]
        public void A_suffixed_name_uses_its_own_row_first_then_its_base_names()
        {
            var both = Agents(("Vella", "--a"), ("Vella-2", "--b"));
            Assert.Equal(new[] { "--b" }, ClaudeExtraArgsPolicy.Decide(null, both, "vella-2").Tokens);
            Assert.Equal(new[] { "--a" }, ClaudeExtraArgsPolicy.Decide(null, Agents(("Vella", "--a")), "Vella-3").Tokens);
            Assert.Null(ClaudeExtraArgsPolicy.FindAgentRow(Agents(("Vella", "--a")), "Vellas"));
            Assert.Null(ClaudeExtraArgsPolicy.FindAgentRow(Agents(("Vella-2", "--a")), "Vella"));
            Assert.Null(ClaudeExtraArgsPolicy.FindAgentRow(Agents(("Vella", "--a")), null));
        }

        [Fact]
        public void An_invalid_stored_level_is_skipped_with_a_problem_and_the_other_still_applies()
        {
            var d = ClaudeExtraArgsPolicy.Decide("--resume x", Agents(("Vella", "--plugin-dir C:\\dolly")), "Vella");
            Assert.Equal(new[] { "--plugin-dir", @"C:\dolly" }, d.Tokens);
            Assert.Single(d.Problems);
            Assert.Contains("project's extra Claude flags were skipped", d.Problems[0]);

            var onlyBad = ClaudeExtraArgsPolicy.Decide(null, Agents(("Vella", "\"open")), "Vella");
            Assert.Empty(onlyBad.Tokens);
            Assert.Equal(string.Empty, onlyBad.Formatted);
            Assert.Single(onlyBad.Problems);
        }

        // ── Where they land in a command ─────────────────────────────────────

        private static void WithTempDir(Action<string> body)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt_extra_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { body(dir); }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [Fact]
        public void The_flags_go_after_MTs_flags_and_before_model_resume_and_skip_permissions()
        {
            WithTempDir(dir =>
            {
                const string id = "0f8fad5b-d9cb-469f-a165-70867728950e";
                string extra = ClaudeExtraArgsPolicy.Format(new[] { "--plugin-dir", @"C:\dolly" });
                string plain = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                string full = LaunchCommandBuilder.BuildClaudeCommand(null, dir, id, "opus").AutoRunCommand;

                string withPlain = LaunchCommandBuilder.WithExtraArgs(plain, extra);
                Assert.Equal(plain.Replace(Skip + "; exit", extra + Skip + "; exit"), withPlain);

                string withFull = LaunchCommandBuilder.WithExtraArgs(full, extra);
                Assert.Equal(full.Replace(" --model opus", extra + " --model opus"), withFull);
                Assert.True(withFull.IndexOf(extra, StringComparison.Ordinal) < withFull.IndexOf(" --resume ", StringComparison.Ordinal));

                // Still a command ApplySessionName names, with -n right after the bare claude token.
                Assert.StartsWith("claude -n 'Vella-2'", ConPtyTerminal.ApplySessionName(withFull, "Vella-2"));
            });
        }

        /// <summary>
        /// MT's own flags carry quoted paths (the plugin, the MCP config) that can contain any text. The place for
        /// the extra flags and for <c>--model</c> is found from the END of the command, so a path holding
        /// " --model ", " --resume " or the skip flag is left alone (Codex adversary, run 1). Falsified: the
        /// previous IndexOf search inserted inside the plugin path and turned the first assertion red.
        /// </summary>
        [Fact]
        public void Text_inside_MTs_quoted_paths_is_never_taken_for_the_insertion_point()
        {
            const string id = "0f8fad5b-d9cb-469f-a165-70867728950e";
            const string flags = " --plugin-dir 'C:\\x --model a --resume b' --mcp-config 'C:\\y --dangerously-skip-permissions; exit z'";
            string extra = ClaudeExtraArgsPolicy.Format(new[] { "--plugin-dir", @"C:\dolly" });

            string plain = "claude" + flags + Skip + "; exit";
            Assert.Equal("claude" + flags + extra + Skip + "; exit", LaunchCommandBuilder.WithExtraArgs(plain, extra));

            string full = "claude" + flags + " --model opus --resume " + id + Skip + "; exit";
            Assert.Equal("claude" + flags + extra + " --model opus --resume " + id + Skip + "; exit", LaunchCommandBuilder.WithExtraArgs(full, extra));

            string resumed = "claude" + flags + " --resume " + id + Skip + "; exit";
            Assert.Equal("claude" + flags + " --model haiku --resume " + id + Skip + "; exit", LaunchCommandBuilder.WithModel(resumed, "haiku"));
            Assert.Equal("claude" + flags + " --model haiku" + Skip + "; exit", LaunchCommandBuilder.WithModel(plain, "haiku"));
        }

        [Theory]
        [InlineData("--bare")]
        [InlineData("--safe-mode")]
        [InlineData("--restricted")]
        [InlineData("--bg")]
        [InlineData("--background")]
        [InlineData("--cloud=x")]
        [InlineData("-w")]
        [InlineData("--worktree feature")]
        [InlineData("--no-session-persistence")]
        [InlineData("--disable-slash-commands")]
        [InlineData("--from-pr 12")]
        public void Flags_that_strip_MTs_plugin_or_leave_the_pane_are_refused(string text)
        {
            Assert.False(ClaudeExtraArgsPolicy.TryParse(text, out _, out string error));
            Assert.Contains("set by MultiTerminal itself", error);
        }

        /// <summary>
        /// The launch log names the flags but never their values, which can be secrets (Codex security, run 1).
        /// </summary>
        [Fact]
        public void The_log_text_has_flag_names_and_no_values()
        {
            var d = ClaudeExtraArgsPolicy.Decide("--plugin-dir \"C:\\secret path\" --append-system-prompt=TOKEN123 --verbose", null, null);
            string log = d.DescribeForLog();
            Assert.Equal("--plugin-dir --append-system-prompt --verbose (2 value(s) not logged)", log);
            Assert.DoesNotContain("secret", log);
            Assert.DoesNotContain("TOKEN123", log);
        }

        [Fact]
        public void No_flags_leave_the_command_unchanged_and_a_foreign_command_throws()
        {
            WithTempDir(dir =>
            {
                string plain = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                Assert.Equal(plain, LaunchCommandBuilder.WithExtraArgs(plain, string.Empty));
                Assert.Equal(plain, LaunchCommandBuilder.WithExtraArgs(plain, null));
            });
            Assert.Throws<ArgumentException>(() => LaunchCommandBuilder.WithExtraArgs("codex; exit", " '--x'"));
        }
    }
}
