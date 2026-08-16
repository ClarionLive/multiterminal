using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MultiTerminal.Controls;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The <see cref="IZoomableTab"/> contract (task 0d72698a, item 1).
    /// <para>Task 0d72698a exists because HUD tab wiring was a hand-maintained list of types: six of
    /// seven renderers raised ZoomChanged into a subscriber nobody had written, and nothing failed —
    /// the tabs just quietly behaved differently from the Tasks tab. The interface makes the container
    /// side a compile error. These tests cover the half a compiler cannot: a NEW renderer that copies
    /// an existing one (and so has both members) but never declares the interface, and is therefore
    /// silently skipped by the container's loop.</para>
    /// </summary>
    public sealed class ZoomableTabContractTests
    {
        private static readonly Assembly AppAssembly = typeof(IZoomableTab).Assembly;

        /// <summary>Types that look like a zoomable HUD tab: both members, in the HUD controls namespace.</summary>
        private static IEnumerable<Type> TypesThatWalkLikeAZoomableTab()
        {
            return AppAssembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.Namespace == "MultiTerminal.Controls")
                .Where(t =>
                    t.GetEvent("ZoomChanged", BindingFlags.Public | BindingFlags.Instance) != null &&
                    t.GetMethod("SetZoomFactor", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(double) }, null) != null);
        }

        /// <summary>
        /// THE ONE THAT MATTERS. A renderer carrying both members but not the interface is invisible to
        /// <c>HudTabContainer.SetZoomFactor</c>'s loop — it would never zoom and never persist, which is
        /// exactly the original defect wearing new clothes. Copy-pasting a renderer is the likely way in.
        /// </summary>
        [Fact]
        public void Every_control_that_looks_like_a_zoomable_tab_actually_declares_the_interface()
        {
            var offenders = TypesThatWalkLikeAZoomableTab()
                .Where(t => !typeof(IZoomableTab).IsAssignableFrom(t))
                .Select(t => t.Name)
                .OrderBy(n => n)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "These MultiTerminal.Controls types expose ZoomChanged + SetZoomFactor but do not implement " +
                "IZoomableTab, so HudTabContainer's zoom loop will silently skip them: " +
                string.Join(", ", offenders));
        }

        /// <summary>
        /// Guards the reverse direction: the scan above must actually be finding things. If a refactor
        /// moved the renderers to another namespace, the offender query would return an empty set and
        /// the test above would pass vacuously while covering nothing.
        /// </summary>
        [Fact]
        public void The_scan_finds_the_known_hud_tabs()
        {
            var found = TypesThatWalkLikeAZoomableTab().Select(t => t.Name).ToList();

            foreach (var expected in new[]
            {
                "TaskHudRenderer", "HudGraphRenderer", "HudGitRenderer", "HudNotesRenderer",
                "HudKnowledgeRenderer", "HudSessionsRenderer", "HudDashboardRenderer", "BrowserTabPage",
            })
            {
                Assert.Contains(expected, found);
            }
        }

        /// <summary>
        /// Browser tabs were the one HUD tab that could be zoomed but never observed — it had
        /// SetZoomFactor and no ZoomChanged, so the shared __browser__ bucket would have been a setting
        /// nothing ever wrote to.
        /// </summary>
        [Fact]
        public void Browser_tabs_can_now_report_a_zoom_change_not_merely_receive_one()
        {
            Assert.True(typeof(IZoomableTab).IsAssignableFrom(typeof(BrowserTabPage)));
            Assert.NotNull(typeof(BrowserTabPage).GetEvent("ZoomChanged"));
        }

        /// <summary>
        /// REGRESSION — pipeline Run 1, cross-model adversary gate, MEDIUM. The container must not
        /// accept a permanent tab that cannot be zoomed.
        /// </summary>
        /// <remarks>
        /// The finding: the interface was DOCUMENTED as making the omission a compile error — in the
        /// plan's acceptance criteria, in IZoomableTab's own XML docs, in the rules file and in the
        /// commit message — but `AddPermanentTab` took a plain `Control`, and `HookTabZoom` returned
        /// early for a non-zoomable one. A new permanent tab could compile in, render normally, and
        /// silently never persist or report zoom. The interface closed the copy-paste hole (a renderer
        /// with both members that forgets the declaration, covered above) and left the omission hole
        /// wide open, while the documentation claimed both were shut.
        /// The generic constraint is what makes the documented claim true.
        /// </remarks>
        [Fact]
        public void A_permanent_tab_cannot_be_registered_unless_it_is_zoomable()
        {
            var add = typeof(HudTabContainer)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == "AddPermanentTab");

            Assert.True(add.IsGenericMethodDefinition,
                "AddPermanentTab is not generic, so it cannot constrain its control type — a non-zoomable " +
                "tab would compile in and silently skip zoom persistence.");

            var constraints = add.GetGenericArguments().Single().GetGenericParameterConstraints();

            Assert.Contains(typeof(IZoomableTab), constraints);
        }

        /// <summary>
        /// The interface must carry BOTH halves. An interface with only SetZoomFactor would re-create the
        /// original asymmetry — restore reaching every tab while save reached only one — with the
        /// appearance of having been fixed.
        /// </summary>
        [Fact]
        public void The_interface_carries_both_the_apply_and_the_observe_half()
        {
            Assert.NotNull(typeof(IZoomableTab).GetMethod("SetZoomFactor", new[] { typeof(double) }));
            Assert.NotNull(typeof(IZoomableTab).GetEvent("ZoomChanged"));
        }
    }
}
