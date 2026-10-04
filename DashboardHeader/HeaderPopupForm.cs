using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// A borderless popup window that hosts header-popup.html: the M menu, its Grid Layout flyout and
    /// the Select Project picker (task 4cac608c).
    /// </summary>
    /// <remarks>
    /// The header is one 80px-tall WebView2, and a dropdown drawn inside it is clipped at its bottom
    /// edge, which is why the M menu used to be a native ContextMenuStrip. A separate top-level window
    /// can hang over the terminals, so the menus can be HTML and look like the rest of the header.
    /// The WebView2 is created hidden at startup (<see cref="WarmUpAsync"/>) so a click opens it
    /// without the browser's start-up delay.
    /// <para>
    /// The window is never shown until the page has rendered the requested view and reported its
    /// size, so it never appears at the wrong size and then jumps.
    /// </para>
    /// </remarks>
    public sealed class HeaderPopupForm : Form
    {
        private readonly WebView2 _webView;
        private bool _pageReady;
        private string _pendingShow;          // a show requested before the page was ready
        private Rectangle _anchor;            // screen rect the popup opens against
        private PopupSide _side;
        private double _scale = 1.0;          // CSS px -> physical px
        private bool _activateOnShow = true;
        private Form _owner;

        /// <summary>The view the page is showing: "menu", "submenu" or "projects".</summary>
        public string CurrentView { get; private set; }

        /// <summary>The user chose something: the action name and, for a project, its id.</summary>
        public event Action<string, string> ActionChosen;

        /// <summary>The menu asks for the Grid Layout flyout: open, the item's top (CSS px), whether to focus it.</summary>
        public event Action<bool, double, bool> SubmenuRequested;

        /// <summary>Esc or Left: true when only this popup should close (back to the menu).</summary>
        public event Action<bool> DismissRequested;

        public HeaderPopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = false;
            BackColor = Color.FromArgb(30, 30, 46);
            Size = new Size(250, 200);

            _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            _webView.WebMessageReceived += OnWebMessageReceived;
            Controls.Add(_webView);
        }

        /// <summary>Owner for z-order and activation: the popup always sits above MainForm.</summary>
        public void SetOwnerForm(Form owner) => _owner = owner;

        // A hover-opened flyout must not take activation from the menu, or the menu would lose the
        // keyboard. Keyboard-opened popups do activate (so arrows and Esc reach the page).
        protected override bool ShowWithoutActivation => !_activateOnShow;

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                const int WS_EX_TOOLWINDOW = 0x00000080; // keeps it out of Alt+Tab
                var cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 rounds the corners and draws the edge; earlier Windows ignores both
            // attributes and keeps a square window, which is acceptable.
            try
            {
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>Creates the WebView2 and loads the page while the window stays hidden.</summary>
        public async Task WarmUpAsync()
        {
            if (_webView.CoreWebView2 != null) return;
            _ = Handle;          // the WebView2 needs a parent window, but not a visible one
            _ = _webView.Handle;
            var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
            await _webView.EnsureCoreWebView2Async(env);
            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = false;

            var path = Path.Combine(AppContext.BaseDirectory, "DashboardHeader", "header-popup.html");
            if (File.Exists(path))
                _webView.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
        }

        /// <summary>
        /// Renders a view and shows the popup beside <paramref name="anchor"/> (screen coordinates)
        /// once the page reports its size.
        /// </summary>
        /// <param name="showJson">The page's {type:'show', view, ...} message.</param>
        /// <param name="scale">Physical pixels per CSS pixel (DPI scale times the header's zoom).</param>
        public void ShowAt(string showJson, Rectangle anchor, PopupSide side, double scale, double zoom, bool activate)
        {
            _anchor = anchor;
            _side = side;
            _scale = scale <= 0 ? 1.0 : scale;
            _activateOnShow = activate;
            if (_webView.CoreWebView2 != null && Math.Abs(_webView.ZoomFactor - zoom) > 0.001)
                _webView.ZoomFactor = zoom;

            if (!_pageReady)
            {
                _pendingShow = showJson;
                _ = WarmUpAsync();
                return;
            }
            _webView.CoreWebView2.PostWebMessageAsJson(showJson);
        }

        /// <summary>Hides the popup; the page stays loaded for next time.</summary>
        public void HidePopup()
        {
            _pendingShow = null;
            if (Visible) Hide();
        }

        /// <summary>Moves keyboard focus into an already-visible popup.</summary>
        public void FocusPage()
        {
            if (!Visible) return;
            Activate();
            _webView.Focus();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;

                switch (typeEl.GetString())
                {
                    case "ready":
                        _pageReady = true;
                        if (_pendingShow != null)
                        {
                            var show = _pendingShow;
                            _pendingShow = null;
                            _webView.CoreWebView2.PostWebMessageAsJson(show);
                        }
                        break;

                    case "size":
                        CurrentView = root.TryGetProperty("view", out var v) ? v.GetString() : null;
                        var size = new Size(
                            (int)Math.Ceiling(root.GetProperty("width").GetDouble() * _scale),
                            (int)Math.Ceiling(root.GetProperty("height").GetDouble() * _scale));
                        var area = Screen.FromRectangle(_anchor).WorkingArea;
                        Bounds = PopupPlacement.Place(_anchor, size, area, _side);
                        if (!Visible)
                        {
                            if (_owner != null) Show(_owner); else Show();
                        }
                        if (_activateOnShow) FocusPage();
                        break;

                    case "action":
                        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                        var projectId = root.TryGetProperty("projectId", out var p) ? p.GetString() : null;
                        if (!string.IsNullOrEmpty(action)) ActionChosen?.Invoke(action, projectId);
                        break;

                    case "submenu":
                        bool open = root.TryGetProperty("open", out var o) && o.GetBoolean();
                        double top = root.TryGetProperty("top", out var t) ? t.GetDouble() : 0;
                        bool focus = root.TryGetProperty("focus", out var f) && f.GetBoolean();
                        SubmenuRequested?.Invoke(open, top, focus);
                        break;

                    case "dismiss":
                        DismissRequested?.Invoke(root.TryGetProperty("back", out var b) && b.GetBoolean());
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"[HeaderPopupForm] Bad message: {ex.Message}");
            }
        }

        /// <summary>Scale of an item's top edge (CSS px) to a screen y inside this popup.</summary>
        public int ScreenYForCssTop(double cssTop) => Top + (int)Math.Round(cssTop * _scale);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _webView.Dispose();
            base.Dispose(disposing);
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
