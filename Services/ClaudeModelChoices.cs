using System;
using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>One entry of a model dropdown on the project settings form.</summary>
    public sealed class ClaudeModelOption
    {
        public ClaudeModelOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>What is stored: an alias, a full name, or <see cref="ClaudeModelLaunchPolicy.DefaultToken"/>.</summary>
        public string Value { get; }

        public string Label { get; }
    }

    /// <summary>What the Settings dialog shows for one model setting.</summary>
    public sealed class ClaudeModelSettingsPick
    {
        public ClaudeModelSettingsPick(IReadOnlyList<string> items, int selectedIndex, string customText)
        {
            Items = items;
            SelectedIndex = selectedIndex;
            CustomText = customText;
        }

        /// <summary>The aliases, then Claude Code's default, then "Other".</summary>
        public IReadOnlyList<string> Items { get; }

        public int SelectedIndex { get; }

        /// <summary>The typed name when "Other" is selected, else empty.</summary>
        public string CustomText { get; }
    }

    /// <summary>
    /// The model choices the UI offers (ticket b0d2f090), pure so they can be tested without a window.
    /// The aliases follow Anthropic's current model; a full or newer name is typed instead, so a new
    /// model works without an MT release. A stored name the list does not offer stays selected rather
    /// than being replaced, the way <see cref="ClaudeProfileChoices"/> keeps an unknown profile.
    /// </summary>
    public static class ClaudeModelChoices
    {
        public static readonly IReadOnlyList<string> Aliases = new[] { "opus", "sonnet", "haiku", "fable" };

        public const string DefaultLabel = "Claude Code's default";
        public const string OtherLabel = "Other (type a name)";

        private static int DefaultIndex => Aliases.Count;
        private static int OtherIndex => Aliases.Count + 1;

        /// <summary>
        /// Items for the project form's two dropdowns (the empty "Use global" entry is added by the page).
        /// A stored name outside this list is kept by the page as its own entry.
        /// </summary>
        public static IReadOnlyList<ClaudeModelOption> ProjectFormOptions()
        {
            var list = new List<ClaudeModelOption>();
            foreach (var alias in Aliases)
                list.Add(new ClaudeModelOption(alias, alias));
            list.Add(new ClaudeModelOption(ClaudeModelLaunchPolicy.DefaultToken, DefaultLabel));
            return list;
        }

        /// <summary>
        /// What the Settings dialog shows for <paramref name="stored"/>. Nothing stored shows
        /// <paramref name="builtIn"/> (the value the machine behaves as), so a fresh install and an
        /// existing machine both read opus, sonnet, sonnet.
        /// </summary>
        public static ClaudeModelSettingsPick BuildSettingsPick(string stored, string builtIn)
        {
            var items = new List<string>(Aliases) { DefaultLabel, OtherLabel };
            string value = ClaudeModelLaunchPolicy.Normalize(stored) ?? builtIn;

            if (ClaudeModelLaunchPolicy.IsDefaultToken(value))
                return new ClaudeModelSettingsPick(items, DefaultIndex, string.Empty);

            for (int i = 0; i < Aliases.Count; i++)
            {
                if (string.Equals(Aliases[i], value, StringComparison.OrdinalIgnoreCase))
                    return new ClaudeModelSettingsPick(items, i, string.Empty);
            }

            return new ClaudeModelSettingsPick(items, OtherIndex, value);
        }

        /// <summary>
        /// The value to store for a Settings pick, or an error message the dialog shows. A typed name is
        /// checked with <see cref="ClaudeModelLaunchPolicy.IsValidModelName"/>, the one gate in front of the
        /// command line.
        /// </summary>
        public static bool TryResolveSettingsPick(int selectedIndex, string customText, out string value, out string error)
        {
            value = null;
            error = null;

            if (selectedIndex >= 0 && selectedIndex < Aliases.Count)
            {
                value = Aliases[selectedIndex];
                return true;
            }

            if (selectedIndex == DefaultIndex)
            {
                value = ClaudeModelLaunchPolicy.DefaultToken;
                return true;
            }

            if (selectedIndex != OtherIndex)
            {
                error = "Pick a model.";
                return false;
            }

            string typed = ClaudeModelLaunchPolicy.Normalize(customText);
            if (typed == null)
            {
                error = "Type a model name, or pick one from the list.";
                return false;
            }

            if (ClaudeModelLaunchPolicy.IsDefaultToken(typed))
            {
                value = ClaudeModelLaunchPolicy.DefaultToken;
                return true;
            }

            if (!ClaudeModelLaunchPolicy.IsValidModelName(typed))
            {
                error = $"'{typed}' is not a valid model name. Use letters, digits, dots and hyphens, with an optional [1m] suffix.";
                return false;
            }

            value = typed;
            return true;
        }

        /// <summary>True when the "Other" entry is selected, which is when the dialog shows its text box.</summary>
        public static bool IsOtherIndex(int selectedIndex) => selectedIndex == OtherIndex;
    }
}
