using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Result of a single git subprocess invocation.
    /// <para><see cref="TimedOut"/> is DISTINGUISHABLE from a real git failure:
    /// on timeout the process tree is killed and the result is
    /// <c>(ExitCode: -1, TimedOut: true)</c>; a real non-zero-exit failure is
    /// <c>(ExitCode != 0, TimedOut: false)</c>. Callers that reconcile on-disk
    /// state from git output (notably <see cref="WorktreeJanitorService"/>) MUST
    /// treat <see cref="TimedOut"/> as "retry next sweep", NOT as evidence that a
    /// worktree/branch is gone — a wedged git that returns no output is not the
    /// same as git authoritatively reporting an empty set.</para>
    /// </summary>
    public readonly record struct GitResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        /// <summary>
        /// Backwards-compatible 3-value deconstruction so the many existing
        /// <c>var (exitCode, stdout, stderr) = await GitExec.RunAsync(...)</c>
        /// call sites compile unchanged after the five private RunGitAsync
        /// copies were folded into this helper. Timeout-aware callers read
        /// <see cref="TimedOut"/> off the struct instead of deconstructing.
        /// </summary>
        public void Deconstruct(out int exitCode, out string stdout, out string stderr)
        {
            exitCode = ExitCode;
            stdout = Stdout;
            stderr = Stderr;
        }
    }

    /// <summary>
    /// Single shared helper for invoking <c>git</c> as a subprocess with a
    /// per-call timeout and process-tree kill. Replaces five byte-identical
    /// private <c>RunGitAsync</c> copies that previously lived in
    /// WorktreeManager, WorktreeJanitorService, WorktreeMergeService,
    /// WorktreeListService and WorktreeAutoCommitService — four of which had NO
    /// timeout at all (a wedged git hung the caller forever), and one of which
    /// (List) had a timeout but collapsed timeout and failure into the same
    /// <c>-1</c> sentinel.
    /// <para><c>GIT_TERMINAL_PROMPT=0</c> is set so a credential/interactive
    /// prompt fails fast instead of blocking the subprocess (and its caller)
    /// indefinitely — the same wedge the timeout guards against, closed at the
    /// source.</para>
    /// </summary>
    public static class GitExec
    {
        /// <summary>Default per-call timeout. Generous for typical local git
        /// operations; genuinely slow sites (e.g. <c>worktree add</c>, which
        /// materializes a working tree) pass a larger explicit value.</summary>
        public const int DefaultTimeoutMs = 30000;

        /// <summary>Timeout for genuinely slow operations: those that materialize a
        /// working tree (<c>git worktree add</c>) OR mutate repository state
        /// (<c>git merge</c>, <c>git commit</c>). These can legitimately exceed the
        /// default on a large repository, and a false timeout is worse than for a
        /// read op — a killed mutation can leave the checkout in an indeterminate
        /// state — so these sites pass this larger budget explicitly and branch on
        /// <see cref="GitResult.TimedOut"/> rather than treating a timeout as a
        /// genuine failure (e.g. a merge conflict).</summary>
        public const int SlowOpTimeoutMs = 120000;

        /// <summary>
        /// Run git with the <see cref="DefaultTimeoutMs"/> timeout.
        /// </summary>
        public static Task<GitResult> RunAsync(string workingDir, params string[] args)
            => RunProcessAsync("git", workingDir, DefaultTimeoutMs, args);

        /// <summary>
        /// Run git with an explicit per-call timeout (milliseconds). On timeout
        /// the process tree is killed and a <see cref="GitResult.TimedOut"/>
        /// result is returned — distinct from a real failure's non-zero exit.
        /// </summary>
        public static Task<GitResult> RunAsync(string workingDir, int timeoutMs, params string[] args)
            => RunProcessAsync("git", workingDir, timeoutMs, args);

        /// <summary>
        /// Core process invocation. The executable name is a parameter ONLY so
        /// tests (via InternalsVisibleTo) can point it at a controllable process —
        /// a sleeper to force a timeout, a fixed-exit-code command to force a real
        /// failure — and assert the two outcomes are DISTINGUISHABLE. Production
        /// call paths always pass "git".
        /// </summary>
        internal static async Task<GitResult> RunProcessAsync(string fileName, string workingDir, int timeoutMs, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            // Never block on an interactive credential prompt — fail fast.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var exitTask = proc.WaitForExitAsync();

                var winner = await Task.WhenAny(exitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (winner != exitTask)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }

                    // Confirm the tree actually died within a bounded window rather
                    // than reporting "timed out and handled" before the kill lands.
                    // If the process cannot be confirmed dead (Kill failed, partial
                    // tree, detached helper children), log it — a surviving process
                    // keeps consuming resources the caller believes are released.
                    try
                    {
                        if (await Task.WhenAny(exitTask, Task.Delay(2000)).ConfigureAwait(false) != exitTask)
                        {
                            Debug.WriteLine(
                                $"[GitExec] timeout kill for '{fileName}' not confirmed dead within 2s — process tree may survive");
                        }
                    }
                    catch { }

                    // Distinguishable timeout — NOT a synthetic failure. The -1
                    // exit code matches the failure sentinel on purpose; callers
                    // that must tell the two apart branch on TimedOut, not exit.
                    return new GitResult(-1, string.Empty, string.Empty, TimedOut: true);
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                return new GitResult(proc.ExitCode, stdout, stderr, TimedOut: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GitExec] git invocation failed: {ex.Message}");
                return new GitResult(-1, string.Empty, string.Empty, TimedOut: false);
            }
        }
    }
}
