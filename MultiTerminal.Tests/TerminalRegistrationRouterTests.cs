using System;
using System.Collections.Generic;
using MultiTerminal.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 5e1dea4c (19a26090 Run 5, Codex security): a registration raised on a broker thread is
    /// snapshotted, resolved and committed in ONE UI-thread step, so a pane that ends its launch and
    /// is relaunched while the registration is queued is judged as it is NOW, on every route.
    ///
    /// <para>The queue stands in for <c>Control.Invoke</c>; <see cref="Commit"/> is
    /// <c>MainForm.CommitRegistrationBinding</c>'s writes (routing map, title, identity) reduced to
    /// plain fields. That MainForm passes its real Invoke and its real commit is build + review only.</para>
    ///
    /// <para>Falsified by moving the snapshot and <c>Resolve</c> in
    /// <see cref="TerminalRegistrationRouter.Route{TPane}"/> OUTSIDE <c>runOnUiThread</c> (the
    /// pre-5e1dea4c shape: resolve on the broker thread, commit later). Predicted: the two relaunch
    /// facts and the snapshot-timing fact red, the control fact green; observed exactly that, 3 failed / 1
    /// passed.</para>
    /// </summary>
    public class TerminalRegistrationRouterTests
    {
        private sealed class ModelPane
        {
            public string DocId;
            public PaneLaunchLifecycle Launch = new PaneLaunchLifecycle();
            public string Promoted;
            public string Title;

            public PaneIdentity Identity => new PaneIdentity(DocId, Promoted, Title, Launch.LaunchNonce);

            /// <summary>"Launch as..." / Home + relaunch: the launch ends, then StartTerminal adopts the new name.</summary>
            public void Relaunch(string name)
            {
                Launch.EndLaunch();
                Title = name;
                Promoted = name;
            }
        }

        private readonly Dictionary<string, ModelPane> _terminalDocMap = new Dictionary<string, ModelPane>();
        private readonly Queue<Action> _ui = new Queue<Action>();
        private readonly List<PaneBinding> _resolved = new List<PaneBinding>();

        private void Raise(List<ModelPane> panes, string terminalId, string name, string docId, string nonce) =>
            TerminalRegistrationRouter.Route(
                _ui.Enqueue,
                () => panes,
                p => p.Identity,
                name, docId, nonce,
                (binding, pane) => Commit(binding, pane, terminalId, name));

        private void Commit(PaneBinding binding, ModelPane pane, string terminalId, string name)
        {
            _resolved.Add(binding);
            if (pane == null) return;
            _terminalDocMap[terminalId] = pane;
            pane.Title = name;
            if (binding.Route == PaneBindingRoute.ProvenOwnLaunch || string.IsNullOrEmpty(pane.Promoted))
                pane.Promoted = name;
        }

        private void DrainUi()
        {
            while (_ui.Count > 0) _ui.Dequeue()();
        }

        [Fact]
        public void A_name_matched_registration_queued_across_a_relaunch_touches_nothing()
        {
            // The ordering the ticket asks for: Alice re-registers with no docId (name route), the
            // registration is queued for the UI thread, and before it runs the pane ends Alice's
            // launch and is relaunched as Bob with a rotated nonce.
            var pane = new ModelPane { DocId = "aaaa1111", Promoted = "Alice", Title = "Alice" };
            var panes = new List<ModelPane> { pane };

            Raise(panes, "t-alice", "Alice", null, null);
            pane.Relaunch("Bob");
            DrainUi();

            Assert.Empty(_terminalDocMap);
            Assert.Equal("Bob", pane.Title);
            Assert.Equal("Bob", pane.Promoted);
            Assert.Equal(PaneBindingRoute.None, Assert.Single(_resolved).Route);
        }

        [Fact]
        public void A_proven_registration_queued_across_a_relaunch_is_no_longer_proof()
        {
            // Proven against launch A (docId + A's nonce); the pane rotates and runs Bob before the
            // queued registration is judged. Resolved now, A's nonce proves nothing, the 1:1 guard
            // sees Bob, and nothing is written. This is what made 19a26090's proven-only UI-thread
            // re-check redundant, which is why MainForm no longer has it.
            var pane = new ModelPane { DocId = "aaaa1111", Promoted = "Alice", Title = "Alice" };
            var panes = new List<ModelPane> { pane };
            string nonceA = pane.Launch.LaunchNonce;

            Raise(panes, "t-alice", "Alice", "aaaa1111", nonceA);
            pane.Relaunch("Bob");
            DrainUi();

            Assert.Empty(_terminalDocMap);
            Assert.Equal("Bob", pane.Title);
            Assert.Equal("Bob", pane.Promoted);
            Assert.Equal(PaneBindingRoute.CollisionUnbound, Assert.Single(_resolved).Route);
        }

        [Fact]
        public void Control_without_a_relaunch_the_same_registrations_do_commit()
        {
            // Guards the two facts above against passing because the model never writes anything.
            var pane = new ModelPane { DocId = "aaaa1111", Promoted = "Alice", Title = "Alice" };
            var panes = new List<ModelPane> { pane };

            Raise(panes, "t-name", "Alice", null, null);
            Raise(panes, "t-proven", "Alice", "aaaa1111", pane.Launch.LaunchNonce);
            DrainUi();

            Assert.Same(pane, _terminalDocMap["t-name"]);
            Assert.Same(pane, _terminalDocMap["t-proven"]);
            Assert.Equal(new[] { PaneBindingRoute.Matched, PaneBindingRoute.ProvenOwnLaunch },
                _resolved.ConvertAll(b => b.Route));
        }

        [Fact]
        public void Nothing_is_read_or_decided_before_the_UI_thread_runs_the_step()
        {
            // The structural half: the pane list is read only inside the UI-thread step. A router
            // that snapshots on the calling (broker) thread reads it at Raise time.
            int reads = 0;
            var pane = new ModelPane { DocId = "aaaa1111", Promoted = "Alice", Title = "Alice" };

            TerminalRegistrationRouter.Route(
                _ui.Enqueue,
                () => { reads++; return new List<ModelPane> { pane }; },
                p => p.Identity,
                "Alice", null, null,
                (binding, p) => _resolved.Add(binding));

            Assert.Equal(0, reads);
            Assert.Empty(_resolved);

            DrainUi();

            Assert.Equal(1, reads);
            Assert.Single(_resolved);
        }
    }
}
