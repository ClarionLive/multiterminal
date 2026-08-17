using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task f5744489 — the Tasks pane's own HUD, driven by the selected card.
    ///
    /// <para>This file REPLACES PlanGraphDoorwayTests, which pinned the route this ticket deleted
    /// (card Plan glyph -> PlanGraphRequested -> MainForm picks a terminal -> TerminalDocument
    /// pins its HUD). The old file was not simply dropped: the contracts it guarded were of a shape
    /// that still exists, so the same discipline is re-pointed at the new route, plus assertions
    /// that the OLD one is genuinely gone rather than merely unreferenced.</para>
    ///
    /// <para>The new route is: card click -> WebView2 message {type:'card_selected', taskId} ->
    /// TasksPanelControl.TaskSelected -> TasksPanelDocument.SetSelectedTask -> both board renderers.
    /// Every hop but the last is a STRING contract between files that no compiler checks.</para>
    ///
    /// <para>That risk is not hypothetical on this feature. Under 60665c6c, pipeline Run 1 caught a
    /// CRITICAL defect of exactly this shape — the renderer serialized PascalCase while the view
    /// read camelCase, so the tab rendered nothing, with a clean build and 442 passing tests,
    /// because nothing exercised the seam.</para>
    ///
    /// <para>WHAT IS NOT COVERED, stated plainly: this is WinForms + WebView2, so none of it can be
    /// instantiated here. These are source-level assertions about contracts, not proof that a click
    /// fills a panel. That needs the deployed app.</para>
    ///
    /// <para>DISCIPLINE: the implementation comments discuss the deleted names at length while
    /// explaining the design, so a bare Contains() over raw source would be satisfied by PROSE and
    /// would pass against deleted code — which would make the removal assertions below actively
    /// misleading. Every assertion runs over comment-stripped text.</para>
    /// </summary>
    public class BoardHudDoorwayTests
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
                if (src[i] == '{')
                {
                    depth++;
                }
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return src.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail($"Unbalanced braces while reading {what}.");
            return string.Empty;
        }

        private static string PanelCaseBody()
        {
            string src = ReadStripped("TasksPanel", "TasksPanelControl.cs");
            int start = src.IndexOf("case \"card_selected\":", StringComparison.Ordinal);
            Assert.True(
                start >= 0,
                "TasksPanelControl no longer handles the 'card_selected' message — clicking a card " +
                "would select nothing and the board HUD would never update.");

            int end = src.IndexOf("break;", start, StringComparison.Ordinal);
            Assert.True(end >= 0, "Found the card_selected case but no terminating break;.");
            return src.Substring(start, end - start);
        }

        private static string SelectTaskJsBody()
            => BalancedBodyAfter(
                ReadStripped("TasksPanel", "tasks-panel.html"),
                "function selectTask(",
                "the panel's selectTask() function");

        // -----------------------------------------------------------------
        // The new route
        // -----------------------------------------------------------------

        /// <summary>
        /// THE test on this ticket. The panel and the host agree on a message type and a set of
        /// field names, in two different files, in two different languages, with nothing checking
        /// them. Every field the C# reads must be a field the panel actually sends — otherwise the
        /// click silently selects nothing, with no error anywhere.
        /// </summary>
        [Fact]
        public void Panel_sends_every_field_the_host_reads_for_card_selection()
        {
            string caseBody = PanelCaseBody();
            string jsBody = SelectTaskJsBody();

            Assert.Contains("'card_selected'", jsBody, StringComparison.Ordinal);

            var hostReads = Regex.Matches(caseBody, @"TryGetProperty\(""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            Assert.True(
                hostReads.Count >= 1,
                $"Expected the host to read at least taskId; found: [{string.Join(", ", hostReads)}].");

            var panelSends = Regex.Matches(jsBody, @"(\w+)\s*:")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string field in hostReads)
            {
                Assert.True(
                    panelSends.Contains(field),
                    $"TasksPanelControl reads '{field}' from the card_selected payload, but " +
                    $"selectTask() never sends it. Panel sends: [{string.Join(", ", panelSends)}].");
            }
        }

        /// <summary>
        /// The host must actually raise the event; reading the payload and dropping it is a failure
        /// mode that looks identical to a working parse from inside this file.
        /// </summary>
        [Fact]
        public void Host_raises_the_selection_event_rather_than_swallowing_the_click()
        {
            Assert.Matches(@"TaskSelected\?\.Invoke", PanelCaseBody());
        }

        /// <summary>
        /// A cleared selection must reach the host. The board clears the selection when the card is
        /// deleted, filtered out, or hidden by a project switch — if that only repainted the CSS,
        /// the HUD would carry on describing a ticket that is no longer anywhere on screen, which is
        /// the "honest empty state" acceptance criterion failing silently.
        /// </summary>
        [Fact]
        public void Clearing_the_selection_notifies_the_host_and_does_not_merely_repaint()
        {
            string body = BalancedBodyAfter(
                ReadStripped("TasksPanel", "tasks-panel.html"),
                "function reconcileSelection(",
                "the panel's reconcileSelection() function");

            Assert.Contains("card_selected", body, StringComparison.Ordinal);
            Assert.Contains("selectedTaskId = null", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every path that rebuilds the cards funnels through renderAllTasks, so the reconcile must
        /// hang off it. Without this the highlight survives only until the next refresh — and a
        /// deleted card's selection would never be cleared at all, because nothing else notices.
        /// </summary>
        [Fact]
        public void Every_card_rebuild_reconciles_the_selection()
        {
            string body = BalancedBodyAfter(
                ReadStripped("TasksPanel", "tasks-panel.html"),
                "function renderAllTasks(",
                "the panel's renderAllTasks() function");

            Assert.Contains("reconcileSelection()", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// One selection must drive BOTH tabs. If only one were wired, the panel would show a plan
        /// for one ticket beside the detail of another — a disagreement more misleading than either
        /// tab simply being empty.
        /// </summary>
        [Fact]
        public void Selection_drives_both_board_tabs()
        {
            string body = BalancedBodyAfter(
                ReadStripped("TasksPanel", "TasksPanelDocument.cs"),
                "public void SetSelectedTask(",
                "TasksPanelDocument.SetSelectedTask");

            Assert.Contains("_boardTaskHud", body, StringComparison.Ordinal);
            Assert.Contains("_boardGraph", body, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------
        // The decisions this ticket had to make, pinned so they cannot rot
        // -----------------------------------------------------------------

        /// <summary>
        /// The board HUD's tab ids must DIFFER from the terminal HUD's.
        /// </summary>
        /// <remarks>
        /// Not a style preference. A tab id doubles as the zoom persistence key in one flat,
        /// app-wide settings file, and MainForm fans a changed key out to every TerminalDocument.
        /// Shared ids would therefore make zooming the board's Plan tab silently resize the Plan tab
        /// in every open terminal — a surprising effect with no visible cause. This asserts the ids
        /// are distinct rather than asserting their exact spelling, so they can be renamed.
        /// </remarks>
        [Fact]
        public void Board_hud_tab_ids_are_distinct_from_the_terminal_hud_tab_ids()
        {
            string doc = ReadStripped("TasksPanel", "TasksPanelDocument.cs");

            string boardTasks = Regex.Match(doc, @"BoardTasksTabId\s*=\s*""([^""]+)""").Groups[1].Value;
            string boardGraph = Regex.Match(doc, @"BoardGraphTabId\s*=\s*""([^""]+)""").Groups[1].Value;

            Assert.False(string.IsNullOrEmpty(boardTasks), "BoardTasksTabId constant not found.");
            Assert.False(string.IsNullOrEmpty(boardGraph), "BoardGraphTabId constant not found.");

            Assert.NotEqual("__tasks__", boardTasks);
            Assert.NotEqual("__graph__", boardGraph);
            Assert.NotEqual(boardTasks, boardGraph);

            string container = ReadStripped("Controls", "HudTabContainer", "HudTabContainer.cs");
            string defaultId = Regex.Match(container, @"DefaultTaskTabId\s*=\s*""([^""]+)""").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(defaultId), "HudTabContainer.DefaultTaskTabId not found.");
            Assert.NotEqual(defaultId, boardTasks);
        }

        /// <summary>
        /// The board's Tasks renderer must refuse activation BEFORE it touches the broker.
        /// </summary>
        /// <remarks>
        /// <para>The ticket named the failure mode exactly: "silently reusing the renderer and
        /// hoping the action path is never hit". Hiding the button is not enough — the message
        /// arrives from a WebView2, and a stale or malformed one can reach the handler regardless of
        /// what the current DOM offers.</para>
        /// <para>ORDER is the assertion, not mere presence. A refusal placed after the first broker
        /// call would already have read (or mutated) state on behalf of a terminal that was never
        /// chosen, which is the whole thing being prevented.</para>
        /// </remarks>
        [Fact]
        public void Board_mode_refuses_activation_before_any_broker_call()
        {
            string body = BalancedBodyAfter(
                ReadStripped("Controls", "TaskHudPanel", "TaskHudRenderer.cs"),
                "private void HandleSetTaskActive(",
                "TaskHudRenderer.HandleSetTaskActive");

            int refusal = body.IndexOf("TaskHudMode.Board", StringComparison.Ordinal);
            Assert.True(
                refusal >= 0,
                "HandleSetTaskActive has no board-mode refusal. The board's Tasks tab could claim, " +
                "re-assign or activate a task on behalf of no terminal at all.");

            int firstBrokerUse = body.IndexOf("_broker", StringComparison.Ordinal);
            Assert.True(firstBrokerUse >= 0, "Expected HandleSetTaskActive to reference _broker at all.");

            Assert.True(
                refusal < firstBrokerUse,
                $"The board-mode refusal (index {refusal}) comes AFTER the first _broker reference " +
                $"(index {firstBrokerUse}). It must be the first thing checked, or state is read or " +
                "mutated for a terminal that was never chosen before the guard runs.");
        }

        /// <summary>
        /// Board mode must be declared BEFORE the broker is wired.
        /// </summary>
        /// <remarks>
        /// TaskHudRenderer.SetBoardMode clears the queued terminal name. A name queued first would
        /// be adopted later by Initialize and quietly re-arm the action path this panel must never
        /// have — a wiring-order bug that would leave every visible symptom looking correct.
        /// </remarks>
        [Fact]
        public void Board_renderers_enter_board_mode_before_the_broker_is_wired()
        {
            string doc = ReadStripped("TasksPanel", "TasksPanelDocument.cs");

            int setBoardMode = doc.IndexOf("_boardTaskHud.SetBoardMode()", StringComparison.Ordinal);
            int initialize = doc.IndexOf("_boardTaskHud?.Initialize(", StringComparison.Ordinal);

            Assert.True(setBoardMode >= 0, "TasksPanelDocument never puts the Tasks renderer into board mode.");
            Assert.True(initialize >= 0, "TasksPanelDocument never initializes the board Tasks renderer.");
            Assert.True(
                setBoardMode < initialize,
                "SetBoardMode must run before Initialize; otherwise a queued terminal name survives " +
                "and re-arms the activate path.");

            Assert.Contains("_boardGraph.SetBoardMode()", doc, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------
        // Removal proofs — "gone, not merely unused"
        // -----------------------------------------------------------------

        /// <summary>
        /// The old doorway must be absent from production source, not just unreferenced.
        /// </summary>
        /// <remarks>
        /// The acceptance criterion is "the pinning path and the pinned badge are gone, not merely
        /// unused", and a dormant path is exactly what someone re-wires by accident later. Runs over
        /// comment-stripped text because all three deletion sites carry comments explaining what
        /// used to be there — those comments are wanted, and would otherwise fail this test.
        /// </remarks>
        [Theory]
        [InlineData("MainForm.cs")]
        [InlineData("Docking/TerminalDocument.cs")]
        [InlineData("TasksPanel/TasksPanelControl.cs")]
        [InlineData("TasksPanel/TasksPanelDocument.cs")]
        [InlineData("TasksPanel/tasks-panel.html")]
        public void The_old_plan_glyph_doorway_is_gone_from(string relativePath)
        {
            string src = ReadStripped(relativePath.Split('/'));

            foreach (string token in new[]
            {
                "open_plan_graph",
                "openPlanGraph",
                "OpenPlanGraph",
                "PlanGraphRequested",
                "PlanGraphRequestEventArgs",
            })
            {
                Assert.False(
                    src.Contains(token, StringComparison.Ordinal),
                    $"'{token}' still appears in {relativePath} outside comments. The board doorway " +
                    "was deleted by task f5744489; a surviving fragment is a path back to terminals " +
                    "being borrowable.");
            }
        }

        /// <summary>
        /// The pinned badge and its unpin round-trip must be gone from the Plan view.
        /// </summary>
        /// <remarks>
        /// The badge existed to make a borrow reversible. With no borrow possible there is nothing
        /// to reverse, and leaving a control that can never appear is how a later reader concludes
        /// the feature still exists.
        /// </remarks>
        [Fact]
        public void The_pin_badge_is_gone_from_the_plan_view()
        {
            string html = ReadStripped("Controls", "HudGraphPanel", "hud-graph.html");

            Assert.DoesNotContain("pinBadge", html, StringComparison.Ordinal);
            Assert.DoesNotContain("'unpin'", html, StringComparison.Ordinal);

            string renderer = ReadStripped("Controls", "HudGraphPanel", "HudGraphRenderer.cs");
            Assert.DoesNotContain("_pinnedTaskId", renderer, StringComparison.Ordinal);
        }

        /// <summary>
        /// A terminal-mode Plan view must not be bindable to an arbitrary ticket.
        /// </summary>
        /// <remarks>
        /// SetTask still EXISTS — the board needs it — so its survival is not the risk. The risk is
        /// it being callable on a terminal's renderer, which is what the mode guard prevents. This
        /// asserts the guard is the first statement, because a guard placed after the assignment
        /// would set the field and then decline to act on it, leaving the renderer holding a foreign
        /// task id that a later refresh could pick up.
        /// </remarks>
        [Fact]
        public void SetTask_is_inert_outside_board_mode()
        {
            string body = BalancedBodyAfter(
                ReadStripped("Controls", "HudGraphPanel", "HudGraphRenderer.cs"),
                "public void SetTask(",
                "HudGraphRenderer.SetTask");

            int guard = body.IndexOf("_mode != GraphViewMode.Board", StringComparison.Ordinal);
            int assignment = body.IndexOf("_boardTaskId =", StringComparison.Ordinal);

            Assert.True(guard >= 0, "HudGraphRenderer.SetTask has no board-mode guard — a terminal's Plan tab is still pinnable.");
            Assert.True(assignment >= 0, "HudGraphRenderer.SetTask no longer assigns the board task id.");
            Assert.True(guard < assignment, "The board-mode guard must precede the assignment.");
        }

        /// <summary>
        /// All of the Plan view's empty-state sends must carry the context field.
        /// </summary>
        /// <remarks>
        /// This is the direct descendant of a real defect: the pre-f5744489 catch block hand-built
        /// its payload and omitted the flag the view keyed its copy off, so a tab bound to a ticket
        /// announced "No active task" — telling the reader the opposite of the truth. The fix routes
        /// every send through one helper; this asserts nobody has hand-rolled a second one.
        /// </remarks>
        [Fact]
        public void Every_empty_state_send_goes_through_the_single_helper()
        {
            string renderer = ReadStripped("Controls", "HudGraphPanel", "HudGraphRenderer.cs");

            var handRolled = Regex.Matches(renderer, @"Send\(new\s*\{[^}]*no_task[^}]*\}")
                .Select(m => m.Value)
                .ToList();

            Assert.True(
                handRolled.Count == 0,
                $"Found {handRolled.Count} hand-built no_task payload(s) bypassing NoTask(). Every " +
                "empty-state send must go through the one helper, or they drift apart and the view " +
                $"renders the wrong empty state: [{string.Join(" | ", handRolled)}]");

            Assert.Contains("private object NoTask()", renderer, StringComparison.Ordinal);
            Assert.Contains("context = ContextName", renderer, StringComparison.Ordinal);
        }
    }
}
