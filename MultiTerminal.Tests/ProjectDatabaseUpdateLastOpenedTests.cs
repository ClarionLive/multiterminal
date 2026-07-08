using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression tests for ticket 93ad8184's pipeline Run-1 finding: the best-effort recency
    /// stamp must be a single-column UPDATE, not the SaveRichProject full-row UPSERT. A deferred
    /// background stamp replaying a stale snapshot could revert a concurrent edit (snapshot's
    /// non-null fields win the per-column COALESCE) or resurrect a deleted project (the UPSERT
    /// inserts). <see cref="ProjectDatabase.UpdateLastOpened"/> can do neither — proven here by
    /// the exact interleaving the cross-model adversary described: snapshot → edit → stamp.
    /// </summary>
    public sealed class ProjectDatabaseUpdateLastOpenedTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly ProjectDatabase _projectDb;

        public ProjectDatabaseUpdateLastOpenedTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_lastopened_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _projectDb = new ProjectDatabase();
        }

        public void Dispose()
        {
            _projectDb?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
            foreach (var side in new[] { _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(side)) File.Delete(side);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void UpdateLastOpened_DeferredStamp_DoesNotRevertConcurrentEdit()
        {
            // Launch path takes a snapshot of the project...
            var project = MultiTerminal.Models.Project.Create("StampProj", @"C:\temp\stampproj");
            project.BuildCommand = "build-v1";
            _projectDb.SaveRichProject(project);
            var snapshotTime = DateTime.Now;

            // ...then the user edits the project while the deferred stamp is still queued...
            var edited = _projectDb.GetRichProject(project.Id);
            edited.BuildCommand = "build-v2";
            edited.Description = "edited while stamp deferred";
            _projectDb.SaveRichProject(edited);

            // ...and the deferred stamp finally lands, knowing only id + timestamp.
            _projectDb.UpdateLastOpened(project.Id, snapshotTime);

            var after = _projectDb.GetRichProject(project.Id);
            Assert.Equal("build-v2", after.BuildCommand);                    // edit survived
            Assert.Equal("edited while stamp deferred", after.Description);  // edit survived
            Assert.Equal(snapshotTime, after.LastOpenedAt, TimeSpan.FromSeconds(1)); // stamp landed
        }

        [Fact]
        public void UpdateLastOpened_DeletedProject_IsNotResurrected()
        {
            var project = MultiTerminal.Models.Project.Create("GhostProj", @"C:\temp\ghostproj");
            _projectDb.SaveRichProject(project);
            _projectDb.DeleteProject(project.Id);
            Assert.Null(_projectDb.GetRichProject(project.Id));

            // The deferred stamp lands after the delete: UPDATE affects zero rows, no insert.
            _projectDb.UpdateLastOpened(project.Id, DateTime.Now);

            Assert.Null(_projectDb.GetRichProject(project.Id));
        }

        [Fact]
        public void UpdateLastOpened_NullOrEmptyId_IsANoOp()
        {
            _projectDb.UpdateLastOpened(null, DateTime.Now);
            _projectDb.UpdateLastOpened(string.Empty, DateTime.Now);
        }

        [Fact]
        public void UpdateLastOpened_OlderDeferredStamp_CannotMoveTimestampBackwards()
        {
            // Two fire-and-forget stamps can land out of order (Run-2 adversary finding):
            // the newer stamp lands first, then an older queued stamp arrives late.
            var project = MultiTerminal.Models.Project.Create("MonotonicProj", @"C:\temp\monotonicproj");
            _projectDb.SaveRichProject(project);

            var newer = DateTime.Now;
            var older = newer.AddMinutes(-5);

            _projectDb.UpdateLastOpened(project.Id, newer);
            _projectDb.UpdateLastOpened(project.Id, older); // late arrival — must lose

            var after = _projectDb.GetRichProject(project.Id);
            Assert.Equal(newer, after.LastOpenedAt, TimeSpan.FromSeconds(1));
        }
    }
}
