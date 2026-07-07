using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// schema_version / migration-runner tests for ticket 1df2a534 (P5) item 3. The runner records each
    /// applied MigrateXxx in the <c>schema_migrations</c> ledger and skips already-recorded ones on the
    /// next startup. This covers the plan's three-DB matrix:
    ///   (1) fresh DB — every migration runs once and is recorded;
    ///   (2) existing dev DB — a second Initialize skips all of them and doesn't error or double-apply;
    ///   (3) DB missing one column — a migration whose ledger record is ABSENT re-runs and heals the
    ///       column (proves the runner keys off the ledger, and that the idempotent migrations self-heal
    ///       a partially-migrated DB rather than being permanently skipped).
    ///
    /// Each <c>new TaskDatabase()</c> runs InitializeDatabase (CreateSchema + the wrapped migrations) in
    /// its ctor against the isolated temp DB pointed to by MULTITERMINAL_TEST_DB. We inspect the ledger /
    /// columns through our OWN short-lived connection (TaskDatabase's handle is private and unshared).
    /// </summary>
    public sealed class SchemaMigrationsTests : IDisposable
    {
        private readonly string _dbPath;

        public SchemaMigrationsTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_schemamig_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools(); // release pooled handles so the file can be deleted
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void FreshDb_RecordsEveryMigration()
        {
            using (new TaskDatabase()) { }   // ctor runs InitializeDatabase
            SQLiteConnection.ClearAllPools();

            // All wrapped migrations are recorded (there are 42 today; assert >= 42 so adding one later
            // doesn't fail this test), and a representative migrated column exists on a fresh DB.
            Assert.True(CountMigrations() >= 42, $"expected all migrations recorded on a fresh DB, got {CountMigrations()}");
            Assert.True(ColumnExists("tasks", "review_notes"));
            Assert.True(MigrationRecorded("MigrateAddReviewNotesToTasks"));
        }

        [Fact]
        public void ExistingDb_SecondInit_SkipsAllAndDoesNotError()
        {
            using (new TaskDatabase()) { }
            SQLiteConnection.ClearAllPools();
            int firstCount = CountMigrations();

            // Second Initialize on the same DB: every migration is already recorded, so nothing re-runs;
            // the ledger count is unchanged and the ctor completes without throwing.
            using (new TaskDatabase()) { }
            SQLiteConnection.ClearAllPools();

            Assert.Equal(firstCount, CountMigrations());
            Assert.True(ColumnExists("tasks", "review_notes"));
        }

        [Fact]
        public void MissingColumn_ReInit_HealsWhenLedgerRecordAbsent()
        {
            using (new TaskDatabase()) { }
            SQLiteConnection.ClearAllPools();
            Assert.True(ColumnExists("tasks", "review_notes"));

            // Simulate a DB where this migration never applied: drop the column AND its ledger row.
            using (var c = OpenRaw())
            {
                using (var drop = new SQLiteCommand("ALTER TABLE tasks DROP COLUMN review_notes", c))
                {
                    drop.ExecuteNonQuery();
                }
                using (var del = new SQLiteCommand("DELETE FROM schema_migrations WHERE name = 'MigrateAddReviewNotesToTasks'", c))
                {
                    del.ExecuteNonQuery();
                }
            }
            SQLiteConnection.ClearAllPools();
            Assert.False(ColumnExists("tasks", "review_notes"), "column should be gone before re-init");
            Assert.False(MigrationRecorded("MigrateAddReviewNotesToTasks"), "ledger record should be gone before re-init");

            // Re-init: the runner sees the record absent, re-runs the idempotent migration, heals the column.
            using (new TaskDatabase()) { }
            SQLiteConnection.ClearAllPools();

            Assert.True(ColumnExists("tasks", "review_notes"), "re-init should re-add the missing column");
            Assert.True(MigrationRecorded("MigrateAddReviewNotesToTasks"), "re-init should re-record the migration");
        }

        // ── helpers: inspect the DB through our own short-lived connection ───────────────────────────
        private SQLiteConnection OpenRaw()
        {
            var c = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = _dbPath, Version = 3 }.ToString());
            c.Open();
            return c;
        }

        private int CountMigrations()
        {
            using var c = OpenRaw();
            using var cmd = new SQLiteCommand("SELECT COUNT(*) FROM schema_migrations", c);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private bool MigrationRecorded(string name)
        {
            using var c = OpenRaw();
            using var cmd = new SQLiteCommand("SELECT 1 FROM schema_migrations WHERE name = @n LIMIT 1", c);
            cmd.Parameters.AddWithValue("@n", name);
            return cmd.ExecuteScalar() != null;
        }

        private bool ColumnExists(string table, string column)
        {
            using var c = OpenRaw();
            // PRAGMA can't take a parameterized table name; `table` is a hardcoded test literal ("tasks"),
            // never user input, so the interpolation is safe.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand($"PRAGMA table_info({table})", c);
#pragma warning restore CA2100
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
