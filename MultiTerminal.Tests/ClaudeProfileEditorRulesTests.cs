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
    /// Ticket c8ee0eec (GitHub #21): the rules behind the Settings dialog's Claude Profiles tab. The
    /// validator runs against an injected context, so nothing here reads the real settings.txt,
    /// environment or disk. The WPF side (bindings, message boxes, the save call) is not covered.
    /// </summary>
    public sealed class ClaudeProfileEditorRulesTests
    {
        private const string Home = @"C:\Users\Dev";
        private const string WorkDir = Home + @"\.claude-work";
        private const string PlayDir = Home + @"\.claude-play";

        private static IReadOnlyList<ClaudeProfileValidationError> Validate(IReadOnlyList<ClaudeProfile> profiles) =>
            ClaudeProfileResolver.Validate(
                profiles,
                new ClaudeProfileContext(Array.Empty<ClaudeProfile>(), null, Home, _ => true));

        private static (string Name, string ConfigDir)[] Rows(params (string Name, string ConfigDir)[] rows) => rows;

        [Fact]
        public void Blank_rows_are_dropped_and_the_rest_are_trimmed()
        {
            var result = ClaudeProfileEditorRules.Evaluate(
                Rows(("", ""), ("  Work  ", "  " + WorkDir + "  "), (null, null), ("   ", "\t")),
                Validate);

            Assert.True(result.IsValid);
            Assert.Empty(result.RowErrors);
            var only = Assert.Single(result.ToSave);
            Assert.Equal("Work", only.Name);
            Assert.Equal(WorkDir, only.ConfigDir);
        }

        [Fact]
        public void A_half_filled_row_is_kept_and_reported_not_dropped()
        {
            var result = ClaudeProfileEditorRules.Evaluate(Rows(("Work", "")), Validate);

            Assert.False(result.IsValid);
            Assert.Single(result.ToSave);
            Assert.True(result.RowErrors.ContainsKey(0));
        }

        /// <summary>
        /// Grid rows 0 and 2 are blank, so the validator sees the bad row (grid row 3) as its index 1.
        /// The error must come back on grid row 3, and on no other row.
        /// </summary>
        [Fact]
        public void Errors_map_back_to_grid_rows_when_blank_rows_are_interleaved()
        {
            var result = ClaudeProfileEditorRules.Evaluate(
                Rows(("", ""), ("Work", WorkDir), ("", ""), ("Play", "not-absolute"), ("", "")),
                Validate);

            Assert.False(result.IsValid);
            Assert.Equal(2, result.ToSave.Count);
            Assert.Equal(new[] { 3 }, result.RowErrors.Keys.ToArray());
            Assert.Contains("absolute", result.RowErrors[3], StringComparison.Ordinal);
            Assert.Equal(result.RowErrors[3], Assert.Single(result.Messages));
        }

        [Fact]
        public void Two_problems_on_one_row_share_that_rows_error_line()
        {
            // "Default" is reserved AND the folder is relative: two validator errors, both index 0,
            // which is grid row 1 here.
            var result = ClaudeProfileEditorRules.Evaluate(Rows(("", ""), ("Default", "relative")), Validate);

            Assert.Equal(2, result.Messages.Count);
            Assert.Equal(new[] { 1 }, result.RowErrors.Keys.ToArray());
            Assert.Equal(result.Messages[0] + Environment.NewLine + result.Messages[1], result.RowErrors[1]);
        }

        [Fact]
        public void A_rename_counts_as_a_removal_but_a_case_change_does_not()
        {
            var stored = new[] { new ClaudeProfile("Work", WorkDir), new ClaudeProfile("Play", PlayDir) };

            // Same folder, new name: the old name is gone.
            var renamed = new[] { new ClaudeProfile("Office", WorkDir), new ClaudeProfile("Play", PlayDir) };
            Assert.Equal(new[] { "Work" }, ClaudeProfileEditorRules.RemovedNames(stored, renamed));

            var recased = new[] { new ClaudeProfile("WORK", WorkDir), new ClaudeProfile("play", PlayDir) };
            Assert.Empty(ClaudeProfileEditorRules.RemovedNames(stored, recased));

            Assert.Equal(new[] { "Work", "Play" }, ClaudeProfileEditorRules.RemovedNames(stored, Array.Empty<ClaudeProfile>()));
            Assert.Empty(ClaudeProfileEditorRules.RemovedNames(Array.Empty<ClaudeProfile>(), renamed));
        }

        [Fact]
        public void Removal_warning_names_every_profile()
        {
            Assert.Contains("'Work'", ClaudeProfileEditorRules.RemovalWarning(new[] { "Work" }), StringComparison.Ordinal);

            string both = ClaudeProfileEditorRules.RemovalWarning(new[] { "Work", "Play" });
            Assert.Contains("'Work'", both, StringComparison.Ordinal);
            Assert.Contains("'Play'", both, StringComparison.Ordinal);
        }

        [Fact]
        public void Unchanged_means_same_names_folders_and_order()
        {
            var stored = new[] { new ClaudeProfile("Work", WorkDir), new ClaudeProfile("Play", PlayDir) };

            Assert.True(ClaudeProfileEditorRules.IsUnchanged(stored, new[] { new ClaudeProfile("Work", WorkDir), new ClaudeProfile("Play", PlayDir) }));
            Assert.True(ClaudeProfileEditorRules.IsUnchanged(Array.Empty<ClaudeProfile>(), Array.Empty<ClaudeProfile>()));

            Assert.False(ClaudeProfileEditorRules.IsUnchanged(stored, new[] { new ClaudeProfile("Play", PlayDir), new ClaudeProfile("Work", WorkDir) }));
            Assert.False(ClaudeProfileEditorRules.IsUnchanged(stored, new[] { new ClaudeProfile("work", WorkDir), new ClaudeProfile("Play", PlayDir) }));
            Assert.False(ClaudeProfileEditorRules.IsUnchanged(stored, new[] { new ClaudeProfile("Work", PlayDir), new ClaudeProfile("Play", PlayDir) }));
            Assert.False(ClaudeProfileEditorRules.IsUnchanged(stored, new[] { new ClaudeProfile("Work", WorkDir) }));
        }

        [Fact]
        public void Default_description_mentions_the_inherited_variable_only_when_there_is_one()
        {
            string plain = ClaudeProfileEditorRules.DescribeDefault(Home + @"\.claude", null);
            Assert.Equal(Home + @"\.claude", plain);

            string inherited = ClaudeProfileEditorRules.DescribeDefault(@"D:\cfg", @"D:\cfg");
            Assert.StartsWith(@"D:\cfg", inherited, StringComparison.Ordinal);
            Assert.Contains("MultiTerminal's own CLAUDE_CONFIG_DIR", inherited, StringComparison.Ordinal);
        }
    }
}
