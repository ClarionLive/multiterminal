#nullable enable
using System;
using System.Collections.Generic;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Decides where <c>MainForm.AddNewTerminal</c> puts a new terminal (task 7f91349f). Pure: the
    /// caller describes the panes, this picks one, and MainForm turns the answer into a
    /// <c>Show(...)</c> call. Generic over the pane type so tests need no DockPanel.
    ///
    /// <para><b>There is no "float" answer, on purpose.</b> The previous inline logic floated the
    /// terminal once every pane held <c>MaxTabsPerGrid</c> contents, and a float with no size set
    /// opens at DockPanel's tiny default. With helpers spawned in bursts that happened after about
    /// five terminals, and the Owner re-docked each one by hand. Once the grid slots are used up, the
    /// terminal now joins the emptiest docked pane however full it is (Owner decision).</para>
    ///
    /// <para><b>That makes <c>MaxTabsPerGrid</c> irrelevant to placement,</b> which is why it is not a
    /// parameter. The old rule was "the emptiest pane under the limit, else float"; the emptiest pane
    /// is under the limit whenever any pane is, so the limit only ever chose the moment to float.</para>
    /// </summary>
    public static class TerminalPlacement
    {
        /// <summary>How the new terminal is shown.</summary>
        public enum Kind
        {
            /// <summary>No docked pane holds a terminal: show it as a plain document.</summary>
            FirstDocument,

            /// <summary>Split <see cref="Decision{TPane}.Target"/> and put the terminal on the right.</summary>
            SplitFrom,

            /// <summary>Add the terminal as a tab in <see cref="Decision{TPane}.Target"/>.</summary>
            TabInto,
        }

        /// <summary>One pane that holds at least one terminal, as the caller sees it.</summary>
        /// <param name="Pane">The pane itself; compared by reference.</param>
        /// <param name="IsDocked">True for a pane in the main document area; false for a floating
        /// window or a tool-window edge. Only docked panes are grid slots, split sources or tab
        /// targets. A floating window used to count as a grid pane, so later terminals went in as tabs
        /// inside the tiny window.</param>
        /// <param name="TerminalCount">Visible terminals in the pane. Not all contents: counting the
        /// Tasks panel or a hidden document made a pane look full before it held MaxTabsPerGrid
        /// terminals.</param>
        public readonly record struct PaneInfo<TPane>(TPane Pane, bool IsDocked, int TerminalCount)
            where TPane : class;

        /// <summary>The placement: a <see cref="Kind"/> and, except for FirstDocument, its pane.</summary>
        public readonly record struct Decision<TPane>(Kind Kind, TPane? Target)
            where TPane : class;

        /// <summary>
        /// Picks where the next terminal goes. Never floats.
        /// </summary>
        /// <param name="panes">Every pane that currently holds a terminal, docked or not, in dock
        /// order. The last docked one is the split source when the active pane cannot be used.</param>
        /// <param name="activePane">The active terminal's pane, or null. Used as the split source only
        /// when it is one of the docked panes; splitting from a floating pane put the new terminal
        /// inside the floating window.</param>
        /// <param name="maxGrids">MaxGridPanes: how many docked panes to split into before tabbing.</param>
        public static Decision<TPane> Decide<TPane>(
            IReadOnlyList<PaneInfo<TPane>> panes, TPane? activePane, int maxGrids)
            where TPane : class
        {
            ArgumentNullException.ThrowIfNull(panes);

            var docked = new List<PaneInfo<TPane>>();
            foreach (var p in panes)
            {
                if (p.IsDocked)
                {
                    docked.Add(p);
                }
            }

            if (docked.Count == 0)
            {
                return new Decision<TPane>(Kind.FirstDocument, null);
            }

            if (docked.Count < maxGrids)
            {
                TPane splitFrom = docked[docked.Count - 1].Pane;
                if (activePane != null)
                {
                    foreach (var p in docked)
                    {
                        if (ReferenceEquals(p.Pane, activePane))
                        {
                            splitFrom = activePane;
                            break;
                        }
                    }
                }

                return new Decision<TPane>(Kind.SplitFrom, splitFrom);
            }

            // Grid slots are used up: the emptiest docked pane takes the terminal. Ties go to the
            // earlier pane, as before.
            PaneInfo<TPane> best = docked[0];
            for (int i = 1; i < docked.Count; i++)
            {
                if (docked[i].TerminalCount < best.TerminalCount)
                {
                    best = docked[i];
                }
            }

            return new Decision<TPane>(Kind.TabInto, best.Pane);
        }
    }
}
