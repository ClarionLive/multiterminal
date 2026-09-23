#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Says, visibly, when MT's terminals are about to start without MultiTerminal's agent tools
    /// (task cb4883b6). Every failure this detects was previously SILENT: the terminal opened, Claude
    /// started, and the agent simply had no <c>mcp__multiterminal__*</c> tools.
    ///
    /// <para><b>The case this exists for.</b> The installer runs elevated and copies the MCP server into
    /// <c>{userappdata}</c> and the plugin into <c>{%USERPROFILE}</c> — the profile of whoever is
    /// ELEVATED. A developer who types an administrator's password therefore gets those files in the
    /// administrator's profile. Their own launch finds the gateway (it lives under Program Files) but
    /// not <c>index.js</c>, so <see cref="CentralMcpConfig"/> writes a perfectly valid gateway-only
    /// config and every terminal looks healthy. Found by the pipeline's cross-model adversary; the real
    /// fix (stage under <c>{app}</c>, seed per user at startup) is its own ticket.</para>
    ///
    /// <para>Pure: the filesystem and PATH are injected, so the decision is testable without touching
    /// this machine.</para>
    /// </summary>
    internal static class ClaudeIntegrationCheck
    {
        internal enum Problem
        {
            McpServerMissing,
            PluginMissing,
            NodeMissing,
        }

        /// <summary>The settings key holding the fingerprint of the problem set the Owner chose to stop seeing.</summary>
        internal const string SuppressedFingerprintKey = "claudeIntegrationWarning.suppressed";

        internal static IReadOnlyList<Problem> Find(
            string expectedMcpIndexJs,
            string expectedPluginDir,
            string? pathVariable,
            Func<string, bool> fileExists,
            Func<string, bool> directoryExists)
        {
            var problems = new List<Problem>();
            if (!fileExists(expectedMcpIndexJs))
                problems.Add(Problem.McpServerMissing);
            if (!directoryExists(expectedPluginDir))
                problems.Add(Problem.PluginMissing);
            if (FindOnPath("node.exe", pathVariable, fileExists) == null)
                problems.Add(Problem.NodeMissing);
            return problems;
        }

        /// <summary>
        /// A stable, order-independent identity for a problem set. Dismissing "don't show again" stores
        /// this, so the warning stays quiet for THAT set and reappears if a different problem appears —
        /// a deliberate custom install without the plugin should not be nagged, but it also must not
        /// silence a later, unrelated failure.
        /// </summary>
        internal static string Fingerprint(IEnumerable<Problem> problems) =>
            string.Join("|", problems.Distinct().OrderBy(p => p).Select(p => p.ToString()));

        internal static bool ShouldWarn(IReadOnlyList<Problem> problems, string? suppressedFingerprint) =>
            problems.Count > 0 && !string.Equals(Fingerprint(problems), suppressedFingerprint, StringComparison.Ordinal);

        internal static string BuildMessage(IReadOnlyList<Problem> problems, string expectedMcpIndexJs, string expectedPluginDir)
        {
            var lines = new List<string>
            {
                "New terminals will open, but the agents in them will NOT have MultiTerminal's tools:",
                "",
            };

            if (problems.Contains(Problem.McpServerMissing))
                lines.Add($"• The MultiTerminal MCP server was not found at:\n   {expectedMcpIndexJs}");
            if (problems.Contains(Problem.PluginMissing))
                lines.Add($"• The MultiTerminal Claude Code plugin was not found at:\n   {expectedPluginDir}");
            if (problems.Contains(Problem.NodeMissing))
                lines.Add("• Node.js was not found on PATH. The agent tools need Node.js 18 or later (https://nodejs.org).");

            if (problems.Contains(Problem.McpServerMissing) || problems.Contains(Problem.PluginMissing))
            {
                lines.Add("");
                lines.Add("The usual cause: MultiTerminal was installed while elevated as a DIFFERENT Windows account "
                          + "(for example by entering an administrator's password), so these files went into that "
                          + "account's profile. Re-run the installer while signed in as this account, with the "
                          + "Claude Code integration selected.");
            }

            lines.Add("");
            lines.Add("Show this warning again next time?");
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>First match of <paramref name="exe"/> in a PATH-style list, or null.</summary>
        internal static string? FindOnPath(string exe, string? pathVariable, Func<string, bool> fileExists)
        {
            if (string.IsNullOrWhiteSpace(pathVariable))
                return null;

            foreach (string raw in pathVariable.Split(Path.PathSeparator))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0)
                    continue;

                string candidate;
                try
                {
                    candidate = Path.Combine(dir, exe);
                }
                catch (ArgumentException)
                {
                    continue; // A malformed PATH entry is skipped, as the shell would skip it.
                }

                if (fileExists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
