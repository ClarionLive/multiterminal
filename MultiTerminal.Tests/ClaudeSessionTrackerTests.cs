using MultiTerminal.Docking;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task ec2ed636: the launch fencing behind <see cref="TerminalDocument.CurrentClaudeSessionId"/>.
    /// These exercise <see cref="ClaudeSessionTracker"/> only. They do NOT cover the wiring: that
    /// TerminalDocument calls BeginLaunch in StartTerminal, EndLaunch on process exit / Home / dispose,
    /// and captures the generation before the statusline poll reads its file. Those call sites are
    /// build + review only (the document builds WebView2 renderers).
    /// </summary>
    public class ClaudeSessionTrackerTests
    {
        private const long LaunchMs = 1_000_000;

        [Fact]
        public void Adopts_an_own_file_written_during_the_running_launch()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);

            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 10, ownPaneFile: true));
            Assert.Equal("s1", t.SessionId);
        }

        [Fact]
        public void A_resume_launch_reserves_its_session_until_the_launch_ends()
        {
            // Pipeline Run 1: the reservation exists from BeginLaunch, before any statusline tick.
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs, "old-session");
            Assert.Equal("old-session", t.PendingResumeSessionId);
            Assert.Null(t.SessionId);

            // Adopting the same id (any case) keeps it.
            Assert.True(t.TryAdopt(t.Generation, "OLD-SESSION", LaunchMs + 10, ownPaneFile: true));
            Assert.Equal("old-session", t.PendingResumeSessionId);

            t.EndLaunch();
            Assert.Null(t.PendingResumeSessionId);
        }

        [Fact]
        public void Adopting_a_different_session_or_a_plain_relaunch_drops_the_reservation()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs, "old-session");
            Assert.True(t.TryAdopt(t.Generation, "forked", LaunchMs + 10, ownPaneFile: true));
            Assert.Null(t.PendingResumeSessionId);

            t.BeginLaunch(LaunchMs + 100, "old-session");
            t.BeginLaunch(LaunchMs + 200);
            Assert.Null(t.PendingResumeSessionId);
        }

        [Fact]
        public void A_refused_adoption_keeps_the_reservation()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs, "old-session");
            Assert.False(t.TryAdopt(t.Generation, "stale", LaunchMs - 1, ownPaneFile: true)); // previous process's file
            Assert.Equal("old-session", t.PendingResumeSessionId);
        }

        [Fact]
        public void Launch_start_time_is_reported_only_while_a_launch_runs()
        {
            // Read by the launch prompt's null-session pane rule (task ec2ed636, item 4).
            var t = new ClaudeSessionTracker();
            Assert.Null(t.ActiveLaunchStartedMs);

            t.BeginLaunch(LaunchMs);
            Assert.Equal(LaunchMs, t.ActiveLaunchStartedMs);

            t.EndLaunch();
            Assert.Null(t.ActiveLaunchStartedMs);

            t.BeginLaunch(LaunchMs + 500);
            Assert.Equal(LaunchMs + 500, t.ActiveLaunchStartedMs);
        }

        [Fact]
        public void Exit_clears_and_the_leftover_file_cannot_bring_it_back()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);
            t.TryAdopt(t.Generation, "s1", LaunchMs + 10, ownPaneFile: true);

            t.EndLaunch();

            Assert.Null(t.SessionId);
            // A tick that starts after the exit sees the current generation but no active launch.
            Assert.False(t.TryAdopt(t.Generation, "s1", LaunchMs + 20, ownPaneFile: true));
            Assert.Null(t.SessionId);
        }

        [Fact]
        public void A_tick_that_straddles_a_launch_boundary_adopts_nothing()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);
            long observed = t.Generation;

            t.EndLaunch();
            t.BeginLaunch(LaunchMs + 100);

            // The file is fresh enough for the new launch, so only the generation can refuse it.
            Assert.False(t.TryAdopt(observed, "old", LaunchMs + 200, ownPaneFile: true));
            Assert.Null(t.SessionId);
        }

        [Fact]
        public void A_relaunch_does_not_adopt_the_previous_process_file()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);
            t.TryAdopt(t.Generation, "old", LaunchMs + 10, ownPaneFile: true);

            t.BeginLaunch(LaunchMs + 100);   // "Launch as...": Stop then StartTerminal, no exit event

            Assert.Null(t.SessionId);
            Assert.False(t.TryAdopt(t.Generation, "old", LaunchMs + 10, ownPaneFile: true));
            Assert.True(t.TryAdopt(t.Generation, "new", LaunchMs + 150, ownPaneFile: true));
            Assert.Equal("new", t.SessionId);
        }

        [Fact]
        public void A_foreign_or_untimestamped_file_is_never_adopted()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);

            Assert.False(t.TryAdopt(t.Generation, "sibling", LaunchMs + 10, ownPaneFile: false));
            Assert.False(t.TryAdopt(t.Generation, "s1", null, ownPaneFile: true));
            Assert.Null(t.SessionId);
        }

        // ── Task 7d20e94c item 1: TryAdopt reports a CHANGE, which is what TerminalDocument raises
        // ClaudeSessionAdopted on. The raise itself (and its BeginInvoke) is in the WebView2-backed
        // document and is build + review only; these pin the decision it raises on.

        [Fact]
        public void A_new_id_reports_a_change_once_and_its_repeats_do_not()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);

            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 10, true, out bool changed, out string previous));
            Assert.True(changed);
            Assert.Null(previous);

            // The statusline re-reports the same id on every refresh; same id in another case is the same session.
            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 20, true, out changed, out previous));
            Assert.False(changed);
            Assert.Equal("s1", previous);
            Assert.True(t.TryAdopt(t.Generation, "S1", LaunchMs + 30, true, out changed, out _));
            Assert.False(changed);
        }

        [Fact]
        public void A_forked_id_reports_a_change_with_the_previous_id()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs, "old-session");
            Assert.True(t.TryAdopt(t.Generation, "old-session", LaunchMs + 10, true, out bool changed, out _));
            Assert.True(changed);

            Assert.True(t.TryAdopt(t.Generation, "forked", LaunchMs + 20, true, out changed, out string previous));
            Assert.True(changed);
            Assert.Equal("old-session", previous);
            Assert.Equal("forked", t.SessionId);
        }

        [Fact]
        public void A_fenced_off_adoption_reports_no_change()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);
            long observed = t.Generation;
            Assert.True(t.TryAdopt(observed, "s1", LaunchMs + 10, true, out _, out _));

            // A different id that every fence refuses, one fence at a time: stale generation,
            // previous process's file, foreign file, no timestamp, ended launch.
            t.BeginLaunch(LaunchMs + 100);
            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 110, true, out _, out _));
            Assert.False(t.TryAdopt(observed, "s2", LaunchMs + 200, true, out bool changed, out string previous));
            Assert.False(changed);
            Assert.Null(previous);
            Assert.False(t.TryAdopt(t.Generation, "s2", LaunchMs + 50, true, out changed, out _));
            Assert.False(changed);
            Assert.False(t.TryAdopt(t.Generation, "s2", LaunchMs + 200, false, out changed, out _));
            Assert.False(changed);
            Assert.False(t.TryAdopt(t.Generation, "s2", null, true, out changed, out _));
            Assert.False(changed);
            t.EndLaunch();
            Assert.False(t.TryAdopt(t.Generation, "s2", LaunchMs + 200, true, out changed, out _));
            Assert.False(changed);
        }

        [Fact]
        public void A_relaunch_reports_the_same_id_as_a_change_with_no_previous()
        {
            // A resume of the session the pane last ran is a new launch: subscribers must hear it again.
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);
            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 10, true, out _, out _));

            t.BeginLaunch(LaunchMs + 100, "s1");
            Assert.True(t.TryAdopt(t.Generation, "s1", LaunchMs + 110, true, out bool changed, out string previous));
            Assert.True(changed);
            Assert.Null(previous);
        }

        [Theory]
        [InlineData(null, PaneLaunchKind.PowerShell)]
        [InlineData("   ", PaneLaunchKind.PowerShell)]
        [InlineData("claude --mcp-config 'x.json' --dangerously-skip-permissions; exit", PaneLaunchKind.Claude)]
        [InlineData("claude; exit", PaneLaunchKind.Claude)]
        [InlineData("\"C:\\Tools\\claude.exe\" -r 3f2504e0-4f89-11d3-9a0c-0305e82c3301", PaneLaunchKind.Claude)]
        [InlineData("$env:MULTITERMINAL_CODEX_BIN = 'codex'; & 'C:\\x\\launch.ps1'; exit", PaneLaunchKind.Codex)]
        [InlineData("codex --yolo", PaneLaunchKind.Codex)]
        [InlineData("claudette --help", PaneLaunchKind.Unknown)]
        [InlineData("npm run dev", PaneLaunchKind.Unknown)]
        public void Launch_kind_is_derived_from_the_auto_run_command(string command, string expected)
        {
            Assert.Equal(expected, PaneLaunchKind.FromAutoRunCommand(command));
        }

        [Fact]
        public void An_empty_session_id_is_never_adopted()
        {
            var t = new ClaudeSessionTracker();
            t.BeginLaunch(LaunchMs);

            // Every other fence passes, so only the empty-id check can refuse these.
            Assert.False(t.TryAdopt(t.Generation, "", LaunchMs + 10, ownPaneFile: true));
            Assert.False(t.TryAdopt(t.Generation, null, LaunchMs + 10, ownPaneFile: true));
            Assert.Null(t.SessionId);
        }
    }
}
