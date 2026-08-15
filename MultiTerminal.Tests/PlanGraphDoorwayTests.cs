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
    /// Task 60665c6c, item 6 — the board doorway: a Plan glyph on the kanban card that opens
    /// the HUD Plan tab pinned to that ticket.
    ///
    /// The route is board (its own dock window) -> WebView2 message -> TasksPanelControl ->
    /// PlanGraphRequested -> MainForm (resolves which terminal) -> TerminalDocument.OpenPlanGraph
    /// -> HudGraphRenderer.SetTask + SwitchToTabById("__graph__"). Every hop but the last is a
    /// STRING contract between files that no compiler checks.
    ///
    /// That is not a hypothetical worry on this ticket. Pipeline Run 1 caught a CRITICAL defect of
    /// exactly this shape: the renderer serialized PascalCase while the view read camelCase, so the
    /// tab would have rendered nothing. The build was clean and 442 tests passed, because nothing
    /// exercised the seam. These tests exercise the seams this item adds.
    ///
    /// WHAT IS NOT COVERED, stated plainly: this is WinForms + WebView2, so none of it can be
    /// instantiated in a unit test. These are source-level assertions about contracts, not proof
    /// that a click opens a tab. That needs the deployed app.
    ///
    /// DISCIPLINE (item-④ lesson, and it applies with force here): the implementation comments name
    /// "open_plan_graph", "__graph__" and "SetTask" while explaining the design, so a bare
    /// Contains() over raw source would be satisfied by prose and pass on deleted code. Every
    /// assertion below runs over comment-stripped text.
    /// </summary>
    public class PlanGraphDoorwayTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        /// <summary>
        /// Strips block comments and whole-line // comments. Whole-line is the right granularity:
        /// it removes the explanatory prose that could otherwise satisfy an assertion, without
        /// mangling "https://" inside a string literal.
        /// </summary>
        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var kept = noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", kept);
        }

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return StripComments(File.ReadAllText(path));
        }

        /// <summary>Extracts a brace-balanced body starting from the first match of an anchor.</summary>
        private static string BalancedBodyAfter(string src, string anchor, string what)
        {
            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find {what} (anchor: '{anchor}').");

            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, $"Found {what} but no opening brace followed it.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces while reading {what}.");
            return null;
        }

        private static string PanelCaseBody()
        {
            string src = ReadStripped("TasksPanel", "TasksPanelControl.cs");
            int start = src.IndexOf("case \"open_plan_graph\":", StringComparison.Ordinal);
            Assert.True(
                start >= 0,
                "TasksPanelControl no longer handles the 'open_plan_graph' message — the board " +
                "doorway's host end is gone, so the glyph would post into the void.");

            int end = src.IndexOf("break;", start, StringComparison.Ordinal);
            Assert.True(end >= 0, "Found the open_plan_graph case but no terminating break;.");
            return src.Substring(start, end - start);
        }

        private static string OpenPlanGraphJsBody()
            => BalancedBodyAfter(
                ReadStripped("TasksPanel", "tasks-panel.html"),
                "function openPlanGraph(",
                "the panel's openPlanGraph() function");

        /// <summary>
        /// THE test on this item. The panel and the host agree on a message type and a set of
        /// field names, in two different files, in two different languages, with nothing checking
        /// them. Every field the C# reads must be a field the panel actually sends — otherwise the
        /// click silently opens the wrong thing (or nothing) with no error anywhere.
        /// </summary>
        [Fact]
        public void Panel_sends_every_field_the_host_reads_for_the_plan_doorway()
        {
            string caseBody = PanelCaseBody();
            string jsBody = OpenPlanGraphJsBody();

            Assert.Contains("'open_plan_graph'", jsBody, StringComparison.Ordinal);

            var hostReads = Regex.Matches(caseBody, @"TryGetProperty\(""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            Assert.True(
                hostReads.Count >= 2,
                $"Expected the host to read at least taskId and assignee; found: [{string.Join(", ", hostReads)}].");

            var panelSends = Regex.Matches(jsBody, @"(\w+)\s*:")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string field in hostReads)
            {
                Assert.True(
                    panelSends.Contains(field),
                    $"TasksPanelControl reads '{field}' from the open_plan_graph payload, but " +
                    $"openPlanGraph() never sends it. Panel sends: [{string.Join(", ", panelSends)}].");
            }
        }

        /// <summary>
        /// The host must actually raise the event; reading the payload and dropping it is a
        /// failure mode that looks identical to a working parse from inside this file.
        /// </summary>
        [Fact]
        public void Host_raises_the_plan_graph_event_rather_than_swallowing_the_click()
        {
            Assert.Matches(@"PlanGraphRequested\?\.Invoke", PanelCaseBody());
        }

        /// <summary>
        /// A doorway into an empty room reads as a broken feature rather than an absent one:
        /// the Plan tab's honest state for a checklist-less ticket is "This ticket has no
        /// checklist yet", so the glyph is gated rather than always shown.
        /// </summary>
        [Fact]
        public void Plan_glyph_is_gated_on_the_card_having_a_checklist()
        {
            string html = ReadStripped("TasksPanel", "tasks-panel.html");

            var planIcon = Regex.Match(html, @"const planIcon\s*=\s*([^\n]*)");
            Assert.True(planIcon.Success, "The card no longer builds a planIcon — the doorway is gone from the board.");
            Assert.Contains("hasChecklist(task)", planIcon.Groups[1].Value, StringComparison.Ordinal);

            // And the gate must be a real predicate over the parsed checklist, not a stub that
            // always answers yes.
            string gate = BalancedBodyAfter(html, "function hasChecklist(", "the hasChecklist() gate");
            Assert.Contains("parseChecklist(task)", gate, StringComparison.Ordinal);
            Assert.Contains("length", gate, StringComparison.Ordinal);
        }

        /// <summary>
        /// OpenPlanGraph must pin BEFORE it switches. Reversed, the tab becomes visible still
        /// showing the previously-pinned ticket and then swaps under the reader — which is
        /// precisely the "did my click do anything?" ambiguity this doorway exists to remove.
        /// </summary>
        [Fact]
        public void Terminal_document_pins_the_task_before_switching_to_the_tab()
        {
            string body = BalancedBodyAfter(
                ReadStripped("Docking", "TerminalDocument.cs"),
                "public void OpenPlanGraph(",
                "TerminalDocument.OpenPlanGraph");

            int setTask = body.IndexOf("SetTask(taskId)", StringComparison.Ordinal);
            int switchTab = body.IndexOf("SwitchToTabById(", StringComparison.Ordinal);

            Assert.True(setTask >= 0, "OpenPlanGraph no longer pins the task via SetTask — the tab would show whatever was already active.");
            Assert.True(switchTab >= 0, "OpenPlanGraph no longer switches to the Plan tab.");
            Assert.True(setTask < switchTab, "SetTask must run BEFORE SwitchToTabById.");
        }

        /// <summary>
        /// The tab id in the deep link must be the id the tab was registered under. These live in
        /// the same file but ~700 lines apart, and a mismatch is a silent no-op: SwitchToTabById
        /// guards on the lookup and returns quietly when the id is unknown.
        /// </summary>
        [Fact]
        public void Deep_link_uses_the_tab_id_the_plan_tab_is_registered_under()
        {
            string src = ReadStripped("Docking", "TerminalDocument.cs");

            var registration = Regex.Match(src, @"AddPermanentTab\(\s*""(__\w+__)""[^)]*_hudGraph\s*\)");
            Assert.True(
                registration.Success,
                "Could not find the AddPermanentTab registration for _hudGraph in TerminalDocument.");

            string registeredId = registration.Groups[1].Value;

            string body = BalancedBodyAfter(src, "public void OpenPlanGraph(", "TerminalDocument.OpenPlanGraph");
            Assert.Contains($"SwitchToTabById(\"{registeredId}\")", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// An UNASSIGNED card must still open somewhere. Without a fallback the glyph is dead on
        /// exactly the tickets a reviewer is most likely to be reading — ones nobody has claimed —
        /// and a click that does nothing is indistinguishable from a broken build.
        /// </summary>
        [Fact]
        public void Unassigned_card_falls_back_to_a_terminal_instead_of_doing_nothing()
        {
            string body = BalancedBodyAfter(
                ReadStripped("MainForm.cs"),
                "private void OnPlanGraphRequested(",
                "MainForm.OnPlanGraphRequested");

            int assigneeLookup = body.IndexOf("e.Assignee", StringComparison.Ordinal);
            int fallback = body.IndexOf("_lastActiveTerminal", StringComparison.Ordinal);

            Assert.True(assigneeLookup >= 0, "The handler no longer prefers the assignee's terminal.");
            Assert.True(
                fallback >= 0,
                "The handler has no _lastActiveTerminal fallback, so a click on an unassigned card " +
                "resolves to nothing and silently does nothing.");
            Assert.True(assigneeLookup < fallback, "The assignee must be PREFERRED, with the last-active terminal as the fallback.");
            Assert.Contains("OpenPlanGraph(e.TaskId)", body, StringComparison.Ordinal);
        }
    }
}
