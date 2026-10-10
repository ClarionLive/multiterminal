using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// What <see cref="ClaudeProfileEditorRules.Evaluate"/> made of the Settings dialog's Claude Profiles grid.
    /// </summary>
    /// <param name="ToSave">The non-blank rows, trimmed, in grid order. Only meaningful to store when
    /// <see cref="IsValid"/> is true.</param>
    /// <param name="RowErrors">Grid-row index (blank rows included in the count) → that row's error text,
    /// one line per problem. Rows without a problem have no entry.</param>
    /// <param name="Messages">Every validator message, in the validator's order, for the summary box.</param>
    internal sealed record ClaudeProfileEditorResult(
        IReadOnlyList<ClaudeProfile> ToSave,
        IReadOnlyDictionary<int, string> RowErrors,
        IReadOnlyList<string> Messages)
    {
        public bool IsValid => Messages.Count == 0;
    }

    /// <summary>
    /// The decisions behind the Settings dialog's Claude Profiles tab (ticket c8ee0eec, GitHub #21), kept
    /// out of the WPF code-behind so they can be tested: which grid rows count, which grid row a
    /// validation error belongs to, and which stored profile names a save would take away.
    /// </summary>
    internal static class ClaudeProfileEditorRules
    {
        /// <summary>
        /// Drops rows whose name and folder are both blank, trims the rest, and runs
        /// <paramref name="validate"/> over them. The validator numbers the list it was given, which no
        /// longer lines up with the grid once a blank row has been dropped, so each error is mapped back
        /// to the index of the grid row it came from.
        /// </summary>
        public static ClaudeProfileEditorResult Evaluate(
            IReadOnlyList<(string Name, string ConfigDir)> rows,
            Func<IReadOnlyList<ClaudeProfile>, IReadOnlyList<ClaudeProfileValidationError>> validate)
        {
            ArgumentNullException.ThrowIfNull(validate);

            var toSave = new List<ClaudeProfile>();
            var gridIndex = new List<int>();
            for (int i = 0; i < (rows?.Count ?? 0); i++)
            {
                string name = rows[i].Name?.Trim() ?? "";
                string dir = rows[i].ConfigDir?.Trim() ?? "";
                if (name.Length == 0 && dir.Length == 0)
                    continue;

                toSave.Add(new ClaudeProfile(name, dir));
                gridIndex.Add(i);
            }

            var rowErrors = new Dictionary<int, string>();
            var messages = new List<string>();
            foreach (var error in validate(toSave) ?? Array.Empty<ClaudeProfileValidationError>())
            {
                messages.Add(error.Message);
                if (error.Index < 0 || error.Index >= gridIndex.Count)
                    continue;

                int row = gridIndex[error.Index];
                rowErrors[row] = rowErrors.TryGetValue(row, out string existing)
                    ? existing + Environment.NewLine + error.Message
                    : error.Message;
            }

            return new ClaudeProfileEditorResult(toSave, rowErrors, messages);
        }

        /// <summary>
        /// True when saving <paramref name="toSave"/> would store exactly what is stored now (same names
        /// and folders, same order, compared as text). The dialog then leaves the setting alone, so a
        /// stored profile whose folder is currently missing cannot block OK for an unrelated change.
        /// </summary>
        public static bool IsUnchanged(IReadOnlyList<ClaudeProfile> stored, IReadOnlyList<ClaudeProfile> toSave)
        {
            int count = stored?.Count ?? 0;
            if (count != (toSave?.Count ?? 0))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(stored[i]?.Name, toSave[i]?.Name, StringComparison.Ordinal)
                    || !string.Equals(stored[i]?.ConfigDir, toSave[i]?.ConfigDir, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The stored profile names that <paramref name="toSave"/> no longer contains, compared without
        /// regard to case. A rename shows up here under its old name: projects refer to a profile by
        /// name, so renaming one strands them exactly as removing it does.
        /// </summary>
        public static IReadOnlyList<string> RemovedNames(IReadOnlyList<ClaudeProfile> stored, IReadOnlyList<ClaudeProfile> toSave)
        {
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in toSave ?? Array.Empty<ClaudeProfile>())
            {
                if (!string.IsNullOrWhiteSpace(p?.Name))
                    kept.Add(p.Name.Trim());
            }

            var removed = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in stored ?? Array.Empty<ClaudeProfile>())
            {
                string name = p?.Name?.Trim();
                if (!string.IsNullOrEmpty(name) && !kept.Contains(name) && seen.Add(name))
                    removed.Add(name);
            }

            return removed;
        }

        /// <summary>The confirmation text shown before a save that removes or renames stored profiles.</summary>
        public static string RemovalWarning(IReadOnlyList<string> names)
        {
            var list = (names ?? Array.Empty<string>()).ToList();
            string which = list.Count == 1
                ? $"The Claude profile '{list[0]}' will no longer exist"
                : "These Claude profiles will no longer exist: " + string.Join(", ", list.Select(n => $"'{n}'"));

            return which + " (a renamed profile counts as removed)." + Environment.NewLine + Environment.NewLine
                + "Any project still set to one of them will refuse to start "
                + "until you pick another profile for it in Edit Project. The folders themselves are not deleted."
                + Environment.NewLine + Environment.NewLine
                + "Save anyway?";
        }

        /// <summary>
        /// The read-only Default line: the folder, plus where it came from when MultiTerminal itself was
        /// started with CLAUDE_CONFIG_DIR set.
        /// </summary>
        public static string DescribeDefault(string dir, string inherited)
        {
            string folder = string.IsNullOrWhiteSpace(dir) ? "(unknown)" : dir.Trim();
            return string.IsNullOrWhiteSpace(inherited)
                ? folder
                : folder + $"  (from MultiTerminal's own {ClaudeProfileResolver.ConfigDirVariable})";
        }
    }
}
