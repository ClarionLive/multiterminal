using System;
using System.Collections.Generic;
using System.Threading;
using MultiTerminal.Dialogs;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket ae3d0193: the Launch Claude picker ("Just Claude") opens on "Unassigned", and launching with it
    /// takes no team identity. The dialog facts construct the real form (never shown) and read the list it
    /// built, so they pin the wiring, not just the pure entry builder.
    /// </summary>
    public class JustClaudeUnassignedTests
    {
        private static readonly List<string> Named = new() { "Grace", "Charlie", "Alice", "Bob", "Diana" };

        // ---- Entry builder (pure) ----

        [Fact]
        public void Entries_put_Unassigned_first_then_the_identities_in_order()
        {
            var entries = IdentityPickerDialog.BuildSelectionEntries(Named);

            Assert.Equal(new[] { "Unassigned", "Grace", "Charlie", "Alice", "Bob", "Diana" }, entries);
        }

        [Fact]
        public void Entries_hold_the_placeholder_exactly_once_whatever_discovery_returned()
        {
            // Identity discovery reads past sessions, so it can surface the placeholder in any casing.
            var entries = IdentityPickerDialog.BuildSelectionEntries(new[] { "Grace", "unassigned", " UNASSIGNED ", "Bob" });

            Assert.Equal(new[] { "Unassigned", "Grace", "Bob" }, entries);
        }

        [Fact]
        public void Entries_with_no_identities_still_offer_Unassigned()
        {
            Assert.Equal(new[] { "Unassigned" }, IdentityPickerDialog.BuildSelectionEntries(null));
        }

        // ---- The real dialog ----

        [Fact]
        public void Launch_Claude_dialog_lists_Unassigned_first_and_selects_it()
        {
            var (entries, selected) = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog(null, new List<string>(Named), TerminalTheme.Dark,
                    isSelectionMode: true, defaultFolder: Environment.CurrentDirectory, recentFolders: new List<string>());
                return (dialog.IdentityEntries, dialog.SelectedIdentityIndex);
            });

            Assert.Equal("Unassigned", entries[0]);
            Assert.Equal(0, selected);
            // Named agents are still offered, after it.
            Assert.Equal(Named, entries.Skip(1));
        }

        [Fact]
        public void Conflict_dialog_does_not_offer_Unassigned()
        {
            // The conflict picker swaps an in-use team lead for an alternative NAME; the placeholder is not one.
            var entries = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog("Grace", new List<string>(Named), TerminalTheme.Light);
                return dialog.IdentityEntries;
            });

            Assert.DoesNotContain(entries, e => e.StartsWith("Unassigned", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Grace  (active)", entries[0]);
        }

        // ---- Launch identity ----

        [Fact]
        public void Launching_with_Unassigned_takes_no_team_identity()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned");

            Assert.Equal("Unassigned", identity.TerminalName);
            Assert.False(identity.IsTeamLead);
            // The placeholder route: registered like Open PowerShell, never through the pool-name fallback.
            Assert.True(identity.IsPlaceholder);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unassigned")]
        public void Blank_or_any_casing_of_the_placeholder_is_the_placeholder(string selected)
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection(selected);

            Assert.Equal("Unassigned", identity.TerminalName);
            Assert.False(identity.IsTeamLead);
            Assert.True(identity.IsPlaceholder);
        }

        [Fact]
        public void Launching_with_a_named_agent_is_unchanged()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Grace");

            Assert.Equal("Grace", identity.TerminalName);
            Assert.True(identity.IsTeamLead);
            Assert.False(identity.IsPlaceholder);
        }

        [Fact]
        public void The_dialogs_default_choice_launches_as_the_placeholder()
        {
            // End to end through the pure halves: whatever the dialog selects by default is what the launch
            // turns into no team identity.
            var entries = IdentityPickerDialog.BuildSelectionEntries(Named);
            var identity = JustClaudeLaunchIdentity.FromPickerSelection(entries[0]);

            Assert.True(identity.IsPlaceholder);
            Assert.False(identity.IsTeamLead);
        }

        private static T OnSta<T>(Func<T> work)
        {
            T result = default;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "dialog construction did not finish");
            if (failure != null) throw new InvalidOperationException("dialog construction failed", failure);
            return result;
        }
    }
}
