using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MultiTerminal.Docking;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What a "Just Claude" launch registers as, given the Launch Claude picker's choice (ticket ae3d0193).
    /// Pure, so the rule is testable without WinForms; <c>MainForm.OnStartScreenJustClaude</c> asks here
    /// for both the name and the registration route.
    ///
    /// <para><b>"Unassigned" (the picker's default) launches under its OWN unique, non-team name</b>
    /// (Owner decision 2026-10-04, after pipeline Run 1's debugger finding). It used to register as the
    /// shared "Unassigned" placeholder, which made several live Claude sessions share one name: name-keyed
    /// lookups resolved to the first row, the fallback inbox <c>Unassigned.json</c> could be consumed by any
    /// of them, the plugin told each to send as "Unassigned", and none got an Attention card. The dialog
    /// still SHOWS the entry as "Unassigned"; only the launch identity changed.</para>
    ///
    /// <para><b>The name reuses ticket 6a8d029f's no-lead project rule</b>, not a third scheme:
    /// <see cref="ProjectLaunchIdentity.Resolve"/> with <see cref="DefaultBaseName"/> as the base and the
    /// pane's docId as the qualifier source, then registered through <c>MainForm.TryRegisterProjectIdentity</c>
    /// (the broker's atomic <c>RegisterTerminalUnique</c>, failing closed). So it is "Claude", then
    /// "Claude-2", "Claude-3"… among connected terminals; if "Claude" is itself reserved (a team lead or
    /// roster agent of any project, a project named Claude, a pool name, Oracle, the Codex default or a
    /// name the picker offered, which never includes a generated name: see <see cref="IsGeneratedName"/>)
    /// it becomes "Claude-&lt;first 4 of the docId&gt;". It is never team lead,
    /// never the placeholder, and never goes through <c>PreRegisterTerminalWithName</c>, whose refusal
    /// fallback hands out the first free POOL name (Grace, Charlie…).</para>
    ///
    /// <para>Suffix reuse is inherited from 6a8d029f: after "Claude-2" closes, the next second Just Claude
    /// pane is "Claude-2" again and inherits that name's profile and inbox.</para>
    ///
    /// <para><b>A named agent</b> launches exactly as before this ticket: under that name, as team lead,
    /// through <c>PreRegisterTerminalWithName</c>.</para>
    /// </summary>
    /// <param name="TerminalName">The name to REQUEST (the unique route may hand back a suffixed one).</param>
    /// <param name="IsTeamLead">Passed to registration and to <c>StartTerminal</c>.</param>
    /// <param name="RegisterUnique">The picker's default: register through the unique, fail-closed route
    /// and skip the active-task spawn-directory lookup.</param>
    internal readonly record struct JustClaudeLaunchIdentity(string TerminalName, bool IsTeamLead, bool RegisterUnique)
    {
        /// <summary>The base of the unique name a default ("Unassigned") launch takes.</summary>
        internal const string DefaultBaseName = "Claude";

        /// <summary>
        /// Exactly the names a default launch can end up with, and no others. <see cref="ProjectLaunchIdentity.Resolve"/>
        /// gives the literal base or base + "-" + the first 4 of the docId (always lower-case hex: the docId is
        /// <c>Guid.ToString("N")</c>); <c>MessageBroker.FindUniqueCandidate</c> then adds at most ONE suffix,
        /// "-2".."-999" or a last-resort 4-hex guid. Case-sensitive and untrimmed on purpose: a name a person
        /// chose ("claude", "Claude-1", "Claude-Reviewer") is not generated and stays pickable.
        /// </summary>
        private static readonly Regex GeneratedNamePattern = new(
            "^" + DefaultBaseName
                + "(?:-(?:[2-9]|[1-9][0-9]{1,2}|[0-9a-f]{4})"                // Claude-N | Claude-hhhh (unqualified base, broker suffix)
                + "|-[0-9a-f]{4}-(?:[2-9]|[1-9][0-9]{1,2}|[0-9a-f]{4}))?$",  // Claude-hhhh-N | Claude-hhhh-hhhh (qualified base, broker suffix)
            RegexOptions.CultureInvariant);

        /// <summary>
        /// True for a name a default ("Unassigned") launch generates. Such a name is a throwaway, not a team
        /// identity, so it must never be offered as a pickable agent: once a "Claude" session was recorded in
        /// a folder's history the picker listed it, the census then reserved every listed name, and every
        /// later default launch became "Claude-&lt;docId4&gt;" (each of which was listed next time, and picking
        /// one launched it as team lead). Live test 2026-10-04.
        /// </summary>
        internal static bool IsGeneratedName(string name) =>
            !string.IsNullOrEmpty(name) && GeneratedNamePattern.IsMatch(name);

        /// <summary>
        /// The identities a Launch Claude / project picker offers: the names discovered in the folder's
        /// session history, minus <see cref="IsGeneratedName"/> names, then the pool names not already
        /// listed. <c>MainForm.GetAvailableIdentities</c> builds its list here, so the picker and the
        /// reserved-name census (which reserves every offered name) both leave the generated names out.
        /// </summary>
        /// <param name="isRegisteredIdentity">True for a name a project declares (team lead or roster agent).
        /// Such a name is a real identity even when it has a generated shape (a team lead called "Claude"),
        /// so it is kept. Null means no project names are known.</param>
        internal static List<string> BuildPickerIdentities(
            IEnumerable<string> discovered,
            IEnumerable<string> pool,
            Func<string, bool> isRegisteredIdentity = null)
        {
            var identities = new List<string>();
            if (discovered != null)
            {
                foreach (var name in discovered)
                {
                    if (!IsGeneratedName(name) || (isRegisteredIdentity?.Invoke(name) ?? false))
                        identities.Add(name);
                }
            }

            var existing = identities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (pool != null)
            {
                foreach (var name in pool)
                {
                    if (existing.Add(name)) identities.Add(name);
                }
            }

            return identities;
        }

        /// <summary>The unique, fail-closed registration route (<c>MainForm.TryRegisterProjectIdentity</c>).</summary>
        internal delegate bool TryRegisterUniqueName(string requested, out string resolved, out string error);

        /// <summary>
        /// True when the picker's choice is the default: the "Unassigned" entry, in any casing, or nothing.
        /// Such a launch needs the reserved-name census before <see cref="FromPickerSelection"/>.
        /// </summary>
        internal static bool IsDefaultChoice(string selected) =>
            string.IsNullOrWhiteSpace(selected) || TerminalRegistrationBinder.IsUnassigned(selected.Trim());

        /// <summary>
        /// Maps the picker's selected entry to the launch identity.
        /// </summary>
        /// <param name="selected">The picker's selection.</param>
        /// <param name="docId">The pane's docId; the qualifier source when "Claude" is reserved.</param>
        /// <param name="reserved">Names a default launch must not take. Required for the default choice
        /// (a null set must never read as "nothing reserved"); ignored for a named agent.</param>
        internal static JustClaudeLaunchIdentity FromPickerSelection(string selected, string docId, IReadOnlyCollection<string> reserved)
        {
            if (!IsDefaultChoice(selected))
                return new JustClaudeLaunchIdentity(selected, IsTeamLead: true, RegisterUnique: false);

            if (reserved == null)
                throw new ArgumentNullException(nameof(reserved), "A default Just Claude launch needs the reserved-name census (ae3d0193).");

            // 6a8d029f's no-lead rule, with "Claude" as the base: sanitized base, qualified with the id if
            // reserved. Resolve is called with no team lead and no Codex default, so it always takes that
            // branch and asks for the unique route.
            var request = ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, DefaultBaseName, docId, reserved);
            return new JustClaudeLaunchIdentity(request.Name, IsTeamLead: false, RegisterUnique: true);
        }

        /// <summary>
        /// Registers <paramref name="identity"/> through the route it calls for. The default goes ONLY
        /// through <paramref name="registerUnique"/> and fails closed (no name, a refusal) when that is
        /// refused; <paramref name="registerNamed"/> (the pool-falling-back route) is never called for it.
        /// A named agent goes through <paramref name="registerNamed"/>, as before this ticket.
        /// </summary>
        /// <returns>False with <paramref name="refusal"/> set when the launch must not start.</returns>
        internal static bool TryRegister(
            JustClaudeLaunchIdentity identity,
            TryRegisterUniqueName registerUnique,
            Func<string, string> registerNamed,
            out string terminalName,
            out string refusal)
        {
            ArgumentNullException.ThrowIfNull(registerUnique);
            ArgumentNullException.ThrowIfNull(registerNamed);

            if (identity.RegisterUnique)
            {
                if (registerUnique(identity.TerminalName, out terminalName, out refusal))
                    return true;

                terminalName = null;
                refusal = string.IsNullOrWhiteSpace(refusal) ? "the broker refused the registration without a reason" : refusal;
                return false;
            }

            terminalName = registerNamed(identity.TerminalName);
            refusal = null;
            return true;
        }
    }
}
