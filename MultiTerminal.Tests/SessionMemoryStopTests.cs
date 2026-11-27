using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Review of PR #105 (task 33ac0034): startup crash-recovery indexing runs in the background, and MT's
    /// close waits on the session-memory gate it holds for a whole transcript (minutes for a big one). A
    /// caller-supplied stop check now abandons a file before anything is embedded or written. Not covered:
    /// that MainForm sets the flag in OnFormClosing and passes it (wiring no test executes), and the per-chunk
    /// check during embedding (it would need the embedding model).
    /// </summary>
    public sealed class SessionMemoryStopTests : IDisposable
    {
        private readonly string _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_smstop_{Guid.NewGuid():N}.db");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mt_smstop_{Guid.NewGuid():N}");
        private readonly TaskDatabase _taskDb;
        private readonly SessionMemoryDatabase _memory;

        public SessionMemoryStopTests()
        {
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Directory.CreateDirectory(_dir);
            _taskDb = new TaskDatabase();
            _memory = new SessionMemoryDatabase(_taskDb);
        }

        public void Dispose()
        {
            _memory.Dispose();
            _taskDb.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string Transcript()
        {
            string path = Path.Combine(_dir, $"{Guid.NewGuid()}.jsonl");
            string line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Please refactor the parser so that the tokenizer handles quoted strings and escaped quotes correctly across multiple lines of input.\"}}";
            File.WriteAllLines(path, new[] { line, line.Replace("Please refactor", "Now also test") });
            return path;
        }

        /// <summary>
        /// A stop check that answers true abandons the file: nothing is stored and it is not marked indexed,
        /// so the next run picks it up. Falsified: dropping the check before the embedder in IndexSessionFile
        /// makes it embed and store the file, and the assertions red.
        /// </summary>
        [Fact]
        public void A_stopping_caller_abandons_the_file_without_indexing_it()
        {
            string path = Transcript();
            string sessionId = Path.GetFileNameWithoutExtension(path);

            int chunks = _memory.IndexSessionFile(path, null, _dir, shouldStop: () => true);

            Assert.Equal(0, chunks);
            Assert.False(_memory.IsSessionIndexed(sessionId));
        }
    }
}
