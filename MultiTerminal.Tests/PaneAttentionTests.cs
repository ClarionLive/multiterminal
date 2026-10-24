using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 51a8a17b: which terminal pane shows that its agent needs the Owner. Colouring the wrong
    /// pane sends the Owner to the wrong agent, so an entry that cannot be placed exactly colours
    /// nothing.
    /// </summary>
    public sealed class PaneAttentionTests
    {
        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static PaneAttention.PaneInfo Pane(string id, string session, string name, bool helper = false) =>
            new PaneAttention.PaneInfo { PaneId = id, SessionId = session, AgentName = name, IsHelper = helper };

        private static AgentAttentionEntry Entry(string session, string name, AttentionState state) =>
            new AgentAttentionEntry { SessionId = session, AgentName = name, State = state };

        [Fact]
        public void An_entry_colours_the_pane_holding_its_session_even_when_another_pane_has_the_same_name()
        {
            // Two panes named Alice: only the session id can tell them apart.
            var result = PaneAttention.Assign(
                new[] { Pane("p1", "s1", "Alice"), Pane("p2", "s2", "Alice") },
                new[] { Entry("s2", "Alice", AttentionState.BlockedPermission) });

            Assert.Equal(PaneAttentionLevel.None, result["p1"]);
            Assert.Equal(PaneAttentionLevel.Permission, result["p2"]);
        }

        [Fact]
        public void A_pane_without_a_session_yet_is_found_by_name_only_when_it_is_the_only_one()
        {
            var one = PaneAttention.Assign(
                new[] { Pane("p1", null, "Bob") },
                new[] { Entry("s9", "Bob", AttentionState.BlockedQuestion) });
            Assert.Equal(PaneAttentionLevel.Question, one["p1"]);

            var two = PaneAttention.Assign(
                new[] { Pane("p1", null, "Bob"), Pane("p2", null, "bob") },
                new[] { Entry("s9", "Bob", AttentionState.BlockedQuestion) });
            Assert.Equal(PaneAttentionLevel.None, two["p1"]);
            Assert.Equal(PaneAttentionLevel.None, two["p2"]);
        }

        [Fact]
        public void A_name_never_overrides_a_pane_that_holds_a_different_session()
        {
            // The pane moved on to a new session (/clear); an old entry for the name must not
            // colour it.
            var result = PaneAttention.Assign(
                new[] { Pane("p1", "s-new", "Carol") },
                new[] { Entry("s-old", "Carol", AttentionState.BlockedPermission) });

            Assert.Equal(PaneAttentionLevel.None, result["p1"]);
        }

        [Fact]
        public void Two_panes_claiming_one_session_are_both_left_uncoloured()
        {
            var result = PaneAttention.Assign(
                new[] { Pane("p1", "s1", "Dan"), Pane("p2", "s1", "Dan") },
                new[] { Entry("s1", "Dan", AttentionState.YourTurn) });

            Assert.Equal(PaneAttentionLevel.None, result["p1"]);
            Assert.Equal(PaneAttentionLevel.None, result["p2"]);
        }

        [Fact]
        public void A_helpers_turn_end_is_for_its_PM_but_its_blocks_still_show()
        {
            var result = PaneAttention.Assign(
                new[] { Pane("h1", "s1", "Helper1", helper: true), Pane("h2", "s2", "Helper2", helper: true) },
                new[]
                {
                    Entry("s1", "Helper1", AttentionState.YourTurn),
                    Entry("s2", "Helper2", AttentionState.BlockedPermission),
                });

            Assert.Equal(PaneAttentionLevel.None, result["h1"]);
            Assert.Equal(PaneAttentionLevel.Permission, result["h2"]);
        }

        [Fact]
        public void Every_pane_is_in_the_result_so_a_cleared_one_goes_back_to_none()
        {
            var result = PaneAttention.Assign(new[] { Pane("p1", "s1", "Eve") }, new List<AgentAttentionEntry>());

            Assert.Equal(PaneAttentionLevel.None, result["p1"]);
        }

        [Theory]
        [InlineData(AttentionState.BlockedQuestion, PaneAttentionLevel.Question)]
        [InlineData(AttentionState.BlockedPermission, PaneAttentionLevel.Permission)]
        [InlineData(AttentionState.BlockedUnknown, PaneAttentionLevel.Permission)]
        [InlineData(AttentionState.YourTurn, PaneAttentionLevel.YourTurn)]
        [InlineData(AttentionState.Working, PaneAttentionLevel.None)]
        [InlineData(AttentionState.Idle, PaneAttentionLevel.None)]
        [InlineData(AttentionState.Offline, PaneAttentionLevel.None)]
        [InlineData(AttentionState.Unknown, PaneAttentionLevel.None)]
        public void Each_state_maps_to_its_level(AttentionState state, PaneAttentionLevel expected)
        {
            Assert.Equal(expected, PaneAttention.LevelFor(state, isHelper: false));
        }

        [Fact]
        public void The_header_page_handles_every_wire_value_the_pane_can_send()
        {
            // The page and C# share these strings with no compiler between them.
            string html = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Controls", "TerminalStatusBar", "statusbar.html"));

            Assert.Contains("data.startsWith('attention:')", html);
            foreach (PaneAttentionLevel level in System.Enum.GetValues(typeof(PaneAttentionLevel)))
            {
                if (level == PaneAttentionLevel.None) continue;
                Assert.Contains($"body[data-attention=\"{PaneAttention.WireName(level)}\"]", html);
            }
        }
    }
}
