using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 760827ad: which launched terminals are told they are their project's manager. The rule is
    /// "has a project AND was not spawned". Most facts model a launch route, using the arguments that
    /// route's caller passes to <see cref="ConPtyTerminal.Start"/>. Those arguments were read from the
    /// callers when this was written; nothing here checks that the callers still pass them.
    /// <see cref="An_empty_spawner_counts_as_no_spawner"/> is an edge case of the value, not a route.
    /// These tests cover the decision only; that <c>Start</c> appends it to the launch command is a
    /// single line they do not see.
    /// </summary>
    public class ConPtyTerminalProjectPmTests
    {
        private const string Set = "$env:MULTITERMINAL_PROJECT_PM = 'true'; ";
        private const string Cleared = "$env:MULTITERMINAL_PROJECT_PM = $null; ";

        [Fact]
        public void A_terminal_the_Owner_opens_on_a_project_is_the_PM()
        {
            // Project card, Project panel and New Project all pass a project id and no spawner,
            // whether or not the project has a team lead configured.
            Assert.Equal(Set, ConPtyTerminal.BuildProjectPmEnvAssignment("5d7853b8-c695-4684-8f32-dfad644b0669", null));
        }

        [Fact]
        public void A_helper_spawned_into_the_same_project_is_not()
        {
            // spawn_helper passes the project it discovered for the working directory AND the spawner's name.
            Assert.Equal(Cleared, ConPtyTerminal.BuildProjectPmEnvAssignment("5d7853b8-c695-4684-8f32-dfad644b0669", "Alice"));
        }

        [Fact]
        public void A_terminal_started_from_the_phone_is_not()
        {
            // Phone and gateway spawns carry the spawner "ClaudeRemote"; the Owner ruled these are not PMs.
            Assert.Equal(Cleared, ConPtyTerminal.BuildProjectPmEnvAssignment("5d7853b8-c695-4684-8f32-dfad644b0669", "ClaudeRemote"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_terminal_with_no_project_is_not(string projectId)
        {
            // Just Claude, Oracle and "Launch as..." pass no project and no spawner. The value is
            // cleared rather than omitted, so a stale PROJECT_PM in MultiTerminal's own environment
            // cannot leak into the child.
            Assert.Equal(Cleared, ConPtyTerminal.BuildProjectPmEnvAssignment(projectId, null));
        }

        [Fact]
        public void An_empty_spawner_counts_as_no_spawner()
        {
            // Matches the MULTITERMINAL_SPAWNER branch, which also clears on an empty string.
            Assert.Equal(Set, ConPtyTerminal.BuildProjectPmEnvAssignment("5d7853b8-c695-4684-8f32-dfad644b0669", ""));
        }
    }
}
