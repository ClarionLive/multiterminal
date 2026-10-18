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
    /// The Notes rail's stored state (task 9a4a9d5e): pinning, the pinned group's manual order, the shared
    /// category list, and the rule that agents may only file under an existing category. Runs against the
    /// REAL <see cref="TaskDatabase"/> on a temp SQLite file.
    /// </summary>
    public sealed class NoteRailTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _msgDbPath;
        private readonly string _project;

        public NoteRailTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_noterail_{Guid.NewGuid():N}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_noterail_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
            _project = Path.Combine(Path.GetTempPath(), $"mt-rail-project-{Guid.NewGuid():N}");
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

        private static ProjectNoteTab Tab(TaskDatabase db, string project, string name)
            => db.GetProjectNoteTabDetails(project).Single(t => t.Name == name);

        private static string[] PinnedInOrder(TaskDatabase db, string project)
            => db.GetProjectNoteTabDetails(project).Where(t => t.IsPinned).OrderBy(t => t.Order).Select(t => t.Name).ToArray();

        // ── Upgrade ──

        /// <summary>
        /// Owner decision: on upgrade, every note the Owner wrote is pinned in today's tab order, and agent
        /// notes are not. Every note starts in General. Built on a pre-9a4a9d5e table (no pinned or
        /// category_id column), so the migration is what is under test.
        /// </summary>
        [Fact]
        public void Upgrade_pins_the_owners_notes_in_their_order_and_leaves_agent_notes_unpinned_in_General()
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
                        created_by TEXT, updated_by TEXT,
                        agent_rev INTEGER NOT NULL DEFAULT 0, owner_read_rev INTEGER NOT NULL DEFAULT 0,
                        UNIQUE(project_path, tab_name));", raw);
                create.ExecuteNonQuery();
                using var insert = new SQLiteCommand(@"
                    INSERT INTO project_note_tabs (id, project_path, tab_name, content, tab_order, is_default, created_by, updated_by)
                    VALUES ('a1', @p, 'General', 'x', 0, 1, NULL, NULL),
                           ('a2', @p, 'Agent note', 'y', 1, 0, 'Alice', 'Alice'),
                           ('a3', @p, 'Todo', 'z', 2, 0, NULL, NULL),
                           ('a4', @p, 'Deploy', 'w', 3, 0, NULL, NULL)", raw);
                insert.Parameters.AddWithValue("@p", TaskDatabase.CanonicalNoteTabKey(_project));
                insert.ExecuteNonQuery();
            }
            SQLiteConnection.ClearAllPools();

            using var db = new TaskDatabase();

            Assert.Equal(new[] { "General", "Todo", "Deploy" }, PinnedInOrder(db, _project));
            Assert.False(Tab(db, _project, "Agent note").IsPinned);
            Assert.All(db.GetProjectNoteTabDetails(_project), t => Assert.Equal(NoteCategory.General, t.Category));
        }

        // ── Pinning and the manual order ──

        [Fact]
        public void A_new_pin_goes_to_the_end_of_the_pinned_group_and_reorder_only_moves_pinned_notes()
        {
            using var db = new TaskDatabase();
            db.SaveNoteTab(_project, "A", "a");
            db.SaveNoteTab(_project, "B", "b");
            db.WriteAgentNoteTab(_project, "C", "c", "Alice", AgentNoteWriteMode.New);
            Assert.True(db.SetNoteTabPinned(_project, "B", true));
            Assert.True(db.SetNoteTabPinned(_project, "A", true));
            Assert.False(db.SetNoteTabPinned(_project, "A", true)); // already pinned: no change reported
            Assert.Equal(new[] { "B", "A" }, PinnedInOrder(db, _project));

            int unpinnedOrderBefore = Tab(db, _project, "C").Order;
            db.ReorderNoteTabs(_project, new System.Collections.Generic.List<string> { "C", "A", "B" });

            Assert.Equal(new[] { "A", "B" }, PinnedInOrder(db, _project));
            Assert.Equal(unpinnedOrderBefore, Tab(db, _project, "C").Order);

            Assert.True(db.SetNoteTabPinned(_project, "A", false));
            Assert.Equal(new[] { "B" }, PinnedInOrder(db, _project));
        }

        [Fact]
        public void An_agents_note_is_never_pinned()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Steps", "x", "Alice", AgentNoteWriteMode.New);
            db.WriteAgentNoteTab(_project, "Steps", "y", "Alice", AgentNoteWriteMode.Append);

            Assert.False(Tab(db, _project, "Steps").IsPinned);
        }

        // ── The shared category list ──

        [Fact]
        public void Categories_are_one_list_without_near_duplicates_and_General_is_built_in()
        {
            using var db = new TaskDatabase();

            var live = db.AddNoteCategory("Live test", out bool created);
            Assert.True(created);
            var again = db.AddNoteCategory("  live TEST ", out bool createdAgain);
            Assert.False(createdAgain);
            Assert.Equal(live.Id, again.Id);
            Assert.Equal("Live test", again.Name);

            var general = db.AddNoteCategory("general", out bool createdGeneral);
            Assert.False(createdGeneral);
            Assert.Null(general.Id);
            Assert.Null(db.AddNoteCategory("   ", out _));
            Assert.Null(db.AddNoteCategory(new string('x', TaskDatabase.NoteCategoryMaxLength + 1), out _));

            db.AddNoteCategory("Deploy steps", out _);
            Assert.Equal(new[] { "General", "Deploy steps", "Live test" }, db.ListNoteCategories().Select(c => c.Name).ToArray());
        }

        [Fact]
        public void Rename_refuses_General_and_a_clash_and_delete_moves_notes_to_General_in_every_project()
        {
            using var db = new TaskDatabase();
            string other = _project + "-other";
            db.AddNoteCategory("Live test", out _);
            db.AddNoteCategory("Deploy", out _);
            db.SaveNoteTab(_project, "Here", "x");
            db.SaveNoteTab(other, "There", "y");
            Assert.True(db.SetNoteTabCategory(_project, "Here", "live test"));
            Assert.True(db.SetNoteTabCategory(other, "There", "Live test"));
            Assert.Equal("Live test", Tab(db, _project, "Here").Category);

            Assert.False(db.RenameNoteCategory("Live test", "deploy"));   // another category has that name
            Assert.False(db.RenameNoteCategory("Live test", "General"));
            Assert.False(db.RenameNoteCategory("General", "Anything"));
            Assert.True(db.RenameNoteCategory("Live test", "Live tests"));
            Assert.Equal("Live tests", Tab(db, other, "There").Category); // a rename reaches every note at once

            Assert.True(db.DeleteNoteCategory("live tests"));

            Assert.Equal(NoteCategory.General, Tab(db, _project, "Here").Category);
            Assert.Equal(NoteCategory.General, Tab(db, other, "There").Category);
            Assert.DoesNotContain(db.ListNoteCategories(), c => c.Name == "Live tests");
            // The model reads a dangling id as General too, so check the rows themselves were moved.
            using var raw = new SQLiteConnection($"Data Source={_testDbPath};Version=3;");
            raw.Open();
            using var count = new SQLiteCommand("SELECT COUNT(*) FROM project_note_tabs WHERE category_id IS NOT NULL", raw);
            Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Filing_under_an_unknown_category_changes_nothing()
        {
            using var db = new TaskDatabase();
            db.AddNoteCategory("Live test", out _);
            db.SaveNoteTab(_project, "Here", "x");
            Assert.True(db.SetNoteTabCategory(_project, "Here", "Live test"));

            Assert.False(db.SetNoteTabCategory(_project, "Here", "No such category"));

            Assert.Equal("Live test", Tab(db, _project, "Here").Category);
            Assert.Equal(new[] { "General", "Live test" }, db.ListNoteCategories().Select(c => c.Name).ToArray());
        }

        // ── Agents and categories ──

        [Fact]
        public void An_agent_files_a_new_note_under_an_existing_category_and_an_unknown_one_lands_in_General()
        {
            using var db = new TaskDatabase();
            db.AddNoteCategory("Live test", out _);

            var known = db.WriteAgentNoteTab(_project, "Known", "x", "Alice", AgentNoteWriteMode.New, "live test");
            var unknown = db.WriteAgentNoteTab(_project, "Unknown", "x", "Alice", AgentNoteWriteMode.New, "Made up");
            var omitted = db.WriteAgentNoteTab(_project, "Omitted", "x", "Alice", AgentNoteWriteMode.New);

            Assert.Equal("Live test", known.Category);
            Assert.Null(known.CategoryNote);
            Assert.Equal("Live test", Tab(db, _project, "Known").Category);

            Assert.True(unknown.Succeeded);
            Assert.Equal(NoteCategory.General, unknown.Category);
            Assert.Contains("Unknown category 'Made up'", unknown.CategoryNote, StringComparison.Ordinal);
            Assert.Contains("Live test", unknown.CategoryNote, StringComparison.Ordinal); // tells the agent what exists
            Assert.Equal(NoteCategory.General, Tab(db, _project, "Unknown").Category);
            Assert.DoesNotContain(db.ListNoteCategories(), c => c.Name == "Made up"); // agents never add one

            Assert.Equal(NoteCategory.General, omitted.Category);
            Assert.Null(omitted.CategoryNote);
        }

        /// <summary>
        /// A category is applied only when the note is created: an agent's later append must not undo the
        /// Owner refiling the note.
        /// </summary>
        [Fact]
        public void An_agents_append_keeps_the_category_the_owner_filed_the_note_under()
        {
            using var db = new TaskDatabase();
            db.AddNoteCategory("Live test", out _);
            db.AddNoteCategory("Deploy", out _);
            db.WriteAgentNoteTab(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New, "Live test");
            Assert.True(db.SetNoteTabCategory(_project, "Steps", "Deploy")); // the Owner refiles it

            var appended = db.WriteAgentNoteTab(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append, "Live test");

            Assert.Equal(AgentNoteWriteOutcome.Appended, appended.Outcome);
            Assert.Equal("Deploy", appended.Category);
            Assert.NotNull(appended.CategoryNote);
            Assert.Equal("Deploy", Tab(db, _project, "Steps").Category);
        }
    }
}
