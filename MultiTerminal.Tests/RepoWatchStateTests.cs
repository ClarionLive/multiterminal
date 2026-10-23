using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <c>repo_watch_state</c> (task df58b35d, GitHub #53): the per-(project, repo) fetch cache and the seen
    /// high-water mark. Runs against the REAL <see cref="TaskDatabase"/> on a temp SQLite file.
    /// </summary>
    public sealed class RepoWatchStateTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _msgDbPath;

        private static readonly DateTime T1 = new DateTime(2026, 10, 23, 9, 15, 30, DateTimeKind.Utc);
        private static readonly DateTime T2 = new DateTime(2026, 10, 23, 9, 20, 45, DateTimeKind.Utc);

        public RepoWatchStateTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_repowatch_{Guid.NewGuid():N}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_repowatch_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _testDbPath, _msgDbPath })
            {
                foreach (var p in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        [Fact]
        public void A_repo_never_checked_has_no_state()
        {
            using var db = new TaskDatabase();
            Assert.Null(db.GetRepoWatchState("p1", "owner/repo"));
            Assert.Empty(db.GetRepoWatchStates("p1"));
        }

        [Fact]
        public void A_saved_fetch_reads_back_with_no_seen_mark()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "owner/repo", "\"etag-1\"", T1, "ok", null, "[{\"number\":7}]");

            var state = db.GetRepoWatchState("p1", "owner/repo");
            Assert.NotNull(state);
            Assert.Equal("p1", state.ProjectId);
            Assert.Equal("owner/repo", state.Repo);
            Assert.Equal("\"etag-1\"", state.ETag);
            Assert.Equal(T1, state.LastCheckedAt);
            Assert.Equal(DateTimeKind.Utc, state.LastCheckedAt.Value.Kind);
            Assert.Equal("ok", state.LastStatus);
            Assert.Null(state.LastMessage);
            Assert.Equal("[{\"number\":7}]", state.SnapshotJson);
            Assert.Null(state.SeenMaxNumber); // a fetch never initializes the mark
        }

        [Fact]
        public void The_repo_is_one_row_whatever_its_case()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "Owner/Repo", "e1", T1, "ok", null, "[]");
            db.MarkRepoSeen("p1", "OWNER/REPO", 12);

            var state = db.GetRepoWatchState("p1", "owner/repo");
            Assert.NotNull(state);
            Assert.Equal("owner/repo", state.Repo);
            Assert.Equal(12, state.SeenMaxNumber);
            Assert.Single(db.GetRepoWatchStates("p1"));
        }

        [Fact]
        public void A_fetch_without_a_snapshot_keeps_the_old_snapshot_and_etag_but_records_the_outcome()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "owner/repo", "e1", T1, "ok", null, "[1]");
            db.MarkRepoSeen("p1", "owner/repo", 5);

            // A 304 (or an error) arrives: the caller may still hand over an etag, which must not land.
            db.SaveRepoWatchFetch("p1", "owner/repo", "e-other", T2, "error", "rate limited", null);

            var state = db.GetRepoWatchState("p1", "owner/repo");
            Assert.Equal("e1", state.ETag);
            Assert.Equal("[1]", state.SnapshotJson);
            Assert.Equal("error", state.LastStatus);
            Assert.Equal("rate limited", state.LastMessage);
            Assert.Equal(T2, state.LastCheckedAt);
            Assert.Equal(5, state.SeenMaxNumber); // a fetch never touches the mark

            // The next 200 replaces both, and clears the old message.
            db.SaveRepoWatchFetch("p1", "owner/repo", "e2", T2, "ok", null, "[2]");
            state = db.GetRepoWatchState("p1", "owner/repo");
            Assert.Equal("e2", state.ETag);
            Assert.Equal("[2]", state.SnapshotJson);
            Assert.Null(state.LastMessage);
        }

        [Fact]
        public void A_first_fetch_without_a_snapshot_stores_no_etag()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "owner/repo", "e1", T1, "error", "boom", null);

            var state = db.GetRepoWatchState("p1", "owner/repo");
            Assert.Null(state.ETag);
            Assert.Null(state.SnapshotJson);
            Assert.Equal("error", state.LastStatus);
        }

        [Fact]
        public void Initialize_sets_the_mark_only_the_first_time()
        {
            using var db = new TaskDatabase();
            Assert.True(db.InitializeRepoSeenIfUnset("p1", "owner/repo", 40));
            Assert.False(db.InitializeRepoSeenIfUnset("p1", "owner/repo", 90));
            Assert.Equal(40, db.GetRepoWatchState("p1", "owner/repo").SeenMaxNumber);

            // Also on a row that already exists from a fetch.
            db.SaveRepoWatchFetch("p1", "other/repo", "e", T1, "ok", null, "[]");
            Assert.True(db.InitializeRepoSeenIfUnset("p1", "other/repo", 3));
            Assert.False(db.InitializeRepoSeenIfUnset("p1", "other/repo", 0));
            Assert.Equal(3, db.GetRepoWatchState("p1", "other/repo").SeenMaxNumber);
            Assert.Equal("[]", db.GetRepoWatchState("p1", "other/repo").SnapshotJson);
        }

        [Fact]
        public void MarkRepoSeen_raises_the_mark_and_never_lowers_it()
        {
            using var db = new TaskDatabase();
            db.MarkRepoSeen("p1", "owner/repo", 10);
            db.MarkRepoSeen("p1", "owner/repo", 5);
            Assert.Equal(10, db.GetRepoWatchState("p1", "owner/repo").SeenMaxNumber);

            db.MarkRepoSeen("p1", "owner/repo", 11);
            Assert.Equal(11, db.GetRepoWatchState("p1", "owner/repo").SeenMaxNumber);

            // After a mark, initializing is a no-op.
            Assert.False(db.InitializeRepoSeenIfUnset("p1", "owner/repo", 2));
            Assert.Equal(11, db.GetRepoWatchState("p1", "owner/repo").SeenMaxNumber);
        }

        [Fact]
        public void MarkRepoSeen_creates_a_missing_row_and_sets_a_null_mark()
        {
            using var db = new TaskDatabase();
            db.MarkRepoSeen("p1", "owner/repo", 8);
            var created = db.GetRepoWatchState("p1", "owner/repo");
            Assert.NotNull(created);
            Assert.Equal(8, created.SeenMaxNumber);
            Assert.Null(created.LastCheckedAt);

            // A row from a fetch has a NULL mark; MAX with NULL must not leave it NULL.
            db.SaveRepoWatchFetch("p1", "other/repo", "e", T1, "ok", null, "[]");
            db.MarkRepoSeen("p1", "other/repo", 4);
            Assert.Equal(4, db.GetRepoWatchState("p1", "other/repo").SeenMaxNumber);
        }

        [Fact]
        public void States_are_scoped_by_project_and_deleted_per_project()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "b/repo", "e", T1, "ok", null, "[]");
            db.SaveRepoWatchFetch("p1", "a/repo", "e", T1, "ok", null, "[]");
            db.MarkRepoSeen("p2", "a/repo", 99);

            Assert.Equal(new[] { "a/repo", "b/repo" }, db.GetRepoWatchStates("p1").Select(s => s.Repo).ToArray());
            Assert.Null(db.GetRepoWatchState("p1", "a/repo").SeenMaxNumber); // p2's mark is not p1's
            Assert.Single(db.GetRepoWatchStates("p2"));

            Assert.Equal(2, db.DeleteRepoWatchStates("p1"));
            Assert.Empty(db.GetRepoWatchStates("p1"));
            Assert.Null(db.GetRepoWatchState("p1", "a/repo"));
            Assert.Equal(99, db.GetRepoWatchState("p2", "a/repo").SeenMaxNumber);
        }

        [Fact]
        public void DeleteExcept_keeps_the_listed_repos_case_insensitively_and_only_in_that_project()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "a/keep", "e", T1, "ok", null, "[]");
            db.SaveRepoWatchFetch("p1", "b/drop", "e", T1, "ok", null, "[]");
            db.SaveRepoWatchFetch("p1", "c/drop", "e", T1, "ok", null, "[]");
            db.SaveRepoWatchFetch("p2", "b/drop", "e", T1, "ok", null, "[]");

            Assert.Equal(2, db.DeleteRepoWatchStatesExcept("p1", new[] { " A/Keep " }));

            Assert.Equal(new[] { "a/keep" }, db.GetRepoWatchStates("p1").Select(s => s.Repo).ToArray());
            Assert.NotNull(db.GetRepoWatchState("p2", "b/drop"));
        }

        [Fact]
        public void DeleteExcept_with_nothing_to_keep_removes_nothing()
        {
            using var db = new TaskDatabase();
            db.SaveRepoWatchFetch("p1", "a/repo", "e", T1, "ok", null, "[]");

            Assert.Equal(0, db.DeleteRepoWatchStatesExcept("p1", Array.Empty<string>()));
            Assert.Equal(0, db.DeleteRepoWatchStatesExcept("p1", new[] { " " }));
            Assert.Equal(0, db.DeleteRepoWatchStatesExcept("p1", null!));
            Assert.Single(db.GetRepoWatchStates("p1"));
        }
    }
}
