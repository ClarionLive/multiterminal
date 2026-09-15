using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task <c>dc813ddd</c>. The checklist transition rule exists THREE times, in two languages, and
    /// nothing held the copies together. This parses all three and asserts they agree.
    ///
    /// <para><b>The three sites, and why the third is the dangerous one.</b>
    /// <list type="number">
    /// <item><c>TaskService.TransitionChecklistItem</c> — the API gate.</item>
    /// <item><c>TaskLifecycleBoardForm</c> — which does NOT call the gate. It reimplements the whole
    /// transition (validation, status, Done, CycleCount, note append), so the board's drag path never
    /// passes through site 1 at all.</item>
    /// <item><c>VALID_TRANSITIONS</c> in <c>lifecycle-board.html</c> — the page's pre-check.</item>
    /// </list>
    /// Site 2 was found only because a UI revert line implied the board had its own opinion. That is
    /// discovery by accident, and accidental discovery says nothing about the total — so the count was
    /// established by searching every language before this file was written, not inferred from the one
    /// copy that happened to surface.</para>
    ///
    /// <para><b>Why parse instead of substring-match.</b> <c>verification-discipline.md</c> says prefer
    /// a structural pin to a textual one. A census built from <c>Assert.Contains("\"done\"")</c> has
    /// both documented failure directions: satisfied by a comment mentioning the cell, and — worse —
    /// tripped by a refusal comment naming it, which punishes the house style of writing "do NOT change
    /// this" above the code. Extracting each table into a dictionary and comparing the dictionaries has
    /// neither property: comments are stripped before parsing, a disagreement names the cell, and no
    /// prose can satisfy it because prose does not parse into cells.</para>
    ///
    /// <para><b>These are not the same table by coincidence.</b> They agreed before this ticket too — I
    /// diffed them cell by cell first, precisely so this file would pin an agreement that exists rather
    /// than manufacture one. The only structural difference is that the JS states <c>"done": []</c>
    /// explicitly where the C# copies omit the key; both rejected, so the outcome matched. That is why
    /// the comparison below is over OUTCOMES (which targets are reachable from which status) rather
    /// than over text.</para>
    ///
    /// <para>Collapsing the duplication so only one copy exists is ticket <c>12eb1e7c</c>, deliberately
    /// not done here — the two C# implementations differ in what they RECORD, so merging them silently
    /// changes board behaviour.</para>
    /// </summary>
    public sealed class ChecklistTransitionRuleCensusTests
    {
        private const string ApiGate = "MCPServer/Services/TaskService.cs";
        private const string BoardHost = "TaskLifecycleBoard/TaskLifecycleBoardForm.cs";
        private const string BoardPage = "TaskLifecycleBoard/lifecycle-board.html";

        /// <summary>
        /// The rule, as this ticket leaves it. Stated once here so a disagreement names the cell that
        /// drifted rather than merely reporting that two files differ.
        /// </summary>
        private static readonly Dictionary<string, string[]> Expected = new()
        {
            ["pending"] = new[] { "coding" },
            ["coding"] = new[] { "testing" },
            ["testing"] = new[] { "coding", "done" },
            ["done"] = new[] { "testing" },
        };

        [Theory]
        [InlineData(ApiGate)]
        [InlineData(BoardHost)]
        [InlineData(BoardPage)]
        public void Every_copy_of_the_transition_table_says_the_same_thing(string relativePath)
        {
            var table = ParseTable(relativePath);

            Assert.True(
                table.Count > 0,
                $"No transition table could be parsed out of {relativePath}. The extraction broke, so "
                + "this fact is vacuous rather than passing — fix the parser, do not delete the fact.");

            Assert.Equal(
                Expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
                table.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var (from, targets) in Expected)
            {
                Assert.True(
                    table.TryGetValue(from, out var actual),
                    $"{relativePath} has no rule for '{from}'.");

                Assert.True(
                    targets.OrderBy(t => t, StringComparer.Ordinal)
                           .SequenceEqual(actual.OrderBy(t => t, StringComparer.Ordinal)),
                    $"{relativePath} allows '{from}' → [{string.Join(", ", actual)}] but the other copies "
                    + $"allow [{string.Join(", ", targets)}]. Three sites hold this rule and the board "
                    + "bypasses the API gate entirely, so a divergence here is a transition one path "
                    + "permits and another refuses, with nothing to reconcile them (task dc813ddd).");
            }
        }

        /// <summary>
        /// ⚠️ THE CELL THIS TICKET ADDED, asserted separately from the table comparison above.
        /// <para>Without this, all three copies could lose the reopen together and the comparison would
        /// stay green — they would still agree, just on the old rule. Agreement and correctness are
        /// different properties and a census that only checks the first can go green on a full
        /// revert.</para>
        /// </summary>
        [Theory]
        [InlineData(ApiGate)]
        [InlineData(BoardHost)]
        [InlineData(BoardPage)]
        public void Every_copy_permits_the_reopen(string relativePath)
        {
            var table = ParseTable(relativePath);

            Assert.True(
                table.TryGetValue("done", out var fromDone) && fromDone.Contains("testing"),
                $"{relativePath} no longer permits done → testing. Reopening is what stops a review "
                + "gate closed early — or closed by an ordinary misclick — from being permanent; "
                + "'done' was terminal for the PM and the Owner too, not just for agents.");
        }

        /// <summary>
        /// The notes requirement is duplicated the same three times, and it is what makes a reopen an
        /// audit entry instead of a silent reversal: notes are APPENDED, never replaced, so a required
        /// reason lands after the pass it undoes and the history keeps both.
        /// </summary>
        [Theory]
        [InlineData(ApiGate)]
        [InlineData(BoardHost)]
        [InlineData(BoardPage)]
        public void Every_copy_requires_a_reason_for_the_reopen(string relativePath)
        {
            string src = StripComments(ReadRepoFile(relativePath));

            int marker = src.IndexOf("RequiresNotes", StringComparison.Ordinal);
            if (marker < 0) marker = src.IndexOf("IsNullOrWhiteSpace(notes)", StringComparison.Ordinal);

            Assert.True(
                marker >= 0,
                $"Could not locate the notes-required rule in {relativePath}; this fact is vacuous.");

            // Scope to the rule's own expression rather than the file: a file-wide scan for "done"
            // is satisfied by any unrelated mention.
            int start = Math.Max(0, marker - 400);
            string region = src[start..Math.Min(src.Length, marker + 400)];

            Assert.True(
                region.Contains("\"done\"", StringComparison.Ordinal),
                $"{relativePath}'s notes-required rule does not mention 'done', so a reopen can be "
                + "recorded with no reason. That turns the one transition that undoes a recorded pass "
                + "into the only one that need not say why.");
        }

        /// <summary>
        /// ⚠️ THE PROSE SITE THAT IS HANDED TO CALLERS AT RUNTIME. An MCP tool description is read by
        /// agents AS the rule, so a stale one misinforms continuously rather than sitting in a file
        /// nobody opens.
        ///
        /// <para>Asserted POSITIVELY — that the description mentions the reopen — and never negatively
        /// (that it lacks "terminal", say). The negative direction is tripped by a refusal comment
        /// naming the forbidden phrase, manufacturing a failure and pushing the next person to change
        /// correct text. That failure mode was found in this program's own review pass, and putting it
        /// into the fix for it would be a poor joke.</para>
        /// </summary>
        [Fact]
        public void The_mcp_tool_description_tells_agents_the_reopen_exists()
        {
            string src = ReadRepoFile("mcp/index.js");

            int def = src.IndexOf("name: \"update_task_checklist\"", StringComparison.Ordinal);
            Assert.True(def >= 0, "The update_task_checklist tool definition is gone; this fact is vacuous.");

            int nextTool = src.IndexOf("name: \"", def + 20, StringComparison.Ordinal);
            string block = nextTool > def ? src[def..nextTool] : src[def..];

            Assert.Contains("done→testing", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// Parses a transition table into <c>from → targets</c>, from either the C# dictionary-initialiser
        /// form or the JS object form. Comments are stripped first, so a commented-out table cannot be
        /// parsed and a commented-out cell cannot contribute one.
        /// </summary>
        private static Dictionary<string, string[]> ParseTable(string relativePath)
        {
            string src = StripComments(ReadRepoFile(relativePath));
            var table = new Dictionary<string, string[]>(StringComparer.Ordinal);

            // C#:  { "testing", new[] { "coding", "done" } }
            foreach (Match m in Regex.Matches(src, @"\{\s*""(\w+)""\s*,\s*new\[\]\s*\{([^}]*)\}\s*\}"))
            {
                table[m.Groups[1].Value] = ExtractQuoted(m.Groups[2].Value);
            }

            // JS:  "testing":  ["coding", "done"]
            foreach (Match m in Regex.Matches(src, @"""(\w+)""\s*:\s*\[([^\]]*)\]"))
            {
                table[m.Groups[1].Value] = ExtractQuoted(m.Groups[2].Value);
            }

            // Keep only the four lifecycle statuses: both patterns are generic enough to match unrelated
            // literals elsewhere in a large file, and a stray key would fail the key-set comparison for
            // the wrong reason.
            return table
                .Where(kv => Expected.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        private static string[] ExtractQuoted(string inner) =>
            Regex.Matches(inner, @"""([^""]*)""").Select(m => m.Groups[1].Value).ToArray();

        /// <summary>Block and line comments removed, so no census here can be satisfied by prose.</summary>
        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            noBlocks = Regex.Replace(noBlocks, @"<!--.*?-->", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string ReadRepoFile(string relativePath)
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = Path.GetFullPath(Path.Combine(here, "..", relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(path), $"Could not locate '{relativePath}' at '{path}'.");
            return File.ReadAllText(path);
        }

        private static string ThisFile([CallerFilePath] string path = null) => path;
    }
}
