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
            string subagents = Path.Combine(TeamWatcherService.GetLeadProjectDir(root, leadCwd), leadSessionId, "subagents");
            Directory.CreateDirectory(subagents);
            string transcript = Path.Combine(subagents, $"agent-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(transcript, "{}\n");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string config =
                "{\"leadSessionId\":\"" + leadSessionId + "\",\"members\":[" +
                "{\"name\":\"team-lead\",\"agentType\":\"team-lead\",\"cwd\":\"" + leadCwd.Replace("\\", "\\\\") + "\",\"joinedAt\":" + now + "}," +
                "{\"name\":\"" + memberName + "\",\"agentType\":\"general-purpose\",\"joinedAt\":" + now + "}]}";

            string staging = Path.Combine(_base, "staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "config.json"), config);
            Directory.Move(staging, Path.Combine(root, "teams", teamName));
            return transcript;
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
