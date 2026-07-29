#nullable enable

using System;
using System.Data.SQLite;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for the permanent write-contention diagnostics (task a5ac5f71). The class is a
    /// process-wide static registry, so these tests assert on report CONTENT via the internal
    /// <see cref="WriteContentionDiagnostics.BuildBusyReport"/> seam rather than global counts —
    /// content assertions stay valid even if other tests hold scopes concurrently.
    /// </summary>
    public class WriteContentionDiagnosticsTests
    {
        [Fact]
        public void IsBusyException_BusyResultCode_True()
        {
            var ex = new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            Assert.True(WriteContentionDiagnostics.IsBusyException(ex));
        }

        [Fact]
        public void IsBusyException_LockedResultCode_True()
        {
            var ex = new SQLiteException(SQLiteErrorCode.Locked, "database table is locked");
            Assert.True(WriteContentionDiagnostics.IsBusyException(ex));
        }

        [Fact]
        public void IsBusyException_WrappedInner_True()
        {
            var inner = new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            var outer = new InvalidOperationException("registration failed", inner);
            Assert.True(WriteContentionDiagnostics.IsBusyException(outer));
        }

        [Fact]
        public void IsBusyException_OtherSqliteError_False()
        {
            var ex = new SQLiteException(SQLiteErrorCode.Constraint, "constraint failed");
            Assert.False(WriteContentionDiagnostics.IsBusyException(ex));
        }

        [Fact]
        public void IsBusyException_NonSqlite_And_Null_False()
        {
            Assert.False(WriteContentionDiagnostics.IsBusyException(new InvalidOperationException("nope")));
            Assert.False(WriteContentionDiagnostics.IsBusyException(null));
        }

        [Fact]
        public void BuildBusyReport_ActiveScope_NamesHolderWithDetail()
        {
            using (WriteContentionDiagnostics.BeginWrite("Test.HolderOwner", "holder-detail"))
            {
                string report = WriteContentionDiagnostics.BuildBusyReport("Test.Victim", "victim-context");

                Assert.Contains("SQLITE_BUSY victim: Test.Victim", report, StringComparison.Ordinal);
                Assert.Contains("victim-context", report, StringComparison.Ordinal);
                Assert.Contains("Test.HolderOwner [holder-detail]", report, StringComparison.Ordinal);
                Assert.Contains("Active in-process write transactions", report, StringComparison.Ordinal);
                // With a live holder the report must NOT claim the lock is external.
                Assert.DoesNotContain("NO in-process write transaction", report, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void BuildBusyReport_DisposedScope_MovesFromActiveToRecent()
        {
            using (WriteContentionDiagnostics.BeginWrite("Test.CompletedOwner", "completed-detail"))
            {
            }

            string report = WriteContentionDiagnostics.BuildBusyReport("Test.Victim");

            // The disposed scope must not appear as an ACTIVE holder...
            int activeSection = report.IndexOf("Active in-process write transactions", StringComparison.Ordinal);
            if (activeSection >= 0)
            {
                int recentSection = report.IndexOf("Writes completed in the last", StringComparison.Ordinal);
                Assert.True(recentSection > activeSection, "recent section should follow active section");
                string activeText = report[activeSection..recentSection];
                Assert.DoesNotContain("Test.CompletedOwner", activeText, StringComparison.Ordinal);
            }

            // ...but must appear in the recent-completions window (64-slot ring, just written).
            Assert.Contains("Test.CompletedOwner [completed-detail]", report, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildBusyReport_EmptySnapshot_PointsAtExternalProcess()
        {
            // Cannot guarantee zero active scopes process-wide, so only assert when none are live.
            string report = WriteContentionDiagnostics.BuildBusyReport("Test.Victim");
            if (!report.Contains("Active in-process write transactions", StringComparison.Ordinal))
            {
                Assert.Contains("NO in-process write transaction was active", report, StringComparison.Ordinal);
                Assert.Contains("another process", report, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void WriteScope_DoubleDispose_IsIdempotent()
        {
            var scope = WriteContentionDiagnostics.BeginWrite("Test.DoubleDispose");
            scope.Dispose();
            scope.Dispose();

            // A second dispose must not re-enqueue: the ring holds ONE completion for this owner.
            string report = WriteContentionDiagnostics.BuildBusyReport("Test.Victim");
            int first = report.IndexOf("Test.DoubleDispose", StringComparison.Ordinal);
            Assert.True(first >= 0, "completed scope should appear in recent writes");
            int second = report.IndexOf("Test.DoubleDispose", first + 1, StringComparison.Ordinal);
            Assert.True(second < 0, "double dispose must not produce a second recent-write entry");
        }

        [Fact]
        public void LogBusy_WithoutLogger_DoesNotThrow()
        {
            // No SetLogger call in tests — must fall back silently, never throw from a failure path.
            WriteContentionDiagnostics.LogBusy("Test.Victim", "no logger wired");
        }
    }
}
