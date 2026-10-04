using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <para>
    /// Every show carries a request id that the page echoes in its size reply. A reply for any
    /// request but the current one is ignored, and <see cref="HidePopup"/> retires the current id,
    /// so a popup closed (or re-requested) while its page was still rendering never appears late,
    /// at the wrong size, or beside the wrong item.
    /// </para>
    /// <para>
    /// The page loads once, hidden, at startup. If it cannot load (WebView2 failure, missing file,
    /// no "ready" within <see cref="ReadyTimeoutMs"/>), a pending show raises
    /// <see cref="ShowFailed"/> instead of silently doing nothing, and the next show retries.
    /// </para>
    /// </remarks>
    public sealed class HeaderPopupForm : Form
    {
        /// <summary>How long a show waits for the page's "ready" before it is reported as failed.</summary>
        public const int ReadyTimeoutMs = 5000;

        private readonly WebView2 _webView;
        private readonly Action<string> _logError;
        private readonly string _pageUri;
        private readonly System.Windows.Forms.Timer _readyTimer = new() { Interval = ReadyTimeoutMs };
        private readonly PopupLoadState _load = new();
        private bool _hooked;
        private JsonObject _pendingShow;      // a show requested before the page was ready
        private int _requestId;               // the show whose size reply may still be applied
        private Rectangle _anchor;            // screen rect the popup opens against
        private PopupSide _side;
        private double _scale = 1.0;          // CSS px -> physical px
        private double _zoom = 1.0;
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

        /// <summary>A requested show could not happen; the argument says why, in plain words.</summary>
        public event Action<string> ShowFailed;

        /// <param name="logError">Where failures are logged (MT's debug log).</param>
        public HeaderPopupForm(Action<string> logError)
        {
            _logError = logError ?? (_ => { });
            _pageUri = new Uri(Path.Combine(AppContext.BaseDirectory, "DashboardHeader", "header-popup.html")).AbsoluteUri;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(250, 200);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            _webView.WebMessageReceived += OnWebMessageReceived;
            Controls.Add(_webView);
            ApplyThemeColors(isDark: true);

            _readyTimer.Tick += (s, e) =>
            {
                _readyTimer.Stop();
                if (_load.PageReady) return;
                _load.LoadFailed(); // the next show reloads the page rather than waiting forever
                Fail("the menu page did not load within " + (ReadyTimeoutMs / 1000) + " seconds");
            };
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
            // Windows 11 rounds the corners; earlier Windows ignores the attribute and keeps a
            // square window, which is acceptable.
            try
            {
                int round = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // Alt+F4 reaches a focused popup. Closing a Show()n form disposes it while the header still
        // holds it, which used to break the M menu until restart; hide it instead.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HidePopup();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>The background warm-up at startup: starts the WebView2 unless something already has.</summary>
        public Task WarmUpAsync() => _load.ShouldWarmUp() ? InitAsync() : Task.CompletedTask;

        /// <summary>
        /// The WebView2's browser process died: this window cannot recover. The header replaces a
        /// broken popup before its next show.
        /// </summary>
        public bool IsBroken { get; private set; }

        // Callers have already moved _load into its "init running" state.
        private async Task InitAsync()
        {
            try
            {
                var path = new Uri(_pageUri).LocalPath;
                if (!File.Exists(path))
                {
                    _load.InitFailed();
                    Fail("the menu page is missing (" + path + ")");
                    return;
                }

                _ = Handle;          // the WebView2 needs a parent window, but not a visible one
                _ = _webView.Handle;
                if (_webView.CoreWebView2 == null)
                {
                    var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                    await _webView.EnsureCoreWebView2Async(env);
                }
                HookOnce();
                _webView.ZoomFactor = _zoom;
                _load.InitSucceeded();
                _webView.CoreWebView2.Navigate(_pageUri);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _load.InitFailed();
                Fail("the menu could not start (" + ex.Message + ")");
            }
        }

        // A retried start must not attach the handlers a second time.
        private void HookOnce()
        {
            if (_hooked) return;
            _hooked = true;

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = false;
            WebViewPagePin.Pin(_webView, _pageUri);

            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                // The pin cancelling a foreign navigation also ends here; that is not a failure.
                if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                _load.LoadFailed();
                Fail("the menu page failed to load (" + e.WebErrorStatus + ")");
            };

            _webView.CoreWebView2.ProcessFailed += (s, e) =>
            {
                // Only failures that take the page with them. GPU and utility process exits are
                // recovered by WebView2 itself; reacting to them would only reload a working menu.
                var kind = e.ProcessFailedKind;
                if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited
                    && kind != CoreWebView2ProcessFailedKind.RenderProcessExited
                    && kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                {
                    return;
                }
                if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited) IsBroken = true;
                _load.LoadFailed();
                Fail("the menu's browser process stopped (" + kind + ")");
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(HidePopup));
            };
        }

        /// <summary>
        /// Renders a view and shows the popup beside <paramref name="anchor"/> (screen coordinates)
        /// once the page reports its size. If the page is not ready, the show waits for it, and any
        /// earlier failure is retried first (see <see cref="PopupLoadState"/>).
        /// </summary>
        /// <param name="show">The page's show message: view, theme and view data. A request id is added here.</param>
        /// <param name="scale">Physical pixels per CSS pixel (DPI scale times the header's zoom).</param>
        /// <param name="zoom">The header's zoom, so the popup's text matches the header's.</param>
        public void ShowAt(JsonObject show, Rectangle anchor, PopupSide side, double scale, double zoom, bool activate, bool isDark)
        {
            _anchor = anchor;
            _side = side;
            _scale = scale <= 0 ? 1.0 : scale;
            _zoom = zoom <= 0 ? 1.0 : zoom;
            _activateOnShow = activate;
            show["requestId"] = ++_requestId;
            ApplyThemeColors(isDark);

            // Pending from here on, so a failure below is reported to the user (Fail only reports a
            // show that is pending). The ready/size path clears it as usual.
            _pendingShow = show;
            var next = _load.NextForShow();
            if (next == PopupLoadAction.Post)
            {
                _pendingShow = null;
                Guarded(() => PostShow(show), "show the menu", show);
                return;
            }

            _readyTimer.Stop();
            _readyTimer.Start();
            if (next == PopupLoadAction.Init) _ = InitAsync();
            else if (next == PopupLoadAction.Reload) Guarded(() => _webView.CoreWebView2.Navigate(_pageUri), "reload the menu page", show);
        }

        private void PostShow(JsonObject show)
        {
            if (Math.Abs(_webView.ZoomFactor - _zoom) > 0.001) _webView.ZoomFactor = _zoom;
            _webView.CoreWebView2.PostWebMessageAsJson(show.ToJsonString());
        }

        // Every WebView2 call on the show path goes through here (see PopupWebViewGuard).
        private void Guarded(Action op, string what, JsonObject show)
        {
            PopupWebViewGuard.Run(op, what, _load, () => IsBroken = true, reason =>
            {
                _pendingShow = show; // so Fail reports it rather than only logging
                Fail(reason);
                if (Visible) Hide();
            });
        }

        /// <summary>Hides the popup and retires any show still in flight; the page stays loaded.</summary>
        public void HidePopup()
        {
            _pendingShow = null;
            _readyTimer.Stop();
            _requestId++;
            if (Visible) Hide();
        }

        /// <summary>Moves keyboard focus into an already-visible popup.</summary>
        public void FocusPage()
        {
            if (!Visible) return;
            Activate();
            _webView.Focus();
        }

        /// <summary>Scale of an item's top edge (CSS px) to a screen y inside this popup.</summary>
        public int ScreenYForCssTop(double cssTop) => Top + (int)Math.Round(cssTop * _scale);

        // Every failure is logged, including a warm-up nobody was waiting for; only a show the user
        // asked for by clicking or from the keyboard is reported (see ShouldReportFailure).
        private void Fail(string reason)
        {
            _readyTimer.Stop();
            _logError("HeaderPopup: " + reason);
            bool report = ShouldReportFailure(_pendingShow != null, userInitiated: _activateOnShow);
            _pendingShow = null;
            if (report) ShowFailed?.Invoke(reason);
        }

        /// <summary>
        /// Whether a failure is shown to the user (Run 3 adversary delta). Only a show that is still
        /// pending AND was asked for by a click or a key: a flyout opened because the mouse passed
        /// over Grid Layout must not raise a modal error box. It is logged and marked broken instead.
        /// </summary>
        public static bool ShouldReportFailure(bool hasPendingShow, bool userInitiated) => hasPendingShow && userInitiated;

        // The window's own colour shows for a moment before the page paints, and at the rounded edge.
        private void ApplyThemeColors(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.White;
            _webView.DefaultBackgroundColor = BackColor;
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Only the popup's own page may drive it (see WebViewPagePin).
            if (!WebViewPagePin.IsFromPage(e.Source, _pageUri))
            {
                _logError("HeaderPopup: ignored a message from " + e.Source);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;

                switch (typeEl.GetString())
                {
                    case "ready":
                        _load.Ready();
                        _readyTimer.Stop();
                        if (_pendingShow != null)
                        {
                            var show = _pendingShow;
                            _pendingShow = null;
                            Guarded(() => PostShow(show), "show the menu", show);
                        }
                        break;

                    case "size":
                        if (!root.TryGetProperty("requestId", out var idEl) || idEl.GetInt32() != _requestId) break;
                        CurrentView = root.TryGetProperty("view", out var v) ? v.GetString() : null;
                        var size = new Size(
                            (int)Math.Ceiling(root.GetProperty("width").GetDouble() * _scale),
                            (int)Math.Ceiling(root.GetProperty("height").GetDouble() * _scale));
                        Bounds = PopupPlacement.Place(_anchor, size, Screen.FromRectangle(_anchor).WorkingArea, _side);
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
                        bool open = root.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True;
                        double top = root.TryGetProperty("top", out var t) ? t.GetDouble() : 0;
                        bool focus = root.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.True;
                        SubmenuRequested?.Invoke(open, top, focus);
                        break;

                    case "dismiss":
                        DismissRequested?.Invoke(root.TryGetProperty("back", out var b) && b.ValueKind == JsonValueKind.True);
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException)
            {
                _logError("HeaderPopup: bad message from the page: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _readyTimer.Dispose();
                _webView.Dispose();
            }
            base.Dispose(disposing);
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
