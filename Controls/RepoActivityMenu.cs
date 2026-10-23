#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Controls
{
    /// <summary>What clicking a <see cref="RepoActivityMenuEntry"/> does.</summary>
    internal enum RepoActivityMenuAction
    {
        /// <summary>Nothing: a header, an information line, or a separator.</summary>
        None = 0,

        /// <summary>Opens <see cref="RepoActivityMenuEntry.Url"/> in the default browser.</summary>
        OpenUrl,

        MarkAllSeen,

        CheckNow,

        /// <summary>The same path as the status bar's gear.</summary>
        ProjectSettings,
    }

    /// <summary>One line of the list. Built purely by <see cref="RepoActivityMenu.BuildEntries"/>, rendered by <see cref="RepoActivityMenu.Show"/>.</summary>
    internal sealed record RepoActivityMenuEntry(
        string Text,
        RepoActivityMenuAction Action = RepoActivityMenuAction.None,
        string? Url = null,
        bool Enabled = true,
        bool IsSeparator = false,
        bool IsHeader = false,
        bool IsNew = false,
        string? Repo = null)
    {
        public static RepoActivityMenuEntry Separator { get; } = new(string.Empty, IsSeparator: true, Enabled: false);

        public static RepoActivityMenuEntry Info(string text) => new(text, Enabled: false);
    }

    /// <summary>
    /// The list a click on the status bar's repository badge opens (task df58b35d, GitHub #53): per
    /// watched repository, its open issues and pull requests (new first), then the actions. A native menu
    /// rather than HTML, so the only thing that opens a browser is <see cref="IsAllowedRepoUrl"/>.
    /// </summary>
    internal static class RepoActivityMenu
    {
        /// <summary>Items listed per repository before "More on GitHub…".</summary>
        internal const int MaxItemsPerRepo = 25;

        /// <summary>Titles longer than this are cut, with an ellipsis.</summary>
        internal const int MaxTitleLength = 70;

        /// <summary>
        /// The last gate before the shell opens a link: true only for a github.com page of
        /// <paramref name="repoFullName"/> (<see cref="GitHubRepoRef.IsPageUrl"/>: <c>/issues/{n}</c>,
        /// <c>/pull/{n}</c>, <c>/issues</c> or <c>/pulls</c>). Checked again at click time, because the item
        /// URLs came from the network.
        /// </summary>
        internal static bool IsAllowedRepoUrl(string? url, string? repoFullName)
            => GitHubRepoRef.TryParseFullName(repoFullName, out var repo) && repo.IsPageUrl(url);

        // Empty (a disabled row) for a name that does not parse.
        private static string PageUrl(string fullName, Func<GitHubRepoRef, string> page)
            => GitHubRepoRef.TryParseFullName(fullName, out var repo) ? page(repo) : string.Empty;

        /// <summary>The list for <paramref name="summary"/>. Pure: no UI, no I/O.</summary>
        internal static IReadOnlyList<RepoActivityMenuEntry> BuildEntries(RepoActivitySummary? summary, DateTime nowUtc)
        {
            var entries = new List<RepoActivityMenuEntry>();

            if (summary == null || !summary.Watching)
            {
                entries.Add(RepoActivityMenuEntry.Info(summary?.Reason ?? "Watching issues and pull requests is off for this project."));
                entries.Add(RepoActivityMenuEntry.Separator);
                entries.Add(new RepoActivityMenuEntry("Project settings…", RepoActivityMenuAction.ProjectSettings));
                return entries;
            }

            bool checking = summary.State == RepoActivityState.Checking;

            if (summary.Repos.Count == 0)
            {
                entries.Add(RepoActivityMenuEntry.Info(summary.Reason ?? "No GitHub repository found for this project."));
                entries.Add(RepoActivityMenuEntry.Separator);
                entries.Add(new RepoActivityMenuEntry("Check now", RepoActivityMenuAction.CheckNow, Enabled: !checking));
                entries.Add(new RepoActivityMenuEntry("Project settings…", RepoActivityMenuAction.ProjectSettings));
                return entries;
            }

            foreach (var repo in summary.Repos)
            {
                entries.Add(new RepoActivityMenuEntry(repo.FullName, Enabled: false, IsHeader: true));

                if (repo.State != RepoActivityState.Ok && !string.IsNullOrWhiteSpace(repo.Reason))
                    entries.Add(RepoActivityMenuEntry.Info(repo.Reason!));

                foreach (var entry in repo.Items.Take(MaxItemsPerRepo))
                {
                    entries.Add(new RepoActivityMenuEntry(
                        FormatItem(entry, nowUtc),
                        RepoActivityMenuAction.OpenUrl,
                        entry.Url,
                        Enabled: IsAllowedRepoUrl(entry.Url, repo.FullName),
                        IsNew: entry.IsNew,
                        Repo: repo.FullName));
                }

                if (repo.Items.Count > MaxItemsPerRepo || repo.Truncated)
                {
                    string more = PageUrl(repo.FullName, r => r.IssuesUrl);
                    entries.Add(new RepoActivityMenuEntry("More on GitHub…", RepoActivityMenuAction.OpenUrl, more,
                        Enabled: IsAllowedRepoUrl(more, repo.FullName), Repo: repo.FullName));
                }
                else if (repo.Items.Count == 0 && repo.State == RepoActivityState.Ok)
                {
                    entries.Add(RepoActivityMenuEntry.Info("No open issues or pull requests"));
                }

                entries.Add(RepoActivityMenuEntry.Separator);
            }

            if (summary.State != RepoActivityState.Ok && !checking && !string.IsNullOrWhiteSpace(summary.Reason))
                entries.Add(RepoActivityMenuEntry.Info(summary.Reason!));

            entries.Add(new RepoActivityMenuEntry("Mark all seen", RepoActivityMenuAction.MarkAllSeen, Enabled: summary.NewCount > 0));
            entries.Add(new RepoActivityMenuEntry(checking ? "Checking…" : "Check now", RepoActivityMenuAction.CheckNow, Enabled: !checking));

            bool several = summary.Repos.Count > 1;
            foreach (var repo in summary.Repos)
            {
                string suffix = several ? $" ({repo.FullName})" : string.Empty;
                string issues = PageUrl(repo.FullName, r => r.IssuesUrl);
                string pulls = PageUrl(repo.FullName, r => r.PullsUrl);
                entries.Add(new RepoActivityMenuEntry("Open issues on GitHub" + suffix, RepoActivityMenuAction.OpenUrl, issues,
                    Enabled: IsAllowedRepoUrl(issues, repo.FullName), Repo: repo.FullName));
                entries.Add(new RepoActivityMenuEntry("Open pull requests on GitHub" + suffix, RepoActivityMenuAction.OpenUrl, pulls,
                    Enabled: IsAllowedRepoUrl(pulls, repo.FullName), Repo: repo.FullName));
            }

            return entries;
        }

        /// <summary><c>● #53  [PR]  Title — author, 2d</c>; the dot only for a new item.</summary>
        internal static string FormatItem(RepoActivityEntry entry, DateTime nowUtc)
        {
            var item = entry.Item;
            string kind = item.IsPullRequest ? (item.IsDraft ? "[Draft PR]  " : "[PR]  ") : string.Empty;
            string title = item.Title ?? string.Empty;
            if (title.Length > MaxTitleLength) title = title.Substring(0, MaxTitleLength - 1).TrimEnd() + "…";
            return (entry.IsNew ? "● " : "    ")
                   + "#" + item.Number.ToString(CultureInfo.InvariantCulture) + "  "
                   + kind + title
                   + " — " + item.Author + ", " + ShortAge(item.CreatedAtUtc, nowUtc);
        }

        /// <summary>"now", "5m", "3h", "2d".</summary>
        internal static string ShortAge(DateTime thenUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - thenUtc;
            if (age < TimeSpan.FromMinutes(1)) return "now";
            if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
            if (age < TimeSpan.FromDays(1)) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
            return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
        }

        /// <summary>
        /// Shows the list under the cursor, themed like the app's other menus. A URL item opens only if it
        /// passes <see cref="IsAllowedRepoUrl"/> again at click time, for the repository it belongs to
        /// (<see cref="RepoActivityMenuEntry.Repo"/>).
        /// </summary>
        internal static void Show(
            RepoActivitySummary summary,
            bool isDark,
            Action markAllSeen,
            Action checkNow,
            Action openProjectSettings,
            Action<string>? log,
            Rectangle? badgeScreenBounds = null)
        {
            // CA2000: both are owned by the menu's Closed handler below, which disposes them once the menu
            // has closed and its click has run; a using here would dispose them while the menu is open.
#pragma warning disable CA2000
            var menu = new ContextMenuStrip
            {
                Renderer = isDark ? new DarkToolStripRenderer() : new LightToolStripRenderer(),
                BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(246, 246, 246),
                ForeColor = isDark ? Color.White : Color.Black,
                ShowImageMargin = false,
            };
            Color dimColor = isDark ? Color.FromArgb(150, 150, 155) : Color.FromArgb(110, 110, 115);
            Color newColor = isDark ? Color.FromArgb(129, 140, 248) : Color.FromArgb(79, 70, 229);
            var headerFont = new Font(menu.Font, FontStyle.Bold);
#pragma warning restore CA2000

            foreach (var entry in BuildEntries(summary, DateTime.UtcNow))
            {
                if (entry.IsSeparator)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    continue;
                }

                // '&' is a mnemonic marker in a menu item; a title may contain one.
                var item = new ToolStripMenuItem(entry.Text.Replace("&", "&&", StringComparison.Ordinal))
                {
                    Enabled = entry.Enabled,
                    ForeColor = entry.IsNew ? newColor : entry.Enabled ? menu.ForeColor : dimColor,
                };
                if (entry.IsHeader)
                {
                    item.Font = headerFont;
                }

                if (entry.Enabled)
                {
                    string? url = entry.Url;
                    string? repoForUrl = entry.Repo;
                    switch (entry.Action)
                    {
                        case RepoActivityMenuAction.OpenUrl:
                            item.Click += (s, e) => OpenUrl(url, repoForUrl, log);
                            break;
                        case RepoActivityMenuAction.MarkAllSeen:
                            item.Click += (s, e) => markAllSeen();
                            break;
                        case RepoActivityMenuAction.CheckNow:
                            item.Click += (s, e) => checkNow();
                            break;
                        case RepoActivityMenuAction.ProjectSettings:
                            item.Click += (s, e) => openProjectSettings();
                            break;
                    }
                }

                menu.Items.Add(item);
            }

            // A click on a terminal or any other WebView2 never reaches the menu's own click-away
            // handling, so this closes it instead. The press that closes it may be on the badge itself,
            // whose click then arrives here: that click only closes (see ShouldSuppressReopen).
            OutsideClickMenuCloser.Attach(menu, () => _lastOutsideCloseTicks = Environment.TickCount64);

            // The menu is built per click; release it and the header font once it closes.
            menu.Closed += (s, e) => menu.BeginInvoke(new Action(() =>
            {
                menu.Dispose();
                headerFont.Dispose();
            }));

            if (badgeScreenBounds is Rectangle badge)
            {
                // Right above the badge, on the badge's own monitor (Owner live test: at the cursor
                // position the list opened on the other screen). A list taller than the work area starts
                // at its top; WinForms keeps a drop-down on the screen it opens on and adds scroll arrows.
                Rectangle area = Screen.FromRectangle(badge).WorkingArea;
                Rectangle bounds = PlaceAbove(badge, menu.GetPreferredSize(Size.Empty), area);
                menu.Show(bounds.Location);
            }
            else
            {
                menu.Show(Cursor.Position);
            }
        }

        /// <summary>
        /// Where a list of <paramref name="menuSize"/> goes for a badge at <paramref name="badge"/>:
        /// above it, left edges aligned; below it when only below has room; otherwise pinned to the top
        /// of <paramref name="workingArea"/>. Always entirely inside the work area, at most its size.
        /// </summary>
        internal static Rectangle PlaceAbove(Rectangle badge, Size menuSize, Rectangle workingArea)
        {
            int width = Math.Min(menuSize.Width, workingArea.Width);
            int height = Math.Min(menuSize.Height, workingArea.Height);

            int x = Math.Clamp(badge.Left, workingArea.Left, workingArea.Right - width);

            int y;
            if (badge.Top - height >= workingArea.Top)
            {
                y = badge.Top - height;
            }
            else if (badge.Bottom + height <= workingArea.Bottom)
            {
                y = badge.Bottom;
            }
            else
            {
                y = workingArea.Top;
            }

            return new Rectangle(x, y, width, height);
        }

        /// <summary>How long after a click-away closes the list a badge click is taken as that same click.</summary>
        internal const long ReopenSuppressMs = 400;

        private static long _lastOutsideCloseTicks = long.MinValue / 2;

        /// <summary>
        /// True when the list was closed by a press outside it in the last <see cref="ReopenSuppressMs"/>.
        /// The close fires on the press and the badge's click on the release, so without this a click on
        /// the badge while the list is open would close it and immediately open it again.
        /// </summary>
        internal static bool ShouldSuppressReopen(long nowTicks, long lastOutsideCloseTicks) =>
            nowTicks - lastOutsideCloseTicks < ReopenSuppressMs;

        /// <summary><see cref="ShouldSuppressReopen(long, long)"/> against the last real close.</summary>
        internal static bool JustClosedByOutsideClick() =>
            ShouldSuppressReopen(Environment.TickCount64, _lastOutsideCloseTicks);

        private static void OpenUrl(string? url, string? repoFullName, Action<string>? log)
        {
            if (!IsAllowedRepoUrl(url, repoFullName))
            {
                log?.Invoke($"Refused to open a link that is not a GitHub page of {repoFullName}.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not open {url}: {ex.Message}");
            }
        }
    }
}
