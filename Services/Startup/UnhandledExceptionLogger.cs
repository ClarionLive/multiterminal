#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// Writes a crash report when an exception escapes everything (GitHub #38, task 8f0d527b).
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists:</b> a 2.1.0 install terminated on an unhandled
    /// <c>InvalidOperationException</c> from <c>WindowsFormsSynchronizationContext.Post</c>, rethrown on a
    /// thread-pool thread, and MT's own log had nothing: the only record was the Event Viewer entry.
    /// Nothing in MT subscribed to <see cref="AppDomain.UnhandledException"/>. This does not stop such a
    /// crash (an exception escaping to the thread pool always ends the process); it makes the next one
    /// leave a full report, with the whole exception chain, the thread and the time, next to the debug
    /// logs.</para>
    /// <para>The report is written synchronously with plain file I/O, not through
    /// <see cref="DebugLogService"/>, whose writer is a background queue the dying process may never
    /// drain. Writing never throws: a logger that crashes inside a crash handler hides the original.</para>
    /// <para>It changes no behaviour. <c>Application.ThreadException</c> (UI-thread exceptions) keeps its
    /// default dialog; unobserved task exceptions are only logged, as the runtime already ignores them.</para>
    /// </remarks>
    internal static class UnhandledExceptionLogger
    {
        private static int _installed;

        /// <summary>Subscribes the process-wide handlers once. Call first thing in <c>Main</c>.</summary>
        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Record("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

            TaskScheduler.UnobservedTaskException += (s, e) =>
                Record("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
        }

        private static void Record(string source, Exception? exception, bool isTerminating)
        {
            var thread = Thread.CurrentThread;
            DateTime now = DateTime.Now; // one reading, so the report's time and its file's date agree
            string report = FormatReport(
                source, exception, isTerminating, now, thread.ManagedThreadId, thread.Name, thread.IsThreadPoolThread);
            TryAppendReport(DebugLogService.LogDirectory, now, report);
        }

        /// <summary>The report text. Pure, so the content can be tested without crashing anything.</summary>
        internal static string FormatReport(
            string source,
            Exception? exception,
            bool isTerminating,
            DateTime localTime,
            int threadId,
            string? threadName,
            bool isThreadPoolThread)
        {
            var sb = new StringBuilder();
            sb.Append("===== ").Append(localTime.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
              .Append(" ===== ").AppendLine(source);
            sb.Append("Terminating: ").AppendLine(isTerminating ? "yes" : "no");
            sb.Append("Thread: ").Append(threadId.ToString(CultureInfo.InvariantCulture))
              .Append(string.IsNullOrEmpty(threadName) ? string.Empty : $" \"{threadName}\"")
              .AppendLine(isThreadPoolThread ? " (thread pool)" : string.Empty);

            if (exception == null)
            {
                sb.AppendLine("Exception: (not an Exception object)");
            }
            else
            {
                // ToString() carries the type, message, stack and every inner exception (including each
                // one of an AggregateException), which is exactly what the Event Viewer entry lacked.
                sb.AppendLine(exception.ToString());
            }

            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// Appends <paramref name="report"/> to <c>crash-yyyy-MM-dd.log</c> in <paramref name="directory"/>.
        /// When that file is busy, the report goes to a file of its own instead
        /// (<c>crash-yyyy-MM-dd-HHmmss-fff-&lt;id&gt;.log</c>). Returns the file written, or null when
        /// nothing could be written. Never throws.
        /// </summary>
        /// <remarks>
        /// Two handlers can fire at once: an unobserved task exception on the finalizer thread while the
        /// fatal exception is being reported. The append takes the file exclusively for writing, so the
        /// second writer gets a sharing violation, and the report it carries may be the fatal one. Sharing
        /// the file for writing instead would not be safe either: an append positions at the end when the
        /// file is opened, so two writers can overwrite each other.
        /// </remarks>
        internal static string? TryAppendReport(string directory, DateTime localTime, string report)
        {
            string day = localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "crash-" + day + ".log");
                File.AppendAllText(path, report, Encoding.UTF8);
                return path;
            }
            catch (IOException)
            {
                // Busy (or otherwise unwritable): fall through to a file nobody else can be holding.
            }
            catch (Exception)
            {
                // Nowhere left to report to; the runtime's own handling (Event Viewer) still applies.
                return null;
            }

            try
            {
                string own = Path.Combine(
                    directory,
                    "crash-" + day + "-" + localTime.ToString("HHmmss-fff", CultureInfo.InvariantCulture)
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                using var stream = new FileStream(own, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(report);
                stream.Write(bytes, 0, bytes.Length);
                return own;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
