using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The decision logic behind the project-launch session prompt (task ec2ed636):
    /// <see cref="LaunchSessionChoiceModel"/>. Pure, no Form, no disk.
    /// </summary>
    public sealed class LaunchSessionChoiceModelTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        private static SessionCatalogEntry Entry(string id, int minutesAgo, string agent = "Alice",
            SessionCatalogStatus status = SessionCatalogStatus.Available) =>
            new SessionCatalogEntry
            {
                SessionId = id,
                Cwd = @"C:\proj",
                LastActivityUtc = Now.AddMinutes(-minutesAgo),
                AgentName = agent,
                Status = status,
            };

        private const string A = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string B = "bbbbbbbb-0000-0000-0000-000000000002";
        private const string C = "cccccccc-0000-0000-0000-000000000003";

        private static LaunchSessionChoiceModel Model(IEnumerable<SessionCatalogEntry> catalog, params PaneSessionState[] panes) =>
            new LaunchSessionChoiceModel(catalog, panes);

        // ── Decide: what happens before any UI ────────────────────────────────

        [Fact]
        public void Shift_starts_new_without_reading_the_catalog()
        {
            var loaded = false;
            var d = LaunchSessionChoiceModel.Decide(true, shiftHeld: true, ProjectLaunchMode.Ask,
                () => { loaded = true; return Model(new[] { Entry(A, 30) }); });

            Assert.Equal(LaunchSessionAction.New, d.Action);
            Assert.False(loaded);
        }

        [Fact]
        public void Non_claude_kind_starts_new_without_reading_the_catalog()
        {
            var loaded = false;
            var d = LaunchSessionChoiceModel.Decide(isClaudeKind: false, false, ProjectLaunchMode.Continue,
                () => { loaded = true; return Model(new[] { Entry(A, 30) }); });

            Assert.Equal(LaunchSessionAction.New, d.Action);
            Assert.False(loaded);
        }

        [Fact]
        public void Mode_new_starts_new_without_reading_the_catalog()
        {
            var loaded = false;
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.New,
                () => { loaded = true; return Model(new[] { Entry(A, 30) }); });

            Assert.Equal(LaunchSessionAction.New, d.Action);
            Assert.False(loaded);
        }

        [Fact]
        public void Mode_ask_prompts_when_there_is_something_to_resume()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Ask, () => Model(new[] { Entry(A, 30) }));
            Assert.Equal(LaunchSessionAction.Prompt, d.Action);
            Assert.NotNull(d.Model);
        }

        [Fact]
        public void Mode_ask_with_an_empty_catalog_starts_new_without_prompting()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Ask, () => Model(Array.Empty<SessionCatalogEntry>()));
            Assert.Equal(LaunchSessionAction.New, d.Action);
        }

        [Fact]
        public void Mode_ask_where_every_session_is_live_starts_new_without_prompting()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Ask,
                () => Model(new[] { Entry(A, 30) }, new PaneSessionState(true, A, Now.AddHours(-1))));
            Assert.Equal(LaunchSessionAction.New, d.Action);
        }

        [Fact]
        public void Mode_continue_resumes_the_newest_non_live_session()
        {
            // B is newest but live in a pane; A is next. C is oldest.
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Continue,
                () => Model(new[] { Entry(C, 300), Entry(B, 10), Entry(A, 60) },
                            new PaneSessionState(true, B, Now.AddHours(-2))));

            Assert.Equal(LaunchSessionAction.Resume, d.Action);
            Assert.Equal(A, d.Candidate.Entry.SessionId);
        }

        [Fact]
        public void Mode_continue_with_nothing_to_resume_falls_back_to_new()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Continue, () => Model(Array.Empty<SessionCatalogEntry>()));
            Assert.Equal(LaunchSessionAction.New, d.Action);
            Assert.Null(d.Candidate);
        }

        [Fact]
        public void Mode_continue_prompts_instead_of_silently_resuming_a_session_that_may_be_open()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Continue,
                () => Model(new[] { Entry(A, 1, status: SessionCatalogStatus.PossiblyOpenElsewhere), Entry(B, 90) }));

            Assert.Equal(LaunchSessionAction.Prompt, d.Action);
        }

        // ── Liveness ──────────────────────────────────────────────────────────

        [Fact]
        public void A_session_held_by_an_open_pane_is_never_offered_whatever_its_case()
        {
            var m = Model(new[] { Entry(A, 5), Entry(B, 50) }, new PaneSessionState(true, A.ToUpperInvariant(), Now.AddHours(-1)));

            Assert.Equal(new[] { B }, m.Candidates.Select(c => c.Entry.SessionId));
            Assert.Contains(A, m.LiveSessionIds);
        }

        [Fact]
        public void A_known_live_pane_flags_nothing()
        {
            var m = Model(new[] { Entry(A, 5), Entry(B, 50) }, new PaneSessionState(true, C, Now.AddHours(-3)));
            Assert.All(m.Candidates, c => Assert.False(c.MayBeOpenElsewhere));
            Assert.False(m.HasUnknownPane);
        }

        [Fact]
        public void A_stopped_pane_with_no_session_flags_nothing()
        {
            var m = Model(new[] { Entry(A, 5) }, new PaneSessionState(false, null, null));
            Assert.False(m.Candidates.Single().MayBeOpenElsewhere);
        }

        [Fact]
        public void A_started_pane_with_no_session_id_flags_sessions_written_since_its_launch_and_hides_none()
        {
            // The pane launched 30 min ago and has not said which session it holds. A (5 min ago) and
            // B (29 min ago) were written since: either could be it. C (2 h ago) was not.
            var m = Model(new[] { Entry(A, 5), Entry(B, 29), Entry(C, 120) },
                new PaneSessionState(true, null, Now.AddMinutes(-30)));

            Assert.Equal(3, m.Candidates.Count);
            Assert.True(m.HasUnknownPane);
            var flagged = m.Candidates.ToDictionary(c => c.Entry.SessionId, c => c.MayBeOpenElsewhere);
            Assert.True(flagged[A]);
            Assert.True(flagged[B]);
            Assert.False(flagged[C]);
        }

        [Fact]
        public void A_started_pane_with_no_session_id_and_no_launch_time_flags_every_session()
        {
            var m = Model(new[] { Entry(A, 5), Entry(C, 6000) }, new PaneSessionState(true, null, null));
            Assert.All(m.Candidates, c => Assert.True(c.MayBeOpenElsewhere));
        }

        [Fact]
        public void A_recently_written_transcript_is_flagged_with_no_unknown_pane()
        {
            var m = Model(new[] { Entry(A, 1, status: SessionCatalogStatus.PossiblyOpenElsewhere), Entry(B, 60) });
            Assert.True(m.Candidates[0].MayBeOpenElsewhere);
            Assert.False(m.Candidates[1].MayBeOpenElsewhere);
        }

        [Fact]
        public void Candidates_are_newest_first_and_continue_is_the_newest()
        {
            var m = Model(new[] { Entry(C, 300), Entry(A, 10), Entry(B, 60) });
            Assert.Equal(new[] { A, B, C }, m.Candidates.Select(c => c.Entry.SessionId));
            Assert.Equal(A, m.ContinueCandidate.Entry.SessionId);
        }

        // ── "Don't ask again" ─────────────────────────────────────────────────

        [Theory]
        [InlineData(LaunchSessionChoice.NewSession, true, ProjectLaunchMode.New)]
        [InlineData(LaunchSessionChoice.ContinueLast, true, ProjectLaunchMode.Continue)]
        [InlineData(LaunchSessionChoice.PickedSession, true, null)]
        [InlineData(LaunchSessionChoice.NewSession, false, null)]
        [InlineData(LaunchSessionChoice.ContinueLast, false, null)]
        [InlineData(LaunchSessionChoice.PickedSession, false, null)]
        public void Dont_ask_again_maps_to_the_mode_it_persists(LaunchSessionChoice choice, bool dontAsk, ProjectLaunchMode? expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.ModeToPersist(choice, dontAsk));
        }

        // ── Name ──────────────────────────────────────────────────────────────

        [Fact]
        public void Resume_defaults_to_the_sessions_original_name_and_new_to_the_launch_name()
        {
            var m = Model(new[] { Entry(A, 10, agent: "Notes") });
            var c = m.ContinueCandidate;

            Assert.Equal("Notes", LaunchSessionChoiceModel.ResolveName(LaunchSessionChoice.ContinueLast, c, "MultiTerminal", null));
            Assert.Equal("Notes", LaunchSessionChoiceModel.ResolveName(LaunchSessionChoice.PickedSession, c, "MultiTerminal", "  "));
            Assert.Equal("MultiTerminal", LaunchSessionChoiceModel.ResolveName(LaunchSessionChoice.NewSession, c, "MultiTerminal", null));
        }

        [Fact]
        public void Resume_of_a_session_with_no_known_agent_falls_back_to_the_launch_name()
        {
            var m = Model(new[] { Entry(A, 10, agent: null) });
            Assert.Equal("MultiTerminal", LaunchSessionChoiceModel.ResolveName(LaunchSessionChoice.ContinueLast, m.ContinueCandidate, "MultiTerminal", null));
        }

        [Fact]
        public void A_typed_name_wins_for_every_choice()
        {
            var m = Model(new[] { Entry(A, 10, agent: "Notes") });
            foreach (var choice in new[] { LaunchSessionChoice.NewSession, LaunchSessionChoice.ContinueLast, LaunchSessionChoice.PickedSession })
                Assert.Equal("Gear", LaunchSessionChoiceModel.ResolveName(choice, m.ContinueCandidate, "MultiTerminal", " Gear "));
        }

        [Theory]
        [InlineData(0, "just now")]
        [InlineData(12, "12 min ago")]
        [InlineData(180, "3 h ago")]
        [InlineData(60 * 24 * 5, "5 d ago")]
        public void Relative_age(int minutesAgo, string expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.FormatRelativeAge(Now.AddMinutes(-minutesAgo), Now));
        }
    }
}
