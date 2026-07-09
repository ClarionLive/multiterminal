using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression tests for ticket 75a612dd (rework cycle): the live "set default terminal"
    /// card dropdown. Unlike <c>status</c> (DB-only), <c>DefaultTerminal</c> is serialized to
    /// <c>.claude/project.json</c>, so a DB-only write is silently reverted the next time
    /// ChangelogService / VersioningService does <c>LoadProject → SaveProject</c> and the stale
    /// on-disk value re-asserts through <see cref="ProjectDatabase.SaveRichProject"/>'s COALESCE
    /// (which can't guard a non-null field). <see cref="ProjectService.SetDefaultTerminal"/> must
    /// therefore dual-write DB + project.json — these tests prove it does, and that the choice
    /// survives the exact json-shaped re-save that would otherwise revert it.
    /// (Found by the pipeline: code-reviewer MAJOR + cross-model adversary HIGH.)
    /// </summary>
    public sealed class ProjectServiceTerminalDurabilityTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _projectDir;
        private readonly ProjectDatabase _projectDb;
        private readonly ProjectService _service;

        public ProjectServiceTerminalDurabilityTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_term_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _projectDb = new ProjectDatabase();
            _service = new ProjectService(_projectDb);
            _projectDir = Path.Combine(Path.GetTempPath(), $"mt_termproj_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_projectDir);
        }

        public void Dispose()
        {
            _service?.Dispose();   // disposes the shared ProjectDatabase too
            _projectDb?.Dispose(); // idempotent second dispose (satisfies CA2213 for the owned field)
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            foreach (var side in new[] { _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(side)) File.Delete(side);
            }
            if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        // Registers a project (DB + .claude/project.json) with a known starting terminal.
        private Project SeedProject(string startTerminal)
        {
            var project = Project.Create("TermDurProj", _projectDir);
            project.DefaultTerminal = startTerminal;
            _service.SaveProject(project); // writes .claude/project.json AND upserts SQLite
            return project;
        }

        [Fact]
        public void SetDefaultTerminal_WritesBothDbAndProjectJson()
        {
            var project = SeedProject("claude-code");

            _service.SetDefaultTerminal(project.Id, "codex");

            // DB store
            Assert.Equal("codex", _projectDb.GetRichProject(project.Id).DefaultTerminal);
            // Portable project.json store (this is the sync that prevents the revert)
            Assert.Equal("codex", _service.LoadProject(_projectDir).DefaultTerminal);
        }

        [Fact]
        public void SetDefaultTerminal_SurvivesProjectJsonReSave()
        {
            // This reproduces the reverting caller: ChangelogService/VersioningService do
            // LoadProject(fromJson) → SaveProject(→ SaveRichProject). Before the fix, the stale
            // project.json still said "claude-code" and this sequence clobbered the DB back.
            var project = SeedProject("claude-code");
            _service.SetDefaultTerminal(project.Id, "codex");

            var reloaded = _service.LoadProject(_projectDir); // reads project.json (now synced to codex)
            _service.SaveProject(reloaded);                   // the revert-shaped re-save

            Assert.Equal("codex", _projectDb.GetRichProject(project.Id).DefaultTerminal);
        }

        [Theory]
        [InlineData("codex", "codex")]
        [InlineData("CODEX", "codex")]        // case-insensitive
        [InlineData("claude-code", "claude-code")]
        [InlineData("nonsense", "claude-code")] // unrecognized → default
        public void SetDefaultTerminal_NormalizesValue(string input, string expected)
        {
            var project = SeedProject("claude-code");

            _service.SetDefaultTerminal(project.Id, input);

            Assert.Equal(expected, _projectDb.GetRichProject(project.Id).DefaultTerminal);
        }
    }
}
