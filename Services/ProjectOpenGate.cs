using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Which projects a REST <c>POST /api/projects/{id}/open</c> (PR #103) is launching right now (review of
    /// PR #103, task ba0f0ee0). The launch is posted and can sit in MT's modal questions for as long as the
    /// Owner takes; a second request for the same project in that time used to post a second launch onto the
    /// same Home tab (two session dialogs, StartTerminal twice on one pane). With the gate the second request
    /// joins the first and answers "launching" without posting anything. Thread-safe.
    /// </summary>
    public sealed class ProjectOpenGate
    {
        private readonly HashSet<string> _launching = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        /// <summary>
        /// Claims <paramref name="projectId"/> for a launch. False when a launch of it is already under way:
        /// the caller must not start another.
        /// </summary>
        public bool TryBegin(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
                return false;
            lock (_lock)
                return _launching.Add(projectId);
        }

        /// <summary>Releases <paramref name="projectId"/> once its launch has finished, been cancelled or failed.</summary>
        public void End(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
                return;
            lock (_lock)
                _launching.Remove(projectId);
        }

        /// <summary>Whether a launch of <paramref name="projectId"/> is under way.</summary>
        public bool IsLaunching(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
                return false;
            lock (_lock)
                return _launching.Contains(projectId);
        }
    }

    /// <summary>
    /// Runs work on the UI thread for a REST caller, with a time limit (review of PR #103, task ba0f0ee0). The
    /// open and open-state callbacks used <c>Task.Run(() =&gt; Invoke(...))</c>, so a stalled UI thread left the
    /// HTTP request hanging and a thread-pool thread blocked. Here the work is posted, the caller waits at most
    /// <c>timeout</c>, and work that had not started by then is abandoned: it never runs late, so a caller told
    /// "busy" cannot have the open happen after all.
    /// </summary>
    public static class UiThreadCall
    {
        /// <summary>How long a REST open waits for MT's UI thread.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Posts <paramref name="work"/> with <paramref name="post"/> (e.g. <c>Control.BeginInvoke</c>) and waits
        /// for it. Ran is false when it had not started within <paramref name="timeout"/> (then it never will) or
        /// could not be posted. An exception from the work is rethrown.
        /// </summary>
        public static async Task<(bool Ran, T Value)> RunAsync<T>(Action<Action> post, Func<T> work, TimeSpan timeout)
        {
            if (post == null) throw new ArgumentNullException(nameof(post));
            if (work == null) throw new ArgumentNullException(nameof(work));

            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            int state = 0; // 0 waiting, 1 started, 2 abandoned

            try
            {
                post(() =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                        return; // abandoned: the caller has already been told it timed out
                    try
                    {
                        done.TrySetResult(work());
                    }
#pragma warning disable CA1031 // Handed to the awaiting caller, which turns it into a REST error.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        done.TrySetException(ex);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                return (false, default);
            }

            var winner = await Task.WhenAny(done.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (winner != done.Task && Interlocked.CompareExchange(ref state, 2, 0) == 0)
                return (false, default);

            // Started (in time, or in the instant the timer fired): wait for it to finish.
            return (true, await done.Task.ConfigureAwait(false));
        }
    }
}
