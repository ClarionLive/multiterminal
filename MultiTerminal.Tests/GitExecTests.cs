using System.IO;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Falsifiable coverage for <see cref="GitExec"/> — the single shared git
    /// subprocess helper that replaced five private RunGitAsync copies (Eval P5
    /// repair C). The load-bearing invariant this ticket exists to establish is
    /// that a TIMEOUT is DISTINGUISHABLE from a real command failure: the
    /// worktree janitor must treat a wedged/timed-out git as "retry next sweep",
    /// not as authoritative evidence that a worktree/branch is gone.
    ///
    /// <para>These tests drive the internal <see cref="GitExec.RunProcessAsync"/>
    /// seam with controllable Windows commands rather than trying to make real
    /// git hang, so the timeout fires deterministically. The runner is
    /// windows-latest (the app is WinForms/net8.0-windows), so <c>ping</c> and
    /// <c>cmd</c> are always present.</para>
    /// </summary>
    public sealed class GitExecTests
    {
        private static readonly string WorkDir = Path.GetTempPath();

        [Fact]
        public async Task Timeout_IsDistinguishable_FromSuccessAndFailure()
        {
            // A process that outlives the timeout: `ping -n 6 127.0.0.1` runs ~5s.
            // With a 200ms budget the timeout fires first, the process tree is
            // killed, and the result is the DISTINCT timeout shape.
            var timedOut = await GitExec.RunProcessAsync("ping", WorkDir, 200, "-n", "6", "127.0.0.1");

            Assert.True(timedOut.TimedOut, "a process that outlived the timeout must report TimedOut=true");
            Assert.Equal(-1, timedOut.ExitCode);

            // A real command failure: `cmd /c exit 3` returns promptly with a
            // non-zero exit and is NOT a timeout.
            var failed = await GitExec.RunProcessAsync("cmd", WorkDir, 30000, "/c", "exit 3");

            Assert.False(failed.TimedOut, "a prompt non-zero exit is a failure, NOT a timeout");
            Assert.Equal(3, failed.ExitCode);

            // The whole point: a caller CANNOT tell timeout from failure by exit
            // code alone (both are "unsuccessful"), but TimedOut cleanly separates
            // them. This is the property WorktreeJanitorService branches on to
            // choose retry-later over stranded-worktree deletion.
            Assert.NotEqual(timedOut.TimedOut, failed.TimedOut);
        }

        [Fact]
        public async Task Success_ReportsZeroExit_NoTimeout_AndCapturesStdout()
        {
            var ok = await GitExec.RunProcessAsync("cmd", WorkDir, 30000, "/c", "echo", "hello");

            Assert.False(ok.TimedOut);
            Assert.Equal(0, ok.ExitCode);
            Assert.Contains("hello", ok.Stdout);
        }

        [Fact]
        public async Task GitResult_DeconstructsToThreeValues_ForBackCompatCallSites()
        {
            // The ~50 existing `var (exitCode, stdout, stderr) = await ...` sites
            // rely on the 3-value Deconstruct still compiling and matching the
            // struct's fields.
            var (exitCode, stdout, _) = await GitExec.RunProcessAsync("cmd", WorkDir, 30000, "/c", "echo", "hi");

            Assert.Equal(0, exitCode);
            Assert.Contains("hi", stdout);
        }
    }
}
