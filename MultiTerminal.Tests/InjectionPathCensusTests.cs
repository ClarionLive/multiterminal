using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Census of the UNSERIALIZED injection path — <c>TerminalControl.InjectInputAsync</c>, which
    /// writes straight to ConPTY and never reaches <c>terminal.html</c>'s typing queue (task f420feeb).
    ///
    /// <para><b>Why this class exists at all.</b> <c>InitialPromptTriggerWiringTests</c> pins the
    /// typing queue, and its own class doc states that it "structurally cannot observe" this path.
    /// That was true and remained true while the gap went unwatched — the suite gave confidence
    /// exactly where the hole was. These facts watch the other half.</para>
    ///
    /// <para><b>Removal proofs, not presence checks.</b> Two of the facts below assert that deleted
    /// code is ABSENT rather than merely unused. A dormant wrapper or an unreachable fallback reads
    /// as harmless and is one edit away from being live again, so "nothing calls it today" is not the
    /// property worth pinning. Same idiom as <c>BoardHudDoorwayTests</c>' removal proofs.</para>
    /// </summary>
    public class InjectionPathCensusTests
    {
        /// <summary>
        /// 1c — the fire-and-forget <c>InjectInput</c> pair is gone, not merely uncalled. It discarded
        /// the result of an operation whose result is the only way to know whether anything was
        /// submitted, and offered a one-line way into the unserialized path for the next person who
        /// wanted "just inject this".
        /// </summary>
        [Fact]
        public void The_fire_and_forget_inject_wrapper_pair_is_absent_not_merely_unused()
        {
            foreach (string file in new[] { "Controls/TerminalControl.cs", "Docking/TerminalDocument.cs" })
            {
                string src = Strip(File.ReadAllText(RepoPath(file.Split('/'))));

                // Declarations only: `InjectInputAsync` must survive, `InjectInput` must not.
                var declarations = Regex.Matches(src, @"public\s+(?:bool|void)\s+InjectInput\s*\(");
                Assert.True(
                    declarations.Count == 0,
                    $"{file} still declares a non-async InjectInput. It was deleted on task f420feeb because it swallowed the success/failure of an injection; re-adding one re-opens that.");
            }
        }

        /// <summary>
        /// 1d — the task-drop failure path must not re-type the prompt.
        /// <c>InjectInputAsync</c> writes text to ConPTY BEFORE it attempts Enter, so nearly every
        /// failure it can report leaves the prompt already in the composer; typing it again appended
        /// a second copy. Retrying the SUBMIT adds no text and therefore cannot duplicate.
        /// </summary>
        [Fact]
        public void The_task_drop_failure_path_retries_the_submit_and_never_retypes()
        {
            string body = MethodBody("private async void OnTaskDroppedOnTerminal");

            Assert.Contains("SendEnterAsync()", body, StringComparison.Ordinal);

            // ⚠️ NO TypeInput AT ALL, not "no TypeInput(prompt)" (self-reported in peer review). The
            // earlier assertion named the local variable, so renaming `prompt` to `taskPrompt` would
            // have made it vacuous while the defect walked back in — and the falsification I ran
            // reinstated the call VERBATIM, which is the one edit a name-coupled assertion still
            // catches. This handler has no business typing anything: it injects, and on failure it
            // retries the submit.
            Assert.DoesNotContain("TypeInput", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ THE ENUMERATION ITSELF. The ticket that opened this work listed FOUR callers of the
        /// unserialized path; there were SIX. The list was not wrong through carelessness — a caller
        /// is one ordinary line in an 8K-line file, and nothing anywhere forced the list to stay
        /// true.
        ///
        /// <para>So this fact is deliberately brittle: it fails when a caller is added OR removed.
        /// That is the intended cost. Until the two injection mechanisms are unified, every new
        /// caller of the path that bypasses the typing queue should be a decision somebody makes on
        /// purpose, not a line that slips in — and the failure message is where they find out what
        /// they are joining.</para>
        /// </summary>
        [Fact]
        public void Every_caller_of_the_unserialized_injection_path_is_enumerated_here()
        {
            // ⚠️ SCANS THE WHOLE TREE, because the NAME of this fact says "every caller" and it used to
            // read MainForm.cs alone (self-reported in peer review of this ticket). TerminalDocument
            // .InjectInputAsync is public, so a panel, a dialog or a future service can call it from
            // any file and would have been neither counted nor enumerated — the fact's own failure
            // message said "MainForm.cs calls InjectInputAsync…" while its name claimed the path. The
            // description was wider than the code, which is the defect this ticket keeps finding, in
            // the test written to enforce this ticket's own census.
            //
            // Widened rather than renamed: "every caller in one file" is not the property worth having.
            var callSites = RepoCallSites();
            string src = Strip(File.ReadAllText(RepoPath("MainForm.cs")));

            // The known census, from the f420feeb investigation. Each entry is a live caller of
            // InjectInputAsync — a write straight to ConPTY with no ordering against typed input.
            string[] census =
            {
                "OnChatInjectRequested",          // Chat panel + board Inject button
                "OnChatReplyRequested",           // reply prompt (see ticket 1fcb266c)
                "OnBrokerTerminalInjectRequested",// the self-clear submit tool
                "ProcessNextMessage",             // message-injection fallback (tier 3)
                "OnStartScreenNewProject",        // /new-project onboarding
                "OnTaskDroppedOnTerminal",        // drag a card onto a pane
            };

            // The ONE legitimate call outside MainForm: TerminalDocument's passthrough to the control.
            // It is a delegation, not a caller — every real caller reaches the path through it.
            const string Passthrough = "Docking/TerminalDocument.cs";

            var callers = callSites
                .Where(s => !string.Equals(s.File, Passthrough, StringComparison.Ordinal))
                .ToList();

            Assert.True(
                callSites.Any(s => string.Equals(s.File, Passthrough, StringComparison.Ordinal)),
                $"{Passthrough} no longer delegates to InjectInputAsync; this census's model of the path is stale.");

            string found = string.Join("\n  ", callers.Select(c => $"{c.File}:{c.Line}"));
            Assert.True(
                callers.Count == census.Length,
                $"The repo has {callers.Count} call(s) of InjectInputAsync outside the {Passthrough} passthrough; this census lists {census.Length}.\n" +
                $"Listed: {string.Join(", ", census)}\n" +
                $"Found:\n  {found}\n" +
                "If you ADDED a caller: you are writing straight to ConPTY, bypassing terminal.html's typing queue, with no ordering against any concurrent typing — see task f420feeb before deciding that is what you want, then add yourself here.\n" +
                "If you REMOVED one: delete it from this list.");

            Assert.True(
                callers.All(c => string.Equals(c.File, "MainForm.cs", StringComparison.Ordinal)),
                "A caller of the unserialized injection path appeared OUTSIDE MainForm.cs:\n  " + found +
                "\nThat is not forbidden, but this census resolves its named methods against MainForm.cs, so it must be taught about the new home before it can keep its promise.");

            // ⚠️ THE NAMES ARE CHECKED, NOT JUST THE COUNT. A matching count with a wrong name is a
            // census that reads authoritative and documents nothing — and this list HAD one: it said
            // "OnSpawnRequested" for the /new-project caller, which actually lives in
            // OnStartScreenNewProject. The count passed anyway, so nothing would have caught it.
            // A list of names nobody verifies is the same failure this ticket keeps finding, just in
            // a test instead of a comment.
            foreach (string method in census)
            {
                string body = MethodBodyByName(src, method);
                Assert.True(
                    body.Contains("InjectInputAsync", StringComparison.Ordinal),
                    $"The census names '{method}' as a caller of InjectInputAsync, but its body contains no such call. Either the caller moved and this name is now fiction, or the entry was wrong when written.");
            }
        }

        /// <summary>
        /// The queue is a property of <c>terminal.html</c>, and <c>InjectInputAsync</c> reaches it
        /// through nothing. This pins the GAP rather than a fix — so that when the paths are unified
        /// this fact goes red and has to be dealt with deliberately, instead of the unification
        /// quietly landing while stale prose elsewhere still describes two mechanisms.
        ///
        /// <para>🔴 <b>WHEN THIS GOES RED, DELETE THIS TEST AND RETURN TO TASK f420feeb PHASE 2
        /// (OPTION A) TO CLOSE IT OUT. THE GAP HAS BEEN CLOSED — THIS IS NOT A REGRESSION.</b>
        /// A pinned gap and a pinned invariant are indistinguishable without this line, and the
        /// difference matters in the exact direction that hurts: read as an invariant, a red here
        /// says "someone broke the separation" and the next person reverts the UNIFICATION to
        /// restore the green. That would delete the improvement to save the test.</para>
        /// </summary>
        [Fact]
        public void The_direct_conpty_write_still_bypasses_the_typing_queue()
        {
            string body = MethodBody("private async Task<bool> InjectSingleInputAsync", "Controls", "TerminalControl.cs");

            Assert.Contains("_terminal.Write(text)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TypeInput", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The body of a method located by NAME rather than by a full signature — the census lists
        /// names, and pinning full signatures would make the list fail on an unrelated parameter
        /// change. Finds the declaration (a line carrying an access modifier and the name) rather
        /// than the first mention, so a call site cannot be mistaken for the definition.
        /// </summary>
        private static string MethodBodyByName(string strippedSrc, string name)
        {
            var declaration = Regex.Match(
                strippedSrc,
                @"^[ \t]*(?:private|public|protected|internal)[^\n(]*\b" + Regex.Escape(name) + @"\s*\(",
                RegexOptions.Multiline);

            Assert.True(declaration.Success, $"No declaration of '{name}' found — renamed or removed.");
            return BodyFrom(strippedSrc, declaration.Index, name);
        }

        /// <summary>
        /// Every CALL of <c>InjectInputAsync</c> in the production tree, comments stripped.
        /// <para>Matched on a leading dot (<c>.InjectInputAsync(</c>) so the method's own declarations
        /// are not counted as calls. Test sources are excluded — a test may legitimately drive the path
        /// and is not a production caller.</para>
        /// </summary>
        private static List<(string File, int Line)> RepoCallSites()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile()) ?? ".", ".."));
            var sites = new List<(string, int)>();

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal)
                    || rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith(".claude/", StringComparison.Ordinal)
                    || rel.StartsWith("MultiTerminal.Tests/", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] lines = Strip(File.ReadAllText(path)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], @"\.InjectInputAsync\s*\("))
                    {
                        sites.Add((rel, i + 1));
                    }
                }
            }

            return sites;
        }

        private static string MethodBody(string signature, params string[] file)
        {
            string[] target = file.Length > 0 ? file : new[] { "MainForm.cs" };
            string src = Strip(File.ReadAllText(RepoPath(target)));

            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{signature}' not found in {string.Join('/', target)} — renamed, or the strip broke.");

            return BodyFrom(src, start, signature);
        }

        private static string BodyFrom(string src, int declarationIndex, string label)
        {
            int open = src.IndexOf('{', declarationIndex);
            Assert.True(open >= 0, $"No opening brace after '{label}'.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src[open..(i + 1)];
                }
            }

            Assert.Fail($"Braces never balanced for '{label}'.");
            return string.Empty;
        }

        /// <summary>
        /// Comments stripped before every scan. These facts assert on identifiers that the
        /// surrounding prose discusses at length — the task-drop site's own comment names
        /// <c>TypeInput(prompt)</c> to explain why it is gone — so an unstripped scan would be
        /// satisfied by the explanation of the defect instead of its absence. That exact failure was
        /// found in task 2ddfc32f, where a census passed on a <c>&lt;see cref&gt;</c>.
        ///
        /// <para>⚠️ KNOWN RESIDUAL, shared with every census idiom in this repo
        /// (<c>ReadMainFormStripped</c> and <c>SpawnIdentityCanonicalizationTests.ReadRepoFile</c> are
        /// the same three lines). A line is dropped only when its FIRST non-whitespace is <c>//</c>, so
        /// a TRAILING comment on a line of real code survives and can still satisfy a scan:
        /// <code>doc.SendEnterAsync();  // replaced doc.TypeInput(prompt)</code>
        /// would keep the 1d fact green with the defect reintroduced. Found while re-checking task
        /// c28e6177, by running the same defeating edit in both forms.</para>
        ///
        /// <para>Left as-is deliberately: stripping trailing <c>//</c> naively corrupts string literals
        /// containing it — every URL in <c>mcp/index.js</c>, which this class scans — so the fix is a
        /// real tokenizer, not a regex, and that is a change to make for the whole repo at once rather
        /// than in one test file. Recorded so the limit is known rather than assumed away.</para>
        /// </summary>
        private static string Strip(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string RepoPath(params string[] parts)
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string[] segments = new string[parts.Length + 2];
            segments[0] = here;
            segments[1] = "..";
            Array.Copy(parts, 0, segments, 2, parts.Length);

            string path = Path.GetFullPath(Path.Combine(segments));
            Assert.True(File.Exists(path), $"Could not locate '{string.Join('/', parts)}' at '{path}'.");
            return path;
        }

        private static string ThisFile([CallerFilePath] string path = "") => path;
    }
}
