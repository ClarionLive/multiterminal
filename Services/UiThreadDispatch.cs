namespace MultiTerminal.Services
{
    /// <summary>How work raised on some thread reaches the UI thread (GitHub #38, task 8f0d527b).</summary>
    internal enum UiDispatch
    {
        /// <summary>Already on the UI thread: run it now.</summary>
        RunHere,

        /// <summary>Another thread, and the form has a window handle: Invoke/BeginInvoke through it.</summary>
        InvokeThroughHandle,

        /// <summary>
        /// Another thread, and the form has no handle yet (before it is shown) or any more (closing):
        /// post through the UI thread's synchronization context, captured when the form was built.
        /// </summary>
        PostToUiContext,

        /// <summary>The form is disposed: nothing on it can run.</summary>
        Drop,
    }

    /// <summary>
    /// The one rule for getting from an event's thread to the UI thread.
    /// </summary>
    /// <remarks>
    /// The pattern it replaces, <c>if (InvokeRequired) BeginInvoke(work); else work();</c>, is wrong
    /// whenever the form has no window handle: <c>Control.InvokeRequired</c> is documented to return
    /// false when no handle can be found, so the <c>else</c> branch ran the work on the CALLING thread.
    /// Where that work builds a WebView2 panel (team and subagent panels, spawned agents, browser tabs),
    /// WinForms installs a synchronization context on that background thread and the panel's async
    /// set-up captures it, which is the shape of the crash in #38 (BeginInvoke from
    /// <c>WindowsFormsSynchronizationContext.Post</c> on a control with no handle). The team watcher
    /// starts in MainForm's constructor and reports existing teams at once, before the form has a
    /// handle, so the window is real at startup and again during shutdown.
    /// Which thread we are on is decided by thread id, never by <c>InvokeRequired</c>.
    /// </remarks>
    internal static class UiThreadDispatch
    {
        internal static UiDispatch Decide(bool onUiThread, bool handleCreated, bool disposed)
        {
            if (disposed) return UiDispatch.Drop;
            if (onUiThread) return UiDispatch.RunHere;
            return handleCreated ? UiDispatch.InvokeThroughHandle : UiDispatch.PostToUiContext;
        }
    }
}
