using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The inbox panel painted exactly once and then went deaf.
    ///
    /// <c>#loadingState</c> was declared as a CHILD of <c>#messageList</c>. The last statement of
    /// <c>renderMessages()</c> is <c>list.innerHTML = html</c>, so the very first render deleted
    /// the loader. From the second call onward <c>document.getElementById('loadingState')</c>
    /// returned null and the bare deref <c>loading.style.display = 'none'</c> threw — near the TOP
    /// of the function, ahead of both the badge update and the list update.
    ///
    /// Measured live, on the Owner's inbox:
    ///
    ///     panel opened 14:43:40 with 55 unread -> badge painted 55
    ///     Mark Read clicked on 084e361d       -> user_inbox.read_at written at 21:57:09Z, unread 54 -> 53
    ///     badge still read 55, row still on screen, still offering its Mark Read button
    ///
    /// Everything downstream of the click was healthy — the WebView2 message reached C#, the broker
    /// ran, SQLite committed, and C# posted a correct fresh payload back. The page threw it away.
    /// Refresh, the Unread Only toggle and the optimistic local update were dead for the same
    /// reason: each one ends in <c>renderMessages()</c>, and <c>renderMessages()</c> could no
    /// longer survive its own first line. Nothing was logged, because a JS exception inside WebView2
    /// has no path to <c>DebugLogService</c>.
    ///
    /// It reported as "Mark Read doesn't work", and it outlived one round of fixes aimed at the
    /// fetch window, the filter mode and the counts — all of which were, by then, correct.
    ///
    /// WHY SOURCE-LEVEL ASSERTIONS: the render logic lives in inbox-panel.html and runs only inside
    /// a WebView2 browser environment driven by postMessage; there is no honest way to execute it
    /// in-process. Same technique and same rationale as
    /// <see cref="InboxPanelFetchWindowTests"/> — and the same stated limit: these catch someone
    /// REMOVING the fix, not a runtime behaviour change within it.
    /// </summary>
    public class InboxPanelRenderSurvivalTests
    {
        /// <summary>
        /// Repo root from THIS FILE's compile-time path — unaffected by the runner's working
        /// directory and correct inside a git worktree.
        /// </summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadPanelHtmlSource()
        {
            string path = Path.Combine(RepoRoot(), "InboxPanel", "inbox-panel.html");
            Assert.True(File.Exists(path), $"Could not locate inbox-panel.html at '{path}'.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The structural half of the fix. #messageList is declared EMPTY, so nothing a render can
        /// destroy is allowed to live inside it. Anything the panel needs on the next render —
        /// the loader, the empty state, the status line, the badge — must be a sibling.
        /// </summary>
        [Fact]
        public void Message_list_container_is_declared_empty_so_renders_destroy_nothing_reusable()
        {
            string html = ReadPanelHtmlSource();

            Assert.True(
                Regex.IsMatch(html, @"<div[^>]*id=""messageList""[^>]*>\s*</div>", RegexOptions.Singleline),
                "#messageList must be declared as an EMPTY container. renderMessages() assigns its " +
                "innerHTML, which deletes every descendant — so any element parked inside it is " +
                "alive for exactly one render. That is how #loadingState disappeared and took the " +
                "whole panel down with it on the following call.");
        }

        /// <summary>
        /// The specific element that caused it. Pinned by name, because the generic assertion above
        /// would still pass if someone re-nested the loader while leaving other markup between the
        /// tags — and the loader is the one whose absence is fatal rather than cosmetic.
        /// </summary>
        [Fact]
        public void Loading_state_is_a_sibling_of_the_message_list_not_a_child()
        {
            string html = ReadPanelHtmlSource();

            Assert.True(
                html.Contains(@"id=""loadingState"""),
                "#loadingState must still exist — the panel shows it before the first payload lands.");

            var container = Regex.Match(
                html,
                @"<div[^>]*id=""messageList""[^>]*>(?<body>.*?)</div>",
                RegexOptions.Singleline);

            Assert.True(container.Success, "Could not find the #messageList element in inbox-panel.html.");

            Assert.False(
                container.Groups["body"].Value.Contains("loadingState"),
                "#loadingState must NOT be nested inside #messageList. The first render replaces " +
                "messageList.innerHTML and deletes it; every render after that gets null from " +
                "getElementById and throws before it can repaint anything. Keep it alongside " +
                "#emptyState, which is a sibling for exactly this reason.");
        }

        /// <summary>
        /// The defensive half. Even with the loader parked outside the list, a bare deref is a
        /// single-point-of-failure for the entire panel: it sits above the badge and list updates,
        /// so ANY future null there silently freezes the UI while the data layer keeps working
        /// perfectly. Guarding it means a missing decoration costs a decoration, not the repaint.
        /// </summary>
        [Fact]
        public void Every_loader_dereference_in_render_is_null_guarded()
        {
            string html = ReadPanelHtmlSource();

            var derefs = Regex.Matches(html, @"^.*\bloading\.\w+.*$", RegexOptions.Multiline);

            Assert.True(
                derefs.Count > 0,
                "Expected renderMessages() to still hide the loader — if that line is gone entirely, " +
                "this test is no longer guarding anything and should be revisited deliberately.");

            foreach (Match line in derefs)
            {
                Assert.True(
                    line.Value.Contains("if (loading)"),
                    "Every dereference of the loader must be null-guarded. Unguarded here:\n" +
                    line.Value.Trim() +
                    "\nThis exact line threw on every render after the first and froze the panel at " +
                    "its opening paint, while Mark Read went on committing to SQLite unseen.");
            }
        }
    }
}
