using System;
using System.Linq;
using System.Text.Json;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>What a pane's header shows for its Claude profile: a short text and a longer tooltip.</summary>
    internal sealed record PaneProfileBadgeInfo(string Text, string Tooltip);

    /// <summary>
    /// Ticket 37edc158: what a pane's header shows for its Claude profile. Pure; what it needs from the
    /// machine arrives in a <see cref="ClaudeProfileContext"/>. The header shows the EFFECTIVE situation:
    /// <list type="bullet">
    ///   <item>a named profile: its trimmed name;</item>
    ///   <item>Default with nothing inherited: nothing;</item>
    ///   <item>Default while MT itself was started with CLAUDE_CONFIG_DIR (the resolver then repeats that
    ///   folder for a Default pane, claude-profiles.md invariant 1): the configured profile whose folder it
    ///   is, else "Inherited config" with the folder as the tooltip. Otherwise a Default pane would run
    ///   from another folder while the header said nothing.</item>
    /// </list>
    /// The Default test is <see cref="ClaudeProfile.NormalizeName"/> and folders compare through
    /// <see cref="ClaudeProfileResolver.Canonicalize"/>, ordinal-ignore-case (invariant 10).
    /// </summary>
    internal static class PaneProfileBadge
    {
        internal const string InheritedLabel = "Inherited config";

        /// <summary>The badge to show, or null when the header shows no profile.</summary>
        public static PaneProfileBadgeInfo For(string launchProfileName, ClaudeProfileContext ctx)
        {
            string named = ClaudeProfile.NormalizeName(launchProfileName);
            if (named != null)
                return new PaneProfileBadgeInfo(named, "Claude profile: " + named);

            string inherited = ClaudeProfileResolver.Canonicalize(ctx?.InheritedConfigDir);
            if (inherited == null)
                return null;

            // MT's inherited folder IS Default's folder when it is %USERPROFILE%\.claude: nothing unusual.
            string home = string.IsNullOrWhiteSpace(ctx.UserProfile)
                ? null
                : ClaudeProfileResolver.Canonicalize(System.IO.Path.Combine(ctx.UserProfile, ".claude"));
            if (home != null && SameFolder(inherited, home))
                return null;

            var match = ctx.Configured?.FirstOrDefault(p =>
                p != null && !string.IsNullOrWhiteSpace(p.Name) && ClaudeProfile.NormalizeName(p.Name) != null
                && SameFolder(ClaudeProfileResolver.Canonicalize(p.ConfigDir), inherited));
            if (match != null)
            {
                string name = match.Name.Trim();
                return new PaneProfileBadgeInfo(name, $"Claude profile: {name} (inherited: MultiTerminal was started with CLAUDE_CONFIG_DIR = {inherited})");
            }

            return new PaneProfileBadgeInfo(InheritedLabel, $"MultiTerminal was started with CLAUDE_CONFIG_DIR = {inherited}, and Default panes use that folder.");
        }

        /// <summary>The header message payload: JSON, or the word null for no badge. The renderer keeps
        /// this string and replays it, so the tooltip survives a page reload with the text.</summary>
        public static string ToWire(PaneProfileBadgeInfo badge) =>
            badge == null ? "null" : JsonSerializer.Serialize(new { text = badge.Text, tooltip = badge.Tooltip });

        private static bool SameFolder(string a, string b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
