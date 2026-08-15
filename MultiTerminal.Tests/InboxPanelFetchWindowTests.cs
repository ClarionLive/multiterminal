using System;
using System.Data.SQLite;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
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
        /// Strips // line comments, /* block */ comments and &lt;!-- HTML comments --&gt; before asserting.
        ///
        /// This is the ④ lesson made mechanical. The comments in these files QUOTE THE CODE they
        /// explain — several of them contain the exact strings these tests match on — so an
        /// assertion run against raw source can be satisfied by prose describing code that was
        /// deleted. The better the comment, the likelier the false pass. Strip first, then assert,
        /// and a test can only be satisfied by real code.
        ///
        /// Used for negative fixtures too: a comment matching a forbidden shape there would cause a
        /// false FAILURE rather than a false pass — the safe direction, but still a wrong red.
        /// Stripping keeps both polarities keyed on code alone.
        /// </summary>
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            source = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
            return source;
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
                "setUnreadFilter() must POST a set_filter message and trigger a re-fetch. Re-filtering the " +
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

            // Anchored on ASSIGNMENT SYNTAX, not on the bare identifier text. A quoted identifier
            // inside a comment satisfies Contains(), and the comments in this very file quote code
            // — so the weaker form would pass against deleted assignments (the ④ lesson).
            Assert.True(
                Regex.IsMatch(cs, @"unreadCount\s*=\s*result\.UnreadCount\s*,") &&
                Regex.IsMatch(cs, @"totalCount\s*=\s*result\.TotalCount\s*,"),
                "The payload's counts must come from the broker's result, which counts the whole " +
                "inbox — not from the size of the fetched window.");

            Assert.True(
                Regex.IsMatch(html, @"unreadCount\s*=\s*data\.unreadCount") &&
                Regex.IsMatch(html, @"totalCount\s*=\s*data\.totalCount"),
                "The panel must take its counts from the host payload. Deriving them from " +
                "messages.length would make the badge agree with a truncated list and hide the " +
                "backlog again — that is GH#6's defect wearing different clothes.");
        }

        /// <summary>
        /// PIPELINE RUN 1, F2 — converged independently by three gates (Codex cross-model adversary,
        /// Claude code-reviewer, Claude debugger).
        ///
        /// The OPTIMISTIC paths are the other half of the invariant above, and they were the half
        /// that still got it wrong. `messages` is a CAPPED WINDOW; recomputing a global count from
        /// it collapses unreadCount to the window size (with 900 unread, one click showed 499) and,
        /// because `universe > shown` then goes false, silently hides the truncation notice too.
        /// Same window-vs-universe conflation the ticket fixed one layer up in GetInbox.TotalCount.
        ///
        /// Asserted as an INVARIANT over ALL optimistic sites rather than against the two lines the
        /// reports named: a future third optimistic path would otherwise reintroduce it unnoticed.
        /// </summary>
        [Fact]
        public void Optimistic_updates_decrement_the_count_they_never_recompute_it_from_the_window()
        {
            string html = ReadPanelHtmlSource();
            string code = StripComments(html);

            // Positive: both optimistic sites decrement the host-supplied value.
            int decrements = Regex.Matches(code, @"unreadCount\s*=\s*Math\.max\(\s*0\s*,\s*unreadCount\s*-\s*1\s*\)").Count;
            Assert.True(
                decrements >= 2,
                $"Expected both optimistic paths (markRead and sendReply) to DECREMENT the " +
                $"host-supplied unreadCount; found {decrements} decrement site(s). Recomputing a " +
                $"global count from the capped window is the defect this ticket exists to remove.");

            // Negative fixture: the recompute-from-window shape must not exist anywhere.
            Assert.False(
                Regex.IsMatch(code, @"unreadCount\s*=\s*messages\s*\.\s*filter\("),
                "An optimistic path is deriving the GLOBAL unread count from `messages`, which is a " +
                "capped window, not the unread universe. That drops the badge to the window size and " +
                "hides the truncation disclosure with it.");
        }

        /// <summary>
        /// PIPELINE RUN 1, F1 — found by the debugger and the code-reviewer independently.
        ///
        /// "Use Mark All Read to clear the rest" is only TRUE in unread mode, where `universe` is
        /// unreadCount. In all-mode `universe` is totalCount, which marking-read does not reduce —
        /// so the banner never clears and its instruction provably does nothing. A disclosure
        /// feature that gives false guidance is worse than one that gives none.
        /// </summary>
        [Fact]
        public void Truncation_remedy_is_offered_only_in_the_mode_where_it_would_work()
        {
            string code = StripComments(ReadPanelHtmlSource());

            Assert.True(
                Regex.IsMatch(code, @"const\s+remedy\s*=\s*showUnreadOnly\s*\?"),
                "The 'Mark All Read' remedy must be branched on showUnreadOnly, not appended to " +
                "both modes.");

            // The remedy must NOT be baked into the unconditional template literal.
            Assert.False(
                Regex.IsMatch(code, @"not listed\.\s*Use Mark All Read"),
                "The remedy sentence is still hard-coded into the notice for both modes. In all-mode " +
                "it is inert and the banner is permanent.");
        }

        /// <summary>
        /// PIPELINE RUN 1, F3 — found only by the debugger.
        ///
        /// GetInbox turns EVERY exception into Success=false, and the old code had no `else`: no
        /// log, no push, no retry. After a set_filter that leaves the checkbox already flipped while
        /// the list on screen is still the previous mode's payload, with nothing to correct it —
        /// item ⑤'s original complaint (the control disagreeing with the list) returning through the
        /// error path. The failure push must therefore still echo the host's mode, and the panel
        /// must render it as a FAILURE rather than as an empty inbox.
        /// </summary>
        [Fact]
        public void Failed_fetch_still_pushes_the_mode_and_is_not_rendered_as_an_empty_inbox()
        {
            string cs = StripComments(ReadPanelControlSource());
            string html = StripComments(ReadPanelHtmlSource());

            Assert.True(
                Regex.IsMatch(cs, @"else\s*\{[^}]*DebugLogService\s*\?\s*\.\s*Error", RegexOptions.Singleline),
                "A failed inbox fetch must be logged. Success=false with no else is invisible twice: " +
                "nothing logged and nothing sent.");

            Assert.True(
                Regex.IsMatch(cs, @"error\s*=\s*result\.Error"),
                "The failure payload must carry the broker's error so the panel can say what went wrong.");

            Assert.True(
                Regex.Matches(cs, @"unreadOnly\s*=\s*_showUnreadOnly").Count >= 2,
                "BOTH the success and failure payloads must echo the host's filter mode — otherwise a " +
                "failed fetch after a toggle leaves the checkbox describing data that was never loaded.");

            Assert.True(
                Regex.IsMatch(html, @"fetchError\s*=\s*typeof\s+data\.error"),
                "The panel must adopt the error field from the payload.");

            Assert.True(
                Regex.IsMatch(html, @"if\s*\(\s*fetchError\s*\)"),
                "The panel must branch on the error before the truncation branch — a failed fetch " +
                "rendered as 'No inbox messages' is a plausible wrong answer, which is the silent " +
                "failure class this ticket exists to remove.");
        }

        /// <summary>
        /// Item ⑤. The filter control must be a CHECKBOX, because the button it replaced was
        /// unreadable: its label named the ACTION ("Show All" while you were in unread mode), which
        /// reads naturally as a label naming the STATE. With no other mode indicator on the panel, a
        /// correctly-working filter and a broken one looked identical — and since "all" mode
        /// legitimately keeps read rows in the list, Mark Read appeared to do nothing. The Owner hit
        /// exactly this and reported the fix as non-functional when the query layer was fine.
        ///
        /// Asserting the OLD control is gone matters as much as asserting the new one exists: a
        /// well-meaning revert to a compact toggle reintroduces the ambiguity without breaking
        /// anything a behavioural test would notice.
        /// </summary>
        [Fact]
        public void Filter_control_is_a_checkbox_whose_state_is_the_mode()
        {
            string html = ReadPanelHtmlSource();

            Assert.True(
                Regex.IsMatch(html, @"<input[^>]*type=""checkbox""[^>]*id=""filterUnread""", RegexOptions.Singleline),
                "The unread filter must be a checkbox with id 'filterUnread' — the control's state " +
                "must BE the mode, not something the user has to infer from a verb on a button.");

            Assert.True(
                Regex.IsMatch(html, @"onchange=""setUnreadFilter\(this\.checked\)"""),
                "The checkbox must drive setUnreadFilter(this.checked) — the handler follows the " +
                "control rather than flipping a separate boolean that can drift out of sync with it.");

            // NEGATIVE FIXTURE: the ambiguous control must not come back.
            Assert.False(
                html.Contains("toggleFilter") || html.Contains("filterToggle"),
                "The old action-labelled toggle button must stay gone. It is the control that made " +
                "a working filter indistinguishable from a broken one.");
        }

        /// <summary>
        /// Item ⑤, second half. C# echoes <c>unreadOnly</c> back on every push, so the checkbox must
        /// be re-synced from the payload — otherwise the box can say one thing while the list on
        /// screen is the result of a different query. A checkbox that lies about the mode is worse
        /// than the button it replaced, because it looks authoritative.
        /// </summary>
        [Fact]
        public void Filter_checkbox_is_resynced_from_the_host_payload()
        {
            string html = ReadPanelHtmlSource();

            Assert.True(
                Regex.IsMatch(html, @"function\s+syncFilterCheckbox\s*\(", RegexOptions.Singleline),
                "A single sync helper must own writing showUnreadOnly back onto the checkbox.");

            // The adopt-from-payload block must actually call it. Assert on the assignment and the
            // call together so a comment mentioning the name cannot satisfy this (the ④ lesson).
            Assert.True(
                Regex.IsMatch(
                    html,
                    @"showUnreadOnly\s*=\s*data\.unreadOnly\s*;\s*syncFilterCheckbox\s*\(\s*\)\s*;",
                    RegexOptions.Singleline),
                "After adopting the host's unreadOnly the panel must re-sync the checkbox, or the " +
                "control and the query that produced the visible list can disagree.");
        }

        /// <summary>
        /// Item ⑥, source-level half. <c>TotalCount = messages.Count</c> made the field a synonym
        /// for "rows returned", which is why the truncation notice could never fire: the panel asks
        /// "is the list short of the total?" and was handed two copies of the same number.
        /// </summary>
        [Fact]
        public void Broker_total_count_is_not_the_returned_row_count()
        {
            string path = Path.Combine(RepoRoot(), "MCPServer", "Services", "MessageBroker.cs");
            Assert.True(File.Exists(path), $"Could not locate MessageBroker.cs at '{path}'.");
            string cs = File.ReadAllText(path);

            Assert.True(
                Regex.IsMatch(cs, @"TotalCount\s*=\s*totalCount"),
                "GetInbox must report a real total, not the size of the page it just fetched.");

            Assert.True(
                Regex.IsMatch(cs, @"GetInboxTotalCount\s*\(\s*userId\s*\)"),
                "The total must come from a COUNT over the whole inbox, mirroring GetInboxUnreadCount.");

            Assert.False(
                Regex.IsMatch(cs, @"TotalCount\s*=\s*messages\.Count"),
                "REGRESSION: TotalCount = messages.Count reintroduces the defect that made the " +
                "'showing N of M' disclosure structurally unable to render.");
        }
    }

    /// <summary>
    /// Item ⑥, behavioural half — the one assertion in this ticket that is NOT source-level.
    ///
    /// <see cref="TaskDatabase.GetInboxTotalCount"/> is plain data access with no UI in the way, so
    /// it can be tested honestly against a real SQLite file. This is the test that would have caught
    /// the original defect: it seeds MORE rows than the fetch limit and asserts the reported total
    /// exceeds the number of rows handed back. Under the old <c>TotalCount = messages.Count</c> the
    /// two were equal by construction, so no arrangement of data could have failed it.
    /// </summary>
    public sealed class InboxTotalCountTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly TaskDatabase _taskDb;

        public InboxTotalCountTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_inboxtotal_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _taskDb = new TaskDatabase();
        }

        public void Dispose()
        {
            _taskDb?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var p in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        private void Seed(string userId, int count, bool read)
        {
            for (int i = 0; i < count; i++)
            {
                _taskDb.SaveInboxMessage(new InboxMessage
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    UserId = userId,
                    TaskId = "task0001",   // user_inbox.task_id is NOT NULL
                    TaskTitle = "seeded task",
                    Type = "test",
                    Summary = $"seeded {i}",
                    CreatedAt = DateTime.UtcNow.AddSeconds(-i),
                    CreatedBy = "test",
                    ReadAt = read ? DateTime.UtcNow : (DateTime?)null
                });
            }
        }

        /// <summary>
        /// THE CORE GUARD. Total must describe the inbox, not the window.
        /// </summary>
        [Fact]
        public void Total_count_reflects_the_whole_inbox_not_the_fetch_window()
        {
            const int seeded = 40;
            const int fetchLimit = 10;
            Seed("Tester", seeded, read: true);

            var page = _taskDb.GetInboxMessages("Tester", unreadOnly: false, limit: fetchLimit);
            int total = _taskDb.GetInboxTotalCount("Tester");

            Assert.Equal(fetchLimit, page.Count);   // the fetch really was capped...
            Assert.Equal(seeded, total);            // ...and the total is unmoved by that cap
            Assert.True(
                total > page.Count,
                "With more rows than the limit, the total MUST exceed the returned page — this is " +
                "precisely the comparison the truncation notice makes, and the old code made it " +
                "impossible by defining the total as the page size.");
        }

        /// <summary>
        /// Total counts read and unread alike, and is not a second unread count. Guards a plausible
        /// mis-fix: copying GetInboxUnreadCount and forgetting to drop its read_at predicate.
        /// </summary>
        [Fact]
        public void Total_count_includes_read_messages_and_is_scoped_per_user()
        {
            Seed("Tester", 7, read: true);
            Seed("Tester", 3, read: false);
            Seed("Someone-Else", 5, read: false);

            Assert.Equal(10, _taskDb.GetInboxTotalCount("Tester"));      // 7 read + 3 unread
            Assert.Equal(3, _taskDb.GetInboxUnreadCount("Tester"));      // unread only
            Assert.Equal(5, _taskDb.GetInboxTotalCount("Someone-Else")); // no bleed across users
            Assert.Equal(0, _taskDb.GetInboxTotalCount("Nobody"));
        }
    }
}
