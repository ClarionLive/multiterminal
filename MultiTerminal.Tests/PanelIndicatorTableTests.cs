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
    /// Task f2e32f6c — does the icon for an OPEN panel actually light up?
    ///
    /// <para>WHY THIS FILE EXISTS. <c>DashboardHeaderControl.UpdatePanelState</c> existed, was
    /// correct, had a complete and working view half — <c>updatePanelState</c> in
    /// <c>dashboard.html</c>, the <c>.icon-btn.active</c> style, matching <c>btn-{key}</c> ids for
    /// all ten panels — and had <b>zero callers</b>. So no panel icon in the header had ever
    /// highlighted, for any panel, ever. Nothing failed; there was simply nothing to fail.</para>
    ///
    /// <para>The fix routes every panel through one table (<c>MainForm.PanelIndicators</c>) and one
    /// method (<c>SyncPanelIndicators</c>) rather than ten hand-written call sites. These tests guard
    /// the table, because a table with a missing row fails exactly as silently as the missing calls
    /// did.</para>
    ///
    /// <para>DELIBERATELY GENERAL, not per-panel. A test naming <c>attention</c> would have been
    /// written by the same person who forgot the callers, in the same moment, from the same wrong
    /// model — the identical reasoning recorded in <see cref="DashboardHeaderDoorwayTests"/>. Both
    /// assertions below hold for EVERY panel, so panel eleven is covered before anyone thinks to
    /// cover it.</para>
    ///
    /// <para>WHAT IS NOT COVERED, plainly: this is WinForms + WebView2, so nothing here can be
    /// instantiated and no assertion here proves an icon lights up on screen. These are source-level
    /// checks that the table and the toolbar agree in both directions. Whether the pixel changes
    /// still needs the deployed app.</para>
    /// </summary>
    public class PanelIndicatorTableTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string PathTo(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return path;
        }

        private static string ReadCsStripped(params string[] relativeParts)
        {
            string src = File.ReadAllText(PathTo(relativeParts));
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string ReadHtmlStripped(params string[] relativeParts)
        {
            string src = File.ReadAllText(PathTo(relativeParts));
            string noHtml = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            return Regex.Replace(noHtml, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        }

        private static string MainForm() => ReadCsStripped("MainForm.cs");

        private static string Header() => ReadHtmlStripped("DashboardHeader", "dashboard.html");

        /// <summary>
        /// The keys in <c>PanelIndicators</c>, read out of the array initializer rather than by
        /// reflection — the field is private, and a source-level read also catches a row that was
        /// commented out rather than deleted.
        /// </summary>
        private static HashSet<string> TableKeys()
        {
            string src = MainForm();
            int start = src.IndexOf("PanelIndicators =", StringComparison.Ordinal);
            Assert.True(start >= 0, "Could not find the PanelIndicators table in MainForm.cs.");

            int open = src.IndexOf('{', start);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            Assert.True(open >= 0 && close > open, "PanelIndicators table initializer is malformed.");

            string body = src.Substring(open, close - open);
            var keys = Regex.Matches(body, @"\(\s*""([a-zA-Z0-9_]+)""\s*,")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(keys.Count > 0, "PanelIndicators table parsed but yielded no keys.");
            return keys;
        }

        /// <summary>
        /// The panel keys MainForm actually dispatches on. This is the authoritative list of "things
        /// that are panels" — <c>btn-history</c> sends <c>toggle_history</c> and is NOT a panel, so
        /// deriving the list from the dispatch rather than from the buttons keeps it out without a
        /// hardcoded exception that would rot.
        /// </summary>
        private static HashSet<string> DispatchedPanelKeys()
        {
            string src = MainForm();
            int start = src.IndexOf("TogglePanelRequested += (panel) =>", StringComparison.Ordinal);
            Assert.True(start >= 0, "Could not find MainForm's TogglePanelRequested handler.");

            int open = src.IndexOf('{', start);
            int depth = 0;
            int end = -1;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) { end = i; break; }
            }

            Assert.True(end > open, "Unbalanced braces in MainForm's TogglePanelRequested handler.");

            string body = src.Substring(open, end - open + 1);
            var keys = Regex.Matches(body, @"case\s+""([a-zA-Z0-9_]+)""\s*:")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(keys.Count > 0, "TogglePanelRequested handler parsed but yielded no cases.");
            return keys;
        }

        /// <summary>
        /// Every panel that can be toggled must have a row in the indicator table.
        /// <para>
        /// This is the assertion that catches the next panel. Someone adds panel eleven, wires its
        /// button and its dispatch case (both of which they will notice, because otherwise the panel
        /// does not open), and forgets the indicator row — which they will NOT notice, because a
        /// missing row produces no error, no warning, and a panel that works perfectly except that
        /// its icon never lights. That is precisely how all ten got into this state.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_toggleable_panel_has_a_row_in_the_indicator_table()
        {
            var dispatched = DispatchedPanelKeys();
            var table = TableKeys();

            var missing = dispatched.Except(table).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(
                missing.Count == 0,
                "These panels can be toggled but have no row in MainForm.PanelIndicators, so their "
                + "header icon will never light up: " + string.Join(", ", missing));
        }

        /// <summary>
        /// And the reverse. A row whose key has no <c>btn-{key}</c> in the markup is a silent no-op:
        /// <c>updatePanelState</c> does <c>getElementById('btn-' + panel)</c> and simply returns when
        /// the element is missing. No error, no log — the row looks wired and does nothing.
        /// </summary>
        [Fact]
        public void Every_indicator_row_names_a_button_that_exists_in_the_markup()
        {
            string header = Header();
            var orphans = TableKeys()
                .Where(k => !header.Contains($"id=\"btn-{k}\"", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                orphans.Count == 0,
                "These indicator rows name a button id that does not exist in dashboard.html, so "
                + "updatePanelState silently does nothing for them: " + string.Join(", ", orphans));
        }

        /// <summary>
        /// The table is worthless if nothing calls the method that reads it — which is the exact
        /// defect this ticket fixes, one level up. <c>UpdatePanelState</c> had a complete
        /// implementation and zero callers for the entire life of the header.
        /// <para>
        /// Both call sites matter and they fail differently: the dispatch keeps the icons honest as
        /// the user works, and the restore path decides what the user sees in the first second after
        /// launch — a panel restored open with a dark icon.
        /// </para>
        /// </summary>
        [Fact]
        public void SyncPanelIndicators_is_called_from_both_the_toggle_dispatch_and_the_restore_path()
        {
            string src = MainForm();

            int definitions = Regex.Matches(src, @"private void SyncPanelIndicators\(\)").Count;
            Assert.True(definitions == 1, $"Expected exactly one SyncPanelIndicators definition, found {definitions}.");

            // Counting call sites is NOT enough, and this test originally did exactly that and was
            // caught by its own falsification: with three call sites present, deleting one still
            // satisfied "at least two". Each required site is therefore named and checked inside its
            // own method body, because the two fail DIFFERENTLY and one covering for the other is
            // precisely the confusion worth preventing.
            Assert.Contains(
                "SyncPanelIndicators();",
                BodyOf(src, "TogglePanelRequested += (panel) =>", "the TogglePanelRequested dispatch"));

            Assert.Contains(
                "SyncPanelIndicators();",
                BodyOf(src, "private void RestorePanelStates()", "RestorePanelStates"));
        }

        /// <summary>Extracts a brace-balanced body starting at the first match of an anchor.</summary>
        private static string BodyOf(string src, string anchor, string what)
        {
            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find {what} (anchor: '{anchor}').");

            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, $"Found {what} but no opening brace followed it.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces while extracting {what}.");
            return string.Empty;
        }
    }
}
