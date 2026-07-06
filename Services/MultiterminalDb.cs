using System.Data.SQLite;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The single place a connection to <c>multiterminal.db</c> is opened.
    ///
    /// <para>Ticket bb2b0104 established the invariant: <b>every SQLite connection has exactly
    /// ONE owner class; the owner serializes its own multithreaded access; no class ever touches
    /// another class's handle.</b> Under that model each connection-owning class
    /// (TaskDatabase, ProjectDatabase, KnowledgeDatabase, CodeGraphDatabase, SessionMemoryDatabase,
    /// BranchMetadataService, OwnerProfileService, SourceControlAccountService) opens its OWN
    /// connection here rather than borrowing a sibling's <c>Connection</c> property (that escape
    /// hatch — the pre-bb2b0104 race — has been deleted).</para>
    ///
    /// <para>Centralizing the open in one factory (bb2b0104 condition 2) guarantees the WAL /
    /// pooling / busy_timeout settings cannot drift per-site. Multiple WAL connections to one file
    /// are exactly what WAL is designed for: concurrent readers see a consistent snapshot and never
    /// block; writers serialize at commit, absorbed by <c>busy_timeout</c>. The file path honors the
    /// <c>MULTITERMINAL_TEST_DB</c> override via <see cref="TaskDatabase.GetDatabasePath"/> so tests
    /// (and the bb2b0104 cross-connection hammer) point every owner at the same isolated temp DB.</para>
    /// </summary>
    public static class MultiterminalDb
    {
        /// <summary>
        /// Opens and returns a configured, already-open connection to multiterminal.db.
        /// The caller OWNS the returned connection and is responsible for disposing it.
        /// </summary>
        public static SQLiteConnection Open()
        {
            string path = TaskDatabase.GetDatabasePath();
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                Version = 3,
                JournalMode = SQLiteJournalModeEnum.Wal,
                Pooling = true
            }.ToString();

            var connection = new SQLiteConnection(connectionString);
            try
            {
                connection.Open();

                // Cross-PROCESS contention on the shared multiterminal.db file: the in-process gate each
                // owner holds serializes only THIS process's threads on THIS connection. mcp-session-history's
                // better-sqlite3 indexer is a separate OS process opening its own handle on the same DB
                // family — no in-process lock can serialize against it. busy_timeout makes SQLite wait/retry
                // a locked DB for up to 5s rather than throwing SQLITE_BUSY immediately. Set uniformly here
                // so no owner can forget it (ad08caac item 4 set it on TaskDatabase; bb2b0104 centralizes it).
                connection.BusyTimeout = 5000;
            }
            catch
            {
                // Don't leak a half-open handle to the finalizer if Open()/BusyTimeout throws.
                connection.Dispose();
                throw;
            }

            return connection;
        }
    }
}
