using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Identifies which CLI tool a terminal is running. Used to route launch
    /// profile construction (binary path, flags, per-session config) to the
    /// appropriate builder.
    /// </summary>
    public enum TerminalKind
    {
        /// <summary>
        /// Claude Code CLI (the original/default MultiTerminal terminal type).
        /// </summary>
        ClaudeCode = 0,

        /// <summary>
        /// OpenAI Codex CLI (Phase 1 Codex integration; first-class team member).
        /// </summary>
        Codex = 1,
    }

    /// <summary>
    /// Converts between the <see cref="TerminalKind"/> enum and the
    /// stable string values used in persistence (project.json, SQLite,
    /// JS UI messages). Unknown / missing values resolve to ClaudeCode.
    /// </summary>
    public static class TerminalKindHelper
    {
        /// <summary>Storage string for Claude Code. Also the safe default.</summary>
        public const string ClaudeCodeValue = "claude-code";

        /// <summary>Storage string for Codex.</summary>
        public const string CodexValue = "codex";

        /// <summary>
        /// Parses a persisted string into a TerminalKind. Null, empty,
        /// whitespace, or unrecognized values return ClaudeCode.
        /// </summary>
        public static TerminalKind ParseOrDefault(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return TerminalKind.ClaudeCode;
            if (string.Equals(value, CodexValue, StringComparison.OrdinalIgnoreCase))
                return TerminalKind.Codex;
            return TerminalKind.ClaudeCode;
        }

        /// <summary>
        /// Returns the storage string for a TerminalKind.
        /// </summary>
        public static string ToStorageString(TerminalKind kind)
        {
            return kind == TerminalKind.Codex ? CodexValue : ClaudeCodeValue;
        }

        /// <summary>
        /// Normalizes a storage string — maps null/empty/unknown values to
        /// the canonical ClaudeCodeValue. Safe to use on read paths to ensure
        /// callers always see a recognized value.
        /// </summary>
        public static string Normalize(string value)
        {
            return ToStorageString(ParseOrDefault(value));
        }
    }
}
