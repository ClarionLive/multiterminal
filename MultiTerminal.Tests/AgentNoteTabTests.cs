using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Agent-written project notes (task 5d94ac91), through the REAL <see cref="TaskDatabase"/> on a
    /// temp SQLite file. The rule these pin: an agent never modifies a tab the Owner created, and
    /// may replace its own tab only while nobody else has written it since.
    /// </summary>
    public sealed class AgentNoteTabTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _project;

        public AgentNoteTabTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_agentnotes_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _project = Path.Combine(Path.GetTempPath(), $"mt-notes-project-{Guid.NewGuid():N}");
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        private static ProjectNoteTab Tab(TaskDatabase db, string project, string name)
            => db.GetProjectNoteTabDetails(project).SingleOrDefault(t => t.Name == name);

        [Fact]
        public void New_creates_an_agent_marked_tab_and_leaves_the_owner_tab_alone()
        {
            using var db = new TaskDatabase();
            db.SaveNoteTab(_project, "General", "owner text");

            var result = db.WriteAgentNoteTab(_project, "After redeploy", "1. relaunch from Deploy", "Alice", AgentNoteWriteMode.New);

            Assert.Equal(AgentNoteWriteOutcome.Created, result.Outcome);
            Assert.Equal("After redeploy", result.TabName);
            var written = Tab(db, _project, "After redeploy");
            Assert.Equal("Alice", written.CreatedBy);
            Assert.Equal("Alice", written.UpdatedBy);
            Assert.True(written.IsAgentWritten);
            var general = Tab(db, _project, "General");
            Assert.Null(general.CreatedBy);
            Assert.Equal("owner text", general.Content);
        }

        [Theory]
        [InlineData(AgentNoteWriteMode.Append)]
        [InlineData(AgentNoteWriteMode.Replace)]
        public void Append_and_replace_never_touch_an_owner_tab(AgentNoteWriteMode mode)
        {
            using var db = new TaskDatabase();
            db.SaveNoteTab(_project, "Todo", "owner list");

            var result = db.WriteAgentNoteTab(_project, "Todo", "agent text", "Alice", mode);

            Assert.Equal(AgentNoteWriteOutcome.Refused, result.Outcome);
            Assert.Contains("the Owner", result.Reason, StringComparison.Ordinal);
            var todo = Tab(db, _project, "Todo");
            Assert.Equal("owner list", todo.Content);
            Assert.Null(todo.CreatedBy);
            Assert.Null(todo.UpdatedBy);
        }

        [Fact]
        public void New_on_an_owner_name_adds_a_suffixed_tab_matched_case_insensitively()
        {
            using var db = new TaskDatabase();
            db.SaveNoteTab(_project, "Todo", "owner list");

            // "todo" must not become a sibling the Owner reads as the same tab.
            var result = db.WriteAgentNoteTab(_project, "todo", "agent text", "Alice", AgentNoteWriteMode.New);

            Assert.Equal(AgentNoteWriteOutcome.Created, result.Outcome);
            Assert.Equal("todo (2)", result.TabName);
            Assert.Equal("owner list", Tab(db, _project, "Todo").Content);
            Assert.Null(Tab(db, _project, "todo"));
            Assert.Equal("Alice", Tab(db, _project, "todo (2)").CreatedBy);
        }

        [Fact]
        public void New_on_an_own_name_is_refused_and_append_adds_to_it()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Test steps", "P1", "Alice", AgentNoteWriteMode.New);

            var again = db.WriteAgentNoteTab(_project, "Test steps", "P2", "Alice", AgentNoteWriteMode.New);
            Assert.Equal(AgentNoteWriteOutcome.Refused, again.Outcome);
            Assert.Contains("append", again.Reason, StringComparison.Ordinal);

            var appended = db.WriteAgentNoteTab(_project, "Test steps", "P2", "Alice", AgentNoteWriteMode.Append);
            Assert.Equal(AgentNoteWriteOutcome.Appended, appended.Outcome);
            Assert.Equal("P1\n\nP2", Tab(db, _project, "Test steps").Content);
            Assert.Equal(2, db.GetProjectNoteTabDetails(_project).Count); // General + Test steps, no duplicates
        }

        [Fact]
        public void Another_agents_tab_is_not_yours()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Deploy", "Bob's steps", "Bob", AgentNoteWriteMode.New);

            Assert.Equal(AgentNoteWriteOutcome.Refused,
                db.WriteAgentNoteTab(_project, "Deploy", "x", "Alice", AgentNoteWriteMode.Append).Outcome);
            Assert.Equal(AgentNoteWriteOutcome.Refused,
                db.WriteAgentNoteTab(_project, "Deploy", "x", "Alice", AgentNoteWriteMode.Replace).Outcome);
            Assert.Equal("Bob's steps", Tab(db, _project, "Deploy").Content);

            var separate = db.WriteAgentNoteTab(_project, "Deploy", "Alice's steps", "Alice", AgentNoteWriteMode.New);
            Assert.Equal("Deploy (2)", separate.TabName);
        }

        [Fact]
        public void Replace_works_until_the_owner_edits_the_tab_then_only_append_does()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Reboot", "v1", "Alice", AgentNoteWriteMode.New);

            var replaced = db.WriteAgentNoteTab(_project, "Reboot", "v2", "alice", AgentNoteWriteMode.Replace);
            Assert.Equal(AgentNoteWriteOutcome.Replaced, replaced.Outcome);
            Assert.Equal("v2", Tab(db, _project, "Reboot").Content);

            db.SaveNoteTab(_project, "Reboot", "v2 plus owner remark"); // the Notes pane's save
            var afterOwner = Tab(db, _project, "Reboot");
            Assert.Equal("Alice", afterOwner.CreatedBy); // still marked as the agent's tab
            Assert.Null(afterOwner.UpdatedBy);

            var refused = db.WriteAgentNoteTab(_project, "Reboot", "v3", "Alice", AgentNoteWriteMode.Replace);
            Assert.Equal(AgentNoteWriteOutcome.Refused, refused.Outcome);
            Assert.Equal("v2 plus owner remark", Tab(db, _project, "Reboot").Content);

            var appended = db.WriteAgentNoteTab(_project, "Reboot", "v3", "Alice", AgentNoteWriteMode.Append);
            Assert.Equal(AgentNoteWriteOutcome.Appended, appended.Outcome);
            Assert.Equal("v2 plus owner remark\n\nv3", Tab(db, _project, "Reboot").Content);
        }

        [Fact]
        public void Migration_adds_authorship_to_an_existing_table_and_reads_old_rows_as_the_owners()
        {
            using (var raw = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                raw.Open();
                using var create = new SQLiteCommand(@"
                    CREATE TABLE project_note_tabs (
                        id TEXT PRIMARY KEY, project_path TEXT NOT NULL, tab_name TEXT NOT NULL,
                        content TEXT DEFAULT '', tab_order INTEGER NOT NULL DEFAULT 0,
                        is_default INTEGER NOT NULL DEFAULT 0,
                        created_at DATETIME DEFAULT (datetime('now')), updated_at DATETIME DEFAULT (datetime('now')),
                        UNIQUE(project_path, tab_name));", raw);
                create.ExecuteNonQuery();
                using var insert = new SQLiteCommand(
                    "INSERT INTO project_note_tabs (id, project_path, tab_name, content, tab_order, is_default) VALUES ('a1', @p, 'General', 'old', 0, 1)", raw);
                insert.Parameters.AddWithValue("@p", TaskDatabase.CanonicalNoteTabKey(_project));
                insert.ExecuteNonQuery();
            }
            SQLiteConnection.ClearAllPools();

            using var db = new TaskDatabase();
            var general = Assert.Single(db.GetProjectNoteTabDetails(_project));
            Assert.Equal("old", general.Content);
            Assert.Null(general.CreatedBy);
            Assert.Equal(AgentNoteWriteOutcome.Refused,
                db.WriteAgentNoteTab(_project, "General", "x", "Alice", AgentNoteWriteMode.Append).Outcome);
        }
    }
}
