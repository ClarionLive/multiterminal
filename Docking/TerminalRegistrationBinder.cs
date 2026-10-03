using System;
using System.Collections.Generic;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// What a terminal pane looks like to the registration binder: the fields
    /// <c>MainForm.OnMcpTerminalRegistered</c> used to read straight off each
    /// <see cref="TerminalDocument"/>. A plain value so the decision is testable without WinForms.
    ///
    /// <para>Deliberately no TabText: that is display text, and since GH #26 a placeholder in project
    /// "Proj" displays just "Proj", so an agent named "Proj" would match it (158d60ac). Leaving the
    /// field out means no binder change can match on it.</para>
    /// </summary>
    internal readonly record struct PaneIdentity(
        string DocId,
        string PromotedName,
        string CustomTitle,
        string LaunchNonce);

    /// <summary>How <see cref="TerminalRegistrationBinder.Resolve"/> reached its answer, for logging.</summary>
    internal enum PaneBindingRoute
    {
        /// <summary>No pane matched. Channel and inbox delivery still work by terminal id and name.</summary>
        None,

        /// <summary>
        /// Matched by docId AND the registration echoed that pane's launch nonce: the registrant is
        /// the pane's own child process. Authoritative, so its name becomes the pane's identity.
        /// </summary>
        ProvenOwnLaunch,

        /// <summary>Matched by docId or by displayed title, and nothing objected.</summary>
        Matched,

        /// <summary>The first match was bound to a different identity; re-resolved to another pane.</summary>
        CollisionReResolved,

        /// <summary>The first match was bound to a different identity and no other pane fitted.</summary>
        CollisionUnbound,

        /// <summary>The match was an unclaimed placeholder and the registration did not prove origin.</summary>
        NonceDenied,
    }

    internal readonly record struct PaneBinding(int Index, PaneBindingRoute Route, string BoundIdentity);

    /// <summary>
    /// Decides which terminal pane a broker registration belongs to (task 19a26090).
    ///
    /// <para><b>The defect this exists to remove.</b> On 2026-10-01 MT restored three panes from
    /// layout still titled with the previous day's agents: Charlie, Alice, Grace. Alice was then
    /// launched into pane 1. Her pre-registration matched pane 1 exactly, by docId, carrying pane 1's
    /// own launch nonce. But pane 1's restored title read "Charlie", so the 1:1 guard treated the pane
    /// as bound to someone else, re-resolved "a pane titled Alice" and permanently promoted
    /// <b>pane 2</b> to Alice. Grace, launched into pane 2, then stole pane 3 the same way, and Charlie
    /// in pane 3 bound nowhere. Each header showed the previous pane's agent: its folder, project,
    /// cost and context.</para>
    ///
    /// <para>A restored title is persisted UI state, not identity. The launch nonce is identity: MT
    /// mints it per pane, injects it only into that pane's child, and keeps it out of every API
    /// response. So a registration that matches a pane by docId and echoes that pane's nonce is
    /// that pane's own process, and nothing a title says may redirect it.</para>
    ///
    /// <para>Every other path is the pre-19a26090 logic, unchanged, so foreign registrations (an
    /// inherited docId without the nonce) are refused exactly as before.</para>
    /// </summary>
    internal static class TerminalRegistrationBinder
    {
        internal const string UnassignedPlaceholder = "Unassigned";

        internal static bool IsUnassigned(string name) =>
            name != null && name.Equals(UnassignedPlaceholder, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Case-insensitive match of a pane by its CustomTitle. Never TabText (158d60ac). The name
        /// fallback in <see cref="Resolve"/> tries PromotedName before this.
        /// </summary>
        internal static bool MatchesByName(PaneIdentity pane, string name) =>
            pane.CustomTitle?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false;

        /// <summary>
        /// The identity a pane is treated as bound to: its promoted name, else its CustomTitle
        /// (the pre-promotion window, ab50355f). Placeholders and empty titles count as unbound.
        /// </summary>
        internal static string BoundIdentityOf(PaneIdentity pane)
        {
            if (!string.IsNullOrEmpty(pane.PromotedName)) return pane.PromotedName;
            if (!string.IsNullOrEmpty(pane.CustomTitle) && !IsUnassigned(pane.CustomTitle)) return pane.CustomTitle;
            return null;
        }

        /// <summary>
        /// Returns the index into <paramref name="panes"/> this registration binds to, or -1.
        /// </summary>
        internal static PaneBinding Resolve(IReadOnlyList<PaneIdentity> panes, string name, string docId, string launchNonce)
        {
            if (panes == null) return new PaneBinding(-1, PaneBindingRoute.None, null);

            int target = -1;
            if (!string.IsNullOrEmpty(docId))
            {
                // Ordinal, as before: docIds are machine-minted, never typed.
                target = IndexOf(panes, p => p.DocId == docId);

                // Proof of origin wins over anything a title says (task 19a26090). Both sides must be
                // non-empty: an empty nonce proves nothing, and treating two empties as equal would let
                // any nonce-less registration that knows a docId claim the pane.
                if (target >= 0
                    && !string.IsNullOrEmpty(panes[target].LaunchNonce)
                    && string.Equals(launchNonce, panes[target].LaunchNonce, StringComparison.Ordinal))
                {
                    return new PaneBinding(target, PaneBindingRoute.ProvenOwnLaunch, BoundIdentityOf(panes[target]));
                }
            }

            // Fall back to name match (re-registration where Claude passes a wrong docId but
            // pre-registration already set the title). Identity first, then title, exactly as
            // TerminalDocument.FindByIdentity resolves a name (task 5e1dea4c): a promoted pane the
            // Owner cosmetically renamed still answers to its agent name. Never TabText (158d60ac).
            if (target < 0 && !string.IsNullOrEmpty(name))
            {
                target = IndexOf(panes, p => p.PromotedName?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false);
                if (target < 0)
                    target = IndexOf(panes, p => MatchesByName(p, name));
            }

            if (target < 0) return new PaneBinding(-1, PaneBindingRoute.None, null);

            var route = PaneBindingRoute.Matched;

            // 1:1 binding guard: a pane already bound to a DIFFERENT identity belongs to another
            // terminal. Re-resolve to this registrant's own pane: identity first, then an unclaimed
            // pane by title.
            string bound = BoundIdentityOf(panes[target]);
            if (!string.IsNullOrEmpty(name) && bound != null && !bound.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                int reResolved = IndexOf(panes, p => p.PromotedName?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false);
                if (reResolved < 0)
                    reResolved = IndexOf(panes, p => string.IsNullOrEmpty(p.PromotedName) && MatchesByName(p, name));

                if (reResolved < 0) return new PaneBinding(-1, PaneBindingRoute.CollisionUnbound, bound);
                target = reResolved;
                route = PaneBindingRoute.CollisionReResolved;
            }

            // Proof-of-origin gate (fd3437e6): an unclaimed placeholder is only adopted by a
            // registration that echoes its nonce. No unclaimed-by-title fallback here, deliberately.
            var pane = panes[target];
            if (!string.IsNullOrEmpty(pane.LaunchNonce))
            {
                bool unclaimed = string.IsNullOrEmpty(pane.PromotedName)
                    && (string.IsNullOrEmpty(pane.CustomTitle) || IsUnassigned(pane.CustomTitle));
                if (unclaimed && !string.Equals(launchNonce, pane.LaunchNonce, StringComparison.Ordinal))
                {
                    int owned = IndexOf(panes, p => p.PromotedName?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false);
                    return new PaneBinding(owned, PaneBindingRoute.NonceDenied, bound);
                }
            }

            return new PaneBinding(target, route, bound);
        }

        private static int IndexOf(IReadOnlyList<PaneIdentity> panes, Func<PaneIdentity, bool> predicate)
        {
            for (int i = 0; i < panes.Count; i++)
                if (predicate(panes[i])) return i;
            return -1;
        }
    }

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
