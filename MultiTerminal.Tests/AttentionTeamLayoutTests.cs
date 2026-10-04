using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MultiTerminal.AttentionPanel;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The Attention rail's "By team" order (task d71bb221): which agent is a PM or a helper, which
    /// PM a helper belongs to, how teams are ordered, and that the link and the zoom survive a restart.
    /// </summary>
    /// <remarks>
    /// The ordering rules are the Owner-approved design, so each fact names the rule it pins. Every
    /// fact here was falsified once by breaking the rule it names and watching it go red; the ticket
    /// report lists which edit broke which fact.
    /// </remarks>
    public sealed class AttentionTeamLayoutTests : IDisposable
    {
        private readonly string _dir;

        public AttentionTeamLayoutTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_team_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // A leaked temp folder is not worth failing a test run over.
            }
        }

        // ───────────────────────────────────────────────────────────── helpers

        private static AttentionCard Card(string agent, string state = "Working", long since = 10,
            string role = null, string pm = null)
            => new AttentionCard { Id = "s-" + agent, Agent = agent, State = state, SinceSeconds = since, Role = role, Pm = pm };

        private static AttentionCard Pm(string agent, string state = "Working", long since = 10)
            => Card(agent, state, since, AttentionTeamRoles.Pm);

        private static AttentionCard Helper(string agent, string pm, string state = "Working", long since = 10)
            => Card(agent, state, since, AttentionTeamRoles.Helper, pm);

        /// <summary>The group order as head names (or "[kind:pm]" for a headless group).</summary>
        private static List<string> Heads(List<AttentionTeamGroup> groups) =>
            groups.Select(g => g.HeadId != null ? g.HeadId.Substring(2) : "[" + g.Kind + ":" + g.PmName + "]").ToList();

        private static List<string> Names(IEnumerable<string> ids) => ids.Select(id => id.Substring(2)).ToList();

        /// <summary>A fresh pane key in the stored form.</summary>
        private static string Key() => Guid.NewGuid().ToString("N");

        /// <summary>A restored pane as the rail sees it once the Owner relaunches it as the same agent.</summary>
        private static AttentionPaneTeamFacts Restored(TerminalSessionInfo r) => new AttentionPaneTeamFacts
        {
            PaneKey = r.PaneKey,
            AgentName = r.CustomTitle,
            DisplayedAsPm = true,   // relaunched from the project start screen
            LinkHelperName = r.HelperName,
            LinkSpawnerPaneKey = r.SpawnerPaneKey,
            LinkSpawnerName = r.SpawnerName,
        };

        private string WriteRawSessions(string value)
        {
            string path = Path.Combine(_dir, "settings.txt");
            File.WriteAllText(path, "SessionTerminals=" + value + "\n");
            return path;
        }

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadRepoFile(params string[] parts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(parts)}' at '{path}'.");
            return File.ReadAllText(path);
        }

        /// <summary>Block and line comments out, so prose naming a symbol cannot satisfy a check.</summary>
        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", noBlocks.Split('\n').Select(l => Regex.Replace(l, @"(^|\s)//.*$", string.Empty)));
        }

        /// <summary>The body of the first method/function declared by <paramref name="signature"/>.</summary>
        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{signature}' not found — the anchor is broken, not the code.");
            int open = src.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        // ───────────────────────────────────────────────────────────── grouping

        /// <summary>Helpers sit under the PM whose pane spawned them; everyone else is a group of one.</summary>
        [Fact]
        public void Helpers_are_grouped_under_their_pm()
        {
            var cards = new List<AttentionCard>
            {
                Helper("Tess", "Owen", since: 30),
                Pm("Owen", since: 200),
                Pm("Diana", state: "Idle", since: 100),
                Helper("Piper", "Owen", since: 90),
                Card("Zed", since: 50),
            };

            var groups = AttentionTeamLayout.Build(cards);

            var owen = groups.Single(g => g.HeadId == "s-Owen");
            Assert.Equal(AttentionTeamLayout.KindTeam, owen.Kind);
            Assert.Equal(new[] { "Piper", "Tess" }, Names(owen.HelperIds));   // longest in state first

            Assert.Equal(AttentionTeamLayout.KindSolo, groups.Single(g => g.HeadId == "s-Diana").Kind);
            Assert.Equal(AttentionTeamLayout.KindSolo, groups.Single(g => g.HeadId == "s-Zed").Kind);

            // Every card exactly once.
            var placed = groups.SelectMany(g => (g.HeadId == null ? new string[0] : new[] { g.HeadId }).Concat(g.HelperIds)).ToList();
            Assert.Equal(cards.Select(c => c.Id).OrderBy(x => x), placed.OrderBy(x => x));
        }

        /// <summary>
        /// One blocked member lifts the WHOLE team; among blocked teams the longest wait goes first,
        /// the same rule Attention first applies to single cards.
        /// </summary>
        [Fact]
        public void A_blocked_team_floats_to_the_top_longest_wait_first()
        {
            var cards = new List<AttentionCard>
            {
                Pm("Diana", state: "Idle", since: 5000),                       // calm, very old
                Pm("Owen", since: 10),
                Helper("Tess", "Owen", state: "BlockedPermission", since: 100),
                Pm("Charlie", state: "Idle", since: 20),
                Helper("Uma", "Charlie", state: "BlockedQuestion", since: 300),
                Card("Zed", state: "BlockedUnknown", since: 200),               // a blocked agent with no role
            };

            var groups = AttentionTeamLayout.Build(cards);

            Assert.Equal(new[] { "Charlie", "Zed", "Owen", "Diana" }, Heads(groups));
        }

        /// <summary>A blocked helper sorts first among the helpers, and its PM's card names it.</summary>
        [Fact]
        public void A_blocked_helper_sorts_first_and_its_pm_card_names_it()
        {
            var cards = new List<AttentionCard>
            {
                Pm("Charlie", state: "Idle", since: 16),
                Helper("Vic", "Charlie", since: 500),
                Helper("Wes", "Charlie", state: "BlockedPermission", since: 30),
                Helper("Uma", "Charlie", state: "BlockedQuestion", since: 120),
            };

            var team = AttentionTeamLayout.Build(cards).Single();

            Assert.Equal(new[] { "Uma", "Wes", "Vic" }, Names(team.HelperIds));
            Assert.NotNull(team.NeedsYou);
            Assert.Equal("Uma", team.NeedsYou.Agent);
            Assert.Equal("s-Uma", team.NeedsYou.Id);
            Assert.Equal(120, team.NeedsYou.SinceSeconds);
            Assert.Equal(1, team.NeedsYou.More);
        }

        /// <summary>A calm team carries no badge.</summary>
        [Fact]
        public void A_calm_team_has_no_needs_you_badge()
        {
            var team = AttentionTeamLayout.Build(new List<AttentionCard> { Pm("Owen"), Helper("Tess", "Owen") }).Single();
            Assert.Null(team.NeedsYou);
        }

        /// <summary>
        /// A helper whose PM is unknown goes in the fallback group at the very bottom — below even an
        /// offline agent — and is never filed under a guessed PM. One whose PM is known by name but
        /// has no card gets its own "Helpers of X" group, just above.
        /// </summary>
        [Fact]
        public void Unknown_pm_goes_to_the_fallback_group_at_the_bottom()
        {
            var cards = new List<AttentionCard>
            {
                Helper("Orphan", pm: null, since: 900),
                Pm("Owen", since: 10),
                Helper("Kid", pm: "Ghost", since: 800),
                Card("Gone", state: "Offline", since: 50),
            };

            var groups = AttentionTeamLayout.Build(cards);

            Assert.Equal(new[] { "Owen", "Gone", "[headless:Ghost]", "[unknown:]" }, Heads(groups));
            Assert.Equal(new[] { "Orphan" }, Names(groups.Last().HelperIds));
            Assert.Equal("Ghost", groups[2].PmName);

            // Owen is the only PM on the rail; the orphan must not have been handed to him.
            Assert.Empty(groups.Single(g => g.HeadId == "s-Owen").HelperIds);
        }

        /// <summary>Blocked-first holds in every order: a blocked helper with no PM still goes to the top.</summary>
        [Fact]
        public void A_blocked_helper_with_unknown_pm_still_goes_to_the_top()
        {
            var cards = new List<AttentionCard>
            {
                Pm("Owen", since: 10),
                Helper("Orphan", pm: null, state: "BlockedPermission", since: 5),
            };

            Assert.Equal(new[] { "[unknown:]", "Owen" }, Heads(AttentionTeamLayout.Build(cards)));
        }

        /// <summary>A helper of a helper is filed under the first ancestor that is not a helper.</summary>
        [Fact]
        public void A_helpers_helper_joins_the_root_team()
        {
            var cards = new List<AttentionCard>
            {
                Pm("Owen"),
                Helper("Tess", "Owen", since: 40),
                Helper("Tiny", "Tess", since: 20),
            };

            var team = AttentionTeamLayout.Build(cards).Single();
            Assert.Equal("s-Owen", team.HeadId);
            Assert.Equal(new[] { "Tess", "Tiny" }, Names(team.HelperIds));
        }

        /// <summary>A link that loops proves nothing and ends in the fallback group, not in a hang.</summary>
        [Fact]
        public void A_looping_link_falls_back_to_unknown()
        {
            var cards = new List<AttentionCard> { Helper("A", "B"), Helper("B", "A") };

            var groups = AttentionTeamLayout.Build(cards);
            Assert.Equal(AttentionTeamLayout.KindUnknown, groups.Single().Kind);
        }

        // ───────────────────────────────────────────────────────────── unchanged behaviour

        /// <summary>
        /// Agents with no role are a group of one each, in exactly the order Attention first gives
        /// single cards: blocked first (longest wait first), then longest in state, offline last.
        /// </summary>
        [Fact]
        public void No_role_cards_are_ordered_exactly_as_attention_first()
        {
            var cards = new List<AttentionCard>
            {
                Card("A", since: 10),
                Card("B", state: "Offline", since: 900),
                Card("C", state: "BlockedQuestion", since: 5),
                Card("D", state: "Idle", since: 70),
                Card("E", state: "BlockedPermission", since: 50),
                Card("F", since: 70),
            };

            var groups = AttentionTeamLayout.Build(cards);

            Assert.All(groups, g => Assert.Equal(AttentionTeamLayout.KindSolo, g.Kind));
            Assert.All(groups, g => Assert.Empty(g.HelperIds));
            Assert.Equal(new[] { "E", "C", "D", "F", "A", "B" }, Heads(groups));
        }

        /// <summary>
        /// A card for an agent with no team link is byte-identical to the card this projector built
        /// before roles existed, and the projector never reorders: the existing orders are applied by
        /// the view to this same list.
        /// </summary>
        [Fact]
        public void No_role_cards_are_unchanged_and_the_card_list_is_not_reordered()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var entries = new[]
            {
                new AgentAttentionEntry { SessionId = "1", AgentName = "Zed", State = AttentionState.Idle, EnteredAtUtc = now.AddSeconds(-40) },
                new AgentAttentionEntry { SessionId = "2", AgentName = "Owen", State = AttentionState.Working, EnteredAtUtc = now.AddSeconds(-5) },
                new AgentAttentionEntry { SessionId = "3", AgentName = "Tess", State = AttentionState.BlockedPermission, EnteredAtUtc = now.AddSeconds(-9) },
            };
            var colors = new Dictionary<string, string>();
            var claims = new Dictionary<string, AttentionTicketClaim>();
            var links = new Dictionary<string, AttentionTeamLink>(StringComparer.OrdinalIgnoreCase)
            {
                ["Owen"] = new AttentionTeamLink { Role = AttentionTeamRoles.Pm },
                ["Tess"] = new AttentionTeamLink { Role = AttentionTeamRoles.Helper, Pm = "Owen" },
            };

            var before = AttentionCardProjector.Project(entries, colors, claims, now);
            var after = AttentionCardProjector.Project(entries, colors, claims, now, teamLinks: links);

            Assert.Equal(new[] { "1", "2", "3" }, after.Select(c => c.Id));   // input order, untouched

            // Zed has no link: the whole serialised card is the same either way.
            Assert.Equal(JsonSerializer.Serialize(before[0]), JsonSerializer.Serialize(after[0]));
            Assert.Null(after[0].Role);
            Assert.Null(after[0].Pm);

            Assert.Equal(AttentionTeamRoles.Pm, after[1].Role);
            Assert.Null(after[1].Pm);   // a PM has no PM
            Assert.Equal(AttentionTeamRoles.Helper, after[2].Role);
            Assert.Equal("Owen", after[2].Pm);
        }

        /// <summary>A malformed link cannot invent a role: only "pm" and "helper" pass through.</summary>
        [Fact]
        public void An_unknown_role_string_is_dropped()
        {
            var now = DateTime.UtcNow;
            var entries = new[] { new AgentAttentionEntry { SessionId = "1", AgentName = "Zed", EnteredAtUtc = now } };
            var links = new Dictionary<string, AttentionTeamLink> { ["Zed"] = new AttentionTeamLink { Role = "boss", Pm = "Owen" } };

            var card = AttentionCardProjector.Project(entries, null, null, now, teamLinks: links).Single();
            Assert.Null(card.Role);
            Assert.Null(card.Pm);
        }

        /// <summary>
        /// The three existing orders are still offered and still drawn by the view's own sort, which
        /// does not consult the team grouping at all.
        /// </summary>
        [Fact]
        public void The_existing_orders_are_still_offered_and_untouched_by_the_grouping()
        {
            Assert.Equal(new[] { "attention", "fixed", "project", "team" }, AttentionPanelControl.Orders);

            string script = StripComments(ReadRepoFile("AttentionPanel", "attention-panel.html"));
            string ordered = Body(script, "function ordered()");
            Assert.True(ordered.Length > 400, "ordered() extraction came back too short to mean anything.");
            Assert.DoesNotContain("teams", ordered, StringComparison.Ordinal);
            Assert.DoesNotContain(".role", ordered, StringComparison.Ordinal);
            Assert.DoesNotContain(".pm", ordered, StringComparison.Ordinal);

            foreach (var order in AttentionPanelControl.Orders)
            {
                Assert.Matches("\\[\"" + order + "\",", script);
            }
        }

        // ───────────────────────────────────────────────────────────── who is whose helper

        /// <summary>
        /// The PM is found through the spawner PANE's key, never through a name. The discriminator:
        /// two PMs on the rail, and the helper's pane recorded under one of their keys.
        /// </summary>
        [Fact]
        public void A_helper_is_filed_under_the_pane_that_spawned_it()
        {
            var panes = new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-alice", AgentName = "Alice", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-owen", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "Tess", LaunchedAsHelper = true, LinkHelperName = "Tess", LinkSpawnerPaneKey = "k-owen", LinkSpawnerName = "Owen" },
                new AttentionPaneTeamFacts { PaneKey = "k-zed", AgentName = "Zed" },
            };

            var links = AttentionTeamResolver.Resolve(panes);

            Assert.Equal(AttentionTeamRoles.Pm, links["Alice"].Role);
            Assert.Equal(AttentionTeamRoles.Pm, links["Owen"].Role);
            Assert.Equal(AttentionTeamRoles.Helper, links["Tess"].Role);
            Assert.Equal("Owen", links["Tess"].Pm);
            Assert.False(links.ContainsKey("Zed"));   // no role: no entry, card renders as today
        }

        /// <summary>A helper launched with no proven spawner is a helper whose PM is unknown.</summary>
        [Fact]
        public void An_unproven_helper_has_no_pm()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-owen", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "Tess", LaunchedAsHelper = true },
            });

            Assert.Equal(AttentionTeamRoles.Helper, links["Tess"].Role);
            Assert.Null(links["Tess"].Pm);
        }

        /// <summary>
        /// A link holds only while the pane runs under the name it was recorded for: a restored pane
        /// relaunched as the same agent keeps it, the same pane reused for someone else does not.
        /// </summary>
        [Fact]
        public void A_link_holds_only_under_the_name_it_was_recorded_for()
        {
            var sameName = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-owen", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "tess", DisplayedAsPm = true, LinkHelperName = "Tess", LinkSpawnerPaneKey = "k-owen", LinkSpawnerName = "Owen" },
            });
            Assert.Equal(AttentionTeamRoles.Helper, sameName["tess"].Role);
            Assert.Equal("Owen", sameName["tess"].Pm);

            var reused = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-owen", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "Bob", DisplayedAsPm = true, LinkHelperName = "Tess", LinkSpawnerPaneKey = "k-owen", LinkSpawnerName = "Owen" },
            });
            Assert.Equal(AttentionTeamRoles.Pm, reused["Bob"].Role);
        }

        /// <summary>A spawner pane that is no longer open leaves its helper with no PM, never a guess.</summary>
        [Fact]
        public void A_closed_spawner_pane_leaves_the_pm_unknown()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-alice", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "Tess", LinkHelperName = "Tess", LinkSpawnerPaneKey = "k-owen-gone" },
            });

            Assert.Equal(AttentionTeamRoles.Helper, links["Tess"].Role);
            Assert.Null(links["Tess"].Pm);
        }

        // ───────────────────────────────────────────────────────────── surviving a restart

        /// <summary>
        /// The pane key and helper link are saved with the session and read back by a NEW settings
        /// instance (a restart), and the read-back link still resolves the helper's PM.
        /// </summary>
        [Fact]
        public void The_helper_link_survives_a_reload()
        {
            string kOwen = Key(), kTess = Key(), kUma = Key();
            new SettingsService(_dir).SetSessionTerminals(new List<TerminalSessionInfo>
            {
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = "Owen", PaneKey = kOwen },
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = "Tess", PaneKey = kTess, HelperName = "Tess", SpawnerPaneKey = kOwen, SpawnerName = "Owen" },
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = "Uma", PaneKey = kUma, HelperName = "Uma" },
            });

            var restored = new SettingsService(_dir).GetSessionTerminals();

            Assert.Equal(new[] { kOwen, kTess, kUma }, restored.Select(r => r.PaneKey));
            Assert.Null(restored[0].HelperName);
            Assert.Equal("Tess", restored[1].HelperName);
            Assert.Equal(kOwen, restored[1].SpawnerPaneKey);
            Assert.Equal("Owen", restored[1].SpawnerName);
            Assert.Equal("Uma", restored[2].HelperName);
            Assert.Null(restored[2].SpawnerPaneKey);

            var links = AttentionTeamResolver.Resolve(restored.Select(Restored));

            Assert.Equal(AttentionTeamRoles.Pm, links["Owen"].Role);
            Assert.Equal("Owen", links["Tess"].Pm);
            Assert.Equal(AttentionTeamRoles.Helper, links["Uma"].Role);
            Assert.Null(links["Uma"].Pm);
        }

        /// <summary>A session saved before this ticket (three fields) still restores, with no link.</summary>
        [Fact]
        public void An_old_three_field_session_still_restores()
        {
            File.WriteAllText(Path.Combine(_dir, "settings.txt"), "SessionTerminals=C:\\p|10.0|Owen;C:\\q|11.0|Tess\n");
            var restored = new SettingsService(_dir).GetSessionTerminals();

            Assert.NotNull(restored);
            Assert.Equal(new[] { "Owen", "Tess" }, restored.Select(r => r.CustomTitle));
            Assert.All(restored, r => Assert.Null(r.PaneKey));
            Assert.All(restored, r => Assert.Null(r.HelperName));
        }

        // ───────────────────────────────────────────────────────────── the stored format (pipeline Run 1)

        /// <summary>
        /// Every field round-trips ANY string exactly — separators, quotes, a backslash, a newline,
        /// non-ASCII — and a title, directory or name can never become a pane key, helper or spawner.
        /// </summary>
        [Fact]
        public void Values_holding_separators_and_quotes_round_trip_exactly_and_set_no_link()
        {
            string kOwen = Key(), kTess = Key(), kOdd = Key();
            const string oddTitle = "O|d;d \"quoted\" 'x' \\ back\nline Å";
            const string oddDir = @"C:\a;b|c ""d""";
            const string oddHelper = "Te|s;s\"";
            const string oddSpawner = "Ow|e;n\"";

            new SettingsService(_dir).SetSessionTerminals(new List<TerminalSessionInfo>
            {
                new TerminalSessionInfo { WorkingDirectory = oddDir, FontSize = 12.5f, CustomTitle = oddTitle, PaneKey = kOdd },
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = oddSpawner, PaneKey = kOwen },
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = oddHelper, PaneKey = kTess, HelperName = oddHelper, SpawnerPaneKey = kOwen, SpawnerName = oddSpawner },
            });

            // The raw stored line is a single line: a newline in a title must not split settings.txt.
            string raw = File.ReadAllLines(Path.Combine(_dir, "settings.txt")).Single(l => l.StartsWith("SessionTerminals=", StringComparison.Ordinal));
            Assert.StartsWith("SessionTerminals={", raw, StringComparison.Ordinal);

            var restored = new SettingsService(_dir).GetSessionTerminals();

            Assert.Equal(3, restored.Count);
            Assert.Equal(oddTitle, restored[0].CustomTitle);
            Assert.Equal(oddDir, restored[0].WorkingDirectory);
            Assert.Equal(12.5f, restored[0].FontSize);
            Assert.Equal(kOdd, restored[0].PaneKey);
            Assert.Null(restored[0].HelperName);
            Assert.Null(restored[0].SpawnerPaneKey);
            Assert.Null(restored[0].SpawnerName);

            // A link whose names hold separators is KEPT (Run 0 silently dropped it), exactly.
            Assert.Equal(oddHelper, restored[2].HelperName);
            Assert.Equal(kOwen, restored[2].SpawnerPaneKey);
            Assert.Equal(oddSpawner, restored[2].SpawnerName);

            var links = AttentionTeamResolver.Resolve(restored.Select(Restored));
            Assert.Equal(oddSpawner, links[oddHelper].Pm);
            Assert.False(links[oddHelper].PmRecordedOnly);
        }

        /// <summary>
        /// Codex's exploit: an agent named <c>Evil|&lt;key&gt;|Evil|&lt;targetKey&gt;</c>. Under the old
        /// format it restored as title "Evil" with a forged helper link to the target PM. Now it
        /// restores under its full name with no link, in the new format AND from both legacy writers.
        /// </summary>
        [Fact]
        public void The_crafted_name_cannot_forge_a_link()
        {
            string kOwen = Key(), kEvil = Key(), kForged = Key();
            string crafted = $"Evil|{kForged}|Evil|{kOwen}";

            // New format.
            new SettingsService(_dir).SetSessionTerminals(new List<TerminalSessionInfo>
            {
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = "Owen", PaneKey = kOwen },
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = crafted, PaneKey = kEvil },
            });
            AssertNoForgedLink(new SettingsService(_dir).GetSessionTerminals(), crafted, kEvil);

            // Written by the Run 0 build (a key for every pane, fields raw): no legacy column is
            // trusted, so that build's own trailing columns stay part of the title text.
            WriteRawSessions($@"C:\p|10.0|Owen|{kOwen}||;C:\p|10.0|{crafted}|{kEvil}||");
            AssertNoForgedLink(new SettingsService(_dir).GetSessionTerminals(), $"{crafted}|{kEvil}||", expectedKey: null);

            // Written by a pre-ticket build (three fields, title raw): the crafted entry is exactly
            // six well-formed parts. No legacy column is trusted, so it is a title.
            WriteRawSessions($@"C:\p|10.0|Owen;C:\p|10.0|{crafted}");
            AssertNoForgedLink(new SettingsService(_dir).GetSessionTerminals(), crafted, expectedKey: null);

            // Pipeline Run 2 (Codex security + adversary): the same pre-ticket session holding ONLY
            // the crafted pane, and one where every pane is crafted. Each looks like a session in
            // which every entry is six parts with GUID columns, which the Run 1 rule trusted.
            string crafted2 = $"Mal|{Key()}|Mal|{kOwen}";
            foreach (string raw in new[] { $@"C:\p|10.0|{crafted}", $@"C:\p|10.0|{crafted};C:\q|10.0|{crafted2}" })
            {
                WriteRawSessions(raw);
                var restored = new SettingsService(_dir).GetSessionTerminals();
                Assert.Equal(crafted, restored[0].CustomTitle);
                Assert.All(restored, r =>
                {
                    Assert.Null(r.PaneKey);
                    Assert.Null(r.HelperName);
                    Assert.Null(r.SpawnerPaneKey);
                    Assert.Null(r.SpawnerName);
                });
            }
        }

        private static void AssertNoForgedLink(List<TerminalSessionInfo> restored, string crafted, string expectedKey)
        {
            Assert.Equal(2, restored.Count);
            var evil = restored[1];
            Assert.Equal(crafted, evil.CustomTitle);
            Assert.Equal(expectedKey, evil.PaneKey);
            Assert.Null(evil.HelperName);
            Assert.Null(evil.SpawnerPaneKey);
            Assert.Null(evil.SpawnerName);

            // Relaunched as "Evil" (what the shifted columns would have named it): still no team.
            var links = AttentionTeamResolver.Resolve(new[]
            {
                Restored(restored[0]),
                new AttentionPaneTeamFacts
                {
                    PaneKey = evil.PaneKey, AgentName = "Evil",
                    LinkHelperName = evil.HelperName, LinkSpawnerPaneKey = evil.SpawnerPaneKey, LinkSpawnerName = evil.SpawnerName,
                },
            });
            Assert.False(links.ContainsKey("Evil"));
        }

        /// <summary>
        /// A six-field legacy line (written only by the unshipped Run 0 build) restores its pane
        /// with directory and size but NO key or link (pipeline Run 2): its columns cannot be told
        /// from a pre-ticket title that held separators, so they are kept as title text.
        /// </summary>
        [Fact]
        public void A_legacy_six_field_line_restores_with_no_key_or_link()
        {
            string kOwen = Key(), kTess = Key();
            WriteRawSessions($@"C:\p|10.0|Owen|{kOwen}||;C:\q|11.0|Tess|{kTess}|Tess|{kOwen}");

            var restored = new SettingsService(_dir).GetSessionTerminals();

            Assert.Equal(new[] { @"C:\p", @"C:\q" }, restored.Select(r => r.WorkingDirectory));
            Assert.Equal(new[] { 10f, 11f }, restored.Select(r => r.FontSize));
            Assert.Equal($"Tess|{kTess}|Tess|{kOwen}", restored[1].CustomTitle);
            Assert.All(restored, r =>
            {
                Assert.Null(r.PaneKey);
                Assert.Null(r.HelperName);
                Assert.Null(r.SpawnerPaneKey);
            });
        }

        /// <summary>
        /// A duplicated or malformed pane key is replaced by a fresh, unique GUID on every pane that
        /// held it, and a link pointing at it loses its spawner key; the recorded name remains.
        /// </summary>
        [Fact]
        public void Duplicate_and_malformed_keys_are_regenerated_and_their_links_cleared()
        {
            string kDup = Key(), kGood = Key(), kHelperA = Key(), kHelperB = Key();
            string upperGood = kGood.ToUpperInvariant();   // well-formed, canonicalised on read
            string json = "{\"v\":2,\"terminals\":["
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Alice\",\"paneKey\":\"{kDup}\"}},"
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Bob\",\"paneKey\":\"{kDup}\"}},"
                + "{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Carl\",\"paneKey\":\"not-a-guid\"},"
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Dina\",\"paneKey\":\"{upperGood}\"}},"
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Uma\",\"paneKey\":\"{kHelperA}\",\"helper\":\"Uma\",\"spawnerPaneKey\":\"{kDup}\",\"spawnerName\":\"Alice\"}},"
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Vic\",\"paneKey\":\"{kHelperB}\",\"helper\":\"Vic\",\"spawnerPaneKey\":\"not-a-guid\",\"spawnerName\":\"Carl\"}},"
                + $"{{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Wes\",\"paneKey\":\"{kHelperB.ToUpperInvariant()}\",\"helper\":\"Wes\",\"spawnerPaneKey\":\"{upperGood}\",\"spawnerName\":\"Dina\"}}"
                + "]}";
            WriteRawSessions(json);

            var r = new SettingsService(_dir).GetSessionTerminals();

            Assert.Equal(7, r.Count);
            Assert.All(r, t => Assert.Matches("^[0-9a-f]{32}$", t.PaneKey));
            Assert.Equal(r.Count, r.Select(t => t.PaneKey).Distinct(StringComparer.Ordinal).Count());

            Assert.NotEqual(kDup, r[0].PaneKey);   // both holders of the duplicate re-keyed
            Assert.NotEqual(kDup, r[1].PaneKey);
            Assert.Equal(kGood, r[3].PaneKey);     // a well-formed unique key survives, canonicalised

            // Vic and Wes held the same key once canonicalised: both re-keyed.
            Assert.NotEqual(kHelperB, r[5].PaneKey);
            Assert.NotEqual(kHelperB, r[6].PaneKey);

            Assert.Null(r[4].SpawnerPaneKey);      // pointed at the ambiguous key
            Assert.Equal("Alice", r[4].SpawnerName);
            Assert.Null(r[5].SpawnerPaneKey);      // malformed
            Assert.Equal(kGood, r[6].SpawnerPaneKey);   // unambiguous, canonicalised

            // Uma's link no longer names a pane: "Helpers of Alice", never Alice's or Bob's card.
            var links = AttentionTeamResolver.Resolve(r.Select(Restored));
            Assert.Equal("Alice", links["Uma"].Pm);
            Assert.True(links["Uma"].PmRecordedOnly);
            Assert.Equal("Dina", links["Wes"].Pm);
            Assert.False(links["Wes"].PmRecordedOnly);
        }

        /// <summary>
        /// Two live panes holding the same key (the restore check is bypassed, as by a hand-built
        /// list): resolution refuses both rather than taking the first.
        /// </summary>
        [Fact]
        public void An_ambiguous_spawner_key_resolves_to_no_pane()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-dup", AgentName = "Alice", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-dup", AgentName = "Alice2", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-uma", AgentName = "Uma", LinkHelperName = "Uma", LinkSpawnerPaneKey = "k-dup", LinkSpawnerName = "Alice" },
            });

            Assert.Equal("Alice", links["Uma"].Pm);
            Assert.True(links["Uma"].PmRecordedOnly);
        }

        // ───────────────────────────────────────────────────────────── the spawner's name (pipeline Run 1)

        /// <summary>
        /// Alice spawned Uma; Alice's pane now runs Bob, and Alice runs in another pane. Uma does NOT
        /// move under Bob, and is not attached to Alice's new card either: "Helpers of Alice".
        /// </summary>
        [Fact]
        public void A_reused_spawner_pane_does_not_take_over_the_helper()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-1", AgentName = "Bob", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-2", AgentName = "Alice", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-uma", AgentName = "Uma", LaunchedAsHelper = true, LinkHelperName = "Uma", LinkSpawnerPaneKey = "k-1", LinkSpawnerName = "Alice" },
            });

            Assert.Equal(AttentionTeamRoles.Pm, links["Bob"].Role);
            Assert.Equal("Alice", links["Uma"].Pm);
            Assert.True(links["Uma"].PmRecordedOnly);

            var now = DateTime.UtcNow;
            var entries = new[] { "Bob", "Alice", "Uma" }
                .Select((n, i) => new AgentAttentionEntry { SessionId = n, AgentName = n, State = AttentionState.Working, EnteredAtUtc = now.AddSeconds(-10 - i) })
                .ToArray();
            var cards = AttentionCardProjector.Project(entries, null, null, now, teamLinks: links);
            var groups = AttentionTeamLayout.Build(cards);

            Assert.Empty(groups.Single(g => g.HeadId == "Bob").HelperIds);
            Assert.Empty(groups.Single(g => g.HeadId == "Alice").HelperIds);
            var headless = groups.Single(g => g.Kind == AttentionTeamLayout.KindHeadless);
            Assert.Equal("Alice", headless.PmName);
            Assert.Equal(new[] { "Uma" }, headless.HelperIds);
        }

        /// <summary>The spawner pane still running the recorded agent IS the PM (the positive control).</summary>
        [Fact]
        public void A_spawner_pane_still_running_the_recorded_agent_is_the_pm()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-1", AgentName = "alice", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-uma", AgentName = "Uma", LaunchedAsHelper = true, LinkHelperName = "Uma", LinkSpawnerPaneKey = "k-1", LinkSpawnerName = "Alice" },
            });

            Assert.Equal("alice", links["Uma"].Pm);
            Assert.False(links["Uma"].PmRecordedOnly);
        }

        /// <summary>A link with a spawner key but no recorded name cannot be checked, so the PM is unknown.</summary>
        [Fact]
        public void A_link_without_a_recorded_spawner_name_is_pm_unknown()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-1", AgentName = "Bob", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-uma", AgentName = "Uma", LinkHelperName = "Uma", LinkSpawnerPaneKey = "k-1" },
            });

            Assert.Equal(AttentionTeamRoles.Helper, links["Uma"].Role);
            Assert.Null(links["Uma"].Pm);
        }

        /// <summary>
        /// The registry hands back the name the PROVEN spawner pane had at spawn time, which is what
        /// MainForm stamps, and never the nonce.
        /// </summary>
        [Fact]
        public void The_registry_returns_the_spawners_name_at_spawn_time()
        {
            var reg = new SpawnedPaneRegistry();
            Assert.True(reg.Record("helper-doc", "Uma", "spawner-doc", "nonce-123", "Alice"));
            Assert.Equal(("spawner-doc", "Alice"), reg.SpawnerFor("helper-doc"));
            Assert.Equal(((string)null, (string)null), reg.SpawnerFor("nobody"));

            Assert.True(reg.Record("helper-2", "Vic", "spawner-doc", "nonce-123"));
            Assert.Equal(("spawner-doc", (string)null), reg.SpawnerFor("helper-2"));
        }

        // ───────────────────────────────────────────────────────────── helper of a helper (pipeline Run 1)

        /// <summary>
        /// Owen spawned Tess, Tess spawned Tiny, and Tess has no card on the rail. Tiny still resolves
        /// to Owen by following the PANES, not "Helpers of Tess".
        /// </summary>
        [Fact]
        public void A_helpers_helper_resolves_to_the_root_pm_through_the_panes()
        {
            var links = AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-owen", AgentName = "Owen", DisplayedAsPm = true },
                new AttentionPaneTeamFacts { PaneKey = "k-tess", AgentName = "Tess", LaunchedAsHelper = true, LinkHelperName = "Tess", LinkSpawnerPaneKey = "k-owen", LinkSpawnerName = "Owen" },
                new AttentionPaneTeamFacts { PaneKey = "k-tiny", AgentName = "Tiny", LaunchedAsHelper = true, LinkHelperName = "Tiny", LinkSpawnerPaneKey = "k-tess", LinkSpawnerName = "Tess" },
            });

            Assert.Equal("Owen", links["Tess"].Pm);
            Assert.Equal("Owen", links["Tiny"].Pm);
            Assert.False(links["Tiny"].PmRecordedOnly);

            // Tess has no card (no attention entry): Tiny still joins Owen's team.
            var now = DateTime.UtcNow;
            var entries = new[]
            {
                new AgentAttentionEntry { SessionId = "Owen", AgentName = "Owen", State = AttentionState.Working, EnteredAtUtc = now },
                new AgentAttentionEntry { SessionId = "Tiny", AgentName = "Tiny", State = AttentionState.Working, EnteredAtUtc = now },
            };
            var team = AttentionTeamLayout.Build(AttentionCardProjector.Project(entries, null, null, now, teamLinks: links)).Single();
            Assert.Equal("Owen", team.HeadId);
            Assert.Equal(new[] { "Tiny" }, team.HelperIds);
        }

        /// <summary>
        /// A chain of panes that loops proves nothing: PM unknown. Run with a deadline, so a broken
        /// loop guard FAILS this fact instead of hanging the suite.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task A_looping_pane_chain_is_pm_unknown()
        {
            var resolving = System.Threading.Tasks.Task.Run(() => AttentionTeamResolver.Resolve(new[]
            {
                new AttentionPaneTeamFacts { PaneKey = "k-a", AgentName = "A", LinkHelperName = "A", LinkSpawnerPaneKey = "k-b", LinkSpawnerName = "B" },
                new AttentionPaneTeamFacts { PaneKey = "k-b", AgentName = "B", LinkHelperName = "B", LinkSpawnerPaneKey = "k-a", LinkSpawnerName = "A" },
            }));

            var winner = await System.Threading.Tasks.Task.WhenAny(resolving, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(winner == resolving, "Resolve did not return: the loop guard is broken.");
            var links = await resolving;
            Assert.Null(links["A"].Pm);
            Assert.Null(links["B"].Pm);
        }

        /// <summary>
        /// IsBlocked reads the one blocked-state list (AttentionStates.IsBlocking) through the card's
        /// state NAME, which must round-trip exactly: a numeric string is not a state name.
        /// </summary>
        [Fact]
        public void Only_an_exact_blocked_state_name_is_blocked()
        {
            Assert.True(AttentionTeamLayout.IsBlocked(Card("A", state: nameof(AttentionState.BlockedQuestion))));
            Assert.True(AttentionTeamLayout.IsBlocked(Card("A", state: nameof(AttentionState.BlockedPermission))));
            Assert.True(AttentionTeamLayout.IsBlocked(Card("A", state: nameof(AttentionState.BlockedUnknown))));
            Assert.False(AttentionTeamLayout.IsBlocked(Card("A", state: nameof(AttentionState.Working))));
            Assert.False(AttentionTeamLayout.IsBlocked(Card("A", state: ((int)AttentionState.BlockedQuestion).ToString(System.Globalization.CultureInfo.InvariantCulture))));
            Assert.False(AttentionTeamLayout.IsBlocked(Card("A", state: "blockedquestion")));
            Assert.False(AttentionTeamLayout.IsBlocked(Card("A", state: null)));
        }

        // ───────────────────────────────────────────────────────────── zoom

        /// <summary>The Attention pane's zoom is saved and read back by a new instance, clamped like ChatPanel's.</summary>
        [Fact]
        public void Attention_zoom_round_trips_and_clamps()
        {
            Assert.Equal(1.0, new SettingsService(_dir).GetAttentionPanelZoom());

            new SettingsService(_dir).SetAttentionPanelZoom(1.75);
            Assert.Equal(1.75, new SettingsService(_dir).GetAttentionPanelZoom());

            new SettingsService(_dir).SetAttentionPanelZoom(9.0);
            Assert.Equal(5.0, new SettingsService(_dir).GetAttentionPanelZoom());

            new SettingsService(_dir).SetAttentionPanelZoom(0.1);
            Assert.Equal(0.25, new SettingsService(_dir).GetAttentionPanelZoom());

            // Its own key: the chat panel's zoom is not touched.
            Assert.Equal(1.0, new SettingsService(_dir).GetChatPanelZoom());
        }

        /// <summary>
        /// MainForm saves the pane's zoom when it changes and restores it on the path that runs for
        /// both construction and re-creation (ApplyAttentionPanelSettings).
        /// </summary>
        [Fact]
        public void Attention_zoom_is_saved_on_change_and_restored_with_the_other_preferences()
        {
            string mainForm = StripComments(ReadRepoFile("MainForm.cs"));

            string wire = Body(mainForm, "private void WireAttentionPanel()");
            Assert.Matches(@"_attentionPanel\.ZoomChanged\s*\+=\s*\(s,\s*zoom\)\s*=>\s*_settings\?\.SetAttentionPanelZoom\(zoom\)", wire);

            string apply = Body(mainForm, "private void ApplyAttentionPanelSettings()");
            Assert.Contains("_attentionPanel.SetZoomFactor(_settings.GetAttentionPanelZoom())", apply, StringComparison.Ordinal);

            // The control holds the value until the WebView2 exists, and re-raises Ctrl+wheel changes.
            string control = StripComments(ReadRepoFile("AttentionPanel", "AttentionPanelControl.cs"));
            string init = Body(control, "private async void InitializeWebView()");
            Assert.Contains("ZoomFactorChanged", init, StringComparison.Ordinal);
            Assert.Contains("_webView.ZoomFactor = _pendingZoom", init, StringComparison.Ordinal);
        }

        // ───────────────────────────────────────────────────────────── the view contract

        private static IReadOnlyList<string> JsonNames(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .ToList();

        /// <summary>
        /// The grouping's field names are a string contract no compiler checks: each one the host
        /// sends must be read by the view (as <c>g.x</c> for a group, <c>ny.x</c> for the badge), and
        /// the view must read the grouping from the same envelope as the cards.
        /// </summary>
        [Fact]
        public void Every_team_field_the_host_sends_is_read_by_the_view()
        {
            string script = StripComments(Regex.Replace(ReadRepoFile("AttentionPanel", "attention-panel.html"),
                @"<style\b[^>]*>.*?</style>", string.Empty, RegexOptions.Singleline));

            var groupNames = JsonNames(typeof(AttentionTeamGroup));
            Assert.DoesNotContain(null, groupNames);
            var unreadGroup = groupNames.Where(n => !Regex.IsMatch(script, @"\bg\." + n + @"\b")).ToList();
            Assert.True(unreadGroup.Count == 0, "Group fields the view never reads: " + string.Join(", ", unreadGroup));

            var nyNames = JsonNames(typeof(AttentionNeedsYou));
            Assert.DoesNotContain(null, nyNames);
            var unreadNy = nyNames.Where(n => !Regex.IsMatch(script, @"\bny\." + n + @"\b")).ToList();
            Assert.True(unreadNy.Count == 0, "Badge fields the view never reads: " + string.Join(", ", unreadNy));

            Assert.Contains("applySessions(msg.sessions, msg.teams)", script, StringComparison.Ordinal);
            Assert.Contains("type = \"sessions\", sessions, quota, teams", ReadRepoFile("AttentionPanel", "AttentionPanelControl.cs"), StringComparison.Ordinal);
        }

        /// <summary>
        /// The kind and role strings the host emits are the ones the view compares against, in its
        /// SCRIPT (comments stripped, so prose naming them cannot satisfy this).
        /// </summary>
        [Fact]
        public void Every_group_kind_is_known_to_the_view()
        {
            string script = StripComments(Regex.Replace(ReadRepoFile("AttentionPanel", "attention-panel.html"),
                @"<style\b[^>]*>.*?</style>", string.Empty, RegexOptions.Singleline));
            Assert.True(script.Length > 5000, "attention-panel.html script extraction came back too short to mean anything.");

            Assert.Contains("g.kind === \"" + AttentionTeamLayout.KindUnknown + "\"", script, StringComparison.Ordinal);
            Assert.Matches(@"\bs\.role\s*===\s*""" + AttentionTeamRoles.Pm + @"""", script);
        }
    }
}
