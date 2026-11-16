using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>One subagent a Claude Code session still has open (ticket b3590495).</summary>
    /// <param name="Name">Its name, as the session addresses it (and stops it: TaskStop(task_id = name)).</param>
    /// <param name="AgentType">Its agent type (verifier, code-reviewer, ...), or null.</param>
    /// <param name="JoinedAtUtc">When it was spawned.</param>
    /// <param name="LastActivityUtc">When its transcript was last written; <paramref name="JoinedAtUtc"/> when none was found.</param>
    internal sealed record OpenSubagent(string Name, string AgentType, DateTime JoinedAtUtc, DateTime LastActivityUtc);

    /// <summary>
    /// Reads which subagents a Claude Code session still has open (ticket b3590495). Agents left named
    /// subagents open after they had reported (every pipeline gate is named), and only the session that
    /// spawned one can stop it, so MT shows the count and reminds that session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Claude Code keeps a session's live teammates in <c>&lt;config root&gt;\teams\&lt;team&gt;\config.json</c>:
    /// <c>leadSessionId</c> is the session, <c>members</c> its teammates plus the lead. TaskStop removes the
    /// member, so a listed member is one still open. The team is named <c>session-&lt;first 8 of the id&gt;</c>
    /// unless the session named it with TeamCreate, so teams are matched by <c>leadSessionId</c>, not by name.
    /// </para>
    /// <para>
    /// A teammate's transcript is <c>projects\&lt;encoded cwd&gt;\&lt;session id&gt;\subagents\agent-*.jsonl</c>,
    /// and the <c>.meta.json</c> beside it carries its <c>name</c>. The session can have such folders under
    /// several encoded cwds (it moves between worktrees), so every one is searched. The newest transcript of
    /// that name gives the last activity.
    /// </para>
    /// <para>
    /// These are Claude Code's own files, not a published contract. A missing or unreadable file reads as
    /// "nothing open", so a layout change shows nothing rather than something wrong.
    /// </para>
    /// </remarks>
    internal static class OpenSubagentReader
    {
        /// <summary>The open subagents of <paramref name="sessionId"/> under <paramref name="configRoot"/>; empty when none or unreadable.</summary>
        public static IReadOnlyList<OpenSubagent> Read(string configRoot, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(configRoot) || string.IsNullOrWhiteSpace(sessionId))
            {
                return Array.Empty<OpenSubagent>();
            }

            var members = ReadMembers(Path.Combine(configRoot, "teams"), sessionId);
            if (members.Count == 0)
            {
                return Array.Empty<OpenSubagent>();
            }

            var lastActivity = ReadLastActivityByName(Path.Combine(configRoot, "projects"), sessionId);
            return members
                .Select(m => m with
                {
                    LastActivityUtc = lastActivity.TryGetValue(m.Name, out var at) && at > m.JoinedAtUtc ? at : m.JoinedAtUtc,
                })
                .ToList();
        }

        private static List<OpenSubagent> ReadMembers(string teamsDir, string sessionId)
        {
            var result = new List<OpenSubagent>();
            if (!Directory.Exists(teamsDir))
            {
                return result;
            }

            foreach (string dir in SafeDirectories(teamsDir))
            {
                string configPath = Path.Combine(dir, "config.json");
                try
                {
                    if (!File.Exists(configPath))
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("leadSessionId", out var lead) || lead.ValueKind != JsonValueKind.String
                        || !string.Equals(lead.GetString(), sessionId, StringComparison.OrdinalIgnoreCase)
                        || !root.TryGetProperty("members", out var membersArr) || membersArr.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string leadAgentId = root.TryGetProperty("leadAgentId", out var la) && la.ValueKind == JsonValueKind.String ? la.GetString() : null;
                    foreach (var member in membersArr.EnumerateArray())
                    {
                        string name = Str(member, "name");
                        string type = Str(member, "agentType");
                        if (string.IsNullOrWhiteSpace(name)
                            || string.Equals(type, "team-lead", StringComparison.OrdinalIgnoreCase)
                            || (leadAgentId != null && string.Equals(Str(member, "agentId"), leadAgentId, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        DateTime joined = member.TryGetProperty("joinedAt", out var j) && j.ValueKind == JsonValueKind.Number && j.TryGetInt64(out long ms)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                            : DateTime.MinValue;
                        result.Add(new OpenSubagent(name, type, joined, joined));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
                {
                    // Being written, or not a team config: nothing to count from it this time.
                }
            }

            return result;
        }

        private static Dictionary<string, DateTime> ReadLastActivityByName(string projectsDir, string sessionId)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            if (!Directory.Exists(projectsDir))
            {
                return result;
            }

            foreach (string projectDir in SafeDirectories(projectsDir))
            {
                string subagentsDir = Path.Combine(projectDir, sessionId, "subagents");
                if (!Directory.Exists(subagentsDir))
                {
                    continue;
                }

                IEnumerable<string> metas;
                try
                {
                    metas = Directory.EnumerateFiles(subagentsDir, "*.meta.json").ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string metaPath in metas)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                        string name = Str(doc.RootElement, "name");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        string transcript = metaPath.Substring(0, metaPath.Length - ".meta.json".Length) + ".jsonl";
                        DateTime at = File.Exists(transcript) ? File.GetLastWriteTimeUtc(transcript) : File.GetLastWriteTimeUtc(metaPath);
                        if (!result.TryGetValue(name, out var known) || at > known)
                        {
                            result[name] = at;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
                    {
                        // Unreadable meta: that subagent falls back to its join time.
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static string Str(JsonElement e, string property) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// What MT shows and says about a session's open subagents (ticket b3590495). Pure.
    /// </summary>
    internal static class IdleSubagents
    {
        /// <summary>A subagent with no activity for this long counts as idle (Owner decision, 2026-11-15).</summary>
        internal static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(30);

        /// <summary>How often MT reads every pane's open subagents.</summary>
        internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

        /// <summary>The sender name the agent sees on the reminder.</summary>
        internal const string SenderName = "MultiTerminal";

        /// <summary>Whether <paramref name="subagent"/> has been idle for <see cref="IdleAfter"/> or longer at <paramref name="nowUtc"/>.</summary>
        public static bool IsIdle(OpenSubagent subagent, DateTime nowUtc) => subagent != null && nowUtc - subagent.LastActivityUtc >= IdleAfter;

        /// <summary>The header text, e.g. "3 subagents, 2 idle"; null when none are open.</summary>
        public static string HeaderText(IReadOnlyList<OpenSubagent> open, DateTime nowUtc)
        {
            int count = open?.Count ?? 0;
            if (count == 0)
            {
                return null;
            }

            int idle = open.Count(s => IsIdle(s, nowUtc));
            string text = count == 1 ? "1 subagent" : $"{count} subagents";
            return idle == 0 ? text : $"{text}, {idle} idle";
        }

        /// <summary>The header tooltip: one line per subagent with its type and how long it has been idle.</summary>
        public static string HeaderTooltip(IReadOnlyList<OpenSubagent> open, DateTime nowUtc)
        {
            if (open == null || open.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder("Subagents this session still has open (it closes them with TaskStop):");
            foreach (var s in open.OrderByDescending(s => nowUtc - s.LastActivityUtc))
            {
                sb.Append('\n').Append(s.Name);
                if (!string.IsNullOrWhiteSpace(s.AgentType))
                {
                    sb.Append(" (").Append(s.AgentType).Append(')');
                }

                sb.Append(": ").Append(IsIdle(s, nowUtc) ? "idle " + Span(nowUtc - s.LastActivityUtc) : "active " + Span(nowUtc - s.LastActivityUtc) + " ago");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The header pill as wire JSON: text, tooltip, and whether any is idle (the pill is amber then);
        /// "null" when none are open. Names are free text, so the page sets them with textContent/title.
        /// </summary>
        public static string ToWire(IReadOnlyList<OpenSubagent> open, DateTime nowUtc)
        {
            string text = HeaderText(open, nowUtc);
            return text == null
                ? "null"
                : JsonSerializer.Serialize(new { text, tooltip = HeaderTooltip(open, nowUtc), idle = open.Any(s => IsIdle(s, nowUtc)) });
        }

        /// <summary>The key a reminder is recorded under: one per session, subagent name and spawn.</summary>
        public static string NudgeKey(string sessionId, OpenSubagent subagent) =>
            sessionId + "\n" + subagent.Name + "\n" + subagent.JoinedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture);

        /// <summary>The idle subagents not mentioned to this session before, oldest activity first.</summary>
        public static IReadOnlyList<OpenSubagent> DueForNudge(IReadOnlyList<OpenSubagent> open, string sessionId, ICollection<string> alreadyNudged, DateTime nowUtc)
        {
            if (open == null || open.Count == 0 || string.IsNullOrEmpty(sessionId))
            {
                return Array.Empty<OpenSubagent>();
            }

            return open
                .Where(s => IsIdle(s, nowUtc) && (alreadyNudged == null || !alreadyNudged.Contains(NudgeKey(sessionId, s))))
                .OrderBy(s => s.LastActivityUtc)
                .ToList();
        }

        /// <summary>The reminder sent to the session that owns <paramref name="due"/>.</summary>
        public static string BuildMessage(IReadOnlyList<OpenSubagent> due, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append(due.Count == 1 ? "You have a subagent" : $"You have {due.Count} subagents")
              .Append(" open with no activity for ").Append((int)IdleAfter.TotalMinutes).Append(" minutes or more:");
            foreach (var s in due)
            {
                sb.Append("\n- ").Append(s.Name);
                if (!string.IsNullOrWhiteSpace(s.AgentType))
                {
                    sb.Append(" (").Append(s.AgentType).Append(')');
                }

                sb.Append(", idle ").Append(Span(nowUtc - s.LastActivityUtc));
            }

            sb.Append("\nIf you are done with one, stop it: TaskStop(task_id=\"<its name>\"). Leave any you still need. ")
              .Append("MultiTerminal will not mention these again.");
            return sb.ToString();
        }

        private static string Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1)
            {
                return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
            }

            return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }
    }
}
