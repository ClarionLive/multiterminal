using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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

        private static AgentAttentionEntry Blocked(string agent, DateTime enteredAtUtc, long blockSeq = 0) => new AgentAttentionEntry
        {
            SessionId = "sess-" + agent,
            AgentName = agent,
            State = AttentionState.BlockedPermission,
            EnteredAtUtc = enteredAtUtc,
            BlockSeq = blockSeq,
        };

        /// <summary>
        /// The exact defect: two DIFFERENT blocks that no clock can separate must still be
        /// distinguishable. Their ages round to the same whole second AND land in the same
        /// millisecond — the two things the first and second attempts at this identity each
        /// relied on. Only a counter survives both.
        /// </summary>
        [Fact]
        public void Two_blocks_the_clock_cannot_separate_are_still_distinguishable()
        {
            var instant = new DateTime(2026, 9, 3, 12, 0, 0, 120, DateTimeKind.Utc);
            var now = instant.AddMilliseconds(50);

            // Identical timestamps, to the millisecond. Any clock-derived identity collides here.
            var a = AttentionCardProjector.Project(new[] { Blocked("Alice", instant, blockSeq: 4) }, null, null, now).Single();
            var b = AttentionCardProjector.Project(new[] { Blocked("Alice", instant, blockSeq: 5) }, null, null, now).Single();

            // The age the view displays cannot tell these apart — that is the trap, asserted so the
            // reason for carrying a separate identity stays visible.
            Assert.Equal(a.SinceSeconds, b.SinceSeconds);
            Assert.Equal(0, a.SinceSeconds);

            Assert.NotEqual(a.BlockSeq, b.BlockSeq);
        }

        /// <summary>The identity is stable across pushes WITHIN one block, or the ack would drop instantly.</summary>
        [Fact]
        public void Block_identity_is_stable_while_the_block_lasts()
        {
            var entered = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var entry = Blocked("Alice", entered, blockSeq: 3);

            var early = AttentionCardProjector.Project(new[] { entry }, null, null, entered.AddSeconds(1)).Single();
            var later = AttentionCardProjector.Project(new[] { entry }, null, null, entered.AddMinutes(5)).Single();

            Assert.Equal(early.BlockSeq, later.BlockSeq);
            Assert.NotEqual(early.SinceSeconds, later.SinceSeconds);
        }

        /// <summary>
        /// A second blocking notification arriving while the card ALREADY reads blocked must mint a
        /// new identity. This is the hole the first version of the identity fix left open, found by
        /// the run-2 debugger: the producer restamped only on a state CHANGE, so block -> block of
        /// the same kind reused one timestamp and an acknowledgement of the first prompt silenced
        /// the second.
        /// </summary>
        /// <remarks>
        /// Reachable on the ordinary cadence rather than in a corner: the SET edge is synchronous
        /// while the CLEAR edge is polled, so two prompts closer together than one poll tick produce
        /// exactly this block -> block sequence with no intervening Working ever rendered.
        /// <para>
        /// There is no tool_use_id to distinguish the two — Claude Code does not supply one on a
        /// Notification payload, confirmed from the live presence-only diagnostic rather than
        /// assumed. So every blocking notification counts as a new block.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_second_prompt_in_the_same_state_starts_a_new_block()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "s1",
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = "permission_prompt",
                ["message"] = "first prompt",
            }));

            DateTime firstAge = svc.Get("s1").EnteredAtUtc;
            long firstSeq = svc.Get("s1").BlockSeq;
            Assert.Equal(AttentionState.BlockedPermission, svc.Get("s1").State);

            Assert.True(svc.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "s1",
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = "permission_prompt",
                ["message"] = "second prompt",
            }));

            var after = svc.Get("s1");

            // The state never changed, which is precisely why a state diff could not see this.
            Assert.Equal(AttentionState.BlockedPermission, after.State);

            // The identity moved...
            Assert.NotEqual(firstSeq, after.BlockSeq);

            // ...and the age did NOT. Both halves matter: this is the same input that
            // AgentAttentionServiceTests.Rewriting_the_detail_does_not_restart_the_age_clock
            // asserts must preserve the age, so keying identity to the clock cannot satisfy both.
            // Asserting them together is what stops a future fix from "solving" one by breaking
            // the other, which is exactly what the first attempt at this did.
            Assert.Equal(firstAge, after.EnteredAtUtc);

            // And it must survive projection, since the wire field is what the view compares.
            var b = AttentionCardProjector.Project(new[] { after }, null, null, DateTime.UtcNow).Single();
            Assert.Equal(after.BlockSeq, b.BlockSeq);
            Assert.NotEqual(firstSeq, b.BlockSeq);
        }

        /// <summary>
        /// The same thing again with a BYTE-IDENTICAL payload, which is the case that actually
        /// happens and the one the first version of this test missed.
        /// </summary>
        /// <remarks>
        /// Two permission prompts for the same tool carry the same message, both carry a null
        /// tool_use_id, and while the polled clear edge is still outstanding they carry the same
        /// state and activity line too. Every field <c>UpsertLocked</c>'s change-diff inspects
        /// compares equal, so the whole call used to early-return before the counter was touched.
        /// The previous test varied the message and so reached the increment through the Detail
        /// diff — it passed against code that was still broken for the real case, which is the
        /// entire reason this one exists alongside it (task 42052f0c, pipeline run 3).
        /// </remarks>
        [Fact]
        public void An_identical_repeat_prompt_still_starts_a_new_block()
        {
            var svc = new AgentAttentionService();

            // ONE payload object, sent twice. Nothing varies — not even by a character.
            var payload = new Dictionary<string, object>
            {
                ["session_id"] = "s1",
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = "permission_prompt",
                ["message"] = "Allow Bash(rm -rf build)?",
            };

            Assert.True(svc.ApplyNotification(payload));
            long first = svc.Get("s1").BlockSeq;
            DateTime firstAge = svc.Get("s1").EnteredAtUtc;

            // Must report a change: a new block IS news, even when every displayed field matches.
            Assert.True(svc.ApplyNotification(payload));

            var after = svc.Get("s1");
            Assert.NotEqual(first, after.BlockSeq);

            // The age still must not move — same pairing as the test above, so a future fix cannot
            // buy the identity back by spending the ordering signal.
            Assert.Equal(firstAge, after.EnteredAtUtc);
        }

        /// <summary>
        /// The counterweight: a NON-blocking update must still not restart the clock, or the age
        /// that tells the owner which agent has been stuck longest resets on every detail rewrite.
        /// </summary>
        [Fact]
        public void A_detail_rewrite_does_not_restart_the_clock()
        {
            var svc = new AgentAttentionService();

            svc.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "s1",
                ["agent_name"] = "Alice",
                ["notification_type"] = "idle_prompt",
                ["message"] = "first",
            });

            var entered = svc.Get("s1").EnteredAtUtc;
            var state = svc.Get("s1").State;
            Assert.False(AgentAttentionServiceIsBlocking(state));

            svc.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "s1",
                ["agent_name"] = "Alice",
                ["notification_type"] = "idle_prompt",
                ["message"] = "second",
            });

            Assert.Equal(entered, svc.Get("s1").EnteredAtUtc);
        }

        private static bool AgentAttentionServiceIsBlocking(AttentionState s) =>
            s == AttentionState.BlockedQuestion ||
            s == AttentionState.BlockedPermission ||
            s == AttentionState.BlockedUnknown;

        /// <summary>
        /// A session that has never blocked carries identity 0, which the view treats as "no
        /// identity" and falls back to state-change expiry — the pre-existing behaviour.
        /// </summary>
        [Fact]
        public void A_session_that_never_blocked_has_identity_zero()
        {
            var card = AttentionCardProjector.Project(
                new[] { new AgentAttentionEntry { SessionId = "s", AgentName = "Alice", State = AttentionState.Working } },
                null, null, DateTime.UtcNow).Single();

            Assert.Equal(0, card.BlockSeq);
        }

        /// <summary>
        /// The C#-to-JavaScript half of the identity contract, which no compiler checks. The field
        /// is useless if the view still keys acknowledgement off the age.
        /// </summary>
        [Fact]
        public void The_view_keys_acknowledgement_off_the_block_identity()
        {
            string html = ReadRepoFile("AttentionPanel", "attention-panel.html");

            // Read the wire name off the C# attribute rather than hardcoding it, so renaming the
            // JsonPropertyName ALONE fails here. Asserting a literal on the view side only would
            // pin half a cross-language contract and quietly bless the other half — the exact
            // shape of defect this file exists to catch.
            string wireName = typeof(AttentionCard)
                .GetProperty(nameof(AttentionCard.BlockSeq))
                .GetCustomAttributes(typeof(JsonPropertyNameAttribute), false)
                .Cast<JsonPropertyNameAttribute>()
                .Single()
                .Name;

            Assert.Equal("blockSeq", wireName);
            Assert.Contains(wireName, html, StringComparison.Ordinal);
            Assert.Contains("block: blockId(s)", html, StringComparison.Ordinal);
            Assert.Contains("blockId(s) !== acked[id].block", html, StringComparison.Ordinal);

            // The old age comparison must be GONE, not merely supplemented — leaving it in place
            // would re-expire acknowledgements on the ordinary local tick.
            Assert.DoesNotContain("s.sinceSeconds < acked[id].since", html, StringComparison.Ordinal);
            Assert.DoesNotContain("s.enteredAtMs", html, StringComparison.Ordinal);
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

            // The else is the half that matters and the half a "confirm on success" refactor is
            // most likely to drop: without it a failed focus leaves the view's optimistic mark
            // standing, which is the card claiming focus landed somewhere it did not.
            Assert.Contains("else _attentionPanel?.SetFocusedSession(null);", src, StringComparison.Ordinal);
        }
    }
}
