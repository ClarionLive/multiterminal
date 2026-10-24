using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// xterm.js's WebGL renderer in terminal.html (GitHub #51 part 2, task 1862a4d8): loaded with the
    /// addon release that pairs with the page's xterm, attached only after the terminal is opened, and
    /// dropped (back to the DOM renderer) when the GPU context is lost.
    ///
    /// <para>WHAT IS NOT COVERED: the WebView2 page is not run, so whether WebGL actually starts, renders
    /// correctly and falls back on a real machine needs the running app; the page logs which renderer each
    /// pane ended up on ("renderer: webgl" / "renderer: dom ...") to the debug log for that. The facts below
    /// are source-level, scoped to <c>enableWebglRenderer</c> (whose body holds no comments) and
    /// <c>initTerminal</c> (which does; its comments do not contain the two call texts the ordering fact
    /// looks for, so a comment cannot satisfy it today).</para>
    /// </summary>
    public sealed class TerminalWebglRendererTests
    {
        [Fact]
        public void The_webgl_addon_is_the_release_that_pairs_with_the_pages_xterm()
        {
            string html = ReadTerminalHtml();

            // @xterm/xterm 6.0.0 and @xterm/addon-webgl 0.19.0 were published together (2025-12-22);
            // 0.18.x is the xterm 5 line.
            Assert.Contains("@xterm/xterm@6.0.0/lib/xterm.min.js", html, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(html, @"<script src=""[^""]*@xterm/addon-webgl@0\.19\.0/lib/addon-webgl\.min\.js""></script>"));

            int xterm = html.IndexOf("@xterm/xterm@6.0.0/lib/xterm.min.js", StringComparison.Ordinal);
            int webgl = html.IndexOf("@xterm/addon-webgl@0.19.0", StringComparison.Ordinal);
            Assert.True(webgl > xterm, "The addon script must load after xterm itself.");
        }

        [Fact]
        public void A_lost_context_disposes_the_addon_and_says_so()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function enableWebglRenderer(");

            Assert.Contains("new WebglAddon.WebglAddon()", body, StringComparison.Ordinal);
            Assert.Contains("addon.onContextLoss(", body, StringComparison.Ordinal);
            Assert.Contains("addon.dispose()", body, StringComparison.Ordinal);
            Assert.Contains("'renderer: webgl'", body, StringComparison.Ordinal);
            Assert.Contains("typeof WebglAddon === 'undefined'", body, StringComparison.Ordinal);
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
            Assert.DoesNotContain("fitAddon.fit()", perFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("setFontSize(", perFrame, StringComparison.Ordinal);
            Assert.DoesNotContain("sendToHost(", perFrame, StringComparison.Ordinal);
        }

        private static string FunctionBody(string src, string anchor)
        {
            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{anchor}' is gone from terminal.html.");
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                {
                    string body = src.Substring(open, i - open + 1);
                    Assert.True(body.Length > 100, $"The body after '{anchor}' was not extracted.");
                    return body;
                }
            }

            Assert.Fail($"Unbalanced braces after '{anchor}'.");
            return string.Empty;
        }

        private static string ReadTerminalHtml() => File.ReadAllText(RepoPath("Terminal", "terminal.html"));

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
