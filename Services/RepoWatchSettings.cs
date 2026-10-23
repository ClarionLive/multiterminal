#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The rules for a project's repository watch settings (ticket df58b35d, GitHub #53): which check
    /// intervals are allowed, and what a "Repositories" entry may contain. Pure and static, so the
    /// settings save (<see cref="ProjectSettingsService"/>), the single-field writer
    /// (<see cref="ProjectDatabase.UpdateProjectField"/>, which the REST/MCP update_project route uses)
    /// and the watcher that reads the list later all apply the same rule.
    /// </summary>
    public static class RepoWatchSettings
    {
        /// <summary>The interval a NULL <c>repo_watch_interval_min</c> reads as.</summary>
        public const int DefaultIntervalMinutes = 15;

        /// <summary>The intervals the form offers. 0 = check only at startup and when the project is launched.</summary>
        public static readonly IReadOnlyList<int> AllowedIntervals = new[] { 0, 5, 15, 30, 60 };

        private static readonly char[] Separators = { ',', ' ', '\t', '\r', '\n' };

        /// <summary>
        /// Parses an interval in minutes. Blank is <c>null</c> (read as <see cref="DefaultIntervalMinutes"/>);
        /// anything not in <see cref="AllowedIntervals"/> is refused with a message the form can show.
        /// </summary>
        public static bool TryParseInterval(string? input, out int? minutes, out string? error)
        {
            minutes = null;
            error = null;
            if (string.IsNullOrWhiteSpace(input))
                return true;

            if (int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && AllowedIntervals.Contains(value))
            {
                minutes = value;
                return true;
            }

            error = $"'Check every' must be one of {string.Join(", ", AllowedIntervals)} minutes, not '{input.Trim()}'.";
            return false;
        }

        /// <summary>
        /// Normalizes the Repositories text: entries split on commas and whitespace, each must be
        /// <c>owner/repo</c> (a GitHub owner and repository name, not a URL), duplicates dropped ignoring
        /// case (the first spelling is kept). <paramref name="normalized"/> is <c>owner/repo, owner/repo</c>,
        /// or <c>null</c> when nothing is listed (which means every GitHub remote of the project folder).
        /// On failure <paramref name="error"/> names the first bad entry.
        /// </summary>
        public static bool TryNormalizeRepos(string? input, out string? normalized, out string? error)
        {
            normalized = null;
            error = null;
            if (string.IsNullOrWhiteSpace(input))
                return true;

            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsOwnerRepo(entry))
                {
                    error = $"'{entry}' is not a repository in the form owner/repo (for example ClarionLive/multiterminal).";
                    return false;
                }
                if (seen.Add(entry))
                    kept.Add(entry);
            }

            normalized = kept.Count == 0 ? null : string.Join(", ", kept);
            return true;
        }

        /// <summary>
        /// The repositories in a stored (normalized) value; empty when none are listed. Entries that are not
        /// <c>owner/repo</c> are skipped, so a value written before validation existed cannot reach a URL.
        /// </summary>
        public static IReadOnlyList<string> ParseRepos(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return stored.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                         .Where(e => IsOwnerRepo(e) && seen.Add(e))
                         .ToList();
        }

        /// <summary>
        /// True when <paramref name="value"/> is exactly <c>owner/repo</c> under GitHub's naming rules.
        /// The rule itself is <see cref="GitHubRepoRef.TryParseFullName"/>, so remotes and typed entries agree.
        /// </summary>
        public static bool IsOwnerRepo(string? value) => GitHubRepoRef.TryParseFullName(value, out _);
    }
}
