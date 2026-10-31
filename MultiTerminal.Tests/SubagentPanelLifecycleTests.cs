using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH #89: subagent panels left on "Running" after the agent finished, and a team whose lead had
    /// moved into a worktree showing no agent panels at all. Both came from Claude Code keeping a
    /// session's files under the project folder of the session's CURRENT directory: when the parent
    /// session changes worktree, a running subagent goes on in a new file under another folder, and a
    /// team's transcripts are no longer under the folder its config names. All facts run over temp
    /// folders; nothing reads the real Claude folders.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "A tailer handed back through 'out' is one the test already owns and disposes with 'using'.")]
    public sealed class SubagentPanelLifecycleTests : IDisposable
    {
        private const string SessionId = "d7acab46-ce25-4f72-8594-9adb35e0f3db";
        private const string AgentFile = "agent-a507d26986ef907e3.jsonl";

        private readonly string _base = Path.Combine(Path.GetTempPath(), $"mt_subagent89_{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string TranscriptIn(string projectFolder, string session = SessionId, string agentFile = AgentFile) =>
            Path.Combine(_base, "projects", projectFolder, session, "subagents", agentFile);

        private string WriteTranscript(string path, params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines);
            return path;
        }

        private static string Assistant(string uuid, string text, string stopReason) =>
            "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"sessionId\":\"" + SessionId + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\""
            + text + "\"}],\"stop_reason\":" + (stopReason == null ? "null" : "\"" + stopReason + "\"") + "}}";

        private static string ToolResult(string uuid) =>
            "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"sessionId\":\"" + SessionId + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]}}";

        // ── Which agent a file belongs to ───────────────────────────────────

        [Fact]
        public void The_same_agent_in_two_project_folders_has_one_key()
        {
            var main = TranscriptIn("F--repo");
            var worktree = TranscriptIn("F--repo--claude-worktrees-1234abcd");

            Assert.NotEqual(main, worktree);
            Assert.Equal(OrphanSubagentRegistry.AgentKey(main), OrphanSubagentRegistry.AgentKey(worktree));
        }

        [Fact]
        public void Another_session_or_another_agent_has_another_key()
        {
            var key = OrphanSubagentRegistry.AgentKey(TranscriptIn("F--repo"));

            Assert.NotEqual(key, OrphanSubagentRegistry.AgentKey(TranscriptIn("F--repo", session: "11111111-2222-3333-4444-555555555555")));
            Assert.NotEqual(key, OrphanSubagentRegistry.AgentKey(TranscriptIn("F--repo", agentFile: "agent-b000000000000000.jsonl")));
        }

        [Fact]
        public void A_path_outside_the_subagents_layout_keys_on_itself()
        {
            // A file not under <session>\subagents\agent-*.jsonl must never be merged with anything.
            var odd = Path.Combine(_base, "elsewhere", AgentFile);
            Assert.Equal(Path.GetFullPath(odd), OrphanSubagentRegistry.AgentKey(odd));
        }

        // ── One panel per agent ─────────────────────────────────────────────

        [Fact]
        public void A_second_file_of_a_running_agent_joins_its_tailer_and_the_hook_finds_it_by_either_path()
        {
            var registry = new OrphanSubagentRegistry();
            var main = TranscriptIn("F--repo");
            var worktree = TranscriptIn("F--repo--claude-worktrees-1234abcd");
            using var first = new TranscriptTailer(main, null, "Subagent a507d269");
            using var second = new TranscriptTailer(worktree, null, "Subagent a507d269");

            Assert.Equal(OrphanSubagentRegistry.ClaimResult.New, registry.Claim(main, first, DateTime.UtcNow, out _));
            Assert.Equal(OrphanSubagentRegistry.ClaimResult.Existing, registry.Claim(worktree, second, DateTime.UtcNow, out var existing));

            Assert.Same(first, existing);
            Assert.Same(first, registry.Find(main));
            Assert.Same(first, registry.Find(worktree));
        }

        [Fact]
        public void The_hook_naming_a_copy_the_watcher_never_saw_still_finds_the_agent()
        {
            var registry = new OrphanSubagentRegistry();
            using var tailer = new TranscriptTailer(TranscriptIn("F--repo"), null, "Subagent a507d269");
            registry.Claim(TranscriptIn("F--repo"), tailer, DateTime.UtcNow, out _);

            Assert.Same(tailer, registry.Find(TranscriptIn("F--repo--claude-worktrees-1234abcd")));
            Assert.Null(registry.Find(TranscriptIn("F--repo", agentFile: "agent-b000000000000000.jsonl")));
        }

        [Fact]
        public void A_finished_agent_opens_no_new_panel_but_a_stopped_one_may()
        {
            var now = DateTime.UtcNow;
            var registry = new OrphanSubagentRegistry();
            var main = TranscriptIn("F--repo");
            var copy = TranscriptIn("F--repo--claude-worktrees-1234abcd");
            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            using var candidate = new TranscriptTailer(copy, null, "Subagent a507d269");

            registry.Claim(main, tailer, now, out _);
            registry.Remove(tailer, finished: true, now);
            Assert.Null(registry.Find(main));
            Assert.Equal(OrphanSubagentRegistry.ClaimResult.Finished, registry.Claim(copy, candidate, now, out _));

            // A stop that is not a finish (a team reclaiming the file, a root going away) is not remembered.
            var other = TranscriptIn("F--repo", agentFile: "agent-b000000000000000.jsonl");
            using var otherTailer = new TranscriptTailer(other, null, "Subagent b0000000");
            registry.Claim(other, otherTailer, now, out _);
            registry.Remove(otherTailer, finished: false, now);
            Assert.Equal(OrphanSubagentRegistry.ClaimResult.New, registry.Claim(other, otherTailer, now, out _));
        }

        [Fact]
        public void A_finished_agent_is_forgotten_after_the_memory_window()
        {
            var now = DateTime.UtcNow;
            var registry = new OrphanSubagentRegistry();
            var main = TranscriptIn("F--repo");
            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            registry.Claim(main, tailer, now, out _);
            registry.Remove(tailer, finished: true, now);

            var later = now + OrphanSubagentRegistry.FinishedMemory + TimeSpan.FromMinutes(1);
            Assert.Equal(OrphanSubagentRegistry.ClaimResult.New, registry.Classify(main, later, out _));
        }

        [Fact]
        public void Classify_registers_nothing_for_a_new_agent()
        {
            var registry = new OrphanSubagentRegistry();
            var main = TranscriptIn("F--repo");

            Assert.Equal(OrphanSubagentRegistry.ClaimResult.New, registry.Classify(main, DateTime.UtcNow, out var existing));
            Assert.Null(existing);
            Assert.Null(registry.Find(main));
        }

        [Fact]
        public void Taking_a_root_removes_the_tailer_under_every_path_it_held()
        {
            var registry = new OrphanSubagentRegistry();
            var main = TranscriptIn("F--repo");
            var worktree = TranscriptIn("F--repo--claude-worktrees-1234abcd");
            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            registry.Claim(main, tailer, DateTime.UtcNow, out _);
            registry.Classify(worktree, DateTime.UtcNow, out _);

            var taken = registry.TakeWhere((path, t) => path.Contains("claude-worktrees", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(new[] { tailer }, taken);
            Assert.Null(registry.Find(main));
        }

        // ── The tailer: one copy of each line, and a finish without the hook ─

        [Fact]
        public void Lines_repeated_in_the_continuation_file_are_shown_once()
        {
            var main = WriteTranscript(TranscriptIn("F--repo"),
                Assistant("u1", "one", "tool_use"),
                Assistant("u2", "two", "tool_use"));
            var worktree = WriteTranscript(TranscriptIn("F--repo--claude-worktrees-1234abcd"),
                Assistant("u2", "two", "tool_use"),
                Assistant("u3", "three", "end_turn"));

            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            tailer.StartAsync();
            tailer.AddTranscriptFile(worktree);

            var texts = tailer.Messages.Select(m => m.Content).Where(c => c is "one" or "two" or "three").ToList();
            Assert.Equal(new[] { "one", "two", "three" }, texts);
        }

        [Fact]
        public void A_line_repeated_inside_one_file_is_not_dropped()
        {
            // The dedup is only across files: within one file nothing is skipped.
            var main = WriteTranscript(TranscriptIn("F--repo"),
                Assistant("u1", "one", "tool_use"),
                Assistant("u1", "one", "tool_use"));

            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            tailer.StartAsync();

            Assert.Equal(2, tailer.Messages.Count(m => m.Content == "one"));
        }

        [Fact]
        public void An_ended_turn_followed_by_quiet_completes_the_agent_with_exit_code_zero()
        {
            var main = WriteTranscript(TranscriptIn("F--repo"),
                Assistant("u1", "working", "tool_use"),
                ToolResult("u2"),
                Assistant("u3", "done", "end_turn"));

            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269") { QuietCompletionWindow = TimeSpan.FromSeconds(60) };
            var exits = new List<int>();
            tailer.Stopped += (s, code) => exits.Add(code);
            tailer.StartAsync();

            tailer.CheckQuietCompletion(DateTime.UtcNow);
            Assert.Empty(exits);
            Assert.True(tailer.IsActive);

            tailer.CheckQuietCompletion(DateTime.UtcNow.AddMinutes(2));
            Assert.Equal(new[] { 0 }, exits);
            Assert.False(tailer.IsActive);

            tailer.CheckQuietCompletion(DateTime.UtcNow.AddMinutes(3));
            tailer.Complete();
            Assert.Equal(new[] { 0 }, exits);
        }

        [Fact]
        public void A_quiet_agent_whose_last_turn_did_not_end_is_not_completed()
        {
            // Waiting on a long tool call: the last assistant line stopped for tool_use.
            var main = WriteTranscript(TranscriptIn("F--repo"),
                Assistant("u1", "done earlier", "end_turn"),
                ToolResult("u2"),
                Assistant("u3", "running a long build", "tool_use"));

            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            var exits = new List<int>();
            tailer.Stopped += (s, code) => exits.Add(code);
            tailer.StartAsync();

            tailer.CheckQuietCompletion(DateTime.UtcNow.AddHours(1));
            Assert.Empty(exits);
        }

        [Fact]
        public void A_completed_agent_is_not_restarted_by_a_continuation_file()
        {
            var main = WriteTranscript(TranscriptIn("F--repo"), Assistant("u1", "done", "end_turn"));
            var worktree = WriteTranscript(TranscriptIn("F--repo--claude-worktrees-1234abcd"), Assistant("u2", "late line", "tool_use"));

            using var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            tailer.StartAsync();
            tailer.Complete();

            tailer.AddTranscriptFile(worktree);
            tailer.StartAsync();

            Assert.False(tailer.IsActive);
            Assert.DoesNotContain(tailer.Messages, m => m.Content == "late line");
        }

        [Fact]
        public void Disposing_a_completed_tailer_sends_no_second_stop()
        {
            // A second, failing exit would close a panel that On Complete = Keep open is keeping.
            var main = WriteTranscript(TranscriptIn("F--repo"), Assistant("u1", "done", "end_turn"));

            var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            var exits = new List<int>();
            tailer.Stopped += (s, code) => exits.Add(code);
            tailer.StartAsync();
            tailer.Complete();
            tailer.Dispose();

            Assert.Equal(new[] { 0 }, exits);
        }

        [Fact]
        public void Disposing_a_running_tailer_still_reports_a_failing_stop()
        {
            var main = WriteTranscript(TranscriptIn("F--repo"), Assistant("u1", "working", "tool_use"));

            var tailer = new TranscriptTailer(main, null, "Subagent a507d269");
            var exits = new List<int>();
            tailer.Stopped += (s, code) => exits.Add(code);
            tailer.StartAsync();
            tailer.Dispose();

            Assert.Equal(new[] { -1 }, exits);
        }

        [Fact]
        public void A_team_agent_is_never_completed_by_the_quiet_fallback()
        {
            var main = WriteTranscript(TranscriptIn("F--repo"), Assistant("u1", "idle between turns", "end_turn"));

            using var tailer = new TranscriptTailer(main, "inbox.json", "Verifier") { IsTeamAgent = true };
            var exits = new List<int>();
            tailer.Stopped += (s, code) => exits.Add(code);
            tailer.StartAsync();

            tailer.CheckQuietCompletion(DateTime.UtcNow.AddHours(1));
            Assert.Empty(exits);
        }

        // ── A team whose lead moved into a worktree ────────────────────────

        [Fact]
        public void A_lead_session_that_moved_into_a_worktree_is_found_under_the_worktrees_folder()
        {
            var start = Path.Combine(_base, "projects", "F--github-ClarionAddinFinder");
            var moved = Path.Combine(_base, "projects", "F--github-ClarionAddinFinder--claude-worktrees-0a3180ff");
            Directory.CreateDirectory(start);
            Directory.CreateDirectory(Path.Combine(moved, SessionId, "subagents"));

            Assert.Equal(moved, TeamWatcherService.FindLeadSessionProjectDir(start, SessionId));
        }

        [Fact]
        public void When_both_folders_hold_the_session_the_more_recently_written_one_wins()
        {
            // The old folder can be left behind after the move, so its mere existence decides nothing.
            var start = Path.Combine(_base, "projects", "F--repo");
            var moved = Path.Combine(_base, "projects", "F--repo--claude-worktrees-1234abcd");
            var startSession = Directory.CreateDirectory(Path.Combine(start, SessionId));
            var movedSubagents = Directory.CreateDirectory(Path.Combine(moved, SessionId, "subagents"));
            var old = DateTime.UtcNow.AddHours(-2);
            startSession.LastWriteTimeUtc = old;
            new DirectoryInfo(Path.Combine(moved, SessionId)).LastWriteTimeUtc = old;
            movedSubagents.LastWriteTimeUtc = DateTime.UtcNow;

            Assert.Equal(moved, TeamWatcherService.FindLeadSessionProjectDir(start, SessionId));

            startSession.LastWriteTimeUtc = DateTime.UtcNow.AddMinutes(5);
            Assert.Equal(start, TeamWatcherService.FindLeadSessionProjectDir(start, SessionId));
        }

        [Fact]
        public void A_lead_session_found_nowhere_keeps_the_starting_folder()
        {
            var start = Path.Combine(_base, "projects", "F--repo");
            Directory.CreateDirectory(start);

            Assert.Equal(start, TeamWatcherService.FindLeadSessionProjectDir(start, SessionId));
        }

        // ── Settings: On Complete ──────────────────────────────────────────

        [Fact]
        public void On_complete_defaults_to_auto_close_and_keeps_a_saved_keep_open()
        {
            var dir = Path.Combine(_base, "settings");
            Directory.CreateDirectory(dir);

            Assert.Equal("AutoClose", new SettingsService(dir).GetAgentPanelCloseMode());

            new SettingsService(dir).SetAgentPanelCloseMode("ManualClose");
            Assert.Equal("ManualClose", new SettingsService(dir).GetAgentPanelCloseMode());

            new SettingsService(dir).SetAgentPanelCloseMode("AutoClose");
            Assert.Equal("AutoClose", new SettingsService(dir).GetAgentPanelCloseMode());
        }

        [Fact]
        public void A_keep_open_saved_under_the_old_unread_key_is_ignored()
        {
            // The old dialog wrote ManualClose on every OK while nothing read it, so it records no choice.
            var dir = Path.Combine(_base, "settings-old");
            Directory.CreateDirectory(dir);
            new SettingsService(dir).Set("AgentPanelCloseMode", "ManualClose");

            Assert.Equal("AutoClose", new SettingsService(dir).GetAgentPanelCloseMode());
        }
    }
}
