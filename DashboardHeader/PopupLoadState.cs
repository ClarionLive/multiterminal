namespace MultiTerminal.DashboardHeader
{
    /// <summary>What a header popup must do to serve a show request.</summary>
    public enum PopupLoadAction
    {
        /// <summary>The page is ready: post the show now.</summary>
        Post,

        /// <summary>A start or a load is already under way: keep the show pending until it finishes.</summary>
        Wait,

        /// <summary>Start (or restart) the WebView2 and load the page.</summary>
        Init,

        /// <summary>The WebView2 runs but its page failed or never answered: load the page again.</summary>
        Reload,
    }

    /// <summary>
    /// The load state of a header popup's page, kept apart from the window so the retry rules can be
    /// tested without a WebView2 (task 4cac608c, pipeline Run 2). The rule: after any reported
    /// failure, the next show tries again with the cheapest step that can work: restart the WebView2
    /// only when it never started, and only reload the page when the WebView2 is fine. Reporting
    /// each failure here is HeaderPopupForm's job.
    /// </summary>
    public sealed class PopupLoadState
    {
        private bool _initRunning;
        private bool _webViewCreated;
        private bool _loading;
        private bool _failed;

        /// <summary>The page has posted "ready" and nothing has failed since.</summary>
        public bool PageReady { get; private set; }

        /// <summary>Decides the next step for a show request, and records that it is being taken.</summary>
        public PopupLoadAction NextForShow()
        {
            if (PageReady) return PopupLoadAction.Post;
            if (_initRunning) return PopupLoadAction.Wait;
            if (!_webViewCreated)
            {
                _initRunning = true;
                _failed = false;
                return PopupLoadAction.Init;
            }
            if (_failed || !_loading)
            {
                _failed = false;
                _loading = true;
                return PopupLoadAction.Reload;
            }
            return PopupLoadAction.Wait;
        }

        /// <summary>The background warm-up at startup: only starts the WebView2 if nothing has yet.</summary>
        public bool ShouldWarmUp()
        {
            if (_initRunning || _webViewCreated) return false;
            _initRunning = true;
            return true;
        }

        /// <summary>The WebView2 exists and the page's navigation has begun.</summary>
        public void InitSucceeded()
        {
            _initRunning = false;
            _webViewCreated = true;
            _loading = true;
        }

        /// <summary>The WebView2 could not be created (or the page file is missing).</summary>
        public void InitFailed()
        {
            _initRunning = false;
            _failed = true;
        }

        /// <summary>The page posted "ready".</summary>
        public void Ready()
        {
            PageReady = true;
            _loading = false;
            _failed = false;
        }

        /// <summary>The page failed to load, never answered in time, or its renderer died.</summary>
        public void LoadFailed()
        {
            PageReady = false;
            _loading = false;
            _failed = true;
        }
    }
}
