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
        /// True when a registration for the row identified by <paramref name="registeredDocId"/> means the
        /// helper occupying <paramref name="awaitedDocId"/>'s pane is alive and can be typed into.
        /// </summary>
        /// <remarks>
        /// <para>⚠️ <b>The port, never the event.</b> <c>TerminalRegistered</c> fires TWICE per spawn:
        /// once for MT's own pre-registration, which carries no channel port, and again for the helper's
        /// real registration, which does. A trigger keyed on "did a registration happen" would fire on the
        /// first — before <c>claude</c> has started — and type a job into a pane with nothing in it.</para>
        ///
        /// <para>⚠️ <b>DOCID, NOT THE DISPLAY NAME</b> (task c28e6177). This predicate used to compare
        /// TRIMMED display names, and nothing else in the system trims one. The broker's uniqueness scan
        /// (<c>FindUniqueCandidate</c>) and its row lookup (<c>GetTerminal</c>) both compare
        /// ordinal-ignore-case with no trim, as do the plugin channel server's roster lookup and its
        /// <c>isAddressedToMe</c>. So a helper spawned as <c>"Alice "</c> while <c>"Alice"</c> was live got
        /// a SECOND broker row — the name was not held, so it was never suffixed — and this predicate then
        /// answered TRUE for the live Alice's registration. Reproduced in
        /// <c>HelperReadinessIdentityAsymmetryTests</c>; the ticket arrived as an unreproduced review
        /// inference and was demonstrated before being touched.</para>
        ///
        /// <para>A docId is minted by MT per pane (<c>Guid.NewGuid</c>), never round-trips through another
        /// process, and is the SAME key <c>Deliver</c> uses to find the document it is about to type into
        /// (<c>t.DocId == docId</c>). Comparing it is therefore not a stricter name check — it is the
        /// actual invariant: the row that registered is the row whose pane we are about to type into.
        /// <c>StringComparison.Ordinal</c> matches that lookup exactly; ignoring case here would let this
        /// approve a delivery the pane lookup then fails to find.</para>
        ///
        /// <para>The registering row carries the pane's docId even though the helper's own registration
        /// sends none: the channel server posts name + port + nonce + ownerPid, which lands in
        /// <c>DecideRegistration</c>'s name-match reuse branch and re-raises the PRE-REGISTERED row —
        /// and that row was created with <c>doc.DocId</c>.</para>
        ///
        /// <para>Binding to the docId also removes the suffixed-name hazard rather than handling it: a
        /// held name comes back as "Name-2", and a predicate keyed on the REQUESTED name would wait for a
        /// registration that never comes (the orphan-pane defect from 77d1182f item 8). There is no
        /// requested-vs-resolved distinction for a docId.</para>
        /// </remarks>
        /// <param name="registeredDocId">DocId on the row the registration event carried.</param>
        /// <param name="registeredChannelPort">That row's channel port; null before the helper registers.</param>
        /// <param name="awaitedDocId">DocId of the pane this spawn created.</param>
        internal static bool IsHelperAlive(string registeredDocId, int? registeredChannelPort, string awaitedDocId)
        {
            if (registeredChannelPort == null) return false;

            // An absent docId must never match an absent docId. Rows exist with no docId (an adopted
            // session registers without one), and treating two blanks as the same pane would fire this
            // trigger for a terminal that has no relationship to the spawn at all.
            if (string.IsNullOrWhiteSpace(registeredDocId) || string.IsNullOrWhiteSpace(awaitedDocId)) return false;

            // ⛔ DO NOT "FIX" AN IDENTITY MISMATCH BY TRIMMING. Not here, and above all not in
            // MessageBroker.FindUniqueCandidate, MessageBroker.GetTerminal, or the plugin channel
            // server (multiterminal-channel.mjs: the roster lookup and isAddressedToMe).
            //
            // Those four comparisons agree today — ordinal-ignore-case, untrimmed — and that agreement
            // is the system's definition of "one terminal". This predicate was the fifth and the only
            // one that trimmed, which is what task c28e6177 was: NOT a missing trim somewhere, but one
            // extra trim here. The tempting cleanup is to make the other four match this one. It is
            // exactly backwards, and it is not a tidy-up — adding Trim to those comparisons MERGES two
            // rows the broker deliberately keeps separate, inside the comparison that decides which
            // agent receives a message. Two terminals would answer to one name.
            //
            // The asymmetry is reproduced against a real broker in HelperReadinessIdentityAsymmetryTests,
            // including two CONTROL facts (an exact collision, and a cased variant) that go red if
            // anyone teaches the broker to trim. If the trim question comes up again, run that file
            // before arguing from first principles.
            //
            // Whitespace in a name is a real problem; the place to deal with it is the EDGE, by
            // normalising a requested name before any row is created from it — never in a comparison
            // between rows that already exist.
            return string.Equals(registeredDocId, awaitedDocId, StringComparison.Ordinal);
        }
    }
}
