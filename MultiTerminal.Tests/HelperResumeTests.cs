using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="HelperResume.Check"/> and <see cref="HelperResumeReport"/> (task 7d20e94c items 4 and 6):
    /// the per-helper rules a resumed PM's helpers must pass, and the one summary the Owner sees. Pure:
    /// the filesystem probes are injected, so no fact here touches the disk.
    /// </summary>
    public sealed class HelperResumeTests
    {
        private const string Root = @"C:\proj";
        private const string Sid = "11111111-2222-3333-4444-555555555555";
        private static readonly DateTime Written = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

        private static OpenHelper Helper(string name = "Notes", string kind = "claude") =>
            new OpenHelper { SessionId = Sid, TerminalName = name, Kind = kind, Cwd = Root };

        private static SessionCatalogEntry Entry(string cwd = Root, SessionCatalogStatus status = SessionCatalogStatus.Available) =>
            new SessionCatalogEntry
            {
                SessionId = Sid,
                Cwd = cwd,
                TranscriptPath = @"C:\t\" + Sid + ".jsonl",
                LastActivityUtc = Written,
                Status = status,
            };

        private static HelperResumeCheck Run(
            OpenHelper helper,
            SessionCatalogEntry entry,
            IEnumerable<PaneSessionState> panes = null,
            DateTime? transcriptWrite = null,
            bool dirExists = true)
            => HelperResume.Check(helper, entry, panes ?? Array.Empty<PaneSessionState>(), Root,
                _ => transcriptWrite ?? Written, _ => dirExists);

        [Fact]
        public void A_resumable_helper_passes_with_its_validated_folder()
        {
            var worktree = Root + @"\.claude\worktrees\abc";
            var check = Run(Helper(), Entry(worktree));
            Assert.True(check.CanResume);
            Assert.Equal(worktree, check.Cwd);
            Assert.Equal(Sid, check.Candidate.Entry.SessionId);
        }

        [Fact]
        public void A_helper_with_no_name_or_a_non_claude_kind_is_skipped()
        {
            Assert.Contains("no recorded name", Run(Helper(name: " "), Entry()).SkipReason);
            Assert.Contains("not a Claude session", Run(Helper(kind: "codex"), Entry()).SkipReason);
        }

        [Fact]
        public void A_session_the_catalog_refused_is_skipped()
        {
            Assert.False(Run(Helper(), null).CanResume);
        }

        [Fact]
        public void A_session_now_held_by_a_pane_is_skipped()
        {
            var panes = new[] { new PaneSessionState(true, Sid, Written.AddHours(-1)) };
            Assert.Contains("already open", Run(Helper(), Entry(), panes).SkipReason);
        }

        [Fact]
        public void A_session_reserved_by_a_resuming_pane_is_skipped()
        {
            var panes = new[] { new PaneSessionState(true, null, Written.AddHours(-1), reservedSessionId: Sid) };
            Assert.Contains("already open", Run(Helper(), Entry(), panes).SkipReason);
        }

        [Fact]
        public void A_session_that_may_be_open_elsewhere_is_skipped_because_nobody_can_confirm()
        {
            Assert.Contains("may still be open elsewhere", Run(Helper(), Entry(status: SessionCatalogStatus.PossiblyOpenElsewhere)).SkipReason);

            // An unknown pane launched before the transcript's last write may be holding it.
            var unknown = new[] { new PaneSessionState(true, null, Written.AddSeconds(-1)) };
            Assert.Contains("may still be open elsewhere", Run(Helper(), Entry(), unknown).SkipReason);
        }

        [Fact]
        public void A_transcript_written_since_it_was_read_is_skipped()
        {
            Assert.Contains("written after", Run(Helper(), Entry(), transcriptWrite: Written.AddSeconds(1)).SkipReason);
        }

        [Fact]
        public void A_folder_outside_the_project_or_gone_is_skipped()
        {
            Assert.Contains("not this project's folder", Run(Helper(), Entry(@"C:\elsewhere")).SkipReason);
            Assert.Contains("not this project's folder", Run(Helper(), Entry(@"\\host\share\proj")).SkipReason);
            Assert.Contains("no longer exists", Run(Helper(), Entry(), dirExists: false).SkipReason);
        }

        [Fact]
        public void The_report_is_silent_when_every_helper_came_back_as_it_was()
        {
            var report = new HelperResumeReport();
            report.AddResumed("Notes");
            report.AddResumed("Bob");
            Assert.False(report.HasAnythingToReport);
            Assert.Null(report.BuildSummary("Alice"));
        }

        [Fact]
        public void A_rename_alone_is_reported()
        {
            var report = new HelperResumeReport();
            report.AddResumed("Notes-2");
            report.AddRenamed("Notes", "Notes-2");
            Assert.True(report.HasAnythingToReport);
            Assert.Contains("Notes came back as Notes-2", report.BuildSummary("Alice"));
        }

        [Fact]
        public void The_report_names_every_skip_and_rename_in_one_summary()
        {
            var report = new HelperResumeReport();
            report.AddResumed("Notes-2");
            report.AddRenamed("Notes", "Notes-2");
            report.AddSkipped("Bob", "the folder it ran in no longer exists (C:\\x)");
            string summary = report.BuildSummary("Alice");
            Assert.Contains("Resumed 1 of 2 helpers of Alice.", summary);
            Assert.Contains("- Bob: the folder it ran in no longer exists (C:\\x).", summary);
            Assert.Contains("- Notes came back as Notes-2.", summary);
        }
    }
}
