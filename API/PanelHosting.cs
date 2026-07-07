namespace MultiTerminal.API
{
    /// <summary>
    /// Single source of truth for the WebView2 virtual-host origin that serves the in-app
    /// panels which fetch the :5050 REST API (currently only <c>TasksPanel/tasks-panel.html</c>).
    /// </summary>
    /// <remarks>
    /// Task f9697aac migrated tasks-panel.html off <c>file://</c> (whose fetch Origin serialized
    /// to the forgeable literal "null") onto this real, allowlistable virtual host via
    /// <c>CoreWebView2.SetVirtualHostNameToFolderMapping</c>. Three places must agree on the exact
    /// origin string or the panel silently breaks (CORS rejects its report fetches) or a hole
    /// reopens (the allowlist tolerates an origin the panel no longer uses):
    /// <list type="bullet">
    ///   <item>the panel loader (<see cref="MultiTerminal.TasksPanel.TasksPanelControl"/>) — maps the
    ///   host and navigates to it;</item>
    ///   <item>the CORS allowlist (<see cref="RestCorsOriginPolicy.IsLoopbackOrigin"/>) — permits this
    ///   origin to READ (so the browser exposes report responses to the panel's JS);</item>
    ///   <item>the Sec-Fetch-Site write-guard (<see cref="SecFetchSiteWriteGuardMiddleware"/>) — treats
    ///   this origin as trusted for WRITES.</item>
    /// </list>
    /// Keeping the constants here (not duplicated as literals) is what makes a future rename a
    /// one-line, non-desyncing change.
    /// <para>
    /// The <c>http</c> scheme is deliberate: the panel fetches <c>http://localhost:5050</c>, so an
    /// <c>http</c> virtual-host origin keeps that request http→http and sidesteps browser
    /// mixed-content blocking entirely (an https origin would rely on Chromium's loopback-trustworthy
    /// exemption; http needs no exemption). The panels use no secure-context-only web features.
    /// </para>
    /// </remarks>
    internal static class PanelHosting
    {
        /// <summary>
        /// Virtual hostname mapped to the panel's folder via
        /// <c>CoreWebView2.SetVirtualHostNameToFolderMapping</c>. WebView2 intercepts this name
        /// in-process, so no DNS resolution occurs.
        /// </summary>
        public const string VirtualHostName = "mt-panels.local";

        /// <summary>
        /// The serialized <c>Origin</c> that panels served from <see cref="VirtualHostName"/> send on
        /// their cross-origin fetches to :5050. Compared (ordinal, case-insensitive) by the CORS
        /// predicate and the write-guard allowlist.
        /// </summary>
        public const string Origin = "http://" + VirtualHostName;
    }
}
