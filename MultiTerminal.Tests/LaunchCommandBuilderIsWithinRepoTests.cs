using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the AC7 spawn-dir containment guard (task f1f74a8f).
    /// The pure surface carries the load: the end-to-end repro (New Project launch
    /// hijacked by the team lead's active-task worktree in a different repo) needs
    /// a live WinForms launch the Owner must drive, so the path semantics that
    /// decide "may the active-task override apply?" are asserted here.
    /// </summary>
    public class LaunchCommandBuilderIsWithinRepoTests
    {
        [Fact]
        public void True_when_target_equals_repo_root()
        {
            Assert.True(LaunchCommandBuilder.IsWithinRepo(
                @"H:\Dev\MultiTerminal", @"H:\Dev\MultiTerminal"));
        }

        [Fact]
        public void True_when_target_equals_repo_root_with_trailing_separator_and_casing_differences()
        {
            Assert.True(LaunchCommandBuilder.IsWithinRepo(
                @"h:\dev\multiterminal\", @"H:\Dev\MultiTerminal"));
        }

        [Fact]
        public void True_when_target_is_worktree_inside_repo()
        {
            Assert.True(LaunchCommandBuilder.IsWithinRepo(
                @"H:\Dev\MultiTerminal\.claude\worktrees\7c59c004", @"H:\Dev\MultiTerminal"));
        }

        [Fact]
        public void True_when_target_uses_forward_slashes()
        {
            Assert.True(LaunchCommandBuilder.IsWithinRepo(
                "H:/Dev/MultiTerminal/subdir", @"H:\Dev\MultiTerminal"));
        }

        [Fact]
        public void False_when_target_is_a_different_repo()
        {
            // The reported repro: new project folder vs the lead's active-task repo.
            Assert.False(LaunchCommandBuilder.IsWithinRepo(
                @"H:\DevLaptop\Projects\POSitiveMobile", @"H:\DevLaptop\ClarionPowerShell\MultiTerminal"));
        }

        [Fact]
        public void False_when_target_is_sibling_with_common_prefix()
        {
            // "C:\repo2" must not count as inside "C:\repo".
            Assert.False(LaunchCommandBuilder.IsWithinRepo(
                @"C:\repo2", @"C:\repo"));
        }

        [Fact]
        public void False_when_target_is_parent_of_repo_root()
        {
            Assert.False(LaunchCommandBuilder.IsWithinRepo(
                @"H:\Dev", @"H:\Dev\MultiTerminal"));
        }

        [Theory]
        [InlineData(null, @"C:\repo")]
        [InlineData("", @"C:\repo")]
        [InlineData("   ", @"C:\repo")]
        [InlineData(@"C:\target", null)]
        [InlineData(@"C:\target", "")]
        public void False_when_either_path_is_null_or_whitespace(string target, string root)
        {
            Assert.False(LaunchCommandBuilder.IsWithinRepo(target, root));
        }

        [Fact]
        public void False_when_target_path_is_malformed()
        {
            // On .NET 8 GetFullPath only throws for an embedded NUL — the guard must
            // swallow that and refuse rather than propagate.
            Assert.False(LaunchCommandBuilder.IsWithinRepo(
                "H:\\Dev\\bad\0seg", @"H:\Dev"));
        }

        [Fact]
        public void Relative_target_is_resolved_against_cwd_not_repo_root()
        {
            // Relative segments go through GetFullPath (cwd-anchored). A bare relative
            // name should therefore NOT be considered inside an unrelated repo root.
            Assert.False(LaunchCommandBuilder.IsWithinRepo(
                "some-folder", @"H:\Dev\MultiTerminal"));
        }
    }
}
