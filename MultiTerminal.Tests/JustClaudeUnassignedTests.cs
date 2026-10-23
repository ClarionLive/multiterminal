using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Dialogs;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket ae3d0193: the Launch Claude picker ("Just Claude") opens on "Unassigned", and launching with it
    /// takes its OWN unique, non-team name ("Claude", "Claude-2"...; Owner decision after Run 1), never the
    /// shared placeholder and never a pool name. The dialog facts construct the real form (never shown) and
    /// read the list it built, so they pin the wiring, not just the pure entry builder.
    ///
    /// <para>What these facts cover: the name rule, the registration ROUTE seam
    /// (<see cref="JustClaudeLaunchIdentity.TryRegister"/>, which <c>MainForm.OnStartScreenJustClaude</c> calls
    /// with <c>TryRegisterProjectIdentity</c> as the unique route and <c>PreRegisterTerminalWithName</c> as the
    /// named route), and that route fed into the REAL broker's <c>RegisterTerminalUnique</c>. What they do NOT
    /// cover: that MainForm passes those two methods in those slots, takes the census, and shows the start
    /// screen on refusal. That handler is a private WinForms method; the Owner's live test checks it.</para>
    /// </summary>
    public sealed class JustClaudeUnassignedTests : IDisposable
    {
        private static readonly List<string> Named = new() { "Grace", "Charlie", "Alice", "Bob", "Diana" };
        private static readonly IReadOnlyCollection<string> NoneReserved = Array.Empty<string>();

        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public JustClaudeUnassignedTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_justclaude_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_justclaude_msg_{stamp}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

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
            var (entries, selected, listBack) = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog(null, new List<string>(Named), TerminalTheme.Dark,
                    isSelectionMode: true, defaultFolder: Environment.CurrentDirectory, recentFolders: new List<string>());
                return (dialog.IdentityEntries, dialog.SelectedIdentityIndex, dialog.IdentityListBackColor);
            });

            Assert.Equal("Unassigned", entries[0]);
            Assert.Equal(0, selected);
            // Named agents are still offered, after it.
            Assert.Equal(Named, entries.Skip(1));
            // The theme reaches the list inside the layout panel (ApplyThemeTo recurses). A ListBox does not
            // take its parent's colour ambiently, so without the recursion this stays SystemColors.Window.
            Assert.Equal(TerminalTheme.Dark.Background.ToArgb(), listBack.ToArgb());
        }

        [Fact]
        public void The_Claude_profile_choice_is_rows_of_the_resizable_layout_between_the_folder_and_the_recents()
        {
            // Release 3.0.0 merge: c8ee0eec wrote this section with fixed Locations; ae3d0193 turned the
            // dialog into a resizable TableLayoutPanel. Controls with fixed Locations added to the form itself
            // would sit at -1 here (not rows of the table) and overlap the growing lists.
            var recent = Path.GetTempPath().TrimEnd('\\');
            var rows = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog(null, new List<string>(Named), TerminalTheme.Dark,
                    isSelectionMode: true, Environment.CurrentDirectory, new List<string> { recent },
                    new[] { "Default", "Work" }, _ => null, _ => "a note");
                return new[] { "identity", "folder", "profile", "profileNote", "recent" }.Select(dialog.SectionRow).ToArray();
            });

            Assert.DoesNotContain(-1, rows);
            Assert.Equal(rows.OrderBy(r => r), rows); // identity < folder < profile < note < recent
            Assert.Equal(rows.Length, rows.Distinct().Count());
        }

        [Fact]
        public void With_one_Claude_profile_there_is_no_profile_choice()
        {
            var row = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog(null, new List<string>(Named), TerminalTheme.Dark,
                    isSelectionMode: true, Environment.CurrentDirectory, new List<string>(),
                    new[] { "Default" }, _ => null, _ => null);
                return dialog.SectionRow("profile");
            });

            Assert.Equal(-1, row);
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
        public void Launching_with_Unassigned_takes_a_unique_non_team_name_not_the_placeholder()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1d2e3f4", NoneReserved);

            Assert.Equal("Claude", identity.TerminalName);
            Assert.False(identity.IsTeamLead);
            Assert.True(identity.RegisterUnique);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unassigned")]
        [InlineData(" UNASSIGNED ")]
        public void Blank_or_any_casing_of_the_placeholder_is_the_default_choice(string selected)
        {
            Assert.True(JustClaudeLaunchIdentity.IsDefaultChoice(selected));
            var identity = JustClaudeLaunchIdentity.FromPickerSelection(selected, "d0c1d2e3f4", NoneReserved);

            Assert.Equal("Claude", identity.TerminalName);
            Assert.False(identity.IsTeamLead);
            Assert.True(identity.RegisterUnique);
        }

        [Fact]
        public void A_reserved_Claude_is_qualified_with_the_pane_id()
        {
            // A team lead or roster agent called Claude, or a project called Claude, reserves the base.
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude", "Grace" };
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1d2e3f4", reserved);

            Assert.Equal("Claude-d0c1", identity.TerminalName);
            Assert.True(identity.RegisterUnique);
        }

        // The generator's whole output: the base or base-<docId4> from Resolve (docId is lower-case hex), plus at
        // most one broker suffix, -2..-999 or a last-resort 4-hex guid.
        [Theory]
        [InlineData("Claude")]
        [InlineData("Claude-2")]
        [InlineData("Claude-17")]
        [InlineData("Claude-999")]
        [InlineData("Claude-1f3c")]
        [InlineData("Claude-02a8")]
        [InlineData("Claude-02a8-2")]
        [InlineData("Claude-02a8-999")]
        [InlineData("Claude-02a8-1f3c")]
        [InlineData("Claude-1000")] // all-digit hex: a docId or guid can start "1000"
        [InlineData("Claude-0002")]
        public void A_name_the_default_launch_generates_counts_as_generated(string name)
        {
            Assert.True(JustClaudeLaunchIdentity.IsGeneratedName(name));
        }

        // Names a person could choose that the generator never produces, including near misses on each part
        // of the pattern: case, the 2..999 range, leading zeros, two numeric suffixes, three segments, padding.
        [Theory]
        [InlineData("Grace")]
        [InlineData("claude")]
        [InlineData("CLAUDE")]
        [InlineData("Claude-DBB9")]
        [InlineData("Claude-0")]
        [InlineData("Claude-1")]
        [InlineData("Claude-02")]
        [InlineData("Claude-10000")]
        [InlineData("Claude-2-3")]
        [InlineData("Claude-02a8-1")]
        [InlineData("Claude-02a8-2-3")]
        [InlineData("Claude-02a")]
        [InlineData("Claude-02a8x")]
        [InlineData("Claude-Reviewer")]
        [InlineData("Claudette")]
        [InlineData("MyClaude")]
        [InlineData(" Claude")]
        [InlineData("Claude ")]
        [InlineData("")]
        [InlineData(null)]
        public void A_name_the_default_launch_never_generates_is_not_generated(string name)
        {
            Assert.False(JustClaudeLaunchIdentity.IsGeneratedName(name));
        }

        [Fact]
        public void Picker_identities_drop_generated_names_and_keep_real_ones_then_the_pool()
        {
            var discovered = new[] { "Claude", "Bob", "Claude-02a8", "Claude-dbb9", "Charlie" };
            var pool = new[] { "Alice", "Bob", "Charlie", "Diana" };

            var identities = JustClaudeLaunchIdentity.BuildPickerIdentities(discovered, pool);

            Assert.Equal(new[] { "Bob", "Charlie", "Alice", "Diana" }, identities);
        }

        [Fact]
        public void A_generated_shape_name_a_project_declares_stays_pickable()
        {
            // A team lead or roster agent genuinely called "Claude" is a real identity, not a throwaway.
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Claude" };

            var identities = JustClaudeLaunchIdentity.BuildPickerIdentities(
                new[] { "Claude", "Claude-02a8" }, new[] { "Alice" }, declared.Contains);

            Assert.Equal(new[] { "Claude", "Alice" }, identities);
        }

        [Fact]
        public void A_past_Claude_session_in_the_folder_does_not_qualify_the_next_default_launch()
        {
            // The live failure (2026-10-04): a "Claude" session in the folder's history was offered by the
            // picker, the census reserved every offered name, and both default launches came out as
            // "Claude-<docId4>". The same picker-list -> census -> name sequence as OnStartScreenJustClaude,
            // with the census's other sources (pool, Oracle, Codex default) left out; none of them is "Claude".
            // MainForm.GetAvailableIdentities calling BuildPickerIdentities is NOT covered here (private WinForms
            // wiring); the Owner's live test covers it.
            var offered = JustClaudeLaunchIdentity.BuildPickerIdentities(
                new[] { "Claude", "Claude-02a8", "Bob" }, new[] { "Alice", "Grace" });
            var census = ProjectLaunchIdentity.TryTakeCensus(
                null,
                () => Array.Empty<(string Id, string Name, string TeamLead)>(),
                _ => Array.Empty<string>(),
                offered);
            Assert.True(census.Succeeded, census.Error);

            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1d2e3f4", census.Names);

            Assert.Equal("Claude", identity.TerminalName);
            Assert.Contains("Bob", census.Names, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_default_choice_without_a_census_is_refused()
        {
            // A null set must never read as "nothing reserved". Two guards hold this (FromPickerSelection's own
            // and ProjectLaunchIdentity.Resolve's); removing either alone leaves this green, by design.
            Assert.Throws<ArgumentNullException>(() => JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1", null));
        }

        [Fact]
        public void Launching_with_a_named_agent_is_unchanged()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Grace", "d0c1d2e3f4", null);

            Assert.Equal("Grace", identity.TerminalName);
            Assert.True(identity.IsTeamLead);
            Assert.False(identity.RegisterUnique);
        }

        [Fact]
        public void The_dialogs_default_choice_launches_under_a_unique_name()
        {
            // Read what the REAL dialog has selected by default (what TryAccept would hand back), not entries[0].
            var selected = OnSta(() =>
            {
                using var dialog = new IdentityPickerDialog(null, new List<string>(Named), TerminalTheme.Dark,
                    isSelectionMode: true, defaultFolder: Environment.CurrentDirectory, recentFolders: new List<string>());
                return dialog.SelectedIdentityEntry;
            });

            Assert.Equal("Unassigned", selected);
            var identity = JustClaudeLaunchIdentity.FromPickerSelection(selected, "d0c1d2e3f4", NoneReserved);
            Assert.True(identity.RegisterUnique);
            Assert.False(identity.IsTeamLead);
            Assert.Equal("Claude", identity.TerminalName);
        }

        // ---- Registration route (the seam OnStartScreenJustClaude calls) ----

        [Fact]
        public void Default_launch_registers_only_through_the_unique_route_and_never_the_pool_fallback()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1d2e3f4", NoneReserved);
            var uniqueCalls = new List<string>();
            int namedCalls = 0;

            bool ok = JustClaudeLaunchIdentity.TryRegister(
                identity,
                (string requested, out string resolved, out string error) =>
                {
                    uniqueCalls.Add(requested);
                    resolved = requested + "-2";
                    error = null;
                    return true;
                },
                named => { namedCalls++; return "Grace"; },
                out string terminalName,
                out string refusal);

            Assert.True(ok);
            Assert.Null(refusal);
            Assert.Equal(new[] { "Claude" }, uniqueCalls);
            Assert.Equal(0, namedCalls);
            // The name the unique route RESOLVED is the one launched, not the one requested.
            Assert.Equal("Claude-2", terminalName);
        }

        [Fact]
        public void A_refused_default_launch_fails_closed_instead_of_falling_back_to_a_pool_name()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", "d0c1d2e3f4", NoneReserved);
            int namedCalls = 0;

            bool ok = JustClaudeLaunchIdentity.TryRegister(
                identity,
                (string requested, out string resolved, out string error) =>
                {
                    resolved = null;
                    error = "held by another terminal";
                    return false;
                },
                named => { namedCalls++; return "Grace"; },
                out string terminalName,
                out string refusal);

            Assert.False(ok);
            Assert.Null(terminalName);
            Assert.Equal("held by another terminal", refusal);
            Assert.Equal(0, namedCalls);
        }

        [Fact]
        public void A_named_launch_still_goes_through_the_named_route()
        {
            var identity = JustClaudeLaunchIdentity.FromPickerSelection("Grace", "d0c1d2e3f4", null);
            int uniqueCalls = 0;

            bool ok = JustClaudeLaunchIdentity.TryRegister(
                identity,
                (string requested, out string resolved, out string error) =>
                {
                    uniqueCalls++;
                    resolved = requested;
                    error = null;
                    return true;
                },
                named => named,
                out string terminalName,
                out _);

            Assert.True(ok);
            Assert.Equal("Grace", terminalName);
            Assert.Equal(0, uniqueCalls);
        }

        [Fact]
        public void Three_default_launches_on_the_real_broker_get_three_distinct_names_and_no_placeholder()
        {
            using var broker = new MessageBroker();
            var names = new List<string>();

            foreach (var docId in new[] { "aaaa1111", "bbbb2222", "cccc3333" })
            {
                var identity = JustClaudeLaunchIdentity.FromPickerSelection("Unassigned", docId, NoneReserved);
                // The same atomic call MainForm.TryRegisterProjectIdentity makes.
                bool ok = JustClaudeLaunchIdentity.TryRegister(
                    identity,
                    (string requested, out string resolved, out string error) =>
                    {
                        var result = broker.RegisterTerminalUnique(requested, out resolved, docId, nonce: "N-" + docId);
                        error = result.Error;
                        return result.Success;
                    },
                    _ => throw new InvalidOperationException("the default launch must not take the named route"),
                    out string terminalName,
                    out string refusal);
                Assert.True(ok, refusal);
                names.Add(terminalName);
            }

            Assert.Equal(new[] { "Claude", "Claude-2", "Claude-3" }, names);
            Assert.DoesNotContain(broker.GetAllConnectedTerminals(), t => string.Equals(t.Name, "Unassigned", StringComparison.OrdinalIgnoreCase));
            // Each name resolves to its own pane, so name-keyed routing can tell them apart.
            Assert.Equal("aaaa1111", broker.GetTerminal("Claude")?.DocId);
            Assert.Equal("bbbb2222", broker.GetTerminal("Claude-2")?.DocId);
            Assert.Equal("cccc3333", broker.GetTerminal("Claude-3")?.DocId);
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
