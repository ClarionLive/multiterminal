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
        private const string OtherProject = "9f2a1c44-0b73-4e55-8a10-6cc2d9e41b7f";

        // The production const, not a retyped copy of it. Here the string is a DECODER — it answers
        // "did the launch set PM?" — and is not itself under test (ConPtyTerminalProjectPmTests pins the
        // literal per case). Retyping it means a harmless change to the const's spacing or casing makes
        // envSaysPm false for every pair, and the agreement fact below goes red accusing the code of drift
        // when nothing drifted: the manufactured-failure direction .claude/rules/verification-discipline.md
        // warns about, in the one test whose whole job is to be believed.
        private const string PmSet = ConPtyTerminal.ProjectPmSetAssignment;

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

        [Theory]
        [InlineData("Unassigned")]
        [InlineData("unassigned")]
        public void An_unassigned_team_lead_shows_only_the_project(string placeholder)
        {
            // GH #26: a project with no team lead launches under the "Unassigned" placeholder, and the
            // tab used to read "Unassigned - MultiTerminal". The role suffix is still appended.
            Assert.Equal("MultiTerminal", TerminalDocument.ComposeTabTitle(placeholder, "MultiTerminal", TerminalRole.None));
            Assert.Equal("MultiTerminal (PM)",
                TerminalDocument.ComposeTabTitle(placeholder, "MultiTerminal", TerminalRole.ProjectManager));
        }

        [Fact]
        public void An_unassigned_terminal_with_no_project_keeps_its_placeholder()
        {
            // With no project the placeholder is the tab's only text; dropping it would leave a blank tab.
            Assert.Equal("Unassigned", TerminalDocument.ComposeTabTitle("Unassigned", null, TerminalRole.None));
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

        // ---------------------------------------------------------------------------------------------
        // ForDisplay — withdrawing the badge when the agent walks away from the project it manages.
        //
        // Found by the pipeline (Run 1) AFTER the facts above were green, by the debugger and the
        // cross-model adversary independently. The facts above pin that ONE rule decides the role; they
        // cannot see this, because the tab is composed from TWO values and only the role was launch-scoped.
        // _projectName keeps moving — the statusline poll re-resolves it from the agent's current folder —
        // so a frozen role beside a moving project name eventually reads "Alice - OtherProject (PM)".
        // ---------------------------------------------------------------------------------------------

        // Every fact below asserts the COMPOSED TAB TEXT, not only the enum. That is not belt-and-braces:
        // the pipeline's Run 2 defect was live underneath a green enum-only assertion here, because the lie
        // ("Alice - Scratch (PM)") exists only in the composition — the role and the project name are each
        // defensible alone and wrong together. A fact about this rule that never composes cannot see it.

        [Fact]
        public void A_PM_who_walks_into_another_project_stops_being_labelled_its_PM()
        {
            // The defect, stated as the thing that must not happen: PM of SomeProject, agent now in
            // OtherProject. MULTITERMINAL_PROJECT_PM still says true — it is fixed in the child env at
            // launch — so the tab is the ONLY thing that can stop over-claiming here.
            var shown = TerminalRoles.ForDisplay(TerminalRole.ProjectManager, SomeProject, OtherProject, true);

            Assert.Equal(TerminalRole.None, shown);
            Assert.Equal("Alice - OtherProject", TerminalDocument.ComposeTabTitle("Alice", "OtherProject", shown));
        }

        [Fact]
        public void A_PM_still_in_its_own_project_keeps_the_badge()
        {
            // This is the ordinary state of a PM terminal, including one sitting in a worktree: the poll
            // resolves a worktree under .claude\worktrees\ back to its containing registered project, so the
            // current id EQUALS the launch id. That containment behaviour is pinned in ProjectPathResolverTests,
            // NOT here — this fact only asserts what equal ids produce. Getting it wrong would strip "(PM)"
            // from most real PM terminals.
            var shown = TerminalRoles.ForDisplay(TerminalRole.ProjectManager, SomeProject, SomeProject, true);

            Assert.Equal(TerminalRole.ProjectManager, shown);
            Assert.Equal("Alice - MultiTerminal (PM)", TerminalDocument.ComposeTabTitle("Alice", "MultiTerminal", shown));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_folder_in_no_registered_project_is_a_departure(string noMatch)
        {
            // The lookup RAN and matched nothing, so the agent is positively outside every registered
            // project — including the one it manages. Withdraw.
            //
            // This is the fact that was wrong. Its previous form asserted the opposite ("an unresolved folder
            // is not evidence of leaving") and was GREEN while the tab read "Alice - Scratch (PM)", because it
            // checked only the enum and never composed the title. The composed assertion below is the part
            // that would have caught it: _projectName is downgraded to the folder's leaf name whether or not a
            // project resolved, so the role is the only half left that can tell the truth.
            var shown = TerminalRoles.ForDisplay(TerminalRole.ProjectManager, SomeProject, noMatch, true);

            Assert.Equal(TerminalRole.None, shown);
            Assert.Equal("Alice - Scratch", TerminalDocument.ComposeTabTitle("Alice", "Scratch", shown));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(OtherProject)]
        public void A_lookup_that_could_not_run_changes_nothing(string whatever)
        {
            // The other half of the distinction: currentProjectKnown false means the registry was empty or
            // absent, or the resolver threw — we never learned where the agent is. Guessing "departed" here
            // would STICK, because the poll only re-runs when the folder CHANGES, so a badge dropped on a
            // transient failure stays dropped until the agent happens to move again. The id argument is
            // ignored entirely, which is why even OtherProject must keep the badge.
            Assert.Equal(
                TerminalRole.ProjectManager,
                TerminalRoles.ForDisplay(TerminalRole.ProjectManager, SomeProject, whatever, false));
        }

        [Fact]
        public void Walking_back_into_the_project_restores_the_badge()
        {
            // ForDisplay is derived, never destructive — _terminalRole still holds what the launch granted.
            // A one-way "set the role to None on departure" would look identical in the departure facts above
            // and leave the tab permanently unlabelled for the rest of the session.
            var launched = TerminalRoles.Resolve(SomeProject, null);

            Assert.Equal(TerminalRole.None, TerminalRoles.ForDisplay(launched, SomeProject, OtherProject, true));
            Assert.Equal(TerminalRole.None, TerminalRoles.ForDisplay(launched, SomeProject, null, true));
            Assert.Equal(TerminalRole.ProjectManager, TerminalRoles.ForDisplay(launched, SomeProject, SomeProject, true));
        }

        [Fact]
        public void A_helper_keeps_its_label_wherever_it_goes()
        {
            // Only PM is project-scoped, because only PM is a claim ABOUT the named project. "(Helper)"
            // says who spawned this terminal, which stays true in any folder. Scoping it too would strip
            // the label from every helper that cd's anywhere, for no gain in honesty.
            var helper = TerminalRoles.ForDisplay(TerminalRole.Helper, SomeProject, OtherProject, true);

            Assert.Equal(TerminalRole.Helper, helper);
            Assert.Equal("Nadia - OtherProject (Helper)",
                TerminalDocument.ComposeTabTitle("Nadia", "OtherProject", helper));

            // And a terminal granted nothing is never granted something by moving.
            Assert.Equal(
                TerminalRole.None,
                TerminalRoles.ForDisplay(TerminalRole.None, SomeProject, OtherProject, true));
        }
    }
}
