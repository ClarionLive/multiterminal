using System;
using System.Text.Json.Serialization;

namespace MultiTerminal.Models
{
    /// <summary>
    /// A named Claude Code configuration directory (ticket c8ee0eec, GitHub #21). A terminal launched
    /// under a profile gets <c>CLAUDE_CONFIG_DIR</c> pointed at <see cref="ConfigDir"/>.
    /// <para>The configured profiles are machine-level settings (settings.txt, key
    /// <c>ClaudeProfiles</c>), read and written through <c>SettingsService.GetClaudeProfiles</c> /
    /// <c>SetClaudeProfiles</c>. Projects refer to a profile by <see cref="Name"/>, never by path.</para>
    /// <para>The implicit <c>Default</c> profile is never stored: it comes from
    /// <c>ClaudeProfileResolver.DefaultProfile</c>. Names are unique case-insensitively, and no stored
    /// profile may be called <c>Default</c>.</para>
    /// </summary>
    public sealed class ClaudeProfile
    {
        /// <summary>The reserved name of the implicit default profile.</summary>
        public const string DefaultName = "Default";

        /// <summary>Display name, and the value a project stores. Unique, case-insensitive.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Absolute path of the Claude Code config directory. For the implicit Default profile this is
        /// informational only: the launch prologue decides Default's environment from MT's own inherited
        /// <c>CLAUDE_CONFIG_DIR</c>, never from this value.
        /// </summary>
        public string ConfigDir { get; set; }

        /// <summary>True for the implicit Default profile (decided by name, case-insensitive).</summary>
        [JsonIgnore]
        public bool IsDefault => !string.IsNullOrWhiteSpace(Name) && NormalizeName(Name) == null;

        /// <summary>
        /// The one rule for what a profile NAME means: trimmed, and null for a blank name or
        /// <c>Default</c> (any case). Null therefore always reads as "the Default profile", and a
        /// non-null result is the name of a stored profile, compared ordinal-ignore-case elsewhere.
        /// Every place that stores, compares or resolves a profile name goes through this.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string trimmed = name.Trim();
            return string.Equals(trimmed, DefaultName, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }

        public ClaudeProfile()
        {
        }

        public ClaudeProfile(string name, string configDir)
        {
            Name = name;
            ConfigDir = configDir;
        }

        public override string ToString() => Name ?? "";
    }
}
