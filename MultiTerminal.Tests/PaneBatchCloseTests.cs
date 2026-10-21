using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 7d20e94c item 8 review fix: Close All Tabs / Close all but this go through
    /// <see cref="PaneBatchClose"/>. Pins: a PM is closed before its helpers whatever the tab order; a
    /// cancelled close stops the batch (its helpers and everything after it stay open); helpers handled by
    /// the PM's answer (Yes or No) are not closed again; a batch with no helpers keeps tab order.
    /// </summary>
    public sealed class PaneBatchCloseTests
    {
        private static Func<string, IEnumerable<string>> Helpers(params (string Pm, string[] Helpers)[] map)
        {
            var d = map.ToDictionary(m => m.Pm, m => m.Helpers, StringComparer.Ordinal);
            return id => d.TryGetValue(id, out var h) ? h : null;
        }

        /// <summary>Drives the plan like MainForm: closes each Next(), applying the scripted outcome.</summary>
        private static List<string> Run(PaneBatchClose plan, Func<string, (bool Cancelled, string[] Handled)> outcome)
        {
            var closed = new List<string>();
            for (var id = plan.Next(); id != null; id = plan.Next())
            {
                var (cancelled, handled) = outcome(id);
                if (cancelled)
                {
                    plan.ReportCancelled();
                    break;
                }

                closed.Add(id);
                plan.ReportHelpersHandled(handled);
            }

            return closed;
        }

        [Fact]
        public void A_helper_whose_tab_comes_before_its_PM_is_ordered_after_it()
        {
            // Tab order: H1, X, PM, H2. Both helpers belong to PM.
            var plan = new PaneBatchClose(new[] { "H1", "X", "PM", "H2" }, Helpers(("PM", new[] { "H1", "H2" })));

            Assert.Equal(new[] { "X", "PM", "H1", "H2" }, plan.Order);
        }

        [Fact]
        public void Without_helpers_the_order_is_tab_order()
        {
            var plan = new PaneBatchClose(new[] { "C", "A", "B" }, _ => null);

            Assert.Equal(new[] { "C", "A", "B" }, plan.Order);
        }

        [Fact]
        public void Cancel_on_the_PM_stops_the_batch_and_leaves_its_helpers_open()
        {
            var plan = new PaneBatchClose(new[] { "H1", "PM", "Y" }, Helpers(("PM", new[] { "H1" })));

            var closed = Run(plan, id => (id == "PM", Array.Empty<string>()));

            Assert.Empty(closed);
            Assert.True(plan.Stopped);
            Assert.Null(plan.Next());
        }

        [Fact]
        public void Helpers_closed_with_their_PM_are_not_closed_again()
        {
            var plan = new PaneBatchClose(new[] { "H1", "PM", "H2", "Z" }, Helpers(("PM", new[] { "H1", "H2" })));

            // Yes: H1 and H2 were closed by the PM's own close.
            var closed = Run(plan, id => (false, id == "PM" ? new[] { "H1", "H2" } : Array.Empty<string>()));

            Assert.Equal(new[] { "PM", "Z" }, closed);
        }

        [Fact]
        public void No_keeps_the_PMs_helpers_out_of_the_rest_of_the_batch()
        {
            // No: only PM closes; its helpers keep running, so the batch must not close them.
            var plan = new PaneBatchClose(new[] { "PM", "H1", "Q" }, Helpers(("PM", new[] { "H1" })));

            var closed = Run(plan, id => (false, id == "PM" ? new[] { "H1" } : Array.Empty<string>()));

            Assert.Equal(new[] { "PM", "Q" }, closed);
        }

        [Fact]
        public void A_helper_outside_the_batch_does_not_affect_the_order_and_duplicates_are_dropped()
        {
            var plan = new PaneBatchClose(new[] { "B", "PM", "B", "" }, Helpers(("PM", new[] { "OUTSIDE" })));

            Assert.Equal(new[] { "B", "PM" }, plan.Order);
        }
    }
}
