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
    }
}
