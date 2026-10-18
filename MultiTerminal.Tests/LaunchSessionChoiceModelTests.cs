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

        // ── Pipeline Run 1 ────────────────────────────────────────────────────

        private static SessionCatalogEntry Helper(string id, int minutesAgo, string agent) =>
            new SessionCatalogEntry
            {
                SessionId = id,
                Cwd = @"C:\proj",
                LastActivityUtc = Now.AddMinutes(-minutesAgo),
                AgentName = agent,
                IsHelper = true,
                SpawnedBy = "Alice",
            };

        [Fact]
        public void Continue_prefers_the_newest_non_helper_over_a_newer_helper()
        {
            // The helper finished after its PM's last turn, so it is the newest transcript.
            var m = Model(new[] { Helper(B, 5, "Notes"), Entry(A, 30, agent: "Alice"), Helper(C, 1, "Gear") });

            Assert.Equal(C, m.Candidates[0].Entry.SessionId); // the list itself stays newest first
            Assert.Equal(A, m.ContinueCandidate.Entry.SessionId);

            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Continue, () => m);
            Assert.Equal(A, d.Candidate.Entry.SessionId);
        }

        [Fact]
        public void Continue_falls_back_to_the_newest_helper_when_every_candidate_is_a_helper()
        {
            var m = Model(new[] { Helper(A, 30, "Notes"), Helper(B, 5, "Gear") });
            Assert.Equal(B, m.ContinueCandidate.Entry.SessionId);
        }

        [Theory]
        [InlineData(ProjectLaunchMode.Ask)]
        [InlineData(ProjectLaunchMode.Continue)]
        public void A_catalog_that_could_not_be_read_is_reported_not_treated_as_empty(ProjectLaunchMode mode)
        {
            var failed = LaunchSessionChoiceModel.ForLoadFailure(null, "access denied");
            var d = LaunchSessionChoiceModel.Decide(true, false, mode, () => failed);

            Assert.Equal(LaunchSessionAction.LoadFailed, d.Action);
            Assert.Null(d.Candidate);
            Assert.True(d.Model.LoadFailed);
            Assert.Equal("access denied", d.Model.LoadError);

            // The contrast that makes the distinction: a genuinely empty catalog still starts new quietly.
            var empty = LaunchSessionChoiceModel.Decide(true, false, mode, () => Model(Array.Empty<SessionCatalogEntry>()));
            Assert.Equal(LaunchSessionAction.New, empty.Action);
            Assert.False(empty.Model.LoadFailed);
        }

        [Fact]
        public void A_load_failure_with_a_blank_error_still_reads_as_failed()
        {
            Assert.True(LaunchSessionChoiceModel.ForLoadFailure(null, "  ").LoadFailed);
        }

        [Fact]
        public void Continue_prompt_defaults_to_continue_and_prefills_the_continue_candidates_name()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Continue,
                () => Model(new[] { Entry(A, 1, agent: "Notes", status: SessionCatalogStatus.PossiblyOpenElsewhere) }));

            Assert.Equal(LaunchSessionAction.Prompt, d.Action);
            Assert.Equal(LaunchSessionChoice.ContinueLast, d.DefaultChoice);
            Assert.Equal("Notes", d.Model.InitialNameFor(d.DefaultChoice, "MultiTerminal"));
        }

        [Fact]
        public void Ask_prompt_defaults_to_new_and_prefills_the_new_session_name()
        {
            var d = LaunchSessionChoiceModel.Decide(true, false, ProjectLaunchMode.Ask, () => Model(new[] { Entry(A, 30, agent: "Notes") }));

            Assert.Equal(LaunchSessionChoice.NewSession, d.DefaultChoice);
            Assert.Equal("MultiTerminal", d.Model.InitialNameFor(d.DefaultChoice, "MultiTerminal"));
        }

        [Fact]
        public void A_reserved_resume_is_live_but_does_not_make_its_pane_known()
        {
            // Pane 1 was just launched with --resume A and has not reported a session id yet.
            var reserving = new PaneSessionState(true, null, Now.AddSeconds(-2), reservedSessionId: A);
            var m = Model(new[] { Entry(A, 600), Entry(B, 700) }, reserving);

            Assert.DoesNotContain(m.Candidates, c => c.Entry.SessionId == A);
            Assert.Contains(A, LaunchSessionChoiceModel.LiveSessionIdsOf(new[] { reserving }));
            Assert.True(reserving.IsUnknown);
            Assert.True(m.HasUnknownPane);
        }

        [Fact]
        public void A_running_helper_holds_its_session_and_one_with_no_id_yet_is_an_unknown_pane()
        {
            var known = PaneSessionState.ForHelper(isRunning: true, A, Now.AddHours(-1));
            var fresh = PaneSessionState.ForHelper(isRunning: true, null, Now.AddMinutes(-20));
            Assert.Null(PaneSessionState.ForHelper(isRunning: false, B, Now.AddHours(-1)));

            var m = Model(new[] { Entry(A, 5), Entry(B, 10), Entry(C, 60) }, known, fresh);

            Assert.DoesNotContain(m.Candidates, c => c.Entry.SessionId == A);
            var byId = m.Candidates.ToDictionary(c => c.Entry.SessionId);
            Assert.True(byId[B].MayBeOpenElsewhere);   // written since the id-less helper started
            Assert.False(byId[C].MayBeOpenElsewhere);  // written before it started
        }

        [Fact]
        public void Recheck_refuses_a_session_that_became_live_or_was_written_since_and_allows_an_unchanged_one()
        {
            var m = Model(new[] { Entry(A, 30) });
            var candidate = m.ContinueCandidate;
            var lastWrite = candidate.Entry.LastActivityUtc;

            Assert.Null(LaunchSessionChoiceModel.RecheckResume(candidate, Array.Empty<PaneSessionState>(), lastWrite));
            Assert.NotNull(LaunchSessionChoiceModel.RecheckResume(candidate, new[] { new PaneSessionState(true, A.ToUpperInvariant(), Now) }, lastWrite));
            Assert.NotNull(LaunchSessionChoiceModel.RecheckResume(candidate, new[] { new PaneSessionState(true, null, Now, reservedSessionId: A) }, lastWrite));
            Assert.NotNull(LaunchSessionChoiceModel.RecheckResume(candidate, new[] { PaneSessionState.ForHelper(true, A, Now) }, lastWrite));
            Assert.NotNull(LaunchSessionChoiceModel.RecheckResume(candidate, Array.Empty<PaneSessionState>(), lastWrite.AddSeconds(1)));
            Assert.NotNull(LaunchSessionChoiceModel.RecheckResume(candidate, Array.Empty<PaneSessionState>(), null));
        }

        [Theory]
        // project-derived identity chosen unchanged (any case): the fail-closed path, broker or not
        [InlineData(true, "MultiTerminal", "MultiTerminal", true, ChosenNameRegistration.ProjectIdentityFailClosed)]
        [InlineData(true, "MultiTerminal", "multiterminal", true, ChosenNameRegistration.ProjectIdentityFailClosed)]
        [InlineData(true, "MultiTerminal", "MultiTerminal", false, ChosenNameRegistration.ProjectIdentityFailClosed)]
        // a different name (the resumed session's own, or typed)
        [InlineData(true, "MultiTerminal", "Notes", true, ChosenNameRegistration.ChosenName)]
        [InlineData(true, "MultiTerminal", "Notes", false, ChosenNameRegistration.RefuseNoBroker)]
        // not project-derived (team lead / Codex default): never the project-identity path
        [InlineData(false, "Alice", "Alice", true, ChosenNameRegistration.ChosenName)]
        [InlineData(false, "Alice", "Alice", false, ChosenNameRegistration.RefuseNoBroker)]
        public void Chosen_name_registration_route(bool projectDerived, string identity, string chosen, bool broker, ChosenNameRegistration expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.RouteChosenName(projectDerived, identity, chosen, broker));
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

        [Theory]
        [InlineData(142_350, 200_000, "142k / 200k (71%)")]
        [InlineData(221_642, 1_000_000, "221k / 1M (22%)")]
        // truncated, not rounded: 79.95% must not read as 80% while it is not highlighted
        [InlineData(159_900, 200_000, "159k / 200k (79%)")]
        [InlineData(180_000, 200_000, "180k / 200k (90%)")]
        [InlineData(850, 200_000, "850 / 200k (0%)")]
        [InlineData(1_250_000, null, "1.2M")]
        [InlineData(142_350, null, "142k")]
        [InlineData(null, 200_000, "—")]
        [InlineData(null, null, "—")]
        public void Context_text(int? tokens, int? window, string expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.FormatContext(tokens, window));
        }

        [Theory]
        [InlineData(159_999, 200_000, ContextLevel.Normal)]
        [InlineData(160_000, 200_000, ContextLevel.Warning)]
        [InlineData(179_999, 200_000, ContextLevel.Warning)]
        [InlineData(180_000, 200_000, ContextLevel.Critical)]
        [InlineData(950_000, 1_000_000, ContextLevel.Critical)]
        [InlineData(180_000, 1_000_000, ContextLevel.Normal)]
        [InlineData(180_000, null, ContextLevel.Unknown)]
        [InlineData(null, 200_000, ContextLevel.Unknown)]
        public void Context_level(int? tokens, int? window, ContextLevel expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.ContextLevelOf(tokens, window));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(999, "999")]
        [InlineData(1_000, "1k")]
        [InlineData(199_999, "199k")]
        [InlineData(1_000_000, "1M")]
        [InlineData(1_099_999, "1M")]
        [InlineData(2_500_000, "2.5M")]
        public void Token_count(int tokens, string expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.FormatTokenCount(tokens));
        }

        [Fact]
        public void Tooltip_shows_a_sanitised_copy_of_the_cwd_and_leaves_the_launch_cwd_alone()
        {
            // U+202E would render the tail of the path reversed; U+200B is invisible.
            const string cwd = "C:\\proj\\.claude\\worktrees\\ab\u202Ecd\u200B";
            var entry = new SessionCatalogEntry
            {
                SessionId = "11111111-2222-3333-4444-555555555555",
                Cwd = cwd,
                LastActivityUtc = DateTime.UtcNow,
            };

            var tip = MultiTerminal.Dialogs.LaunchSessionDialog.FormatTooltip(new LaunchSessionCandidate(entry, null));

            Assert.Contains("C:\\proj\\.claude\\worktrees\\abcd\n", tip, StringComparison.Ordinal);
            Assert.DoesNotContain("\u202E", tip, StringComparison.Ordinal);
            Assert.DoesNotContain("\u200B", tip, StringComparison.Ordinal);
            Assert.Equal(cwd, entry.Cwd); // the value a resume launches from is untouched
        }
    }
}
