#nullable enable
using System.Collections.Generic;
using MultiTerminal.Docking;
using Xunit;
using Kind = MultiTerminal.Docking.TerminalPlacement.Kind;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="TerminalPlacement"/> (task 7f91349f): where a new terminal goes. Before this, a
    /// terminal was floated once every pane was full, and helpers spawned in bursts opened as tiny
    /// separate windows the Owner had to re-dock. Panes here are plain named objects; no DockPanel.
    /// </summary>
    public class TerminalPlacementTests
    {
        private sealed class Pane
        {
            public Pane(string name) => Name = name;

            public string Name { get; }

            public override string ToString() => Name;
        }

        private static TerminalPlacement.PaneInfo<Pane> Docked(Pane p, int terminals) => new(p, true, terminals);

        private static TerminalPlacement.PaneInfo<Pane> Floating(Pane p, int terminals) => new(p, false, terminals);

        /// <summary>
        /// The reported bug: the Owner's MaxGridPanes=2 with both panes full. The old code floated here.
        /// The answer must be a tab in a docked pane, and the emptier one.
        /// </summary>
        [Fact]
        public void Full_panes_take_a_tab_in_the_emptiest_pane_instead_of_floating()
        {
            var left = new Pane("left");
            var right = new Pane("right");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(left, 6), Docked(right, 5) }, left, maxGrids: 2);

            Assert.Equal(Kind.TabInto, d.Kind);
            Assert.Same(right, d.Target);
        }

        /// <summary>
        /// A floating window is not a grid slot. With one docked pane and one float under MaxGridPanes=2,
        /// the old code counted two grid panes and tabbed into whichever had fewer contents, which was
        /// the float. The float must not be counted, so there is still a free slot to split into.
        /// </summary>
        [Fact]
        public void A_floating_pane_is_not_counted_as_a_grid_slot_or_used_as_a_tab_target()
        {
            var main = new Pane("main");
            var tiny = new Pane("tiny float");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(main, 3), Floating(tiny, 1) }, null, maxGrids: 2);

            Assert.Equal(Kind.SplitFrom, d.Kind);
            Assert.Same(main, d.Target);
        }

        /// <summary>
        /// When the grid is full and a float holds fewer terminals than any docked pane, the float is
        /// still never chosen as the tab target.
        /// </summary>
        [Fact]
        public void With_the_grid_full_an_emptier_float_is_still_not_chosen()
        {
            var a = new Pane("a");
            var b = new Pane("b");
            var tiny = new Pane("tiny float");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(a, 4), Floating(tiny, 1), Docked(b, 3) }, tiny, maxGrids: 2);

            Assert.Equal(Kind.TabInto, d.Kind);
            Assert.Same(b, d.Target);
        }

        /// <summary>
        /// The active terminal is in a floating window: splitting from it put the new terminal inside
        /// that window. The split source must be a docked pane, the last one.
        /// </summary>
        [Fact]
        public void A_floating_active_pane_is_not_used_as_the_split_source()
        {
            var first = new Pane("first");
            var second = new Pane("second");
            var tiny = new Pane("tiny float");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(first, 1), Docked(second, 1), Floating(tiny, 1) }, tiny, maxGrids: 4);

            Assert.Equal(Kind.SplitFrom, d.Kind);
            Assert.Same(second, d.Target);
        }

        /// <summary>A docked active pane is still the split source, as before.</summary>
        [Fact]
        public void A_docked_active_pane_is_the_split_source()
        {
            var first = new Pane("first");
            var second = new Pane("second");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(first, 1), Docked(second, 1) }, first, maxGrids: 4);

            Assert.Equal(Kind.SplitFrom, d.Kind);
            Assert.Same(first, d.Target);
        }

        /// <summary>
        /// Only floating terminals exist: there is nothing docked to split or tab into, so the terminal
        /// becomes a plain document. It is not added to the float.
        /// </summary>
        [Fact]
        public void With_only_floating_terminals_the_new_one_is_a_plain_document()
        {
            var tiny = new Pane("tiny float");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Floating(tiny, 2) }, tiny, maxGrids: 2);

            Assert.Equal(Kind.FirstDocument, d.Kind);
            Assert.Null(d.Target);
        }

        /// <summary>The first terminal of all is a plain document.</summary>
        [Fact]
        public void The_first_terminal_is_a_plain_document()
        {
            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>>(), null, maxGrids: 2);

            Assert.Equal(Kind.FirstDocument, d.Kind);
        }

        /// <summary>
        /// Equal counts go to the earlier pane, matching the old strict less-than scan, so placement in
        /// the everyday case is unchanged.
        /// </summary>
        [Fact]
        public void A_tie_goes_to_the_earlier_pane()
        {
            var a = new Pane("a");
            var b = new Pane("b");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(a, 2), Docked(b, 2) }, b, maxGrids: 2);

            Assert.Equal(Kind.TabInto, d.Kind);
            Assert.Same(a, d.Target);
        }

        /// <summary>
        /// Placement is decided on terminal counts, not raw pane contents. The count itself is taken in
        /// MainForm (<c>Contents.OfType&lt;TerminalDocument&gt;()</c> filtered on <c>!IsHidden</c>), so
        /// this pins only the half that lives here: a pane holding the Tasks panel plus one terminal is
        /// passed in as 1 and wins over a pane with 2 terminals.
        /// </summary>
        [Fact]
        public void The_decision_uses_the_terminal_count_it_is_given()
        {
            var withTasksPanel = new Pane("terminal + Tasks panel");
            var twoTerminals = new Pane("two terminals");

            var d = TerminalPlacement.Decide(new List<TerminalPlacement.PaneInfo<Pane>> { Docked(twoTerminals, 2), Docked(withTasksPanel, 1) }, null, maxGrids: 2);

            Assert.Same(withTasksPanel, d.Target);
        }
    }
}
