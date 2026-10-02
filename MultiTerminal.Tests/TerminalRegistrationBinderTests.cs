using System.Collections.Generic;
using MultiTerminal.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 19a26090: which pane a broker registration binds to.
    ///
    /// <para><see cref="The_2026_10_01_restore_replay_binds_every_agent_to_the_pane_it_launched_in"/>
    /// is the incident, step for step, with the docIds from that day's log. Falsified by removing the
    /// proven-origin return in <see cref="TerminalRegistrationBinder.Resolve"/> (predicted beforehand:
    /// this fact and the reused-pane fact red, the other 12 green; observed exactly that). The replay
    /// failed at the first launch with route <c>CollisionReResolved</c>: the guard re-resolving Alice
    /// away from her own pane, which is the production cross.</para>
    /// </summary>
    public class TerminalRegistrationBinderTests
    {
        private static PaneIdentity Pane(string docId, string promoted, string title, string nonce) =>
            new PaneIdentity(docId, promoted, title, title, nonce);

        /// <summary>What MainForm does to a pane after a binding, so a replay can continue.</summary>
        private static void Apply(List<PaneIdentity> panes, PaneBinding binding, string name)
        {
            var p = panes[binding.Index];
            string promoted = binding.Route == PaneBindingRoute.ProvenOwnLaunch || string.IsNullOrEmpty(p.PromotedName)
                ? name
                : p.PromotedName;
            panes[binding.Index] = p with { PromotedName = promoted, CustomTitle = name, TabText = name };
        }

        [Fact]
        public void The_2026_10_01_restore_replay_binds_every_agent_to_the_pane_it_launched_in()
        {
            // Restored from layout with yesterday's titles, nothing promoted yet.
            var panes = new List<PaneIdentity>
            {
                Pane("66371416", null, "Charlie", "nonce-0"),
                Pane("15fbd989", null, "Alice", "nonce-1"),
                Pane("5cbc12d2", null, "Grace", "nonce-2"),
            };

            var launches = new[]
            {
                (Name: "Alice", Pane: 0),
                (Name: "Grace", Pane: 1),
                (Name: "Charlie", Pane: 2),
            };

            foreach (var launch in launches)
            {
                var binding = TerminalRegistrationBinder.Resolve(
                    panes, launch.Name, panes[launch.Pane].DocId, panes[launch.Pane].LaunchNonce);

                Assert.Equal(PaneBindingRoute.ProvenOwnLaunch, binding.Route);
                Assert.Equal(launch.Pane, binding.Index);
                Apply(panes, binding, launch.Name);
            }

            Assert.Equal(new[] { "Alice", "Grace", "Charlie" },
                new[] { panes[0].PromotedName, panes[1].PromotedName, panes[2].PromotedName });
        }

        [Fact]
        public void A_proven_registration_replaces_the_identity_of_a_reused_pane()
        {
            // Alice ran here, exited, and Bob was launched into the same pane.
            var panes = new List<PaneIdentity> { Pane("aaaa1111", "Alice", "Alice", "nonce-a") };

            var binding = TerminalRegistrationBinder.Resolve(panes, "Bob", "aaaa1111", "nonce-a");

            Assert.Equal(PaneBindingRoute.ProvenOwnLaunch, binding.Route);
            Assert.Equal(0, binding.Index);
            Assert.Equal("Alice", binding.BoundIdentity);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nonce-guessed")]
        public void A_docId_without_the_pane_nonce_cannot_claim_a_bound_pane(string presentedNonce)
        {
            // The inherited-env case the 1:1 guard exists for (ab50355f): something that learned the
            // docId but not the nonce. It must not bind to Alice's pane.
            var panes = new List<PaneIdentity> { Pane("aaaa1111", "Alice", "Alice", "nonce-a") };

            var binding = TerminalRegistrationBinder.Resolve(panes, "Intruder", "aaaa1111", presentedNonce);

            Assert.Equal(-1, binding.Index);
            Assert.Equal(PaneBindingRoute.CollisionUnbound, binding.Route);
        }

        [Fact]
        public void Two_empty_nonces_are_not_proof()
        {
            // A pane with no nonce (defensive; real panes always mint one) plus a registration with
            // none must not count as "matching": equal emptiness proves nothing.
            var panes = new List<PaneIdentity> { Pane("aaaa1111", "Alice", "Alice", "") };

            var binding = TerminalRegistrationBinder.Resolve(panes, "Intruder", "aaaa1111", "");

            Assert.NotEqual(PaneBindingRoute.ProvenOwnLaunch, binding.Route);
            Assert.Equal(-1, binding.Index);
        }

        [Fact]
        public void An_unproven_registration_still_cannot_adopt_an_unclaimed_placeholder()
        {
            var panes = new List<PaneIdentity> { Pane("aaaa1111", null, "Unassigned", "nonce-a") };

            var binding = TerminalRegistrationBinder.Resolve(panes, "Bob", "aaaa1111", "wrong");

            Assert.Equal(PaneBindingRoute.NonceDenied, binding.Route);
            Assert.Equal(-1, binding.Index);
        }

        [Fact]
        public void Without_a_docId_the_name_match_still_binds()
        {
            var panes = new List<PaneIdentity>
            {
                Pane("aaaa1111", "Alice", "Alice", "nonce-a"),
                Pane("bbbb2222", "Bob", "Bob", "nonce-b"),
            };

            var binding = TerminalRegistrationBinder.Resolve(panes, "bob", null, null);

            Assert.Equal(PaneBindingRoute.Matched, binding.Route);
            Assert.Equal(1, binding.Index);
        }

        [Fact]
        public void An_unproven_collision_still_re_resolves_to_the_registrants_own_pane()
        {
            // Pre-19a26090 behaviour, kept: a registration carrying the wrong pane's docId and no
            // proof goes to the pane already promoted under its own name.
            var panes = new List<PaneIdentity>
            {
                Pane("aaaa1111", "Alice", "Alice", "nonce-a"),
                Pane("bbbb2222", "Bob", "Bob", "nonce-b"),
            };

            var binding = TerminalRegistrationBinder.Resolve(panes, "Bob", "aaaa1111", null);

            Assert.Equal(PaneBindingRoute.CollisionReResolved, binding.Route);
            Assert.Equal(1, binding.Index);
        }
    }
}
