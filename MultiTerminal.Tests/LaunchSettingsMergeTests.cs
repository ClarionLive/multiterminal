using System;
using System.IO;
using System.Text.Json.Nodes;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The settings file MT hands every terminal via <c>--settings</c>, and the
    /// <c>crossSessionInbound</c> key added to it by ticket 0ff1b520 item 2.
    ///
    /// WHY THIS FILE EXISTS AT ALL: before this ticket, <c>BuildForcedStatuslineFlag</c>'s merge had
    /// NO test coverage of any kind — <c>LaunchCommandBuilderWorkingDirectoryTests</c> states that it
    /// deliberately does not assert flag contents "including the statusline merge". That merge decides
    /// what every docked terminal is launched with, and it is the same code path that re-supplies a
    /// project's local settings after MT drops the LOCAL source. A silent defect there misconfigures
    /// every session at once, which is why the Owner chose to cover the merge rather than only the new
    /// key (2026-09-21).
    ///
    /// The precedence rule these tests protect is not MT's invention. Claude Code states it directly:
    /// a repository may only TIGHTEN cross-session delivery, and a user's own "accept" cannot override
    /// a repo tightening. MT drops the LOCAL source and re-supplies its keys at --settings precedence,
    /// so MT is carrying someone else's safety choice across a precedence boundary. Overwriting it
    /// there would not be MT expressing a preference — it would be MT laundering a tightening through
    /// a level the repository could not reach.
    /// </summary>
    public sealed class LaunchSettingsMergeTests : IDisposable
    {
        private readonly string _projectDir;

        public LaunchSettingsMergeTests()
        {
            _projectDir = Path.Combine(Path.GetTempPath(), "mt_lsm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_projectDir, ".claude"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_projectDir, recursive: true); } catch { /* best-effort */ }
            GC.SuppressFinalize(this);
        }

        private void WriteLocalSettings(string json)
            => File.WriteAllText(Path.Combine(_projectDir, ".claude", "settings.local.json"), json);

        /// <summary>Reads back the settings file the builder actually wrote for this project.</summary>
        private static JsonObject ReadEmittedSettings(string flag)
        {
            // Flag shape: " --settings '<path>'"
            int first = flag.IndexOf('\'');
            int last = flag.LastIndexOf('\'');
            Assert.True(first >= 0 && last > first, $"could not find a quoted path in flag: {flag}");
            string path = flag.Substring(first + 1, last - first - 1);

            Assert.True(File.Exists(path), $"builder reported a settings file that does not exist: {path}");
            var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            Assert.NotNull(parsed);
            return parsed;
        }

        // ------------------------------------------------------------------
        // The new key (item 2)
        // ------------------------------------------------------------------

        [Fact]
        public void Absent_crossSessionInbound_is_set_to_accept()
        {
            var settings = new JsonObject();

            LaunchCommandBuilder.ApplyCrossSessionInbound(settings);

            Assert.Equal("accept", settings["crossSessionInbound"].GetValue<string>());
        }

        [Theory]
        [InlineData("hold")]
        [InlineData("refuse")]
        [InlineData("accept")]
        public void An_existing_crossSessionInbound_is_never_overwritten(string existing)
        {
            // "hold" and "refuse" are the cases that matter: they are TIGHTENINGS. MT re-supplies this
            // file's keys at --settings precedence, so overwriting them here would carry a repository's
            // deliberate restriction across a precedence boundary and drop it on the way. "accept" is
            // included so the assertion is about NOT WRITING, not about the value happening to match.
            var settings = new JsonObject { ["crossSessionInbound"] = existing };

            LaunchCommandBuilder.ApplyCrossSessionInbound(settings);

            Assert.Equal(existing, settings["crossSessionInbound"].GetValue<string>());
        }

        [Fact]
        public void An_unrecognized_existing_value_is_still_left_alone()
        {
            // Claude Code holds messages while an unrecognized value is present, and names the file in
            // a settings warning. That is the CLI's call to make and the project's mistake to fix.
            // MT silently replacing it would repair the symptom and hide the warning's cause.
            var settings = new JsonObject { ["crossSessionInbound"] = "Accept" };

            LaunchCommandBuilder.ApplyCrossSessionInbound(settings);

            Assert.Equal("Accept", settings["crossSessionInbound"].GetValue<string>());
        }

        [Fact]
        public void Null_settings_object_is_tolerated()
        {
            LaunchCommandBuilder.ApplyCrossSessionInbound(null);
        }

        // ------------------------------------------------------------------
        // The merge this key rides in — previously untested entirely
        // ------------------------------------------------------------------

        [Fact]
        public void With_no_local_file_the_emitted_settings_carry_statusline_and_accept()
        {
            var (flag, canDropLocal) = LaunchCommandBuilder.BuildForcedStatuslineFlag(_projectDir);

            if (string.IsNullOrEmpty(flag))
            {
                // The bundled scripts/statusline.js is not beside the test assembly in every run
                // configuration. Skipping silently would make this file vacuous, so assert the ONE
                // thing that must still hold on that path: no local file means nothing was lost.
                Assert.True(canDropLocal);
                return;
            }

            Assert.True(canDropLocal);
            var emitted = ReadEmittedSettings(flag);
            Assert.NotNull(emitted["statusLine"]);
            Assert.Equal("accept", emitted["crossSessionInbound"].GetValue<string>());
        }

        [Fact]
        public void A_projects_local_keys_survive_the_merge_alongside_the_new_key()
        {
            // The reason canDropLocal exists: MT drops the LOCAL source, so anything the project put
            // there must be carried forward. Hooks and permissions are the safety-relevant ones.
            WriteLocalSettings(@"{
                ""permissions"": { ""deny"": [""Bash(rm -rf /)""] },
                ""hooks"": { ""PreToolUse"": [] },
                ""env"": { ""PROJECT_MARKER"": ""kept"" }
            }");

            var (flag, canDropLocal) = LaunchCommandBuilder.BuildForcedStatuslineFlag(_projectDir);
            if (string.IsNullOrEmpty(flag)) return; // statusline script unavailable in this run configuration

            Assert.True(canDropLocal);
            var emitted = ReadEmittedSettings(flag);

            Assert.NotNull(emitted["permissions"]);
            Assert.NotNull(emitted["hooks"]);
            Assert.Equal("kept", emitted["env"]["PROJECT_MARKER"].GetValue<string>());
            Assert.Equal("accept", emitted["crossSessionInbound"].GetValue<string>());
        }

        [Fact]
        public void A_projects_own_hold_survives_into_the_emitted_settings()
        {
            // The end-to-end form of the laundering guard. A repository tightened delivery; MT carries
            // its keys across a precedence boundary; the tightening must arrive intact on the far side.
            WriteLocalSettings(@"{ ""crossSessionInbound"": ""hold"" }");

            var (flag, _) = LaunchCommandBuilder.BuildForcedStatuslineFlag(_projectDir);
            if (string.IsNullOrEmpty(flag)) return; // statusline script unavailable in this run configuration

            var emitted = ReadEmittedSettings(flag);
            Assert.Equal("hold", emitted["crossSessionInbound"].GetValue<string>());
        }

        [Fact]
        public void An_unparseable_local_file_fails_closed()
        {
            // Pre-existing behaviour, untested until now. A local file that cannot be re-supplied must
            // report CanDropLocal=false, or the caller drops LOCAL and silently strips the project's
            // hooks and deny rules while --dangerously-skip-permissions is active.
            WriteLocalSettings("{ this is not valid json");

            var (_, canDropLocal) = LaunchCommandBuilder.BuildForcedStatuslineFlag(_projectDir);

            Assert.False(canDropLocal);
        }

        [Fact]
        public void A_local_file_that_is_not_an_object_fails_closed()
        {
            // Valid JSON, wrong shape. Distinct from the parse failure above because it takes a
            // different branch, and a reader could reasonably assume "valid JSON" is enough.
            WriteLocalSettings("[1, 2, 3]");

            var (_, canDropLocal) = LaunchCommandBuilder.BuildForcedStatuslineFlag(_projectDir);

            Assert.False(canDropLocal);
        }

        [Fact]
        public void Setting_sources_are_repeated_not_comma_joined()
        {
            // Pins the CLI 2.1.168 workaround documented on BuildSettingSourcesFlags: the comma form is
            // mis-parsed and fails with "Invalid setting source: user project". A future tidy-up toward
            // the comma form would look like a simplification and would break every launch.
            string flags = LaunchCommandBuilder.BuildSettingSourcesFlags("user,project");

            Assert.Equal(" --setting-sources user --setting-sources project", flags);
            Assert.DoesNotContain("user,project", flags, StringComparison.Ordinal);
        }
    }
}
