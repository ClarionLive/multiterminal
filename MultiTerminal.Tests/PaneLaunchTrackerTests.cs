using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="PaneLaunchTracker"/> (task 7d20e94c item 2): which launch a pane runs, which end reason
    /// each close writes, the spawner link in either order, and the shutdown guard. Against a fake sink
    /// that records every call in order, so each fact asserts exactly what was written.
    /// </summary>
    public sealed class PaneLaunchTrackerTests
    {
        private sealed class FakeSink : IPaneLaunchSink
        {
            private int _next;
            public readonly List<string> Calls = new List<string>();
            public bool FailBegin;

            public string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
            {
                if (FailBegin) { Calls.Add($"begin-failed {docId}"); return null; }
                string id = "L" + (++_next);
                Calls.Add($"begin {docId} -> {id}");
                return id;
            }

            public void AdoptSession(string launchId, string sessionId) => Calls.Add($"adopt {launchId} {sessionId}");
            public void LinkSpawner(string childLaunchId, string spawnerLaunchId) => Calls.Add($"link {childLaunchId} <- {spawnerLaunchId}");
            public void End(string launchId, string reason) => Calls.Add($"end {launchId} {reason}");
            public void EndAllForShutdown() => Calls.Add("end-all");
        }

        private static string Begin(PaneLaunchTracker t, string docId) => t.OnLaunchBegan(docId, "n-" + docId, "p", @"C:\w", "claude", null);

        [Fact]
        public void A_relaunch_of_an_open_pane_ends_the_previous_launch_process_exit_before_beginning()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "A");
            Begin(t, "A");
            Assert.Equal(new[] { "begin A -> L1", "end L1 process_exit", "begin A -> L2" }, sink.Calls);
            Assert.Equal("L2", t.GetCurrentLaunchId("A"));
        }

        [Fact]
        public void Close_helper_first_wins_and_the_following_pane_closed_writes_nothing()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "H");
            t.OnHelperClosedBySpawner("H");
            t.OnPaneClosed("H");
            Assert.Equal(new[] { "begin H -> L1", "end L1 close_helper" }, sink.Calls);
        }

        [Fact]
        public void Each_close_path_writes_its_own_reason()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "A");
            Begin(t, "B");
            t.OnPaneClosed("A");
            t.OnProcessExited("B");
            Assert.Contains("end L1 pane_closed", sink.Calls);
            Assert.Contains("end L2 process_exit", sink.Calls);
            Assert.Null(t.GetCurrentLaunchId("A"));
            Assert.Null(t.GetCurrentLaunchId("B"));
        }

        [Fact]
        public void After_shutdown_began_no_close_path_writes_and_shutdown_ends_all_once()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "A");
            Begin(t, "B");
            Begin(t, "C");
            t.BeginShutdown();
            t.BeginShutdown();
            t.OnPaneClosed("A");
            t.OnProcessExited("B");
            t.OnHelperClosedBySpawner("C");
            Begin(t, "A"); // a relaunch while shutting down must not end the previous launch either

            Assert.True(t.IsShuttingDown);
            Assert.Equal(1, sink.Calls.Count(c => c == "end-all"));
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("end L", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Link_after_the_child_launch_exists_links_at_once_to_the_spawners_current_launch()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "PM");    // L1
            Begin(t, "H");     // L2
            t.LinkSpawner("H", "PM");
            Assert.Equal("link L2 <- L1", sink.Calls.Last());
        }

        [Fact]
        public void Link_before_the_child_launch_is_applied_once_when_it_begins()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "PM");    // L1
            t.LinkSpawner("H", "PM");
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("link", System.StringComparison.Ordinal));

            Begin(t, "H");     // L2
            Assert.Equal("link L2 <- L1", sink.Calls.Last());

            Begin(t, "H");     // L3: a later launch of the same pane is not the spawned one
            Assert.Equal(1, sink.Calls.Count(c => c.StartsWith("link", System.StringComparison.Ordinal)));
        }

        [Fact]
        public void A_pending_link_keeps_the_spawner_launch_of_the_moment_it_was_made()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "PM");    // L1
            t.LinkSpawner("H", "PM");
            Begin(t, "PM");    // L2 (and L1 ended)
            Begin(t, "H");     // L3
            Assert.Equal("link L3 <- L1", sink.Calls.Last());
        }

        [Fact]
        public void No_spawner_launch_means_no_link_now_or_later()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            t.LinkSpawner("H", "PM");
            Begin(t, "H");
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("link", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Closing_the_child_before_its_launch_drops_the_pending_link()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "PM");
            t.LinkSpawner("H", "PM");
            t.OnPaneClosed("H");
            Begin(t, "H");
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("link", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Session_adoption_goes_to_the_current_launch_and_stops_after_it_ended()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "A");
            t.OnSessionAdopted("A", "s1");
            t.OnProcessExited("A");
            t.OnSessionAdopted("A", "s2");
            Assert.Contains("adopt L1 s1", sink.Calls);
            Assert.DoesNotContain(sink.Calls, c => c.Contains("s2"));
        }

        [Fact]
        public void A_launch_row_that_failed_to_write_leaves_no_current_launch()
        {
            var sink = new FakeSink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "A");
            sink.FailBegin = true;
            Assert.Null(Begin(t, "A"));
            Assert.Null(t.GetCurrentLaunchId("A"));
            t.OnPaneClosed("A");
            Assert.Equal(new[] { "begin A -> L1", "end L1 process_exit", "begin-failed A" }, sink.Calls);
        }
    }
}
