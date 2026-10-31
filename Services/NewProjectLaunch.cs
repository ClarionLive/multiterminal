using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// How the terminal opened by New Project starts (GitHub #91).
    /// </summary>
    internal static class NewProjectLaunch
    {
        /// <summary>
        /// Whether the New Project pane is a quiet start for this launch. True for Claude Code: MT types
        /// <c>/new-project</c> into it, and the normal "initializing..." kick would open the session-start
        /// menu first, leaving <c>/new-project</c> queued to run after whatever the user picked there.
        /// Codex gets no <c>/new-project</c> and keeps its normal start.
        /// </summary>
        internal static bool IsQuietStart(TerminalKind kind) => kind == TerminalKind.ClaudeCode;
    }
}
