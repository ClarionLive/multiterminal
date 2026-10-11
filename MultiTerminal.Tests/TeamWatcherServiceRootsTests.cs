using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec item 12: the team watcher covers every configured Claude profile folder.
    /// The pure facts take their roots as arguments. The watcher facts run the real service over temp
    /// folders with an injected root list and no refresh timer; nothing reads the real settings.txt or
    /// any real profile folder. (The service does read two hook tracking files in %TEMP% by team name
    /// and by transcript file name; every name here carries a fresh GUID so no real entry can match.)
    /// </summary>
    public sealed class TeamWatcherServiceRootsTests : IDisposable
    {
        private const string Home = @"C:\Users\Dev";
        private const string DefaultRoot = Home + @"\.claude\";
        private const string WorkRoot = Home + @"\.claude-work\";

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

        private readonly string _base = Path.Combine(Path.GetTempPath(), $"mt_teamwatch_test_{Guid.NewGuid():N}");
        private readonly string _id = Guid.NewGuid().ToString("N").Substring(0, 12);

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ── Pure: which folders are watched ──────────────────────────────────

        [Fact]
        public void Watch_plan_has_teams_and_projects_under_each_root_and_nothing_else()
        {
            var plan = TeamWatcherService.BuildWatchPlan(new[] { DefaultRoot, WorkRoot }, DefaultRoot, "H--Repo");

            Assert.Equal(2, plan.Count);

            Assert.Equal(DefaultRoot, plan[0].Root);
            Assert.Equal(Home + @"\.claude\teams", plan[0].TeamsDir);
            Assert.Equal(Home + @"\.claude\projects", plan[0].ProjectsDir);
            Assert.Equal(Home + @"\.claude\projects\H--Repo", plan[0].AppProjectDir);
            Assert.True(plan[0].IsDefault);

            Assert.Equal(WorkRoot, plan[1].Root);
            Assert.Equal(Home + @"\.claude-work\teams", plan[1].TeamsDir);
            Assert.Equal(Home + @"\.claude-work\projects", plan[1].ProjectsDir);
            Assert.Equal(Home + @"\.claude-work\projects\H--Repo", plan[1].AppProjectDir);
            Assert.False(plan[1].IsDefault);
        }

        [Fact]
        public void Watch_plan_drops_blank_and_duplicate_roots_and_adds_a_missing_separator()
        {
            var plan = TeamWatcherService.BuildWatchPlan(
                new[] { null, "  ", WorkRoot, WorkRoot.ToUpperInvariant(), Home + @"\.claude" },
                Home + @"\.claude",
                "slug");

            Assert.Equal(new[] { WorkRoot, DefaultRoot }, plan.Select(p => p.Root).ToArray());
            Assert.Equal(new[] { false, true }, plan.Select(p => p.IsDefault).ToArray());

            Assert.Empty(TeamWatcherService.BuildWatchPlan(null, DefaultRoot, "slug"));
            Assert.Empty(TeamWatcherService.BuildWatchPlan(Array.Empty<string>(), DefaultRoot, "slug"));
        }

        // ── Pure: the keying decision ────────────────────────────────────────

        [Fact]
        public void Team_key_is_the_bare_name_under_Default_and_differs_for_the_same_name_under_another_root()
        {
            string underDefault = TeamWatcherService.BuildTeamKey(DefaultRoot, "review", isDefaultRoot: true);
            string underWork = TeamWatcherService.BuildTeamKey(WorkRoot, "review", isDefaultRoot: false);
            string underThird = TeamWatcherService.BuildTeamKey(@"D:\cfg\", "review", isDefaultRoot: false);

            Assert.Equal("review", underDefault);
            Assert.NotEqual(underDefault, underWork);
            Assert.NotEqual(underWork, underThird);

            // MainForm builds panel keys as "team:{key}:{member}" and removes a team's panels by the
            // prefix "team:{key}:". Neither team's prefix may match the other's panel keys.
            Assert.False($"team:{underWork}:m".StartsWith($"team:{underDefault}:", StringComparison.Ordinal));
            Assert.False($"team:{underDefault}:m".StartsWith($"team:{underWork}:", StringComparison.Ordinal));
        }

        // ── Pure: slug resolution for a profile ──────────────────────────────

        [Fact]
        public void Lead_project_dir_is_under_the_root_it_is_asked_for()
        {
            Assert.Equal(
                Home + @"\.claude-work\projects\H--Dev-Foo",
                TeamWatcherService.GetLeadProjectDir(WorkRoot, @"H:\Dev\Foo"));
            Assert.Equal(
                Home + @"\.claude\projects\H--Dev-Foo",
                TeamWatcherService.GetLeadProjectDir(DefaultRoot, @"H:\Dev\Foo\"));
        }

        [Fact]
        public void Config_root_for_a_pane_follows_its_profile_name_and_is_null_when_the_profile_cannot_be_resolved()
        {
            var work = new ClaudeProfile("Work", Home + @"\.claude-work");
            var gone = new ClaudeProfile("Gone", Home + @"\.claude-gone");
            var ctx = new ClaudeProfileContext(
                new[] { work, gone },
                null,
                Home,
                dir => !dir.EndsWith("gone", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(DefaultRoot, TeamWatcherService.GetConfigRootForProfileName(null, ctx));
            Assert.Equal(DefaultRoot, TeamWatcherService.GetConfigRootForProfileName("  ", ctx));
            Assert.Equal(WorkRoot, TeamWatcherService.GetConfigRootForProfileName("work", ctx));

            // Not Default: a pane whose profile is unknown or whose folder is missing belongs to no root.
            Assert.Null(TeamWatcherService.GetConfigRootForProfileName("Nobody", ctx));
            Assert.Null(TeamWatcherService.GetConfigRootForProfileName("Gone", ctx));

            // One pane-to-folder resolver (item 35): for every name the watcher's answer is
            // ClaudeProfileDataRoots.ForProfileName's folder plus the separator, and null where that is null.
            foreach (string name in new[] { null, "  ", "work", "Nobody", "Gone" })
            {
                var root = ClaudeProfileDataRoots.ForProfileName(name, ctx);
                Assert.Equal(
                    root == null ? null : root.ConfigDir + Path.DirectorySeparatorChar,
                    TeamWatcherService.GetConfigRootForProfileName(name, ctx));
            }

            // That resolver also requires Default's own folder to exist: with none, a Default pane
            // matches no root (there is no folder a team of its could be in).
            var noDefaultFolder = ctx with { DirectoryExists = dir => dir.EndsWith("work", StringComparison.OrdinalIgnoreCase) };
            Assert.Null(TeamWatcherService.GetConfigRootForProfileName(null, noDefaultFolder));
            Assert.Equal(WorkRoot, TeamWatcherService.GetConfigRootForProfileName("Work", noDefaultFolder));
        }

        // ── Real folders ─────────────────────────────────────────────────────

        [Fact]
        public async Task A_team_created_under_a_second_root_is_discovered_with_that_root()
        {
            string rootA = MakeRoot("a");
            string rootB = MakeRoot("b");
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();

            string team = "team-" + _id;
            string transcript = AddTeam(rootB, team, "bee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());

            var found = await h.WaitForDiscovery(e => e.MemberName == "bee-" + _id);

            Assert.NotNull(found);
            Assert.Equal(rootB, found.ConfigRoot);
            Assert.Equal(team, found.TeamName);
            Assert.Equal(team + "|" + rootB, found.TeamKey);
            Assert.True(((TranscriptTailer)found.Tailer).HasTranscriptFile(transcript));
        }

        /// <summary>
        /// The discriminator for the keying decision: the same team name, the same lead folder and the
        /// same lead session id under two roots, differing only in the root and the member.
        /// </summary>
        [Fact]
        public async Task Same_team_name_under_two_roots_stays_two_teams()
        {
            string rootA = MakeRoot("a");
            string rootB = MakeRoot("b");
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();

            string team = "team-" + _id;
            string session = Guid.NewGuid().ToString();
            string memberA = "ay-" + _id;
            string memberB = "bee-" + _id;
            string transcriptA = AddTeam(rootA, team, memberA, @"H:\Lead\Dir", session);
            string transcriptB = AddTeam(rootB, team, memberB, @"H:\Lead\Dir", session);

            var foundA = await h.WaitForDiscovery(e => e.MemberName == memberA);
            var foundB = await h.WaitForDiscovery(e => e.MemberName == memberB);

            Assert.NotNull(foundA);
            Assert.NotNull(foundB);
            Assert.Equal(team, foundA.TeamName);
            Assert.Equal(team, foundB.TeamName);
            Assert.Equal(rootA, foundA.ConfigRoot);
            Assert.Equal(rootB, foundB.ConfigRoot);
            Assert.Equal(team, foundA.TeamKey);
            Assert.NotEqual(foundA.TeamKey, foundB.TeamKey);

            // Each team's member reads the transcript under its OWN root, although the lead folder and
            // session id are identical.
            Assert.True(((TranscriptTailer)foundA.Tailer).HasTranscriptFile(transcriptA));
            Assert.False(((TranscriptTailer)foundA.Tailer).HasTranscriptFile(transcriptB));
            Assert.True(((TranscriptTailer)foundB.Tailer).HasTranscriptFile(transcriptB));
            Assert.False(((TranscriptTailer)foundB.Tailer).HasTranscriptFile(transcriptA));

            // Dropping root B removes B's team only, and names only B's member.
            h.Roots = new[] { rootA };
            h.Service.RefreshRoots();

            var removed = Assert.Single(h.Removed);
            Assert.Equal(foundB.TeamKey, removed.TeamKey);
            Assert.Equal(rootB, removed.ConfigRoot);
            Assert.Equal(new[] { memberB }, removed.MemberNames.ToArray());
            Assert.Equal(new[] { transcriptB }, removed.TranscriptPaths.ToArray());
        }

        [Fact]
        public async Task A_missing_root_folder_is_skipped_without_throwing_and_watched_once_it_exists()
        {
            string rootA = MakeRoot("a");
            string rootC = Path.Combine(_base, "c") + Path.DirectorySeparatorChar; // not created
            using var h = new Harness(new[] { rootA, rootC }, rootA);

            h.Service.StartWatching();
            h.Service.RefreshRoots();
            Assert.False(Directory.Exists(rootC)); // the watcher must not create a profile folder

            Directory.CreateDirectory(Path.Combine(rootC, "teams"));
            Directory.CreateDirectory(Path.Combine(rootC, "projects"));
            h.Service.RefreshRoots();

            AddTeam(rootC, "team-" + _id, "cee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());
            var found = await h.WaitForDiscovery(e => e.MemberName == "cee-" + _id);

            Assert.NotNull(found);
            Assert.Equal(rootC, found.ConfigRoot);
        }

        [Fact]
        public async Task A_folder_that_is_not_a_configured_root_is_never_watched()
        {
            string rootA = MakeRoot("a");
            string rootD = MakeRoot("d"); // exists on disk, laid out like a profile, but not configured
            using var h = new Harness(new[] { rootA }, rootA);
            h.Service.StartWatching();

            AddTeam(rootD, "stray-" + _id, "dee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());
            AddTeam(rootA, "team-" + _id, "ay-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());

            // The configured root's team went through the same delays and arrived; the stray one,
            // created first, did not.
            Assert.NotNull(await h.WaitForDiscovery(e => e.MemberName == "ay-" + _id));
            Assert.DoesNotContain(h.Discovered, e => e.MemberName == "dee-" + _id);
            Assert.All(h.Discovered, e => Assert.Equal(rootA, e.ConfigRoot));
        }

        // ── Item 35: the first team of a fresh profile ───────────────────────

        /// <summary>
        /// Claude Code creates teams\ and the first team's folder together. The teams folder is moved in
        /// whole here, so the only way this team can be found is the one-off look at a teams folder the
        /// service had seen missing: no Created event for the team folder exists to find it by.
        /// </summary>
        [Fact]
        public async Task The_team_already_inside_a_teams_folder_that_appears_is_discovered()
        {
            string rootA = MakeRoot("a");
            string rootB = Path.Combine(_base, "b") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(rootB, "projects")); // the profile folder exists; teams\ does not
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();

            string transcript = AddTeamsFolderWithTeam(rootB, "team-" + _id, "bee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());

            var found = await h.WaitForDiscovery(e => e.MemberName == "bee-" + _id);

            Assert.NotNull(found);
            Assert.Equal(rootB, found.ConfigRoot);
            Assert.True(((TranscriptTailer)found.Tailer).HasTranscriptFile(transcript));
        }

        /// <summary>The same one level up: the whole profile folder did not exist at the last refresh.</summary>
        [Fact]
        public async Task The_team_already_inside_a_profile_folder_that_appears_is_discovered_at_the_next_refresh()
        {
            string rootA = MakeRoot("a");
            string rootC = Path.Combine(_base, "c") + Path.DirectorySeparatorChar; // not created
            using var h = new Harness(new[] { rootA, rootC }, rootA);
            h.Service.StartWatching();

            Directory.CreateDirectory(Path.Combine(rootC, "projects"));
            AddTeamsFolderWithTeam(rootC, "team-" + _id, "cee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());
            h.Service.RefreshRoots();

            var found = await h.WaitForDiscovery(e => e.MemberName == "cee-" + _id);

            Assert.NotNull(found);
            Assert.Equal(rootC, found.ConfigRoot);
        }

        /// <summary>
        /// A teams folder that appears is not necessarily new: a drive that comes back, or a folder
        /// renamed away and back, brings the teams of earlier sessions with it. The teams folder is
        /// moved in whole with TWO teams inside, so neither gets a Created event and both are seen only
        /// by the one-off look. The one whose folder was created a day ago is not adopted; the one
        /// created now is, which is also the proof that the look ran.
        /// </summary>
        [Fact]
        public async Task An_old_team_inside_a_teams_folder_that_appears_is_not_adopted_and_a_new_one_is()
        {
            string rootA = MakeRoot("a");
            string rootB = Path.Combine(_base, "b") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(rootB, "projects")); // the profile folder exists; teams\ does not
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();

            string stagedTeams = Path.Combine(_base, "staging", Guid.NewGuid().ToString("N"), "teams");
            foreach (string kind in new[] { "old", "new" })
            {
                string session = Guid.NewGuid().ToString();
                WriteTranscript(rootB, @"H:\Lead\Dir", session);
                string teamDir = Path.Combine(stagedTeams, kind + "-" + _id);
                Directory.CreateDirectory(teamDir);
                File.WriteAllText(Path.Combine(teamDir, "config.json"), ConfigJson(kind + "-member-" + _id, @"H:\Lead\Dir", session));
            }
            string oldDir = Path.Combine(stagedTeams, "old-" + _id);
            Directory.SetCreationTimeUtc(oldDir, DateTime.UtcNow.AddDays(-1));
            Directory.Move(stagedTeams, Path.Combine(rootB, "teams"));

            // The instrument: the move kept the old folder's creation time (were it reset to now, the
            // "not adopted" assert below could only fail, never pass by accident).
            Assert.True(Directory.GetCreationTimeUtc(Path.Combine(rootB, "teams", "old-" + _id)) < DateTime.UtcNow.AddHours(-23));

            Assert.NotNull(await h.WaitForDiscovery(e => e.MemberName == "new-member-" + _id));
            await Task.Delay(1000); // both were listed in the same pass; this covers the old one's processing
            Assert.DoesNotContain(h.Discovered, e => e.MemberName == "old-member-" + _id);
        }

        [Fact]
        public void A_team_folder_counts_as_new_only_from_the_time_its_teams_folder_was_seen_missing()
        {
            var seenMissing = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(TeamWatcherService.IsTeamFolderFromThisAbsence(seenMissing.AddSeconds(5), seenMissing));
            Assert.True(TeamWatcherService.IsTeamFolderFromThisAbsence(seenMissing, seenMissing));
            // A file system's coarser clock: just before, within the tolerance.
            Assert.True(TeamWatcherService.IsTeamFolderFromThisAbsence(seenMissing.AddMilliseconds(-20), seenMissing));

            Assert.False(TeamWatcherService.IsTeamFolderFromThisAbsence(seenMissing - TeamWatcherService.TeamCreationTolerance - TimeSpan.FromSeconds(1), seenMissing));
            Assert.False(TeamWatcherService.IsTeamFolderFromThisAbsence(seenMissing.AddDays(-30), seenMissing));
        }

        /// <summary>
        /// "Nothing from a previous session is restored" still holds: a teams folder that was there
        /// when watching began is not looked into. The control is a team added afterwards to the same
        /// root, which went through the same watcher and arrived.
        /// </summary>
        [Fact]
        public async Task A_team_that_was_already_there_when_watching_began_is_not_restored()
        {
            string rootA = MakeRoot("a");
            AddTeam(rootA, "old-" + _id, "old-member-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());
            using var h = new Harness(new[] { rootA }, rootA);
            h.Service.StartWatching();

            AddTeam(rootA, "new-" + _id, "new-member-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());

            Assert.NotNull(await h.WaitForDiscovery(e => e.MemberName == "new-member-" + _id));
            await Task.Delay(1000); // longer than the one-off scan's delay, had one been scheduled
            Assert.DoesNotContain(h.Discovered, e => e.MemberName == "old-member-" + _id);
        }

        // ── Item 35: a root stopped under a delayed callback ─────────────────

        /// <summary>
        /// The root is stopped at exactly the point a delayed callback has passed its own "is this root
        /// still watched" check (the hook; waiting cannot arrange that). The callback then goes on to
        /// read the team's config. It must not leave a team behind under the stopped root: nothing
        /// would ever clean it up, and the same folder configured again would be handed that entry.
        /// </summary>
        [Fact]
        public async Task A_delayed_callback_cannot_leave_a_team_under_a_root_that_was_stopped_meanwhile()
        {
            string rootA = MakeRoot("a");
            string rootB = MakeRoot("b");
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();

            string team = "team-" + _id;
            string member = "bee-" + _id;
            int stops = 0;
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Service.ProcessTeamHook = (phase, name) =>
            {
                if (name != team) return;
                if (phase == "start")
                {
                    if (System.Threading.Interlocked.Increment(ref stops) == 1)
                    {
                        h.Roots = new[] { rootA };
                        h.Service.RefreshRoots();
                    }
                }
                else
                {
                    ended.TrySetResult(true);
                }
            };

            AddTeam(rootB, team, member, @"H:\Lead\Dir", Guid.NewGuid().ToString());

            await Task.WhenAny(ended.Task, Task.Delay(Wait));
            Assert.True(ended.Task.IsCompleted, "the team folder was never processed, so nothing was tested");
            Assert.Equal(1, stops);

            Assert.Equal(0, h.Service.ActiveTeamCount);
            Assert.DoesNotContain(h.Discovered, e => e.MemberName == member);
        }

        // ── Item 35: the refresh must survive a failing subscriber ───────────

        /// <summary>
        /// RefreshRoots runs on a timer thread, where an exception that escapes ends the process. A
        /// TeamRemoved subscriber that throws is the cheapest way to make stopping a root fail.
        /// </summary>
        [Fact]
        public async Task Dropping_a_root_does_not_throw_when_a_TeamRemoved_subscriber_does()
        {
            string rootA = MakeRoot("a");
            string rootB = MakeRoot("b");
            using var h = new Harness(new[] { rootA, rootB }, rootA);
            h.Service.StartWatching();
            AddTeam(rootB, "team-" + _id, "bee-" + _id, @"H:\Lead\Dir", Guid.NewGuid().ToString());
            Assert.NotNull(await h.WaitForDiscovery(e => e.MemberName == "bee-" + _id));

            int thrown = 0;
            h.Service.TeamRemoved += (s, e) =>
            {
                thrown++;
                throw new InvalidOperationException("subscriber failed");
            };

            h.Roots = new[] { rootA };
            var escaped = Record.Exception(() => h.Service.RefreshRoots());

            Assert.Null(escaped);
            Assert.Equal(1, thrown); // the subscriber did run and did throw: the refresh swallowed it
            Assert.Equal(0, h.Service.ActiveTeamCount);
        }

        // ── Item 35: subagent panels are per profile folder too ──────────────

        [Fact]
        public void Subagent_panel_key_is_the_bare_name_under_Default_and_carries_the_root_under_any_other()
        {
            string underDefault = TeamWatcherService.BuildSubagentPanelKey(DefaultRoot, "Alice", isDefaultRoot: true);
            string underWork = TeamWatcherService.BuildSubagentPanelKey(WorkRoot, "Alice", isDefaultRoot: false);

            Assert.Equal("subagent:Alice", underDefault);
            Assert.Equal("subagent:Alice|" + WorkRoot, underWork);

            // The key says which root it is under, and only that one.
            Assert.Equal("Alice", TeamWatcherService.SubagentNameIfUnderRoot(underDefault, DefaultRoot, isDefaultRoot: true));
            Assert.Null(TeamWatcherService.SubagentNameIfUnderRoot(underDefault, WorkRoot, isDefaultRoot: false));
            Assert.Equal("Alice", TeamWatcherService.SubagentNameIfUnderRoot(underWork, WorkRoot.ToUpperInvariant(), isDefaultRoot: false));
            Assert.Null(TeamWatcherService.SubagentNameIfUnderRoot(underWork, DefaultRoot, isDefaultRoot: true));
            Assert.Null(TeamWatcherService.SubagentNameIfUnderRoot(underWork, @"D:\cfg\", isDefaultRoot: false));
            Assert.Null(TeamWatcherService.SubagentNameIfUnderRoot("team:review:Alice", DefaultRoot, isDefaultRoot: true));
            Assert.Null(TeamWatcherService.SubagentNameIfUnderRoot(null, DefaultRoot, isDefaultRoot: true));
        }

        /// <summary>
        /// The discriminator: the SAME subagent names under two roots. A removed team may take only
        /// the panels under its own root, by name in either direction.
        /// </summary>
        [Fact]
        public void A_removed_team_selects_orphan_subagent_panels_under_its_own_root_only()
        {
            string defaultAlice = TeamWatcherService.BuildSubagentPanelKey(DefaultRoot, "Alice", true);
            string defaultAgentAlice = TeamWatcherService.BuildSubagentPanelKey(DefaultRoot, "Agent Alice", true);
            string workAlice = TeamWatcherService.BuildSubagentPanelKey(WorkRoot, "Alice", false);
            string workAgentAlice = TeamWatcherService.BuildSubagentPanelKey(WorkRoot, "Agent Alice", false);
            string workBob = TeamWatcherService.BuildSubagentPanelKey(WorkRoot, "Bob", false);
            var panels = new[] { defaultAlice, defaultAgentAlice, workAlice, workAgentAlice, workBob, "team:review:Alice", "team:review|" + WorkRoot + ":Alice" };

            Assert.Equal(
                new[] { workAlice, workAgentAlice },
                TeamWatcherService.SelectOrphanSubagentPanels(panels, new[] { "alice" }, WorkRoot, isDefaultRoot: false));
            Assert.Equal(
                new[] { defaultAlice, defaultAgentAlice },
                TeamWatcherService.SelectOrphanSubagentPanels(panels, new[] { "Alice" }, DefaultRoot, isDefaultRoot: true));

            // The member name is longer than the panel's: still a match, still only under its root.
            Assert.Equal(
                new[] { workAlice, workAgentAlice },
                TeamWatcherService.SelectOrphanSubagentPanels(panels, new[] { "Agent Alice" }, WorkRoot, isDefaultRoot: false));

            Assert.Empty(TeamWatcherService.SelectOrphanSubagentPanels(panels, new[] { "Alice" }, @"D:\cfg\", isDefaultRoot: false));
            Assert.Empty(TeamWatcherService.SelectOrphanSubagentPanels(panels, null, WorkRoot, isDefaultRoot: false));
            Assert.Empty(TeamWatcherService.SelectOrphanSubagentPanels(panels, new[] { "" }, WorkRoot, isDefaultRoot: false));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>A profile folder with empty teams and projects folders. Returned with a trailing separator.</summary>
        private string MakeRoot(string name)
        {
            string root = Path.Combine(_base, name) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(root, "teams"));
            Directory.CreateDirectory(Path.Combine(root, "projects"));
            return root;
        }

        /// <summary>
        /// Lays down what Claude Code writes for a team with one teammate: the member's transcript under
        /// the root's projects folder, then the team folder with a complete config.json. The team folder
        /// is prepared elsewhere and moved in, so the watcher never sees a half-written config.
        /// Returns the transcript path.
        /// </summary>
        private string AddTeam(string root, string teamName, string memberName, string leadCwd, string leadSessionId)
        {
            string transcript = WriteTranscript(root, leadCwd, leadSessionId);

            string staging = Path.Combine(_base, "staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "config.json"), ConfigJson(memberName, leadCwd, leadSessionId));
            Directory.Move(staging, Path.Combine(root, "teams", teamName));
            return transcript;
        }

        /// <summary>
        /// As <see cref="AddTeam"/>, for a root that has NO teams folder yet: the teams folder is moved
        /// in whole with the team already inside it, so no Created event for the team folder can ever
        /// reach a watcher on teams\ (there was no teams\ to watch when it was made).
        /// </summary>
        private string AddTeamsFolderWithTeam(string root, string teamName, string memberName, string leadCwd, string leadSessionId)
        {
            string transcript = WriteTranscript(root, leadCwd, leadSessionId);

            string stagedTeams = Path.Combine(_base, "staging", Guid.NewGuid().ToString("N"), "teams");
            Directory.CreateDirectory(Path.Combine(stagedTeams, teamName));
            File.WriteAllText(Path.Combine(stagedTeams, teamName, "config.json"), ConfigJson(memberName, leadCwd, leadSessionId));
            Directory.Move(stagedTeams, Path.Combine(root, "teams"));
            return transcript;
        }

        private static string WriteTranscript(string root, string leadCwd, string leadSessionId)
        {
            string subagents = Path.Combine(TeamWatcherService.GetLeadProjectDir(root, leadCwd), leadSessionId, "subagents");
            Directory.CreateDirectory(subagents);
            string transcript = Path.Combine(subagents, $"agent-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(transcript, "{}\n");
            return transcript;
        }

        private static string ConfigJson(string memberName, string leadCwd, string leadSessionId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return
                "{\"leadSessionId\":\"" + leadSessionId + "\",\"members\":[" +
                "{\"name\":\"team-lead\",\"agentType\":\"team-lead\",\"cwd\":\"" + leadCwd.Replace("\\", "\\\\") + "\",\"joinedAt\":" + now + "}," +
                "{\"name\":\"" + memberName + "\",\"agentType\":\"general-purpose\",\"joinedAt\":" + now + "}]}";
        }

        private sealed class Harness : IDisposable
        {
            private readonly string _defaultRoot;

            public Harness(IReadOnlyList<string> roots, string defaultRoot)
            {
                Roots = roots;
                _defaultRoot = defaultRoot;
                Service = new TeamWatcherService("test-slug", () => new TeamWatchRootSet(Roots, _defaultRoot), TimeSpan.Zero);
                Service.TeammateDiscovered += (s, e) => Discovered.Enqueue(e);
                Service.TeamRemoved += (s, e) => Removed.Enqueue(e);
            }

            public volatile IReadOnlyList<string> Roots;
            public TeamWatcherService Service { get; }
            public ConcurrentQueue<TeammateDiscoveredEventArgs> Discovered { get; } = new ConcurrentQueue<TeammateDiscoveredEventArgs>();
            public ConcurrentQueue<TeamRemovedEventArgs> Removed { get; } = new ConcurrentQueue<TeamRemovedEventArgs>();

            /// <summary>The first matching discovery, or null after <see cref="Wait"/> (never hangs).</summary>
            public async Task<TeammateDiscoveredEventArgs> WaitForDiscovery(Func<TeammateDiscoveredEventArgs, bool> match)
            {
                var deadline = DateTime.UtcNow + Wait;
                while (DateTime.UtcNow < deadline)
                {
                    var hit = Discovered.FirstOrDefault(match);
                    if (hit != null) return hit;
                    await Task.Delay(50);
                }
                return Discovered.FirstOrDefault(match);
            }

            public void Dispose() => Service.Dispose();
        }
    }
}
