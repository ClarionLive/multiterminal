using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MultiTerminal.AttentionPanel;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The configurable glance language: ambient/alarm preferences, project identity, and the
    /// rules that keep the two channels honest (task 42052f0c).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule pinned here fails INVISIBLY if broken. A renamed message field arrives as
    /// <c>undefined</c>; an unrecognised stored preference renders a card with no ambient layer;
    /// project grouping that outranks a blocked agent buries the one thing the rail exists to
    /// surface; and a card that treats acknowledgement as clearing looks exactly like a calm card.
    /// None of these produces an error, which is the whole reason they are asserted.
    /// </para>
    /// </remarks>
    public sealed class AttentionGlanceLanguageTests : IDisposable
    {
        private readonly string _dir;

        public AttentionGlanceLanguageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_glance_test_{Guid.NewGuid():N}");
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

        private SettingsService NewService() => new SettingsService(_dir);

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadRepoFile(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        // ---------------------------------------------------------------- preferences

        /// <summary>The Owner's chosen pair is what an unconfigured install gets.</summary>
        [Fact]
        public void Defaults_are_token_stream_and_red_alert()
        {
            var s = NewService();
            Assert.Equal("stream", s.GetAttentionPanelAmbient());
            Assert.Equal("redalert", s.GetAttentionPanelAlarm());
            Assert.Equal("attention", s.GetAttentionPanelOrder());
        }

        [Fact]
        public void Preferences_survive_a_restart()
        {
            NewService().SetAttentionPanelAmbient("walker");
            NewService().SetAttentionPanelAlarm("beacon");
            NewService().SetAttentionPanelOrder("project");

            // A SECOND instance over the same folder — an assertion against the same in-memory
            // object would pass even if nothing reached disk, which is the failure that matters.
            Assert.Equal("walker", NewService().GetAttentionPanelAmbient());
            Assert.Equal("beacon", NewService().GetAttentionPanelAlarm());
            Assert.Equal("project", NewService().GetAttentionPanelOrder());
        }

        /// <summary>
        /// settings.txt is a plaintext file a user can edit, and a value can outlive a rename
        /// across versions. An unrecognised treatment would select nothing and render a card with
        /// no ambient layer AND no error to explain it.
        /// </summary>
        [Theory]
        [InlineData("sparkles")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Unrecognised_ambient_falls_back_to_the_default(string stored)
        {
            NewService().SetAttentionPanelAmbient(stored);
            Assert.Equal("stream", NewService().GetAttentionPanelAmbient());
        }

        [Theory]
        [InlineData("airhorn")]
        [InlineData(null)]
        public void Unrecognised_alarm_falls_back_to_the_default(string stored)
        {
            NewService().SetAttentionPanelAlarm(stored);
            Assert.Equal("redalert", NewService().GetAttentionPanelAlarm());
        }

        [Fact]
        public void Preference_values_are_case_insensitive_but_stored_canonically()
        {
            NewService().SetAttentionPanelAmbient("WALKER");
            Assert.Equal("walker", NewService().GetAttentionPanelAmbient());
        }

        /// <summary>
        /// "Off" is not decoration. A user who dislikes motion and cannot switch it off closes the
        /// whole pane — and then the ALARM cannot reach them either.
        /// </summary>
        [Fact]
        public void Off_is_a_selectable_ambient()
        {
            NewService().SetAttentionPanelAmbient("off");
            Assert.Equal("off", NewService().GetAttentionPanelAmbient());
        }

        // ------------------------------------------------- disk allowlist == wire allowlist

        /// <summary>
        /// The settings layer guards the disk and the control guards the wire, so the two lists are
        /// deliberately separate code. If they DISAGREE, a value that persists fine is silently
        /// rewritten on its way to the view (or vice versa) and the panel shows a treatment the
        /// user did not choose, with nothing logged.
        /// </summary>
        [Fact]
        public void Control_and_settings_agree_on_every_allowed_value()
        {
            var s = NewService();

            foreach (var ambient in AttentionPanelControl.Ambients)
            {
                s.SetAttentionPanelAmbient(ambient);
                Assert.Equal(ambient, s.GetAttentionPanelAmbient());
            }

            foreach (var alarm in AttentionPanelControl.Alarms)
            {
                s.SetAttentionPanelAlarm(alarm);
                Assert.Equal(alarm, s.GetAttentionPanelAlarm());
            }

            foreach (var order in AttentionPanelControl.Orders)
            {
                s.SetAttentionPanelOrder(order);
                Assert.Equal(order, s.GetAttentionPanelOrder());
            }
        }

        /// <summary>The view must be able to render every value the control is willing to send.</summary>
        [Fact]
        public void The_view_implements_every_treatment_the_control_allows()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");

            foreach (var ambient in AttentionPanelControl.Ambients)
            {
                Assert.True(html.Contains("\"" + ambient + "\"", StringComparison.Ordinal),
                    $"attention-panel.html never mentions ambient '{ambient}', so choosing it renders nothing.");
            }

            foreach (var alarm in AttentionPanelControl.Alarms)
            {
                Assert.True(html.Contains("\"" + alarm + "\"", StringComparison.Ordinal),
                    $"attention-panel.html never mentions alarm '{alarm}', so choosing it renders nothing.");
            }
        }

        /// <summary>
        /// The message names crossing the WebView2 boundary. No compiler checks these; a rename on
        /// one side alone is silent.
        /// </summary>
        [Fact]
        public void Every_message_type_exists_on_both_sides()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");
            string cs = ReadRepoFile("AttentionPanel", "AttentionPanelControl.cs");

            // host -> view
            foreach (var t in new[] { "ambient", "alarm", "focused" })
            {
                Assert.True(cs.Contains("type = \"" + t + "\"", StringComparison.Ordinal), $"control never posts '{t}'");
                Assert.True(html.Contains("msg.type === \"" + t + "\"", StringComparison.Ordinal), $"view never handles '{t}'");
            }

            // view -> host
            foreach (var t in new[] { "set_ambient", "set_alarm", "set_order" })
            {
                Assert.True(html.Contains("type: \"" + t + "\"", StringComparison.Ordinal), $"view never posts '{t}'");
                Assert.True(cs.Contains("\"" + t + "\"", StringComparison.Ordinal), $"control never handles '{t}'");
            }

            // Payload field names, which are a separate contract from the message names.
            Assert.Contains("ambient = _ambient", cs, StringComparison.Ordinal);
            Assert.Contains("alarm = _alarm", cs, StringComparison.Ordinal);
            Assert.Contains("msg.ambient", html, StringComparison.Ordinal);
            Assert.Contains("msg.alarm", html, StringComparison.Ordinal);
            Assert.Contains("msg.sessionId", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// Project grouping must never bury a blocked agent. The view sorts by a rank that puts
        /// blocking first and only then applies the mode, so this asserts the ORDER of those two
        /// steps inside the project branch — a guard placed after the project comparison would
        /// already have re-sorted an alarm underneath a heading.
        /// </summary>
        [Fact]
        public void Project_grouping_ranks_blocking_first()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");

            int branch = html.IndexOf("order === \"project\"", StringComparison.Ordinal);
            Assert.True(branch > 0, "the project ordering branch is gone");

            int rankGuard = html.IndexOf("rank(a) - rank(b)", branch, StringComparison.Ordinal);
            int projectCompare = html.IndexOf("localeCompare", branch, StringComparison.Ordinal);

            Assert.True(rankGuard > 0, "the project branch no longer ranks by blocking at all");
            Assert.True(projectCompare > 0, "the project branch no longer compares projects");
            Assert.True(rankGuard < projectCompare,
                "blocking must be ranked BEFORE projects are compared, or an alarm sorts under a project heading.");
        }

        /// <summary>
        /// Acknowledgement silences the alarm; it must not touch the state. The view expresses this
        /// by adding a CSS class and an "acknowledged" marker only — if it ever started rewriting
        /// the state, a card would go calm over an agent that is still waiting, which is
        /// indistinguishable from nobody needing you.
        /// </summary>
        [Fact]
        public void Acknowledgement_does_not_alter_state()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");

            int activate = html.IndexOf("function activate(", StringComparison.Ordinal);
            Assert.True(activate > 0, "the click handler is gone");

            int end = html.IndexOf("\n}", activate, StringComparison.Ordinal);
            string body = html.Substring(activate, end - activate);

            Assert.Contains("acked[s.id]", body, StringComparison.Ordinal);
            Assert.DoesNotContain("s.state =", body, StringComparison.Ordinal);
            Assert.DoesNotContain("s.observedVerb =", body, StringComparison.Ordinal);
            Assert.DoesNotContain("sinceSeconds =", body, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- project on the card

        private static AgentAttentionEntry Entry(string agent, string project = null) => new AgentAttentionEntry
        {
            SessionId = "sess-" + agent,
            AgentName = agent,
            State = AttentionState.Working,
            EnteredAtUtc = DateTime.UtcNow,
            Project = project,
        };

        /// <summary>The fallback fills the gap that made the project line blank for most terminals.</summary>
        [Fact]
        public void Claimed_task_project_fills_in_when_nothing_was_observed()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Alice") },
                null, null, DateTime.UtcNow,
                new Dictionary<string, string> { ["Alice"] = "MultiTerminal" });

            Assert.Equal("MultiTerminal", cards.Single().Project);
        }

        /// <summary>
        /// Observed beats inferred. What the hook read off disk is where the agent IS running; the
        /// claimed task's project is a guess about where it is working, and an agent can legitimately
        /// hold a ticket in one project while running in another.
        /// </summary>
        [Fact]
        public void Observed_project_wins_over_the_claimed_task_fallback()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Alice", "ObservedProject") },
                null, null, DateTime.UtcNow,
                new Dictionary<string, string> { ["Alice"] = "ClaimedProject" });

            Assert.Equal("ObservedProject", cards.Single().Project);
        }

        /// <summary>
        /// With neither source the card says nothing rather than guessing. A wrong project name is
        /// worse than none: it is unfalsifiable from the rail.
        /// </summary>
        [Fact]
        public void No_project_from_either_source_stays_empty()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Alice") }, null, null, DateTime.UtcNow, null);

            Assert.True(string.IsNullOrEmpty(cards.Single().Project));
        }

        /// <summary>A blank observed value is a gap to fill, not a value to preserve.</summary>
        [Fact]
        public void Whitespace_observed_project_is_treated_as_missing()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Alice", "   ") },
                null, null, DateTime.UtcNow,
                new Dictionary<string, string> { ["Alice"] = "MultiTerminal" });

            Assert.Equal("MultiTerminal", cards.Single().Project);
        }

        /// <summary>The fallback is per agent — one agent's project must not leak onto another's card.</summary>
        [Fact]
        public void Fallback_is_matched_per_agent()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Alice"), Entry("Diana") },
                null, null, DateTime.UtcNow,
                new Dictionary<string, string> { ["Alice"] = "MultiTerminal" });

            Assert.Equal("MultiTerminal", cards.Single(c => c.Agent == "Alice").Project);
            Assert.True(string.IsNullOrEmpty(cards.Single(c => c.Agent == "Diana").Project));
        }

        // ------------------------------------------------- pipeline run 1: block identity
        //
        // Acknowledgement used to expire on a DROP in the whole-second age. Two blocks that both
        // sample zero are indistinguishable under that rule, so an early click adopted the next
        // block and a live alarm rendered permanently calm. These pin the identity that replaced it.

        private static AgentAttentionEntry Blocked(string agent, DateTime enteredAtUtc) => new AgentAttentionEntry
        {
            SessionId = "sess-" + agent,
            AgentName = agent,
            State = AttentionState.BlockedPermission,
            EnteredAtUtc = enteredAtUtc,
        };

        /// <summary>
        /// The exact defect: two DIFFERENT blocks whose ages both round to zero must still be
        /// distinguishable. This is the assertion that goes red if anyone reverts to age-based
        /// identity, because under the old rule both cards were identical.
        /// </summary>
        [Fact]
        public void Two_blocks_inside_one_second_are_distinguishable()
        {
            var first = new DateTime(2026, 9, 3, 12, 0, 0, 120, DateTimeKind.Utc);
            var second = first.AddMilliseconds(400);          // same whole second, different block
            var now = second.AddMilliseconds(50);

            var a = AttentionCardProjector.Project(new[] { Blocked("Alice", first) }, null, null, now).Single();
            var b = AttentionCardProjector.Project(new[] { Blocked("Alice", second) }, null, null, now).Single();

            // The age the view displays cannot tell these apart — that is the trap, asserted so the
            // reason for carrying a separate identity stays visible.
            Assert.Equal(a.SinceSeconds, b.SinceSeconds);
            Assert.Equal(0, a.SinceSeconds);

            Assert.NotEqual(a.EnteredAtEpochMs, b.EnteredAtEpochMs);
        }

        /// <summary>The identity is stable across pushes WITHIN one block, or the ack would drop instantly.</summary>
        [Fact]
        public void Block_identity_is_stable_while_the_block_lasts()
        {
            var entered = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var entry = Blocked("Alice", entered);

            var early = AttentionCardProjector.Project(new[] { entry }, null, null, entered.AddSeconds(1)).Single();
            var later = AttentionCardProjector.Project(new[] { entry }, null, null, entered.AddMinutes(5)).Single();

            Assert.Equal(early.EnteredAtEpochMs, later.EnteredAtEpochMs);
            Assert.NotEqual(early.SinceSeconds, later.SinceSeconds);
        }

        /// <summary>An unset entry reads as "no identity" rather than a spuriously distinct one.</summary>
        [Fact]
        public void Unset_entry_time_projects_as_zero_not_a_negative_epoch()
        {
            var card = AttentionCardProjector.Project(
                new[] { new AgentAttentionEntry { SessionId = "s", AgentName = "Alice", State = AttentionState.Working } },
                null, null, DateTime.UtcNow).Single();

            Assert.Equal(0, card.EnteredAtEpochMs);
        }

        /// <summary>
        /// The C#-to-JavaScript half of the identity contract, which no compiler checks. The field
        /// is useless if the view still keys acknowledgement off the age.
        /// </summary>
        [Fact]
        public void The_view_keys_acknowledgement_off_the_block_identity()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");

            Assert.Contains("enteredAtMs", html, StringComparison.Ordinal);
            Assert.Contains("block: blockId(s)", html, StringComparison.Ordinal);
            Assert.Contains("blockId(s) !== acked[id].block", html, StringComparison.Ordinal);

            // The old age comparison must be GONE, not merely supplemented — leaving it in place
            // would re-expire acknowledgements on the ordinary local tick.
            Assert.DoesNotContain("s.sinceSeconds < acked[id].since", html, StringComparison.Ordinal);
        }

        // -------------------------------------------- pipeline run 1: preferences actually load
        //
        // The loads ran from WireAttentionPanel, which the constructor reaches BEFORE LoadSettings
        // assigns _settings — so every `_settings?.Get... ?? "default"` took the literal default and
        // the rail came up on defaults no matter what was saved. The saves worked, so it looked fine
        // until a restart. These pin the split that fixed it.

        /// <summary>
        /// Loading is separate from wiring, and happens where <c>_settings</c> is known to exist.
        /// </summary>
        [Fact]
        public void Preferences_are_pushed_from_a_loader_that_runs_after_settings_exist()
        {
            string src = ReadRepoFile("MainForm.cs");

            Assert.Contains("private void ApplyAttentionPanelSettings()", src, StringComparison.Ordinal);

            // The guard is what makes the construction-path call a no-op instead of a lie.
            Assert.Contains("if (_attentionPanel == null || _settings == null) return;", src, StringComparison.Ordinal);

            // The reads must be unconditional now: a surviving `_settings?.GetAttentionPanel...`
            // would mean a null-settings path still silently yields the default.
            Assert.DoesNotContain("_settings?.GetAttentionPanelAmbient()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("_settings?.GetAttentionPanelAlarm()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("_settings?.GetAttentionPanelOrder()", src, StringComparison.Ordinal);
        }

        /// <summary>
        /// LoadSettings must call the loader. Without this the split is real but nothing on the
        /// startup path ever pushes the stored values — the original bug, rearranged.
        /// </summary>
        [Fact]
        public void LoadSettings_pushes_the_attention_preferences()
        {
            string src = ReadRepoFile("MainForm.cs");

            int loadSettings = src.IndexOf("private void LoadSettings()", StringComparison.Ordinal);
            Assert.True(loadSettings >= 0, "LoadSettings not found");

            int nextMethod = src.IndexOf("private bool IsVisibleOnAnyScreen", loadSettings, StringComparison.Ordinal);
            Assert.True(nextMethod > loadSettings, "could not bound LoadSettings");

            Assert.Contains(
                "ApplyAttentionPanelSettings();",
                src.Substring(loadSettings, nextMethod - loadSettings),
                StringComparison.Ordinal);
        }

        // ------------------------------------------ pipeline run 1: the focus contract has a caller
        //
        // SetFocusedSession documented itself as the correction that keeps the highlight honest when
        // focus moves via a terminal TAB. Its only caller echoed back the id the view had just set
        // itself, so the highlight was click history wearing the label of focus.

        /// <summary>The documented tab-click correction now has the caller it always described.</summary>
        [Fact]
        public void Focus_follows_terminal_tab_clicks()
        {
            string src = ReadRepoFile("MainForm.cs");

            Assert.Contains("private void SyncAttentionPanelFocus(TerminalDocument activeDoc)", src, StringComparison.Ordinal);

            int handler = src.IndexOf("private void OnActiveDocumentChanged", StringComparison.Ordinal);
            Assert.True(handler >= 0, "OnActiveDocumentChanged not found");

            int end = src.IndexOf("OnActiveDocumentChanged error", handler, StringComparison.Ordinal);
            Assert.True(end > handler, "could not bound OnActiveDocumentChanged");

            Assert.Contains(
                "SyncAttentionPanelFocus(activeDoc);",
                src.Substring(handler, end - handler),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// A card whose terminal is gone must not keep the optimistic highlight. Focusing nothing
        /// and confirming focus are different outcomes and have to stay that way.
        /// </summary>
        [Fact]
        public void A_failed_focus_clears_the_highlight_instead_of_confirming_it()
        {
            string src = ReadRepoFile("MainForm.cs");

            Assert.Contains("private bool FocusTerminalForSession(string sessionKey)", src, StringComparison.Ordinal);
            Assert.Contains(
                "if (FocusTerminalForSession(sessionId)) _attentionPanel?.SetFocusedSession(sessionId);",
                src,
                StringComparison.Ordinal);
        }
    }
}
