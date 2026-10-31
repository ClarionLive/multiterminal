using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GitHub #91: the Claude pane New Project opens is a quiet start, so the session-start menu does not
    /// open and /new-project is the only thing typed into it. These facts cover the decision; that
    /// <c>MainForm.OnStartScreenNewProject</c> passes it to <c>StartTerminal</c> is wiring no test executes.
    /// </summary>
    public sealed class NewProjectLaunchTests
    {
        [Fact]
        public void A_claude_new_project_pane_is_quiet_even_when_its_project_is_not()
        {
            Assert.True(NewProjectLaunch.IsQuietStart(TerminalKind.ClaudeCode));
            Assert.True(TerminalRoles.IsQuietStart(projectQuietStart: false, spawnerName: null,
                launchQuietStart: NewProjectLaunch.IsQuietStart(TerminalKind.ClaudeCode)));
        }

        [Fact]
        public void A_codex_new_project_pane_keeps_its_normal_start()
        {
            // Codex gets no /new-project, so nothing collides with its start-up.
            Assert.False(NewProjectLaunch.IsQuietStart(TerminalKind.Codex));
            Assert.False(TerminalRoles.IsQuietStart(projectQuietStart: false, spawnerName: null,
                launchQuietStart: NewProjectLaunch.IsQuietStart(TerminalKind.Codex)));
        }
    }
}
