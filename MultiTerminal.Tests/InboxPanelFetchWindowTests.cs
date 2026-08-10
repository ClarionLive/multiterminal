using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 46e61d12 — the inbox panel silently stranded most of the unread backlog.
    ///
    /// <c>SendInboxData</c> called <c>_broker.GetInbox(_defaultUserId)</c> and took the broker's
    /// defaults, which are <c>unreadOnly: false, limit: 50</c>. So the panel held the newest 50
    /// rows of ALL messages, while the badge rendered <c>unreadCount</c> — the TRUE total.
    /// Measured on a real inbox before the fix:
    ///
    ///     total rows 2951 | total unread 259 | panel held 50 rows, 46 of them unread
    ///     => 213 unread messages were UNREACHABLE: not visible, not individually clearable,
    ///        yet fully counted by the badge.
    ///
    /// It presented to the user as "Mark Read does nothing": the badge is capped at "99+", so
    /// 259 -> 258 never moves it, and one item leaving a dense list of 46 is easy to miss. The
    /// marks were landing all along — there was simply no signal saying so.
    ///
    /// Same defect class as GH#6 (ticket 2b202b9a): a counter and a list reading different data
    /// and disagreeing. The panel already RECEIVED <c>totalCount</c> and ignored it.
    ///
    /// WHY SOURCE-LEVEL ASSERTIONS: InboxPanelControl is a WinForms UserControl hosting WebView2
    /// and cannot be instantiated without a message loop and a browser environment, and the render
    /// logic lives in inbox-panel.html driven by WebView2 postMessage. There is no honest way to
    /// drive either in-process. Rather than fake coverage with a mock that proves nothing, these
    /// inspect the source — the technique this repo already uses in
    /// <see cref="InboxBadgeRecipientTests"/> and ProjectPanelCopyCompletenessTests, for the same
    /// reason and the same "silence in the UI layer" failure mode.
    ///
    /// CONSEQUENCE, STATED PLAINLY: these catch someone REMOVING the fix. They do not catch a
    /// runtime behaviour change within it. Live verification is what covers that.
    /// </summary>
    public class InboxPanelFetchWindowTests
    {
        /// <summary>
        /// Repo root from THIS FILE's compile-time path — unaffected by the runner's working
        /// directory and correct inside a git worktree.
        /// </summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadPanelControlSource()
        {
            string path = Path.Combine(RepoRoot(), "InboxPanel", "InboxPanelControl.cs");
            Assert.True(File.Exists(path), $"Could not locate InboxPanelControl.cs at '{path}'.");
            return File.ReadAllText(path);
        }

        private static string ReadPanelHtmlSource()
        {
            string path = Path.Combine(RepoRoot(), "InboxPanel", "inbox-panel.html");
            Assert.True(File.Exists(path), $"Could not locate inbox-panel.html at '{path}'.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// THE CORE REGRESSION GUARD. A bare <c>GetInbox(_defaultUserId)</c> silently inherits
        /// <c>limit: 50</c>, which is exactly what stranded 213 unread messages. The call must pass
        /// its arguments explicitly so the window is a decision, not an inherited accident.
        /// </summary>
        [Fact]
        public void Inbox_fetch_passes_explicit_arguments_and_never_relies_on_GetInbox_defaults()
        {
            string src = ReadPanelControlSource();

            var bareCall = new Regex(@"GetInbox\s*\(\s*_defaultUserId\s*\)");
            Assert.False(
                bareCall.IsMatch(src),
                "InboxPanelControl calls GetInbox(_defaultUserId) with no further arguments, which " +
                "silently inherits the broker's defaults (unreadOnly: false, limit: 50). That is the " +
                "46e61d12 defect: the panel holds a window of recent traffic while the badge counts " +
                "the whole backlog, leaving most unread messages unreachable. Pass unreadOnly and " +
                "limit explicitly.");

            Assert.True(
                Regex.IsMatch(src, @"GetInbox\s*\([^)]*unreadOnly\s*:", RegexOptions.Singleline),
                "The inbox fetch must pass unreadOnly explicitly so the query follows the panel's " +
                "filter — otherwise unread mode can only ever show whichever unread rows happen to " +
                "fall inside a window of recent traffic.");

            Assert.True(
                Regex.IsMatch(src, @"GetInbox\s*\([^)]*limit\s*:", RegexOptions.Singleline),
                "The inbox fetch must pass limit explicitly rather than inheriting the default of 50.");
        }

        /// <summary>
        /// The cap must be big enough to cover a realistic unread backlog. 50 was the original
        /// value and is the specific number that caused the defect; anything in that neighbourhood
        /// reintroduces it.
        /// </summary>
        [Fact]
        public void Inbox_fetch_limit_is_large_enough_to_cover_a_real_backlog()
        {
            string src = ReadPanelControlSource();

            var m = Regex.Match(src, @"InboxFetchLimit\s*=\s*(\d+)");
            Assert.True(m.Success,
                "Expected a named InboxFetchLimit constant in InboxPanelControl. A named constant is " +
                "what makes the window reviewable; an inline literal is how the 50 went unnoticed.");

            int limit = int.Parse(m.Groups[1].Value);
            Assert.True(limit >= 250,
                $"InboxFetchLimit is {limit}. The inbox that exposed this defect had 259 unread, so a " +
                "cap at or below that reintroduces the stranding this ticket fixed. Raise it, or add " +
                "pagination and update this test to match the new design.");
        }

        /// <summary>
        /// The filter is what makes the query correct, so the panel has to tell C# about it and C#
        /// has to act on it. Without the round-trip, the mode toggle is cosmetic.
        /// </summary>
        [Fact]
        public void Filter_mode_round_trips_between_panel_and_host()
        {
            string cs = ReadPanelControlSource();
            string html = ReadPanelHtmlSource();

            // Assert on the CASE LABEL, not on the bare quoted string. The first version of this
            // test used Contains("\"set_filter\"") and was NOT load-bearing: the explanatory comment
            // above the switch mentions "set_filter" in quotes, so the assertion passed against a
            // build where the handler had been renamed away. Caught by the falsifiability run —
            // which is the entire reason for doing one.
            Assert.True(
                Regex.IsMatch(cs, @"case\s+""set_filter""\s*:"),
                "InboxPanelControl must handle a 'set_filter' case — toggling Unread Only changes the " +
                "QUERY now, not just a client-side view of one payload. (Assert on the case label: a " +
                "comment mentioning the name must not be able to satisfy this.)");

            Assert.True(
                Regex.IsMatch(cs, @"TryGetProperty\s*\(\s*""unreadOnly""\s*,\s*out", RegexOptions.Singleline),
                "InboxPanelControl must read the panel's reported unreadOnly mode, otherwise the fetch " +
                "cannot follow the view the user is actually looking at.");

            Assert.True(
                Regex.IsMatch(html, @"postMessage\s*\(\s*\{\s*type:\s*'set_filter'"),
                "toggleFilter() must POST a set_filter message and trigger a re-fetch. Re-filtering the " +
                "existing payload client-side is precisely what stranded the unread backlog. (Assert on " +
                "the postMessage call, not a bare string that a comment could contain.)");

            Assert.True(
                Regex.IsMatch(html, @"type:\s*'ready',\s*unreadOnly", RegexOptions.Singleline),
                "The initial 'ready' message must carry the filter mode, or the very first fetch is " +
                "for the wrong query.");
        }

        /// <summary>
        /// A capped list that says nothing is claiming to be complete. This is the honesty half of
        /// the fix, and it is the part that would have made the original symptom self-explanatory.
        /// </summary>
        [Fact]
        public void Truncated_fetch_is_disclosed_to_the_user()
        {
            string cs = ReadPanelControlSource();
            string html = ReadPanelHtmlSource();

            Assert.True(
                Regex.IsMatch(cs, @"truncated\s*="),
                "The payload must carry a 'truncated' flag. The panel cannot infer truncation " +
                "reliably by comparing counts, because an optimistic mark-read makes them differ " +
                "for a moment.");

            Assert.True(
                html.Contains("inboxStatus"),
                "inbox-panel.html must render a truncation notice element.");

            Assert.True(
                Regex.IsMatch(html, @"fetchTruncated\s*&&"),
                "The truncation notice must be driven by the server's truncated flag, not by a count " +
                "comparison that transiently disagrees after an optimistic mark-read.");

            Assert.True(
                Regex.IsMatch(html, @"Showing \$\{shown\} of \$\{universe\}"),
                "The notice must state both how many rows are shown and how many exist. 'Showing some' " +
                "is the same lie as showing nothing.");
        }

        /// <summary>
        /// NEGATIVE FIXTURE. The panel is only honest if the totals it reports come from the host's
        /// count of the whole inbox, not from the length of the window it happens to hold. If a
        /// future edit "simplifies" these to messages.length, the badge and the notice would agree
        /// with each other and both be wrong — the exact failure mode of GH#6.
        /// </summary>
        [Fact]
        public void Counts_come_from_the_host_not_from_the_loaded_window()
        {
            string cs = ReadPanelControlSource();
            string html = ReadPanelHtmlSource();

            Assert.True(
                cs.Contains("unreadCount = result.UnreadCount") && cs.Contains("totalCount = result.TotalCount"),
                "The payload's counts must come from the broker's result, which counts the whole " +
                "inbox — not from the size of the fetched window.");

            Assert.True(
                Regex.IsMatch(html, @"unreadCount\s*=\s*data\.unreadCount") &&
                Regex.IsMatch(html, @"totalCount\s*=\s*data\.totalCount"),
                "The panel must take its counts from the host payload. Deriving them from " +
                "messages.length would make the badge agree with a truncated list and hide the " +
                "backlog again — that is GH#6's defect wearing different clothes.");
        }
    }
}
