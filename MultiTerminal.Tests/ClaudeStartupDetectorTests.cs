using System;
using System.Linq;
using MultiTerminal.Controls;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="ClaudeStartupDetector"/> (task 00cdd389): the first terminal after MT launched
    /// sat on its Claude Code banner with no "initializing..." typed, and twelve minutes later the
    /// still-armed detector typed it into that session's live conversation instead.
    ///
    /// <para>The two MATCH-CONDITION fixtures (escape-spaced banner, agent prose) also assert that the
    /// replaced condition, <see cref="LegacyBannerCheck"/>, copied verbatim, gets them wrong, so neither
    /// could pass under the old code.</para>
    ///
    /// <para>Separately, these behaviours were each falsified by reintroducing the old or broken form
    /// and watching the named facts go red. The ticket notes record each run with its expected count:
    /// scanning after the trim, never disarming, re-arming from typed input, arming a plain shell at
    /// start, redacting after the excerpt cut, no redaction, counting any mention of the npm package as
    /// a launch, counting non-session flags and subcommands as a launch, redacting only unbroken runs,
    /// and not redacting URL passwords. Other facts pin the new behaviour without discriminating on the
    /// behaviour they are named for. The literal-spaced banner fact passes under the old code outright.
    /// The split-chunk and single-character facts would fail under it, but only because their fixture is
    /// the escape-spaced banner, not because of what they test.</para>
    ///
    /// <para>Provenance of the fixtures: the escape-spaced BANNER is SYNTHESIZED. No raw banner bytes
    /// have been captured yet; it applies the spacing convention visible in the development-channel
    /// dialog MT dumped at 2026-09-22 11:38:19 (<c>WARNING:\x1b[1CLoading</c>) to the banner text shown
    /// on screen. That dialog's own fixture went with its auto-accept (ticket 0ff1b520, item 16).</para>
    /// </summary>
    public class ClaudeStartupDetectorTests
    {
        private const string Esc = "\u001b";

        /// <summary>Banner with spaces drawn as cursor-forward escapes. Synthesized; see the class doc.</summary>
        private static readonly string EscapeSpacedBanner =
            Esc + "[1mClaude" + Esc + "[1CCode" + Esc + "[m" + Esc + "[1C" + Esc + "[38;2;153;153;153mv2.1.280" + Esc + "[m";

        /// <summary>
        /// A startup confirmation prompt, drawn escape-spaced like every Claude Code dialog, ending in the
        /// generic "Enter to confirm" line the removed dev-channel auto-accept used to match on.
        /// </summary>
        private static readonly string ConfirmPrompt =
            Esc + "[3;3HDo" + Esc + "[1Cyou" + Esc + "[1Ctrust" + Esc + "[1Cthe" + Esc + "[1Cfiles" + Esc + "[1Cin" +
            Esc + "[1Cthis" + Esc + "[1Cfolder?" + Esc + "[12;3H>" + Esc + "[1C1." + Esc + "[1CYes" +
            Esc + "[13;5H2." + Esc + "[1CNo" + Esc + "[15;3HEnter" + Esc + "[1Cto" + Esc + "[1Cconfirm" + Esc + "[1C·" +
            Esc + "[1CEsc" + Esc + "[1Cto" + Esc + "[1Ccancel";

        /// <summary>Shaped like CLAUDE_CODE_MESSAGING_TOKEN: 32 hex characters. Not a real credential.</summary>
        private const string FakeToken = "0123456789abcdef0123456789abcdef";

        /// <summary>The condition this ticket replaced (TerminalControl.cs before 00cdd389), verbatim.</summary>
        private static bool LegacyBannerCheck(string buffer) =>
            buffer.Contains("Claude Code") ||
            buffer.Contains("claude-code") ||
            (buffer.Contains("╭─") && buffer.Contains("Tips"));

        [Fact]
        public void Recognises_a_banner_whose_spaces_are_cursor_forward_escapes()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            StartupScan scan = detector.Append(EscapeSpacedBanner);

            Assert.Equal("banner (escape-spaced)", scan.BannerAnchor);
            Assert.True(detector.BannerDetected);
            Assert.False(LegacyBannerCheck(EscapeSpacedBanner)); // the old check missed exactly this
        }

        [Fact]
        public void Recognises_a_banner_drawn_with_literal_spaces()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            StartupScan scan = detector.Append(Esc + "[1mClaude Code" + Esc + "[m v2.1.280");

            Assert.Equal("banner (spaced)", scan.BannerAnchor);
        }

        [Fact]
        public void A_banner_split_across_two_chunks_is_still_recognised()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);
            int cut = EscapeSpacedBanner.IndexOf("Code", StringComparison.Ordinal) + 2;

            StartupScan first = detector.Append(EscapeSpacedBanner.Substring(0, cut));
            StartupScan second = detector.Append(EscapeSpacedBanner.Substring(cut));

            Assert.Null(first.BannerAnchor);
            Assert.NotNull(second.BannerAnchor);
        }

        /// <summary>
        /// The exact text that fired the 2026-09-22 11:40:51 false trigger: an agent printing a debug
        /// log line into its own pane. The version anchor is what keeps prose from matching.
        /// </summary>
        [Fact]
        public void Agent_prose_mentioning_Claude_Code_is_not_a_banner()
        {
            const string prose = "11:40:51.944 [MainForm] Claude Code detected in terminal";
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            StartupScan scan = detector.Append(prose);

            Assert.Null(scan.BannerAnchor);
            Assert.True(LegacyBannerCheck(prose)); // the old check fired on exactly this
        }

        /// <summary>
        /// A confirmation prompt is not a banner, reports nothing, and leaves detection watching for the
        /// banner that follows it. Before item 16 this same text made the detector report a dev-channel
        /// warning, which TerminalControl answered by typing "1" into whatever the prompt was. This fact
        /// alone would still pass under that code (it pins that the banner is found after a prompt); what
        /// fails if a prompt action comes back is the structural pin below.
        /// </summary>
        [Fact]
        public void A_confirmation_prompt_triggers_nothing_and_the_banner_is_still_found_after_it()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            StartupScan scan = detector.Append(ConfirmPrompt);

            Assert.Null(scan.BannerAnchor);
            Assert.Null(scan.DisarmLog);
            Assert.True(detector.IsWatching);
            Assert.NotNull(detector.Append(EscapeSpacedBanner).BannerAnchor);
        }

        /// <summary>
        /// Structural pin on what a scan can ask TerminalControl to do. Every member of
        /// <see cref="StartupScan"/> is acted on there, so a new one is a new startup action. Adding one is
        /// allowed; adding one without asking which OTHER prompts its match could fire on is how "enter to
        /// confirm" came to be armed. Update this list in the same commit, deliberately.
        /// </summary>
        [Fact]
        public void A_startup_scan_reports_only_the_banner_and_a_disarm()
        {
            string[] members = typeof(StartupScan)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { nameof(StartupScan.BannerAnchor), nameof(StartupScan.DisarmLog) }, members);
        }

        /// <summary>
        /// The old code trimmed to the last 1000 characters BEFORE checking, so a banner near the
        /// start of one large chunk was discarded unseen.
        /// </summary>
        [Fact]
        public void A_banner_at_the_start_of_one_oversized_chunk_is_scanned_before_the_trim()
        {
            string chunk = EscapeSpacedBanner + new string('x', ClaudeStartupDetector.MaxBufferChars + 1000);
            Assert.True(chunk.IndexOf("Claude", StringComparison.Ordinal) < chunk.Length - ClaudeStartupDetector.TrimToChars);
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            StartupScan scan = detector.Append(chunk);

            Assert.NotNull(scan.BannerAnchor);
        }

        [Fact]
        public void A_submitted_prompt_disarms_detection_and_logs_once()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);
            detector.Append("some startup output");

            string log = detector.NotifyLineSubmitted("hello");
            StartupScan later = detector.Append(EscapeSpacedBanner);

            Assert.NotNull(log);
            Assert.True(detector.Disarmed);
            Assert.False(detector.IsWatching);
            Assert.Null(later.BannerAnchor);
            Assert.Null(detector.NotifyLineSubmitted("again")); // once, not per prompt
        }

        /// <summary>A startup menu can be answered with one key; that is not the session starting.</summary>
        [Fact]
        public void A_single_character_submission_does_not_disarm()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);

            Assert.Null(detector.NotifyLineSubmitted("1"));
            Assert.Null(detector.NotifyLineSubmitted("   "));
            Assert.False(detector.Disarmed);
            Assert.NotNull(detector.Append(EscapeSpacedBanner).BannerAnchor);
        }

        [Fact]
        public void Output_after_the_banner_window_disarms_instead_of_matching()
        {
            var now = new DateTime(2026, 9, 22, 18, 28, 0, DateTimeKind.Utc);
            var detector = new ClaudeStartupDetector(launchesClaude: true, utcNow: () => now);

            now += ClaudeStartupDetector.BannerWindow + TimeSpan.FromSeconds(1);
            StartupScan scan = detector.Append(EscapeSpacedBanner);

            Assert.Null(scan.BannerAnchor);
            Assert.NotNull(scan.DisarmLog);
            Assert.True(detector.Disarmed);
        }

        [Fact]
        public void Disarming_after_the_banner_was_seen_logs_nothing()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);
            detector.Append(EscapeSpacedBanner);

            Assert.Null(detector.NotifyLineSubmitted("initializing..."));
        }

        [Fact]
        public void Reset_for_a_claude_launch_rearms_and_restarts_the_window()
        {
            var now = new DateTime(2026, 9, 22, 18, 28, 0, DateTimeKind.Utc);
            var detector = new ClaudeStartupDetector(launchesClaude: true, utcNow: () => now);
            detector.NotifyLineSubmitted("hello");

            now += ClaudeStartupDetector.BannerWindow + TimeSpan.FromSeconds(1);
            detector.Reset(launchesClaude: true);
            StartupScan scan = detector.Append(EscapeSpacedBanner);

            Assert.NotNull(scan.BannerAnchor);
        }

        // ---- Launch provenance (pipeline run 1: adversary HIGH, debugger MEDIUM) ----

        /// <summary>
        /// A plain shell is not armed at start, so text a program prints there, before anyone has
        /// launched Claude, cannot make MT type into the pane.
        /// </summary>
        [Fact]
        public void A_plain_shell_ignores_banner_text_until_claude_is_launched()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: false);

            StartupScan scan = detector.Append(EscapeSpacedBanner);

            Assert.Null(scan.BannerAnchor);
            Assert.True(detector.AwaitingShellLaunch);
        }

        /// <summary>
        /// The Owner runs other commands first, then types a launch line. Shell commands must not use
        /// up the one arming, or this flow loses the auto-start it always had.
        /// </summary>
        [Theory]
        [InlineData("claude")]
        [InlineData("claude --resume")]
        [InlineData("C:\\Users\\x\\.local\\bin\\claude.exe -n Alice")]
        [InlineData("& claude")]
        [InlineData("& \"C:\\Program Files\\Claude Tools\\claude.exe\" --resume")]
        [InlineData("npx @anthropic-ai/claude-code")]
        public void Typing_a_claude_launch_in_a_plain_shell_arms_detection(string launch)
        {
            var detector = new ClaudeStartupDetector(launchesClaude: false);
            Assert.Null(detector.NotifyLineSubmitted("ls"));
            Assert.Null(detector.NotifyLineSubmitted("cd src"));

            Assert.NotNull(detector.NotifyLineSubmitted(launch));
            StartupScan scan = detector.Append(EscapeSpacedBanner);

            Assert.NotNull(scan.BannerAnchor);
        }

        /// <summary>
        /// The debugger's and the adversary's reproduction: the banner is MISSED (the unexplained
        /// case), the Owner prompts, then types a prompt that begins with "claude". That must not
        /// re-arm, or an agent reply mentioning "Claude Code v2..." types into the live conversation.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_prompt_starting_with_claude_after_a_missed_banner_never_rearms(bool launchesClaude)
        {
            var detector = new ClaudeStartupDetector(launchesClaude);
            if (!launchesClaude) detector.NotifyLineSubmitted("claude");
            detector.Append("banner rendered in some unrecognised form");
            detector.NotifyLineSubmitted("hello");

            Assert.Null(detector.NotifyLineSubmitted("claude code keeps crashing, check the log"));
            Assert.Null(detector.NotifyLineSubmitted("upgrade @anthropic-ai/claude-code please"));
            StartupScan scan = detector.Append("Sure. Claude Code v2.1.280 changed that.");

            Assert.Null(scan.BannerAnchor);
            Assert.True(detector.Disarmed);
        }

        /// <summary>
        /// The debugger's run-2 repro: a plain shell gets ONE arming, so a command that prints and exits
        /// must not spend it. Otherwise the real launch that follows is treated as a prompt and disarms.
        /// </summary>
        [Theory]
        [InlineData("claude --version")]
        [InlineData("claude -v")]
        [InlineData("claude mcp list")]
        [InlineData("claude update")]
        [InlineData("claude doctor")]
        [InlineData("claude -p \"summarise this\"")]
        [InlineData("claude --dangerously-skip-permissions --print hi")]
        [InlineData("npm i -g @anthropic-ai/claude-code")]
        [InlineData("npm view @anthropic-ai/claude-code version")]
        [InlineData("echo @anthropic-ai/claude-code")]
        [InlineData("npx cowsay @anthropic-ai/claude-code")]
        [InlineData("npx --yes cowsay @anthropic-ai/claude-code")]
        [InlineData("npm exec cowsay @anthropic-ai/claude-code")]
        public void A_non_session_command_does_not_spend_the_shell_arming(string command)
        {
            var detector = new ClaudeStartupDetector(launchesClaude: false);

            Assert.Null(detector.NotifyLineSubmitted(command));
            Assert.True(detector.AwaitingShellLaunch);
            Assert.NotNull(detector.NotifyLineSubmitted("claude"));
            Assert.NotNull(detector.Append(EscapeSpacedBanner).BannerAnchor);
        }

        [Theory]
        [InlineData("npx @anthropic-ai/claude-code@latest")]
        [InlineData("pnpm dlx @anthropic-ai/claude-code")]
        [InlineData("npm exec @anthropic-ai/claude-code")]
        [InlineData("bunx @anthropic-ai/claude-code --resume")]
        [InlineData("npx --yes @anthropic-ai/claude-code")]
        public void Running_the_npm_package_through_a_runner_is_a_launch_line(string line)
        {
            Assert.True(ClaudeStartupDetector.IsClaudeLaunchLine(line));
        }

        [Theory]
        [InlineData("claude, what does this do?")]
        [InlineData("ask claude later")]
        [InlineData("claudette")]
        [InlineData("&")]
        [InlineData("\"")]
        [InlineData("   ")]
        public void Lines_that_do_not_run_claude_are_not_launch_lines(string line)
        {
            Assert.False(ClaudeStartupDetector.IsClaudeLaunchLine(line));
        }

        /// <summary>MT's own launch command is what arms an agent pane, so it has to be recognised.</summary>
        [Fact]
        public void MTs_launch_command_is_a_launch_line()
        {
            Assert.True(ClaudeStartupDetector.IsClaudeLaunchLine("claude -n 'Alice' --mcp-config 'x.json' --dangerously-skip-permissions; exit"));
            Assert.True(ClaudeStartupDetector.IsClaudeLaunchLine("claude; exit"));
        }

        // ---- What reaches the agent-readable debug log (pipeline run 1: security HIGH) ----

        [Fact]
        public void The_no_banner_dump_is_excerpts_around_claude_with_tokens_redacted()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);
            detector.Append("PS> $env:SECRET\r\n" + FakeToken + "\r\n" + new string('.', 400) +
                            "\r\nwelcome to claude, token " + FakeToken + " again\r\n" + new string('.', 400));

            string log = detector.NotifyLineSubmitted("hello");

            Assert.NotNull(log);
            Assert.Contains("welcome to claude", log);
            Assert.DoesNotContain("SECRET", log);                  // outside every excerpt window
            Assert.DoesNotContain(FakeToken.Substring(0, 12), log); // inside a window, but redacted
            Assert.Contains("[redacted 32]", log);
        }

        [Fact]
        public void The_no_banner_dump_says_when_claude_never_appeared()
        {
            var detector = new ClaudeStartupDetector(launchesClaude: true);
            detector.Append("nothing relevant");

            string log = detector.NotifyLineSubmitted("hello");

            Assert.Contains("no \"claude\" text", log);
        }

        /// <summary>
        /// A token cut by the excerpt boundary must not survive as a short, unredacted tail. The
        /// redaction happens before the cut, which is what this pins.
        /// </summary>
        [Fact]
        public void A_token_straddling_the_excerpt_boundary_is_not_partly_leaked()
        {
            string token = new string('A', 40);
            string padding = new string('.', ClaudeStartupDetector.ExcerptRadius - 10);
            string raw = token + padding + "claude";

            string excerpt = ClaudeStartupDetector.ExcerptForLog(raw);

            Assert.DoesNotContain("AAAAA", excerpt);
        }

        /// <summary>
        /// Security run 2: secrets split by '-' or '.' can slip past a redactor that only looks for long
        /// unbroken runs. All values below are made up but have the real shapes. Only the UUID actually
        /// depends on the structured rule: the key and JWT fixtures each contain a 20+ character unbroken
        /// run, so the older rule already catches them. Disabling the structured rule turned exactly one
        /// case red. They stay as shape coverage, not as proof of that rule.
        /// </summary>
        [Theory]
        [InlineData("sk-ant-api03-Zx9Qk2Lm7Np4Rt8Vw1Yb")]
        [InlineData("123e4567-e89b-12d3-a456-426614174000")]
        [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N")]
        public void Structured_secrets_near_claude_are_redacted(string secret)
        {
            string excerpt = ClaudeStartupDetector.ExcerptForLog("claude token=" + secret + " end");

            Assert.DoesNotContain(secret, excerpt);
            Assert.DoesNotContain(secret.Substring(secret.Length - 8), excerpt);
            Assert.Contains("[redacted", excerpt);
        }

        [Fact]
        public void A_password_in_a_url_is_redacted()
        {
            string excerpt = ClaudeStartupDetector.ExcerptForLog("claude fetching https://bob:hunter2@example.com/repo");

            Assert.DoesNotContain("hunter2", excerpt);
            Assert.Contains("example.com", excerpt);
        }

        /// <summary>
        /// Redaction runs between escapes, not across them. Across them, the cursor move's "3H" glued onto
        /// a hyphenated flag gave it a digit and made it look like a structured token.
        /// </summary>
        [Fact]
        public void A_hyphenated_flag_after_a_cursor_move_survives_redaction_and_a_token_does_not()
        {
            string excerpt = ClaudeStartupDetector.ExcerptForLog(
                "claude " + Esc + "[5;3H--dangerously-skip-permissions" + Esc + "[1C" + FakeToken);

            Assert.Contains("--dangerously-skip-permissions", excerpt);
            Assert.DoesNotContain(FakeToken, excerpt);
        }
    }
}
