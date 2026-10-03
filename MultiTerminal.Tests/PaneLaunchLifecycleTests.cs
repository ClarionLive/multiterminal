using System;
using System.Collections.Generic;
using MultiTerminal.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 5e1dea4c: the pane launch lifecycle that <see cref="TerminalDocument"/> (nonce rotation)
    /// and <c>TerminalControl</c> (exit acceptance) delegate to. Both build WebView2 renderers, so the
    /// orderings are replayed here against the same rules with a queue standing in for BeginInvoke.
    ///
    /// <para>The ordering facts (suffix <c>_model</c>) exercise <see cref="PaneLaunchLifecycle.AcceptsExit"/>
    /// and <see cref="PaneLaunchLifecycle.EndLaunch"/> for real; the Stop / EndLaunch / Start sequence
    /// around them is this file's <c>ModelPane</c>, written to mirror TerminalControl and
    /// TerminalDocument. They do NOT cover the real wiring: that TerminalDocument calls
    /// <c>EndLaunch</c> at every launch end (process exit, Home, "Launch as..."), that TerminalControl
    /// passes its live ConPtyTerminal, or that the real exit is marshalled the way the queue here is.
    /// Those call sites remain build + review only.</para>
    /// </summary>
    public class PaneLaunchLifecycleTests
    {
        [Fact]
        public void Ending_a_launch_mints_a_fresh_32_hex_nonce()
        {
            var launch = new PaneLaunchLifecycle();
            string first = launch.LaunchNonce;

            launch.EndLaunch();

            Assert.NotEqual(first, launch.LaunchNonce);
            Assert.Matches("^[0-9a-f]{32}$", first);
            Assert.Matches("^[0-9a-f]{32}$", launch.LaunchNonce);
        }

        [Fact]
        public void Only_the_current_process_exit_is_accepted()
        {
            object current = new object();
            object previous = new object();

            Assert.True(PaneLaunchLifecycle.AcceptsExit(current, current));
            Assert.False(PaneLaunchLifecycle.AcceptsExit(previous, current));
            Assert.False(PaneLaunchLifecycle.AcceptsExit(previous, null));   // stopped: nothing is current
            Assert.False(PaneLaunchLifecycle.AcceptsExit(null, null));       // a null sender proves nothing
        }

        /// <summary>
        /// MODEL: the ordering 19a26090 Run 2 found, replayed on <c>ModelPane</c>: launch A's process exits, its exit is queued
        /// (BeginInvoke), and before the queue drains "Launch as..." stops A, rotates the nonce and
        /// starts B in one UI-thread pass. The queued exit must not send B home or rotate B's nonce
        /// away from the one B's broker row and child already hold.
        ///
        /// <para>Falsified by making <see cref="PaneLaunchLifecycle.AcceptsExit"/> return
        /// <c>sender != null</c> (any exit accepted): this fact and the stopped-pane fact below went
        /// red, as predicted; the Only_the_current fact also went red on its "previous" lines.</para>
        /// </summary>
        [Fact]
        public void A_queued_exit_from_the_previous_launch_does_not_end_the_relaunched_session_model()
        {
            var pane = new ModelPane();
            var ui = new Queue<Action>();

            object processA = pane.Start();
            ui.Enqueue(() => pane.OnProcessExited(processA));   // A exits; marshalled, not yet run

            // "Launch as..." on the UI thread, ahead of the queued exit.
            pane.Stop();
            pane.Launch.EndLaunch();
            string nonceB = pane.Launch.LaunchNonce;            // pre-registered with this
            object processB = pane.Start();

            while (ui.Count > 0) ui.Dequeue()();

            Assert.False(pane.WentHome);
            Assert.Equal(nonceB, pane.Launch.LaunchNonce);
            Assert.Same(processB, pane.Current);

            // Control: B's own exit IS accepted, so the facts above are not passing because the
            // model never accepts anything.
            pane.OnProcessExited(processB);
            Assert.True(pane.WentHome);
            Assert.NotEqual(nonceB, pane.Launch.LaunchNonce);
        }

        [Fact]
        public void A_queued_exit_after_Home_does_not_rotate_the_nonce_a_second_time_model()
        {
            // MODEL. Home stops the process and rotates the nonce itself. The stopped process's exit, if
            // already queued, arrives with nothing current and must be dropped.
            var pane = new ModelPane();
            var ui = new Queue<Action>();

            object processA = pane.Start();
            ui.Enqueue(() => pane.OnProcessExited(processA));

            pane.Stop();
            pane.Launch.EndLaunch();
            string afterHome = pane.Launch.LaunchNonce;

            while (ui.Count > 0) ui.Dequeue()();

            Assert.Equal(afterHome, pane.Launch.LaunchNonce);
            Assert.False(pane.WentHome);
        }

        /// <summary>
        /// TerminalControl + TerminalDocument reduced to the lifecycle calls they make. Written by
        /// hand to mirror them; nothing checks that it still does.
        /// </summary>
        private sealed class ModelPane
        {
            public PaneLaunchLifecycle Launch { get; } = new PaneLaunchLifecycle();
            public object Current { get; private set; }
            public bool WentHome { get; private set; }

            public object Start() => Current = new object();

            public void Stop() => Current = null;

            // TerminalControl.OnTerminalProcessExited, then TerminalDocument.OnTerminalProcessExited.
            public void OnProcessExited(object sender)
            {
                if (!PaneLaunchLifecycle.AcceptsExit(sender, Current)) return;
                Launch.EndLaunch();
                WentHome = true;
            }
        }
    }
}
