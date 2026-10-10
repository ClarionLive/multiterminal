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
            Assert.False(none.HasWarning);

            var explicitDefault = ClaudeProfileResolver.Resolve("default", null, ctx);
            Assert.True(explicitDefault.Profile.IsDefault);
            Assert.False(explicitDefault.HasWarning);
        }

        [Fact]
        public void Unknown_name_falls_back_to_default_with_a_warning()
        {
            var r = ClaudeProfileResolver.Resolve("ClientX", null, Ctx(configured: Work));
            Assert.True(r.Profile.IsDefault);
            Assert.Equal("ClientX", r.RequestedName);
            Assert.True(r.HasWarning);
            Assert.Contains("ClientX", r.Warning);
        }

        [Fact]
        public void Known_profile_with_missing_folder_is_kept_with_a_warning()
        {
            var r = ClaudeProfileResolver.Resolve("Work", null, Ctx(exists: _ => false, configured: Work));
            Assert.Equal("Work", r.Profile.Name);
            Assert.True(r.HasWarning);
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
            var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WorkDir, @"D:\a" };
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
            var flagged = ClaudeProfileResolver.Validate(bad, ctx).Select(e => e.Index).ToHashSet();

            Assert.Equal(new HashSet<int> { 0, 1, 3, 4, 5, 6, 7, 8, 9 }, flagged);
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
    }
}
