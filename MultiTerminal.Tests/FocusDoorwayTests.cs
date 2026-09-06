using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Focus reaches the app by THREE routes, and the Attention Rail must learn about all of them
    /// (task 8ca83257).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The routes: a card click (the rail's own), a TAB click
    /// (<c>OnActiveDocumentChanged</c>, wired by 42052f0c item 10), and a click into the terminal
    /// BODY (<c>OnTerminalClicked</c>). The third exists as a separate handler precisely because
    /// <c>ActiveDocumentChanged</c> does not fire for a WebView2-hosted terminal — that handler's
    /// own comments say so — and it already updates the focus borders, <c>_lastActiveTerminal</c>
    /// and the header chip directly. The rail was the consumer added last and never joined the
    /// list, so clicking into a terminal moved the green border but not the highlight.
    /// </para>
    /// <para>
    /// This is the recurring shape in this codebase: a hand-maintained set of things to update on
    /// an event, missing its newest member. <c>.claude/rules/checklist-graph.md</c> records two
    /// prior instances (the <c>HudTabContainer.ApplyTheme</c> type chain, and the zoom chain where
    /// six of seven renderers went unwired for months with nothing failing).
    /// </para>
    /// <para>
    /// SOURCE-LEVEL for the same stated reason as <see cref="FocusTargetDisambiguationTests"/>:
    /// these paths need a live WinForms <c>DockContent</c> and a WebView2. Assertions run over
    /// comment-stripped text, because the remarks here and in the implementation name every symbol
    /// involved and a raw Contains() would otherwise be satisfied by prose alone.
    /// </para>
    /// </remarks>
    public class FocusDoorwayTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

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

        private static string BodyOf(string src, string signature, string what)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find {what} (signature: '{signature}').");

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

            Assert.Fail($"{what} was never brace-balanced.");
            return null;
        }

        // ───────────────────────── the third door ─────────────────────────

        /// <summary>
        /// The reported bug. Clicking into a terminal body must tell the rail, in the same handler
        /// that already moves the green border — the border being what proved the event was
        /// detectable all along.
        /// </summary>
        [Fact]
        public void Clicking_into_a_terminal_tells_the_attention_rail()
        {
            string body = BodyOf(ReadStripped("MainForm.cs"), "private void OnTerminalClicked", "OnTerminalClicked");

            Assert.Contains("SyncAttentionPanelFocus", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The border update is the SIBLING of the rail update — they answer the same question
        /// ("which terminal has focus now?") and must not drift apart again. If someone removes the
        /// border loop and leaves the rail call, or the reverse, this says so.
        /// </summary>
        [Fact]
        public void The_border_and_the_rail_are_updated_by_the_same_handler()
        {
            string body = BodyOf(ReadStripped("MainForm.cs"), "private void OnTerminalClicked", "OnTerminalClicked");

            Assert.Contains("SetFocusBorder", body, StringComparison.Ordinal);
            Assert.Contains("SyncAttentionPanelFocus", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The tab-click door must not have been broken while wiring the third one.
        /// </summary>
        [Fact]
        public void The_tab_click_door_is_still_wired()
        {
            string body = BodyOf(ReadStripped("MainForm.cs"), "private void OnActiveDocumentChanged", "OnActiveDocumentChanged");

            Assert.Contains("SyncAttentionPanelFocus", body, StringComparison.Ordinal);
        }

        // ──────────────── focus acknowledges, and only on a change ────────

        /// <summary>
        /// THE LOAD-BEARING GUARD, and the reason it exists is worth more than the assertion.
        /// <para>
        /// Acknowledgement is now driven by the <c>focused</c> message, and every current sender is
        /// an EVENT — a card click, a tab change, a click into a terminal. If someone ever pushes
        /// focus from the periodic refresh instead ("so the view can't drift"), then every refresh
        /// would re-acknowledge the focused terminal's card, and a card that blocks while its
        /// terminal already has focus would be silenced within one refresh tick — permanently, and
        /// silently, because the state would still read "needs permission" while nothing moved.
        /// </para>
        /// <para>
        /// That is the precise failure this whole ticket family exists to prevent: a calm card in
        /// front of a blocked agent. So the periodic refresh must never touch focus.
        /// </para>
        /// </summary>
        [Fact]
        public void The_periodic_refresh_never_pushes_focus()
        {
            string body = BodyOf(ReadStripped("MainForm.cs"), "private void RefreshAttentionPanel", "RefreshAttentionPanel");

            Assert.DoesNotContain("SetFocusedSession", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SyncAttentionPanelFocus", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Focus acknowledges — the second half of the ticket. Looking straight at the terminal
        /// that is asking is a stronger "I have seen it" than clicking its card, so the weaker
        /// gesture should not be the only one that quietens the alarm.
        /// </summary>
        [Fact]
        public void The_focused_message_acknowledges_a_blocking_card()
        {
            string html = ReadStripped("AttentionPanel", "attention-panel.html");
            int at = html.IndexOf("msg.type === \"focused\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find the 'focused' message handler.");

            // The handler runs to the end of the else-if chain; bound the window generously but
            // finitely so a match cannot be satisfied by unrelated code far below.
            string handler = html.Substring(at, Math.Min(900, html.Length - at));

            Assert.Contains("acked[", handler, StringComparison.Ordinal);
            Assert.Contains("isBlocking", handler, StringComparison.Ordinal);
        }

        /// <summary>
        /// POLARITY AND EXPIRY, which "mentions acked[" cannot catch.
        /// <para>
        /// The ack must be keyed by <c>blockId</c>, exactly as <c>activate()</c> keys it. An ack
        /// stored without the block identity never expires, so one focus click would mute that
        /// terminal for the rest of the session — the failure 42052f0c spent four pipeline runs
        /// eliminating, re-entering through a new door.
        /// </para>
        /// </summary>
        [Fact]
        public void The_focus_acknowledgement_is_keyed_to_this_block_so_it_expires()
        {
            string html = ReadStripped("AttentionPanel", "attention-panel.html");
            int at = html.IndexOf("msg.type === \"focused\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find the 'focused' message handler.");

            string handler = html.Substring(at, Math.Min(900, html.Length - at));

            Assert.Contains("block: blockId(", handler, StringComparison.Ordinal);
            Assert.Contains("state:", handler, StringComparison.Ordinal);
        }

        /// <summary>
        /// The ack shape written by focus must match the one written by a card click, because
        /// <c>reconcileAcks</c> expires them by comparing BOTH fields. A focus ack missing either
        /// field would be expired immediately (harmless but useless) or never (a permanent mute).
        /// </summary>
        [Fact]
        public void Focus_and_click_write_the_same_acknowledgement_shape()
        {
            string html = ReadStripped("AttentionPanel", "attention-panel.html");

            var writes = Regex.Matches(html, @"acked\[[^\]]+\]\s*=\s*\{[^}]*\}")
                .Select(m => m.Value)
                .ToList();

            Assert.True(writes.Count >= 2, $"Expected at least two acknowledgement writes (click and focus); found {writes.Count}.");

            foreach (var w in writes)
            {
                Assert.Contains("state:", w, StringComparison.Ordinal);
                Assert.Contains("block:", w, StringComparison.Ordinal);
            }
        }
    }
}
