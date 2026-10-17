using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Coverage for <see cref="SessionCatalog"/> (task ec2ed636): the list of past Claude Code
    /// sessions offered for resume at project launch, derived from transcripts on disk.
    ///
    /// <para>Each test builds a throwaway tree: a real project root (with a worktree directory,
    /// because the catalog drops sessions whose cwd no longer exists) and a fake
    /// <c>~/.claude/projects</c> holding synthetic JSONL transcripts.</para>
    /// </summary>
    public sealed class SessionCatalogTests : IDisposable
    {
        private const string WorktreeId = "abcd1234";

        private static readonly DateTime Now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _base;
        private readonly string _projectsRoot;
        private readonly string _projectRoot;
        private readonly string _worktreeDir;
        private readonly string _rootFolder;
        private readonly string _worktreeFolder;

        public SessionCatalogTests()
        {
            _base = Path.Combine(Path.GetTempPath(), $"mt_sc_test_{Guid.NewGuid():N}");
            _projectsRoot = Path.Combine(_base, "projects");
            _projectRoot = Path.Combine(_base, "repo", "Proj");
            _worktreeDir = Path.Combine(_projectRoot, ".claude", "worktrees", WorktreeId);
            Directory.CreateDirectory(_worktreeDir);

            _rootFolder = Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(_projectRoot));
            _worktreeFolder = Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(_worktreeDir));
            Directory.CreateDirectory(_rootFolder);
            Directory.CreateDirectory(_worktreeFolder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        }

        // ---- line builders (shapes copied from real 2026-10 transcripts) ----

        private static string User(string text, string cwd, bool isMeta = false)
            => JsonSerializer.Serialize(new
            {
                parentUuid = (string)null,
                isSidechain = false,
                type = "user",
                message = new { role = "user", content = text },
                uuid = Guid.NewGuid().ToString(),
                isMeta,
                cwd,
            });

        private static string UserBlocks(object[] blocks, string cwd)
            => JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = blocks },
                uuid = Guid.NewGuid().ToString(),
                cwd,
            });

        private static string Assistant(string text, string cwd)
            => JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new { role = "assistant", content = new object[] { new { type = "text", text } } },
                uuid = Guid.NewGuid().ToString(),
                cwd,
            });

        private static string Hook(string content, string cwd, string hookEvent = "SessionStart")
            => JsonSerializer.Serialize(new
            {
                isSidechain = false,
                attachment = new
                {
                    type = "hook_success",
                    hookName = hookEvent + ":startup",
                    hookEvent,
                    content,
                    stdout = content,
                },
                type = "attachment",
                uuid = Guid.NewGuid().ToString(),
                cwd,
            });

        private string Write(string folder, DateTime lastWriteUtc, params string[] lines)
        {
            var id = Guid.NewGuid().ToString();
            var path = Path.Combine(folder, id + ".jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return id;
        }

        // Records the cwd that matches the folder, as a real session does: since pipeline Run 1 a
        // transcript whose cwd does not encode to its folder is dropped, not resumed from that cwd.
        private string WriteSimple(string folder, DateTime lastWriteUtc, string prompt = "hello")
        {
            var cwd = folder == _worktreeFolder ? _worktreeDir : _projectRoot;
            return Write(folder, lastWriteUtc, User(prompt, cwd), Assistant("hi", cwd));
        }

        private SessionCatalog Catalog(Func<string, string> names = null, Func<string, string> summaries = null)
            => new SessionCatalog(_projectsRoot, names, summaries);

        private IReadOnlyList<SessionCatalogEntry> List(SessionCatalog catalog = null, ISet<string> live = null, int max = 15)
            => (catalog ?? Catalog()).List(_projectRoot, live ?? new HashSet<string>(), Now, max);

        // ---- tests ----

        [Fact]
        public void Encoding_matches_the_real_claude_projects_folder_names()
        {
            // Both expected strings are real folder names in C:\Users\John Hickey\.claude\projects
            // (listed 2026-10-17): '.', ':', '\' and ' ' all become '-'.
            Assert.Equal(
                "H--DevLaptop-ClarionPowerShell-MultiTerminal--claude-worktrees-5d94ac91",
                SessionCatalog.EncodeProjectPath(@"H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\worktrees\5d94ac91"));
            Assert.Equal(
                "C--Users-John-Hickey-AppData-Local-Temp-claude-H--DevLaptop-ClarionPowerShell-MultiTerminal-d8016236-9df1-432c-9f0c-0629217f6b4c-scratchpad",
                SessionCatalog.EncodeProjectPath(@"C:\Users\John Hickey\AppData\Local\Temp\claude\H--DevLaptop-ClarionPowerShell-MultiTerminal\d8016236-9df1-432c-9f0c-0629217f6b4c\scratchpad"));
        }

        [Fact]
        public void Worktree_folder_session_is_listed_with_its_worktree_id_and_the_LAST_cwd_from_the_tail()
        {
            // The EnterWorktree shape: the head records the project root, the transcript was moved
            // into the worktree's folder, and the worktree cwd appears ONLY beyond the head window.
            // A head-only reader, or a first-cwd reader, both answer _projectRoot.
            var lines = new List<string>
            {
                User("start the worktree", _projectRoot),
                Assistant("ok", _projectRoot),
            };
            var padding = new string('x', 4000);
            for (var i = 0; i < 120; i++)
                lines.Add(JsonSerializer.Serialize(new { type = "progress", data = padding }));
            lines.Add(Assistant("now in the worktree", _worktreeDir));
            var id = Write(_worktreeFolder, Now.AddHours(-1), lines.ToArray());

            var path = Path.Combine(_worktreeFolder, id + ".jsonl");
            Assert.True(new FileInfo(path).Length > SessionCatalog.HeadMaxBytes, "fixture must exceed the head window");

            var entry = Assert.Single(List());
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(_worktreeDir, entry.Cwd);
            Assert.Equal(WorktreeId, entry.WorktreeId);
            Assert.Equal(path, entry.TranscriptPath);
            Assert.Equal(Now.AddHours(-1), entry.LastActivityUtc);
        }

        [Fact]
        public void Session_that_only_cd_ed_into_a_worktree_resumes_from_the_folder_it_is_stored_under()
        {
            // Real shape (PM transcript 99fede3b): stored in the ROOT folder, last cwd is a subdir of a
            // worktree. `claude --resume` from that subdir would look in a different folder, so the
            // catalog must keep the last cwd that encodes to the transcript's own folder.
            var docking = Directory.CreateDirectory(Path.Combine(_worktreeDir, "Docking")).FullName;
            Write(_rootFolder, Now.AddHours(-1),
                User("look at the worktree", _projectRoot),
                Assistant("cd", docking),
                Assistant("still here", docking));

            var entry = Assert.Single(List());
            Assert.Equal(_projectRoot, entry.Cwd);
            Assert.Null(entry.WorktreeId);
        }

        [Fact]
        public void Live_session_ids_are_excluded_ignoring_case()
        {
            var live = WriteSimple(_rootFolder, Now.AddHours(-1));
            var other = WriteSimple(_rootFolder, Now.AddHours(-2));

            // A default (case-sensitive) set holding the id upper-cased: the catalog must still match.
            var liveSet = new HashSet<string> { live.ToUpperInvariant() };
            Assert.NotEqual(live, live.ToUpperInvariant());

            var entry = Assert.Single(List(live: liveSet));
            Assert.Equal(other, entry.SessionId);
        }

        [Fact]
        public void Session_whose_cwd_no_longer_exists_is_excluded()
        {
            // A deleted worktree: its folder and transcript survive, the directory does not.
            var goneDir = Path.Combine(_projectRoot, ".claude", "worktrees", "deadbeef");
            var goneFolder = Directory.CreateDirectory(
                Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(goneDir))).FullName;
            Write(goneFolder, Now.AddHours(-1), User("work", goneDir), Assistant("done", goneDir));
            var kept = WriteSimple(_worktreeFolder, Now.AddHours(-2));

            var entry = Assert.Single(List());
            Assert.Equal(kept, entry.SessionId);
        }

        [Fact]
        public void Transcript_without_user_or_assistant_turns_is_excluded()
        {
            Write(_rootFolder, Now.AddHours(-1),
                Hook("MULTITERMINAL_NAME=Alice", _projectRoot),
                JsonSerializer.Serialize(new { type = "custom-title", customTitle = "Alice" }));
            File.WriteAllText(Path.Combine(_rootFolder, Guid.NewGuid() + ".jsonl"), string.Empty);
            var kept = WriteSimple(_rootFolder, Now.AddHours(-3));

            var entry = Assert.Single(List());
            Assert.Equal(kept, entry.SessionId);
        }

        [Fact]
        public void Recent_write_is_PossiblyOpenElsewhere_and_older_is_Available()
        {
            var recent = WriteSimple(_rootFolder, Now - SessionCatalog.RecentWriteWindow + TimeSpan.FromSeconds(10));
            var old = WriteSimple(_rootFolder, Now - SessionCatalog.RecentWriteWindow - TimeSpan.FromSeconds(10));

            var entries = List().ToDictionary(e => e.SessionId);
            Assert.Equal(TimeSpan.FromMinutes(2), SessionCatalog.RecentWriteWindow);
            Assert.Equal(SessionCatalogStatus.PossiblyOpenElsewhere, entries[recent].Status);
            Assert.Equal(SessionCatalogStatus.Available, entries[old].Status);
        }

        [Fact]
        public void Helper_is_detected_from_SessionStart_hook_text_and_undefined_spawner_is_not_a_helper()
        {
            // Hook text copied from the real helper transcript 5b8431ec (2026-10-16).
            var helper = Write(_rootFolder, Now.AddHours(-1),
                Hook("[DEBUG] MULTITERMINAL_SPAWNER = 'Alice' (isSpawnedAgent: true)\nProfile Notes created/marked online\n## Spawned Agent: Notes\nSpawned by: Alice\nYOUR FIRST ACTION: call get_my_spawn_job", _projectRoot),
                User("initializing...", _projectRoot),
                Assistant("ok", _projectRoot));

            // A normal terminal prints the spawner as 'undefined'. The non-SessionStart attachment
            // repeats the markers the way a file read of CLAUDE.md does; it must not be believed.
            var pm = Write(_rootFolder, Now.AddHours(-2),
                Hook("[DEBUG] MULTITERMINAL_SPAWNER = 'undefined' (isSpawnedAgent: false)\n## MultiTerminal Identity\nMULTITERMINAL_NAME=Bob\nMULTITERMINAL_DOC_ID=9eae2bea", _projectRoot),
                Hook("Spawned by: Mallory\nMULTITERMINAL_NAME=Mallory", _projectRoot, hookEvent: "PostToolUse"),
                User("plan the release", _projectRoot),
                Assistant("ok", _projectRoot));

            var entries = List().ToDictionary(e => e.SessionId);

            Assert.True(entries[helper].IsHelper);
            Assert.Equal("Alice", entries[helper].SpawnedBy);
            Assert.Equal("Notes", entries[helper].AgentName);

            Assert.False(entries[pm].IsHelper);
            Assert.Null(entries[pm].SpawnedBy);
            Assert.Equal("Bob", entries[pm].AgentName);
        }

        [Fact]
        public void Agent_name_lookup_wins_and_a_throwing_lookup_falls_back_to_the_transcript()
        {
            var a = Write(_rootFolder, Now.AddHours(-1),
                Hook("MULTITERMINAL_NAME=FromHook", _projectRoot), User("x", _projectRoot));
            var b = Write(_rootFolder, Now.AddHours(-2),
                Hook("MULTITERMINAL_NAME=FromHookB", _projectRoot), User("y", _projectRoot));

            var catalog = Catalog(names: id => id == a ? "FromLookup" : throw new InvalidOperationException("db down"));
            var entries = List(catalog).ToDictionary(e => e.SessionId);

            Assert.Equal("FromLookup", entries[a].AgentName);
            Assert.Equal("FromHookB", entries[b].AgentName);
        }

        [Fact]
        public void Summary_falls_back_to_the_first_genuine_prompt_skipping_hook_and_system_text()
        {
            var id = Write(_rootFolder, Now.AddHours(-1),
                Hook("MULTITERMINAL_NAME=Alice", _projectRoot),
                User("<command-name>/clear</command-name>", _projectRoot),
                User("<system-reminder>reminder only</system-reminder>", _projectRoot),
                User("[MultiTerminal message from Bob to Alice] ping", _projectRoot),
                User("meta line", _projectRoot, isMeta: true),
                // A tool result can carry a text block beside it; that text is not a prompt either.
                UserBlocks(new object[]
                {
                    new { type = "tool_result", tool_use_id = "t1", content = "tool output" },
                    new { type = "text", text = "text riding on a tool result" },
                }, _projectRoot),
                User("Fix the   login\r\n  bug <system-reminder>noise</system-reminder> please", _projectRoot),
                User("second prompt", _projectRoot),
                Assistant("ok", _projectRoot));

            var entry = Assert.Single(List());
            Assert.Equal(id, entry.SessionId);
            Assert.Equal("Fix the login bug please", entry.Summary);
        }

        [Fact]
        public void Summary_lookup_wins_and_every_summary_is_one_line_of_at_most_160_chars()
        {
            var fromLookup = WriteSimple(_rootFolder, Now.AddHours(-1), "prompt text");
            var longPrompt = WriteSimple(_rootFolder, Now.AddHours(-2), string.Join(" ", Enumerable.Repeat("word", 100)));

            var catalog = Catalog(summaries: id => id == fromLookup ? "Lineage\nsummary" : null);
            var entries = List(catalog).ToDictionary(e => e.SessionId);

            Assert.Equal("Lineage summary", entries[fromLookup].Summary);
            var s = entries[longPrompt].Summary;
            Assert.True(s.Length <= SessionCatalog.SummaryMaxChars, $"length {s.Length}");
            Assert.StartsWith("word word", s, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Newest_first_and_capped_at_max()
        {
            // Written in shuffled order, so creation order cannot pass for mtime order.
            var ids = new Dictionary<int, string>();
            foreach (var hoursAgo in new[] { 3, 1, 5, 2, 4 })
                ids[hoursAgo] = WriteSimple(hoursAgo % 2 == 0 ? _worktreeFolder : _rootFolder, Now.AddHours(-hoursAgo));

            var listed = List(max: 3).Select(e => e.SessionId).ToList();

            Assert.Equal(new[] { ids[1], ids[2], ids[3] }, listed);
        }

        [Fact]
        public void Malformed_lines_are_skipped_and_a_garbage_file_never_throws()
        {
            var ok = Write(_rootFolder, Now.AddHours(-1),
                "{not json at all",
                User("survives the bad line", _projectRoot),
                "[1,2,3]",
                "\"just a string\"",
                Assistant("ok", _projectRoot),
                "{\"type\":\"user\",\"message\":{\"content\":\"torn");
            File.WriteAllBytes(Path.Combine(_rootFolder, Guid.NewGuid() + ".jsonl"), new byte[] { 0xFF, 0xFE, 0x00, 0x7B, 0x0A, 0x7D });

            var entry = Assert.Single(List());
            Assert.Equal(ok, entry.SessionId);
            Assert.Equal("survives the bad line", entry.Summary);
        }

        // ---- pipeline Run 1 ----

        [Theory]
        // accepted: the root itself (any case, either separator, trailing separator) and worktrees
        [InlineData(@"C:\proj", @"C:\proj")]
        [InlineData(@"c:\PROJ\", @"c:\PROJ")]
        [InlineData(@"C:/proj", @"C:\proj")]
        [InlineData(@"C:\proj\.claude\worktrees\abcd1234", @"C:\proj\.claude\worktrees\abcd1234")]
        [InlineData(@"C:\proj\.claude\worktrees\abcd1234\Docking", @"C:\proj\.claude\worktrees\abcd1234\Docking")]
        // rejected: UNC, device paths, relative, drive-relative, ADS
        [InlineData(@"\\evilhost\share", null)]
        [InlineData(@"\\evilhost\share\proj", null)]
        [InlineData(@"//evilhost/share", null)]
        [InlineData(@"\\?\C:\proj", null)]
        [InlineData(@"\\.\C:\proj", null)]
        [InlineData(@"proj", null)]
        [InlineData(@"..\proj", null)]
        [InlineData(@"C:proj", null)]
        [InlineData(@"C:\proj:stream", null)]
        [InlineData(@"C:\proj\.claude\worktrees\abcd1234:ads", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        // rejected: sibling prefix, outside the root, inside the root but not a worktree, worktree depth 0
        [InlineData(@"C:\proj2", null)]
        [InlineData(@"C:\proj2\.claude\worktrees\abcd1234", null)]
        [InlineData(@"C:\other", null)]
        [InlineData(@"D:\proj", null)]
        [InlineData(@"C:\", null)]
        [InlineData(@"C:\proj\src", null)]
        [InlineData(@"C:\proj\.claude", null)]
        [InlineData(@"C:\proj\.claude\worktrees", null)]
        [InlineData(@"C:\proj\.claude\worktrees\", null)]
        [InlineData(@"C:\proj\.claude\worktreesX\abcd1234", null)]
        // rejected: '..' climbing out after canonicalisation
        [InlineData(@"C:\proj\.claude\worktrees\abcd1234\..\..\..\..\other", null)]
        [InlineData(@"C:\proj\.claude\worktrees\..", null)]
        public void ValidateResumeCwd_accepts_only_the_root_or_a_worktree_as_a_local_drive_path(string cwd, string expected)
        {
            Assert.Equal(expected, SessionCatalog.ValidateResumeCwd(@"C:\proj", cwd));
        }

        [Fact]
        public void ValidateResumeCwd_accepts_nothing_for_a_UNC_project_root()
        {
            Assert.Null(SessionCatalog.ValidateResumeCwd(@"\\server\share\proj", @"\\server\share\proj"));
        }

        [Fact]
        public void A_UNC_cwd_in_a_transcript_never_reaches_the_filesystem()
        {
            // Opening the launch prompt must not make Windows authenticate to an attacker's SMB host.
            // The transcript sits in the root's folder but records only a UNC cwd.
            var id = Write(_rootFolder, Now.AddHours(-1),
                User("hi", @"\\evilhost\share"),
                Assistant("ok", @"\\evilhost\share"));

            var probed = new List<string>();
            var catalog = Catalog();
            catalog.DirectoryExists = p => { probed.Add(p); return Directory.Exists(p); };

            var entry = Assert.Single(List(catalog));

            Assert.DoesNotContain(probed, p => p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal));
            Assert.Contains(probed, p => string.Equals(p, _projectRoot, StringComparison.OrdinalIgnoreCase));
            // The root-folder fallback: resuming from the root finds a transcript stored in its folder.
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(_projectRoot, entry.Cwd);
        }

        [Fact]
        public void A_worktree_transcript_whose_recorded_cwds_do_not_encode_to_its_folder_is_dropped()
        {
            // Every cwd is the project root, but the transcript lives in the WORKTREE's folder: resuming
            // from the root would answer "No conversation found". Never offered with a mismatched cwd.
            Write(_worktreeFolder, Now.AddHours(-1), User("x", _projectRoot), Assistant("y", _projectRoot));

            Assert.Empty(List());
        }

        [Fact]
        public void A_root_transcript_with_only_worktree_subfolder_cwds_resumes_from_the_root()
        {
            var docking = Directory.CreateDirectory(Path.Combine(_worktreeDir, "Docking")).FullName;
            Write(_rootFolder, Now.AddHours(-1), User("x", docking), Assistant("y", docking));

            var entry = Assert.Single(List());
            Assert.Equal(_projectRoot, entry.Cwd);
        }

        [Fact]
        public void A_deleted_worktrees_folder_is_not_read_but_an_ambiguous_one_is()
        {
            var kept = WriteSimple(_rootFolder, Now.AddHours(-5));

            // Unambiguous id, directory gone: skipped before any transcript in it is opened.
            var gone = Directory.CreateDirectory(Path.Combine(_projectsRoot,
                SessionCatalog.EncodeProjectPath(Path.Combine(_projectRoot, ".claude", "worktrees", "deadbeef")))).FullName;
            Write(gone, Now.AddHours(-1), User("x", _projectRoot), Assistant("y", _projectRoot));

            // "dead-beef" encodes with a '-', which could equally be a subfolder: kept and read.
            var ambiguous = Directory.CreateDirectory(Path.Combine(_projectsRoot,
                SessionCatalog.EncodeProjectPath(Path.Combine(_projectRoot, ".claude", "worktrees", "dead-beef")))).FullName;
            Write(ambiguous, Now.AddHours(-2), User("x", _projectRoot), Assistant("y", _projectRoot));

            var catalog = Catalog();
            var entry = Assert.Single(List(catalog));

            Assert.Equal(kept, entry.SessionId);
            Assert.Equal(2, catalog.TranscriptsReadLastList); // kept + ambiguous; never the deleted worktree's
        }

        [Fact]
        public void Transcripts_read_are_capped_at_three_times_max_newest_first()
        {
            // Seven newest transcripts that resume nothing, then one good one: with max 2 the budget is
            // 6 reads, so the good one is never reached.
            for (var i = 1; i <= 7; i++)
                Write(_rootFolder, Now.AddMinutes(-i * 10), Hook("MULTITERMINAL_NAME=X", _projectRoot));
            WriteSimple(_rootFolder, Now.AddHours(-5));

            var catalog = Catalog();
            var listed = List(catalog, max: 2);

            Assert.Equal(3, SessionCatalog.ReadBudgetFactor);
            Assert.Equal(6, catalog.TranscriptsReadLastList);
            Assert.Empty(listed);
        }

        [Fact]
        public void Only_session_files_directly_in_this_projects_folders_are_scanned()
        {
            var kept = WriteSimple(_rootFolder, Now.AddHours(-5));

            // Subagent transcripts live in a subfolder named after the session.
            var sub = Directory.CreateDirectory(Path.Combine(_rootFolder, kept, "subagents")).FullName;
            WriteSimple(sub, Now.AddHours(-1));

            // Not a session id.
            File.WriteAllText(Path.Combine(_rootFolder, "notes.jsonl"), User("x", _projectRoot) + "\n");

            // A sibling project whose encoded name merely starts with ours ("ProjOther").
            var sibling = Directory.CreateDirectory(
                Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(_projectRoot + "Other"))).FullName;
            WriteSimple(sibling, Now.AddHours(-1));

            var entry = Assert.Single(List());
            Assert.Equal(kept, entry.SessionId);
        }
    }
}
