#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// <para>Permanent write-transaction contention diagnostics for the shared multiterminal.db
    /// file (task a5ac5f71). Startup "database is locked" (SQLITE_BUSY) failures have now recurred
    /// twice (93ad8184 at project launch, a5ac5f71 at session registration), each time costing a
    /// full instrument-and-reproduce cycle because nothing recorded WHO held the write lock. This
    /// class makes the next occurrence diagnosable from one log read.</para>
    ///
    /// <para>Every in-process holder of a write transaction on multiterminal.db wraps the
    /// transaction in <see cref="BeginWrite"/>. The scope registry then supports the one question
    /// that matters at failure time: when a writer loses the lock race past busy_timeout,
    /// <see cref="LogBusy"/> dumps which in-process transactions were active (and for how long)
    /// plus a ring buffer of recently completed writes (rapid-reacquire starvation is visible as
    /// a dense burst of short holds, a 93ad8184-style holder as one long hold). An EMPTY active
    /// snapshot is itself evidence: the lock holder is another process — e.g. mcp-session-history's
    /// better-sqlite3 indexer, which no in-process gate can serialize against (MultiterminalDb) —
    /// or a non-transactional autocommit writer.</para>
    ///
    /// <para>Quiet by design so it can ship permanently: scopes are tracked but NOT logged
    /// per-acquire/release. Only three things reach the log — long holds
    /// (&gt;= <see cref="LongHoldWarnMs"/>, Warning), explicit phase markers (Info), and busy
    /// dumps (Error). The tracking cost per scope is a Stopwatch, one dictionary add/remove, and
    /// one bounded-queue append.</para>
    /// </summary>
    public static class WriteContentionDiagnostics
    {
        /// <summary>Holds at or above this many milliseconds are logged as warnings on release.</summary>
        public const int LongHoldWarnMs = 1000;

        private const string LogSource = "WriteContention";
        private const int RecentCapacity = 64;
        private const int BusyDumpRecentWindowSeconds = 60;
        private const int BusyDumpRecentMaxLines = 12;

        private static readonly ConcurrentDictionary<long, WriteScope> Active = new();
        private static readonly object RecentLock = new();
        private static readonly Queue<CompletedWrite> Recent = new();
        private static long _nextScopeId;
        private static DebugLogService? _log;

        /// <summary>
        /// Wires the shared <see cref="DebugLogService"/> sink. Called once by MainForm right after
        /// the service is constructed. Before wiring (or if never wired, e.g. unit tests) all output
        /// falls back to <see cref="Debug.WriteLine(string)"/>.
        /// </summary>
        public static void SetLogger(DebugLogService? logger) => _log = logger;

        /// <summary>
        /// Registers a write-transaction scope. Call with <c>using</c> around the lifetime of a
        /// write transaction on multiterminal.db; dispose when the transaction has committed or
        /// rolled back. <paramref name="owner"/> names the code path (e.g.
        /// "SessionMemory.IndexSessionFile"); <paramref name="detail"/> carries the per-call
        /// specifics (file name, chunk range).
        /// </summary>
        public static IDisposable BeginWrite(string owner, string? detail = null)
        {
            var scope = new WriteScope(Interlocked.Increment(ref _nextScopeId), owner, detail);
            Active[scope.Id] = scope;
            return scope;
        }

        /// <summary>
        /// Emits an Info-level phase marker (e.g. "crash-recovery indexing started"). Exists so DB
        /// layers without a <see cref="DebugLogService"/> reference can delineate contention-relevant
        /// windows in the same log stream as the busy dumps.
        /// </summary>
        public static void MarkPhase(string message) => Log(DebugLogLevel.Info, message);

        /// <summary>
        /// True when <paramref name="ex"/> (or any inner exception) is a SQLite busy/locked failure —
        /// the signature of losing a lock race past busy_timeout rather than a logic error.
        /// </summary>
        public static bool IsBusyException(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SQLiteException se &&
                    (se.ResultCode == SQLiteErrorCode.Busy || se.ResultCode == SQLiteErrorCode.Locked))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Dumps the contention snapshot for a writer that just failed with SQLITE_BUSY: active
        /// in-process write transactions with hold durations, then recently completed writes
        /// (starvation shows as a dense burst of short holds). Call from catch sites where
        /// <see cref="IsBusyException"/> matched. Never throws.
        /// </summary>
        public static void LogBusy(string victim, string? context = null)
        {
            try
            {
                Log(DebugLogLevel.Error, BuildBusyReport(victim, context));
            }
            catch
            {
                // Diagnostics must never add a failure mode to the failure path.
            }
        }

        /// <summary>
        /// Builds the busy-dump text. Internal seam so tests can assert on the report's content
        /// (empty-snapshot wording, active-holder lines) without a <see cref="DebugLogService"/>.
        /// </summary>
        internal static string BuildBusyReport(string victim, string? context = null)
        {
            var sb = new StringBuilder();
            sb.Append("SQLITE_BUSY victim: ").Append(victim);
            if (!string.IsNullOrEmpty(context)) sb.Append(" — ").Append(context);
            sb.AppendLine();

            var active = Active.Values.OrderByDescending(s => s.HeldMs).ToList();
            if (active.Count == 0)
            {
                sb.AppendLine(
                    "NO in-process write transaction was active at busy time — the lock holder is " +
                    "another process (e.g. mcp-session-history's better-sqlite3 indexer) or a " +
                    "non-transactional autocommit writer.");
            }
            else
            {
                sb.Append("Active in-process write transactions (").Append(active.Count).AppendLine("):");
                foreach (var s in active)
                {
                    sb.Append("  - ").Append(s.Describe()).AppendLine();
                }
            }

            AppendRecent(sb);
            return sb.ToString().TrimEnd();
        }

        private static void AppendRecent(StringBuilder sb)
        {
            List<CompletedWrite> recent;
            lock (RecentLock)
            {
                recent = Recent.ToList();
            }

            var cutoff = DateTime.UtcNow.AddSeconds(-BusyDumpRecentWindowSeconds);
            var window = recent.Where(w => w.CompletedUtc >= cutoff).ToList();
            if (window.Count == 0)
            {
                sb.Append("No writes completed in the last ").Append(BusyDumpRecentWindowSeconds).AppendLine("s.");
                return;
            }

            sb.Append("Writes completed in the last ").Append(BusyDumpRecentWindowSeconds).Append("s (")
              .Append(window.Count).Append(window.Count > BusyDumpRecentMaxLines ? ", newest " + BusyDumpRecentMaxLines + " shown" : string.Empty)
              .AppendLine("):");
            foreach (var w in Enumerable.Reverse(window).Take(BusyDumpRecentMaxLines))
            {
                sb.Append("  - ").Append(w.Describe()).AppendLine();
            }
        }

        private static void CompleteScope(WriteScope scope)
        {
            Active.TryRemove(scope.Id, out _);
            var completed = new CompletedWrite(scope.Owner, scope.Detail, scope.HeldMs, DateTime.UtcNow);
            lock (RecentLock)
            {
                Recent.Enqueue(completed);
                while (Recent.Count > RecentCapacity) Recent.Dequeue();
            }

            if (scope.HeldMs >= LongHoldWarnMs)
            {
                Log(
                    DebugLogLevel.Warning,
                    $"Long write-transaction hold: {scope.Owner}{FormatDetail(scope.Detail)} held the multiterminal.db write lock {scope.HeldMs}ms (thread {scope.ThreadId})");
            }
        }

        private static void Log(DebugLogLevel level, string message)
        {
            var log = _log;
            if (log != null)
            {
                log.Log(LogSource, level, message);
            }
            else
            {
                Debug.WriteLine($"[{LogSource}] {level}: {message}");
            }
        }

        private static string FormatDetail(string? detail)
            => string.IsNullOrEmpty(detail) ? string.Empty : " [" + detail + "]";

        private sealed class WriteScope : IDisposable
        {
            private readonly Stopwatch _held = Stopwatch.StartNew();
            private int _disposed;

            public WriteScope(long id, string owner, string? detail)
            {
                Id = id;
                Owner = owner;
                Detail = detail;
                ThreadId = Environment.CurrentManagedThreadId;
            }

            public long Id { get; }

            public string Owner { get; }

            public string? Detail { get; }

            public int ThreadId { get; }

            public long HeldMs => _held.ElapsedMilliseconds;

            public string Describe()
                => $"{Owner}{FormatDetail(Detail)} held {HeldMs}ms (thread {ThreadId})";

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _held.Stop();
                CompleteScope(this);
            }
        }

        private sealed record CompletedWrite(string Owner, string? Detail, long HeldMs, DateTime CompletedUtc)
        {
            public string Describe()
            {
                var ago = (long)(DateTime.UtcNow - CompletedUtc).TotalMilliseconds;
                return $"{Owner}{FormatDetail(Detail)} {HeldMs}ms, finished {ago}ms ago";
            }
        }
    }
}
