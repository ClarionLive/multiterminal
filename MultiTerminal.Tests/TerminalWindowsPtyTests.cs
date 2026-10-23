using System;
using System.IO;
using System.Runtime.CompilerServices;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// xterm.js's <c>windowsPty</c> option (GitHub #51, task 9d961086): the host puts the Windows build
    /// number in the terminal page's URL and terminal.html hands it to xterm as
    /// <c>{ backend: 'conpty', buildNumber }</c>.
    ///
    /// <para>WHAT IS NOT COVERED: the WebView2 page is not run, so that xterm actually receives the option
    /// and rewraps better on resize needs the running app. The page facts are source-level, scoped to
    /// <c>readWindowsPty</c>'s body (which holds no comments) and to <c>initTerminal</c>.</para>
    /// </summary>
    public sealed class TerminalWindowsPtyTests
    {
        [Fact]
        public void The_url_carries_the_cache_bust_and_the_windows_build()
            => Assert.Equal(
                "file:///C:/x/terminal.html?v=42&winbuild=26200",
                WebViewTerminalRenderer.BuildTerminalPageUri("file:///C:/x/terminal.html", 42, 26200));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_unknown_build_is_left_out(int build)
            => Assert.Equal(
                "file:///C:/x/terminal.html?v=42",
                WebViewTerminalRenderer.BuildTerminalPageUri("file:///C:/x/terminal.html", 42, build));

        [Fact]
        public void The_page_reads_the_parameter_the_host_writes_and_builds_the_conpty_option()
        {
            string body = FunctionBody(ReadTerminalHtml(), "function readWindowsPty(");

            Assert.Contains($".get('{WebViewTerminalRenderer.WindowsBuildParam}')", body, StringComparison.Ordinal);
            Assert.Contains("backend: 'conpty'", body, StringComparison.Ordinal);
            Assert.Contains("buildNumber: build", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_terminal_is_created_with_the_option()
        {
            string init = FunctionBody(ReadTerminalHtml(), "function initTerminal(");

            int read = init.IndexOf("readWindowsPty()", StringComparison.Ordinal);
            int set = init.IndexOf("options.windowsPty = windowsPty", StringComparison.Ordinal);
            int create = init.IndexOf("new Terminal(options)", StringComparison.Ordinal);
            Assert.True(read >= 0 && set > read && create > set,
                "initTerminal must read the option and set it on the options it creates the terminal with.");
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
