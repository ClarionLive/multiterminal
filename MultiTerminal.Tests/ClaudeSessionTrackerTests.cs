using MultiTerminal.Docking;
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
