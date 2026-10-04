using System;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// Runs one WebView2 call on a header popup's show path and turns any exception into a reported,
    /// recoverable failure (task 4cac608c, pipeline Run 3 adversary finding).
    /// </summary>
    /// <remarks>
    /// WebView2 calls can throw during a renderer or browser failure, or a disposal race, before
    /// ProcessFailed has run. Escaping, the exception was swallowed by the header's message handler
    /// and left the load state saying "ready" (or "loading"), so later clicks failed quietly. The
    /// rule here: any throw marks the page failed AND the popup broken, so the user is told and the
    /// next click rebuilds the window. Rebuilding is the one step certain to work once these calls
    /// throw; a reload would go through the same broken WebView2.
    /// </remarks>
    public static class PopupWebViewGuard
    {
        /// <param name="op">The WebView2 call (post, navigate, zoom).</param>
        /// <param name="what">What was being done, for the message ("show the menu").</param>
        /// <param name="load">The popup's load state; marked failed on a throw.</param>
        /// <param name="markBroken">Marks the popup for rebuilding.</param>
        /// <param name="fail">Logs and reports the failure.</param>
        /// <returns>True when the call completed.</returns>
        public static bool Run(Action op, string what, PopupLoadState load, Action markBroken, Action<string> fail)
        {
            try
            {
                op();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                load.LoadFailed();
                markBroken();
                fail("the menu could not " + what + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                return false;
            }
        }
    }
}
