using System.Collections.Generic;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// The NARROW collaborator surface <see cref="ProfileService"/> needs from <see cref="MessageBroker"/>.
    ///
    /// <para>Ticket 86f3fd21 (MessageBroker decomposition — SECOND region, validating the extraction template
    /// at <c>.claude/rules/broker-extraction-pattern.md</c> proven on <see cref="TaskService"/>). The team-member
    /// profile region was extracted into <see cref="ProfileService"/>, which owns the <c>_profiles</c> cache +
    /// all profile CRUD + the profile write path (clone→persist→swap, from 1df2a534). Everything a profile
    /// method touches that lives in ANOTHER region — event raising, the <c>IsTemporaryAgent</c> naming utility —
    /// is reached through this interface, which the broker implements.</para>
    ///
    /// <para>WHY AN INTERFACE, NOT A RAW <see cref="MessageBroker"/> BACK-REFERENCE: a raw broker reference
    /// would re-expose all ~246 broker methods to ProfileService and make the decomposition cosmetic. This
    /// interface ENUMERATES exactly the region's outbound coupling — the same "enumerate the coupling, don't
    /// prose it" census discipline the P5 verifiers use — so the coupling stays reviewable and bounded. This is
    /// the SECOND host-interface slice (after <see cref="ITaskServiceHost"/>); its small size is itself the
    /// evidence the pattern generalizes to the remaining regions.</para>
    /// </summary>
    internal interface IProfileServiceHost
    {
        // ── Logging (broker DebugLogService wrappers; stamped with source "ProfileService") ─────────
        void LogError(string message);
        void LogInfo(string message);

        // ── Event raiser — the ProfilesUpdated event STAYS declared on the broker so subscribers
        //    (MainForm, ProfilePanel) are untouched; ProfileService raises it through this wrapper, which
        //    calls the broker's private resilient RaiseSafe dispatch (1df2a534). ──────────────────────
        void RaiseProfilesUpdated(List<TeamMemberProfile> profiles);

        // ── Shared naming utility (broker-owned; also used by the registration/worktree regions). Kept
        //    with its owner; ListProfiles filters temporary "Agent *" subagents out of the roster. ─────
        bool IsTemporaryAgent(string name);
    }
}
