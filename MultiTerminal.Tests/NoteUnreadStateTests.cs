using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
using MultiTerminal.Controls;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unread agent notes (task c133b27d): the stored read state, the Notes renderer's host-side count,
    /// and the HUD tab badge. Runs against the REAL <see cref="TaskDatabase"/> on a temp SQLite file.
    /// </summary>
    public sealed class NoteUnreadStateTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _msgDbPath;
        private readonly string _project;

        public NoteUnreadStateTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_noteunread_{Guid.NewGuid():N}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_noteunread_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
            _project = Path.Combine(Path.GetTempPath(), $"mt-unread-project-{Guid.NewGuid():N}");
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

        // ── Stored read state ──

        [Fact]
        public void An_agent_note_is_unread_and_an_owner_tab_never_counts()
        {
            using var db = new TaskDatabase();
            db.SaveNoteTab(_project, "General", "owner text");
            db.SaveNoteTab(_project, "Todo", "owner list");
            Assert.Equal(0, db.CountUnreadNoteTabs(_project));

            db.WriteAgentNoteTab(_project, "After redeploy", "steps", "Alice", AgentNoteWriteMode.New);

            Assert.Equal(1, db.CountUnreadNoteTabs(_project));
            Assert.True(Tab(db, _project, "After redeploy").IsUnread);
            Assert.False(Tab(db, _project, "General").IsUnread);
        }

        [Fact]
        public void The_count_is_per_project()
        {
            using var db = new TaskDatabase();
            string other = _project + "-other";
            db.WriteAgentNoteTab(other, "Elsewhere", "x", "Alice", AgentNoteWriteMode.New);

            Assert.Equal(0, db.CountUnreadNoteTabs(_project));
            Assert.Equal(1, db.CountUnreadNoteTabs(other));
        }

        /// <summary>
        /// The reason read state is a revision counter, not a timestamp: these writes all land inside the
        /// same second, so <c>updated_at</c> cannot order them. Mark-read uses the revision the pane
        /// DISPLAYED, so the append it never showed stays unread.
        /// </summary>
        [Fact]
        public void An_append_the_pane_has_not_shown_stays_unread_after_marking_read()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New);
            long displayed = Tab(db, _project, "Steps").AgentRev;
            db.WriteAgentNoteTab(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append); // reaches the DB first

            Assert.False(db.MarkNoteTabRead(_project, "Steps", displayed));

            Assert.True(Tab(db, _project, "Steps").IsUnread);
            Assert.Equal(1, db.CountUnreadNoteTabs(_project));

            Assert.True(db.MarkNoteTabRead(_project, "Steps", Tab(db, _project, "Steps").AgentRev));
            Assert.Equal(0, db.CountUnreadNoteTabs(_project));
        }

        [Fact]
        public void A_late_stale_mark_read_does_not_unread_a_newer_one_and_an_inflated_rev_cannot_pre_read()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New);
            db.WriteAgentNoteTab(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append);
            long current = Tab(db, _project, "Steps").AgentRev;
            Assert.Equal(2, current);

            // A rev beyond what exists is clamped to the stored one, so it cannot read the NEXT write in advance.
            Assert.True(db.MarkNoteTabRead(_project, "Steps", current + 50));
            Assert.Equal(current, Tab(db, _project, "Steps").OwnerReadRev);
            db.WriteAgentNoteTab(_project, "Steps", "three", "Alice", AgentNoteWriteMode.Append);
            Assert.True(Tab(db, _project, "Steps").IsUnread);

            long third = Tab(db, _project, "Steps").AgentRev;
            Assert.True(db.MarkNoteTabRead(_project, "Steps", third));
            Assert.False(db.MarkNoteTabRead(_project, "Steps", third - 1)); // a stale call arriving late
            Assert.Equal(third, Tab(db, _project, "Steps").OwnerReadRev);
            Assert.False(Tab(db, _project, "Steps").IsUnread);
        }

        [Fact]
        public void An_owner_save_reads_the_note_and_reports_it_only_once()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New);

            Assert.True(db.SaveNoteTab(_project, "Steps", "one\nowner remark"));
            Assert.False(db.SaveNoteTab(_project, "Steps", "one\nowner remark 2"));

            Assert.Equal(0, db.CountUnreadNoteTabs(_project));
            // and the agent's next append is news again
            db.WriteAgentNoteTab(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append);
            Assert.Equal(1, db.CountUnreadNoteTabs(_project));
        }

        [Fact]
        public void Rename_keeps_unread_and_delete_drops_it()
        {
            using var db = new TaskDatabase();
            db.WriteAgentNoteTab(_project, "A", "x", "Alice", AgentNoteWriteMode.New);
            db.WriteAgentNoteTab(_project, "B", "y", "Alice", AgentNoteWriteMode.New);
            Assert.Equal(2, db.CountUnreadNoteTabs(_project));

            Assert.True(db.RenameNoteTab(_project, "A", "A renamed"));
            Assert.Equal(2, db.CountUnreadNoteTabs(_project));
            Assert.True(Tab(db, _project, "A renamed").IsUnread);

            Assert.True(db.DeleteNoteTab(_project, "B"));
            Assert.Equal(1, db.CountUnreadNoteTabs(_project));
        }

        /// <summary>
        /// After the deploy, notes agents already wrote under 5d94ac91 (updated_by set, no rev columns)
        /// must not all light up as unread.
        /// </summary>
        [Fact]
        public void Migration_leaves_every_existing_note_read_including_agent_written_ones()
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
                        UNIQUE(project_path, tab_name));", raw);
                create.ExecuteNonQuery();
                using var insert = new SQLiteCommand(@"
                    INSERT INTO project_note_tabs (id, project_path, tab_name, content, tab_order, is_default, created_by, updated_by)
                    VALUES ('a1', @p, 'General', 'old', 0, 1, NULL, NULL),
                           ('a2', @p, 'Agent note', 'from before', 1, 0, 'Alice', 'Alice')", raw);
                insert.Parameters.AddWithValue("@p", TaskDatabase.CanonicalNoteTabKey(_project));
                insert.ExecuteNonQuery();
            }
            SQLiteConnection.ClearAllPools();

            using var db = new TaskDatabase();
            Assert.Equal(0, db.CountUnreadNoteTabs(_project));
            var agentNote = Tab(db, _project, "Agent note");
            Assert.Equal(0, agentNote.AgentRev);
            Assert.False(agentNote.IsUnread);

            db.WriteAgentNoteTab(_project, "Agent note", "new", "Alice", AgentNoteWriteMode.Append);
            Assert.Equal(1, db.CountUnreadNoteTabs(_project));
        }

        // ── Renderer: host-side count, cross-pane agreement, visibility ──

        /// <summary>
        /// The Notes renderer's page loads only when the tab is first shown; these renderers are never
        /// shown, so this pins that the badge counts without the page.
        /// </summary>
        [Fact]
        public void The_badge_counts_an_agent_note_before_the_notes_page_has_ever_loaded()
        {
            using var broker = new MessageBroker();
            using var notes = new HudNotesRenderer();
            notes.Initialize(broker);
            notes.SetProject(_project);
            int raised = 0;
            notes.BadgeCountChanged += (s, e) => raised++;

            broker.WriteAgentProjectNote(_project, "After redeploy", "steps", "Alice", AgentNoteWriteMode.New);

            Assert.Equal(1, notes.BadgeCount);
            Assert.Equal(1, raised);
        }

        [Fact]
        public void Every_pane_on_the_project_clears_together_and_another_project_is_untouched()
        {
            using var broker = new MessageBroker();
            using var paneA = new HudNotesRenderer();
            using var paneB = new HudNotesRenderer();
            using var otherProject = new HudNotesRenderer();
            paneA.Initialize(broker);
            paneB.Initialize(broker);
            otherProject.Initialize(broker);
            paneA.SetProject(_project);
            paneB.SetProject(_project);
            otherProject.SetProject(_project + "-other");

            broker.WriteAgentProjectNote(_project, "Steps", "x", "Alice", AgentNoteWriteMode.New);
            broker.WriteAgentProjectNote(_project + "-other", "Steps", "x", "Alice", AgentNoteWriteMode.New);
            Assert.Equal(1, paneA.BadgeCount);
            Assert.Equal(1, paneB.BadgeCount);
            Assert.Equal(1, otherProject.BadgeCount);

            // What MarkRead does once the Owner has seen the tab in pane A.
            long rev = broker.TaskDb.GetProjectNoteTabDetails(_project).Single(t => t.Name == "Steps").AgentRev;
            Assert.True(broker.TaskDb.MarkNoteTabRead(_project, "Steps", rev));
            broker.NotifyProjectNoteReadStateChanged(_project);

            Assert.Equal(0, paneA.BadgeCount);
            Assert.Equal(0, paneB.BadgeCount);
            Assert.Equal(1, otherProject.BadgeCount);
        }

        /// <summary>
        /// A renderer that is not on screen must not mark anything read, whatever the page asks. This
        /// control is never shown, so <see cref="HudNotesRenderer.IsSeenByOwner"/> is false.
        /// </summary>
        [Fact]
        public void A_pane_the_owner_cannot_see_refuses_to_mark_a_note_read()
        {
            using var broker = new MessageBroker();
            using var notes = new HudNotesRenderer();
            notes.Initialize(broker);
            notes.SetProject(_project);
            broker.WriteAgentProjectNote(_project, "Steps", "x", "Alice", AgentNoteWriteMode.New);
            long rev = broker.TaskDb.GetProjectNoteTabDetails(_project).Single(t => t.Name == "Steps").AgentRev;

            Assert.False(notes.IsSeenByOwner());
            notes.MarkRead("Steps", rev);

            Assert.Equal(1, broker.TaskDb.CountUnreadNoteTabs(_project));
            Assert.Equal(1, notes.BadgeCount);
        }

        /// <summary>
        /// A rename or delete in one pane must reach the other panes as a TAB-LIST change, with the old and
        /// new names for a rename (debugger finding, pipeline Run 1). A flags-only event left the other
        /// panes posting mark_read under the old name, which matches no row, so the badge stuck.
        /// </summary>
        [Fact]
        public void Rename_and_delete_in_one_pane_announce_a_tab_list_change_to_the_others()
        {
            using var broker = new MessageBroker();
            using var paneA = new HudNotesRenderer();
            using var paneB = new HudNotesRenderer();
            paneA.Initialize(broker);
            paneB.Initialize(broker);
            paneA.SetProject(_project);
            paneB.SetProject(_project);
            broker.WriteAgentProjectNote(_project, "Old", "x", "Alice", AgentNoteWriteMode.New);
            broker.WriteAgentProjectNote(_project, "Doomed", "y", "Alice", AgentNoteWriteMode.New);
            Assert.Equal(2, paneB.BadgeCount);
            var seen = new System.Collections.Generic.List<ProjectNoteReadStateChangedEventArgs>();
            broker.ProjectNoteReadStateChanged += (s, e) => seen.Add(e);

            paneA.RenameTab("Old", "New");

            var renamed = Assert.Single(seen);
            Assert.True(renamed.TabsChanged);
            Assert.Equal("Old", renamed.RenamedFrom);
            Assert.Equal("New", renamed.RenamedTo);
            Assert.Equal(2, paneB.BadgeCount); // a rename keeps the read state

            seen.Clear();
            paneA.DeleteTab("Doomed");

            var deleted = Assert.Single(seen);
            Assert.True(deleted.TabsChanged);
            Assert.Null(deleted.RenamedFrom);
            Assert.Equal(1, paneB.BadgeCount);
        }

        /// <summary>
        /// A count that cannot be read keeps the last known value: publishing 0 on an error would hide
        /// unread notes. The database is closed under the renderer to make the next count throw.
        /// </summary>
        [Fact]
        public void A_failed_recount_keeps_the_last_known_count()
        {
            using var broker = new MessageBroker();
            using var notes = new HudNotesRenderer();
            notes.Initialize(broker);
            notes.SetProject(_project);
            broker.WriteAgentProjectNote(_project, "Steps", "x", "Alice", AgentNoteWriteMode.New);
            Assert.Equal(1, notes.BadgeCount);

            broker.TaskDb.Dispose();
            Assert.ThrowsAny<Exception>(() => broker.TaskDb.CountUnreadNoteTabs(_project)); // the read really fails

            notes.RecountUnread();

            Assert.Equal(1, notes.BadgeCount);

            // ...but a failed count after switching project must not show the old project's number.
            notes.SetProject(_project + "-other");
            Assert.Equal(0, notes.BadgeCount);
        }

        /// <summary>
        /// Minimise is judged on the TOP-LEVEL window (pipeline Run 1, debugger HIGH). A docked
        /// TerminalDocument is a DockContent, itself a non-top-level Form, and the main window's minimise
        /// never reaches it. The first assertion proves this arrangement reproduces that: FindForm, the
        /// previous resolution, picks the inner form.
        /// </summary>
        [Fact]
        public void Minimise_is_judged_on_the_top_level_window_not_the_docked_content_form()
        {
            using var mainWindow = new System.Windows.Forms.Form();
            using var dockedDocument = new WeifenLuo.WinFormsUI.Docking.DockContent { TopLevel = false };
            using var pane = new System.Windows.Forms.Panel();
            dockedDocument.Controls.Add(pane);
            mainWindow.Controls.Add(dockedDocument);

            Assert.Same(dockedDocument, pane.FindForm());
            Assert.Same(mainWindow, HudNotesRenderer.SeenWindowFor(pane));
        }

        // ── Owner live test of c133b27d (46ccfe0): the two item-2 failures ──

        private static void PumpUntil(Func<bool> done, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!done() && DateTime.UtcNow < deadline)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
        }

        private static System.Collections.Generic.List<JsonElement> Parse(System.Collections.Generic.List<string> posted)
            => posted.Select(j => JsonDocument.Parse(j).RootElement.Clone()).ToList();

        private static bool PostedSeen(System.Collections.Generic.List<string> posted, bool value)
            => Parse(posted).Any(m => m.GetProperty("type").GetString() == "seen" && m.GetProperty("seen").GetBoolean() == value);

        private static JsonElement? PostedUnreadState(System.Collections.Generic.List<string> posted, string tab)
        {
            foreach (var m in Parse(posted).Where(m => m.GetProperty("type").GetString() == "unread_state"))
                foreach (var t in m.GetProperty("tabs").EnumerateArray())
                    if (t.GetProperty("name").GetString() == tab) return t;
            return null;
        }

        /// <summary>
        /// Bug 2. An agent write is raised on the REST thread. In a running terminal the HUD's window exists,
        /// so <c>InvokeRequired</c> is true, but a Notes tab that has never been shown has no handle of its
        /// own; the old marshal required one and dropped the event, so the header stayed blank until the page
        /// was opened. The other tests raise the write on the test thread with no window at all, which is
        /// why they never saw it. The precondition assertion proves this arrangement is the failing one.
        /// </summary>
        [Fact]
        public void A_never_shown_pane_counts_an_agent_note_written_on_another_thread()
        {
            using var broker = new MessageBroker();
            using var hud = new System.Windows.Forms.Form();
            using var notes = new HudNotesRenderer();
            hud.Controls.Add(notes);
            _ = hud.Handle; // the HUD's window exists, as it does in a running terminal
            notes.Initialize(broker);
            notes.SetProject(_project);
            Assert.False(notes.IsHandleCreated); // the hidden Notes tab never got a handle of its own

            // A plain thread, joined, rather than an awaited task: the test must stay on the window's thread
            // to pump the message the write posts back to it.
            var restThread = new System.Threading.Thread(() =>
                broker.WriteAgentProjectNote(_project, "Steps", "x", "Alice", AgentNoteWriteMode.New));
            restThread.Start();
            restThread.Join();
            PumpUntil(() => notes.BadgeCount == 1);

            Assert.Equal(1, notes.BadgeCount);
        }

        // A real window, so minimise and restore are real. Kept off-screen and out of the taskbar.
        private static System.Windows.Forms.Form OffscreenWindow() => new System.Windows.Forms.Form
        {
            ShowInTaskbar = false,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(-3000, -3000),
            Size = new System.Drawing.Size(300, 200),
        };

        /// <summary>
        /// Bug 1. Live, a note appended while MT was minimised stayed at agent_rev 3 / owner_read_rev 2 after
        /// the restore, with no dot on screen: the page asked to mark it read, the host refused (not seen), the
        /// page had already cleared its dot, and nothing ever asked again. A refusal must tell the page it is
        /// not seen and give it back the stored flags with the current rev; the restore then sends seen again,
        /// which is the page's cue to mark the active tab with that rev.
        /// </summary>
        [Fact]
        public void A_mark_read_refused_while_minimised_restores_the_dot_and_the_restore_re_marks_it()
        {
            using var broker = new MessageBroker();
            using var window = OffscreenWindow();
            using var notes = new HudNotesRenderer { Dock = System.Windows.Forms.DockStyle.Fill };
            window.Controls.Add(notes);
            notes.Initialize(broker);
            notes.SetProject(_project);
            var posted = new System.Collections.Generic.List<string>();
            notes.AttachPageForTests(posted.Add);
            window.Show();
            notes.Visible = true; // the Notes HUD tab is the selected one
            Assert.True(notes.IsSeenByOwner());

            broker.WriteAgentProjectNote(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New);
            window.WindowState = System.Windows.Forms.FormWindowState.Minimized;
            System.Windows.Forms.Application.DoEvents();
            broker.WriteAgentProjectNote(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append);
            long rev = Tab(broker.TaskDb, _project, "Steps").AgentRev;
            posted.Clear();

            notes.MarkRead("Steps", rev); // the page's ask, arriving while minimised

            Assert.Equal(1, broker.TaskDb.CountUnreadNoteTabs(_project));
            Assert.True(PostedSeen(posted, false));
            var state = PostedUnreadState(posted, "Steps");
            Assert.NotNull(state);
            Assert.True(state.Value.GetProperty("unread").GetBoolean());
            Assert.Equal(rev, state.Value.GetProperty("agentRev").GetInt64());

            posted.Clear();
            window.WindowState = System.Windows.Forms.FormWindowState.Normal;
            System.Windows.Forms.Application.DoEvents();
            Assert.True(PostedSeen(posted, true));

            notes.MarkRead("Steps", rev); // what the page sends on that seen
            Assert.Equal(0, broker.TaskDb.CountUnreadNoteTabs(_project));
            Assert.Equal(0, notes.BadgeCount);
        }

        /// <summary>
        /// Bug 1, the other half: a mark_read carrying an older rev than the stored one (the page had not yet
        /// received the latest append) leaves the tab unread. The page cleared its dot when it asked, so the
        /// host must answer with the stored flags and the CURRENT rev, or the page never asks again.
        /// </summary>
        [Fact]
        public void A_mark_read_with_a_stale_rev_gives_the_page_the_current_rev()
        {
            using var broker = new MessageBroker();
            using var window = OffscreenWindow();
            using var notes = new HudNotesRenderer { Dock = System.Windows.Forms.DockStyle.Fill };
            window.Controls.Add(notes);
            notes.Initialize(broker);
            notes.SetProject(_project);
            var posted = new System.Collections.Generic.List<string>();
            notes.AttachPageForTests(posted.Add);
            window.Show();
            notes.Visible = true;
            Assert.True(notes.IsSeenByOwner());

            broker.WriteAgentProjectNote(_project, "Steps", "one", "Alice", AgentNoteWriteMode.New);
            long shown = Tab(broker.TaskDb, _project, "Steps").AgentRev;
            broker.WriteAgentProjectNote(_project, "Steps", "two", "Alice", AgentNoteWriteMode.Append);
            long current = Tab(broker.TaskDb, _project, "Steps").AgentRev;
            posted.Clear();

            notes.MarkRead("Steps", shown);

            Assert.Equal(1, broker.TaskDb.CountUnreadNoteTabs(_project));
            var state = PostedUnreadState(posted, "Steps");
            Assert.NotNull(state);
            Assert.True(state.Value.GetProperty("unread").GetBoolean());
            Assert.Equal(current, state.Value.GetProperty("agentRev").GetInt64());
        }

        // ── HUD tab badge ──

        private sealed class StubBadgeTab : System.Windows.Forms.Control, IZoomableTab, ITabBadgeSource
        {
            private int _count;
            public event EventHandler<double> ZoomChanged { add { } remove { } }
            public event EventHandler BadgeCountChanged;
            public int BadgeCount => _count;
            public void SetZoomFactor(double zoom) { }
            public void Set(int count) { _count = count; BadgeCountChanged?.Invoke(this, EventArgs.Empty); }
        }

        private sealed class PlainTab : System.Windows.Forms.Control, IZoomableTab
        {
            public event EventHandler<double> ZoomChanged { add { } remove { } }
            public void SetZoomFactor(double zoom) { }
        }

        [Fact]
        // The container owns its tab controls and disposes every one in its own Dispose.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "HudTabContainer disposes its tabs")]
        public void The_tab_header_shows_the_sources_count_and_a_tab_without_a_source_shows_none()
        {
            using var container = new HudTabContainer(new TaskHudRenderer());
            var badged = new StubBadgeTab();
            container.AddPermanentTab("__notes__", "Notes", badged);
            container.AddPermanentTab("__plain__", "Plain", new PlainTab());

            Assert.Equal(0, container.GetTabBadgeCount("__notes__"));
            badged.Set(3);
            Assert.Equal(3, container.GetTabBadgeCount("__notes__"));
            Assert.Equal(0, container.GetTabBadgeCount("__plain__"));
            badged.Set(0);
            Assert.Equal(0, container.GetTabBadgeCount("__notes__"));
        }

        [Theory]
        [InlineData(1, "1")]
        [InlineData(99, "99")]
        [InlineData(100, "99+")]
        public void The_badge_text_is_capped(int count, string expected)
            => Assert.Equal(expected, HudTabContainer.FormatBadge(count));
    }
}
