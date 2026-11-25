using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The note <c>clear_my_context(continueWith)</c> leaves for the fresh session (ticket cc2e51ff): held once
    /// per pane, expiring, typed as one safe line after the hook's prompt. Not covered: that MainForm arms it
    /// before typing the /clear and that TerminalControl takes it in TriggerClearSessionStart (wiring no test
    /// executes); the live test is a helper briefed "clear, then claim card X".
    /// </summary>
    public class ClearContinueNoteTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 24, 22, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_note_is_taken_once()
        {
            var n = new ClearContinueNote();
            n.Arm("Claim card 9b57140c and code it through.", T0);

            Assert.Equal("Claim card 9b57140c and code it through.", n.TryTake(T0.AddSeconds(5)));
            Assert.Null(n.TryTake(T0.AddSeconds(6)));
        }

        /// <summary>
        /// A note whose /clear never produced a post-clear prompt must not ride on a later, unrelated clear.
        /// Falsified: removing the expiry check in TryTake turns this red.
        /// </summary>
        [Fact]
        public void An_expired_note_is_dropped()
        {
            var n = new ClearContinueNote();
            n.Arm("old", T0);
            Assert.Null(n.TryTake(T0 + ClearContinueNote.Expiry + TimeSpan.FromSeconds(1)));
            Assert.Null(n.TryTake(T0));
        }

        [Fact]
        public void A_newer_arm_replaces_the_note_and_a_blank_one_clears_it()
        {
            var n = new ClearContinueNote();
            n.Arm("first", T0);
            n.Arm("second", T0.AddSeconds(1));
            Assert.Equal("second", n.TryTake(T0.AddSeconds(2)));

            n.Arm("third", T0);
            n.Arm("   ", T0.AddSeconds(1));
            Assert.Null(n.TryTake(T0.AddSeconds(2)));
        }

        /// <summary>
        /// The note is typed into the terminal: a line break would submit the prompt early and an escape or
        /// Ctrl-C would reach the terminal. Every control character becomes a space.
        /// </summary>
        [Fact]
        public void Sanitize_makes_one_line_without_control_characters()
        {
            Assert.Equal("Claim card X then run tests", ClearContinueNote.Sanitize("  Claim card X\r\nthen\t\trun \u001b\u0003 tests  "));
            Assert.Null(ClearContinueNote.Sanitize("\r\n\t "));
            Assert.Null(ClearContinueNote.Sanitize(null));
        }

        [Fact]
        public void Sanitize_caps_the_length_and_never_leaves_half_a_surrogate_pair()
        {
            string longText = new string('a', ClearContinueNote.MaxLength + 500);
            Assert.Equal(ClearContinueNote.MaxLength, ClearContinueNote.Sanitize(longText).Length);

            string emoji = new string('a', ClearContinueNote.MaxLength - 1) + "\U0001F600";
            string cut = ClearContinueNote.Sanitize(emoji);
            Assert.False(char.IsHighSurrogate(cut[cut.Length - 1]));
        }

        [Fact]
        public void Compose_follows_the_hooks_prompt_and_no_note_leaves_it_unchanged()
        {
            const string hook = "[MultiTerminal] Your context was cleared. Check in with your PM, Grace, and follow the instructions MultiTerminal gave you at the start of this session.";
            Assert.Equal(hook, ClearContinueNote.Compose(hook, null));
            Assert.Equal("initializing...", ClearContinueNote.Compose("initializing...", ""));

            string composed = ClearContinueNote.Compose(hook, "Claim card 9b57140c.");
            Assert.StartsWith(hook + " ", composed);
            Assert.EndsWith("Before clearing, you left yourself this to do next: Claim card 9b57140c.", composed);
            Assert.DoesNotContain("\n", composed);
        }
    }
}
