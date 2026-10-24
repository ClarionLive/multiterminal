using System;
using System.IO;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The crash report written when an exception escapes everything (GitHub #38, task 8f0d527b).
    /// Covers the report's content and the file write; that <c>Install</c>'s handlers fire on a real
    /// unhandled exception needs a crashing process and is not tested here.
    /// </summary>
    public sealed class UnhandledExceptionLoggerTests : IDisposable
    {
        private static readonly DateTime When = new(2026, 10, 24, 10, 30, 15, 123, DateTimeKind.Local);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-crashlog-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void The_report_carries_the_whole_exception_chain_the_thread_and_whether_it_is_fatal()
        {
            Exception ex;
            try
            {
                throw new InvalidOperationException("outer failure", new TimeoutException("inner cause"));
            }
            catch (InvalidOperationException caught)
            {
                ex = caught;
            }

            string report = UnhandledExceptionLogger.FormatReport(
                "AppDomain.UnhandledException", ex, isTerminating: true, When, threadId: 42, threadName: null, isThreadPoolThread: true);

            Assert.Contains("AppDomain.UnhandledException", report, StringComparison.Ordinal);
            Assert.Contains("Terminating: yes", report, StringComparison.Ordinal);
            Assert.Contains("Thread: 42 (thread pool)", report, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException: outer failure", report, StringComparison.Ordinal);
            Assert.Contains("System.TimeoutException: inner cause", report, StringComparison.Ordinal);
            Assert.Contains(nameof(The_report_carries_the_whole_exception_chain_the_thread_and_whether_it_is_fatal), report, StringComparison.Ordinal);
        }

        [Fact]
        public void A_non_exception_object_still_produces_a_report()
        {
            string report = UnhandledExceptionLogger.FormatReport(
                "AppDomain.UnhandledException", exception: null, isTerminating: true, When, 1, "Main", isThreadPoolThread: false);

            Assert.Contains("(not an Exception object)", report, StringComparison.Ordinal);
            Assert.Contains("Thread: 1 \"Main\"", report, StringComparison.Ordinal);
        }

        [Fact]
        public void Reports_are_appended_to_one_file_per_day()
        {
            string first = UnhandledExceptionLogger.TryAppendReport(_dir, When, "first report\n")!;
            string second = UnhandledExceptionLogger.TryAppendReport(_dir, When.AddHours(1), "second report\n")!;

            Assert.Equal(first, second);
            Assert.Equal("crash-2026-10-24.log", Path.GetFileName(first));
            string text = File.ReadAllText(first);
            Assert.True(text.IndexOf("first report", StringComparison.Ordinal) < text.IndexOf("second report", StringComparison.Ordinal));
        }

        [Fact]
        public void A_report_is_not_lost_while_another_writer_holds_the_daily_file()
        {
            // Stands in for a second crash handler mid-write: the day's file is held open exclusively.
            Directory.CreateDirectory(_dir);
            string daily = Path.Combine(_dir, "crash-2026-10-24.log");
            File.WriteAllText(daily, "earlier report\n");

            string written;
            using (new FileStream(daily, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                written = UnhandledExceptionLogger.TryAppendReport(_dir, When, "fatal report\n");
            }

            Assert.NotNull(written);
            Assert.NotEqual(daily, written);
            Assert.StartsWith("crash-2026-10-24-", Path.GetFileName(written), StringComparison.Ordinal);
            Assert.Equal("fatal report\n", File.ReadAllText(written));
            Assert.Equal("earlier report\n", File.ReadAllText(daily));
        }

        [Fact]
        public void An_unwritable_location_returns_null_instead_of_throwing()
        {
            // A path through an existing FILE cannot be a directory, so the write must fail.
            Directory.CreateDirectory(_dir);
            string file = Path.Combine(_dir, "not-a-folder");
            File.WriteAllText(file, "x");

            Assert.Null(UnhandledExceptionLogger.TryAppendReport(Path.Combine(file, "logs"), When, "report"));
        }
    }
}
