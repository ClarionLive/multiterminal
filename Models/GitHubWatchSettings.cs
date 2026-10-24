using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// What MT does with new issues and PRs in a project's GitHub repository (task 92eeeb47).
    /// Stored in <c>projects.github_watch_mode</c>; NULL or unknown reads as <see cref="Off"/>.
    /// </summary>
    public enum GitHubWatchMode
    {
        /// <summary>Not watched. The default, so no existing project starts calling GitHub.</summary>
        Off = 0,

        /// <summary>Tell the Owner (a phone push) how many new issues and PRs arrived.</summary>
        Notify = 1,

        /// <summary>As Notify, and add one Suggestion card to the board per new issue or PR.</summary>
        Board = 2,
    }

    /// <summary>
    /// How often MT checks the repository on its own (task 92eeeb47). Stored in
    /// <c>projects.github_check_interval</c>; NULL or unknown reads as <see cref="Off"/>.
    /// </summary>
    public enum GitHubCheckInterval
    {
        /// <summary>No timed check; only "check on terminal start", if that is on.</summary>
        Off = 0,
        Minutes15 = 1,
        Hour1 = 2,
        Hours4 = 3,
        Daily = 4,
    }

    /// <summary>Parsing and storage strings for the GitHub watch settings.</summary>
    public static class GitHubWatchSettings
    {
        /// <summary>Case-insensitive, whitespace-tolerant; anything unrecognised is Off.</summary>
        public static GitHubWatchMode ParseMode(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "notify": return GitHubWatchMode.Notify;
                case "board": return GitHubWatchMode.Board;
                default: return GitHubWatchMode.Off;
            }
        }

        public static string ModeToStorage(GitHubWatchMode mode)
        {
            switch (mode)
            {
                case GitHubWatchMode.Notify: return "notify";
                case GitHubWatchMode.Board: return "board";
                default: return "off";
            }
        }

        /// <summary>Case-insensitive, whitespace-tolerant; anything unrecognised is Off.</summary>
        public static GitHubCheckInterval ParseInterval(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "15m": return GitHubCheckInterval.Minutes15;
                case "1h": return GitHubCheckInterval.Hour1;
                case "4h": return GitHubCheckInterval.Hours4;
                case "daily": return GitHubCheckInterval.Daily;
                default: return GitHubCheckInterval.Off;
            }
        }

        public static string IntervalToStorage(GitHubCheckInterval interval)
        {
            switch (interval)
            {
                case GitHubCheckInterval.Minutes15: return "15m";
                case GitHubCheckInterval.Hour1: return "1h";
                case GitHubCheckInterval.Hours4: return "4h";
                case GitHubCheckInterval.Daily: return "daily";
                default: return "off";
            }
        }

        /// <summary>The period of a timed check, or null for <see cref="GitHubCheckInterval.Off"/>.</summary>
        public static TimeSpan? PeriodOf(GitHubCheckInterval interval)
        {
            switch (interval)
            {
                case GitHubCheckInterval.Minutes15: return TimeSpan.FromMinutes(15);
                case GitHubCheckInterval.Hour1: return TimeSpan.FromHours(1);
                case GitHubCheckInterval.Hours4: return TimeSpan.FromHours(4);
                case GitHubCheckInterval.Daily: return TimeSpan.FromDays(1);
                default: return null;
            }
        }
    }
}
