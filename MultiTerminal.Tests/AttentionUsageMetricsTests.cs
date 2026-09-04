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
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Context fill on the cards, account quota in the header (task 85af4635).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are almost all HONESTY tests rather than feature tests, and that is deliberate. The
    /// feature is two numbers on a panel; the risk is that one of them is wrong in a way nobody can
    /// see. This rail exists because "Alice is waiting for your input" sat on a card for 27 minutes
    /// looking current, and a stale percentage is that same failure in a new position — worse,
    /// because the Owner ACTS on this number when deciding whether to hand off.
    /// </para>
    /// <para>
    /// The rule these keep coming back to: <b>absence of a reading is not a reading of zero.</b>
    /// </para>
    /// </remarks>
    public class AttentionUsageMetricsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        private static AgentAttentionEntry Entry(string agent = "Alice") =>
            new AgentAttentionEntry
            {
                SessionId = "sess-1",
                AgentName = agent,
                State = AttentionState.Working,
                EnteredAtUtc = Now.AddMinutes(-1),
            };

        private static List<AttentionCard> Project(TerminalUsageStats stats, string agent = "Alice")
        {
            var byAgent = stats == null
                ? null
                : new Dictionary<string, TerminalUsageStats>(StringComparer.OrdinalIgnoreCase)
                {
                    [agent] = stats,
                };

            return AttentionCardProjector.Project(
                new[] { Entry(agent) }, null, null, Now, null, byAgent);
        }

        // ───────────────── absence is not zero ─────────────────

        /// <summary>
        /// THE ONE THAT MATTERS. No stats at all must leave the percentage NULL so the view can
        /// render "--%".
        /// </summary>
        /// <remarks>
        /// Zero would say "plenty of room" at the exact moment the panel knows nothing, and the
        /// Owner would read it as a fact and keep working. This is not a rare path: an installed
        /// machine with no statusline files is in it for every terminal at once — the screenshot on
        /// GitHub issue #20 shows precisely that, three unavailable readings side by side.
        /// </remarks>
        [Fact]
        public void No_stats_leaves_the_context_percentage_unknown_rather_than_zero()
        {
            var card = Assert.Single(Project(null));

            Assert.Null(card.ContextPercent);
            Assert.Equal(-1, card.ContextAgeSeconds);
            Assert.False(card.ContextStale);
        }

        /// <summary>
        /// A reading that EXISTS but is not <c>Available</c> is the reader's way of saying "no file,
        /// or a torn one". Its other fields are defaults, not measurements, so trusting them would
        /// publish a zero the reader never claimed.
        /// </summary>
        [Fact]
        public void An_unavailable_reading_is_treated_exactly_as_a_missing_one()
        {
            var card = Assert.Single(Project(new TerminalUsageStats
            {
                Available = false,
                ContextPercent = 0,
            }));

            Assert.Null(card.ContextPercent);
        }

        /// <summary>
        /// Available, but the context field itself absent — the quota half of the file can be
        /// present while the context half is not.
        /// </summary>
        [Fact]
        public void An_available_reading_with_no_context_value_is_still_unknown()
        {
            var card = Assert.Single(Project(new TerminalUsageStats
            {
                Available = true,
                ContextPercent = null,
                FiveHourPercent = 40,
            }));

            Assert.Null(card.ContextPercent);
        }

        /// <summary>A real reading arrives intact, including a genuine zero.</summary>
        /// <remarks>
        /// The counterweight to the three above: 0% must still be reportable. A guard that turned
        /// every zero into "unknown" would hide a freshly-cleared terminal, which is real
        /// information — the whole point of the distinction is that MEASURED zero and NO MEASUREMENT
        /// are different claims.
        /// </remarks>
        [Fact]
        public void A_measured_zero_is_reported_as_zero_not_as_unknown()
        {
            var card = Assert.Single(Project(new TerminalUsageStats
            {
                Available = true,
                ContextPercent = 0,
            }));

            Assert.Equal(0, card.ContextPercent);
        }

        // ───────────────── the two staleness kinds stay apart ─────────────────

        /// <summary>
        /// <c>StatusLineStatsReader</c> keeps context staleness and quota staleness separate because
        /// "a terminal can have a fresh context fill but a stale rate-cap reading". Collapsing them
        /// would mark a good number bad, or a bad number good.
        /// </summary>
        [Fact]
        public void A_stale_quota_does_not_make_the_context_reading_look_stale()
        {
            var card = Assert.Single(Project(new TerminalUsageStats
            {
                Available = true,
                ContextPercent = 42,
                Stale = false,
                QuotaStale = true,
            }));

            Assert.False(card.ContextStale);
            Assert.Equal(42, card.ContextPercent);
        }

        /// <summary>And the reverse: a stale context reading is marked, not silently shown.</summary>
        [Fact]
        public void A_stale_context_reading_is_marked_and_still_carries_its_age()
        {
            var card = Assert.Single(Project(new TerminalUsageStats
            {
                Available = true,
                ContextPercent = 91,
                Stale = true,
                AgeSeconds = 400,
            }));

            Assert.True(card.ContextStale);
            Assert.Equal(400, card.ContextAgeSeconds);
            Assert.Equal(91, card.ContextPercent);
        }

        // ───────────────── the account quota ─────────────────

        /// <summary>Stored as USED, shown as LEFT — converted once, in one place.</summary>
        [Fact]
        public void Quota_is_published_as_remaining_not_as_used()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats { Available = true, FiveHourPercent = 30, SevenDayPercent = 82 },
            });

            Assert.Equal(70, quota.FiveHourLeft);
            Assert.Equal(18, quota.SevenDayLeft);
        }

        /// <summary>
        /// Same rule as the cards: nothing readable yields nulls, so the header can hide itself.
        /// "0% left" would be a fabricated emergency.
        /// </summary>
        [Fact]
        public void No_readable_quota_yields_nulls_rather_than_zero_left()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats { Available = false, FiveHourPercent = 10 },
                null,
            });

            Assert.Null(quota.FiveHourLeft);
            Assert.Null(quota.SevenDayLeft);
            Assert.Equal(-1, quota.AgeSeconds);
        }

        /// <summary>An empty or null input is the startup state, not an error.</summary>
        [Fact]
        public void An_empty_input_produces_an_empty_quota()
        {
            Assert.Null(AttentionQuota.From(null).FiveHourLeft);
            Assert.Null(AttentionQuota.From(Array.Empty<TerminalUsageStats>()).SevenDayLeft);
        }

        /// <summary>
        /// Every terminal reports the SAME account numbers, so this is a choice of which copy to
        /// trust, not an aggregation. Fresh beats stale before anything else.
        /// </summary>
        [Fact]
        public void A_fresh_reading_is_preferred_over_a_stale_one()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats
                {
                    Available = true, FiveHourPercent = 90, QuotaStale = true,
                    QuotaSource = "shared", QuotaAgeSeconds = 5,
                },
                new TerminalUsageStats
                {
                    Available = true, FiveHourPercent = 20, QuotaStale = false,
                    QuotaSource = "per-terminal", QuotaAgeSeconds = 30,
                },
            });

            Assert.Equal(80, quota.FiveHourLeft);
            Assert.False(quota.Stale);
        }

        /// <summary>
        /// Freshness settled, the authoritative shared file wins over one terminal's private copy.
        /// </summary>
        [Fact]
        public void The_shared_account_file_wins_a_tie_on_freshness()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats
                {
                    Available = true, FiveHourPercent = 60,
                    QuotaSource = "per-terminal", QuotaAgeSeconds = 10,
                },
                new TerminalUsageStats
                {
                    Available = true, FiveHourPercent = 25,
                    QuotaSource = "shared", QuotaAgeSeconds = 10,
                },
            });

            Assert.Equal(75, quota.FiveHourLeft);
            Assert.True(quota.FromSharedFile);
        }

        /// <summary>
        /// A stale reading is still shown — marked — rather than suppressed. This is the OPPOSITE
        /// of the rule for a missing reading, and the distinction is the whole design: a stale
        /// number that says it is stale remains information the Owner can weigh; a missing number
        /// rendered as a value would be an invention.
        /// </summary>
        [Fact]
        public void A_stale_quota_is_still_shown_but_flagged()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats
                {
                    Available = true, FiveHourPercent = 45, SevenDayPercent = 10,
                    QuotaStale = true, QuotaAgeSeconds = 900,
                },
            });

            Assert.Equal(55, quota.FiveHourLeft);
            Assert.True(quota.Stale);
            Assert.Equal(900, quota.AgeSeconds);
        }

        /// <summary>
        /// A reading carrying neither number is not a quota reading, and must not outrank a real one
        /// just for being fresher.
        /// </summary>
        [Fact]
        public void An_empty_but_fresh_reading_does_not_displace_a_real_one()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats { Available = true, QuotaAgeSeconds = 1 },
                new TerminalUsageStats { Available = true, FiveHourPercent = 35, QuotaAgeSeconds = 60 },
            });

            Assert.Equal(65, quota.FiveHourLeft);
        }

        /// <summary>
        /// The meter is a rolling-window estimate and can report over 100 when a window closes
        /// mid-read. "-7% left" looks like a bug in the panel rather than a quirk of the source,
        /// and negative headroom means the same thing as none.
        /// </summary>
        [Fact]
        public void Over_budget_clamps_to_zero_left_rather_than_going_negative()
        {
            var quota = AttentionQuota.From(new[]
            {
                new TerminalUsageStats { Available = true, FiveHourPercent = 107 },
            });

            Assert.Equal(0, quota.FiveHourLeft);
        }

        // ───────────── the contract the card sweeps cannot reach ─────────────

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string PanelHtml()
        {
            string path = Path.Combine(RepoRoot(), "AttentionPanel", "attention-panel.html");
            Assert.True(File.Exists(path), $"Could not locate the panel html at '{path}'.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The panel html with its <c>&lt;style&gt;</c> block removed, for the sweeps below.
        /// </summary>
        /// <remarks>
        /// NOT tidiness — the sweeps are WRONG without it, and that was demonstrated rather than
        /// theorised. The CSS rule <c>.quota.stale { … }</c> matches <c>\bquota\.stale\b</c>, so the
        /// stylesheet alone satisfied the forward sweep for that one name: deleting both JavaScript
        /// reads of <c>quota.stale</c> left the test green.
        /// <para>
        /// A contract test that can be satisfied by a CSS class name is not checking the contract,
        /// it is checking that a string appears in a file — and it fails in the direction that
        /// matters, quietly reporting coverage it does not have. Stripping the style block leaves
        /// only code the browser executes.
        /// </para>
        /// </remarks>
        private static string PanelScript() =>
            Regex.Replace(PanelHtml(), @"<style\b[^>]*>.*?</style>", string.Empty,
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

        /// <summary>
        /// The stripper must actually strip. Without this, a change to the panel's markup that broke
        /// the regex would silently restore the blind spot above — the sweeps would keep passing and
        /// nothing would say why.
        /// </summary>
        [Fact]
        public void The_sweep_ignores_the_stylesheet()
        {
            Assert.Contains(".quota.stale", PanelHtml(), StringComparison.Ordinal);
            Assert.DoesNotContain(".quota.stale", PanelScript(), StringComparison.Ordinal);

            // And the script half survived: stripping too much would make the sweeps vacuous in the
            // other direction, passing because they find nothing at all to check.
            Assert.Contains("applyQuota", PanelScript(), StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> QuotaJsonNames() =>
            typeof(AttentionQuota)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

        /// <summary>
        /// Every quota property must carry an explicit JSON name, for the reason spelled out at
        /// length on <see cref="AttentionCard"/>: left to the default policy they ship as PascalCase
        /// while the view reads camelCase, and the field silently arrives as <c>undefined</c>.
        /// </summary>
        [Fact]
        public void Every_quota_property_declares_an_explicit_json_name()
        {
            var unnamed = typeof(AttentionQuota)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() == null)
                .Select(p => p.Name)
                .ToList();

            Assert.True(unnamed.Count == 0,
                "AttentionQuota properties with no explicit JSON name: " + string.Join(", ", unnamed));
        }

        /// <summary>
        /// THE GAP THIS FILE EXISTS TO CLOSE. <c>AttentionPanelContractTests</c> sweeps host↔view
        /// field names in both directions, but it reads <see cref="AttentionCard"/> only — the quota
        /// is not a card field, so the header payload had no such net at all.
        /// </summary>
        /// <remarks>
        /// Matched against <c>quota.</c> rather than <c>q.</c>: the card renderer already has a local
        /// named <c>q</c> for the detail-line &lt;q&gt; element, and the shorter prefix caught it — the
        /// sweep's first run failed on <c>q.textContent</c>. A contract test that reads the file with a
        /// regex has to own its own ambiguity.
        /// </remarks>
        [Fact]
        public void Every_quota_field_the_host_sends_is_read_by_the_panel()
        {
            string html = PanelScript();

            var unread = QuotaJsonNames()
                .Where(name => !Regex.IsMatch(html, @"\bquota\." + Regex.Escape(name) + @"\b"))
                .ToList();

            Assert.True(unread.Count == 0,
                "AttentionQuota fields the panel never reads (renamed on one side only?): "
                + string.Join(", ", unread));
        }

        /// <summary>
        /// The other direction, which fails even more quietly: the view must not read a quota field
        /// the host never sends, because that arrives as <c>undefined</c> and renders as blank.
        /// </summary>
        [Fact]
        public void Every_quota_field_the_panel_reads_is_sent_by_the_host()
        {
            string html = PanelScript();
            var sent = new HashSet<string>(QuotaJsonNames(), StringComparer.Ordinal);

            var read = Regex.Matches(html, @"\bquota\.([A-Za-z_][A-Za-z0-9_]*)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            var unknown = read.Where(n => !sent.Contains(n)).ToList();

            Assert.True(unknown.Count == 0,
                "Panel reads quota fields the host does not send: " + string.Join(", ", unknown));
        }

        /// <summary>
        /// The quota must travel INSIDE the sessions envelope. A separate message would be a second
        /// freshness path, letting the header and the cards come from different moments while
        /// presenting as one reading — this panel's signature failure, in a new place.
        /// </summary>
        [Fact]
        public void The_panel_reads_the_quota_from_the_sessions_message()
        {
            string html = PanelScript();

            Assert.Matches(@"applyQuota\(\s*msg\.quota\s*\)", html);
        }
    }
}
