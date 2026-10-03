using System;
using System.Collections.Generic;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Runs a broker registration's snapshot, <see cref="TerminalRegistrationBinder.Resolve"/> and
    /// commit as ONE step on the UI thread (task 5e1dea4c).
    ///
    /// <para><b>Why one step.</b> The registration event is raised on a broker thread. Resolving there
    /// and committing after the marshal left a window in which the pane could end its launch and be
    /// relaunched as someone else: the queued commit then wrote the old registration's terminal id,
    /// agent name, title and identity onto the new launch. 19a26090 re-checked only the
    /// <see cref="PaneBindingRoute.ProvenOwnLaunch"/> route; Matched / CollisionReResolved /
    /// NonceDenied were committed unchecked (19a26090 Run 5, Codex security). A pane's nonce,
    /// title and identity change only on the UI thread, so a decision read and acted on inside a
    /// single UI-thread callback cannot be overtaken, whatever the route.</para>
    ///
    /// <para>Chosen over capturing a per-pane launch generation and re-checking it before the commit:
    /// that needs a generation per route and a re-resolve when it moved, which is the same resolve
    /// on the UI thread with more ways to get it wrong. Resolving here also stops the snapshot
    /// reading WinForms controls' properties from a background thread.</para>
    ///
    /// <para>Generic and free of WinForms so the ordering is testable: MainForm passes
    /// <c>Control.Invoke</c> as <paramref name="runOnUiThread"/>, a test passes a queue.</para>
    /// </summary>
    internal static class TerminalRegistrationRouter
    {
        /// <param name="runOnUiThread">Runs the action on the thread that owns the panes. May defer it.</param>
        /// <param name="livePanes">Reads the panes as they are NOW. Called only inside the UI-thread step.</param>
        /// <param name="identityOf">What the binder sees of a pane.</param>
        /// <param name="onResolved">
        /// Called inside the same UI-thread step with the binding and its pane (null when nothing
        /// bound). Every write a binding causes belongs here, and nowhere that runs later.
        /// </param>
        internal static void Route<TPane>(
            Action<Action> runOnUiThread,
            Func<IReadOnlyList<TPane>> livePanes,
            Func<TPane, PaneIdentity> identityOf,
            string name,
            string docId,
            string launchNonce,
            Action<PaneBinding, TPane> onResolved)
            where TPane : class
        {
            runOnUiThread(() =>
            {
                var panes = livePanes() ?? Array.Empty<TPane>();
                var identities = new PaneIdentity[panes.Count];
                for (int i = 0; i < panes.Count; i++)
                    identities[i] = identityOf(panes[i]);

                var binding = TerminalRegistrationBinder.Resolve(identities, name, docId, launchNonce);
                onResolved(binding, binding.Index >= 0 ? panes[binding.Index] : null);
            });
        }
    }
}
