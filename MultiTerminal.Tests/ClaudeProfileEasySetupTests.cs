using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Dialogs;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec, items 37-39 (Owner decision 2026-10-16, "easy is better"): adding a profile takes
    /// only a name, the folder is created on save, each row shows whether it is signed in, and Sign in
    /// opens a console under the profile. Nothing here touches the real disk, settings or environment;
    /// the WPF bindings and the window Sign in opens are not covered.
    /// </summary>
    public sealed class ClaudeProfileEasySetupTests
    {
        private const string Home = @"C:\Users\Dev";

        // ── Folder suggestion ────────────────────────────────────────────────

        [Theory]
        [InlineData("Work", Home + @"\.claude-work")]
        [InlineData("  Work  ", Home + @"\.claude-work")]
        [InlineData("My Client: ACME!", Home + @"\.claude-my-client-acme")]
        [InlineData("--x--", Home + @"\.claude-x")]
        [InlineData("José", Home + @"\.claude-josé")]
        public void SuggestFolder_lowercases_and_hyphenates_the_name(string name, string expected)
        {
            Assert.Equal(expected, ClaudeProfileEditorRules.SuggestFolder(name, Home));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        [InlineData(null)]
        public void SuggestFolder_is_empty_for_a_name_with_no_letter_or_digit(string name)
        {
            Assert.Equal("", ClaudeProfileEditorRules.SuggestFolder(name, Home));
        }

        [Fact]
        public void SuggestFolder_is_empty_when_the_user_folder_is_unknown()
        {
            Assert.Equal("", ClaudeProfileEditorRules.SuggestFolder("Work", null));
        }

        [Fact]
        public void A_suggested_folder_passes_validation_when_missing_folders_are_allowed()
        {
            // The suggestion must be something OK will accept, or typing a name would produce an error.
            string dir = ClaudeProfileEditorRules.SuggestFolder("Work", Home);
            var ctx = new ClaudeProfileContext(Array.Empty<ClaudeProfile>(), null, Home, _ => false);

            Assert.Empty(ClaudeProfileResolver.ValidateAllowingMissingFolders(new[] { new ClaudeProfile("Work", dir) }, ctx));
        }

        // ── Missing folders ──────────────────────────────────────────────────

        [Fact]
        public void Allowing_missing_folders_drops_only_the_existence_rule()
        {
            var ctx = new ClaudeProfileContext(Array.Empty<ClaudeProfile>(), null, Home, _ => false);
            var missing = new[] { new ClaudeProfile("Work", Home + @"\.claude-work") };

            // The plain validator refuses the missing folder (so the relaxed one is really relaxing something)...
            Assert.Contains(ClaudeProfileResolver.Validate(missing, ctx), e => e.Message.Contains("does not exist", StringComparison.Ordinal));
            Assert.Empty(ClaudeProfileResolver.ValidateAllowingMissingFolders(missing, ctx));

            // ...and every other rule still applies.
            var bad = new[]
            {
                new ClaudeProfile("Default", Home + @"\.claude-a"),
                new ClaudeProfile("Rel", @"relative\dir"),
                new ClaudeProfile("Root", @"C:\"),
                new ClaudeProfile("Home", Home),
                new ClaudeProfile("HomeClaude", Home + @"\.claude"),
            };
            var errors = ClaudeProfileResolver.ValidateAllowingMissingFolders(bad, ctx);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, errors.Select(e => e.Index).Distinct().OrderBy(i => i));
        }

        [Fact]
        public void FoldersToCreate_lists_each_missing_folder_once_canonically()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Home + @"\.claude-play" };
            var toSave = new[]
            {
                new ClaudeProfile("Work", Home + @"\.claude-work\"),
                new ClaudeProfile("Play", Home + @"\.claude-play"),
                new ClaudeProfile("Work2", Home + @"\.CLAUDE-WORK"),
            };

            var create = ClaudeProfileEditorRules.FoldersToCreate(toSave, existing.Contains);

            Assert.Equal(new[] { Home + @"\.claude-work" }, create);
        }

        // ── Row behaviour ────────────────────────────────────────────────────

        private static ClaudeProfileRow NewRow() =>
            new ClaudeProfileRow(name => ClaudeProfileEditorRules.SuggestFolder(name, Home));

        [Fact]
        public void Typing_a_name_fills_the_folder_and_keeps_it_in_step()
        {
            var row = NewRow();

            row.Name = "W";
            Assert.Equal(Home + @"\.claude-w", row.ConfigDir);

            row.Name = "Work";
            Assert.Equal(Home + @"\.claude-work", row.ConfigDir);
        }

        [Fact]
        public void A_folder_the_user_chose_is_never_overwritten_by_the_name()
        {
            var row = NewRow();
            row.Name = "Work";
            row.ConfigDir = @"D:\profiles\work";

            row.Name = "Work account";

            Assert.Equal(@"D:\profiles\work", row.ConfigDir);
        }

        [Fact]
        public void A_loaded_row_does_not_follow_its_name()
        {
            // Renaming a stored profile must not silently point it at a different folder.
            var row = new ClaudeProfileRow { Name = "Work", ConfigDir = @"D:\profiles\work" };

            row.Name = "Office";

            Assert.Equal(@"D:\profiles\work", row.ConfigDir);
        }

        [Fact]
        public void RefreshStatus_reports_signed_in_needs_sign_in_or_nothing()
        {
            var row = new ClaudeProfileRow { Name = "Work", ConfigDir = Home + @"\.claude-work" };

            row.RefreshStatus(_ => true);
            Assert.True(row.IsSignedIn);
            Assert.False(row.CanSignIn);
            Assert.Contains("Signed in", row.Status, StringComparison.Ordinal);

            row.RefreshStatus(_ => false);
            Assert.False(row.IsSignedIn);
            Assert.True(row.CanSignIn);
            Assert.Equal("Needs sign-in", row.Status);

            row.ConfigDir = @"relative\dir";
            row.RefreshStatus(_ => throw new InvalidOperationException("must not probe a relative path"));
            Assert.Equal("", row.Status);
            Assert.False(row.CanSignIn);
        }

        // ── Signed-in probe ──────────────────────────────────────────────────

        [Fact]
        public void IsSignedIn_looks_for_the_state_file_inside_the_folder()
        {
            string dir = Home + @"\.claude-work";
            var probed = new List<string>();

            Assert.False(ClaudeProfileResolver.IsSignedIn(dir, p => { probed.Add(p); return false; }));
            Assert.Equal(new[] { dir + @"\.claude.json", dir + @"\.config.json" }, probed);

            Assert.True(ClaudeProfileResolver.IsSignedIn(dir, p => p == dir + @"\.claude.json"));
            Assert.True(ClaudeProfileResolver.IsSignedIn(dir, p => p == dir + @"\.config.json"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"relative\dir")]
        public void IsSignedIn_is_false_for_an_unusable_folder(string dir)
        {
            Assert.False(ClaudeProfileResolver.IsSignedIn(dir, _ => true));
        }

        // ── Sign-in window ───────────────────────────────────────────────────

        [Fact]
        public void SignIn_environment_points_at_the_folder_and_drops_MT_identity()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = @"C:\bin",
                ["CLAUDE_CONFIG_DIR"] = @"C:\elsewhere",
                ["MULTITERMINAL_NAME"] = "Alice",
                ["multiterminal_doc_id"] = "6464f7fd",
            };

            ClaudeProfileSignIn.ApplyEnvironment(env, Home + @"\.claude-work");

            Assert.Equal(Home + @"\.claude-work", env["CLAUDE_CONFIG_DIR"]);
            Assert.Equal(@"C:\bin", env["PATH"]);
            Assert.DoesNotContain(env.Keys, k => k.StartsWith("MULTITERMINAL_", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void SignIn_arguments_run_claude_without_the_user_profile_script_and_escape_the_name()
        {
            var args = ClaudeProfileSignIn.BuildArguments("Bob's");

            // -NoProfile: a PowerShell profile script that sets CLAUDE_CONFIG_DIR would otherwise send the
            // sign-in to another folder.
            Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-NoExit", "-Command" }, args.Take(4));
            string script = args[4];
            Assert.Contains("claude; ", script, StringComparison.Ordinal);
            Assert.Contains("Bob''s", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Bob's", script, StringComparison.Ordinal);
        }
    }
}
