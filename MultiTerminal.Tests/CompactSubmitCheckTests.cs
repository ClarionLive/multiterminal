using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="CompactSubmitCheck"/> (ticket 354f1efa): an agent-requested <c>/compact</c> that is
    /// still sitting in the input line gets one more Enter, and only when nobody has typed in the pane. The
    /// MainForm loop that reads the screen and sends the Enter is executed live only.
    /// </summary>
    public class CompactSubmitCheckTests
    {
        private const string Typed = "/compact Keep: active task da0fcff8 (#102) at 1d178d9, next = run pipeline then merge to trial-301.";

        [Fact]
        public void A_compaction_that_started_is_verified_whatever_the_screen_shows()
        {
            Assert.Equal(CompactSubmitStep.Verified, CompactSubmitCheck.Decide(true, true, false, false));
            Assert.Equal(CompactSubmitStep.Verified, CompactSubmitCheck.Decide(true, null, true, true));
        }

        [Fact]
        public void A_cleared_line_is_verified()
        {
            Assert.Equal(CompactSubmitStep.Verified, CompactSubmitCheck.Decide(false, false, false, false));
        }

        [Fact]
        public void Text_still_in_the_line_gets_one_more_Enter()
        {
            Assert.Equal(CompactSubmitStep.SendEnterAgain, CompactSubmitCheck.Decide(false, true, false, false));
        }

        [Fact]
        public void Never_a_third_Enter()
        {
            Assert.Equal(CompactSubmitStep.NotVerified, CompactSubmitCheck.Decide(false, true, false, true));
        }

        [Fact]
        public void No_Enter_when_someone_typed_since_the_inject()
        {
            // An Enter on the Owner's half-typed text would submit it.
            Assert.Equal(CompactSubmitStep.NotVerified, CompactSubmitCheck.Decide(false, true, true, false));
        }

        [Fact]
        public void No_Enter_when_the_screen_could_not_be_read()
        {
            Assert.Equal(CompactSubmitStep.NotVerified, CompactSubmitCheck.Decide(false, null, false, false));
        }

        [Fact]
        public void Text_wrapped_inside_a_bordered_input_box_is_found()
        {
            string screen =
                "╭──────────────────────────────╮\n" +
                "│ > /compact Keep: active ta   │\n" +
                "│ sk da0fcff8 (#102) at 1d1    │";

            Assert.True(CompactSubmitCheck.LineStillHolds(screen, Typed));
        }

        [Fact]
        public void An_empty_prompt_after_a_submit_does_not_match()
        {
            string screen =
                "● Compacting conversation…\n" +
                "──────────────────────────────\n" +
                "> ";

            Assert.False(CompactSubmitCheck.LineStillHolds(screen, Typed));
        }

        [Fact]
        public void Only_the_start_of_the_text_is_needed()
        {
            // The tail can be off screen or cut; the first 24 characters (whitespace removed) identify it.
            Assert.True(CompactSubmitCheck.LineStillHolds("> /compact Keep: active task da0f", Typed));
            Assert.False(CompactSubmitCheck.LineStillHolds("> /compact Keep: active task", Typed));
        }

        [Fact]
        public void Nothing_matches_an_empty_screen_or_empty_text()
        {
            Assert.False(CompactSubmitCheck.LineStillHolds(null, Typed));
            Assert.False(CompactSubmitCheck.LineStillHolds(string.Empty, Typed));
            Assert.False(CompactSubmitCheck.LineStillHolds("> /compact", "  "));
        }
    }
}
