using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for ticket 75a612dd: the project lifecycle <c>status</c> column
    /// (Active/Parked/Archived). Verifies the round-trip through
    /// <see cref="ProjectDatabase.SaveRichProject"/> / <see cref="ProjectDatabase.GetRichProject"/>,
    /// the default (null → "Active"), normalization of arbitrary/case-varying strings, and the
    /// COALESCE-preservation invariant — a project.json-shaped re-save that leaves Status null
    /// must NOT clobber an existing Parked/Archived value (the team_lead precedent).
    /// </summary>
    public sealed class ProjectDatabaseStatusTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly ProjectDatabase _projectDb;

        public ProjectDatabaseStatusTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_status_{Guid.NewGuid():N}.db");
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
        public void NewProject_DefaultsToActive()
        {
            // Create() does not set Status → stored NULL → read coerces to "Active".
            var project = MultiTerminal.Models.Project.Create("StatusDefaultProj", @"C:\temp\statusdefault");
            _projectDb.SaveRichProject(project);

            var after = _projectDb.GetRichProject(project.Id);
            Assert.Equal("Active", after.Status);
        }

        [Theory]
        [InlineData("Parked", "Parked")]
        [InlineData("Archived", "Archived")]
        [InlineData("Active", "Active")]
        [InlineData("parked", "Parked")]     // case-insensitive
        [InlineData("ARCHIVED", "Archived")] // case-insensitive
        [InlineData("nonsense", "Active")]   // unrecognized → Active
        public void SaveAndRead_NormalizesStatus(string input, string expected)
        {
            var project = MultiTerminal.Models.Project.Create("StatusRoundTripProj", @"C:\temp\statusroundtrip");
            project.Status = input;
            _projectDb.SaveRichProject(project);

            var after = _projectDb.GetRichProject(project.Id);
            Assert.Equal(expected, after.Status);
        }

        [Fact]
        public void ReSaveWithNullStatus_PreservesExistingStatus()
        {
            // User parks the project via the Edit dialog...
            var project = MultiTerminal.Models.Project.Create("StatusCoalesceProj", @"C:\temp\statuscoalesce");
            project.Status = "Parked";
            _projectDb.SaveRichProject(project);
            Assert.Equal("Parked", _projectDb.GetRichProject(project.Id).Status);

            // ...then some other path re-saves from a project.json that lacks the SQLite-only
            // status field (Status left null). The COALESCE must preserve "Parked".
            var jsonShaped = new MultiTerminal.Models.Project
            {
                Id = project.Id,
                Name = project.Name,
                Path = project.Path,
                SourcePath = project.SourcePath,
                Status = null // project.json carries no status
            };
            _projectDb.SaveRichProject(jsonShaped);

            var after = _projectDb.GetRichProject(project.Id);
            Assert.Equal("Parked", after.Status); // NOT reverted to Active
        }

        [Fact]
        public void GetAllRichProjects_RoundTripsStatus()
        {
            var parked = MultiTerminal.Models.Project.Create("ParkedProj", @"C:\temp\parked");
            parked.Status = "Parked";
            _projectDb.SaveRichProject(parked);

            var archived = MultiTerminal.Models.Project.Create("ArchivedProj", @"C:\temp\archived");
            archived.Status = "Archived";
            _projectDb.SaveRichProject(archived);

            var all = _projectDb.GetAllRichProjects();
            Assert.Equal("Parked", Assert.Single(all, p => p.Id == parked.Id).Status);
            Assert.Equal("Archived", Assert.Single(all, p => p.Id == archived.Id).Status);
        }
    }
}
