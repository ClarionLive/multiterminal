using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The launch prompt's "Also resume helpers" logic (task 7d20e94c item 5):
    /// <see cref="LaunchSessionChoiceModel.HelpersFor"/>, <see cref="LaunchSessionChoiceModel.HelperWhere"/>
    /// and <see cref="HelperTickState"/>. The dialog only draws what these decide.
    /// </summary>
    public sealed class LaunchSessionHelperChoiceTests
    {
        private const string PmA = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string PmB = "bbbbbbbb-0000-0000-0000-000000000002";
        private const string H1 = "11111111-0000-0000-0000-000000000001";
        private const string H2 = "22222222-0000-0000-0000-000000000002";
        private static readonly DateTime T = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

        private static SessionCatalogEntry Entry(string sid, int minutesAgo) => new SessionCatalogEntry
        {
            SessionId = sid,
            Cwd = @"C:\proj",
            LastActivityUtc = T.AddMinutes(-minutesAgo),
            Status = SessionCatalogStatus.Available,
        };

        private static OpenHelper Helper(string sid, string name) =>
            new OpenHelper { SessionId = sid, TerminalName = name, Kind = "claude", Cwd = @"C:\proj" };

        private static LaunchSessionChoiceModel Model(Func<string, IReadOnlyList<OpenHelper>> lookup, params PaneSessionState[] panes) =>
            new LaunchSessionChoiceModel(new[] { Entry(PmA, 10), Entry(PmB, 20) }, panes) { HelperLookup = lookup };

        private static LaunchSessionCandidate Candidate(LaunchSessionChoiceModel m, string sid) =>
            m.Candidates.Single(c => c.Entry.SessionId == sid);

        [Fact]
        public void Without_a_lookup_no_helpers_are_offered()
        {
            var m = new LaunchSessionChoiceModel(new[] { Entry(PmA, 10) }, null);
            Assert.Empty(m.HelpersFor(m.Candidates[0]));
            Assert.Empty(m.HelpersFor(null));
        }

        [Fact]
        public void Helpers_whose_session_is_live_are_left_out()
        {
            var m = Model(_ => new[] { Helper(H1, "Notes"), Helper(H2, "Bob") },
                new PaneSessionState(true, H2, T));
            Assert.Equal(new[] { H1 }, m.HelpersFor(Candidate(m, PmA)).Select(h => h.SessionId));
        }

        [Fact]
        public void The_lookup_runs_once_per_session_however_often_it_is_asked()
        {
            var asked = new List<string>();
            var m = Model(sid => { asked.Add(sid); return new[] { Helper(H1, "Notes") }; });
            for (int i = 0; i < 3; i++)
            {
                m.HelpersFor(Candidate(m, PmA));
                m.HelpersFor(Candidate(m, PmB));
            }

            Assert.Equal(new[] { PmA, PmB }, asked);
        }

        [Fact]
        public void A_failing_lookup_offers_no_helpers()
        {
            var m = Model(_ => throw new InvalidOperationException("db down"));
            Assert.Empty(m.HelpersFor(Candidate(m, PmA)));
        }

        [Theory]
        [InlineData(@"C:\proj", "project folder")]
        [InlineData(@"C:\proj\.claude\worktrees\7d20e94c", "worktree 7d20e94c")]
        [InlineData(@"C:\proj\.claude\worktrees\7d20e94c\sub", "worktree 7d20e94c")]
        [InlineData("C:/proj/.claude/worktrees/abc", "worktree abc")]
        [InlineData(null, "—")]
        [InlineData("  ", "—")]
        public void Where_names_the_project_folder_or_the_worktree(string cwd, string expected)
        {
            Assert.Equal(expected, LaunchSessionChoiceModel.HelperWhere(cwd));
        }

        [Fact]
        public void Every_helper_starts_ticked_and_an_untick_belongs_to_one_session()
        {
            var m = Model(_ => new[] { Helper(H1, "Notes"), Helper(H2, "Bob") });
            var ticks = new HelperTickState();
            var a = Candidate(m, PmA);
            var b = Candidate(m, PmB);

            Assert.Equal(new[] { H1, H2 }, ticks.TickedFor(m, a).Select(h => h.SessionId));

            Assert.False(ticks.Toggle(PmA, H1));
            Assert.Equal(new[] { H2 }, ticks.TickedFor(m, a).Select(h => h.SessionId));
            Assert.Equal(new[] { H1, H2 }, ticks.TickedFor(m, b).Select(h => h.SessionId));

            // Ids are GUIDs: case does not make a second key.
            Assert.True(ticks.Toggle(PmA.ToUpperInvariant(), H1.ToUpperInvariant()));
            Assert.Equal(new[] { H1, H2 }, ticks.TickedFor(m, a).Select(h => h.SessionId));
        }

        [Fact]
        public void A_new_session_resumes_no_helpers()
        {
            var m = Model(_ => new[] { Helper(H1, "Notes") });
            Assert.Empty(new HelperTickState().TickedFor(m, null));
        }
    }
}
