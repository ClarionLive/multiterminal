using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ticket 37edc158: what a pane's header shows for its Claude profile. Pure. A pane under Default
    /// (or a blank name, or no launch) shows nothing; a named profile shows its trimmed name. The
    /// Default test is <see cref="ClaudeProfile.NormalizeName"/>, the one rule for profile names
    /// (claude-profiles.md invariant 10), never a local comparison.
    /// </summary>
    internal static class PaneProfileBadge
    {
        /// <summary>The text to show, or null when the header shows no profile.</summary>
        public static string TextFor(string launchProfileName) => ClaudeProfile.NormalizeName(launchProfileName);
    }
}
