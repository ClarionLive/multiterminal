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
    /// Task ab20d921 — the Source Account dropdown "forgot" its selection whenever the
    /// Project panel re-rendered.
    ///
    /// Nothing was ever unsaved. ProjectPanelDocument hands the renderer a HAND-COPIED clone
    /// of the project (<c>var projectWithAllPrompts = new Project { ... }</c>) rather than the
    /// project itself, and that initializer simply omitted SourceControlAccountId. The renderer
    /// serializes <c>SourceControlAccountId ?? ""</c>, and panel.html only assigns
    /// <c>select.value</c> when that string is truthy — so the selection fell back to "(None)"
    /// while SQLite still held the correct id.
    ///
    /// The one-line fix is not the interesting part. A hand-maintained copy of a 29-property
    /// model drops any field anyone forgets, with NO compiler error and NO failing test — the
    /// only symptom is a user reporting that the UI lost their setting. Auditing it turned up
    /// three MORE omissions beyond the reported one:
    ///
    ///   DefaultTerminal — worse than the reported bug: Project initialises it to "claude-code",
    ///                     so instead of blanking, a Codex project silently rendered AS Claude Code.
    ///   Status         — panel.html renders a status badge guarded by `!== undefined`.
    ///   TeamAgents     — not currently sent to the panel.
    ///
    /// So this test exists, not the fix. It reflects over Project for the authoritative property
    /// list (compile-accurate — it cannot drift from the model the way a hard-coded list would)
    /// and asserts every one of them appears in the clone.
    ///
    /// TO SEE IT FAIL, delete any line from that initializer: the test names the omitted
    /// property. That is the whole point — the previous failure mode was silence.
    /// </summary>
    public class ProjectPanelCopyCompletenessTests
    {
        /// <summary>
        /// Resolves the repo root from THIS FILE's compile-time path rather than the working
        /// directory or the assembly location, so the test is unaffected by where the runner is
        /// invoked from and works unchanged inside a git worktree.
        /// </summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadCloneInitializer()
        {
            string path = Path.Combine(RepoRoot(), "Docking", "ProjectPanelDocument.cs");
            Assert.True(File.Exists(path), $"Could not locate ProjectPanelDocument.cs at '{path}'.");

            string src = File.ReadAllText(path);

            // Anchor on the declaration, then take the balanced object-initializer that follows.
            int start = src.IndexOf("var projectWithAllPrompts = new Project", StringComparison.Ordinal);
            Assert.True(
                start >= 0,
                "Could not find 'var projectWithAllPrompts = new Project' in ProjectPanelDocument.cs. " +
                "If the panel copy was renamed or replaced with a real Clone(), update or delete this test " +
                "deliberately — do not let it silently stop checking anything.");

            int open = src.IndexOf('{', start);
            Assert.True(open > 0, "Malformed initializer: no opening brace after the declaration.");

            int depth = 0;
            int end = -1;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            Assert.True(end > open, "Malformed initializer: unbalanced braces.");

            return src.Substring(open, end - open + 1);
        }

        [Fact]
        public void PanelProjectCopy_AssignsEveryWritableProjectProperty()
        {
            string initializer = ReadCloneInitializer();

            var missing = new List<string>();
            foreach (var prop in typeof(MultiTerminal.Models.Project).GetProperties())
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                // Match "Name =" as an assignment target, not as a substring of some other
                // identifier — 'Path' must not be satisfied by 'DeployPath = project.DeployPath'.
                var assigned = Regex.IsMatch(
                    initializer,
                    $@"(?<![A-Za-z0-9_.]){Regex.Escape(prop.Name)}\s*=");

                if (!assigned) missing.Add(prop.Name);
            }

            Assert.True(
                missing.Count == 0,
                "ProjectPanelDocument's project copy omits " + missing.Count + " Project propert" +
                (missing.Count == 1 ? "y" : "ies") + ": " + string.Join(", ", missing) + ".\n" +
                "Every omission is a field the Project panel will silently render as empty or as its " +
                "type default — exactly the Source Account bug (task ab20d921). Add the missing " +
                "assignment(s) to the initializer, or replace the hand-copy with a real Clone().");
        }

        [Fact]
        public void PanelProjectCopy_CarriesTheFieldsWhoseOmissionWasUserVisible()
        {
            // A targeted regression guard for the four found in ab20d921. The test above would
            // catch these too, but it fails as one aggregate; naming them keeps the specific
            // reported bug legible if the general test is ever weakened or skipped.
            string initializer = ReadCloneInitializer();

            foreach (var name in new[] { "SourceControlAccountId", "DefaultTerminal", "Status", "TeamAgents" })
            {
                Assert.True(
                    Regex.IsMatch(initializer, $@"(?<![A-Za-z0-9_.]){name}\s*="),
                    $"'{name}' is missing from the Project panel's copy. This is the ab20d921 regression: " +
                    "the value stays correct in SQLite while the panel displays empty (or, for " +
                    "DefaultTerminal, silently displays the wrong terminal).");
            }
        }
    }
}
