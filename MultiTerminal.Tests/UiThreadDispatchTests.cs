using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The one rule for reaching the UI thread (GitHub #38, task 8f0d527b). The case that matters is
    /// "another thread, form has no handle": the old <c>InvokeRequired</c> pattern ran the work right
    /// there, on the caller's thread. MainForm's use of the rule (RunOnUiThread at the team-watcher,
    /// spawn and browser-tab sites) is wiring no test executes.
    /// </summary>
    public sealed class UiThreadDispatchTests
    {
        [Fact]
        public void Off_the_UI_thread_with_no_handle_the_work_is_posted_never_run_on_the_calling_thread()
            => Assert.Equal(UiDispatch.PostToUiContext, UiThreadDispatch.Decide(onUiThread: false, handleCreated: false, disposed: false));

        [Fact]
        public void Off_the_UI_thread_with_a_handle_it_goes_through_the_handle()
            => Assert.Equal(UiDispatch.InvokeThroughHandle, UiThreadDispatch.Decide(onUiThread: false, handleCreated: true, disposed: false));

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void On_the_UI_thread_it_runs_now_handle_or_not(bool handleCreated)
            => Assert.Equal(UiDispatch.RunHere, UiThreadDispatch.Decide(onUiThread: true, handleCreated, disposed: false));

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void A_disposed_form_runs_nothing(bool onUiThread, bool handleCreated)
            => Assert.Equal(UiDispatch.Drop, UiThreadDispatch.Decide(onUiThread, handleCreated, disposed: true));

        // PR #60 review: the wait path must tell a failure of Invoke (swallowed) from a failure inside the
        // action (reaches the caller). The fake invoke stands in for Control.Invoke, which runs the delegate
        // and rethrows what it throws on the calling thread; MainForm's call is wiring no test executes.

        [Fact]
        public void A_failing_invoke_is_swallowed_and_the_action_never_runs()
        {
            bool ran = false;
            UiThreadDispatch.InvokeRethrowingActionFailure(
                _ => throw new InvalidOperationException("handle destroyed"),
                () => ran = true);
            Assert.False(ran);
        }

        [Fact]
        public void A_disposed_form_at_invoke_time_is_swallowed()
            => UiThreadDispatch.InvokeRethrowingActionFailure(_ => throw new ObjectDisposedException("MainForm"), () => { });

        [Fact]
        public void An_InvalidOperationException_thrown_by_the_action_reaches_the_caller()
        {
            var thrown = new InvalidOperationException("panel creation failed");
            var caught = Assert.Throws<InvalidOperationException>(() =>
                UiThreadDispatch.InvokeRethrowingActionFailure(work => work(), () => throw thrown));
            Assert.Same(thrown, caught);
        }

        [Fact]
        public void An_action_that_succeeds_runs_once_and_nothing_is_thrown()
        {
            int runs = 0;
            UiThreadDispatch.InvokeRethrowingActionFailure(work => work(), () => runs++);
            Assert.Equal(1, runs);
        }
    }
}
