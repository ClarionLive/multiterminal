using MultiTerminal.Docking;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task ad7f6721: the role shown on a terminal's tab — <c>Alice - MultiTerminal (PM)</c>,
    /// <c>Nadia - CA Debugger (Helper)</c>.
    ///
    /// <para>The role and the <c>MULTITERMINAL_PROJECT_PM</c> launch variable are two renderings of ONE
    /// decision. <see cref="The_tab_label_and_the_launch_variable_never_disagree"/> is the fact that
    /// matters here: the others would all stay green if a second copy of the rule appeared in the UI
    /// and then drifted, because each checks only one of the two readers.</para>
    /// </summary>
    public class TerminalRoleTests
    {
        private const string SomeProject = "5d7853b8-c695-4684-8f32-dfad644b0669";
        private const string PmSet = "$env:MULTITERMINAL_PROJECT_PM = 'true'; ";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_terminal_the_Owner_opens_on_a_project_is_its_PM(string noSpawner)
        {
            // Project card / Project panel / New Project: a project id and no spawner. The empty-string
            // case is the same edge the SPAWNER launch branch treats as absent.
            Assert.Equal(TerminalRole.ProjectManager, TerminalRoles.Resolve(SomeProject, noSpawner));
        }

        [Fact]
        public void A_spawned_helper_is_a_helper_even_though_it_has_a_project()
        {
            // spawn_helper passes the project it resolved for the working directory AND the spawner.
            // The spawner is exactly what makes the session-start hook withhold the PM role, so it
            // has to win here too, or the tab would label a helper "PM".
            Assert.Equal(TerminalRole.Helper, TerminalRoles.Resolve(SomeProject, "Diana"));
        }

        [Fact]
        public void A_phone_spawn_is_a_helper_not_a_PM()
        {
            // Phone / gateway launches carry the spawner "ClaudeRemote" (Owner's ruling, 760827ad).
            Assert.Equal(TerminalRole.Helper, TerminalRoles.Resolve(SomeProject, "ClaudeRemote"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_terminal_with_no_project_and_no_spawner_has_no_role(string projectId)
        {
            // Just Claude, Oracle, "Launch as...". The Owner asked for PM and Helper only, so these
            // tabs must read exactly as they did before this ticket.
            Assert.Equal(TerminalRole.None, TerminalRoles.Resolve(projectId, null));
            Assert.Equal(string.Empty, TerminalRoles.TabSuffix(TerminalRoles.Resolve(projectId, null)));
        }

        [Fact]
        public void A_spawned_helper_with_no_project_is_still_a_helper()
        {
            // Not a route anyone drives today: it is here so the answer is a decision rather than an
            // accident of the project test running first. A spawner means someone else started this
            // terminal, which is the whole meaning of the label.
            Assert.Equal(TerminalRole.Helper, TerminalRoles.Resolve(null, "Diana"));
        }

        [Theory]
        [InlineData(SomeProject, null, "Alice - MultiTerminal (PM)")]
        [InlineData(SomeProject, "Diana", "Alice - MultiTerminal (Helper)")]
        [InlineData(null, null, "Alice - MultiTerminal")]
        public void The_tab_shows_the_role_after_the_project(string projectId, string spawner, string expected)
        {
            var role = TerminalRoles.Resolve(projectId, spawner);
            Assert.Equal(expected, TerminalDocument.ComposeTabTitle("Alice", "MultiTerminal", role));
        }

        [Fact]
        public void A_tab_with_no_project_name_is_just_the_name_and_the_role()
        {
            // The project name can arrive later than the agent name, so the middle part has to be
            // droppable on its own without leaving a dangling separator.
            Assert.Equal("Alice (PM)", TerminalDocument.ComposeTabTitle("Alice", null, TerminalRole.ProjectManager));
            Assert.Equal("Oracle", TerminalDocument.ComposeTabTitle("Oracle", null, TerminalRole.None));
        }

        [Fact]
        public void A_renamed_tab_keeps_its_role()
        {
            // RenameTabDialog writes CustomTitle, which UpdateTabTitle then recomposes. The role is held
            // from launch, so a cosmetic rename cannot drop or change it.
            Assert.Equal("Scratch - MultiTerminal (PM)",
                TerminalDocument.ComposeTabTitle("Scratch", "MultiTerminal", TerminalRole.ProjectManager));
        }

        /// <summary>
        /// The anti-drift fact: for every (project, spawner) pair, the tab says PM exactly when the launch
        /// sets <c>MULTITERMINAL_PROJECT_PM='true'</c>.
        ///
        /// <para>What it can and cannot catch, established by running both breaks rather than reasoning:</para>
        /// <list type="bullet">
        /// <item>Giving <see cref="ConPtyTerminal.BuildProjectPmEnvAssignment"/> its own copy of the rule
        /// that drops the spawner test — the drift this ticket exists to prevent — turns this fact RED.
        /// That is the falsification it is allowed to claim.</item>
        /// <item>Breaking the SHARED <see cref="TerminalRoles.Resolve"/> instead leaves it GREEN, because
        /// both readers then move together and still agree. Verified: that break failed six other facts
        /// in this file and this one passed. So this fact pins that the two readers share a rule, NOT
        /// that the rule is right — the per-case facts above are what pin the rule.</item>
        /// </list>
        /// </summary>
        [Fact]
        public void The_tab_label_and_the_launch_variable_never_disagree()
        {
            string[] projects = { null, "", SomeProject };
            string[] spawners = { null, "", "Diana", "ClaudeRemote" };

            foreach (var projectId in projects)
            {
                foreach (var spawner in spawners)
                {
                    bool envSaysPm = ConPtyTerminal.BuildProjectPmEnvAssignment(projectId, spawner) == PmSet;
                    bool tabSaysPm = TerminalRoles.Resolve(projectId, spawner) == TerminalRole.ProjectManager;

                    Assert.True(envSaysPm == tabSaysPm,
                        $"projectId='{projectId ?? "null"}' spawner='{spawner ?? "null"}': " +
                        $"launch variable says PM={envSaysPm} but the tab says PM={tabSaysPm}.");
                }
            }
        }
    }
}
