using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The extra Claude Code flags a launch gets (ticket 5380b6fb), and what the launch log says about them.
    /// <see cref="Formatted"/> is empty when there are none, which leaves the command exactly as before.
    /// </summary>
    public sealed class ClaudeExtraArgsDecision
    {
        public static readonly ClaudeExtraArgsDecision None = new ClaudeExtraArgsDecision(Array.Empty<string>(), string.Empty, null, Array.Empty<string>());

        public ClaudeExtraArgsDecision(IReadOnlyList<string> tokens, string formatted, string source, IReadOnlyList<string> problems)
        {
            Tokens = tokens;
            Formatted = formatted;
            Source = source;
            Problems = problems;
        }

        /// <summary>The flags as Claude Code will receive them, project's first, then the agent's.</summary>
        public IReadOnlyList<string> Tokens { get; }

        /// <summary>The flags as they go on the PowerShell command line, with a leading space, or empty.</summary>
        public string Formatted { get; }

        /// <summary>Where they came from ("project", "agent 'Vella'", "project + agent 'Vella'"), or null for none.</summary>
        public string Source { get; }

        /// <summary>Stored levels that were skipped because their text was not valid, one sentence each.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>
        /// The flags for a log line: flag names only (the part before any <c>=</c>), and how many values were
        /// left out. A value can be a secret, and the debug log is readable by agents (Codex security review).
        /// </summary>
        public string DescribeForLog()
        {
            var names = Tokens.Where(t => t.Length > 1 && t[0] == '-').Select(t => { int eq = t.IndexOf('='); return eq > 0 ? t.Substring(0, eq) : t; }).ToList();
            int values = Tokens.Count(t => !(t.Length > 1 && t[0] == '-')) + Tokens.Count(t => t.Length > 1 && t[0] == '-' && t.IndexOf('=') > 0);
            return (names.Count == 0 ? "(no flag names)" : string.Join(" ", names)) + (values > 0 ? $" ({values} value(s) not logged)" : "");
        }
    }

    /// <summary>
    /// Extra Claude Code command-line flags per project and per agent (ticket 5380b6fb, Owner request
    /// 2026-11-23: one agent working on mrtStudio needs <c>--plugin-dir "C:\scratch\cc-plugin-1123"</c>).
    /// Pure: <c>MainForm.DecideLaunchExtraArgs</c> reads the project row and its agent rows and passes them in.
    ///
    /// <para>The text is typed by the Owner, but it lands on a PowerShell command line that is itself
    /// inside a double-quoted <c>-Command "..."</c> argument of <c>CreateProcess</c>
    /// (<c>ConPtyTerminal.StartProcess</c>). So nothing of it is passed through as written: it is split into
    /// tokens here and each token is written as a PowerShell single-quoted literal, in which nothing is
    /// evaluated. A token may not contain a double quote (it would end the outer <c>-Command</c> string),
    /// a control character, or one of the typographic quotes PowerShell also treats as quotes.</para>
    ///
    /// <para>Flags MT sets itself are refused (<see cref="ReservedFlags"/>): a second <c>--resume</c> or
    /// <c>--mcp-config</c> would silently change which session or which MCP servers a terminal gets.</para>
    /// </summary>
    public static class ClaudeExtraArgsPolicy
    {
        /// <summary>Longest stored text accepted, per level.</summary>
        public const int MaxLength = 2000;

        /// <summary>
        /// Flags MT owns on every Claude launch, or that would change what MT expects of the session
        /// (an interactive pane, MT's session id, MT's permissions). Compared ordinal-ignore-case, and a
        /// <c>--flag=value</c> form counts as the flag.
        /// </summary>
        public static readonly IReadOnlyCollection<string> ReservedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-r", "--resume", "-c", "--continue", "--session-id", "--fork-session",
            "--mcp-config", "--strict-mcp-config",
            "--settings", "--setting-sources",
            "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--permission-mode",
            "--model",
            "-n", "--name",
            "-p", "--print", "--output-format", "--input-format",
            // Print and exit instead of opening a session (the same list as ClaudeStartupDetector's
            // NonSessionFlags): saved once, every terminal of the project would end at once.
            "-h", "--help", "-v", "--version",
            // Start somewhere other than this pane, or end at once (Codex review; from `claude --help`).
            "--bg", "--background", "--cloud", "--desktop", "--environment", "--teleport", "--tmux",
            // Strip what MT installs: its plugin's hooks and skills, the slash commands MT types (/compact).
            "--bare", "--safe-mode", "--restricted", "--disable-slash-commands",
            // Change the session or folder MT tracks: another session, another worktree, no transcript to resume.
            "--from-pr", "-w", "--worktree", "--no-session-persistence",
        };

        private static readonly Regex TrailingNumber = new Regex(@"-\d+\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Splits <paramref name="text"/> into tokens: whitespace separates, a double-quoted run is one
        /// token (or part of one) with the quotes removed. Blank text is no tokens. Returns false with a
        /// one-sentence <paramref name="error"/> for text that cannot be used.
        /// </summary>
        public static bool TryParse(string text, out List<string> tokens, out string error)
        {
            tokens = new List<string>();
            error = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (text.Length > MaxLength)
            {
                error = $"The flags are longer than {MaxLength} characters.";
                return false;
            }

            var current = new StringBuilder();
            bool inQuotes = false, hasToken = false;
            foreach (char ch in text)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                    continue;
                }

                if (!inQuotes && (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n'))
                {
                    if (hasToken)
                        tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                    continue;
                }

                if (char.IsControl(ch))
                {
                    error = "The flags contain a control character.";
                    return false;
                }

                if (IsTypographicQuote(ch))
                {
                    error = $"The flags contain the character '{ch}', which PowerShell reads as a quote. Use a plain \" instead.";
                    return false;
                }

                current.Append(ch);
                hasToken = true;
            }

            if (inQuotes)
            {
                error = "A double quote in the flags is not closed.";
                return false;
            }

            if (hasToken)
                tokens.Add(current.ToString());

            foreach (string token in tokens)
            {
                if (token.Length == 0)
                {
                    error = "The flags contain an empty \"\" value.";
                    return false;
                }

                // Windows PowerShell 5.1 (a pane's fallback shell) passes a value with a space as "...", and a
                // trailing backslash then escapes that closing quote for the program reading it (pipeline Run 1).
                if (token.EndsWith('\\') && token.Any(char.IsWhiteSpace))
                {
                    error = "A value with a space cannot end in a backslash. Leave the trailing \\ off the folder.";
                    return false;
                }

                string flag = FlagName(token);
                if (flag != null && ReservedFlags.Contains(flag))
                {
                    error = $"'{flag}' is set by MultiTerminal itself and cannot be added here.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Validates stored or typed text; null when it can be used (blank included), else the reason.</summary>
        public static string Validate(string text) => TryParse(text, out _, out string error) ? null : error;

        /// <summary>
        /// The tokens as they go on the command line: each one a PowerShell single-quoted literal (a
        /// single quote doubled), joined with a leading space. Empty for no tokens. Throws for a token
        /// <see cref="TryParse"/> would not produce, so nothing unchecked reaches a command line.
        /// </summary>
        public static string Format(IEnumerable<string> tokens)
        {
            var sb = new StringBuilder();
            foreach (string token in tokens ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(token) || token.IndexOf('"') >= 0 || token.Any(c => char.IsControl(c) || IsTypographicQuote(c)))
                    throw new ArgumentException("A flag is not valid for a command line.", nameof(tokens));
                sb.Append(" '").Append(token.Replace("'", "''")).Append('\'');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The project's agent row for <paramref name="agentName"/>: an exact name match (ignoring case),
        /// else the name without a trailing <c>-&lt;number&gt;</c>, which MT adds when a name is already
        /// held (Vella-2 uses Vella's row when there is no Vella-2 row). Null for none.
        /// </summary>
        public static ProjectAgent FindAgentRow(IEnumerable<ProjectAgent> agents, string agentName)
        {
            if (agents == null || string.IsNullOrWhiteSpace(agentName))
                return null;

            var list = agents.Where(a => a != null && !string.IsNullOrWhiteSpace(a.AgentName)).ToList();
            var exact = list.FirstOrDefault(a => string.Equals(a.AgentName.Trim(), agentName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            string baseName = TrailingNumber.Replace(agentName.Trim(), string.Empty);
            if (baseName.Length == 0 || baseName.Length == agentName.Trim().Length)
                return null;
            return list.FirstOrDefault(a => string.Equals(a.AgentName.Trim(), baseName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The flags for one launch: the project's (<paramref name="projectText"/>, every Claude terminal of
        /// the project), then those of the agent row matching <paramref name="agentName"/>. A stored level
        /// whose text is not valid is skipped and named in <see cref="ClaudeExtraArgsDecision.Problems"/>;
        /// the launch is not refused.
        /// </summary>
        public static ClaudeExtraArgsDecision Decide(string projectText, IEnumerable<ProjectAgent> agents, string agentName)
        {
            var tokens = new List<string>();
            var problems = new List<string>();
            var sources = new List<string>();

            if (TryParse(projectText, out var projectTokens, out string projectError))
            {
                if (projectTokens.Count > 0)
                {
                    tokens.AddRange(projectTokens);
                    sources.Add("project");
                }
            }
            else
            {
                problems.Add($"The project's extra Claude flags were skipped: {projectError}");
            }

            var row = FindAgentRow(agents, agentName);
            if (row != null)
            {
                if (TryParse(row.ExtraArgs, out var agentTokens, out string agentError))
                {
                    if (agentTokens.Count > 0)
                    {
                        tokens.AddRange(agentTokens);
                        sources.Add($"agent '{row.AgentName}'");
                    }
                }
                else
                {
                    problems.Add($"Agent '{row.AgentName}''s extra Claude flags were skipped: {agentError}");
                }
            }

            if (tokens.Count == 0 && problems.Count == 0)
                return ClaudeExtraArgsDecision.None;

            return new ClaudeExtraArgsDecision(tokens, Format(tokens), sources.Count == 0 ? null : string.Join(" + ", sources), problems);
        }

        /// <summary>The flag part of a token (<c>--x</c> of <c>--x=1</c>), or null when it is not a flag.</summary>
        private static string FlagName(string token)
        {
            if (token.Length < 2 || token[0] != '-')
                return null;
            int eq = token.IndexOf('=');
            return eq > 0 ? token.Substring(0, eq) : token;
        }

        // U+2018 to U+201F: wider than the set PowerShell treats as quotes (U+2018 to U+201E), on purpose;
        // refusing one more typographic quote costs nothing.
        private static bool IsTypographicQuote(char ch) => ch >= '\u2018' && ch <= '\u201F';
    }
}
