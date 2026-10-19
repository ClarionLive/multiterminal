using System;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The CLI a pane launch runs, as recorded in <c>pane_launches.kind</c> (task 7d20e94c item 1).
    /// Values: <see cref="Claude"/>, <see cref="Codex"/>, <see cref="PowerShell"/> (no auto-run
    /// command: a plain shell), <see cref="Unknown"/>. A launch site that knows its kind passes it
    /// (<see cref="From(TerminalKind)"/>); <see cref="FromAutoRunCommand"/> is the fallback for one that
    /// does not (pipeline Run 1).
    /// </summary>
    public static class PaneLaunchKind
    {
        public const string Claude = "claude";
        public const string Codex = "codex";
        public const string PowerShell = "powershell";
        public const string Unknown = "unknown";

        /// <summary>The kind of a launch whose <see cref="TerminalKind"/> the caller chose.</summary>
        public static string From(TerminalKind kind) => kind switch
        {
            TerminalKind.ClaudeCode => Claude,
            TerminalKind.Codex => Codex,
            _ => Unknown,
        };

        /// <summary>
        /// Fallback: classifies an auto-run command. Claude: the first token (quotes stripped) is
        /// <c>claude</c> or <c>claude.exe</c>, bare or as a path's file name, which is how
        /// <c>LaunchCommandBuilder.BuildClaudeCommand</c> starts its command. Codex: the command sets
        /// <c>MULTITERMINAL_CODEX_BIN</c> (<c>BuildCodexCommand</c>'s launcher) or its first token is
        /// <c>codex</c>/<c>codex.exe</c>. Null or blank: a plain shell. Anything else: unknown, never a
        /// guess.
        /// </summary>
        public static string FromAutoRunCommand(string autoRunCommand)
        {
            if (string.IsNullOrWhiteSpace(autoRunCommand)) return PowerShell;
            string trimmed = autoRunCommand.TrimStart();
            if (trimmed.IndexOf("MULTITERMINAL_CODEX_BIN", StringComparison.OrdinalIgnoreCase) >= 0) return Codex;

            int end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != ';') end++;
            string token = trimmed.Substring(0, end).Trim('"', '\'');
            string name;
            try { name = System.IO.Path.GetFileName(token); }
            catch (ArgumentException) { return Unknown; }

            if (string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "claude.exe", StringComparison.OrdinalIgnoreCase))
                return Claude;
            if (string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "codex.exe", StringComparison.OrdinalIgnoreCase))
                return Codex;
            return Unknown;
        }
    }
}
