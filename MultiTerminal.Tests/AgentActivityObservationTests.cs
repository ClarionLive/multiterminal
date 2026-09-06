using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task edcdcdd5 — does the rail tell the truth?
    ///
    /// <para>WHAT WENT WRONG. Task 2289bb8a shipped a complete attention state machine whose clear
    /// path, <c>NoteObservedActivity</c>, had NO CALLER. The consequences were both visible on the
    /// owner's screen at the same moment: one card read "Finished and idle" for 27 minutes while
    /// that agent was working (because <c>Working</c> is only reachable through the uncalled method,
    /// so the state could only ever be moved by a notification), and another pulsed "needs
    /// permission" for 49 minutes after the owner had pressed Escape on it.</para>
    ///
    /// <para>Every test in this file pins a rule whose violation is SILENT. None of them would have
    /// been caught by a build, and 679 green tests coexisted with both failures above.</para>
    ///
    /// <para>Kept in its own file rather than appended to <see cref="AgentAttentionServiceTests"/>
    /// deliberately: ticket cafd47b9 is in flight and appends to that file, and two branches
    /// appending to one file is a merge conflict for no benefit.</para>
    /// </summary>
    public class AgentActivityObservationTests
    {
        private const string Session = "sess-1";

        private static Dictionary<string, object> Notification(string rawType = "permission_prompt")
            => new Dictionary<string, object>
            {
                ["session_id"] = Session,
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = "Alice needs permission to continue",
            };

        // ── The Escape case ──────────────────────────────────────────────

        /// <summary>
        /// THE ONE THE OWNER REPORTED. The 2289bb8a spike concluded <c>UserPromptSubmit</c> was the
        /// clear-edge; the owner disproved it by pressing Escape, which submits no prompt, leaving
        /// the card pulsing for 49 more minutes.
        /// <para>
        /// Escape ends the TURN, so <c>Stop</c> fires. If this goes red, dismissed prompts have
        /// started pulsing forever again — fix the service, not the test.
        /// </para>
        /// </summary>
        [Fact]
        public void A_turn_ending_clears_a_block_the_owner_dismissed_without_answering()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());
            Assert.True(svc.Get(Session).IsBlocking);

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false);

            var after = svc.Get(Session);
            Assert.Equal(AttentionState.Idle, after.State);
            Assert.False(after.IsBlocking);
        }

        /// <summary>
        /// Idle is not a block, so it must not pulse — the same rule that keeps <c>idle_prompt</c>
        /// quiet. An alert that fires for every agent that ever finished work is an alert nobody
        /// reads.
        /// </summary>
        [Fact]
        public void A_turn_that_ended_is_not_a_block()
        {
            var svc = new AgentAttentionService();
            svc.NoteTurnEnded(Session, DateTime.UtcNow, isSubagent: false);

            Assert.False(svc.Get(Session).IsBlocking);
        }

        /// <summary>
        /// A subagent finishing says nothing about whether its PARENT is still waiting on the owner.
        /// Subagents get their own <c>Stop</c> and are logged under the parent's name.
        /// </summary>
        [Fact]
        public void A_subagents_turn_ending_does_not_clear_its_parents_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: true);

            Assert.True(svc.Get(Session).IsBlocking);
        }

        /// <summary>
        /// A turn that ended BEFORE the block was raised is an older event arriving late, not the
        /// owner dismissing this prompt. Clearing on it would clear a live block with evidence that
        /// predates it.
        /// </summary>
        [Fact]
        public void A_turn_that_ended_before_the_block_does_not_clear_it()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddMinutes(-5), isSubagent: false);

            Assert.True(svc.Get(Session).IsBlocking);
        }

        // ── The live activity line ───────────────────────────────────────

        /// <summary>
        /// The live line is what replaces the fossil. Previously the card rendered the notification
        /// message — captured at block time, never regenerated — in the position of a live
        /// observation, so the owner read a 27-minute-old sentence as a current one.
        /// </summary>
        [Fact]
        public void Observed_activity_records_a_live_line_with_its_own_timestamp()
        {
            var svc = new AgentAttentionService();
            var at = DateTime.UtcNow;

            svc.NoteObservedActivity(Session, at, isSubagent: false, toolUseId: null,
                activitySummary: "Edit: MainForm.cs");

            var e = svc.Get(Session);
            Assert.Equal("Edit: MainForm.cs", e.LastActivity);
            Assert.Equal(at, e.LastActivityAtUtc);
        }

        /// <summary>
        /// THE DISPLAY-ONLY PATH MUST NOT CLEAR. <c>TOOL_START</c> is written on <c>PreToolUse</c>,
        /// and <c>safety-hook.js</c> is itself a PreToolUse hook returning
        /// <c>permissionDecision: 'ask'</c> — so it runs BEFORE the prompt it causes. Routing it
        /// through the clear path would clear a block at the instant of its creation.
        /// <para>This is finding 1 of the spike and the easiest rule here to "simplify" into a bug.</para>
        /// </summary>
        [Fact]
        public void The_display_only_path_updates_the_line_without_clearing_the_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());

            svc.NoteActivityLineOnly(Session, "Bash: dotnet build", DateTime.UtcNow.AddSeconds(5));

            var e = svc.Get(Session);
            Assert.True(e.IsBlocking);
            Assert.Equal("Bash: dotnet build", e.LastActivity);
        }

        /// <summary>
        /// An older observation must not overwrite a newer one. The writer is a separate Node
        /// process with its own clock, so out-of-order rows are possible, and a line that goes
        /// backwards looks exactly like the agent repeating itself.
        /// </summary>
        [Fact]
        public void An_older_observation_does_not_overwrite_a_newer_line()
        {
            var svc = new AgentAttentionService();
            var now = DateTime.UtcNow;

            svc.NoteActivityLineOnly(Session, "newer", now);
            svc.NoteActivityLineOnly(Session, "older", now.AddSeconds(-30));

            Assert.Equal("newer", svc.Get(Session).LastActivity);
        }

        /// <summary>
        /// Subagent activity updates NOTHING — not the state, and not the display line. Attributing
        /// a subagent's tool call to its parent puts a confident, wrong sentence exactly where the
        /// live observation goes.
        /// </summary>
        [Fact]
        public void Subagent_activity_updates_neither_the_state_nor_the_line()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());

            svc.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: true,
                toolUseId: null, activitySummary: "Edit: something.cs");

            var e = svc.Get(Session);
            Assert.True(e.IsBlocking);
            Assert.Null(e.LastActivity);
        }

        /// <summary>
        /// The line changing is itself worth announcing. Without this, an agent already in
        /// <c>Working</c> raises no event as it moves from tool to tool — which is the ONE thing the
        /// owner actually asked for: cards "ALWAYS updating with what's happening in the terminal".
        /// </summary>
        [Fact]
        public void A_new_activity_line_raises_AttentionChanged_even_when_the_state_is_unchanged()
        {
            var svc = new AgentAttentionService();
            svc.NoteObservedActivity(Session, DateTime.UtcNow, isSubagent: false, toolUseId: null,
                activitySummary: "Edit: a.cs");

            int fired = 0;
            svc.AttentionChanged += (s, e) => fired++;

            svc.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false,
                toolUseId: null, activitySummary: "Edit: b.cs");

            Assert.Equal(1, fired);
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
        }

        /// <summary>
        /// <c>activity_feed.actor</c> is an agent NAME while the cache is keyed by session id, so
        /// the watcher has to resolve one to the other.
        /// </summary>
        [Fact]
        public void GetByAgent_finds_the_entry_for_an_agent_name()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification());

            var found = svc.GetByAgent("Alice");

            Assert.NotNull(found);
            Assert.Equal(Session, found.SessionId);
            Assert.Null(svc.GetByAgent("Nobody"));
        }

        // ── Subagent detection, where the safe default lives ─────────────

        /// <summary>
        /// THE POLARITY TEST, and the most important one in this file.
        ///
        /// <para>A row that cannot say whether it came from a subagent must be treated as though it
        /// DID. The failure modes are not symmetric: refusing to clear leaves a stale pulse the
        /// owner can see and dismiss, whereas clearing wrongly renders a CALM CARD — indistinguishable
        /// from nobody needing you, and therefore silent. Subagent tool calls are logged under the
        /// parent's name and were measured at 12,544 of 64,444 PreToolUse events (19.5%), so this is
        /// not a rare edge.</para>
        ///
        /// <para>The subtle half: an explicit <c>"agent_id": null</c> means "the hook looked, and it
        /// was the main thread", while a MISSING key means "this row predates provenance and cannot
        /// say". Conflating them inverts the safe default on exactly the historical data — every row
        /// written before the hook change is the second kind.</para>
        /// </summary>
        [Theory]
        [InlineData(null, true)]                                    // no details at all
        [InlineData("", true)]                                      // empty
        [InlineData("not json", true)]                              // unparseable
        [InlineData("[1,2,3]", true)]                               // not an object
        [InlineData("{\"tool\":\"Edit\"}", true)]                   // predates provenance: cannot say
        [InlineData("{\"tool\":\"Edit\",\"agent_id\":null}", false)] // looked, and it was main thread
        [InlineData("{\"agent_id\":\"\"}", false)]                  // present but blank: main thread
        [InlineData("{\"agent_id\":\"sub-7\"}", true)]              // a real subagent
        public void Subagent_detection_defaults_to_the_safe_answer(string detailsJson, bool expected)
        {
            Assert.Equal(expected, AgentActivityWatcher.LooksLikeSubagent(detailsJson));
        }

        // ── Is it wired? ─────────────────────────────────────────────────

        /// <summary>
        /// THE GUARD FOR THE ACTUAL FAILURE OF 2289bb8a.
        ///
        /// <para>That ticket built a correct, fully-tested state machine and never called it. The
        /// clear path had zero callers, so blocks set and stayed set and <c>Working</c> was
        /// unreachable — and nothing failed, because every test verified what happens once the
        /// method is invoked. The same file could exist again tomorrow with the construction in
        /// MainForm deleted, every test in this file would still pass, and the rail would silently
        /// go back to lying.</para>
        ///
        /// <para>So: assert MainForm constructs it AND starts it AND disposes it. Constructing
        /// without starting is the same bug wearing a better disguise.</para>
        ///
        /// <para>Source-level, over comment-stripped text so prose naming the class cannot satisfy
        /// it — the same discipline as <c>BoardHudDoorwayTests</c> and
        /// <c>DashboardHeaderDoorwayTests</c>, and for the same reason: this comment mentions
        /// <c>AgentActivityWatcher</c> repeatedly.</para>
        /// </summary>
        [Fact]
        public void MainForm_constructs_starts_and_disposes_the_activity_watcher()
        {
            string src = ReadMainFormStripped();

            Assert.Contains("new MCPServer.Services.AgentActivityWatcher(", src, StringComparison.Ordinal);
            Assert.Contains("StartAgentActivityWatcher();", src, StringComparison.Ordinal);
            Assert.Contains("watcher.Start();", src, StringComparison.Ordinal);
            Assert.Contains("_agentActivityWatcher = watcher;", src, StringComparison.Ordinal);
            Assert.Contains("_agentActivityWatcher?.Dispose();", src, StringComparison.Ordinal);
        }

        private static string ReadMainFormStripped()
        {
            string here = System.IO.Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(here, "..", "MainForm.cs"));
            Assert.True(System.IO.File.Exists(path), $"Could not locate MainForm.cs at '{path}'.");

            string src = System.IO.File.ReadAllText(path);
            string noBlocks = System.Text.RegularExpressions.Regex.Replace(
                src, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

            return string.Join(
                "\n",
                System.Linq.Enumerable.Where(
                    noBlocks.Split('\n'),
                    l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;
    }
}
