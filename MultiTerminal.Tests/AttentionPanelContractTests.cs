using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MultiTerminal.AttentionPanel;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the attention panel's C#/JavaScript boundary and its card projection
    /// (task 2289bb8a item 9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundary is a set of string field names that NO COMPILER CHECKS. Rename one side and
    /// the field arrives as <c>undefined</c>, which the view renders as an empty string — no
    /// error, no exception, just a card that quietly stops saying something. A mismatch of exactly
    /// this kind once produced a CRITICAL on a clean build with 442 green tests, which is why
    /// <c>BoardHudDoorwayTests</c> exists and why this follows it.
    /// </para>
    /// <para>
    /// These tests read the real files from the repository rather than a copy, so they fail when
    /// the shipped artifacts drift — including the csproj entry whose absence makes the panel
    /// render blank with no diagnostic at all.
    /// </para>
    /// </remarks>
    public class AttentionPanelContractTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadRepoFile(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return File.ReadAllText(path);
        }

        private static string PanelHtml() => ReadRepoFile("AttentionPanel", "attention-panel.html");

        private static IReadOnlyList<string> CardJsonNames() =>
            typeof(AttentionCard)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

        // ─────────────────────────────────────────────────────────────────
        // The boundary
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every property the host serialises must carry an explicit JSON name. Left to the
        /// default policy they would ship as PascalCase while the view reads camelCase, which is
        /// the precise shape of the Run-1 CRITICAL.
        /// </summary>
        [Fact]
        public void Every_card_property_declares_an_explicit_json_name()
        {
            var missing = typeof(AttentionCard)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() == null)
                .Select(p => p.Name)
                .ToList();

            Assert.True(missing.Count == 0,
                "AttentionCard properties without [JsonPropertyName]: " + string.Join(", ", missing));
        }

        /// <summary>Host to view: nothing the host sends may be a name the view never mentions.</summary>
        [Fact]
        public void Every_field_the_host_sends_is_read_by_the_panel()
        {
            string html = PanelHtml();

            var unread = CardJsonNames()
                .Where(name => !Regex.IsMatch(html, @"\bs\." + Regex.Escape(name) + @"\b"))
                .ToList();

            Assert.True(unread.Count == 0,
                "AttentionCard fields the panel never reads (renamed on one side only?): "
                + string.Join(", ", unread));
        }

        /// <summary>
        /// View to host: the view must not read a field the host never sends. That direction fails
        /// even more quietly — the value is simply undefined and renders as blank.
        /// </summary>
        [Fact]
        public void Every_field_the_panel_reads_is_sent_by_the_host()
        {
            string html = PanelHtml();
            var sent = new HashSet<string>(CardJsonNames(), StringComparer.Ordinal);

            // Locals in the render loop are named `s`, matching the projected card.
            var read = Regex.Matches(html, @"\bs\.([A-Za-z_][A-Za-z0-9_]*)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            var unknown = read.Where(n => !sent.Contains(n)).ToList();

            Assert.True(unknown.Count == 0,
                "Panel reads fields the host does not send: " + string.Join(", ", unknown));
        }

        /// <summary>
        /// The view keys its stripe, pip and pulse off literal state names, so the ENUM SPELLINGS
        /// are part of the contract. Renaming a member is a silent restyle: the card keeps
        /// rendering, just without the colour that says someone is waiting.
        /// </summary>
        [Fact]
        public void Every_state_the_panel_styles_exists_in_the_enum()
        {
            string html = PanelHtml();
            var enumNames = new HashSet<string>(Enum.GetNames(typeof(AttentionState)), StringComparer.Ordinal);

            var styled = Regex.Matches(html, @"data-state=""([A-Za-z]+)""")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            Assert.NotEmpty(styled);

            var unknown = styled.Where(n => !enumNames.Contains(n)).ToList();
            Assert.True(unknown.Count == 0,
                "Panel styles states that are not AttentionState members: " + string.Join(", ", unknown));
        }

        /// <summary>
        /// Every BLOCKING state must be in the view's BLOCKING map, or an agent genuinely waiting
        /// on the owner would render without a pulse — the one thing the panel exists to do.
        /// </summary>
        [Fact]
        public void Every_blocking_state_is_treated_as_blocking_by_the_panel()
        {
            string html = PanelHtml();

            var blocking = Enum.GetValues(typeof(AttentionState))
                .Cast<AttentionState>()
                .Where(st => new AgentAttentionEntry { State = st }.IsBlocking)
                .Select(st => st.ToString())
                .ToList();

            Assert.NotEmpty(blocking);

            var mapMatch = Regex.Match(html, @"BLOCKING\s*=\s*\{(?<body>[^}]*)\}");
            Assert.True(mapMatch.Success, "Could not find the panel's BLOCKING map.");

            string body = mapMatch.Groups["body"].Value;
            var missing = blocking.Where(n => !body.Contains(n, StringComparison.Ordinal)).ToList();

            Assert.True(missing.Count == 0,
                "Blocking states missing from the panel's BLOCKING map: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Without the Content Include the HTML is absent from the build output and the panel
        /// loads a blank WebView2 WITH NO ERROR. Documented as a trap in the repo rules; asserted
        /// here so it cannot regress silently.
        /// </summary>
        [Fact]
        public void The_panel_html_is_shipped_by_the_csproj()
        {
            string csproj = ReadRepoFile("MultiTerminal.csproj");

            var match = Regex.Match(
                csproj,
                @"<Content\s+Include=""AttentionPanel\\attention-panel\.html"">\s*<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
                RegexOptions.Singleline);

            Assert.True(match.Success,
                "attention-panel.html is not in MultiTerminal.csproj as Content/PreserveNewest — "
                + "the panel would load blank with no error.");
        }

        // ─────────────────────────────────────────────────────────────────
        // The projection
        // ─────────────────────────────────────────────────────────────────

        private static AgentAttentionEntry Entry(AttentionState state, DateTime enteredUtc) =>
            new AgentAttentionEntry
            {
                SessionId = "sess-1",
                AgentName = "Alice",
                State = state,
                EnteredAtUtc = enteredUtc,
                Detail = "why",
                Project = "MultiTerminal",
            };

        [Fact]
        public void Projection_carries_state_verb_age_and_claim()
        {
            var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            var claims = new Dictionary<string, AttentionTicketClaim>
            {
                ["Alice"] = new AttentionTicketClaim
                {
                    TaskId = "2289bb8a",
                    ItemLabel = "item 2 of 7",
                    UpdatedAtUtc = now.AddMinutes(-41),
                },
            };

            var cards = AttentionCardProjector.Project(
                new[] { Entry(AttentionState.BlockedPermission, now.AddMinutes(-6)) },
                new Dictionary<string, string> { ["Alice"] = "#a6e3a1" },
                claims,
                now);

            var card = Assert.Single(cards);
            Assert.Equal("BlockedPermission", card.State);
            Assert.Equal("Needs permission", card.ObservedVerb);
            Assert.Equal(360, card.SinceSeconds);
            Assert.Equal("2289bb8a", card.TicketId);
            Assert.Equal(41 * 60, card.ClaimAgeSeconds);
            Assert.Equal("#a6e3a1", card.Color);
        }

        /// <summary>
        /// An agent claiming nothing must produce no ticket, not a blank chip. The card then shows
        /// only what was observed, which is the honest rendering.
        /// </summary>
        [Fact]
        public void An_agent_with_no_claim_gets_no_ticket()
        {
            var now = DateTime.UtcNow;

            var cards = AttentionCardProjector.Project(
                new[] { Entry(AttentionState.Working, now) },
                new Dictionary<string, string>(),
                new Dictionary<string, AttentionTicketClaim>(),
                now);

            Assert.Null(Assert.Single(cards).TicketId);
        }

        /// <summary>
        /// Unknown must never read as "idle" or "fine". Ambient notifications are not persisted
        /// while remote mode is off, so after a restart every session starts here — and calling
        /// that idle would state as fact the one thing the panel does not know.
        /// </summary>
        [Fact]
        public void Unknown_state_says_nothing_observed_rather_than_idle()
        {
            var now = DateTime.UtcNow;

            var cards = AttentionCardProjector.Project(
                new[] { Entry(AttentionState.Unknown, now) },
                null,
                null,
                now);

            var card = Assert.Single(cards);
            Assert.Equal("Nothing observed yet", card.ObservedVerb);
            Assert.DoesNotContain("idle", card.ObservedVerb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A clock skew must not render as a negative age.</summary>
        [Fact]
        public void A_future_timestamp_clamps_to_zero_rather_than_going_negative()
        {
            var now = DateTime.UtcNow;

            var cards = AttentionCardProjector.Project(
                new[] { Entry(AttentionState.Working, now.AddMinutes(5)) },
                null,
                null,
                now);

            Assert.Equal(0, Assert.Single(cards).SinceSeconds);
        }

        /// <summary>The panel must survive an empty roster without throwing.</summary>
        [Fact]
        public void Null_inputs_project_to_an_empty_list()
        {
            Assert.Empty(AttentionCardProjector.Project(null, null, null, DateTime.UtcNow));
        }
    }
}
