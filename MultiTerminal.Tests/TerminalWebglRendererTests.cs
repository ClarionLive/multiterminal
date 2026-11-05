using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// xterm.js's WebGL renderer in terminal.html (GitHub #51 part 2, task 1862a4d8): loaded with the
    /// addon release that pairs with the page's xterm, attached only after the terminal is opened,
    /// disposed (back to the DOM renderer) when it cannot start or its GPU context is lost, and kept off
    /// entirely by the host's <c>MULTITERMINAL_TERMINAL_WEBGL</c> switch.
    ///
    /// <para>WHAT IS NOT COVERED: the WebView2 page is not run, so whether WebGL actually starts, renders
    /// correctly and falls back on a real machine needs the running app; the page logs which renderer each
    /// pane ended up on ("renderer: webgl" / "renderer: dom ...") to the debug log for that. The page facts
    /// are source-level, scoped to one function or callback each, and read comment-stripped text (block
    /// comments and whole-line // comments), so explanatory prose can neither satisfy nor trip them.</para>
    /// </summary>
    public sealed class TerminalWebglRendererTests
    {
        [Fact]
        public void The_webgl_addon_is_the_release_that_pairs_with_the_pages_xterm()
        {
            string html = ReadTerminalHtml();

            // @xterm/xterm 6.0.0 and @xterm/addon-webgl 0.19.0 were built from the same commit and published
            // together (2025-12-22); 0.18.x is the xterm 5 line.
            Assert.Contains("@xterm/xterm@6.0.0/lib/xterm.min.js", html, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(html, @"<script src=""[^""]*@xterm/addon-webgl@0\.19\.0/lib/addon-webgl\.min\.js""></script>"));

            int xterm = html.IndexOf("@xterm/xterm@6.0.0/lib/xterm.min.js", StringComparison.Ordinal);
            int webgl = html.IndexOf("@xterm/addon-webgl@0.19.0", StringComparison.Ordinal);
            Assert.True(webgl > xterm, "The addon script must load after xterm itself.");
        }

        [Fact]
        public void A_lost_context_disposes_the_addon_and_logs_it()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function enableWebglRenderer(");
            string onLoss = FunctionBody(body, "addon.onContextLoss(", minLength: 60);

            Assert.Contains("addon.dispose()", onLoss, StringComparison.Ordinal);
            Assert.Contains("'renderer: webgl context lost", onLoss, StringComparison.Ordinal);
        }

        [Fact]
        public void A_renderer_that_cannot_start_is_disposed_and_logged()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function enableWebglRenderer(");

            int tryAt = body.IndexOf("try {", StringComparison.Ordinal);
            int load = body.IndexOf("term.loadAddon(addon)", StringComparison.Ordinal);
            int catchAt = body.IndexOf("} catch (e) {", load < 0 ? 0 : load, StringComparison.Ordinal);
            Assert.True(tryAt >= 0 && load > tryAt && catchAt > load, "loadAddon must run inside the try that falls back to the DOM renderer.");

            string onFailure = FunctionBody(body.Substring(catchAt), "catch (e)", minLength: 60);
            Assert.Contains("addon.dispose()", onFailure, StringComparison.Ordinal);
            Assert.Contains("'renderer: dom (webgl unavailable: '", onFailure, StringComparison.Ordinal);
        }

        [Fact]
        public void A_missing_addon_script_keeps_the_dom_renderer()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function enableWebglRenderer(");

            int check = body.IndexOf("typeof WebglAddon === 'undefined'", StringComparison.Ordinal);
            int create = body.IndexOf("new WebglAddon.WebglAddon()", StringComparison.Ordinal);
            Assert.True(check >= 0 && create > check, "The script-missing check must come before the addon is created.");
        }

        [Fact]
        public void The_renderer_is_enabled_after_the_terminal_is_opened()
        {
            string init = FunctionBody(ReadTerminalHtml(), "function initTerminal(");

            int open = init.IndexOf("term.open(container)", StringComparison.Ordinal);
            int enable = init.IndexOf("enableWebglRenderer()", StringComparison.Ordinal);
            Assert.True(open >= 0, "initTerminal no longer opens the terminal.");
            Assert.True(enable > open, "The WebGL addon must be loaded after term.open(): it attaches to an opened terminal only.");
        }

        // ---- The off switch: MULTITERMINAL_TERMINAL_WEBGL -> &webgl=0 -> the page skips the addon ----

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("OFF")]
        [InlineData(" no ")]
        [InlineData("Disabled")]
        public void Off_values_turn_webgl_off(string raw)
            => Assert.True(WebViewTerminalRenderer.IsWebglTurnedOff(raw));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("on")]
        [InlineData("offf")]
        public void Anything_else_leaves_webgl_on(string raw)
            => Assert.False(WebViewTerminalRenderer.IsWebglTurnedOff(raw));

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("off", true)]
        [InlineData("On", true)]
        [InlineData(" enabled ", true)]
        [InlineData("offf", false)]
        [InlineData("2", false)]
        public void Only_unrecognized_values_are_reported(string raw, bool recognized)
            => Assert.Equal(recognized, WebViewTerminalRenderer.IsWebglSwitchValueRecognized(raw));

        [Fact]
        public void The_url_carries_the_off_switch_only_when_set()
        {
            Assert.Equal(
                "file:///C:/x/terminal.html?v=42&winbuild=26200&webgl=0",
                WebViewTerminalRenderer.BuildTerminalPageUri("file:///C:/x/terminal.html", 42, 26200, webglOff: true));
            Assert.Equal(
                "file:///C:/x/terminal.html?v=42&winbuild=26200",
                WebViewTerminalRenderer.BuildTerminalPageUri("file:///C:/x/terminal.html", 42, 26200, webglOff: false));
        }

        [Fact]
        public void The_page_reads_the_parameter_the_host_writes_and_skips_the_addon()
        {
            string html = ReadTerminalHtml();
            string reader = FunctionBody(html, "function webglTurnedOff(", minLength: 60);
            Assert.Contains($".get('{WebViewTerminalRenderer.WebglParam}') === '0'", reader, StringComparison.Ordinal);

            string body = FunctionBody(html, "function enableWebglRenderer(");
            int check = body.IndexOf("if (webglTurnedOff())", StringComparison.Ordinal);
            int create = body.IndexOf("new WebglAddon.WebglAddon()", StringComparison.Ordinal);
            Assert.True(check >= 0 && create > check, "The off switch must be checked before the addon is created.");
            Assert.Contains(WebViewTerminalRenderer.WebglEnvVar, body, StringComparison.Ordinal);
        }

        // ---- Ctrl+wheel zoom is coalesced (Owner live test 2026-10-24: zooming felt flickery) -------

        [Fact]
        public void Wheel_zoom_draws_once_per_frame_and_fits_and_saves_once_when_the_wheel_stops()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function scheduleWheelZoom(");

            int frame = body.IndexOf("requestAnimationFrame(", StringComparison.Ordinal);
            int settle = body.IndexOf("setTimeout(", StringComparison.Ordinal);
            Assert.True(frame >= 0 && settle > frame, "The size should be drawn per frame, then settled on a timer.");
            Assert.Contains("clearTimeout(wheelZoomSettleTimer)", body, StringComparison.Ordinal);

            // The fit (inside setFontSize) and the save run only in the settle callback, after the timer.
            string settled = body.Substring(settle);
            Assert.Contains("setFontSize(size)", settled, StringComparison.Ordinal);
            Assert.Contains("type: 'fontSizeChanged'", settled, StringComparison.Ordinal);
            string perFrame = body.Substring(0, settle);
            Assert.DoesNotContain("setFontSize(", perFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("sendToHost(", perFrame, StringComparison.Ordinal);
        }

        [Fact]
        public void A_size_set_by_the_host_cancels_a_wheel_zoom_still_settling()
        {
            string html = ReadTerminalHtml();

            string cancel = FunctionBody(html, "function cancelWheelZoom(", minLength: 60);
            Assert.Contains("pendingWheelFontSize = null", cancel, StringComparison.Ordinal);
            Assert.Contains("clearTimeout(wheelZoomSettleTimer)", cancel, StringComparison.Ordinal);
            Assert.Contains("cancelAnimationFrame(wheelZoomFrame)", cancel, StringComparison.Ordinal);

            int hostCase = html.IndexOf("case 'fontSize':", StringComparison.Ordinal);
            Assert.True(hostCase >= 0, "The host's fontSize message case is gone.");
            int brk = html.IndexOf("break;", hostCase, StringComparison.Ordinal);
            string caseBody = html.Substring(hostCase, brk - hostCase);
            int cancelAt = caseBody.IndexOf("cancelWheelZoom()", StringComparison.Ordinal);
            int setAt = caseBody.IndexOf("setFontSize(", StringComparison.Ordinal);
            Assert.True(cancelAt >= 0 && setAt > cancelAt, "The host's size must cancel the pending wheel zoom before it is applied.");
        }

        /// <summary>
        /// Strips block comments and whole-line // comments (the same rule as BoardHudDoorwayTests):
        /// whole-line keeps "https://" inside string literals intact.
        /// </summary>
        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var kept = noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", kept);
        }

        private static string FunctionBody(string src, string anchor, int minLength = 100)
        {
            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{anchor}' is gone from terminal.html.");
            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, $"No body follows '{anchor}'.");
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                {
                    string body = src.Substring(open, i - open + 1);
                    Assert.True(body.Length >= minLength, $"The body after '{anchor}' was not extracted.");
                    return body;
                }
            }

            Assert.Fail($"Unbalanced braces after '{anchor}'.");
            return string.Empty;
        }

        private static string ReadTerminalHtml() => StripComments(File.ReadAllText(RepoPath("Terminal", "terminal.html")));

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
