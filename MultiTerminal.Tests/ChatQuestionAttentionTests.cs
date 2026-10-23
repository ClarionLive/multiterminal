using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.AttentionPanel;
using MultiTerminal.Controls;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 3d51315b: an agent that asks the owner a question in chat, or simply ends its turn, must
    /// not read "Finished and idle" on the attention rail. The Owner's report: Alice asked a question
    /// in plain text and her card sat calm while "Needs you" was set to a beacon.
    /// </summary>
    public class ChatQuestionDetectorTests
    {
        private static string Assistant(string text, bool sidechain = false) =>
            "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false")
            + ",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
            + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

        private static string AssistantToolUse() =>
            "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{}}]}}";

        private static string ToolResult() =>
            "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]}}";

        private static string OwnerPrompt(string text) =>
            "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":"
            + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

        [Fact]
        public void A_last_paragraph_that_asks_is_the_question_without_its_markup()
        {
            string text = "I checked both issues.\n\n**#26 is fixed.**\n\nShould I put this on the board? And do you want `Your turn` quiet?";

            Assert.Equal("Should I put this on the board? And do you want Your turn quiet?",
                ChatQuestionDetector.FindQuestion(text));
        }

        [Fact]
        public void A_question_only_in_an_earlier_paragraph_is_not_the_turn_ending_on_one()
        {
            string text = "Why did the card not beacon? Because nothing told it to.\n\nI'll build the fix next.";

            Assert.Null(ChatQuestionDetector.FindQuestion(text));
        }

        [Fact]
        public void A_question_mark_inside_code_or_a_url_is_not_a_question()
        {
            Assert.Null(ChatQuestionDetector.FindQuestion("Run this:\n\n```\nwhere.exe node?\n```"));
            Assert.Null(ChatQuestionDetector.FindQuestion("See https://example.com/page?id=3 for the details."));
        }

        [Fact]
        public void A_long_question_is_cut_to_the_card_length()
        {
            string q = new string('a', 400) + "?";
            string found = ChatQuestionDetector.FindQuestion(q);

            Assert.Equal(ChatQuestionDetector.MaxQuestionLength, found.Length);
            Assert.EndsWith("…", found, StringComparison.Ordinal);
        }

        [Fact]
        public void The_final_text_is_the_last_main_thread_assistant_text_after_tool_calls()
        {
            var lines = new List<string>
            {
                OwnerPrompt("check the issues"),
                Assistant("Looking now."),
                AssistantToolUse(),
                ToolResult(),
                Assistant("Done. Want me to close them?"),
                Assistant("a subagent's own words?", sidechain: true),
            };

            Assert.Equal("Done. Want me to close them?", ChatQuestionDetector.FinalAssistantText(lines));
        }

        [Fact]
        public void A_turn_with_no_text_does_not_inherit_the_previous_turns_question()
        {
            // Escape ends the turn before any text: the question above the owner's prompt was
            // answered by that prompt, so it must not be raised again.
            var lines = new List<string>
            {
                Assistant("Shall I start?"),
                OwnerPrompt("yes"),
                AssistantToolUse(),
                ToolResult(),
            };

            Assert.Null(ChatQuestionDetector.FinalAssistantText(lines));
        }

        [Fact]
        public void A_transcript_that_ends_in_a_tool_call_or_result_has_no_final_text_yet()
        {
            // The Stop hook's row can be read before the turn's last line is written. Walking past
            // the tool result would land on a question from the middle of the turn (pipeline run 1).
            var lines = new List<string>
            {
                OwnerPrompt("fix it"),
                Assistant("Is it the cache? Checking."),
                AssistantToolUse(),
                ToolResult(),
            };
            Assert.Null(ChatQuestionDetector.FinalAssistantText(lines));

            lines.RemoveAt(lines.Count - 1);
            Assert.Null(ChatQuestionDetector.FinalAssistantText(lines));
        }

        [Fact]
        public void A_torn_line_is_skipped_not_fatal()
        {
            var lines = new List<string> { Assistant("Ready to deploy?"), "{\"type\":\"assist" };

            Assert.Equal("Ready to deploy?", ChatQuestionDetector.FinalAssistantText(lines));
        }

        [Theory]
        [InlineData("..\\..\\secrets")]
        [InlineData("a/b")]
        [InlineData("")]
        public void Only_a_plain_session_id_becomes_a_file_name(string sessionId)
        {
            Assert.Null(ChatQuestionDetector.FindTranscript(sessionId, new[] { Path.GetTempPath() }));
        }

        [Fact]
        public void The_transcript_is_found_under_any_root_and_read_from_its_tail()
        {
            string root = Path.Combine(Path.GetTempPath(), "mt_cq_" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "projects", "H--Some-Project");
            Directory.CreateDirectory(folder);
            string sid = Guid.NewGuid().ToString();
            try
            {
                File.WriteAllLines(Path.Combine(folder, sid + ".jsonl"), new[]
                {
                    OwnerPrompt("go"),
                    Assistant("All three pass.\n\nShould I push it?"),
                });

                string path = ChatQuestionDetector.FindTranscript(sid, new[] { Path.Combine(Path.GetTempPath(), "missing-root"), root });
                Assert.NotNull(path);

                var tail = ChatQuestionDetector.ReadTailLines(path, ChatQuestionDetector.TailBytes);
                Assert.Equal("Should I push it?", ChatQuestionDetector.FindQuestion(ChatQuestionDetector.FinalAssistantText(tail)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public class ChatQuestionTranscriptMoveTests
    {
        [Fact]
        public void After_enter_worktree_the_newest_copy_of_the_session_is_read()
        {
            string root = Path.Combine(Path.GetTempPath(), "mt_cq_" + Guid.NewGuid().ToString("N"));
            string sid = Guid.NewGuid().ToString();
            string oldCopy = Path.Combine(root, "projects", "A-repo", sid + ".jsonl");
            string liveCopy = Path.Combine(root, "projects", "A-repo--claude-worktrees-x", sid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(oldCopy));
            Directory.CreateDirectory(Path.GetDirectoryName(liveCopy));
            try
            {
                File.WriteAllText(oldCopy, "{\"type\":\"user\",\"timestamp\":\"2026-10-23T10:00:00.000Z\"}\n");
                File.WriteAllText(liveCopy, "{\"type\":\"user\",\"timestamp\":\"2026-10-23T11:00:00.000Z\"}\n");

                // The stale copy has the NEWER modified time (a sync tool touched it): the entries
                // inside decide, not the file system (pipeline run 1, adversary).
                File.SetLastWriteTimeUtc(oldCopy, DateTime.UtcNow);
                File.SetLastWriteTimeUtc(liveCopy, DateTime.UtcNow.AddHours(-1));
                Assert.Equal(liveCopy, ChatQuestionDetector.FindTranscript(sid, new[] { root }));

                // And it follows the writes, rather than remembering an earlier answer.
                File.AppendAllText(oldCopy, "{\"type\":\"user\",\"timestamp\":\"2026-10-23T12:00:00.000Z\"}\n");
                Assert.Equal(oldCopy, ChatQuestionDetector.FindTranscript(sid, new[] { root }));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public class TurnEndAttentionTests
    {
        private const string Session = "sess-q";

        private static Dictionary<string, object> Notification(string rawType, string message = "Alice needs permission to continue")
            => new Dictionary<string, object>
            {
                ["session_id"] = Session,
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = message,
            };

        private static AgentAttentionService Seeded()
        {
            var svc = new AgentAttentionService();
            svc.NoteObservedActivity(Session, DateTime.UtcNow, isSubagent: false, agentName: "Alice", activitySummary: "Edit: x.cs");
            return svc;
        }

        [Fact]
        public void A_turn_that_ended_on_a_question_asks_you_with_the_question()
        {
            var svc = Seeded();

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false, chatQuestion: "Should I push it?");

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.True(e.IsBlocking);
            Assert.Equal("Should I push it?", e.Detail);
            Assert.True(e.DetailIsQuestionText);
        }

        [Fact]
        public void Any_other_turn_end_is_your_turn_and_not_a_block()
        {
            var svc = Seeded();

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.YourTurn, e.State);
            Assert.False(e.IsBlocking);
        }

        [Fact]
        public void Each_turn_end_is_a_new_episode_so_an_old_acknowledgement_does_not_silence_it()
        {
            var svc = Seeded();
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);
            long first = svc.Get(Session).BlockSeq;

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(2), isSubagent: false);

            Assert.True(svc.Get(Session).BlockSeq > first);
        }

        [Fact]
        public void Claude_codes_idle_timer_does_not_turn_your_turn_into_idle()
        {
            var svc = Seeded();
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);

            svc.ApplyNotification(Notification("idle_prompt", "Alice is waiting for your input"));

            Assert.Equal(AttentionState.YourTurn, svc.Get(Session).State);
        }

        [Fact]
        public void The_owners_reply_clears_your_turn_and_a_turn_end_question()
        {
            var svc = Seeded();
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);
            Assert.True(svc.NoteOwnerReplied("alice"));
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(2), isSubagent: false, chatQuestion: "Deploy now?");
            Assert.True(svc.NoteOwnerReplied("Alice"));

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.Working, e.State);
            Assert.Null(e.Detail);
        }

        [Fact]
        public void A_prompt_does_not_answer_a_permission_prompt_or_an_ask_user_question()
        {
            // Those are answered in their own dialog; Enter typed there is not the owner's prompt.
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("ask_user_question", "Alice asked: Which one?"));
            Assert.False(svc.NoteOwnerReplied("Alice"));
            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);

            svc.ApplyNotification(Notification("permission_prompt"));
            Assert.False(svc.NoteOwnerReplied("Alice"));
            Assert.True(svc.Get(Session).IsBlocking);
        }

        [Fact]
        public void A_turn_end_older_than_the_owners_reply_does_not_bring_the_card_back()
        {
            // The turn end is polled; the reply is not. A quick reply lands between the Stop hook's
            // row and the poll that reads it (pipeline run 1, debugger).
            var svc = Seeded();
            DateTime turnEnd = DateTime.UtcNow.AddSeconds(-1);
            svc.NoteOwnerReplied("Alice");

            Assert.False(svc.NoteTurnEnded(Session, turnEnd, isSubagent: false, chatQuestion: "Push?"));
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);

            // A turn that ends after the reply still counts.
            Assert.True(svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false));
            Assert.Equal(AttentionState.YourTurn, svc.Get(Session).State);
        }

        [Fact]
        public void The_reply_reaches_a_card_keyed_by_the_agents_name()
        {
            // NoteTurnEnded can create an entry keyed by the NAME, with no AgentName on it; the reply
            // uses the same ownership rule as every other by-name lookup (pipeline run 1, debugger).
            var svc = new AgentAttentionService();
            svc.NoteTurnEnded("Alice", DateTime.UtcNow, isSubagent: false);
            Assert.Null(svc.Get("Alice").AgentName);

            Assert.True(svc.NoteOwnerReplied("alice"));
            Assert.Equal(AttentionState.Working, svc.Get("Alice").State);
        }

        [Fact]
        public void A_late_question_upgrades_only_the_same_turn_end()
        {
            var svc = Seeded();
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);
            long seq = svc.Get(Session).BlockSeq;

            Assert.False(svc.UpgradeTurnEndToQuestion(Session, seq + 1, "Wrong episode?"));
            Assert.True(svc.UpgradeTurnEndToQuestion(Session, seq, "Should I push it?"));

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.Equal("Should I push it?", e.Detail);
            Assert.True(e.RaisedAtTurnEnd);

            svc.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false, agentName: "Alice", activitySummary: "Bash: x");
            Assert.False(svc.UpgradeTurnEndToQuestion(Session, svc.Get(Session).BlockSeq, "Too late?"));
        }

        [Fact]
        public void The_team_view_ranks_your_turn_between_blocked_and_calm_cards()
        {
            int Rank(string state) => AttentionTeamLayout.Rank(new AttentionCard { State = state });

            Assert.True(Rank("BlockedQuestion") < Rank("YourTurn"));
            Assert.True(Rank("YourTurn") < Rank("Idle"));
            Assert.True(Rank("Idle") < Rank("Offline"));
        }

        [Fact]
        public void The_agent_moving_on_after_the_turn_end_clears_it_even_in_the_same_poll()
        {
            // The episode is dated by the turn end's own row, not by when the poll reached it, so a
            // tool row written a moment after the turn end, read in the same batch, still clears it.
            var svc = Seeded();
            DateTime turnEnd = DateTime.UtcNow.AddSeconds(-3);
            svc.NoteTurnEnded(Session, turnEnd, isSubagent: false, chatQuestion: "Go ahead?");

            svc.NoteObservedActivity(Session, turnEnd.AddSeconds(1), isSubagent: false, agentName: "Alice", activitySummary: "Bash: git push");

            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
        }

        [Fact]
        public void A_helpers_turn_end_reads_as_idle_on_its_card_but_its_question_still_asks()
        {
            var svc = Seeded();
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(1), isSubagent: false);
            var helper = new Dictionary<string, AttentionTeamLink>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = new AttentionTeamLink { Role = AttentionTeamRoles.Helper, Pm = "Frank" },
            };

            var card = AttentionCardProjector.Project(svc.Snapshot(), null, null, DateTime.UtcNow, teamLinks: helper).Single();
            Assert.Equal("Idle", card.State);
            Assert.Equal("Finished and idle", card.ObservedVerb);

            var pm = AttentionCardProjector.Project(svc.Snapshot(), null, null, DateTime.UtcNow).Single();
            Assert.Equal("YourTurn", pm.State);
            Assert.Equal("Your turn", pm.ObservedVerb);

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(2), isSubagent: false, chatQuestion: "Which file?");
            var asked = AttentionCardProjector.Project(svc.Snapshot(), null, null, DateTime.UtcNow, teamLinks: helper).Single();
            Assert.Equal("BlockedQuestion", asked.State);
            Assert.Equal("Which file?", asked.ObservedDetail);
        }
    }

    /// <summary>The watcher hands the TURN_END row's own session id to the question lookup.</summary>
    public sealed class TurnEndWatcherTests : IDisposable
    {
        private const string Agent = "Alice";
        private const string Session = "sess-watch";

        private readonly string _dbPath;
        private readonly TaskDatabase _taskDb;
        private readonly ActivityFeedService _feed;
        private readonly AgentAttentionService _attention;
        private readonly List<string> _asked = new List<string>();
        private string _answer;
        private readonly AgentActivityWatcher _watcher;

        public TurnEndWatcherTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_turnend_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            _taskDb = new TaskDatabase();
            _feed = new ActivityFeedService();
            _attention = new AgentAttentionService();
            _watcher = new AgentActivityWatcher(_feed, _attention, null, sid =>
            {
                _asked.Add(sid);
                return _answer;
            });
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _feed.Dispose();
            _taskDb.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        private void TurnEnd() =>
            _feed.RecordGeneralActivity("TURN_END", Agent, "Turn ended", "info",
                $"{{\"agent_id\":null,\"session_id\":\"{Session}\"}}");

        private void Seed()
        {
            _attention.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(-5), isSubagent: false, agentName: Agent, activitySummary: "Edit: x.cs");
            Thread.Sleep(30);
        }

        [Fact]
        public void A_turn_end_row_asks_the_lookup_with_its_session_and_raises_the_question()
        {
            Assert.True(_watcher.Prime());
            Seed();
            _answer = "Should I push it?";

            TurnEnd();
            _watcher.Poll();

            Assert.Equal(new[] { Session }, _asked);
            var e = _attention.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.Equal("Should I push it?", e.Detail);
        }

        [Fact]
        public void No_question_found_is_your_turn()
        {
            Assert.True(_watcher.Prime());
            Seed();
            _answer = null;

            TurnEnd();
            _watcher.Poll();

            Assert.Equal(AttentionState.YourTurn, _attention.Get(Session).State);
        }

        [Fact]
        public void A_question_written_after_the_turn_end_was_read_is_picked_up_on_the_recheck()
        {
            // Pipeline run 1 (adversary): the row can be read before the final line is written.
            Assert.True(_watcher.Prime());
            Seed();
            _answer = null;
            TurnEnd();
            _watcher.Poll();
            Assert.Equal(AttentionState.YourTurn, _attention.Get(Session).State);

            _answer = "Should I push it?";
            _watcher.RecheckTurnEnds(DateTime.UtcNow);                       // not due yet
            Assert.Equal(AttentionState.YourTurn, _attention.Get(Session).State);

            _watcher.RecheckTurnEnds(DateTime.UtcNow + AgentActivityWatcher.TurnEndRecheckDelays[0]);
            var e = _attention.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.Equal("Should I push it?", e.Detail);
        }

        [Fact]
        public void The_recheck_does_not_raise_a_question_once_the_agent_has_moved_on()
        {
            Assert.True(_watcher.Prime());
            Seed();
            _answer = null;
            TurnEnd();
            _watcher.Poll();

            _attention.NoteOwnerReplied(Agent);
            _answer = "A question from the NEXT turn?";
            _watcher.RecheckTurnEnds(DateTime.UtcNow + TimeSpan.FromMinutes(1));

            Assert.Equal(AttentionState.Working, _attention.Get(Session).State);
        }
    }

    public class PromptSubmitDetectorTests
    {
        private static int Feed(PromptSubmitDetector d, string s) => d.Feed(System.Text.Encoding.UTF8.GetBytes(s));

        [Fact]
        public void Typed_text_and_enter_is_one_prompt()
        {
            var d = new PromptSubmitDetector();
            Assert.Equal(0, Feed(d, "yes, go"));
            Assert.Equal(1, Feed(d, "\r"));
        }

        [Fact]
        public void Enter_alone_and_menu_navigation_are_not_prompts()
        {
            var d = new PromptSubmitDetector();
            Assert.Equal(0, Feed(d, "\r"));
            Assert.Equal(0, Feed(d, "\u001b[B\u001b[B\r"));
        }

        [Fact]
        public void A_multi_line_paste_is_one_prompt_when_it_is_sent_not_at_its_first_line_break()
        {
            var d = new PromptSubmitDetector();
            Assert.Equal(0, Feed(d, "\u001b[200~line one\rline two\rline three\u001b[201~"));
            Assert.Equal(1, Feed(d, "\r"));
        }

        [Fact]
        public void A_backslash_enter_is_a_newline_not_a_prompt()
        {
            var d = new PromptSubmitDetector();
            Assert.Equal(0, Feed(d, "first\\\r"));
            Assert.Equal(1, Feed(d, "second\r"));
        }

        [Fact]
        public void Ctrl_c_clears_the_line_and_non_ascii_text_counts()
        {
            var d = new PromptSubmitDetector();
            Assert.Equal(0, Feed(d, "abc\u0003\r"));
            Assert.Equal(1, Feed(d, "café\r"));
        }
    }
}
