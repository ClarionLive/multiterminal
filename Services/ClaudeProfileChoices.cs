using System;
using System.Collections.Generic;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One entry of a "Claude profile" dropdown (ticket c8ee0eec, GitHub #21).
    /// <see cref="Name"/> is what gets stored for the project: null for the Default profile.
    /// </summary>
    public sealed class ClaudeProfileChoice
    {
        public ClaudeProfileChoice(string name, string label, bool isMissing)
        {
            Name = name;
            Label = label;
            IsMissing = isMissing;
        }

        /// <summary>The profile name to store, or null for Default.</summary>
        public string Name { get; }

        /// <summary>The text shown in the dropdown.</summary>
        public string Label { get; }

        /// <summary>True for a stored name that this machine has no profile for.</summary>
        public bool IsMissing { get; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Builds the item list for a project's "Claude profile" dropdown. Pure, so the rule that matters
    /// can be tested without a form: a project whose stored profile is not configured on this machine
    /// keeps that name as its selected entry, so saving the dialog without touching the dropdown does
    /// not move the project to another profile.
    /// </summary>
    public static class ClaudeProfileChoices
    {
        /// <summary>Suffix on the entry for a stored name this machine has no profile for.</summary>
        public const string MissingSuffix = " (not configured on this machine)";

        /// <summary>
        /// Default first, then every configured profile in the order given, then (only when needed) the
        /// stored name marked as missing. Names match the way the resolver matches them: trimmed,
        /// ignoring case.
        /// </summary>
        /// <param name="allProfiles">From <see cref="ClaudeProfileResolver.GetAllProfiles()"/>. Null is treated as empty; a Default entry is always produced.</param>
        /// <param name="storedName">The project's stored profile name; null, blank or "Default" selects Default.</param>
        /// <param name="selectedIndex">Index of the entry for <paramref name="storedName"/>.</param>
        public static IReadOnlyList<ClaudeProfileChoice> Build(
            IReadOnlyList<ClaudeProfile> allProfiles, string storedName, out int selectedIndex)
        {
            var choices = new List<ClaudeProfileChoice>
            {
                new ClaudeProfileChoice(null, ClaudeProfile.DefaultName, isMissing: false)
            };

            if (allProfiles != null)
            {
                foreach (var profile in allProfiles)
                {
                    if (profile == null || profile.IsDefault || string.IsNullOrWhiteSpace(profile.Name))
                        continue;

                    string name = profile.Name.Trim();
                    choices.Add(new ClaudeProfileChoice(name, name, isMissing: false));
                }
            }

            string stored = ProjectDatabase.NormalizeClaudeProfileName(storedName);
            selectedIndex = 0;
            if (stored == null)
                return choices;

            for (int i = 1; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].Name, stored, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    return choices;
                }
            }

            choices.Add(new ClaudeProfileChoice(stored, stored + MissingSuffix, isMissing: true));
            selectedIndex = choices.Count - 1;
            return choices;
        }
    }
}
