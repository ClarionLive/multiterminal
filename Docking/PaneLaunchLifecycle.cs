using System;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// The launch-lifecycle rules of one terminal pane (task 5e1dea4c), pulled out of
    /// <see cref="TerminalDocument"/> and <c>TerminalControl</c> so they can be tested: both build
    /// WebView2 renderers, so neither can be constructed in a unit test.
    ///
    /// <para><b>The launch nonce.</b> Minted per pane and replaced whenever a launch ENDS (task
    /// 19a26090), so it proves the current launch rather than any launch the pane has hosted. MT
    /// injects it into the pane's child and seeds it on the broker placeholder; a registration that
    /// echoes it with the pane's docId is that pane's own process (<see cref="TerminalRegistrationBinder"/>).
    /// Full 32-hex GUID: it must be unguessable, not just unique.</para>
    ///
    /// <para><b>Exit acceptance.</b> A process exit is marshalled to the UI thread with BeginInvoke,
    /// and stopping a process only unsubscribes FUTURE events. So an old process's exit can arrive
    /// after a relaunch has started its replacement; only the exit of the current process instance
    /// ends the pane's session.</para>
    /// </summary>
    internal sealed class PaneLaunchLifecycle
    {
        // Volatile: written on the UI thread, read by the registration path and the broker seed.
        private volatile string _launchNonce = NewNonce();

        /// <summary>The nonce of the launch now running (or about to run) in this pane. Never empty.</summary>
        internal string LaunchNonce => _launchNonce;

        /// <summary>
        /// Retires the current launch's nonce and mints a new one. Call when a launch ends: the child
        /// exited, the pane went home, or "Launch as..." is about to replace it. Never between
        /// pre-registration and start, which must carry the same value.
        /// </summary>
        internal void EndLaunch() => _launchNonce = NewNonce();

        /// <summary>
        /// Whether an exit raised by <paramref name="sender"/> ends the session of the process the
        /// pane currently runs (<paramref name="currentProcess"/>, null once stopped). An exit from any
        /// other instance is a stale, queued one and must be dropped: delivering it would send the
        /// new session home and rotate its nonce away from the one its broker row and child hold.
        /// A null sender never qualifies, even when nothing is running.
        /// </summary>
        internal static bool AcceptsExit(object sender, object currentProcess) =>
            sender != null && ReferenceEquals(sender, currentProcess);

        private static string NewNonce() => Guid.NewGuid().ToString("N");
    }
}
