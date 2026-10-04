using System;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// Keeps a WebView2 on the one local page it was built for, and tells its message handler
    /// whether a message came from that page (task 4cac608c, pipeline Run 1 security finding).
    /// </summary>
    /// <remarks>
    /// The header and its popups turn page messages into native actions: Exit, Open PowerShell,
    /// switch project. Without this, any document that ended up in the WebView (a file dropped on
    /// it, a navigation, a new window) could post those messages too. The pin blocks the ways in;
    /// <see cref="IsFromPage"/> is the check at the point of use, so a way in nobody thought of
    /// still cannot trigger an action.
    /// </remarks>
    public static class WebViewPagePin
    {
        /// <summary>
        /// Blocks dropped files, navigation away from <paramref name="pageUri"/> and new windows.
        /// Call once, after <c>EnsureCoreWebView2Async</c>.
        /// </summary>
        public static void Pin(WebView2 webView, string pageUri)
        {
            webView.AllowExternalDrop = false;
            webView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                if (!IsFromPage(e.Uri, pageUri)) e.Cancel = true;
            };
            webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;
        }

        /// <summary>
        /// True only when <paramref name="source"/> is exactly <paramref name="pageUri"/>. File URIs
        /// compare case-insensitively (Windows paths); anything else, including a different file in
        /// the same folder, a query string or a fragment, is a different page.
        /// </summary>
        public static bool IsFromPage(string source, string pageUri)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pageUri)) return false;
            if (!Uri.TryCreate(source, UriKind.Absolute, out var a) || !Uri.TryCreate(pageUri, UriKind.Absolute, out var b))
                return false;
            if (!a.IsFile || !b.IsFile) return false;
            if (a.Query.Length > 0 || a.Fragment.Length > 0) return false;
            return string.Equals(a.LocalPath, b.LocalPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
