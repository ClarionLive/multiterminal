using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2 wrapper for browser tabs inside HudTabContainer.
    /// Supports URL navigation and raw HTML content with lazy WebView2 initialization.
    /// </summary>
    public class BrowserTabPage : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        // Queued navigation for before WebView2 is ready
        private string _pendingUrl;
        private string _pendingHtml;

        /// <summary>
        /// Unique identifier for this tab.
        /// </summary>
        public string TabId { get; }

        /// <summary>
        /// Display title for the tab strip.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Raised when Title changes from a page navigation.
        /// </summary>
        public event EventHandler TitleChanged;

        public BrowserTabPage(string tabId, string title)
        {
            TabId = tabId ?? throw new ArgumentNullException(nameof(tabId));
            Title = title ?? tabId;

            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 30);
            Name = "BrowserTabPage_" + tabId;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView_" + tabId
            };
            Controls.Add(_webView);
            ResumeLayout(false);

            // Lazy init: start WebView2 when first made visible
            VisibleChanged += OnVisibleChanged;
        }

        private void OnVisibleChanged(object sender, EventArgs e)
        {
            if (Visible && !_isInitialized && !_isInitializing)
            {
                InitializeWebView();
            }
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? Color.FromArgb(26, 26, 46)
                    : Color.FromArgb(245, 245, 245);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                _isInitialized = true;
                _isInitializing = false;

                // Apply pending zoom
                if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                    _webView.ZoomFactor = _pendingZoom;

                // Flush queued navigation
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    _webView.CoreWebView2.Navigate(_pendingUrl);
                    _pendingUrl = null;
                    _pendingHtml = null;
                }
                else if (!string.IsNullOrEmpty(_pendingHtml))
                {
                    _webView.CoreWebView2.NavigateToString(_pendingHtml);
                    _pendingHtml = null;
                }
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess && _webView?.CoreWebView2 != null)
            {
                // Update title from page title if we don't have a custom one
                var pageTitle = _webView.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrEmpty(pageTitle) && Title == TabId)
                {
                    Title = pageTitle.Length > 30 ? pageTitle.Substring(0, 27) + "..." : pageTitle;
                    TitleChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Navigate to a URL.
        /// </summary>
        public void NavigateToUrl(string url)
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Navigate(url);
            }
            else
            {
                _pendingUrl = url;
                _pendingHtml = null;
            }
        }

        /// <summary>
        /// Load raw HTML content.
        /// </summary>
        public void LoadHtmlContent(string html)
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NavigateToString(html);
            }
            else
            {
                _pendingHtml = html;
                _pendingUrl = null;
            }
        }

        /// <summary>
        /// Apply theme (sets WebView2 background color).
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            if (_webView != null)
            {
                _webView.DefaultBackgroundColor = isDark
                    ? Color.FromArgb(26, 26, 46)
                    : Color.FromArgb(245, 245, 245);
            }
        }

        /// <summary>
        /// Set the zoom factor for the WebView2 control.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    }
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
