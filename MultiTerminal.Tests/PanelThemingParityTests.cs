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
    /// Task 3a29b367 — the two panel-theming lists must not drift apart.
    ///
    /// <para>WHY THIS FILE EXISTS. The owner switched the app to light and every panel followed
    /// except Chat, which stayed dark. The cause was not in the Chat panel at all — its
    /// <c>ApplyTheme</c>, its <c>PostWebMessageAsString</c>, its <c>handleStringMessage</c> and its
    /// <c>body.light</c> CSS were all correct. <c>MainForm</c> simply has TWO methods that theme
    /// panels, containing DIFFERENT hand-written lists:</para>
    ///
    /// <list type="bullet">
    /// <item><description><c>ApplyTheme()</c> — what <c>ToggleTheme()</c> calls, i.e. the live theme
    /// switch. It did not mention <c>_chatPanel</c> or <c>_filePreviewPanel</c>.</description></item>
    /// <item><description><c>ApplyThemesToPanels()</c> — only reached from the XML layout-restore
    /// path. It did.</description></item>
    /// </list>
    ///
    /// <para>So Chat was themed when it opened and when a layout restored, and never when the user
    /// toggled. Nothing failed; the two lists just quietly disagreed.</para>
    ///
    /// <para>This is the THIRD time this exact shape has bitten this codebase — after the
    /// <c>HudTabContainer.ApplyTheme</c> type-name chain, and the zoom chain where six of seven
    /// renderers went unwired for months with nothing detecting it. So the guard is deliberately
    /// NOT "assert Chat is in the list": a test naming Chat would be written by the same person who
    /// forgot Chat, from the same wrong model. Instead the two lists are made to check EACH OTHER,
    /// which needs no hardcoded roster of its own and therefore covers panel eleven for free.</para>
    ///
    /// <para>WHAT IS NOT COVERED, plainly: this is a source-level comparison of two method bodies. It
    /// proves the lists agree, not that a panel visually repaints. That still needs the running app.
    /// It also cannot see a panel missing from BOTH lists — for that, the panel would have to be
    /// themed somewhere a person can see, which is the failure the owner would report anyway.</para>
    /// </summary>
    public class PanelThemingParityTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        /// <summary>Strips block comments and whole-line // comments, so prose cannot satisfy a check.</summary>
        private static string MainFormSource()
        {
            string path = Path.Combine(RepoRoot(), "MainForm.cs");
            Assert.True(File.Exists(path), $"Could not locate MainForm.cs at '{path}'.");

            string src = File.ReadAllText(path);
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

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

        /// <summary>
        /// The panel fields a method body actually applies a theme to. Matches both dialects present
        /// in MainForm — <c>ApplyTheme(bool)</c> and <c>SetTheme(...)</c> — because the panels do not
        /// agree on a name and a check that knew only one would silently under-report.
        /// </summary>
        private static HashSet<string> ThemedPanelsIn(string body)
            => Regex.Matches(body, @"(_[a-zA-Z0-9]*[Pp]anel)\s*(?:\?)?\.\s*(?:ApplyTheme|SetTheme)\s*\(")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// THE GUARD. Every panel the restore path themes must also be themed by the live toggle.
        /// <para>
        /// These two lists describe the same thing — "the panels that need telling about a theme" —
        /// and any panel in one but not the other is a panel that themes correctly in one situation
        /// and not the other. That is precisely the reported bug: Chat was right on restore and wrong
        /// on toggle, which is also why it looked intermittent rather than broken.
        /// </para>
        /// If this goes red, add the named panel to <c>ApplyTheme()</c> — do not relax the test.
        /// </summary>
        [Fact]
        public void The_live_theme_toggle_themes_every_panel_the_restore_path_does()
        {
            string src = MainFormSource();

            var onRestore = ThemedPanelsIn(BodyOf(src, "private void ApplyThemesToPanels()", "ApplyThemesToPanels"));
            var onToggle = ThemedPanelsIn(BodyOf(src, "private void ApplyTheme(bool isInitialLoad", "ApplyTheme"));

            Assert.True(onRestore.Count > 0, "Parsed ApplyThemesToPanels but found no themed panels — the regex has rotted.");
            Assert.True(onToggle.Count > 0, "Parsed ApplyTheme but found no themed panels — the regex has rotted.");

            var missing = onRestore.Except(onToggle).OrderBy(p => p, StringComparer.Ordinal).ToList();

            Assert.True(
                missing.Count == 0,
                "These panels are themed when a layout is restored but NOT when the user toggles the "
                + "theme, so they will keep the old theme while every other panel changes: "
                + string.Join(", ", missing));
        }

        /// <summary>
        /// The reverse direction. A panel themed only by the live toggle comes up wrong after a
        /// restart, which is a first-impression bug rather than an intermittent one — different
        /// symptom, same root cause, and equally invisible to everything else.
        /// </summary>
        [Fact]
        public void The_restore_path_themes_every_panel_the_live_toggle_does()
        {
            string src = MainFormSource();

            var onRestore = ThemedPanelsIn(BodyOf(src, "private void ApplyThemesToPanels()", "ApplyThemesToPanels"));
            var onToggle = ThemedPanelsIn(BodyOf(src, "private void ApplyTheme(bool isInitialLoad", "ApplyTheme"));

            var missing = onToggle.Except(onRestore).OrderBy(p => p, StringComparer.Ordinal).ToList();

            Assert.True(
                missing.Count == 0,
                "These panels are themed by the live toggle but NOT when a layout is restored, so "
                + "they will come up in the wrong theme after a restart: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The Chat panel's own light palette must actually cover its controls. <c>.pause-btn</c>
        /// hardcoded dark colours with no <c>body.light</c> override, so it stayed dark-styled on a
        /// light background — a defect that survives the theming fix entirely, because the message
        /// arrives correctly and the CSS then has nothing to say.
        /// </summary>
        [Fact]
        public void The_chat_panels_pause_button_has_a_light_variant()
        {
            string path = Path.Combine(RepoRoot(), "ChatPanel", "chat-panel.html");
            Assert.True(File.Exists(path), $"Could not locate chat-panel.html at '{path}'.");

            string css = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", " ", RegexOptions.Singleline);

            Assert.Contains("body.light .pause-btn", css, StringComparison.Ordinal);
        }
    }
}
