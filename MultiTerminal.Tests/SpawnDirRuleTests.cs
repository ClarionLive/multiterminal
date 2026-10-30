using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 28d71d14: a spawn's named folder is not moved into a task found by NAME when that task's
    /// repo does not contain the folder. The case: Grace spawned Quinn in ClarionClone, and an older
    /// agent also named Quinn had an active MultiTerminal task. The wiring (AddNewTerminal and the
    /// headless spawn passing the caller's folder) runs live only.
    /// </summary>
    public class SpawnDirRuleTests
    {
        private const string Mt = @"H:\DevLaptop\ClarionPowerShell\MultiTerminal";
        private const string MtWorktree = @"H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\worktrees\dbb3165a";
        private const string ClarionClone = @"H:\DevLaptop\Projects\ClarionClone";

        private static bool AllExist(string path) => true;

        [Fact]
        public void A_named_folder_in_another_repo_is_not_moved_into_a_same_name_task()
        {
            var d = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: true, MtWorktree, Mt, AllExist);
            Assert.True(d.TaskInAnotherRepo);
            Assert.Equal(ClarionClone, d.Dir);
            Assert.Null(d.TaskWorktreePath);
        }

        [Fact]
        public void A_named_folder_inside_the_task_repo_still_starts_at_the_repo_root_with_the_worktree()
        {
            var d = SpawnDirRule.Decide(Mt, requestedExplicitly: true, MtWorktree, Mt, AllExist);
            Assert.False(d.TaskInAnotherRepo);
            Assert.Equal(Mt, d.Dir);
            Assert.Equal(MtWorktree, d.TaskWorktreePath);
        }

        [Fact]
        public void A_default_folder_still_follows_the_task()
        {
            var d = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: false, MtWorktree, Mt, AllExist);
            Assert.False(d.TaskInAnotherRepo);
            Assert.Equal(Mt, d.Dir);
            Assert.Equal(MtWorktree, d.TaskWorktreePath);
        }

        [Fact]
        public void No_task_worktree_leaves_the_folder_alone()
        {
            var d = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: true, null, null, AllExist);
            Assert.False(d.TaskInAnotherRepo);
            Assert.Equal(ClarionClone, d.Dir);
            Assert.Null(d.TaskWorktreePath);
        }

        [Fact]
        public void A_repo_root_that_does_not_contain_the_worktree_is_not_trusted()
        {
            // Two lookups by name disagreed: the worktree is MultiTerminal's, the root ClarionClone's.
            var d = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: true, MtWorktree, ClarionClone, AllExist);
            Assert.True(d.TaskInAnotherRepo);
            Assert.Null(d.TaskWorktreePath);

            var unnamed = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: false, MtWorktree, ClarionClone, AllExist);
            Assert.Equal(MtWorktree, unnamed.Dir);   // never the mismatched root
        }

        [Fact]
        public void Without_a_usable_repo_root_the_worktree_is_the_anchor_and_the_start_folder()
        {
            var outside = SpawnDirRule.Decide(ClarionClone, requestedExplicitly: true, MtWorktree, Mt, _ => false);
            Assert.True(outside.TaskInAnotherRepo);

            var inside = SpawnDirRule.Decide(MtWorktree, requestedExplicitly: true, MtWorktree, null, AllExist);
            Assert.False(inside.TaskInAnotherRepo);
            Assert.Equal(MtWorktree, inside.Dir);
        }
    }
}
