using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;
using FileProject = MultiTerminal.Models.Project;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c, observed live 2026-10-03: New Project on a folder whose project.json held
    /// <c>{ this is not json ]]</c> hung the UI thread forever. ProjectService's hand-written parser had
    /// loops that made no progress on a token they had no rule for. Every parse here runs on a
    /// pool thread under a deadline, so a parser that spins FAILS the test instead of hanging the run.
    /// </summary>
    public sealed class ProjectJsonMalformedInputTests : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _root;

        public ProjectJsonMalformedInputTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_pjson_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_pjson_msg_{stamp}.db");
            _root = Path.Combine(Path.GetTempPath(), $"mt_pjson_dir_{stamp}");
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        private string FolderWithProjectJson(string name, string json)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(folder, ".claude"));
            File.WriteAllText(Path.Combine(folder, ".claude", "project.json"), json);
            return folder;
        }

        // WhenAny against a bare Task.Delay (the b840ddee dialect). The await after the check is on a task
        // already known to be complete, so it cannot hang.
        private static async Task<T> WithinDeadline<T>(Func<T> work)
        {
            var task = Task.Run(work);
            var winner = await Task.WhenAny(task, Task.Delay(Deadline)).ConfigureAwait(false);
            Assert.True(winner == task, $"did not return within {Deadline.TotalSeconds:0} s: the parser stopped making progress");
            return await task.ConfigureAwait(false);
        }

        // Each of these hung or half-parsed before the fix. [{{}}] is the input that separates "nested
        // helpers just return" from "nested helpers abort the parse": with the former the outer array
        // re-enters at the stalled '{' and the main loop exits cleanly on a '}'.
        [Theory]
        [InlineData("{ this is not json ]]")]
        [InlineData("{x}")]
        [InlineData("{\"id\":\"a\", x}")]
        [InlineData("{\"prompts\":[1,2]}")]
        [InlineData("{\"team\":{x}}")]
        [InlineData("{\"prompts\":[{x}]}")]
        [InlineData("{")]
        [InlineData("{\"id\":")]
        [InlineData("{\"id\":\"abc\"")]
        [InlineData("{\"hooks\":[{]}")]
        [InlineData("{\"prompts\":[{{}}]}")]
        [InlineData("{\"prompts\":[nope]}")]
        [InlineData("{\"prompts\":[}]}")]
        [InlineData("{\"team\":{\"agents\":[nope]}}")]
        [InlineData("{\"team\":{\"agents\":[}]}}")]
        public async Task Malformed_project_json_loads_as_null_within_the_deadline(string json)
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            string folder = FolderWithProjectJson("garbage", json);

            var project = await WithinDeadline(() => service.LoadProject(folder));

            Assert.Null(project);
        }

        // The save path's own output, with every value shape it writes: escapes, control characters,
        // a null, booleans, two prompts and a team.
        [Fact]
        public async Task A_project_json_written_by_SaveProject_parses_back_unchanged()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            string folder = Path.Combine(_root, "roundtrip");
            Directory.CreateDirectory(folder);
            var saved = FileProject.Create("Round \"Trip\"", folder);
            saved.Id = "rt0001";
            saved.Description = "line1\nline2\ttab \\ back \u0001 ctl é";
            saved.ChangeLog = null;
            saved.IsPinned = true;
            saved.Prompts = new List<Prompt>
            {
                new Prompt { Id = "p1", Category = "cat", Description = "first", Text = "say \"hi\"", IsGlobal = true, CreatedAt = new DateTime(2025, 1, 2, 3, 4, 5) },
                new Prompt { Id = "p2", Category = null, Description = "second", Text = "{ not [ json", IsGlobal = false, CreatedAt = new DateTime(2025, 6, 7, 8, 9, 10) },
            };
            saved.TeamAgents = new List<string> { "Alice", "Bob" };
            service.SaveProject(saved);

            var loaded = await WithinDeadline(() => service.LoadProject(folder));

            Assert.NotNull(loaded);
            Assert.Equal("rt0001", loaded.Id);
            Assert.Equal("Round \"Trip\"", loaded.Name);
            Assert.Equal(saved.Description, loaded.Description);
            Assert.Null(loaded.ChangeLog);
            Assert.True(loaded.IsPinned);
            Assert.Equal(new[] { "p1", "p2" }, loaded.Prompts.Select(p => p.Id));
            Assert.Equal("say \"hi\"", loaded.Prompts[0].Text);
            Assert.True(loaded.Prompts[0].IsGlobal);
            Assert.Null(loaded.Prompts[1].Category);
            Assert.Equal("{ not [ json", loaded.Prompts[1].Text);
            Assert.False(loaded.Prompts[1].IsGlobal);
            Assert.Equal(new DateTime(2025, 6, 7, 8, 9, 10), loaded.Prompts[1].CreatedAt);
            Assert.Equal(new[] { "Alice", "Bob" }, loaded.TeamAgents);
        }

        // Hand-written shapes the save path does not produce but older or hand-edited files carry: hooks
        // with nested objects and arrays, unknown keys, a legacy sourceControlAccountId, nulls in a string
        // array. These parsed before the fix and must parse the same after it.
        [Fact]
        public async Task Valid_hand_written_shapes_still_parse()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            string json =
                "{\n" +
                "  \"id\": \"hw0001\",\n" +
                "  \"name\": \"Hand\",\n" +
                "  \"description\": null,\n" +
                "  \"sourceControlAccountId\": \"acct-1\",\n" +
                "  \"hooks\": { \"PreToolUse\": [ { \"matcher\": \"Bash\", \"hooks\": [ { \"type\": \"command\", \"command\": \"x ]}\" } ] } ] },\n" +
                "  \"extra\": [ 1, [ 2, { \"a\": [ ] } ], \"s\" ],\n" +
                "  \"version\": 3,\n" +
                "  \"isPinned\": false,\n" +
                "  \"prompts\": [ ],\n" +
                "  \"team\": { \"lead\": \"Alice\", \"agents\": [ \"Alice\", null, \"Bob\" ] }\n" +
                "}\n";
            string folder = FolderWithProjectJson("handwritten", json);

            var loaded = await WithinDeadline(() => service.LoadProject(folder));

            Assert.NotNull(loaded);
            Assert.Equal("hw0001", loaded.Id);
            Assert.Equal("Hand", loaded.Name);
            Assert.Null(loaded.Description);
            Assert.Null(loaded.SourceControlAccountId);
            Assert.False(loaded.IsPinned);
            Assert.Empty(loaded.Prompts);
            Assert.Equal(new[] { "Alice", "Bob" }, loaded.TeamAgents);
        }

        // The live path: New Project's existence check on a folder holding the exact garbage file.
        [Fact]
        public async Task FindExistingProjectAtPath_reports_the_live_garbage_file_as_unreadable_and_leaves_it_alone()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            string folder = FolderWithProjectJson("live-case", "{ this is not json ]]");
            string file = Path.Combine(folder, ".claude", "project.json");
            byte[] before = File.ReadAllBytes(file);

            var match = await WithinDeadline(() => broker.FindExistingProjectAtPath(folder));

            Assert.True(match.Exists);
            Assert.Equal(ExistingProjectProblem.UnreadableProjectFile, match.Problem);
            Assert.Equal(before, File.ReadAllBytes(file));
        }
    }
}
