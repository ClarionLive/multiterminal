using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Decides whether a terminal-registration event means "this spawned helper is now alive and can be
    /// handed its job" (task 7806024f).
    ///
    /// <para><b>The defect this exists for.</b> A spawned helper's <c>initialPrompt</c> was delivered on
    /// the helper's first <c>ask_user_question</c> notification, with a 120s timer as the fallback. That
    /// trigger was chosen deliberately: a fixed delay is a guess at a 10–30s variable, and session 9 of
    /// b42b1883 watched exactly that guess fail. But it was designed around the <c>/session-start</c> menu,
    /// and a SPAWNED helper never shows one — the plugin's SessionStart hook short-circuits on
    /// <c>MULTITERMINAL_SPAWNER</c> and hands it a spawned-agent briefing instead. So the question never
    /// arrives, the trigger never fires, and delivery ALWAYS falls through to the timer. Measured end to
    /// end: spawn 09:05:18 → alive with channel port 8803 at 09:05:23 → job delivered 09:07:18. One
    /// hundred and fifteen seconds of a booted, idle agent.</para>
    ///
    /// <para><b>Why the channel port and not <c>TURN_END</c>.</b> "The helper finished its startup turn"
    /// is the semantically perfect signal and is deliberately NOT used, for a reason that does not depend
    /// on any contested detail: <c>TURN_END</c> is written by a HOOK, shipped from the plugin repo on its
    /// own release cadence. This trigger decides when an agent's instructions are typed into a live pane —
    /// resting that on a signal another repository can stop emitting is how the present defect happened,
    /// one layer up. The channel port comes from MultiTerminal's OWN registration path, is already the
    /// 120s fallback's liveness predicate, and was measured arriving five seconds after spawn.</para>
    ///
    /// <para>⚠️ A narrower argument against <c>TURN_END</c> — that <c>AskUserQuestion</c> emits no hook
    /// event at all — is CONTESTED between two in-repo sources and is deliberately not relied on; see
    /// task 7806024f for the full contradiction. The decision above stands without settling it.</para>
    ///
    /// <para><b>Pure by construction.</b> No broker, no UI, no clock. <c>MainForm</c> is an 8K+ LOC
    /// WinForms file whose logic the suite can otherwise only reach by scanning source text
    /// (<c>AgentActivityObservationTests.ReadMainFormStripped</c>), so the rule that decides when an
    /// agent's instructions get typed lives here instead, where a test can call it. Same precedent as
    /// <see cref="LaunchCommandBuilder"/> and <see cref="WorktreePruneCoordinator"/>.</para>
    /// </summary>
    internal static class HelperReadinessTrigger
    {
        /// <summary>
        /// True when a registration for <paramref name="registeredName"/> means the helper called
        /// <paramref name="awaitedName"/> is alive and can be typed into.
        /// </summary>
        /// <remarks>
        /// <para>⚠️ <b>The port, never the event.</b> <c>TerminalRegistered</c> fires TWICE per spawn:
        /// once for MT's own pre-registration, which carries no channel port, and again for the helper's
        /// real registration, which does. A trigger keyed on "did a registration happen" would fire on the
        /// first — before <c>claude</c> has started — and type a job into a pane with nothing in it.</para>
        ///
        /// <para>Name comparison is ordinal-case-insensitive and trimmed, matching
        /// <c>AgentAttentionService.IsQuestionTextType</c>'s handling of agent names: the registration
        /// arrives from a separate process on its own release cadence, so a casing or whitespace variant
        /// must not silently degrade this back to the 120s path.</para>
        ///
        /// <para>The awaited name is the RESOLVED one — a held name comes back suffixed ("Name-2"), and
        /// binding to the requested name instead would wait for a registration that never comes (the
        /// orphan-pane class of defect from 77d1182f item 8).</para>
        /// </remarks>
        /// <param name="registeredName">Name on the row the registration event carried.</param>
        /// <param name="registeredChannelPort">That row's channel port; null before the helper registers.</param>
        /// <param name="awaitedName">The identity actually created for this spawn.</param>
        internal static bool IsHelperAlive(string registeredName, int? registeredChannelPort, string awaitedName)
        {
            if (registeredChannelPort == null) return false;
            if (string.IsNullOrWhiteSpace(registeredName) || string.IsNullOrWhiteSpace(awaitedName)) return false;

            return string.Equals(registeredName.Trim(), awaitedName.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
