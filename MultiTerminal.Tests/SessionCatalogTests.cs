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

        // The first user line of a headless `claude -p --input-format stream-json` run, as Claude Code
        // 2.1.314 wrote it (probe on 2026-10-30): content is a block array and entrypoint is "sdk-cli".
        // An interactive pane writes entrypoint "cli".
        private static string EntrypointUser(string text, string cwd, string entrypoint)
            => JsonSerializer.Serialize(new
            {
                parentUuid = (string)null,
                isSidechain = false,
                type = "user",
                message = new { role = "user", content = new object[] { new { type = "text", text } } },
                uuid = Guid.NewGuid().ToString(),
                promptSource = "sdk",
                entrypoint,
                cwd,
            });

        [Fact]
        public void Gloss_writer_transcripts_are_not_offered_and_TryGetEntry_says_why()
        {
            // GH #67: MT run from a worktree's build output starts its gloss writer in a folder under
            // that worktree, so the writer's transcript lands in a folder this catalog scans.
            var prompt = GlossBackfillPlanner.PromptOpening + "\na reader can LEARN what the plan does instead of approving it on trust.\n\nTASK: x";
            var writer = Write(_worktreeFolder, Now.AddHours(-1),
                EntrypointUser(prompt, _worktreeDir, "sdk-cli"), Assistant("saved", _worktreeDir));
            var work = WriteSimple(_worktreeFolder, Now.AddHours(-2));

            var entry = Assert.Single(List());
            Assert.Equal(work, entry.SessionId);

            var catalog = Catalog();
            Assert.Null(catalog.TryGetEntry(_projectRoot, writer, new HashSet<string>(), Now));
            Assert.Equal("it is a background gloss writer MultiTerminal ran for a checklist, not a work session", catalog.LastMissReason);
        }

        [Fact]
        public void The_gloss_prompt_typed_into_an_interactive_pane_or_a_different_headless_prompt_is_still_offered()
        {
            // The match needs BOTH the writer's opening line and a headless entrypoint.
            var pasted = Write(_worktreeFolder, Now.AddHours(-1),
                EntrypointUser(GlossBackfillPlanner.PromptOpening + " ...", _worktreeDir, "cli"), Assistant("ok", _worktreeDir));
            var otherHeadless = Write(_worktreeFolder, Now.AddHours(-2),
                EntrypointUser("Review the diff and report.", _worktreeDir, "sdk-cli"), Assistant("ok", _worktreeDir));

            var ids = List().Select(e => e.SessionId).ToHashSet();
            Assert.Contains(pasted, ids);
            Assert.Contains(otherHeadless, ids);
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

        // ---- item 7: context used ----

        // The real shape of an assistant line (Claude Code 2.1.301, transcript 99e83244, 2026-10-16),
        // trimmed to the fields the catalog reads plus a few it must ignore.
        private static string AssistantWithUsage(string cwd, string model, long input, long cacheRead, long cacheCreation,
            bool isSidechain = false, string requestedModel = null)
            => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["parentUuid"] = Guid.NewGuid().ToString(),
                ["isSidechain"] = isSidechain,
                ["message"] = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new object[] { new { type = "text", text = "done" } },
                    ["usage"] = new Dictionary<string, object>
                    {
                        ["input_tokens"] = input,
                        ["cache_creation_input_tokens"] = cacheCreation,
                        ["cache_read_input_tokens"] = cacheRead,
                        ["output_tokens"] = 439,
                        ["cache_creation"] = new { ephemeral_1h_input_tokens = cacheCreation, ephemeral_5m_input_tokens = 0 },
                    },
                },
                ["requestedModel"] = requestedModel,
                ["type"] = "assistant",
                ["uuid"] = Guid.NewGuid().ToString(),
                ["cwd"] = cwd,
            });

        [Fact]
        public void Context_usage_is_input_plus_cache_read_plus_cache_creation_and_ignores_output()
        {
            var line = AssistantWithUsage(_projectRoot, "claude-opus-5-5", 2, 221579, 61, requestedModel: "claude-opus-5-5");

            Assert.True(SessionCatalog.TryReadContextUsage(line, out var tokens, out var model, out var requested));
            Assert.Equal(2 + 221579 + 61, tokens);
            Assert.Equal("claude-opus-5-5", model);
            Assert.Equal("claude-opus-5-5", requested);
        }

        [Fact]
        public void Context_usage_counts_a_missing_field_as_zero()
        {
            var line = JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new { model = "claude-opus-5", usage = new { input_tokens = 5, cache_read_input_tokens = 1000 } },
            });

            Assert.True(SessionCatalog.TryReadContextUsage(line, out var tokens, out _, out _));
            Assert.Equal(1005, tokens);
        }

        [Fact]
        public void Context_usage_rejects_lines_that_are_not_a_main_thread_assistant_turn_with_usage()
        {
            Assert.False(SessionCatalog.TryReadContextUsage(User("hi", _projectRoot), out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage(Assistant("no usage", _projectRoot), out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage(
                AssistantWithUsage(_projectRoot, "claude-opus-5-5", 1, 5000, 0, isSidechain: true), out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage(
                AssistantWithUsage(_projectRoot, "<synthetic>", 1, 5000, 0), out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage(
                AssistantWithUsage(_projectRoot, "claude-opus-5-5", 0, 0, 0), out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage("{\"type\":\"assistant\",\"message\":", out _, out _, out _));
            Assert.False(SessionCatalog.TryReadContextUsage(null, out _, out _, out _));
        }

        [Theory]
        // every current model is 1M (model table cached 2026-10-06), including the ids seen in real transcripts
        [InlineData("claude-opus-5-5", 1_000_000)]
        [InlineData("claude-opus-5", 1_000_000)]
        [InlineData("claude-fable-5-1", 1_000_000)]
        [InlineData("claude-mythos-5-1", 1_000_000)]
        [InlineData("claude-sonnet-5-5", 1_000_000)]
        [InlineData("claude-haiku-5-5", 1_000_000)]
        [InlineData("claude-opus-4-8", 1_000_000)]
        [InlineData("claude-sonnet-4-6", 1_000_000)]
        // the 200k ones, with and without date suffixes (a date is not a minor version)
        [InlineData("claude-haiku-4-5", 200_000)]
        [InlineData("claude-haiku-4-5-20251001", 200_000)]
        [InlineData("claude-sonnet-4-5-20250929", 200_000)]
        [InlineData("claude-sonnet-4-20250514", 200_000)]
        [InlineData("claude-opus-4-1", 200_000)]
        [InlineData("claude-3-5-sonnet-20241022", 200_000)]
        [InlineData("claude-3-opus-20240229", 200_000)]
        // Claude Code's 1M selector
        [InlineData("claude-sonnet-4-5[1m]", 1_000_000)]
        [InlineData("claude-sonnet-4-5-20250929[1M]", 1_000_000)]
        // unknown: tokens only
        [InlineData("<synthetic>", null)]
        [InlineData("gpt-5", null)]
        [InlineData("claude-instant-1", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Context_window_from_the_model_id(string model, int? expected)
        {
            Assert.Equal(expected, SessionCatalog.ContextWindowFor(model));
        }

        [Fact]
        public void Context_window_takes_the_requested_models_1m_suffix_and_drops_a_window_the_tokens_disprove()
        {
            Assert.Equal(1_000_000, SessionCatalog.ResolveContextWindow(150_000, "claude-sonnet-4-5-20250929", "claude-sonnet-4-5[1m]"));
            Assert.Equal(200_000, SessionCatalog.ResolveContextWindow(150_000, "claude-sonnet-4-5-20250929", null));
            Assert.Null(SessionCatalog.ResolveContextWindow(250_000, "claude-sonnet-4-5-20250929", null));
            Assert.Null(SessionCatalog.ResolveContextWindow(1000, "<synthetic>", null));
            Assert.Null(SessionCatalog.ResolveContextWindow(null, "claude-opus-5-5", null));
        }

        [Fact]
        public void Listed_session_carries_the_context_of_its_LAST_main_thread_assistant_turn()
        {
            var id = Write(_rootFolder, Now.AddHours(-1),
                User("go", _projectRoot),
                AssistantWithUsage(_projectRoot, "claude-haiku-4-5", 10, 50_000, 0),
                AssistantWithUsage(_projectRoot, "claude-haiku-4-5", 3, 150_000, 1_000),
                // a later subagent turn and a synthetic error turn must not replace it
                AssistantWithUsage(_projectRoot, "claude-haiku-4-5", 1, 5, 0, isSidechain: true),
                AssistantWithUsage(_projectRoot, "<synthetic>", 0, 0, 0),
                User("thanks", _projectRoot));

            var entry = Assert.Single(List());
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(151_003, entry.ContextTokens);
            Assert.Equal(200_000, entry.ContextWindow);
        }

        [Fact]
        public void Listed_session_without_usage_has_no_context()
        {
            WriteSimple(_rootFolder, Now.AddHours(-1));

            var entry = Assert.Single(List());
            Assert.Null(entry.ContextTokens);
            Assert.Null(entry.ContextWindow);
        }

        [Fact]
        public void Context_is_found_behind_a_final_line_larger_than_the_tail_window()
        {
            // One big tool result after the last reply: the 64KB tail holds no complete line with usage.
            var bigResult = UserBlocks(new object[]
            {
                new { type = "tool_result", tool_use_id = "toolu_x", content = new string('r', SessionCatalog.TailBytes + 10_000) },
            }, _projectRoot);
            Write(_rootFolder, Now.AddHours(-1),
                User("go", _projectRoot),
                AssistantWithUsage(_projectRoot, "claude-opus-5-5", 2, 300_000, 0),
                bigResult);

            var entry = Assert.Single(List());
            Assert.Equal(300_002, entry.ContextTokens);
            Assert.Equal(1_000_000, entry.ContextWindow);
        }

        // ---- item 7: helper summaries ----

        private static string ToolUse(string id, string name, string cwd)
            => JsonSerializer.Serialize(new
            {
                type = "assistant",
                isSidechain = false,
                message = new
                {
                    model = "claude-opus-5-5",
                    role = "assistant",
                    content = new object[] { new { type = "tool_use", id, name, input = new { } } },
                },
                uuid = Guid.NewGuid().ToString(),
                cwd,
            });

        private static string ToolResultText(string toolUseId, string text, string cwd)
            => UserBlocks(new object[]
            {
                new { tool_use_id = toolUseId, type = "tool_result", content = new object[] { new { type = "text", text } } },
            }, cwd);

        // The exact header mcp/index.js get_my_spawn_job prints, then the job.
        private static string JobText(string job) =>
            "\U0001F4CB YOUR JOB from Alice (you are Notes). Carry it out now. When you report back, message Alice.\n\n" + job;

        [Fact]
        public void Spawn_job_summary_is_the_first_line_of_the_job_body_not_the_header()
        {
            Assert.Equal(
                "You are Notes, resuming ticket c133b27d (the unread-note badge).",
                SessionCatalog.SpawnJobSummaryFrom(JobText("You are Notes, resuming ticket c133b27d (the unread-note badge).\n\nFIX THESE (item 2):")));
            Assert.Equal(
                "Fix the badge",
                SessionCatalog.SpawnJobSummaryFrom(JobText("\n\n## Fix the badge\n- details")));
        }

        [Fact]
        public void Spawn_job_summary_rejects_text_that_is_not_a_job()
        {
            Assert.Null(SessionCatalog.SpawnJobSummaryFrom("No spawn job is waiting for you."));
            Assert.Null(SessionCatalog.SpawnJobSummaryFrom("Error: MultiTerminal is not reachable\n\nretry later"));
            Assert.Null(SessionCatalog.SpawnJobSummaryFrom(JobText("   \n\n  ")));
            Assert.Null(SessionCatalog.SpawnJobSummaryFrom(null));
        }

        [Fact]
        public void Spawn_job_summary_strips_control_and_bidi_characters_and_is_capped()
        {
            // U+202E (right-to-left override) would make the line render reversed; U+0007 is BEL.
            var summary = SessionCatalog.SpawnJobSummaryFrom(JobText("Fix‮ the\u0007 badge​ now"));
            Assert.Equal("Fix the badge now", summary);

            var longLine = SessionCatalog.SpawnJobSummaryFrom(JobText(new string('a', 500)));
            Assert.Equal(SessionCatalog.SummaryMaxChars, longLine.Length);
            Assert.EndsWith("…", longLine, StringComparison.Ordinal);
        }

        [Fact]
        public void Spawn_job_is_found_only_in_the_result_of_the_get_my_spawn_job_call()
        {
            // Each decoy result NAMES get_my_spawn_job, as ToolSearch's real result does: a decoy that did
            // not would be skipped by the line prefilter and never parsed, so it would prove nothing
            // about the tool_use_id match (falsification showed exactly that for the first draft).
            var lines = new[]
            {
                ToolUse("toolu_search", "ToolSearch", _projectRoot),
                ToolResultText("toolu_search", JobText("Decoy before: get_my_spawn_job loaded"), _projectRoot),
                ToolUse("toolu_job", "mcp__multiterminal__get_my_spawn_job", _projectRoot),
                ToolResultText("toolu_read", JobText("Decoy after: a file read mentioning get_my_spawn_job"), _projectRoot),
                ToolResultText("toolu_job", JobText("The real job"), _projectRoot),
            };

            Assert.Equal("The real job", SessionCatalog.SpawnJobSummaryFromLines(lines));
        }

        private string WriteHelperTranscript(string job, bool isHelper)
        {
            var cwd = _projectRoot;
            var lines = new List<string>
            {
                Hook(isHelper ? "## Spawned Agent: Notes\nSpawned by: Alice" : "MULTITERMINAL_NAME=Bob", cwd),
                User("collect my job", cwd),
            };
            // Real helper transcripts carry ~600KB of hook output and tool loading before the job
            // result (measured 2026-10-18: lines 53-55, 630-690KB in), well past the 256KB head.
            var padding = new string('p', 8000);
            for (var i = 0; i < 80; i++)
                lines.Add(JsonSerializer.Serialize(new { type = "progress", data = padding, cwd }));
            lines.Add(ToolUse("toolu_job", "mcp__multiterminal__get_my_spawn_job", cwd));
            lines.Add(ToolResultText("toolu_job", JobText(job), cwd));
            lines.Add(Assistant("on it", cwd));
            return Write(_rootFolder, Now.AddHours(-1), lines.ToArray());
        }

        [Fact]
        public void Helper_summary_is_its_spawn_job_even_beyond_the_head_window()
        {
            var id = WriteHelperTranscript("You are Notes, resuming ticket c133b27d (the unread-note badge).", isHelper: true);
            Assert.True(new FileInfo(Path.Combine(_rootFolder, id + ".jsonl")).Length > SessionCatalog.HeadMaxBytes * 2,
                "fixture must put the job well beyond the head window");

            var entry = Assert.Single(List());
            Assert.True(entry.IsHelper);
            Assert.Equal("You are Notes, resuming ticket c133b27d (the unread-note badge).", entry.Summary);
        }

        [Fact]
        public void Non_helper_summary_ignores_a_spawn_job_result_and_keeps_the_first_prompt()
        {
            WriteHelperTranscript("You are Notes, resuming ticket c133b27d.", isHelper: false);

            var entry = Assert.Single(List());
            Assert.False(entry.IsHelper);
            Assert.Equal("collect my job", entry.Summary);
        }

        // ---- review of 702e4fa: read budget, head-continuing job scan, display sanitising ----

        private const string Job = "You are Notes, resuming ticket c133b27d.";

        // A helper transcript laid out like the real ones: hook, first prompt, ~640KB of padding, the
        // job call and result, a reply WITH usage, then optionally one >64KB tool result so the usage
        // is only within reach of the ContextTailBytes fallback.
        private string WriteBudgetHelper(DateTime when, bool usageBehindBigResult)
        {
            var cwd = _projectRoot;
            var lines = new List<string>
            {
                Hook("## Spawned Agent: Notes\nSpawned by: Alice", cwd),
                User("collect my job", cwd),
            };
            var padding = new string('p', 8000);
            for (var i = 0; i < 80; i++)
                lines.Add(JsonSerializer.Serialize(new { type = "progress", data = padding, cwd }));
            lines.Add(ToolUse("toolu_job", "mcp__multiterminal__get_my_spawn_job", cwd));
            lines.Add(ToolResultText("toolu_job", JobText(Job), cwd));
            lines.Add(AssistantWithUsage(cwd, "claude-opus-5-5", 2, 300_000, 0));
            if (usageBehindBigResult)
            {
                lines.Add(UserBlocks(new object[]
                {
                    new { type = "tool_result", tool_use_id = "toolu_x", content = new string('r', SessionCatalog.TailBytes + 10_000) },
                }, cwd));
            }
            return Write(_rootFolder, when, lines.ToArray());
        }

        private long LengthOf(string id) => new FileInfo(Path.Combine(_rootFolder, id + ".jsonl")).Length;

        private static long Mandatory(long length) =>
            Math.Min(length, SessionCatalog.HeadMaxBytes) + Math.Min(length, SessionCatalog.TailBytes);

        private static long Optional(long length) =>
            Math.Min(length, SessionCatalog.ContextTailBytes) +
            (Math.Min(length, SessionCatalog.SpawnJobScanMaxBytes) - SessionCatalog.HeadMaxBytes);

        [Fact]
        public void Optional_reads_stop_when_the_byte_budget_is_spent_and_the_entries_are_still_listed()
        {
            var newer = WriteBudgetHelper(Now.AddHours(-1), usageBehindBigResult: true);
            var older = WriteBudgetHelper(Now.AddHours(-2), usageBehindBigResult: true);
            long a = LengthOf(newer), b = LengthOf(older);
            Assert.True(a > SessionCatalog.ContextTailBytes && a < SessionCatalog.SpawnJobScanMaxBytes, "fixture size");
            Assert.Equal(24L * 1024 * 1024, SessionCatalog.DefaultReadByteBudget);

            // Control: with the default budget both get the job summary and the context from the
            // larger tail, and every byte read is accounted for.
            var full = Catalog();
            var entries = full.List(_projectRoot, new HashSet<string>(), Now).ToDictionary(e => e.SessionId);
            Assert.Equal(Job, entries[newer].Summary);
            Assert.Equal(300_002, entries[newer].ContextTokens);
            Assert.Equal(Job, entries[older].Summary);
            Assert.Equal(300_002, entries[older].ContextTokens);
            Assert.Equal(Mandatory(a) + Optional(a) + Mandatory(b) + Optional(b), full.BytesReadLastList);

            // The budget is per List call and cumulative: enough for the newer entry's optional reads,
            // none left for the older one's. The older entry is still listed, with "no context" and the
            // fallback summary (its first prompt).
            var limited = Catalog();
            limited.ReadByteBudget = Mandatory(a) + Optional(a) + Mandatory(b);
            entries = limited.List(_projectRoot, new HashSet<string>(), Now).ToDictionary(e => e.SessionId);
            Assert.Equal(2, entries.Count);
            Assert.Equal(Job, entries[newer].Summary);
            Assert.Equal(300_002, entries[newer].ContextTokens);
            Assert.Equal("collect my job", entries[older].Summary);
            Assert.Null(entries[older].ContextTokens);
            Assert.True(entries[older].IsHelper);
            Assert.Equal(limited.ReadByteBudget, limited.BytesReadLastList);

            // An exhausted budget: only the mandatory reads, every entry still listed.
            var none = Catalog();
            none.ReadByteBudget = 0;
            var bare = none.List(_projectRoot, new HashSet<string>(), Now);
            Assert.Equal(2, bare.Count);
            Assert.All(bare, e => Assert.Null(e.ContextTokens));
            Assert.All(bare, e => Assert.Equal("collect my job", e.Summary));
            Assert.Equal(Mandatory(a) + Mandatory(b), none.BytesReadLastList);
        }

        [Fact]
        public void Helper_job_scan_continues_from_the_end_of_the_head_and_never_rereads_it()
        {
            // Beyond the head: the scan reads only [HeadMaxBytes, length). Re-reading the head would add
            // HeadMaxBytes to the count.
            var beyond = WriteBudgetHelper(Now.AddHours(-1), usageBehindBigResult: false);
            var length = LengthOf(beyond);
            var catalog = Catalog();
            var entry = Assert.Single(catalog.List(_projectRoot, new HashSet<string>(), Now));
            Assert.Equal(Job, entry.Summary);
            Assert.Equal(300_002, entry.ContextTokens); // usage in the 64KB tail: no fallback read
            Assert.Equal(Mandatory(length) + (length - SessionCatalog.HeadMaxBytes), catalog.BytesReadLastList);
        }

        [Fact]
        public void Helper_job_inside_the_head_is_found_without_any_further_read()
        {
            var cwd = _projectRoot;
            var lines = new List<string>
            {
                Hook("## Spawned Agent: Notes\nSpawned by: Alice", cwd),
                User("collect my job", cwd),
                ToolUse("toolu_job", "mcp__multiterminal__get_my_spawn_job", cwd),
                ToolResultText("toolu_job", JobText(Job), cwd),
            };
            var padding = new string('p', 8000);
            for (var i = 0; i < 80; i++)
                lines.Add(JsonSerializer.Serialize(new { type = "progress", data = padding, cwd }));
            lines.Add(AssistantWithUsage(cwd, "claude-opus-5-5", 2, 300_000, 0));
            var id = Write(_rootFolder, Now.AddHours(-1), lines.ToArray());
            var length = LengthOf(id);
            Assert.True(length > SessionCatalog.HeadMaxBytes, "fixture must exceed the head window");

            var catalog = Catalog();
            var entry = Assert.Single(catalog.List(_projectRoot, new HashSet<string>(), Now));
            Assert.Equal(Job, entry.Summary);
            Assert.Equal(Mandatory(length), catalog.BytesReadLastList);
        }

        [Fact]
        public void Helper_job_result_straddling_the_head_boundary_is_reassembled()
        {
            var cwd = _projectRoot;
            var lines = new List<string>
            {
                Hook("## Spawned Agent: Notes\nSpawned by: Alice", cwd),
                User("collect my job", cwd),
            };
            var toolUse = ToolUse("toolu_job", "mcp__multiterminal__get_my_spawn_job", cwd);
            long bytes = lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1);
            var padding = new string('p', 1000);
            while (bytes + 1100 + Encoding.UTF8.GetByteCount(toolUse) + 1 < SessionCatalog.HeadMaxBytes - 2000)
            {
                var pad = JsonSerializer.Serialize(new { type = "progress", data = padding, cwd });
                lines.Add(pad);
                bytes += Encoding.UTF8.GetByteCount(pad) + 1;
            }
            lines.Add(toolUse);
            bytes += Encoding.UTF8.GetByteCount(toolUse) + 1;
            // The job's first line, then 20KB more of job body: the result line spans the boundary.
            var result = ToolResultText("toolu_job", JobText(Job + "\n" + new string('j', 20_000)), cwd);
            lines.Add(result);
            lines.Add(AssistantWithUsage(cwd, "claude-opus-5-5", 2, 300_000, 0));
            Assert.True(bytes < SessionCatalog.HeadMaxBytes && bytes + Encoding.UTF8.GetByteCount(result) > SessionCatalog.HeadMaxBytes,
                "the result line must straddle the head boundary");
            Write(_rootFolder, Now.AddHours(-1), lines.ToArray());

            var entry = Assert.Single(List());
            Assert.Equal(Job, entry.Summary);
        }

        [Fact]
        public void A_rejected_helper_gets_no_optional_reads()
        {
            var id = WriteBudgetHelper(Now.AddHours(-1), usageBehindBigResult: true);
            var catalog = Catalog();
            catalog.DirectoryExists = _ => false; // its cwd (the project root) is "gone"
            Assert.Empty(catalog.List(_projectRoot, new HashSet<string>(), Now));
            Assert.Equal(1, catalog.TranscriptsReadLastList);
            Assert.Equal(Mandatory(LengthOf(id)), catalog.BytesReadLastList);
        }

        [Fact]
        public void Agent_and_spawner_names_are_sanitised_from_the_transcript_and_from_the_lookup()
        {
            // U+202E right-to-left override, U+2066 left-to-right isolate, U+200B zero-width space.
            var fromHook = Write(_rootFolder, Now.AddHours(-1),
                Hook("MULTITERMINAL_NAME=Ev‮il\nSpawned by: Al⁦ice​", _projectRoot),
                User("x", _projectRoot));
            var fromLookup = Write(_rootFolder, Now.AddHours(-2),
                Hook("MULTITERMINAL_NAME=Plain", _projectRoot),
                User("y", _projectRoot));

            var catalog = Catalog(names: id => id == fromLookup ? "Bo​b‮" : null);
            var entries = List(catalog).ToDictionary(e => e.SessionId);

            Assert.Equal("Evil", entries[fromHook].AgentName);
            Assert.Equal("Alice", entries[fromHook].SpawnedBy);
            Assert.Equal("Bob", entries[fromLookup].AgentName);
        }

        [Fact]
        public void Display_text_drops_control_bidi_and_zero_width_characters_and_caps_only_when_asked()
        {
            Assert.Equal("abc def", SessionCatalog.ToDisplayText("a‮b​c\t\u0007 def"));
            Assert.Null(SessionCatalog.ToDisplayText("‮​"));
            Assert.Equal(300, SessionCatalog.ToDisplayText(new string('a', 300)).Length);
            Assert.Equal(10, SessionCatalog.ToDisplayText(new string('a', 300), 10).Length);
        }

        // ---- TryGetEntry (task 7d20e94c item 3): one session by id, the same answer as List ----

        private SessionCatalogEntry Get(string id, SessionCatalog catalog = null, ISet<string> live = null)
            => (catalog ?? Catalog()).TryGetEntry(_projectRoot, id, live ?? new HashSet<string>(), Now);

        [Fact]
        public void TryGetEntry_finds_a_session_in_the_root_folder()
        {
            var id = WriteSimple(_rootFolder, Now.AddHours(-1), "root work");
            WriteSimple(_rootFolder, Now.AddHours(-2), "another");

            var entry = Get(id);

            Assert.NotNull(entry);
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(_projectRoot, entry.Cwd);
            Assert.Null(entry.WorktreeId);
            Assert.Equal("root work", entry.Summary);
        }

        [Fact]
        public void TryGetEntry_finds_a_session_in_a_worktree_folder()
        {
            var id = WriteSimple(_worktreeFolder, Now.AddHours(-1));

            var entry = Get(id);

            Assert.NotNull(entry);
            Assert.Equal(_worktreeDir, entry.Cwd);
            Assert.Equal(WorktreeId, entry.WorktreeId);
            Assert.Equal(Path.Combine(_worktreeFolder, id + ".jsonl"), entry.TranscriptPath);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData(@"..\x")]
        [InlineData("a/b")]
        [InlineData("TRAVERSAL")]
        public void TryGetEntry_rejects_a_non_GUID_id_before_any_filesystem_call(string id)
        {
            // A real, resumable transcript that a path built from the id WOULD reach:
            // <worktreeFolder>\..\<rootFolderName>\<guid>.jsonl is the root folder's transcript.
            var guid = WriteSimple(_rootFolder, Now.AddHours(-1));
            if (id == "TRAVERSAL")
                id = @"..\" + Path.GetFileName(_rootFolder) + @"\" + guid;
            Assert.NotNull(Get(guid)); // control: the transcript itself is offered

            var probed = new List<string>();
            var catalog = Catalog();
            catalog.DirectoryExists = p => { probed.Add(p); return Directory.Exists(p); };

            Assert.Null(catalog.TryGetEntry(_projectRoot, id, new HashSet<string>(), Now));
            // GetCandidateFolders probes the existing worktree (abcd1234) through the seam, so an empty
            // probe list means the id was refused before the folders were even listed.
            Assert.Empty(probed);
        }

        [Fact]
        public void TryGetEntry_returns_null_for_a_GUID_with_no_transcript()
        {
            WriteSimple(_rootFolder, Now.AddHours(-1));
            Assert.Null(Get(Guid.NewGuid().ToString()));
        }

        [Fact]
        public void TryGetEntry_returns_null_for_a_live_session_ignoring_case()
        {
            var id = WriteSimple(_rootFolder, Now.AddHours(-1));
            Assert.NotNull(Get(id)); // control

            Assert.Null(Get(id, live: new HashSet<string> { id.ToUpperInvariant() }));
            Assert.Null(Get(id.ToUpperInvariant(), live: new HashSet<string> { id }));
        }

        [Fact]
        public void TryGetEntry_refuses_every_transcript_List_rejects()
        {
            // UNC-only cwd in the worktree's folder: no root fallback there, so List drops it.
            var unc = Write(_worktreeFolder, Now.AddHours(-1),
                User("hi", @"\\evilhost\share"), Assistant("ok", @"\\evilhost\share"));
            // A deleted worktree's folder.
            var goneDir = Path.Combine(_projectRoot, ".claude", "worktrees", "deadbeef");
            var goneFolder = Directory.CreateDirectory(
                Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(goneDir))).FullName;
            var gone = Write(goneFolder, Now.AddHours(-2), User("work", goneDir), Assistant("done", goneDir));
            // Root cwds, stored in the worktree's folder: resuming from the root would not find it.
            var mismatched = Write(_worktreeFolder, Now.AddHours(-3), User("x", _projectRoot), Assistant("y", _projectRoot));

            var probed = new List<string>();
            var catalog = Catalog();
            catalog.DirectoryExists = p => { probed.Add(p); return Directory.Exists(p); };

            Assert.Empty(List(catalog)); // the same three, as List sees them
            Assert.Null(Get(unc, catalog));
            Assert.Null(Get(gone, catalog));
            Assert.Null(Get(mismatched, catalog));
            Assert.DoesNotContain(probed, p => p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal));
        }

        [Fact]
        public void TryGetEntry_when_a_session_is_in_two_folders_the_newest_copy_decides()
        {
            // The same id in both folders. The newer copy is in the worktree folder and resumable.
            var id = WriteSimple(_worktreeFolder, Now.AddHours(-1));
            var older = Path.Combine(_rootFolder, id + ".jsonl");
            File.WriteAllText(older, User("old", _projectRoot) + "\n" + Assistant("o", _projectRoot) + "\n");
            File.SetLastWriteTimeUtc(older, Now.AddHours(-5));

            var listed = Assert.Single(List());
            var entry = Get(id);
            Assert.Equal(_worktreeDir, listed.Cwd);
            Assert.Equal(_worktreeDir, entry.Cwd);

            // Now the newest copy is unresumable (root cwds in the worktree folder): List drops the id
            // rather than offering the older copy, and so must TryGetEntry.
            var newer = Path.Combine(_worktreeFolder, id + ".jsonl");
            File.WriteAllText(newer, User("x", _projectRoot) + "\n" + Assistant("y", _projectRoot) + "\n");
            File.SetLastWriteTimeUtc(newer, Now.AddHours(-1));

            Assert.Empty(List());
            Assert.Null(Get(id));
        }

        [Fact]
        public void TryGetEntry_matches_Lists_entry_field_for_field()
        {
            // A helper with its job beyond the head and its usage behind a big final result: every
            // optional read and every lookup is exercised.
            var helper = WriteBudgetHelper(Now.AddHours(-1), usageBehindBigResult: true);
            var plain = WriteSimple(_worktreeFolder, Now - TimeSpan.FromSeconds(30), "recent");
            var catalog = Catalog(names: id => id == plain ? "Bo​b" : null, summaries: _ => null);

            var listed = catalog.List(_projectRoot, new HashSet<string>(), Now).ToDictionary(e => e.SessionId);
            var listBytes = catalog.BytesReadLastList;
            var listReads = catalog.TranscriptsReadLastList;
            Assert.Equal(2, listed.Count);

            foreach (var id in new[] { helper, plain })
            {
                var got = catalog.TryGetEntry(_projectRoot, id, new HashSet<string>(), Now);
                Assert.NotNull(got);
                Assert.Equal(JsonSerializer.Serialize(listed[id]), JsonSerializer.Serialize(got));
            }

            Assert.Equal(Job, listed[helper].Summary);
            Assert.Equal(300_002, listed[helper].ContextTokens);
            Assert.Equal("Bob", listed[plain].AgentName);
            Assert.Equal(SessionCatalogStatus.PossiblyOpenElsewhere, listed[plain].Status);
            // TryGetEntry keeps its own meter: List's counters are untouched.
            Assert.Equal(listBytes, catalog.BytesReadLastList);
            Assert.Equal(listReads, catalog.TranscriptsReadLastList);
        }
    }
}
