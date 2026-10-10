using System;
using MultiTerminal.Controls;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec (GitHub #21): <see cref="TerminalStartFailure.Describe"/>, the pure part of how
    /// <c>TerminalControl.DoStart</c> treats a failed start.
    /// <para>NOT covered, and not coverable without a window and a pseudo console: that DoStart shows
    /// exactly this text, skips Ready, and raises StartFailed; that TerminalDocument then returns the
    /// pane to its start screen; that MainForm releases the registration; and that ConPtyTerminal
    /// throws <see cref="ClaudeProfileRefusedException"/> at all.</para>
    /// </summary>
    public sealed class TerminalStartFailureTests
    {
        [Fact]
        public void A_profile_refusal_shows_only_its_own_message_and_the_pane_is_not_ready()
        {
            var refusal = new ClaudeProfileRefusedException("Claude profile 'Work' cannot be applied, so nothing was started.");

            var failure = TerminalStartFailure.Describe(refusal);

            Assert.True(failure.IsClaudeProfileRefusal);
            Assert.False(failure.RaisesReady);
            Assert.Equal(refusal.Message, failure.Message);
            Assert.Equal("Claude Profile", failure.Title);
        }

        [Fact]
        public void Any_other_failure_keeps_the_pseudo_console_message_and_still_raises_Ready()
        {
            var failure = TerminalStartFailure.Describe(new InvalidOperationException("Failed to create pseudo console. Error: 5"));

            Assert.False(failure.IsClaudeProfileRefusal);
            Assert.True(failure.RaisesReady);
            Assert.StartsWith("Failed to start terminal: Failed to create pseudo console. Error: 5", failure.Message, StringComparison.Ordinal);
            Assert.Contains("Windows 10 version 1809", failure.Message, StringComparison.Ordinal);
            Assert.Equal("Terminal Error", failure.Title);
        }

        [Fact]
        public void A_profile_refusal_is_still_an_InvalidOperationException()
        {
            // What ConPtyTerminal threw before the type existed; a caller catching that keeps working.
            Assert.IsAssignableFrom<InvalidOperationException>(new ClaudeProfileRefusedException("x"));
        }
    }
}
