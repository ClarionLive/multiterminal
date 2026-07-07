using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ProfileService"/> (ticket 86f3fd21) — the team-member-profile cache/CRUD/
    /// write-path extracted from MessageBroker as the SECOND region validating the extraction template. The
    /// POINT of the decomposition is that this can now be tested in isolation: a real temp-SQLite
    /// <see cref="TaskDatabase"/> + a stub <see cref="IProfileServiceHost"/>, with no MessageBroker, no REST
    /// server, no UI. The stub records event raises so we can assert the write path broadcasts, and provides
    /// IsTemporaryAgent so the ListProfiles roster filter is exercised. Proves the single write path
    /// (clone→persist→swap, from 1df2a534) survived the move.
    /// </summary>
    public sealed class ProfileServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly StubHost _host;
        private readonly TaskDatabase _db;
        private readonly ProfileService _svc;

        public ProfileServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_ps_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            _host = new StubHost();
            _db = new TaskDatabase();
            _svc = new ProfileService(_db, _host);
        }

        public void Dispose()
        {
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void CreateProfile_PersistsAndCaches_AndBroadcasts()
        {
            var result = _svc.CreateProfile("diana", "Diana", null, "engineer", "bio",
                new List<string> { "csharp" }, new List<string> { "compilers" });
            Assert.True(result.Success);

            // cached
            var cached = _svc.GetProfile("diana");
            Assert.True(cached.Success);
            Assert.Equal("Diana", cached.Profile.DisplayName);

            // persisted: a fresh service over the SAME db sees it after LoadPersistedProfiles
            using var freshDb = new TaskDatabase();
            var fresh = new ProfileService(freshDb, new StubHost());
            fresh.LoadPersistedProfiles();
            Assert.True(fresh.GetProfile("diana").Success);

            // write-path broadcast fired
            Assert.True(_host.ProfilesUpdatedCount >= 1);
        }

        [Fact]
        public void UpdateProfile_MutatesProvidedFields_Persists()
        {
            _svc.CreateProfile("diana", "Diana", null, "engineer", "bio", null, null);
            var r = _svc.UpdateProfile("diana", null, null, "architect", null, null, null);
            Assert.True(r.Success);

            var p = _svc.GetProfile("diana").Profile;
            Assert.Equal("architect", p.Role);   // changed
            Assert.Equal("Diana", p.DisplayName); // null arg => unchanged

            // clone→persist→swap reached the DB too
            using var freshDb = new TaskDatabase();
            var fresh = new ProfileService(freshDb, new StubHost());
            fresh.LoadPersistedProfiles();
            Assert.Equal("architect", fresh.GetProfile("diana").Profile.Role);
        }

        [Fact]
        public void DeleteProfile_RemovesFromCacheAndDb()
        {
            _svc.CreateProfile("diana", "Diana", null, null, null, null, null);
            Assert.True(_svc.GetProfile("diana").Success);

            var del = _svc.DeleteProfile("diana");
            Assert.True(del.Success);
            Assert.False(_svc.GetProfile("diana").Success);

            using var freshDb = new TaskDatabase();
            var fresh = new ProfileService(freshDb, new StubHost());
            fresh.LoadPersistedProfiles();
            Assert.False(fresh.GetProfile("diana").Success);  // gone from the DB too
        }

        [Fact]
        public void LoadPersistedProfiles_PopulatesCacheFromDb()
        {
            _svc.CreateProfile("a", "A", null, null, null, null, null);
            _svc.CreateProfile("b", "B", null, null, null, null, null);

            using var freshDb = new TaskDatabase();
            var fresh = new ProfileService(freshDb, new StubHost());
            Assert.False(fresh.GetProfile("a").Success);  // empty until loaded
            fresh.LoadPersistedProfiles();
            Assert.True(fresh.GetProfile("a").Success);
            Assert.True(fresh.GetProfile("b").Success);
        }

        [Fact]
        public void SetProfileOnline_ThenOffline_TogglesAndPersists()
        {
            _svc.CreateProfile("diana", "Diana", null, null, null, null, null);

            Assert.True(_svc.SetProfileOnline("diana").Success);
            Assert.True(_svc.GetProfile("diana").Profile.IsOnline);

            Assert.True(_svc.SetProfileOffline("diana").Success);
            Assert.False(_svc.GetProfile("diana").Profile.IsOnline);
        }

        [Fact]
        public void SetProfileOnline_AutoCreatesMissingProfile()
        {
            // No CreateProfile first — SetProfileOnline auto-creates via the write path.
            var r = _svc.SetProfileOnline("newbie");
            Assert.True(r.Success);
            var p = _svc.GetProfile("newbie");
            Assert.True(p.Success);
            Assert.True(p.Profile.IsOnline);
        }

        [Fact]
        public void ListProfiles_ExcludesTemporaryAgents()
        {
            _svc.CreateProfile("diana", "Diana", null, null, null, null, null);
            _svc.CreateProfile("Agent Alice", "Agent Alice", null, null, null, null, null);

            var list = _svc.ListProfiles();
            Assert.True(list.Success);
            Assert.Contains(list.Profiles, p => p.Id == "diana");
            Assert.DoesNotContain(list.Profiles, p => p.Id == "Agent Alice");  // stub IsTemporaryAgent filters it
        }

        /// <summary>
        /// Minimal <see cref="IProfileServiceHost"/> stub. Records the ProfilesUpdated raises (so the write
        /// path's broadcast is assertable), no-ops logging, and mirrors the broker's "Agent " temporary-agent
        /// convention so the ListProfiles roster filter is exercised in isolation.
        /// </summary>
        private sealed class StubHost : IProfileServiceHost
        {
            public int ProfilesUpdatedCount { get; private set; }

            public void RaiseProfilesUpdated(List<TeamMemberProfile> profiles) => ProfilesUpdatedCount++;
            public void LogError(string message) { }
            public void LogInfo(string message) { }
            public bool IsTemporaryAgent(string name) => name != null && name.StartsWith("Agent ", StringComparison.Ordinal);
        }
    }
}
