namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// Whether the show a popup is currently working on was asked for by the user (a click or a
    /// key) rather than by the mouse passing over Grid Layout (task 4cac608c, Run 3 adversary delta).
    /// </summary>
    /// <remarks>
    /// The page posts a hover request every time the pointer moves between elements inside the
    /// Grid Layout item, so hover requests repeat while an earlier show is still loading. Keeping the
    /// intent as "whatever the last request said" let one of those repeats downgrade a keyboard open
    /// still in flight: the flyout then did not take focus, and a failure was logged but never shown.
    /// Here a user request stays a user request until that show appears or is cancelled.
    /// </remarks>
    public sealed class PopupShowIntent
    {
        private bool _inFlight;

        /// <summary>True when the show in flight (or the last one) was asked for by the user.</summary>
        public bool UserInitiated { get; private set; }

        /// <summary>A new show is requested. A hover cannot downgrade a user request still in flight.</summary>
        public void Request(bool userInitiated)
        {
            UserInitiated = userInitiated || (_inFlight && UserInitiated);
            _inFlight = true;
        }

        /// <summary>The show appeared on screen; later requests start fresh.</summary>
        public void Shown() => _inFlight = false;

        /// <summary>The show was cancelled (popup hidden) or failed; later requests start fresh.</summary>
        public void Ended() => _inFlight = false;
    }
}
