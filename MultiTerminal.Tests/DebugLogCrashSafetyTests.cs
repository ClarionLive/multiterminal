using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Panels;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task d41637ab: MultiTerminal crashed on 2026-10-16 16:50:59. CodeGraphWatcher logged a
    /// FileSystemWatcher overflow from a thread-pool thread; DebugLogService raised LogMessageAdded
    /// on that thread; DebugPanel's InvokeRequired answered false (no handle), so it edited the
    /// ListView off the UI thread, which threw, and nothing between there and the pool caught it.
    ///
    /// <para>Falsified by reverting each fix against the same file state: treating every thread as
    /// the UI thread turns the panel fact red at the first count assertion (3 rows instead of 2, the
    /// pool thread having added one); restoring a direct <c>?.Invoke</c> turns the matching
    /// isolation fact red with the subscriber's exception escaping; removing the rate limit turns
    /// the report-count fact red. None can pass by luck: each is single-threaded at the point
    /// asserted.</para>
    ///
    /// <para>NOT covered: the interleavings MarshalOrDefer's re-check exists for (a log racing
    /// OnHandleCreated, a handle destroyed after BeginInvoke). Those are argued in its comments, not
    /// exercised here.</para>
    /// </summary>
    public class DebugLogCrashSafetyTests
    {
        [Fact]
        public void A_panel_without_a_handle_is_not_edited_from_a_pool_thread_and_catches_up_later()
        {
            WithTempLog(log =>
            {
                RunOnSta(() =>
                {
                    log.Info("Test", "one");
                    log.Info("Test", "two");

                    using var panel = new DebugPanel();
                    panel.Initialize(log);
                    Assert.False(panel.IsHandleCreated, "the fact needs a panel with no handle");
                    Assert.Equal(2, panel.DisplayedEntryCount);

                    // The crash path: a log call from a thread-pool thread.
                    Task.Run(() => log.Error("Test", "from-the-pool")).Wait();

                    Assert.Equal(2, panel.DisplayedEntryCount);

                    _ = panel.Handle;   // forces handle creation -> OnHandleCreated -> resync

                    Assert.Equal(3, panel.DisplayedEntryCount);
                });
            });
        }

        [Fact]
        public void A_throwing_subscriber_does_not_escape_Log_or_starve_later_subscribers()
        {
            WithTempLog(log =>
            {
                int laterCalls = 0;
                log.LogMessageAdded += (s, e) => throw new ArgumentOutOfRangeException("index");
                log.LogMessageAdded += (s, e) => laterCalls++;

                var ex = Record.Exception(() => log.Error("Test", "boom"));

                Assert.Null(ex);
                Assert.Equal(1, laterCalls);
            });
        }

        [Fact]
        public void A_throwing_LogCleared_subscriber_does_not_escape_Clear()
        {
            WithTempLog(log =>
            {
                log.LogCleared += (s, e) => throw new InvalidOperationException("boom");

                Assert.Null(Record.Exception(() => log.Clear()));
            });
        }

        /// <summary>
        /// Codex security, Run 1: with system-wide capture on, each fault report comes back through
        /// OutputDebugString into the subscriber that threw, so unlimited reporting loops. A burst of
        /// faults inside one interval must produce one report.
        /// </summary>
        [Fact]
        public void Subscriber_faults_are_reported_at_most_once_per_interval()
        {
            WithTempLog(log =>
            {
                log.LogMessageAdded += (s, e) => throw new ArgumentOutOfRangeException("index");

                for (int i = 0; i < 100; i++) log.Error("Test", "boom " + i);

                Assert.Equal(1, log.SubscriberFaultsReported);
            });
        }

        private static void WithTempLog(Action<DebugLogService> body)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt-debuglog-crash-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var log = new DebugLogService(dir, writerFlushIntervalMs: 50);
                body(log);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        private static void RunOnSta(Action body)
        {
            ExceptionDispatchInfo failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA body did not finish");
            failure?.Throw();
        }
    }
}
