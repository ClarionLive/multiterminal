using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec items 33 and 34: session data follows the PANE's Claude profile wherever a
    /// pane is known, and the "Launch as" picker never preselects Default for a profile it cannot
    /// find. Real temp folders (a "home" whose <c>.claude</c> is Default, a configured "Work", a
    /// configured "Gone" with no folder) and an injected resolver context and project registry;
    /// nothing reads the machine's settings or a real profile folder.
    /// <para>What these facts do NOT cover: that MainForm's shutdown, the two HUD tabs and the REST
    /// controllers actually call the functions tested here, and the live terminal lookup behind
    /// <see cref="ClaudeProfilePaneLookup"/> (MainForm's map). Those are wiring, executed by no test.</para>
    /// </summary>
    public sealed class ClaudeProfilePaneScopeTests : IDisposable
    {
        private readonly string _base;
        private readonly string _defaultDir;
        private readonly string _workDir;
        private readonly string _workProject;
        private readonly string _unownedFolder;
        private readonly ClaudeProfileContext _ctx;
        private readonly ClaudeProfileLookup _lookup;

        public ClaudeProfilePaneScopeTests()
        {
            _base = Path.Combine(Path.GetTempPath(), $"mt_cpps_{Guid.NewGuid():N}");
            string home = Path.Combine(_base, "home");
            _defaultDir = Path.Combine(home, ".claude");
            _workDir = Path.Combine(_base, "work-cfg");
            _workProject = Path.Combine(_base, "proj-work");
            _unownedFolder = Path.Combine(_base, "scratch");

            foreach (string dir in new[] { _defaultDir, _workDir, _workProject, _unownedFolder })
                Directory.CreateDirectory(dir);

            _ctx = new ClaudeProfileContext(
                new[] { new ClaudeProfile("Work", _workDir), new ClaudeProfile("Gone", Path.Combine(_base, "gone-cfg")) },
                null,
                home,
                Directory.Exists);

            var rows = new[] { new ClaudeProfileProjectRow("p-work", "WorkProj", _workProject, null, "Work") };
            _lookup = new ClaudeProfileLookup(
                RowById: id => rows.FirstOrDefault(r => r.Id == id),
                AllRows: () => rows,
                LoadProjectFile: _ => null,
                CandidateFolders: ClaudeProfileLaunchPolicy.CandidateFolders);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private ClaudeDataRoot For(ClaudePaneProfile pane, string folder) =>
            ClaudeProfileDataRoots.ForPaneOrFolder(pane, folder, _lookup, _ctx);

        private static ClaudePaneProfile Pane(string profileName) => new ClaudePaneProfile(profileName);

        // ── Pane or folder ───────────────────────────────────────────────────

        /// <summary>
        /// The reviewers' case. No project owns the folder, so the folder decision answers Default;
        /// a pane sitting in it under Work (inherited from its spawner, or picked by the human) has
        /// its data under Work. Same folder both times: only the pane differs.
        /// </summary>
        [Fact]
        public void In_a_folder_no_project_owns_a_pane_reads_under_its_own_profile_and_no_pane_reads_Default()
        {
            Assert.Equal(_workDir, For(Pane("Work"), _unownedFolder)?.ConfigDir);
            Assert.Equal(_defaultDir, For(Pane(null), _unownedFolder)?.ConfigDir);
            Assert.Equal(_defaultDir, For(null, _unownedFolder)?.ConfigDir);
        }

        /// <summary>The other direction: the folder's project says Work, the pane says Default.</summary>
        [Fact]
        public void In_a_projects_folder_a_pane_still_reads_under_its_own_profile()
        {
            Assert.Equal(_defaultDir, For(Pane(null), _workProject)?.ConfigDir);
            Assert.Equal(_workDir, For(null, _workProject)?.ConfigDir);
        }

        [Fact]
        public void A_pane_whose_profile_cannot_be_honoured_gets_no_folder_not_Default_and_not_the_folders()
        {
            // Not configured, configured without a folder, and "a pane exists but its profile could not
            // be read". By folder, the first three lines below would answer Default, the last Work.
            Assert.Null(For(Pane("NotConfigured"), _unownedFolder));
            Assert.Null(For(Pane("Gone"), _unownedFolder));
            Assert.Null(For(ClaudePaneProfile.Unknown, _unownedFolder));
            Assert.Null(For(Pane("Gone"), _workProject));
        }

        // ── Agent name to pane ───────────────────────────────────────────────

        [Fact]
        public void The_agent_lookup_passes_the_answer_through_and_never_turns_a_failure_into_no_pane()
        {
            var work = Pane("Work");
            int asked = 0;
            Func<string, ClaudePaneProfile> lookup = name =>
            {
                asked++;
                return name == "Alice" ? work : null;
            };

            Assert.Same(work, ClaudeProfilePaneLookup.ForAgent("Alice", lookup));
            Assert.Null(ClaudeProfilePaneLookup.ForAgent("Nobody", lookup));
            Assert.Equal(2, asked);

            // A blank name, or no lookup configured yet: no pane, and the lookup is not called.
            Assert.Null(ClaudeProfilePaneLookup.ForAgent("  ", lookup));
            Assert.Null(ClaudeProfilePaneLookup.ForAgent(null, lookup));
            Assert.Equal(2, asked);
            Assert.Null(ClaudeProfilePaneLookup.ForAgent("Alice", null));

            // A lookup that throws is "could not tell", which yields no folder at all; "no pane"
            // would have decided by folder.
            var failed = ClaudeProfilePaneLookup.ForAgent("Alice", _ => throw new InvalidOperationException("map unavailable"));
            Assert.Same(ClaudePaneProfile.Unknown, failed);
            Assert.Null(For(failed, _unownedFolder));
        }

        // ── The shutdown flush ───────────────────────────────────────────────

        [Fact]
        public void The_flush_key_includes_the_profile_folder()
        {
            var work = new ClaudeDataRoot("Work", _workDir);
            var def = new ClaudeDataRoot("Default", _defaultDir);

            Assert.NotEqual(ClaudeProfileDataRoots.FlushKey(work, _unownedFolder), ClaudeProfileDataRoots.FlushKey(def, _unownedFolder));

            // Same root and folder, written differently: one key under the comparer the set uses.
            var keys = new HashSet<string>(ClaudeProfileDataRoots.FlushKeyComparer)
            {
                ClaudeProfileDataRoots.FlushKey(work, _unownedFolder)
            };
            Assert.False(keys.Add(ClaudeProfileDataRoots.FlushKey(work, _unownedFolder.ToUpperInvariant() + Path.DirectorySeparatorChar)));
            Assert.True(keys.Add(ClaudeProfileDataRoots.FlushKey(def, _unownedFolder)));

            Assert.Null(ClaudeProfileDataRoots.FlushKey(null, _unownedFolder));
            Assert.Null(ClaudeProfileDataRoots.FlushKey(work, " "));
        }

        [Fact]
        public void The_flush_plan_is_one_step_per_profile_folder_and_working_folder_under_each_panes_own_profile()
        {
            var skipped = new List<string>();
            var panes = new[]
            {
                new ClaudePaneSnapshot("Alice", _unownedFolder, Pane("Work")),
                new ClaudePaneSnapshot("Bob", _unownedFolder, Pane(null)),            // same folder, Default
                new ClaudePaneSnapshot("Carol", _unownedFolder, Pane("work")),        // same as Alice: no second step
                new ClaudePaneSnapshot("Dave", _workProject, Pane("Gone")),           // profile has no folder
                new ClaudePaneSnapshot("Erin", _workProject, null),                   // no recorded launch
                new ClaudePaneSnapshot("Frank", "", Pane("Work")),                    // no folder: nothing to flush
            };

            var plan = ClaudeProfileDataRoots.PlanPaneFlush(
                panes, pane => ClaudeProfileDataRoots.ForProfileName(pane.ProfileName, _ctx), skipped.Add);

            Assert.Equal(2, plan.Count);
            Assert.Equal((_workDir, _unownedFolder, "Alice"), (plan[0].Root.ConfigDir, plan[0].Folder, plan[0].AgentName));
            Assert.Equal((_defaultDir, _unownedFolder, "Bob"), (plan[1].Root.ConfigDir, plan[1].Folder, plan[1].AgentName));

            // Dave and Erin are in a folder the project registry gives to Work. Neither is flushed
            // under Work or Default: each is reported and left out.
            Assert.Equal(2, skipped.Count);
            Assert.Contains("'Dave'", skipped[0]);
            Assert.Contains("'Gone'", skipped[0]);
            Assert.Contains("'Erin'", skipped[1]);
        }

        // ── The one context file ─────────────────────────────────────────────

        [Fact]
        public void The_context_file_is_written_for_a_pane_in_its_project_folder_before_any_other()
        {
            string worktree = Path.Combine(_workProject, ".claude", "worktrees", "abc");
            var elsewhere = new ClaudePaneSnapshot("Alice", _unownedFolder, Pane(null));
            var inside = new ClaudePaneSnapshot("Bob", worktree, Pane("Work"));
            var unnamed = new ClaudePaneSnapshot("", _workProject, Pane("Work"));

            var chosen = SessionContextWriter.ChooseContextPane(_workProject, new[] { unnamed, elsewhere, inside }, out bool inProject);
            Assert.Same(inside, chosen);
            Assert.True(inProject);

            // No pane in the folder: the first NAMED pane supplies the name only.
            chosen = SessionContextWriter.ChooseContextPane(_workProject, new[] { unnamed, elsewhere }, out inProject);
            Assert.Same(elsewhere, chosen);
            Assert.False(inProject);

            // A sibling whose name merely starts with the project folder's is not inside it.
            var sibling = new ClaudePaneSnapshot("Carol", _workProject + "X", Pane("Work"));
            chosen = SessionContextWriter.ChooseContextPane(_workProject, new[] { sibling }, out inProject);
            Assert.Same(sibling, chosen);
            Assert.False(inProject);

            Assert.Null(SessionContextWriter.ChooseContextPane(_workProject, new[] { unnamed }, out inProject));
            Assert.Null(SessionContextWriter.ChooseContextPane(_workProject, null, out inProject));
        }

        [Fact]
        public void A_pane_outside_the_context_folder_gets_the_file_only_under_its_own_profile()
        {
            var work = new ClaudeDataRoot("Work", @"C:\Users\me\.claude-work");
            var personal = new ClaudeDataRoot("Default", @"C:\Users\me\.claude");

            Assert.True(SessionContextWriter.SameContextRoot(work, new ClaudeDataRoot("Work", @"c:\users\me\.claude-work\")));
            Assert.False(SessionContextWriter.SameContextRoot(work, personal));
            Assert.False(SessionContextWriter.SameContextRoot(work, null));
            Assert.False(SessionContextWriter.SameContextRoot(null, personal));
            Assert.False(SessionContextWriter.SameContextRoot(null, null));
        }

        // ── The index guard ──────────────────────────────────────────────────

        [Fact]
        public void Only_a_jsonl_inside_a_configured_profiles_projects_folder_may_be_indexed()
        {
            var roots = ClaudeProfileResolver.GetAllRoots(_ctx);
            string transcript = Path.Combine(_workDir, "projects", "slug", "a.jsonl");

            Assert.True(ClaudeProfileDataRoots.IsSessionTranscriptPath(transcript, roots, out string full));
            Assert.Equal(transcript, full);
            Assert.True(ClaudeProfileDataRoots.IsSessionTranscriptPath(Path.Combine(_defaultDir, "projects", "slug", "sess", "subagents", "agent-1.JSONL"), roots, out _));

            string[] refused =
            {
                Path.Combine(_workDir, ".credentials.json"),                       // under the root, not under projects
                Path.Combine(_workDir, "a.jsonl"),                                 // a .jsonl, still not under projects
                Path.Combine(_workDir, "todos", "a.jsonl"),
                Path.Combine(_workDir, "projects", "slug", "settings.json"),       // under projects, not a .jsonl
                Path.Combine(_workDir, "projects", "slug", "a.jsonl.bak"),
                Path.Combine(_workDir, "projects", "slug", "notes.txt:a.jsonl"),   // an alternate data stream of notes.txt
                Path.Combine(_workDir + "X", "projects", "slug", "a.jsonl"),       // a sibling whose name starts with the root's
                Path.Combine(_workDir, "projectsX", "a.jsonl"),
                Path.Combine(_workDir, "projects", "..", "a.jsonl"),               // resolves to the root itself
                Path.Combine(_base, "elsewhere", "projects", "a.jsonl"),           // not a profile folder
                Path.Combine("projects", "a.jsonl"),                               // relative
                null,
            };
            foreach (string path in refused)
            {
                Assert.False(ClaudeProfileDataRoots.IsSessionTranscriptPath(path, roots, out string refusedFull), path ?? "(null)");
                Assert.Null(refusedFull);
            }
        }

        // ── Launch as: the picker (item 34) ──────────────────────────────────

        private static readonly ClaudeProfile[] Configured =
        {
            new ClaudeProfile(ClaudeProfile.DefaultName, @"C:\Users\x\.claude"),
            new ClaudeProfile("Work", @"C:\profiles\work"),
        };

        [Fact]
        public void A_pane_profile_this_machine_no_longer_has_is_selected_as_itself_and_cannot_be_launched()
        {
            var choices = ClaudeProfileChoices.Build(Configured, "Client-X", out int selected);
            var preselected = choices[selected];

            // Not Default: the entry is the pane's own name, marked, and Launch stays disabled on it.
            Assert.Equal("Client-X", preselected.Name);
            Assert.True(preselected.IsMissing);
            Assert.False(ClaudeProfileChoices.CanLaunch(preselected));
            Assert.Contains("Client-X", ClaudeProfileChoices.DescribeUnlaunchable(preselected));

            // Every configured entry, Default included, can be launched once the human picks it.
            foreach (var choice in choices.Where(c => !c.IsMissing))
            {
                Assert.True(ClaudeProfileChoices.CanLaunch(choice));
                Assert.Null(ClaudeProfileChoices.DescribeUnlaunchable(choice));
            }
            Assert.Equal(new[] { null, "Work" }, choices.Where(ClaudeProfileChoices.CanLaunch).Select(c => c.Name));

            Assert.False(ClaudeProfileChoices.CanLaunch(null)); // nothing selected
        }

        [Fact]
        public void A_configured_pane_profile_is_preselected_and_can_be_launched()
        {
            var choices = ClaudeProfileChoices.Build(Configured, "work", out int selected);
            Assert.Equal("Work", choices[selected].Name);
            Assert.True(ClaudeProfileChoices.CanLaunch(choices[selected]));

            choices = ClaudeProfileChoices.Build(Configured, null, out selected);
            Assert.Null(choices[selected].Name); // Default, because the pane IS on Default
            Assert.True(ClaudeProfileChoices.CanLaunch(choices[selected]));
        }
    }
}
