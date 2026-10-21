using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 3e1c09b8: the startup restore offer (which rows, nested how, ticked how) and where each
    /// restored session goes (which restored pane, which panes close).
    /// </summary>
    public sealed class StartupRestoreTests
    {
        private static RestorablePane Pane(string id, string name, string project = "p", string parent = null, string cwd = @"C:\proj") => new RestorablePane
        {
            LaunchId = id,
            DocId = "doc-" + id,
            TerminalName = name,
            ProjectId = project,
            Cwd = cwd,
            SessionId = Guid.NewGuid().ToString(),
            ParentLaunchId = parent,
        };

        private static string Projects(string id) => id == "p" ? "MultiTerminal" : id == "q" ? "Other" : null;

        // ---- the offer ----

        [Fact]
        public void Rows_are_each_pm_followed_by_its_helpers_all_ticked()
        {
            var model = new StartupRestoreModel(new[]
            {
                Pane("1", "Alice"),
                Pane("2", "Bob", project: "q"),
                Pane("3", "TestA", project: null, parent: "1"),
                Pane("4", "TestB", parent: "1"),
            }, Projects);

            Assert.Equal(new[] { "Alice", "TestA", "TestB", "Bob" }, model.Rows.Select(r => r.DisplayName));
            Assert.All(model.Rows, r => Assert.True(r.Checked));
            Assert.Equal(new[] { false, true, true, false }, model.Rows.Select(r => r.IsHelper));
            Assert.Equal("MultiTerminal", model.Rows[1].ProjectName); // a helper shows its PM's project
            Assert.Equal(4, model.CheckedCount);
        }

        [Fact]
        public void A_pane_with_no_registered_project_is_not_listed_and_neither_are_its_helpers()
        {
            var model = new StartupRestoreModel(new[]
            {
                Pane("1", "Loose", project: null),
                Pane("2", "Gone", project: "deleted"),
                Pane("3", "HelperOfLoose", parent: "1"),
            }, Projects);

            Assert.True(model.IsEmpty);
            Assert.Equal(3, model.NotListed.Count);
        }

        [Fact]
        public void A_throwing_project_lookup_lists_nothing_rather_than_failing()
        {
            var model = new StartupRestoreModel(new[] { Pane("1", "Alice") }, _ => throw new InvalidOperationException());
            Assert.True(model.IsEmpty);
        }

        [Fact]
        public void Unticking_a_pm_unticks_and_disables_its_helpers_and_ticking_it_brings_them_back()
        {
            var model = new StartupRestoreModel(new[] { Pane("1", "Alice"), Pane("2", "TestA", parent: "1") }, Projects);
            var pm = model.TopLevel.Single();
            var helper = pm.Helpers.Single();

            model.Toggle(pm);
            Assert.False(helper.Checked);
            Assert.False(helper.Enabled);
            Assert.Empty(StartupRestoreModel.SelectedHelpers(pm));

            model.Toggle(helper); // disabled: no change
            Assert.False(helper.Checked);

            model.Toggle(pm);
            Assert.True(helper.Checked);
            Assert.True(helper.Enabled);
        }

        [Fact]
        public void A_helper_can_be_left_out_on_its_own()
        {
            var model = new StartupRestoreModel(new[] { Pane("1", "Alice"), Pane("2", "TestA", parent: "1"), Pane("3", "TestB", parent: "1") }, Projects);
            var pm = model.TopLevel.Single();
            model.Toggle(pm.Helpers[0]);

            Assert.Equal(new[] { "TestB" }, StartupRestoreModel.SelectedHelpers(pm).Select(h => h.DisplayName));
            Assert.Equal(new[] { "Alice" }, model.SelectedTopLevel.Select(t => t.DisplayName));
        }

        [Fact]
        public void Dont_restore_selects_nothing()
        {
            var model = new StartupRestoreModel(new[] { Pane("1", "Alice"), Pane("2", "TestA", parent: "1") }, Projects);
            model.UncheckAll();
            Assert.Empty(model.SelectedTopLevel);
            Assert.Equal(0, model.CheckedCount);
        }

        [Fact]
        public void Where_names_the_worktree()
        {
            var model = new StartupRestoreModel(new[] { Pane("1", "Alice", cwd: @"H:\mt\.claude\worktrees\3e1c09b8\sub") }, Projects);
            Assert.Equal("worktree 3e1c09b8", model.Rows.Single().Where);
        }

        // ---- placement ----

        [Fact]
        public void Each_session_takes_the_pane_with_its_name_once()
        {
            var placed = StartupRestorePlacement.Match(
                new[] { "Alice", null, "bob", "Alice" },
                new[] { "Bob", "Alice", "Alice", "Alice", "Carol" });

            Assert.Equal(new int?[] { 2, 0, 3, null, null }, placed);
        }

        [Fact]
        public void A_name_is_compared_untrimmed()
        {
            Assert.Equal(new int?[] { null }, StartupRestorePlacement.Match(new[] { "Alice " }, new[] { "Alice" }));
        }

        [Fact]
        public void Unused_panes_close()
        {
            Assert.Equal(new[] { 1, 3 }, StartupRestorePlacement.PanesToClose(4, new[] { 0, 2 }, anySessionRestored: true));
        }

        [Fact]
        public void Every_pane_closes_when_the_sessions_all_went_into_new_panes()
        {
            Assert.Equal(new[] { 0, 1 }, StartupRestorePlacement.PanesToClose(2, Array.Empty<int>(), anySessionRestored: true));
        }

        [Fact]
        public void Nothing_restored_leaves_exactly_one_pane()
        {
            Assert.Equal(new[] { 1, 2, 3 }, StartupRestorePlacement.PanesToClose(4, Array.Empty<int>(), anySessionRestored: false));
            Assert.Empty(StartupRestorePlacement.PanesToClose(1, Array.Empty<int>(), anySessionRestored: false));
            Assert.Empty(StartupRestorePlacement.PanesToClose(0, Array.Empty<int>(), anySessionRestored: false));
        }
    }
}
