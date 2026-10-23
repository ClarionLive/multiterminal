using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Whether an agent's turn ended on a question to the owner, read from its own transcript
    /// (task 3d51315b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attention rail only knew a question when it came through a dialog (AskUserQuestion, an MCP
    /// elicitation). A question asked in plain text just ends the turn, and the rail called that
    /// "Finished and idle" while the agent sat waiting for an answer. The plugin's Stop hook already
    /// writes a TURN_END row carrying the session id, so MT reads the end of that session's
    /// transcript when the row arrives and asks this class whether the final message ends on a
    /// question. No plugin change, so it works whatever plugin and Node version the machine has.
    /// </para>
    /// <para>
    /// It is a heuristic and says so: the LAST PARAGRAPH of the final main-thread message, outside
    /// code, must contain a sentence ending in <c>?</c>. A question worded without one reads as Your
    /// turn (which still beacons, softly); a rhetorical <c>?</c> at the very end raises a question
    /// nobody asked. Every failure to read returns null, the Your-turn side, never a question.
    /// </para>
    /// </remarks>
    public static class ChatQuestionDetector
    {
        /// <summary>Longest question text handed to the card.</summary>
        public const int MaxQuestionLength = 240;

        /// <summary>How much of the transcript's end is read.</summary>
        public const int TailBytes = 128 * 1024;

        private static readonly Regex FencedCode = new Regex(@"```.*?(```|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex InlineMarkup = new Regex(@"[*_`]+", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ParagraphBreak = new Regex(@"\n\s*\n", RegexOptions.Compiled);
        private static readonly Regex SessionIdShape = new Regex(@"^[0-9A-Za-z-]{8,64}$", RegexOptions.Compiled);

        /// <summary>
        /// The question the session's last turn ended on, or null. Never throws.
        /// </summary>
        /// <param name="sessionId">Claude Code session id from the TURN_END row.</param>
        public static string FindQuestionForSession(string sessionId)
        {
            try
            {
                string path = FindTranscript(sessionId, ClaudeProfileResolver.GetAllRoots());
                if (path == null) return null;
                return FindQuestion(FinalAssistantText(ReadTailLines(path, TailBytes)));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The most recently written <c>&lt;root&gt;\projects\&lt;any folder&gt;\&lt;sessionId&gt;.jsonl</c>
        /// under any root. Null for an id that is not a plain id (it becomes a file name, so nothing
        /// else may reach the path) or that no root holds.
        /// </summary>
        /// <remarks>
        /// Searched on every call and never cached: after EnterWorktree, Claude Code goes on writing
        /// the same session id under the worktree's folder, and the copy left in the old folder stops
        /// growing. A cached path would keep reading that stale copy. The newest write is the live one.
        /// </remarks>
        internal static string FindTranscript(string sessionId, IEnumerable<string> roots)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !SessionIdShape.IsMatch(sessionId)) return null;

            string best = null;
            DateTime bestWrite = DateTime.MinValue;
            foreach (string root in roots ?? Enumerable.Empty<string>())
            {
                string projects = Path.Combine(root, "projects");
                if (!Directory.Exists(projects)) continue;

                foreach (string folder in Directory.EnumerateDirectories(projects))
                {
                    string candidate = Path.Combine(folder, sessionId + ".jsonl");
                    if (!File.Exists(candidate)) continue;

                    DateTime written = File.GetLastWriteTimeUtc(candidate);
                    if (best == null || written > bestWrite)
                    {
                        best = candidate;
                        bestWrite = written;
                    }
                }
            }

            return best;
        }

        /// <summary>The complete lines in the last <paramref name="maxBytes"/> of a file.</summary>
        internal static IReadOnlyList<string> ReadTailLines(string path, int maxBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - start];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n').ToList();

            // A read that starts mid-file starts mid-line; that first fragment is not JSON.
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
            return lines;
        }

        /// <summary>
        /// The text of the final main-thread assistant message of the LAST turn, or null.
        /// </summary>
        /// <remarks>
        /// Walks back from the end. A real owner prompt (a main-thread user line that is not a tool
        /// result and not a meta line) ends the walk: text before it belongs to an earlier turn, and
        /// a turn that ended without text (Escape) must not inherit the previous turn's question.
        /// Sidechain lines are a subagent's and say nothing about what the agent asked the owner.
        /// </remarks>
        internal static string FinalAssistantText(IReadOnlyList<string> lines)
        {
            if (lines == null) return null;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;

                    string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    message.TryGetProperty("content", out var content);

                    if (type == "assistant")
                    {
                        string text = LastText(content);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                        continue;
                    }

                    if (type == "user" && IsOwnerPrompt(root, content)) return null;
                }
                catch (JsonException)
                {
                    // A torn or foreign line says nothing; keep walking.
                }
            }

            return null;
        }

        /// <summary>
        /// The question <paramref name="text"/> ends on, or null: its last paragraph outside fenced
        /// code, whitespace collapsed and emphasis marks dropped, when that paragraph has a sentence
        /// ending in <c>?</c>.
        /// </summary>
        public static string FindQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string prose = FencedCode.Replace(text.Replace("\r\n", "\n"), "\n\n");
            string last = ParagraphBreak.Split(prose)
                .Select(p => p.Trim())
                .LastOrDefault(p => p.Length > 0);
            if (last == null) return null;

            string flat = Whitespace.Replace(InlineMarkup.Replace(last, string.Empty), " ").Trim();
            if (!HasQuestionSentence(flat)) return null;

            return flat.Length <= MaxQuestionLength ? flat : flat.Substring(0, MaxQuestionLength - 1).TrimEnd() + "…";
        }

        /// <summary>A <c>?</c> that ends a sentence: at the end, or before whitespace or a closing bracket or quote.</summary>
        private static bool HasQuestionSentence(string flat)
        {
            for (int i = 0; i < flat.Length; i++)
            {
                if (flat[i] != '?') continue;
                int j = i + 1;
                while (j < flat.Length && ")]\"'”’".IndexOf(flat[j]) >= 0) j++;
                if (j >= flat.Length || char.IsWhiteSpace(flat[j])) return true;
            }

            return false;
        }

        private static string LastText(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind != JsonValueKind.Array) return null;

            string last = null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String && bt.GetString() == "text"
                    && block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    last = tx.GetString();
                }
            }

            return last;
        }

        private static bool IsOwnerPrompt(JsonElement root, JsonElement content)
        {
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return false;
            if (content.ValueKind == JsonValueKind.String) return true;
            if (content.ValueKind != JsonValueKind.Array) return false;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                    && bt.GetString() == "tool_result")
                {
                    return false;
                }
            }

            return true;
        }
    }
}
