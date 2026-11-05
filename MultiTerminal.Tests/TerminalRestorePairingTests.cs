#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Docking;
using MultiTerminal.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="TerminalRestorePairing"/> and <see cref="TerminalPersistString"/> (GH #74): which
    /// saved session each terminal entry of a restored layout gets.
    ///
    /// <para>The bug: layout.xml lists every terminal, floated ones included, in the dock panel's
    /// content order, while the saved session list held only the DOCKED terminals. The restore handed
    /// sessions out by position, so one floated terminal in the middle moved every later pane onto
    /// its neighbour's session. The first fact replays exactly that shape.</para>
    /// </summary>
    public class TerminalRestorePairingTests
    {
        private static readonly string KeyA = Guid.NewGuid().ToString("N");
        private static readonly string KeyB = Guid.NewGuid().ToString("N");
        private static readonly string KeyC = Guid.NewGuid().ToString("N");

        private static TerminalSessionInfo Session(string title, string? key)
            => new TerminalSessionInfo { CustomTitle = title, PaneKey = key, WorkingDirectory = @"C:\work\" + title };

        /// <summary>
        /// Layout order A, B (floated), C. The old save left B out, and saved A, C. Paired by
        /// position, the B entry took C's session and the C entry got nothing. By key, each entry
        /// gets its own session and B's (unsaved) entry gets none.
        /// </summary>
        [Fact]
        public void A_floated_pane_in_the_middle_does_not_shift_later_panes_onto_the_wrong_session()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA), Session("Carol", KeyC) };
            var layoutKeys = new[] { KeyA, KeyB, KeyC };
            var pairing = new TerminalRestorePairing(saved, layoutKeys);

            var restored = layoutKeys.Select(k => pairing.Take(k)?.CustomTitle).ToList();

            Assert.Equal(new string?[] { "Alice", null, "Carol" }, restored);
        }

        /// <summary>
        /// With floated panes saved too (the fixed save), sessions saved in a different order from the
        /// layout still land on their own panes.
        /// </summary>
        [Fact]
        public void Keyed_entries_take_their_own_session_whatever_order_the_sessions_were_saved_in()
        {
            var saved = new List<TerminalSessionInfo> { Session("Carol", KeyC), Session("Alice", KeyA), Session("Bob", KeyB) };
            var layoutKeys = new[] { KeyA, KeyB, KeyC };
            var pairing = new TerminalRestorePairing(saved, layoutKeys);

            Assert.Equal(
                new[] { "Alice", "Bob", "Carol" },
                layoutKeys.Select(k => pairing.Take(k)!.CustomTitle).ToArray());
        }

        /// <summary>
        /// A layout written before GH #74 has bare type names: no keys at all. It must pair exactly as
        /// before, first entry first session.
        /// </summary>
        [Fact]
        public void A_layout_from_before_the_change_pairs_in_order_as_it_always_did()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA), Session("Bob", KeyB), Session("Carol", null) };
            var pairing = new TerminalRestorePairing(saved, Array.Empty<string>());

            Assert.Equal("Alice", pairing.Take(null)!.CustomTitle);
            Assert.Equal("Bob", pairing.Take(null)!.CustomTitle);
            Assert.Equal("Carol", pairing.Take(null)!.CustomTitle);
            Assert.Null(pairing.Take(null));
        }

        /// <summary>
        /// An entry whose key matches no session (the session reader replaced a duplicated key, say)
        /// falls back to an unclaimed session, and must not take one a LATER entry is keyed to.
        /// Taking "the next session in order" would give it Bob's, and Bob's own entry would then
        /// miss and take whatever was left.
        /// </summary>
        [Fact]
        public void An_entry_whose_key_misses_does_not_take_the_session_a_later_entry_is_keyed_to()
        {
            string orphanKey = Guid.NewGuid().ToString("N");
            var saved = new List<TerminalSessionInfo> { Session("Bob", KeyB), Session("Loose", Guid.NewGuid().ToString("N")) };
            var layoutKeys = new[] { orphanKey, KeyB };
            var pairing = new TerminalRestorePairing(saved, layoutKeys);

            Assert.Equal("Loose", pairing.Take(orphanKey)!.CustomTitle);
            Assert.Equal("Bob", pairing.Take(KeyB)!.CustomTitle);
        }

        /// <summary>A session is handed out at most once, so a repeated key cannot restore it twice.</summary>
        [Fact]
        public void A_session_is_handed_out_once()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA) };
            var pairing = new TerminalRestorePairing(saved, new[] { KeyA, KeyA });

            Assert.Equal("Alice", pairing.Take(KeyA)!.CustomTitle);
            Assert.Null(pairing.Take(KeyA));
        }

        /// <summary>Keys compare as GUIDs, so an upper-case key in the layout finds its session.</summary>
        [Fact]
        public void Keys_compare_by_their_canonical_form()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA) };
            var pairing = new TerminalRestorePairing(saved, new[] { KeyA.ToUpperInvariant() });

            Assert.Equal("Alice", pairing.Take(KeyA.ToUpperInvariant())!.CustomTitle);
        }

        /// <summary>
        /// The session list is saved during a run (a font change), layout.xml only on a clean exit. A
        /// crash after floating a new terminal B leaves a list holding A and B and a layout holding only
        /// A. The layout asks for A alone; B must come back from <see cref="TerminalRestorePairing.TakeRemaining"/>
        /// rather than be dropped and then lost at the next save.
        /// </summary>
        [Fact]
        public void A_session_newer_than_the_layout_is_handed_back_as_remaining()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA), Session("Bob", KeyB) };
            var pairing = new TerminalRestorePairing(saved, new[] { KeyA });

            Assert.Equal("Alice", pairing.Take(KeyA)!.CustomTitle);
            Assert.Equal(new[] { "Bob" }, pairing.TakeRemaining().Select(s => s.CustomTitle).ToArray());
            Assert.Empty(pairing.TakeRemaining());
        }

        /// <summary>Remaining sessions are the untaken ones only, in saved order.</summary>
        [Fact]
        public void Remaining_leaves_out_what_the_layout_took()
        {
            var saved = new List<TerminalSessionInfo> { Session("Alice", KeyA), Session("Bob", KeyB), Session("Carol", KeyC) };
            var pairing = new TerminalRestorePairing(saved, new[] { KeyB });

            Assert.Equal("Bob", pairing.Take(KeyB)!.CustomTitle);
            Assert.Equal(new[] { "Alice", "Carol" }, pairing.TakeRemaining().Select(s => s.CustomTitle).ToArray());
        }

        [Fact]
        public void No_sessions_gives_every_entry_null()
        {
            var pairing = new TerminalRestorePairing(null, null);
            Assert.Null(pairing.Take(KeyA));
            Assert.Null(pairing.Take(null));
        }

        [Fact]
        public void The_persist_string_carries_the_key_and_parses_back()
        {
            string s = TerminalPersistString.For(KeyA);

            Assert.NotEqual(TerminalPersistString.TypeName, s);
            Assert.True(TerminalPersistString.TryParse(s, out string? key));
            Assert.Equal(KeyA, key);
        }

        [Fact]
        public void The_bare_type_name_is_a_terminal_entry_without_a_key()
        {
            Assert.True(TerminalPersistString.TryParse(TerminalPersistString.TypeName, out string? key));
            Assert.Null(key);
            Assert.Equal(TerminalPersistString.TypeName, TerminalPersistString.For(null));
        }

        [Theory]
        [InlineData("ChatPanel")]
        [InlineData("Oracle")]
        [InlineData("")]
        [InlineData("MultiTerminal.Docking.TerminalDocumentX")]
        [InlineData("MultiTerminal.Docking.TerminalDocument|0123")]
        public void Other_persist_strings_are_not_terminal_entries(string persistString)
        {
            Assert.False(TerminalPersistString.TryParse(persistString, out _));
        }

        /// <summary>A terminal entry whose key is not a GUID is still a terminal, paired as keyless.</summary>
        [Fact]
        public void A_malformed_key_is_a_terminal_entry_without_a_key()
        {
            Assert.True(TerminalPersistString.TryParse(TerminalPersistString.TypeName + ":not-a-guid", out string? key));
            Assert.Null(key);
        }

        [Fact]
        public void Only_docked_and_floated_terminals_are_listed()
        {
            Assert.True(GridLayoutManager.IsListedState(WeifenLuo.WinFormsUI.Docking.DockState.Document));
            Assert.True(GridLayoutManager.IsListedState(WeifenLuo.WinFormsUI.Docking.DockState.Float));
            Assert.False(GridLayoutManager.IsListedState(WeifenLuo.WinFormsUI.Docking.DockState.Hidden));
            Assert.False(GridLayoutManager.IsListedState(WeifenLuo.WinFormsUI.Docking.DockState.Unknown));
        }
    }
}
