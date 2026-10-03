using System;
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
    /// Task 6a8d029f: a project with no team lead launches under its project name, made unique by the
    /// broker ("TestB", "TestB-2"…), instead of the shared "Unassigned".
    ///
    /// <para>What these facts cover: the pure rule (<see cref="ProjectLaunchIdentity"/>), the rule fed
    /// into the REAL broker's <see cref="MessageBroker.RegisterTerminalUnique"/> exactly as
    /// <c>MainForm.PreRegisterTerminalWithName</c> does with <c>atomicUniqueness</c>, and the tab title.
    /// What they do NOT cover: that each <c>MainForm</c> launch site passes <c>Unique</c> through to
    /// <c>atomicUniqueness</c>. Those sites are private WinForms handlers; that wiring is checked by the
    /// Owner's live test, not here.</para>
    /// </summary>
    public sealed class ProjectLaunchIdentityTests : IDisposable
    {
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

        /// <summary>What a launch site does with the rule: resolve, then register atomically when asked.</summary>
        private static string Launch(MessageBroker broker, string docId, string teamLead, string projectName, TerminalKind kind = TerminalKind.ClaudeCode)
        {
            var request = ProjectLaunchIdentity.Resolve(teamLead, kind, null, projectName);
            if (request.Unique)
            {
                var unique = broker.RegisterTerminalUnique(request.Name, out string resolved, docId, nonce: "N-" + docId);
                Assert.True(unique.Success, unique.Error);
                return resolved;
            }

            var plain = broker.RegisterTerminal(request.Name, docId, isTeamLead: !string.IsNullOrEmpty(teamLead), nonce: "N-" + docId);
            Assert.True(plain.Success, plain.Error);
            return request.Name;
        }

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
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, "TestB"));
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true),
                ProjectLaunchIdentity.Resolve("", TerminalKind.Codex, null, "TestB"));
        }

        [Fact]
        public void A_team_lead_project_is_unchanged_its_lead_registered_plainly()
        {
            // Unique=false is what keeps the second launch on the IdentityPicker rather than a silent "-2".
            Assert.Equal(new ProjectLaunchIdentityRequest("Alice", false),
                ProjectLaunchIdentity.Resolve("Alice", TerminalKind.ClaudeCode, "CodexBot", "TestB"));
            Assert.Equal(new ProjectLaunchIdentityRequest("Alice", false),
                ProjectLaunchIdentity.Resolve("Alice", TerminalKind.Codex, "CodexBot", "TestB"));
        }

        [Fact]
        public void A_configured_Codex_default_keeps_precedence_for_Codex_only()
        {
            Assert.Equal(new ProjectLaunchIdentityRequest("CodexBot", true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.Codex, "CodexBot", "TestB"));
            // The setting is Codex's; a Claude launch of the same project ignores it.
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, "CodexBot", "TestB"));
            // "Unassigned" as the setting meant "behave like Claude Code", which is now the project name.
            Assert.Equal(new ProjectLaunchIdentityRequest("TestB", true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.Codex, "unassigned", "TestB"));
        }

        [Fact]
        public void A_project_named_like_a_connected_agent_is_suffixed_never_shared()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NA");

            Assert.Equal("Alice-2", Launch(broker, "DP", teamLead: null, projectName: "Alice"));

            // The real Alice's row is untouched: same pane, same port.
            var alice = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Alice");
            Assert.Equal("DA", alice.DocId);
            Assert.Equal(8801, alice.ChannelPort);
        }

        [Fact]
        public void A_closed_suffix_is_reused_by_the_next_launch()
        {
            // DECIDED OVERNIGHT (Alice, 2026-10-02): reuse is intended — the next "TestB-2" inherits
            // that name's profile, active task and inbox. This pins the decision, not an accident.
            using var broker = new MessageBroker();
            Launch(broker, "D1", teamLead: null, projectName: "TestB");
            Assert.Equal("TestB-2", Launch(broker, "D2", teamLead: null, projectName: "TestB"));

            broker.UnregisterTerminal("D2");

            Assert.Equal("TestB-2", Launch(broker, "D3", teamLead: null, projectName: "TestB"));
        }

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
            // The alphabet TerminalDocument.IsSafeStatusLineSegment accepts, so every file named after
            // the identity (inbox <name>.json, mt-statusline-<name>-<docId>.json) is a plain segment.
            Assert.All(identity, c => Assert.True(
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_',
                $"unsafe character '{c}' in '{identity}'"));
            // Never the temporary-subagent shape the broker ignores.
            Assert.False(identity.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        [InlineData("Unassigned")]
        [InlineData(" UNASSIGNED ")]
        public void A_project_name_with_nothing_usable_falls_back_to_the_shared_placeholder(string projectName)
        {
            // Unique=false: the broker exempts the placeholder from suffixing anyway, and this is exactly
            // what these launches did before 6a8d029f (also what a project-less call gets).
            Assert.Equal(new ProjectLaunchIdentityRequest(ProjectLaunchIdentity.Unassigned, false),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, projectName));
        }

        [Fact]
        public void Only_the_exact_placeholder_name_is_refused()
        {
            // Control for the fallback above: the check is an exact match on the sanitized name, not a
            // substring, so a project merely containing the word still gets its own identity.
            Assert.Equal(new ProjectLaunchIdentityRequest("Unassigned-Work", true),
                ProjectLaunchIdentity.Resolve(null, TerminalKind.ClaudeCode, null, "Unassigned Work"));
        }

        [Fact]
        public void A_long_project_name_is_capped_without_a_trailing_separator()
        {
            string name = new string('a', 63) + " b" + new string('c', 40);
            string identity = ProjectLaunchIdentity.FromProjectName(name);

            Assert.Equal(new string('a', 63), identity);
            Assert.True(identity.Length <= ProjectLaunchIdentity.MaxBaseLength);
        }

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
        public void A_tab_whose_identity_is_not_the_project_keeps_both(string agent, string project, string expected)
        {
            Assert.Equal(expected, TerminalDocument.ComposeTabTitle(agent, project, TerminalRole.None));
        }
    }
}
