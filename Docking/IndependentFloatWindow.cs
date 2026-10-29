#nullable enable
using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// A DockPanelSuite floating window that behaves like an ordinary application window (GH #74,
    /// phase 2): its own taskbar button and Alt+Tab entry, minimise and maximise buttons, a normal
    /// border, and no owner, so it minimises, restores and stacks independently of the main window.
    /// Everything else (docking, tab drag between windows, persistence) is the library's own
    /// <see cref="FloatWindow"/>, created through <see cref="IndependentFloatWindowFactory"/>.
    ///
    /// <para><b>Styles are changed only before the window exists.</b> The base constructor sets
    /// <c>FormBorderStyle.SizableToolWindow</c>, <c>ShowInTaskbar = false</c> and
    /// <c>Owner = main window</c>. Changing <c>ShowInTaskbar</c> or the border on a CREATED window makes
    /// WinForms destroy and rebuild it, children included, which would tear down the WebView2 of every
    /// terminal inside. Measured with DockPanelSuite 3.1.1: the window does not exist yet when this
    /// constructor body runs, so the properties are set here, and only while
    /// <see cref="Control.IsHandleCreated"/> is false. <see cref="StylesSetBeforeWindowExisted"/>
    /// records which way it went, and a test pins it, so a library upgrade that creates the window
    /// sooner fails a test instead of passing quietly.</para>
    ///
    /// <para><see cref="CreateParams"/> is the fallback for a window that already exists: it is then
    /// created with the right styles and no owner. Whether it STAYS unowned on that path was not
    /// measured, and no test can reach it: in an earlier version that set only <c>Owner = null</c>,
    /// WinForms re-applied its hidden owner window (the one it gives a form with
    /// <c>ShowInTaskbar = false</c>) after creation. Such a window would still minimise on its own.</para>
    ///
    /// <para><b>Known limit:</b> a window maximised when the layout is saved comes back as a normal
    /// window at its maximised size. DockPanelSuite's layout file keeps bounds only (it special-cases
    /// a minimised window, saving its restore bounds), not the window state.</para>
    /// </summary>
    public class IndependentFloatWindow : FloatWindow
    {
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WM_SETICON = 0x0080;

        /// <summary>How far below the window's top edge must also lie on the same screen: the title
        /// bar, generously (a caption is about 23 pixels at 100% scaling and grows with it).</summary>
        internal const int MinimumVisibleTitleHeight = 48;

        /// <summary>How much of the title bar must be on that screen, side to side, to grab it.</summary>
        internal const int MinimumVisibleTitleWidth = 120;

        private readonly Func<Form, Keys, bool>? _shortcutHandler;

        public IndependentFloatWindow(DockPanel dockPanel, DockPane pane, Func<Form, Keys, bool>? shortcutHandler)
            : base(dockPanel, pane)
        {
            _shortcutHandler = shortcutHandler;
            StylesSetBeforeWindowExisted = BecomeIndependent();
        }

        public IndependentFloatWindow(DockPanel dockPanel, DockPane pane, Rectangle bounds, Func<Form, Keys, bool>? shortcutHandler)
            : base(dockPanel, pane, bounds)
        {
            _shortcutHandler = shortcutHandler;
            StylesSetBeforeWindowExisted = BecomeIndependent();
        }

        /// <summary>
        /// True when the constructor found no window yet and set the taskbar and border properties
        /// directly; false when it had to rely on <see cref="CreateParams"/> alone.
        /// </summary>
        internal bool StylesSetBeforeWindowExisted { get; }

        private bool BecomeIndependent()
        {
            // The base constructor made the main window the owner. Owned windows sit above their
            // owner and are hidden when it minimises; this window must do neither.
            Owner = null;
            if (IsHandleCreated) return false;

            // With no owner and ShowInTaskbar false, WinForms would give the window a hidden owner of
            // its own; true gives it a taskbar button and no owner at all.
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            return true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;

                // Not a tool window: a tool window has no taskbar button, is left out of Alt+Tab and
                // has no minimise or maximise.
                cp.ExStyle &= ~WS_EX_TOOLWINDOW;
                cp.ExStyle |= WS_EX_APPWINDOW;

                // Minimise and maximise belong to the system menu; the library turns the whole control
                // box off when no content in the window can be closed, and that is respected.
                if ((cp.Style & WS_SYSMENU) != 0)
                    cp.Style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

                // No owner window at creation, including WinForms' hidden taskbar-owner window, which
                // it uses for a top-level form with ShowInTaskbar = false.
                cp.Parent = IntPtr.Zero;
                return cp;
            }
        }

        /// <summary>
        /// The window and its taskbar button show the main window's icon. The library sets the icon
        /// from the active tab's content, both on layout and when the active tab changes, and for a
        /// terminal that is its attention dot. Every icon the window is given is therefore swapped at
        /// the one place they all pass through, the WM_SETICON message.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETICON)
            {
                var appIcon = DockPanel?.FindForm()?.Icon;
                if (appIcon != null)
                    m.LParam = appIcon.Handle;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Keyboard shortcuts the main window handles (Ctrl+Shift+H, ...). Keys reach the top-level
        /// form's <c>ProcessCmdKey</c>, which for a pane in this window is this window, so they are
        /// handed over together with this window: a shortcut must act on a pane IN this window, never
        /// on whatever the main window has active.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_shortcutHandler != null && _shortcutHandler(this, keyData))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Keeps the window reachable (GH #74). A saved layout can place it on a monitor that is no
        /// longer attached, or with its title bar above the top of a screen; the library only pulls it
        /// back into the virtual screen vertically. See <see cref="KeepReachable"/>.
        /// </summary>
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if ((specified & BoundsSpecified.Location) != 0)
            {
                var placed = KeepReachable(new Rectangle(x, y, width, height), WorkingAreas());
                x = placed.X;
                y = placed.Y;
                width = placed.Width;
                height = placed.Height;
            }

            base.SetBoundsCore(x, y, width, height, specified);
        }

        private static Rectangle[] WorkingAreas()
        {
            var screens = Screen.AllScreens;
            var areas = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea;
            return areas;
        }

        /// <summary>
        /// Pure. <paramref name="bounds"/> is left alone when its title bar can be grabbed: the top edge
        /// lies inside one working area with <see cref="MinimumVisibleTitleHeight"/> below it in that
        /// same area, and at least <see cref="MinimumVisibleTitleWidth"/> of the width overlaps it. A
        /// window whose top edge is even one pixel above every screen is not reachable, because the top
        /// edge IS the title bar. Otherwise the window is moved, and shrunk if it does not fit, into the
        /// working area nearest to its top-left corner. No working areas at all leaves it unchanged.
        /// </summary>
        internal static Rectangle KeepReachable(Rectangle bounds, Rectangle[] workingAreas)
        {
            if (workingAreas == null || workingAreas.Length == 0) return bounds;

            foreach (var area in workingAreas)
            {
                bool topInside = bounds.Top >= area.Top && (long)bounds.Top + MinimumVisibleTitleHeight <= area.Bottom;
                long overlap = (long)Math.Min(bounds.Right, area.Right) - Math.Max(bounds.Left, area.Left);
                if (topInside && overlap >= Math.Min(MinimumVisibleTitleWidth, Math.Max(1, bounds.Width)))
                    return bounds;
            }

            Rectangle target = workingAreas[0];
            long best = long.MaxValue;
            foreach (var area in workingAreas)
            {
                long dx = Math.Max(0, Math.Max((long)area.Left - bounds.Left, (long)bounds.Left - area.Right));
                long dy = Math.Max(0, Math.Max((long)area.Top - bounds.Top, (long)bounds.Top - area.Bottom));
                long distance = (dx * dx) + (dy * dy);
                if (distance < best)
                {
                    best = distance;
                    target = area;
                }
            }

            int width = Math.Min(bounds.Width, target.Width);
            int height = Math.Min(bounds.Height, target.Height);
            int x = Math.Min(Math.Max(bounds.X, target.Left), target.Right - width);
            int y = Math.Min(Math.Max(bounds.Y, target.Top), target.Bottom - height);
            return new Rectangle(x, y, width, height);
        }
    }

    /// <summary>
    /// Supplies <see cref="IndependentFloatWindow"/> to DockPanelSuite (GH #74). The factory lives on
    /// the dock panel's THEME (<c>Theme.Extender.FloatWindowFactory</c>), so it must be installed again
    /// after every theme assignment; a new theme brings the library's default factory with it.
    /// </summary>
    public sealed class IndependentFloatWindowFactory : DockPanelExtender.IFloatWindowFactory
    {
        private readonly Func<Form, Keys, bool>? _shortcutHandler;

        /// <param name="shortcutHandler">The main window's shortcut handler, given the window the key
        /// was pressed in; true when it handled the key.</param>
        public IndependentFloatWindowFactory(Func<Form, Keys, bool>? shortcutHandler)
        {
            _shortcutHandler = shortcutHandler;
        }

        public FloatWindow CreateFloatWindow(DockPanel dockPanel, DockPane pane)
            => new IndependentFloatWindow(dockPanel, pane, _shortcutHandler);

        public FloatWindow CreateFloatWindow(DockPanel dockPanel, DockPane pane, Rectangle bounds)
            => new IndependentFloatWindow(dockPanel, pane, bounds, _shortcutHandler);

        /// <summary>Installs this factory on <paramref name="dockPanel"/>'s current theme.</summary>
        public void InstallOn(DockPanel dockPanel)
        {
            if (dockPanel?.Theme != null)
                dockPanel.Theme.Extender.FloatWindowFactory = this;
        }
    }
}
