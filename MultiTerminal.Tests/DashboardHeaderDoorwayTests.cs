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
    /// Task 2289bb8a — can you actually OPEN the panel?
    ///
    /// <para>WHY THIS FILE EXISTS. The Attention panel shipped with a full test suite — the state
    /// machine, the projector, the WebView2 payload contract, 672 green tests, a clean Debug and
    /// Release build — and was completely unreachable in the running app. The toggle button had been
    /// added to <c>MainForm._toolStrip</c>, which the field declaration itself marks as
    /// "Legacy — kept for fallback, replaced by _dashboardHeader" and which is never shown. The
    /// visible toolbar is <c>DashboardHeader/dashboard.html</c>, and it had no button, no
    /// <c>toggle_attention</c> case, and no <c>"attention"</c> case in MainForm's dispatch switch.</para>
    ///
    /// <para>Nothing failed. Every test passed, because every test verified what happens AFTER the
    /// panel is open. The doorway is three string hops across three files in two languages, and no
    /// compiler checks any of them:</para>
    ///
    /// <code>
    /// dashboard.html   sendAction('toggle_X')            -> postMessage {action:'toggle_X'}
    /// DashboardHeaderControl.HandleAction   case "toggle_X"  -> TogglePanelRequested?.Invoke("Y")
    /// MainForm.TogglePanelRequested         case "Y"         -> ToggleYPanel()
    /// </code>
    ///
    /// <para>So these tests are deliberately NOT about the Attention panel specifically. A test that
    /// only pinned <c>btn-attention</c> would have been written by the same person who forgot the
    /// button, at the same moment, from the same wrong mental model of where the toolbar lives. The
    /// two general assertions below hold for EVERY panel, so the next panel added to the header is
    /// covered before anyone thinks to cover it — which is the only version of this test that is
    /// worth having.</para>
    ///
    /// <para>WHAT IS NOT COVERED, stated plainly: this is WinForms + WebView2, so nothing here can be
    /// instantiated. These are source-level assertions that the three hops line up, not proof that a
    /// click opens a panel. That still needs the deployed app. What they do buy is that a hop which
    /// is simply MISSING can no longer pass silently — which is the exact failure that occurred.</para>
    ///
    /// <para>DISCIPLINE, following BoardHudDoorwayTests: every assertion runs over comment-stripped
    /// text, in both languages. The comment I left at the deleted <c>_toolStrip</c> button names
    /// "Attention" and "toggle_attention" while explaining the bug, so a bare Contains() over raw
    /// source would be satisfied by PROSE and the removal proof below would be a lie.</para>
    /// </summary>
    public class DashboardHeaderDoorwayTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string PathTo(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return path;
        }

        /// <summary>Strips block comments and whole-line // comments (C#).</summary>
        private static string ReadCsStripped(params string[] relativeParts)
        {
            string src = File.ReadAllText(PathTo(relativeParts));
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        /// <summary>Strips HTML comments and CSS block comments.</summary>
        private static string ReadHtmlStripped(params string[] relativeParts)
        {
            string src = File.ReadAllText(PathTo(relativeParts));
            string noHtml = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            return Regex.Replace(noHtml, @"/\*.*?\*/", " ", RegexOptions.Singleline);
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

            Assert.Fail($"Unbalanced braces while extracting {what}.");
            return string.Empty;
        }

        private static string Header() => ReadHtmlStripped("DashboardHeader", "dashboard.html");

        private static string Popup() => ReadHtmlStripped("DashboardHeader", "header-popup.html");

        // Since task 4cac608c HandleAction also takes the sending button's position, so the anchor
        // is the method name and first parameter rather than the whole old signature.
        private static string HandleActionBody() => BalancedBodyAfter(
            ReadCsStripped("DashboardHeader", "DashboardHeaderControl.cs"),
            "private void HandleAction(string action",
            "DashboardHeaderControl.HandleAction");

        /// <summary>The popup's choices route through HandleAction, except switch_project, handled here.</summary>
        private static string OnPopupActionBody() => BalancedBodyAfter(
            ReadCsStripped("DashboardHeader", "DashboardHeaderControl.cs"),
            "private void OnPopupAction(string action",
            "DashboardHeaderControl.OnPopupAction");

        private static string MainFormPanelSwitch() => BalancedBodyAfter(
            ReadCsStripped("MainForm.cs"),
            "TogglePanelRequested += (panel) =>",
            "MainForm's TogglePanelRequested handler");

        /// <summary>
        /// HOP 1. Every action the header's markup can emit must be handled in C#. A button wired to
        /// an action nobody dispatches is a dead control: it clicks, it depresses, and nothing occurs.
        /// </summary>
        [Fact]
        public void Every_action_the_header_sends_has_a_HandleAction_case()
        {
            string body = HandleActionBody();

            // sendPopupAction (task 4cac608c) is the M and Select Project buttons: same message, plus a rect.
            var emitted = Regex.Matches(Header(), @"send(?:Popup)?Action\('([a-zA-Z0-9_]+)'")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            // 11 panel/history toggles + show_menu + the 5 project actions. A rename of either send
            // function would otherwise leave this fact checking nothing.
            Assert.True(emitted.Count >= 17, $"Expected at least 17 header actions, extracted {emitted.Count}: {string.Join(", ", emitted)}");

            var unhandled = emitted
                .Where(a => !body.Contains($"case \"{a}\":", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                unhandled.Count == 0,
                "dashboard.html emits action(s) that DashboardHeaderControl.HandleAction does not "
                + $"handle, so the button does nothing: {string.Join(", ", unhandled)}");
        }

        /// <summary>
        /// HOP 2. Every panel name HandleAction forwards must be handled by MainForm. This is the hop
        /// that was actually missing, and it is invisible from either side: the header compiles, and
        /// MainForm compiles, because the contract is a bare string.
        /// </summary>
        [Fact]
        public void Every_panel_the_header_dispatches_has_a_MainForm_case()
        {
            string mainForm = MainFormPanelSwitch();

            var dispatched = Regex.Matches(HandleActionBody(), @"TogglePanelRequested\?\.Invoke\(""([a-zA-Z0-9_]+)""\)")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            Assert.NotEmpty(dispatched);

            var unhandled = dispatched
                .Where(p => !mainForm.Contains($"case \"{p}\":", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                unhandled.Count == 0,
                "DashboardHeaderControl dispatches panel name(s) MainForm's TogglePanelRequested "
                + $"switch does not handle, so the panel never opens: {string.Join(", ", unhandled)}");
        }

        /// <summary>
        /// The specific regression, end to end. The two tests above would each have caught it, but
        /// they answer "is every hop connected?" — this one answers "is the Attention panel, the one
        /// that shipped unreachable, reachable now?" and names the three files in its failure text.
        /// </summary>
        [Fact]
        public void Attention_panel_is_reachable_from_the_dashboard_header()
        {
            string header = Header();
            Assert.True(
                header.Contains("id=\"btn-attention\"", StringComparison.Ordinal),
                "dashboard.html has no btn-attention — the Attention panel has no button on the "
                + "toolbar that is actually visible. (_toolStrip is legacy and is never shown.)");
            Assert.Contains("sendAction('toggle_attention')", header, StringComparison.Ordinal);

            Assert.Contains(
                "TogglePanelRequested?.Invoke(\"attention\")",
                HandleActionBody(),
                StringComparison.Ordinal);

            string mainForm = MainFormPanelSwitch();
            Assert.Contains("case \"attention\":", mainForm, StringComparison.Ordinal);
            Assert.Contains("ToggleAttentionPanel()", mainForm, StringComparison.Ordinal);
        }

        /// <summary>
        /// REMOVAL PROOF. The dead button is gone rather than merely unreferenced, so there is exactly
        /// one doorway. Two entry points where one is structurally unreachable is worse than none: it
        /// reads as wired at every glance, which is precisely how this survived a full review.
        /// </summary>
        [Fact]
        public void The_legacy_toolstrip_carries_no_attention_button()
        {
            string mainForm = ReadCsStripped("MainForm.cs");

            Assert.DoesNotContain("_attentionPanelButton", mainForm, StringComparison.Ordinal);
            Assert.DoesNotContain("_toolStrip.Items.Add(_attentionPanelButton)", mainForm, StringComparison.Ordinal);
        }

        /// <summary>
        /// The header markup is Content-copied, not embedded. Omit the csproj entry and the header
        /// loads a blank WebView2 with no error at all — the trap recorded in
        /// .claude/rules/checklist-graph.md, which costs an entire deploy cycle to diagnose.
        /// The popup page (task 4cac608c) is the same trap: the M menu would open as an empty box.
        /// </summary>
        [Theory]
        [InlineData("dashboard.html")]
        [InlineData("header-popup.html")]
        public void Dashboard_header_html_is_copied_to_output(string file)
        {
            string csproj = File.ReadAllText(PathTo("MultiTerminal.csproj"));
            string name = Regex.Escape(file);

            var include = Regex.Match(
                csproj,
                $@"<(?:Content|None)\s+[^>]*Include=""DashboardHeader[\\/]{name}""[^>]*>.*?</(?:Content|None)>|<(?:Content|None)\s+[^>]*Include=""DashboardHeader[\\/]{name}""[^>]*/>",
                RegexOptions.Singleline);

            Assert.True(include.Success, $"MultiTerminal.csproj does not copy DashboardHeader/{file} to the output.");
            Assert.Contains("PreserveNewest", include.Value, StringComparison.Ordinal);
        }

        /// <summary>
        /// HOP 1 for the popup page (task 4cac608c): every action the M menu, its Grid Layout flyout
        /// and the project picker can send is handled, by HandleAction or (switch_project only) by
        /// OnPopupAction. The menu items are data (MENU/GRID entries) rather than onclick attributes,
        /// so both shapes are extracted.
        /// </summary>
        [Fact]
        public void Every_action_the_header_popup_sends_is_handled()
        {
            string handled = HandleActionBody() + OnPopupActionBody();
            string popup = Popup();

            var emitted = Regex.Matches(popup, @"\baction: '([a-zA-Z0-9_]+)'|data-action=""([a-zA-Z0-9_]+)""")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Distinct()
                .ToList();

            // 5 menu actions + 8 grid layouts + browse_all + switch_project.
            Assert.True(emitted.Count >= 15, $"Expected at least 15 popup actions, extracted {emitted.Count}: {string.Join(", ", emitted)}");

            var unhandled = emitted
                .Where(a => !handled.Contains($"case \"{a}\":", StringComparison.Ordinal)
                         && !handled.Contains($"action == \"{a}\"", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                unhandled.Count == 0,
                "header-popup.html emits action(s) nothing handles, so the menu item does nothing: "
                + string.Join(", ", unhandled));
        }

        /// <summary>
        /// HOP 2 for every header event: each <c>...Requested</c> event DashboardHeaderControl declares
        /// is subscribed by MainForm. An event with no subscriber is a button that clicks and does
        /// nothing, with a clean build. General on purpose, like HOP 1: the next event is covered
        /// before anyone thinks of it.
        /// </summary>
        [Fact]
        public void Every_header_event_is_subscribed_by_MainForm()
        {
            string header = ReadCsStripped("DashboardHeader", "DashboardHeaderControl.cs");
            string mainForm = ReadCsStripped("MainForm.cs");

            var events = Regex.Matches(header, @"public event Action(?:<[^>]+>)? (\w+Requested);")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.True(events.Count >= 13, $"Expected at least 13 header events, extracted {events.Count}: {string.Join(", ", events)}");

            var unsubscribed = events
                .Where(e => !Regex.IsMatch(mainForm, $@"_dashboardHeader\.{e}\s*\+="))
                .ToList();

            Assert.True(
                unsubscribed.Count == 0,
                "DashboardHeaderControl raises event(s) MainForm never subscribes to, so the button "
                + $"does nothing: {string.Join(", ", unsubscribed)}");
        }

        /// <summary>
        /// REMOVAL PROOF (task 4cac608c). "+ New" is gone from the header, and so is the native M menu,
        /// so there is one menu, not a styled one plus a stock one that a code path can still reach.
        /// </summary>
        [Fact]
        public void The_header_has_no_new_terminal_button_and_no_native_menu()
        {
            Assert.DoesNotContain("new_terminal", Header(), StringComparison.Ordinal);
            Assert.DoesNotContain("case \"new_terminal\":", HandleActionBody(), StringComparison.Ordinal);

            string header = ReadCsStripped("DashboardHeader", "DashboardHeaderControl.cs");
            Assert.DoesNotContain("ContextMenuStrip", header, StringComparison.Ordinal);
            Assert.DoesNotContain("NewTerminalRequested", header, StringComparison.Ordinal);
        }
    }
}
