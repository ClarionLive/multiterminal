using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        public void A_pane_with_nothing_running_is_never_coloured_by_its_last_agent()
        {
            // The process exited (or the pane is back on its start screen): the pane keeps its name,
            // and its last agent's entry is still in the snapshot until it goes Offline.
            var exited = Pane("p1", null, "Frank");
            exited.IsLive = false;

            var result = PaneAttention.Assign(
                new[] { exited },
                new[] { Entry("s1", "Frank", AttentionState.YourTurn) });

            Assert.Equal(PaneAttentionLevel.None, result["p1"]);
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

        private static string Read(params string[] parts) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string Hex(System.Drawing.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        /// <summary>The Attention panel variable each level borrows its hue from.</summary>
        private static string PanelVariable(PaneAttentionLevel level) => level switch
        {
            PaneAttentionLevel.YourTurn => "--yourturn",
            PaneAttentionLevel.Permission => "--peach",
            PaneAttentionLevel.Question => "--mauve",
            _ => null,
        };

        [Fact]
        public void The_header_tint_the_tab_dot_and_the_Attention_card_use_one_hue_per_state_and_theme()
        {
            // Three copies of these colours exist with no compiler between them: the Attention panel's
            // CSS variables, the header page's selectors and ColorFor (the tab dot). A pane and its card
            // must read as one signal, so every copy is compared, in both themes.
            string panel = Read("AttentionPanel", "attention-panel.html");
            int lightAt = panel.IndexOf("body.light {", StringComparison.Ordinal);
            Assert.True(lightAt > 0, "attention-panel.html no longer has a 'body.light {' block.");
            string panelDark = panel.Substring(0, lightAt);
            string panelLight = panel.Substring(lightAt, panel.IndexOf('}', lightAt) - lightAt);

            string header = Read("Controls", "TerminalStatusBar", "statusbar.html");
            Assert.Contains("data.startsWith('attention:')", header);

            int compared = 0;
            foreach (PaneAttentionLevel level in Enum.GetValues(typeof(PaneAttentionLevel)))
            {
                if (level == PaneAttentionLevel.None) continue;
                foreach (bool isDark in new[] { true, false })
                {
                    string expected = Hex(PaneAttention.ColorFor(level, isDark));

                    var card = Regex.Match(isDark ? panelDark : panelLight,
                        Regex.Escape(PanelVariable(level)) + @":\s*(#[0-9a-fA-F]{6})");
                    Assert.True(card.Success, $"{PanelVariable(level)} not found in the {(isDark ? "dark" : "light")} panel theme.");
                    Assert.Equal(expected, card.Groups[1].Value, StringComparer.OrdinalIgnoreCase);

                    string selector = (isDark ? "body" : "body.light") + $"[data-attention=\"{PaneAttention.WireName(level)}\"]";
                    var tint = Regex.Match(header, Regex.Escape(selector) + @"\s*\{\s*--attn:\s*(#[0-9a-fA-F]{6})");
                    Assert.True(tint.Success, $"'{selector} {{ --attn: ... }}' not found in statusbar.html.");
                    Assert.Equal(expected, tint.Groups[1].Value, StringComparer.OrdinalIgnoreCase);
                    compared++;
                }
            }

            Assert.Equal(6, compared);
        }
    }
}
