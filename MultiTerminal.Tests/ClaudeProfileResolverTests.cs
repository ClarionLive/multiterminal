using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec (GitHub #21): Claude Code config profiles. Settings, MT's inherited
    /// CLAUDE_CONFIG_DIR, %USERPROFILE% and the directory probe are all injected; nothing reads the real
    /// settings.txt, environment or profile folders.
    /// </summary>
    public sealed class ClaudeProfileResolverTests : IDisposable
    {
        private const string Home = @"C:\Users\Dev";
        private const string HomeClaude = Home + @"\.claude";
        private const string WorkDir = Home + @"\.claude-work";
        private const string RemoveLine = "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; ";
        private const string TeamsLine = "$env:CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = '1'; ";

        private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), $"mt_claudeprofile_test_{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(_settingsDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static ClaudeProfileContext Ctx(string inherited = null, Func<string, bool> exists = null, params ClaudeProfile[] configured) =>
            new ClaudeProfileContext(configured, inherited, Home, exists ?? (_ => true));

        private static ClaudeProfile Work => new ClaudeProfile("Work", WorkDir);

        // ── Prologue: the Default trap ───────────────────────────────────────

        [Fact]
        public void Default_with_nothing_inherited_removes_the_variable_and_never_sets_it()
        {
            var ctx = Ctx(configured: Work);
            var prologue = ClaudeProfileResolver.GetPrologue(ClaudeProfileResolver.GetDefaultProfile(ctx), ctx);

            Assert.Equal(RemoveLine + TeamsLine, prologue);
            Assert.DoesNotContain("$env:CLAUDE_CONFIG_DIR =", prologue);

            // A null profile means Default too.
            Assert.Equal(RemoveLine + TeamsLine, ClaudeProfileResolver.GetPrologue(null, ctx));

            var env = ClaudeProfileResolver.GetEnvironment(null, ctx);
            Assert.Contains(env, kv => kv.Key == "CLAUDE_CONFIG_DIR" && kv.Value == null);
        }

        [Fact]
        public void Non_default_profile_sets_the_variable()
        {
            var ctx = Ctx(configured: Work);
            Assert.Equal($"$env:CLAUDE_CONFIG_DIR = '{WorkDir}'; " + TeamsLine, ClaudeProfileResolver.GetPrologue(Work, ctx));
            Assert.Contains(ClaudeProfileResolver.GetEnvironment(Work, ctx), kv => kv.Key == "CLAUDE_CONFIG_DIR" && kv.Value == WorkDir);
        }

        [Fact]
        public void Agent_teams_line_is_always_present()
        {
            foreach (var inherited in new[] { null, @"D:\cfg" })
            {
                var ctx = Ctx(inherited, configured: Work);
                Assert.EndsWith(TeamsLine, ClaudeProfileResolver.GetPrologue(null, ctx));
                Assert.EndsWith(TeamsLine, ClaudeProfileResolver.GetPrologue(Work, ctx));
                Assert.Contains(ClaudeProfileResolver.GetEnvironment(Work, ctx), kv => kv.Key == "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS" && kv.Value == "1");
            }
        }

        [Fact]
        public void Inherited_config_dir_is_the_default_and_is_emitted_explicitly()
        {
            var ctx = Ctx(inherited: @"D:\Claude\cfg\");
            var def = ClaudeProfileResolver.GetDefaultProfile(ctx);

            Assert.Equal(@"D:\Claude\cfg", def.ConfigDir);
            Assert.Equal(@"D:\Claude\cfg", ClaudeProfileResolver.GetConfigDir(def, ctx));
            Assert.Equal("$env:CLAUDE_CONFIG_DIR = 'D:\\Claude\\cfg'; " + TeamsLine, ClaudeProfileResolver.GetPrologue(def, ctx));
        }

        [Fact]
        public void Default_dir_without_inheritance_is_home_dot_claude()
        {
            Assert.Equal(HomeClaude, ClaudeProfileResolver.GetConfigDir(null, Ctx()));
        }

        [Fact]
        public void A_named_profile_pointing_at_home_dot_claude_still_never_sets_it()
        {
            // Validation rejects this, but the env decision must hold even for a hand-built profile.
            var ctx = Ctx();
            Assert.Equal(RemoveLine + TeamsLine, ClaudeProfileResolver.GetPrologue(new ClaudeProfile("Personal", HomeClaude + @"\"), ctx));
        }

        [Fact]
        public void Single_quote_in_dir_is_doubled()
        {
            var p = new ClaudeProfile("Odd", @"D:\O'Brien\.claude");
            Assert.StartsWith("$env:CLAUDE_CONFIG_DIR = 'D:\\O''Brien\\.claude'; ", ClaudeProfileResolver.GetPrologue(p, Ctx(configured: p)));
        }

        [Theory]
        [InlineData('\u2018')]
        [InlineData('\u2019')]
        [InlineData('\u201A')]
        [InlineData('\u201B')]
        public void A_typographic_single_quote_in_dir_is_doubled_too(char quote)
        {
            // PowerShell accepts each of these as a single-quote character, so an undoubled one ends the
            // string. Proves the escaping only: no PowerShell is run here.
            var p = new ClaudeProfile("Odd", $@"D:\O{quote}Brien\.claude");

            string prologue = ClaudeProfileResolver.GetPrologue(p, Ctx(configured: p));

            Assert.StartsWith($"$env:CLAUDE_CONFIG_DIR = 'D:\\O{quote}{quote}Brien\\.claude'; ", prologue);
        }

        [Theory]
        [InlineData('\u2018')]
        [InlineData('\u2019')]
        [InlineData('\u201A')]
        [InlineData('\u201B')]
        public void Validate_rejects_a_typographic_single_quote_in_the_folder(char quote)
        {
            string dir = $@"D:\O{quote}Brien";
            var errors = ClaudeProfileResolver.Validate(new[] { new ClaudeProfile("Odd", dir) }, Ctx(exists: _ => true));

            var error = Assert.Single(errors);
            Assert.Equal(0, error.Index);
            Assert.Contains("curly quote", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_still_accepts_an_ASCII_apostrophe_in_the_folder()
        {
            Assert.Empty(ClaudeProfileResolver.Validate(new[] { new ClaudeProfile("Odd", @"D:\O'Brien") }, Ctx(exists: _ => true)));
        }

        // ── The one Default-name rule ────────────────────────────────────────

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("Default", null)]
        [InlineData("  dEFAULT ", null)]
        [InlineData("Work", "Work")]
        [InlineData("  Work  ", "Work")]
        [InlineData("Defaults", "Defaults")]
        public void NormalizeName_is_null_for_blank_or_Default_and_trimmed_otherwise(string input, string expected)
        {
            Assert.Equal(expected, ClaudeProfile.NormalizeName(input));
        }

        [Fact]
        public void IsDefault_follows_the_name_rule_but_a_blank_name_is_not_Default()
        {
            Assert.True(new ClaudeProfile(" default ", null).IsDefault);
            Assert.False(new ClaudeProfile("Work", null).IsDefault);

            // A profile with no name is malformed, not Default: treating it as Default would make
            // GetConfigDir ignore its folder.
            Assert.False(new ClaudeProfile("  ", @"D:\x").IsDefault);
            Assert.False(new ClaudeProfile(null, @"D:\x").IsDefault);
        }

        // ── Resolve ──────────────────────────────────────────────────────────

        [Fact]
        public void Precedence_is_override_then_project_then_default()
        {
            var personal = new ClaudeProfile("Personal", @"D:\personal");
            var ctx = Ctx(configured: new[] { Work, personal });

            Assert.Equal("Personal", ClaudeProfileResolver.Resolve("work", "PERSONAL", ctx).Profile.Name);
            Assert.Equal("Work", ClaudeProfileResolver.Resolve("work", "  ", ctx).Profile.Name);
            Assert.Equal("Work", ClaudeProfileResolver.Resolve("Work", null, ctx).Profile.Name);

            var none = ClaudeProfileResolver.Resolve(null, null, ctx);
            Assert.True(none.Profile.IsDefault);
            Assert.True(none.CanLaunch);

            var explicitDefault = ClaudeProfileResolver.Resolve("default", null, ctx);
            Assert.True(explicitDefault.Profile.IsDefault);
            Assert.True(explicitDefault.CanLaunch);

            // An explicit Default override selects Default; it does not fall through to the project's.
            var overrideDefault = ClaudeProfileResolver.Resolve("Work", "Default", ctx);
            Assert.True(overrideDefault.Profile.IsDefault);
            Assert.True(overrideDefault.CanLaunch);
        }

        // Owner decision 2026-10-10: these two used to launch (on Default, and on the missing folder)
        // with a warning. They now refuse, and hand back no profile to launch on by mistake.

        [Fact]
        public void Unknown_name_is_blocked_and_returns_no_profile()
        {
            var r = ClaudeProfileResolver.Resolve("ClientX", null, Ctx(configured: Work));

            Assert.False(r.CanLaunch);
            Assert.Null(r.Profile);
            Assert.Equal("ClientX", r.RequestedName);
            Assert.Contains("'ClientX' is not configured on this machine", r.BlockedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void Known_profile_with_missing_folder_is_blocked_and_returns_no_profile()
        {
            var r = ClaudeProfileResolver.Resolve("Work", null, Ctx(exists: _ => false, configured: Work));

            Assert.False(r.CanLaunch);
            Assert.Null(r.Profile);
            Assert.Contains(WorkDir, r.BlockedReason, StringComparison.Ordinal);
            Assert.Contains("does not exist", r.BlockedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void Known_profile_with_an_existing_folder_launches()
        {
            var r = ClaudeProfileResolver.Resolve("work", null, Ctx(configured: Work));

            Assert.True(r.CanLaunch);
            Assert.Null(r.BlockedReason);
            Assert.Equal("Work", r.Profile.Name);
            Assert.Equal(WorkDir, r.Profile.ConfigDir);
        }

        // ── Roots ────────────────────────────────────────────────────────────

        [Fact]
        public void GetAllRoots_canonicalises_dedupes_and_drops_dangerous_roots()
        {
            var ctx = Ctx(configured: new[]
            {
                new ClaudeProfile("Other", Home + @"\.claude-other"),
                new ClaudeProfile("OtherX", Home + @"\.claude-otherX\"),
                new ClaudeProfile("Dup", Home + @"\sub\..\.CLAUDE-OTHER\"),
                new ClaudeProfile("Drive", @"D:\"),
                new ClaudeProfile("HomeItself", Home + @"\"),
                new ClaudeProfile("Users", @"C:\Users"),
                new ClaudeProfile("Relative", @"relative\dir"),
            });

            var roots = ClaudeProfileResolver.GetAllRoots(ctx);

            Assert.Equal(new[] { HomeClaude + @"\", Home + @"\.claude-other\", Home + @"\.claude-otherX\" }, roots);
            Assert.All(roots, r => Assert.EndsWith(@"\", r));
            Assert.False((Home + @"\.claude-otherX\file.jsonl").StartsWith(roots[1], StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GetAllRoots_uses_the_inherited_default()
        {
            Assert.Equal(new[] { @"D:\cfg\" }, ClaudeProfileResolver.GetAllRoots(Ctx(inherited: @"D:\cfg")));
        }

        // ── Validation ───────────────────────────────────────────────────────

        [Fact]
        public void Validate_reports_each_rule()
        {
            // Every folder that is not itself the subject of a folder rule EXISTS, so a row is flagged
            // by the one rule its comment names and by nothing else. (Rows 1 and 3 used to point at
            // folders that did not exist, so they were flagged with or without their name rule.)
            var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WorkDir, @"D:\a", @"D:\b", @"D:\c" };
            var ctx = Ctx(exists: exists.Contains);

            Assert.Empty(ClaudeProfileResolver.Validate(new[] { Work, new ClaudeProfile("A", @"D:\a") }, ctx));

            ClaudeProfile[] bad =
            {
                new ClaudeProfile(" ", @"D:\a"),            // 0 blank name
                new ClaudeProfile("DEFAULT", @"D:\b"),      // 1 reserved
                new ClaudeProfile("Work", WorkDir),         // 2 ok
                new ClaudeProfile("work", @"D:\c"),         // 3 duplicate name
                new ClaudeProfile("Rel", @"rel\dir"),       // 4 not absolute
                new ClaudeProfile("Root", @"E:\"),          // 5 drive root
                new ClaudeProfile("Home", Home),            // 6 user profile
                new ClaudeProfile("HomeClaude", HomeClaude),// 7 default folder
                new ClaudeProfile("Missing", @"D:\nope"),   // 8 does not exist
                new ClaudeProfile("Shared", WorkDir + @"\"),// 9 same folder as Work
            };
            var expected = new Dictionary<int, string>
            {
                [0] = "a name is required",
                [1] = "is reserved for the built-in profile",
                [3] = "more than one profile named 'work'",
                [4] = "must be an absolute path",
                [5] = "cannot be a drive root",
                [6] = "cannot be your user folder",
                [7] = "is the Default profile's folder",
                [8] = "does not exist",
                [9] = "is already used by profile 'Work'",
            };

            var errors = ClaudeProfileResolver.Validate(bad, ctx);

            // One error per flagged row, carrying that row's rule; row 2 is clean.
            Assert.Equal(expected.Keys.OrderBy(i => i), errors.Select(e => e.Index).OrderBy(i => i));
            foreach (var error in errors)
                Assert.Contains(expected[error.Index], error.Message, StringComparison.Ordinal);
        }

        // ── Settings round-trip ──────────────────────────────────────────────

        [Fact]
        public void Settings_round_trip_through_settings_txt()
        {
            var svc = new SettingsService(_settingsDir);
            svc.SetClaudeProfiles(new[] { Work, new ClaudeProfile(" Spaced ", @"D:\it's here") });

            var reread = new SettingsService(_settingsDir).GetClaudeProfiles();
            Assert.Equal(new[] { "Work", "Spaced" }, reread.Select(p => p.Name));
            Assert.Equal(new[] { WorkDir, @"D:\it's here" }, reread.Select(p => p.ConfigDir));

            svc.SetClaudeProfiles(Array.Empty<ClaudeProfile>());
            Assert.Empty(new SettingsService(_settingsDir).GetClaudeProfiles());
        }

        [Fact]
        public void Settings_store_the_canonical_folder()
        {
            // What Validate judged is what gets stored: no trailing separator, no "..", no quotes.
            string json = SettingsService.SerializeClaudeProfiles(new[]
            {
                new ClaudeProfile("A", @"  ""D:\cfg\sub\..\a\""  "),
            });

            var stored = Assert.Single(SettingsService.ParseClaudeProfiles(json));
            Assert.Equal(@"D:\cfg\a", stored.ConfigDir);
        }

        [Fact]
        public void A_value_stored_before_canonical_storage_still_loads_as_written()
        {
            // Written by the earlier build: trimmed, but with its trailing separator.
            var stored = Assert.Single(SettingsService.ParseClaudeProfiles("[{\"name\":\"A\",\"configDir\":\"D:\\\\cfg\\\\a\\\\\"}]"));

            Assert.Equal(@"D:\cfg\a\", stored.ConfigDir);
            Assert.Equal(@"D:\cfg\a", ClaudeProfileResolver.GetConfigDir(stored, Ctx(configured: stored)));
        }

        [Fact]
        public void Settings_refuses_unreadable_entries_and_skips_them_on_read()
        {
            var svc = new SettingsService(_settingsDir);
            Assert.Throws<ArgumentException>(() => svc.SetClaudeProfiles(new[] { new ClaudeProfile("Default", @"D:\x") }));
            Assert.Throws<ArgumentException>(() => svc.SetClaudeProfiles(new[] { Work, new ClaudeProfile("WORK", @"D:\y") }));

            var parsed = SettingsService.ParseClaudeProfiles(
                "[{\"name\":\"Default\",\"configDir\":\"D:\\\\x\"},{\"name\":\"A\",\"configDir\":\"rel\"},{\"name\":\"B\",\"configDir\":\"D:\\\\b\"},{\"name\":\"b\",\"configDir\":\"D:\\\\c\"}]");
            Assert.Equal(new[] { "B" }, parsed.Select(p => p.Name));
            Assert.Empty(SettingsService.ParseClaudeProfiles("not json"));
        }

        // ── Phase 3 item 20: has this profile ever run Claude ────────────────

        private static Func<string, bool> Files(params string[] present) =>
            path => present.Contains(path, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void State_file_is_inside_the_config_dir_when_the_variable_is_set_and_in_the_user_folder_when_it_is_not()
        {
            var plain = Ctx(configured: Work);
            var inherited = Ctx(@"D:\cfg", configured: Work);

            // Default with nothing inherited: the variable is removed, so NOT inside .claude.
            Assert.Equal(Home + @"\.claude.json", ClaudeProfileResolver.GetStateFilePath(null, plain));
            Assert.Equal(Home + @"\.claude.json", ClaudeProfileResolver.GetStateFilePath(ClaudeProfileResolver.GetDefaultProfile(plain), plain));

            // Default when MT inherited a config dir, and any named profile: inside that directory.
            Assert.Equal(@"D:\cfg\.claude.json", ClaudeProfileResolver.GetStateFilePath(null, inherited));
            Assert.Equal(WorkDir + @"\.claude.json", ClaudeProfileResolver.GetStateFilePath(Work, plain));
            Assert.Equal(WorkDir + @"\.claude.json", ClaudeProfileResolver.GetStateFilePath(Work, inherited));
        }

        [Fact]
        public void A_profile_is_used_exactly_when_its_own_state_file_exists()
        {
            var ctx = Ctx(configured: Work);
            var homeOnly = Files(Home + @"\.claude.json");
            var workOnly = Files(WorkDir + @"\.claude.json");

            // Each profile is judged by its own file, never by the other's.
            Assert.True(ClaudeProfileResolver.HasBeenUsed(null, ctx, homeOnly));
            Assert.False(ClaudeProfileResolver.HasBeenUsed(Work, ctx, homeOnly));
            Assert.False(ClaudeProfileResolver.HasBeenUsed(null, ctx, workOnly));
            Assert.True(ClaudeProfileResolver.HasBeenUsed(Work, ctx, workOnly));

            // The trap: a .claude.json INSIDE %USERPROFILE%\.claude is not Default's state file.
            Assert.False(ClaudeProfileResolver.HasBeenUsed(null, ctx, Files(HomeClaude + @"\.claude.json")));

            // The older name inside the config directory counts.
            Assert.True(ClaudeProfileResolver.HasBeenUsed(Work, ctx, Files(WorkDir + @"\.config.json")));
            Assert.True(ClaudeProfileResolver.HasBeenUsed(null, ctx, Files(HomeClaude + @"\.config.json")));
        }

        [Fact]
        public void Never_used_is_described_with_the_profile_and_the_missing_file_and_is_null_when_used()
        {
            var ctx = Ctx(configured: Work);

            string work = ClaudeProfileResolver.DescribeNeverUsed(Work, ctx, Files());
            string def = ClaudeProfileResolver.DescribeNeverUsed(null, ctx, Files());

            Assert.Contains("'Work'", work, StringComparison.Ordinal);
            Assert.Contains(WorkDir + @"\.claude.json", work, StringComparison.Ordinal);
            Assert.Contains("'Default'", def, StringComparison.Ordinal);
            Assert.Contains(Home + @"\.claude.json", def, StringComparison.Ordinal);
            Assert.Null(ClaudeProfileResolver.DescribeNeverUsed(Work, ctx, Files(WorkDir + @"\.claude.json")));
        }

        [Fact]
        public void A_profile_whose_state_file_cannot_be_located_is_not_called_never_used()
        {
            // No %USERPROFILE% and nothing inherited: there is no place to look for Default's file.
            var noHome = new ClaudeProfileContext(Array.Empty<ClaudeProfile>(), null, null, _ => true);

            Assert.Null(ClaudeProfileResolver.GetStateFilePath(null, noHome));
            Assert.True(ClaudeProfileResolver.HasBeenUsed(null, noHome, Files()));
        }

        [Fact]
        public void The_used_check_only_asks_whether_files_exist()
        {
            // The probe is the only thing handed in, and it cannot create anything: this pins that the
            // check asks about the state file (and the older name) and nothing else.
            var asked = new List<string>();
            ClaudeProfileResolver.HasBeenUsed(Work, Ctx(configured: Work), path => { asked.Add(path); return false; });

            Assert.Equal(new[] { WorkDir + @"\.claude.json", WorkDir + @"\.config.json" }, asked);
        }
    }
}
