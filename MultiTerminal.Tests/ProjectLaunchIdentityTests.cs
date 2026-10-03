using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.Docking;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 6a8d029f: every project-backed launch with no team lead gets a unique, non-placeholder
    /// identity derived from the project ("TestB", "TestB-2"…), which never lands on another project's
    /// or a known agent's name.
    ///
    /// <para>What these facts cover: the pure rule (<see cref="ProjectLaunchIdentity"/>), the rule fed
    /// into the REAL broker's <see cref="MessageBroker.RegisterTerminalUnique"/> as
    /// <c>MainForm.TryRegisterProjectIdentity</c> does, and the tab title. What they do NOT cover: the
    /// <c>MainForm</c> launch sites themselves — that each passes the rule's flags through, builds the
    /// reserved set from the project database, and FAILS CLOSED (error + start screen, no placeholder)
    /// when registration is refused. Those are private WinForms handlers with no seam; the fail-closed
    /// branch is checked only by the Owner's live test. The facts here pin the input that branch keys
    /// on (<see cref="ProjectLaunchIdentityRequest.IsProjectDerived"/>).</para>
    /// </summary>
    public sealed class ProjectLaunchIdentityTests : IDisposable
    {
        private static readonly IReadOnlyCollection<string> NoneReserved = Array.Empty<string>();

        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public ProjectLaunchIdentityTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_projid_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_projid_msg_{stamp}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        private static ProjectLaunchIdentityRequest NoLead(string projectName, string projectId, IReadOnlyCollection<string> reserved = null) =>
            ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, projectName, projectId, reserved ?? NoneReserved);

        private static ProjectIdentitySource Proj(string id, string name, string teamLead = null, params string[] agents) =>
            new ProjectIdentitySource(id, name, teamLead, agents);

        /// <summary>What a launch site does with the rule: resolve, then register atomically when asked.</summary>
        private static string Launch(MessageBroker broker, string docId, string teamLead, string projectName, string projectId = "p1aaaaaa", IReadOnlyCollection<string> reserved = null)
        {
            var request = ProjectLaunchIdentity.Resolve(teamLead, TerminalKind.ClaudeCode, null, projectName, projectId, reserved ?? NoneReserved);
            if (request.RegisterUnique)
            {
                var unique = broker.RegisterTerminalUnique(request.Name, out string resolved, docId, nonce: "N-" + docId);
                Assert.True(unique.Success, unique.Error);
                return resolved;
            }

            var plain = broker.RegisterTerminal(request.Name, docId, isTeamLead: !string.IsNullOrEmpty(teamLead), nonce: "N-" + docId);
            Assert.True(plain.Success, plain.Error);
            return request.Name;
        }

        // ---- the basic rule -------------------------------------------------------------------------

        [Fact]
        public void Opening_a_no_lead_project_three_times_gives_TestB_then_TestB_2_then_TestB_3()
        {
            using var broker = new MessageBroker();

            Assert.Equal("TestB", Launch(broker, "D1", teamLead: null, projectName: "TestB"));
            Assert.Equal("TestB-2", Launch(broker, "D2", teamLead: null, projectName: "TestB"));
            Assert.Equal("TestB-3", Launch(broker, "D3", teamLead: null, projectName: "TestB"));

            // Three distinct broker rows, each bound to its own pane — not one shared identity.
            var rows = broker.GetAllConnectedTerminals().Where(t => t.Name.StartsWith("TestB", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "D1", "D2", "D3" }, rows.OrderBy(t => t.DocId).Select(t => t.DocId));
            Assert.Equal(3, rows.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(broker.GetAllConnectedTerminals(), t => t.Name == ProjectLaunchIdentity.Unassigned);
        }

        [Fact]
        public void A_no_lead_project_requests_its_name_uniquely_for_both_terminal_kinds()
        {
            var expected = new ProjectLaunchIdentityRequest("TestB", RegisterUnique: true, IsProjectDerived: true);
            Assert.Equal(expected, ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, "TestB", "p1aaaaaa", NoneReserved));
            Assert.Equal(expected, ProjectLaunchIdentity.Resolve("", TerminalKind.Codex, null, "TestB", "p1aaaaaa", NoneReserved));
        }

        [Fact]
        public void A_team_lead_project_is_unchanged_its_lead_registered_plainly()
        {
            // RegisterUnique=false keeps the second launch on the IdentityPicker rather than a silent "-2".
            var expected = new ProjectLaunchIdentityRequest("Alice", RegisterUnique: false, IsProjectDerived: false);
            Assert.Equal(expected, ProjectLaunchIdentity.Resolve("Alice", TerminalKind.ClaudeCode, "CodexBot", "TestB", "p1aaaaaa", NoneReserved));
            Assert.Equal(expected, ProjectLaunchIdentity.Resolve("Alice", TerminalKind.Codex, "CodexBot", "TestB", "p1aaaaaa", NoneReserved));
        }

        [Fact]
        public void A_configured_Codex_default_keeps_precedence_for_Codex_only()
        {
            Assert.Equal(new ProjectLaunchIdentityRequest("CodexBot", true, false),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.Codex, "CodexBot", "TestB", "p1aaaaaa", NoneReserved));
            // The setting is Codex's; a Claude launch of the same project ignores it.
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true, true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, "CodexBot", "TestB", "p1aaaaaa", NoneReserved));
            // "Unassigned" as the setting meant "behave like Claude Code", which is now the project identity.
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true, true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.Codex, "unassigned", "TestB", "p1aaaaaa", NoneReserved));
        }

        [Fact]
        public void A_project_named_like_a_connected_agent_is_suffixed_never_shared()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NA");

            // Even with an empty census the broker refuses to share a CONNECTED name.
            Assert.Equal("Alice-2", Launch(broker, "DP", teamLead: null, projectName: "Alice"));

            var alice = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Alice");
            Assert.Equal("DA", alice.DocId);
            Assert.Equal(8801, alice.ChannelPort);
        }

        [Fact]
        public void A_closed_suffix_is_reused_by_the_next_launch()
        {
            // PM decision overnight (Alice, 2026-10-02): reuse is intended — the next "TestB-2" inherits
            // that name's profile, active task and inbox. This pins the decision, not an accident.
            using var broker = new MessageBroker();
            Launch(broker, "D1", teamLead: null, projectName: "TestB");
            Assert.Equal("TestB-2", Launch(broker, "D2", teamLead: null, projectName: "TestB"));

            broker.UnregisterTerminal("D2");

            Assert.Equal("TestB-2", Launch(broker, "D3", teamLead: null, projectName: "TestB"));
        }

        // ---- Run 1 blocking #1: never the placeholder for a project-backed launch ------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Проект")]
        [InlineData("项目")]
        [InlineData("🚀🔥")]
        [InlineData("!!!")]
        [InlineData("Unassigned")]
        [InlineData(" UNASSIGNED ")]
        public void A_project_name_with_nothing_usable_gets_a_project_id_identity_not_the_placeholder(string projectName)
        {
            var a = NoLead(projectName, "a1b2c3d4");
            var b = NoLead(projectName, "e5f6a7b8");

            Assert.Equal(new ProjectLaunchIdentityRequest("Project-a1b2c3", true, true), a);
            Assert.Equal(new ProjectLaunchIdentityRequest("Project-e5f6a7", true, true), b);
        }

        [Fact]
        public void Only_the_exact_placeholder_name_is_refused()
        {
            // Control: the check is an exact match on the sanitized name, not a substring.
            Assert.Equal(new ProjectLaunchIdentityRequest("Unassigned-Work", true, true), NoLead("Unassigned Work", "a1b2c3d4"));
        }

        // ---- Run 1 blocking #2: reserved names ------------------------------------------------------

        [Fact]
        public void Projects_whose_names_sanitize_alike_get_different_identities()
        {
            var projects = new[] { Proj("aaaa1111", "Foo Bar"), Proj("bbbb2222", "Foo-Bar"), Proj("cccc3333", "Foo@Bar") };
            string For(ProjectIdentitySource p) =>
                NoLead(p.Name, p.Id, ProjectLaunchIdentity.ReservedNames(p.Id, projects, null)).Name;

            Assert.Equal("Foo-Bar-aaaa", For(projects[0]));
            Assert.Equal("Foo-Bar-bbbb", For(projects[1]));
            Assert.Equal("Foo-Bar-cccc", For(projects[2]));
        }

        [Fact]
        public void A_project_alone_keeps_its_plain_name()
        {
            // Its own name is not reserved against itself; only OTHER projects' names are.
            var projects = new[] { Proj("aaaa1111", "TestB"), Proj("bbbb2222", "Other") };
            Assert.Equal("TestB", NoLead("TestB", "aaaa1111", ProjectLaunchIdentity.ReservedNames("aaaa1111", projects, null)).Name);
        }

        [Fact]
        public void A_project_named_like_another_projects_team_lead_is_id_qualified_while_that_agent_is_offline()
        {
            var projects = new[] { Proj("lead0001", "MultiTerminal", teamLead: "Diana"), Proj("dian0002", "Diana") };
            var reserved = ProjectLaunchIdentity.ReservedNames("dian0002", projects, null);

            // No broker row for Diana at all: the census, not liveness, protects her.
            Assert.Equal(new ProjectLaunchIdentityRequest("Diana-dian", true, true), NoLead("Diana", "dian0002", reserved));
        }

        [Fact]
        public void A_project_named_like_a_roster_agent_is_id_qualified()
        {
            var projects = new[] { Proj("prj00001", "Shop", null, "Nadia", "Bob"), Proj("bob00002", "Bob") };
            var reserved = ProjectLaunchIdentity.ReservedNames("bob00002", projects, null);

            Assert.Equal("Bob-bob0", NoLead("Bob", "bob00002", reserved).Name);
        }

        [Fact]
        public void A_project_named_Oracle_or_the_Codex_default_is_id_qualified()
        {
            var projects = new[] { Proj("orac0001", "Oracle"), Proj("code0002", "CodexBot") };
            var extra = new[] { OracleService.OracleName, "CodexBot" };

            Assert.Equal("Oracle-orac", NoLead("Oracle", "orac0001", ProjectLaunchIdentity.ReservedNames("orac0001", projects, extra)).Name);
            Assert.Equal("CodexBot-code", NoLead("CodexBot", "code0002", ProjectLaunchIdentity.ReservedNames("code0002", projects, extra)).Name);
        }

        [Fact]
        public void Two_projects_sharing_a_64_char_prefix_get_different_identities()
        {
            string prefix = new string('x', 64);
            var projects = new[] { Proj("aaaa1111", prefix + "-one"), Proj("bbbb2222", prefix + "-two") };

            string a = NoLead(projects[0].Name, projects[0].Id, ProjectLaunchIdentity.ReservedNames(projects[0].Id, projects, null)).Name;
            string b = NoLead(projects[1].Name, projects[1].Id, ProjectLaunchIdentity.ReservedNames(projects[1].Id, projects, null)).Name;

            Assert.NotEqual(a, b, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(prefix + "-aaaa", a);
        }

        [Fact]
        public void The_census_reserves_leads_rosters_other_projects_and_extras_but_not_this_project()
        {
            var projects = new[]
            {
                Proj("this0001", "Mine", null, "Rosa"),
                Proj("othr0002", "Other Thing", "Lena", "Sam"),
                Proj("emoj0003", "🚀"),
            };
            var reserved = ProjectLaunchIdentity.ReservedNames("this0001", projects, new[] { "Oracle", null, "" });

            Assert.Equal(
                new[] { "Lena", "Oracle", "Other-Thing", "Project-emoj00", "Rosa", "Sam" },
                reserved.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Contains("lena", reserved); // case-insensitive
        }

        // ---- Run 1 blocking #3: the fail-closed switch ----------------------------------------------

        [Theory]
        [InlineData("TestB")]
        [InlineData("🚀")]
        [InlineData("Unassigned")]
        [InlineData("Diana")]
        public void Every_no_lead_project_launch_is_a_fail_closed_project_identity_and_never_the_placeholder(string projectName)
        {
            // MainForm keys "no placeholder fallback on refusal" on IsProjectDerived. The branch itself has
            // no seam (private WinForms handler); this pins the input it decides on.
            var request = NoLead(projectName, "p1aaaaaa", new[] { "Diana" });

            Assert.True(request.IsProjectDerived);
            Assert.True(request.RegisterUnique);
            Assert.NotEqual(ProjectLaunchIdentity.Unassigned, request.Name, StringComparer.OrdinalIgnoreCase);
        }

        // ---- Run 2 blocking: the census is all-or-nothing --------------------------------------------

        private static readonly (string Id, string Name, string TeamLead)[] ThreeProjects =
        {
            ("this0001", "Mine", null),
            ("othr0002", "Other", "Lena"),
            ("thrd0003", "Third", null),
        };

        private static IEnumerable<string> Rosters(string id) => id switch
        {
            "this0001" => new[] { "Rosa" },
            "othr0002" => new[] { "Sam" },
            _ => new[] { "Tom" },
        };

        [Fact]
        public void A_census_with_no_database_is_a_failure_not_an_empty_set()
        {
            var both = ProjectLaunchIdentity.TryTakeCensus("this0001", null, null, new[] { "Oracle" });
            var noRoster = ProjectLaunchIdentity.TryTakeCensus("this0001", () => ThreeProjects, null, new[] { "Oracle" });

            Assert.False(both.Succeeded);
            Assert.Null(both.Names);
            Assert.False(noRoster.Succeeded);
            Assert.Null(noRoster.Names);
        }

        [Fact]
        public void A_census_whose_project_query_throws_or_returns_null_fails()
        {
            var threw = ProjectLaunchIdentity.TryTakeCensus("this0001", () => throw new InvalidOperationException("database is locked"), Rosters, null);
            var nulled = ProjectLaunchIdentity.TryTakeCensus("this0001", () => null, Rosters, null);

            Assert.False(threw.Succeeded);
            Assert.Null(threw.Names);
            Assert.Contains("database is locked", threw.Error, StringComparison.Ordinal);
            Assert.False(nulled.Succeeded);
            Assert.Null(nulled.Names);
        }

        [Fact]
        public void A_census_whose_roster_query_fails_on_the_second_project_publishes_nothing()
        {
            int calls = 0;
            IEnumerable<string> FailSecond(string id)
            {
                calls++;
                if (calls == 2) throw new InvalidOperationException("database is locked");
                return Rosters(id);
            }

            var census = ProjectLaunchIdentity.TryTakeCensus("this0001", () => ThreeProjects, FailSecond, new[] { "Oracle" });

            Assert.Equal(2, calls); // the failure really happened midway, after one roster succeeded
            Assert.False(census.Succeeded);
            Assert.Null(census.Names); // no partial set
        }

        [Fact]
        public void A_census_whose_roster_query_returns_null_fails()
        {
            var census = ProjectLaunchIdentity.TryTakeCensus("this0001", () => ThreeProjects, id => id == "thrd0003" ? null : Rosters(id), null);

            Assert.False(census.Succeeded);
            Assert.Null(census.Names);
        }

        [Fact]
        public void A_census_where_every_query_succeeds_is_the_full_set()
        {
            var census = ProjectLaunchIdentity.TryTakeCensus("this0001", () => ThreeProjects, Rosters, new[] { "Oracle", "CodexBot" });

            Assert.True(census.Succeeded);
            Assert.Null(census.Error);
            Assert.Equal(
                new[] { "CodexBot", "Lena", "Oracle", "Other", "Rosa", "Sam", "Third", "Tom" },
                census.Names.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void A_project_derived_identity_cannot_be_resolved_without_a_census()
        {
            // A null reserved set must never read as "nothing reserved".
            Assert.Throws<ArgumentNullException>(() =>
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, "TestB", "p1aaaaaa", null));

            // Team-lead and Codex-default launches do not need it, so a census failure cannot block them.
            Assert.Equal("Alice", ProjectLaunchIdentity.Resolve("Alice", TerminalKind.ClaudeCode, null, "TestB", "p1aaaaaa", null).Name);
            Assert.Equal("CodexBot", ProjectLaunchIdentity.Resolve(null, TerminalKind.Codex, "CodexBot", "TestB", "p1aaaaaa", null).Name);
        }

        [Theory]
        [InlineData("Alice", TerminalKind.ClaudeCode, null, false)]
        [InlineData("Alice", TerminalKind.Codex, "CodexBot", false)]
        [InlineData(null, TerminalKind.Codex, "CodexBot", false)]
        [InlineData(null, TerminalKind.Codex, "Unassigned", true)]
        [InlineData(null, TerminalKind.Codex, "  ", true)]
        [InlineData(null, TerminalKind.ClaudeCode, "CodexBot", true)]
        [InlineData("", TerminalKind.ClaudeCode, null, true)]
        public void Only_a_project_derived_launch_needs_the_census(string teamLead, TerminalKind kind, string codexDefault, bool expected)
        {
            Assert.Equal(expected, ProjectLaunchIdentity.NeedsProjectDerivedIdentity(teamLead, kind, codexDefault));
            // And it agrees with what Resolve actually produces.
            var request = ProjectLaunchIdentity.Resolve(teamLead, kind, codexDefault, "TestB", "p1aaaaaa", NoneReserved);
            Assert.Equal(expected, request.IsProjectDerived);
        }

        // ---- Run 3 blocking: a project-derived launch requires the broker -----------------------------

        [Theory]
        [InlineData("TestB", null, TerminalKind.ClaudeCode, null)]      // no lead, Claude: project-derived
        [InlineData("TestB", null, TerminalKind.Codex, null)]           // no lead, Codex, no default: project-derived
        [InlineData("🚀", null, TerminalKind.ClaudeCode, null)]          // Project-<id6> fallback: project-derived
        public void A_project_derived_launch_with_no_broker_is_refused_whatever_the_registration_says(string projectName, string teamLead, TerminalKind kind, string codexDefault)
        {
            var request = ProjectLaunchIdentity.Resolve(teamLead, kind, codexDefault, projectName, "p1aaaaaa", NoneReserved);
            Assert.True(request.IsProjectDerived);

            // Pre-check (before registering) and post-registration both refuse: no broker means no real name.
            Assert.Equal(ProjectLaunchIdentity.BrokerUnavailableMessage,
                ProjectLaunchIdentity.StartRefusal(request.IsProjectDerived, brokerAvailable: false));
            Assert.Equal(ProjectLaunchIdentity.BrokerUnavailableMessage,
                ProjectLaunchIdentity.StartRefusal(request.IsProjectDerived, brokerAvailable: false, registered: false, registrationError: "x"));
        }

        [Theory]
        [InlineData("Alice", TerminalKind.ClaudeCode, null)]     // team lead
        [InlineData(null, TerminalKind.Codex, "CodexBot")]       // Codex default agent
        public void A_launch_that_is_not_project_derived_is_never_refused_by_this_gate(string teamLead, TerminalKind kind, string codexDefault)
        {
            // Unchanged behaviour: team-lead and Codex-default launches keep their own broker handling, and
            // Open PowerShell never asks this gate (no project, so never project-derived).
            var request = ProjectLaunchIdentity.Resolve(teamLead, kind, codexDefault, "TestB", "p1aaaaaa", null);
            Assert.False(request.IsProjectDerived);

            Assert.Null(ProjectLaunchIdentity.StartRefusal(request.IsProjectDerived, brokerAvailable: false));
            Assert.Null(ProjectLaunchIdentity.StartRefusal(request.IsProjectDerived, brokerAvailable: true, registered: false, registrationError: "x"));
        }

        [Fact]
        public void A_project_derived_launch_starts_only_once_registered()
        {
            Assert.Null(ProjectLaunchIdentity.StartRefusal(isProjectDerived: true, brokerAvailable: true));
            Assert.Null(ProjectLaunchIdentity.StartRefusal(isProjectDerived: true, brokerAvailable: true, registered: true));
            Assert.Equal("name held",
                ProjectLaunchIdentity.StartRefusal(isProjectDerived: true, brokerAvailable: true, registered: false, registrationError: "name held"));
            // A refusal with no reason is still a refusal, never a silent start.
            Assert.False(string.IsNullOrWhiteSpace(
                ProjectLaunchIdentity.StartRefusal(isProjectDerived: true, brokerAvailable: true, registered: false, registrationError: null)));
        }

        // ---- sanitization ----------------------------------------------------------------------------

        [Theory]
        [InlineData("TestB", "TestB")]
        [InlineData("My Project", "My-Project")]
        [InlineData("  Clarion  Tools  ", "Clarion-Tools")]
        [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
        [InlineData("Agent Smith", "Agent-Smith")]
        [InlineData("release_2.1 (beta)", "release_2-1-beta")]
        [InlineData("--x--", "x")]
        [InlineData("Café Bar", "Caf-Bar")]
        [InlineData("O'Brien", "O-Brien")]
        public void A_project_name_becomes_a_file_and_shell_safe_identity(string projectName, string expected)
        {
            string identity = ProjectLaunchIdentity.FromProjectName(projectName);

            Assert.Equal(expected, identity);
            // The shared alphabet TerminalDocument.IsSafeStatusLineSegment also uses.
            Assert.All(identity, c => Assert.True(ProjectLaunchIdentity.IsIdentityChar(c), $"unsafe character '{c}' in '{identity}'"));
            Assert.False(identity.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData('/')]
        [InlineData(' ')]
        [InlineData('.')]
        [InlineData('*')]
        [InlineData('é')]
        public void The_identity_alphabet_excludes_path_glob_and_non_ASCII_characters(char c)
        {
            Assert.False(ProjectLaunchIdentity.IsIdentityChar(c));
        }

        [Fact]
        public void A_long_project_name_is_capped_without_a_trailing_separator()
        {
            string name = new string('a', 63) + " b" + new string('c', 40);
            string identity = ProjectLaunchIdentity.FromProjectName(name);

            Assert.Equal(new string('a', 63), identity);
            Assert.True(identity.Length <= ProjectLaunchIdentity.MaxBaseLength);
        }

        // ---- tab title -------------------------------------------------------------------------------

        [Theory]
        [InlineData("TestB", "TestB", false, "TestB")]
        [InlineData("TestB-2", "TestB", true, "TestB-2 (PM)")]
        [InlineData("testb-10", "TestB", false, "testb-10")]
        [InlineData("My-Project-3", "My Project", false, "My-Project-3")]
        public void A_tab_whose_identity_is_the_project_shows_just_the_identity(string agent, string project, bool isPm, string expected)
        {
            var role = isPm ? TerminalRole.ProjectManager : TerminalRole.None;
            Assert.Equal(expected, TerminalDocument.ComposeTabTitle(agent, project, role));
        }

        [Theory]
        [InlineData("Alice", "TestB", "Alice - TestB")]
        [InlineData("TestBob", "TestB", "TestBob - TestB")]
        [InlineData("TestB-x", "TestB", "TestB-x - TestB")]
        [InlineData("TestB-", "TestB", "TestB- - TestB")]
        [InlineData("Project-a1b2c3", "🚀", "Project-a1b2c3 - 🚀")]
        public void A_tab_whose_identity_is_not_the_project_keeps_both(string agent, string project, string expected)
        {
            Assert.Equal(expected, TerminalDocument.ComposeTabTitle(agent, project, TerminalRole.None));
        }
    }
}
