#nullable enable
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MultiTerminal.Docking;
using WeifenLuo.WinFormsUI.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="IndependentFloatWindow"/> (GH #74 phase 2) against a real DockPanelSuite DockPanel.
    /// The owner and minimise facts are checked next to the library's default window, so they cannot
    /// pass because the default already behaved that way. Falsified when written: keeping the main
    /// window as owner turned the unowned and minimise facts red; recreating the window on layout
    /// turned the child-handle fact red; not forwarding keys turned the shortcut fact red; never moving
    /// a window turned the two off-screen facts red. After pipeline run 1: ignoring which window a key
    /// came from, surfacing without the restore, and dropping the icon swap each turned their one fact
    /// red; the old "any overlap" reachability rule turned the top-edge and near-bottom facts red (the
    /// sliver fact stayed green, as predicted: the old rule also rejected a 20-pixel overlap). Phase 3: not
    /// routing WM_CLOSE to the handler turned the close-button fact red (the declined-close fact stayed
    /// green, as predicted: the library closes the panes either way).
    ///
    /// <para>The child-handle fact stands in for WebView2: a terminal survives a move only if the
    /// native windows inside the pane are re-parented, never destroyed and rebuilt. A plain child
    /// control has the same failure mode (its handle changes) without starting a browser.</para>
    ///
    /// <para>WHAT IS NOT COVERED: the taskbar button and Alt+Tab entry themselves (the facts check the
    /// window styles Windows decides them by), snapping, multi-monitor DPI, and a real WebView2 or
    /// shell inside the window. Those are live checks.</para>
    /// </summary>
    public class IndependentFloatWindowTests
    {
        private const int GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const long WS_MINIMIZEBOX = 0x00020000;
        private const long WS_MAXIMIZEBOX = 0x00010000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private static long Style(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();

        private static long ExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

        private sealed class Pane : DockContent
        {
            public Pane()
            {
                DockAreas = DockAreas.Document | DockAreas.Float;
                Child = new Panel { Dock = DockStyle.Fill };
                Controls.Add(Child);
            }

            public Panel Child { get; }
        }

        private static readonly Rectangle FloatBounds = new Rectangle(-19000, -19000, 400, 300);

        private static void RunOnSta(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA test thread did not finish.");
            failure?.Throw();
        }

        private static void WithHost(bool independent, Action<Form, DockPanel, IndependentFloatWindowFactory?> body, Func<Form, Keys, bool>? shortcuts = null, Func<FloatWindow, bool>? close = null)
        {
            RunOnSta(() =>
            {
                var form = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20000, -20000),
                    Size = new Size(800, 600),
                };
                var panel = new DockPanel { Dock = DockStyle.Fill, Theme = new VS2015DarkTheme(), DocumentStyle = DocumentStyle.DockingWindow };
                form.Controls.Add(panel);
                IndependentFloatWindowFactory? factory = null;
                if (independent)
                {
                    factory = new IndependentFloatWindowFactory(shortcuts, close);
                    factory.InstallOn(panel);
                }

                form.Show();
                try
                {
                    body(form, panel, factory);
                }
                finally
                {
                    form.Close();
                    form.Dispose();
                }
            });
        }

        [Fact]
        public void The_library_default_window_is_an_owned_tool_window()
        {
            WithHost(false, (form, panel, _) =>
            {
                var pane = new Pane();
                pane.Show(panel, FloatBounds);
                var fw = pane.Pane!.FloatWindow!;

                Assert.IsNotType<IndependentFloatWindow>(fw);
                Assert.Equal(form.Handle, GetWindow(fw.Handle, GW_OWNER));
                Assert.NotEqual(0, ExStyle(fw.Handle) & WS_EX_TOOLWINDOW);
            });
        }

        [Fact]
        public void A_floated_pane_gets_an_unowned_application_window_with_minimise_and_maximise()
        {
            WithHost(true, (form, panel, _) =>
            {
                var pane = new Pane();
                pane.Show(panel, FloatBounds);
                var fw = pane.Pane!.FloatWindow!;

                Assert.IsType<IndependentFloatWindow>(fw);
                Assert.Null(fw.Owner);
                Assert.Equal(IntPtr.Zero, GetWindow(fw.Handle, GW_OWNER));
                Assert.Equal(0, ExStyle(fw.Handle) & WS_EX_TOOLWINDOW);
                Assert.NotEqual(0, ExStyle(fw.Handle) & WS_EX_APPWINDOW);
                Assert.NotEqual(0, Style(fw.Handle) & WS_MINIMIZEBOX);
                Assert.NotEqual(0, Style(fw.Handle) & WS_MAXIMIZEBOX);
            });
        }

        /// <summary>
        /// The window and the native child inside the pane keep their handles through float, dock and
        /// float again: the pane is re-parented, not rebuilt. A recreated handle here is what would
        /// destroy a terminal's WebView2.
        /// </summary>
        [Fact]
        public void Moving_a_pane_between_the_main_window_and_floating_windows_never_rebuilds_its_native_child()
        {
            WithHost(true, (form, panel, _) =>
            {
                var pane = new Pane();
                pane.Show(panel, DockState.Document);
                IntPtr child = pane.Child.Handle;
                int childCreations = 0;
                pane.Child.HandleCreated += (s, e) => childCreations++;

                pane.Show(panel, FloatBounds);
                var fw = pane.Pane!.FloatWindow!;
                IntPtr window = fw.Handle;
                int windowCreations = 0;
                fw.HandleCreated += (s, e) => windowCreations++;
                Application.DoEvents();
                Assert.Equal(child, pane.Child.Handle);
                Assert.Equal(window, fw.Handle);

                pane.DockState = DockState.Document;
                Application.DoEvents();
                Assert.Equal(child, pane.Child.Handle);

                pane.DockState = DockState.Float;
                Application.DoEvents();
                Assert.Equal(child, pane.Child.Handle);
                Assert.Equal(0, childCreations);
                Assert.Equal(0, windowCreations);
            });
        }

        [Fact]
        public void Minimising_the_main_window_leaves_an_independent_window_showing_but_hides_an_owned_one()
        {
            foreach (bool independent in new[] { false, true })
            {
                WithHost(independent, (form, panel, _) =>
                {
                    var pane = new Pane();
                    pane.Show(panel, FloatBounds);
                    var fw = pane.Pane!.FloatWindow!;
                    Assert.True(IsWindowVisible(fw.Handle));

                    form.WindowState = FormWindowState.Minimized;
                    Application.DoEvents();

                    Assert.Equal(independent, IsWindowVisible(fw.Handle));
                    if (independent) Assert.False(IsIconic(fw.Handle));
                });
            }
        }

        [Fact]
        public void A_shortcut_pressed_in_a_floating_window_reaches_the_main_window_handler_with_that_window()
        {
            Keys? seen = null;
            Form? from = null;
            WithHost(
                true,
                (form, panel, _) =>
                {
                    var pane = new Pane();
                    pane.Show(panel, FloatBounds);
                    var fw = pane.Pane!.FloatWindow!;

                    var method = typeof(IndependentFloatWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(method);
                    var msg = Message.Create(fw.Handle, 0x0100, IntPtr.Zero, IntPtr.Zero);
                    object[] args = { msg, Keys.Control | Keys.Shift | Keys.H };

                    Assert.True((bool)method!.Invoke(fw, args)!);
                    Assert.Equal(Keys.Control | Keys.Shift | Keys.H, seen);
                    Assert.Same(fw, from);
                },
                shortcuts: (window, k) =>
                {
                    from = window;
                    seen = k;
                    return k == (Keys.Control | Keys.Shift | Keys.H);
                });
        }

        /// <summary>
        /// The factory lives on the theme: assigning a new theme brings back the library default, so
        /// MainForm must install the factory again after each assignment.
        /// </summary>
        [Fact]
        public void A_new_theme_drops_the_factory_until_it_is_installed_again()
        {
            WithHost(true, (form, panel, factory) =>
            {
                Assert.Same(factory, panel.Theme.Extender.FloatWindowFactory);

                panel.Theme = new VS2015LightTheme();
                Assert.NotSame(factory, panel.Theme.Extender.FloatWindowFactory);

                factory!.InstallOn(panel);
                Assert.Same(factory, panel.Theme.Extender.FloatWindowFactory);
            });
        }

        /// <summary>
        /// The library creates a restored floating window with its saved bounds (the same constructor
        /// as a pane floated to a position). Bounds on no screen come back onto one with this window;
        /// the library's own only pulls them back vertically, so a window off to the side stays lost.
        /// </summary>
        [Fact]
        public void A_window_created_off_every_screen_comes_back_onto_one_unlike_the_library_default()
        {
            foreach (bool independent in new[] { false, true })
            {
                WithHost(independent, (form, panel, _) =>
                {
                    var pane = new Pane();
                    pane.Show(panel, FloatBounds);
                    var bounds = pane.Pane!.FloatWindow!.Bounds;

                    bool onAScreen = Array.Exists(Screen.AllScreens, s => s.WorkingArea.IntersectsWith(bounds));
                    Assert.True(onAScreen == independent, $"independent={independent} bounds={bounds}");
                });
            }
        }

        /// <summary>
        /// The styles are set while the window does not exist yet. If a DockPanelSuite upgrade creates
        /// the window inside its constructor, this goes red rather than the code quietly falling back.
        /// </summary>
        [Fact]
        public void The_library_has_not_created_the_window_when_its_styles_are_set()
        {
            WithHost(true, (form, panel, _) =>
            {
                var docked = new Pane();
                docked.Show(panel, DockState.Document);
                var floated = new Pane();
                floated.Show(panel, FloatBounds);
                var dragged = new Pane();
                dragged.Show(panel, DockState.Document);
                dragged.DockState = DockState.Float;

                Assert.True(((IndependentFloatWindow)floated.Pane!.FloatWindow!).StylesSetBeforeWindowExisted);
                Assert.True(((IndependentFloatWindow)dragged.Pane!.FloatWindow!).StylesSetBeforeWindowExisted);
            });
        }

        /// <summary>
        /// Surfacing a pane in a minimised floating window brings the window back; activating it, which
        /// is all MT used to do, leaves it minimised (pipeline debugger).
        /// </summary>
        [Fact]
        public void Surfacing_a_pane_restores_its_minimised_window_where_activating_it_does_not()
        {
            WithHost(true, (form, panel, _) =>
            {
                var pane = new Pane();
                pane.Show(panel, FloatBounds);
                var fw = pane.Pane!.FloatWindow!;

                fw.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                pane.Activate();
                Application.DoEvents();
                Assert.True(IsIconic(fw.Handle), "activating alone should leave the window minimised (control)");

                GridLayoutManager.Surface(pane);
                Application.DoEvents();
                Assert.False(IsIconic(fw.Handle));
            });
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_GETICON = 0x007F;

        /// <summary>
        /// The library gives a floating window its active tab's icon (for a terminal, the attention
        /// dot), on layout and on every tab switch. The window keeps the main window's icon through a
        /// tab switch; the library default shows the tab's (pipeline debugger).
        /// </summary>
        [Fact]
        public void A_floating_window_keeps_the_main_window_icon_through_a_tab_switch_unlike_the_library_default()
        {
            foreach (bool independent in new[] { false, true })
            {
                WithHost(independent, (form, panel, _) =>
                {
                    form.Icon = SystemIcons.Shield;
                    panel.ShowDocumentIcon = true;
                    var first = new Pane { Icon = SystemIcons.Error };
                    first.Show(panel, FloatBounds);
                    var fw = first.Pane!.FloatWindow!;
                    var second = new Pane { Icon = SystemIcons.Warning };
                    second.Show(first.Pane, null);
                    second.Activate();
                    first.Activate();
                    Application.DoEvents();

                    IntPtr big = SendMessage(fw.Handle, WM_GETICON, (IntPtr)1, IntPtr.Zero);
                    IntPtr small = SendMessage(fw.Handle, WM_GETICON, IntPtr.Zero, IntPtr.Zero);
                    Assert.True((big == form.Icon.Handle) == independent, $"big: independent={independent}");
                    Assert.True((small == form.Icon.Handle) == independent, $"small: independent={independent}");
                });
            }
        }

        /// <summary>
        /// A shortcut acts only on a pane in the window it was pressed in (pipeline security finding):
        /// from a floating window, its own active pane or nothing; from the main window, a docked pane,
        /// never the floated one the library still reports as active.
        /// </summary>
        [Fact]
        public void A_shortcut_target_is_always_a_pane_in_the_window_the_key_was_pressed_in()
        {
            WithHost(true, (form, panel, _) =>
            {
                var docked = new Pane();
                docked.Show(panel, DockState.Document);
                var floated = new Pane();
                floated.Show(panel, FloatBounds);
                var fw = floated.Pane!.FloatWindow!;

                floated.Activate();
                Application.DoEvents();
                Assert.Same(floated, panel.ActiveContent);
                Assert.Same(floated, GridLayoutManager.ActiveContentIn<Pane>(panel, fw));
                Assert.Same(docked, GridLayoutManager.ActiveContentIn<Pane>(panel, form));

                docked.Activate();
                Application.DoEvents();
                Assert.Same(docked, panel.ActiveContent);
                Assert.Null(GridLayoutManager.ActiveContentIn<Pane>(panel, fw));
                Assert.Same(docked, GridLayoutManager.ActiveContentIn<Pane>(panel, form));
            });
        }

        /// <summary>
        /// Phase 3: the close button hands the window to MainForm, which closes its terminals as one
        /// batch. While the handler holds the close (a Cancel), the library must not close the panes
        /// one by one behind it, which is what it does on its own.
        /// </summary>
        [Fact]
        public void The_close_button_goes_to_the_handler_and_the_library_closes_nothing_when_it_holds_the_close()
        {
            FloatWindow? handedOver = null;
            WithHost(
                true,
                (form, panel, _) =>
                {
                    var first = new Pane();
                    first.Show(panel, FloatBounds);
                    var fw = first.Pane!.FloatWindow!;
                    var second = new Pane();
                    second.Show(first.Pane, null);

                    fw.Close();
                    Application.DoEvents();

                    Assert.Same(fw, handedOver);
                    Assert.False(first.IsDisposed);
                    Assert.False(second.IsDisposed);
                    Assert.Same(panel, first.DockPanel);
                    Assert.Same(panel, second.DockPanel);
                },
                close: window =>
                {
                    handedOver = window;
                    return true;
                });
        }

        /// <summary>A handler that declines (MT shutting down) leaves the close to the library, which closes the panes.</summary>
        [Fact]
        public void A_declined_close_is_left_to_the_library_which_closes_the_panes()
        {
            WithHost(
                true,
                (form, panel, _) =>
                {
                    var pane = new Pane();
                    pane.Show(panel, FloatBounds);

                    pane.Pane!.FloatWindow!.Close();
                    Application.DoEvents();

                    Assert.True(pane.IsDisposed || pane.DockPanel == null);
                },
                close: _ => false);
        }

        private static readonly Rectangle Primary = new Rectangle(0, 0, 1920, 1040);
        private static readonly Rectangle Right = new Rectangle(1920, 0, 2560, 1400);

        [Fact]
        public void A_window_on_a_screen_is_left_where_it_is()
        {
            var bounds = new Rectangle(2200, 300, 900, 600);
            Assert.Equal(bounds, IndependentFloatWindow.KeepReachable(bounds, new[] { Primary, Right }));
        }

        [Fact]
        public void A_window_partly_off_screen_with_its_title_bar_reachable_is_left_alone()
        {
            var bounds = new Rectangle(1500, 900, 900, 600);
            Assert.Equal(bounds, IndependentFloatWindow.KeepReachable(bounds, new[] { Primary }));
        }

        /// <summary>The window was on a second monitor that has been unplugged.</summary>
        [Fact]
        public void A_window_on_a_monitor_that_is_gone_moves_onto_the_nearest_screen()
        {
            var placed = IndependentFloatWindow.KeepReachable(new Rectangle(2200, 300, 900, 600), new[] { Primary });

            Assert.Equal(new Rectangle(1020, 300, 900, 600), placed);
            Assert.True(Primary.Contains(placed));
        }

        [Fact]
        public void A_window_bigger_than_the_screen_it_moves_to_is_shrunk_to_fit()
        {
            var placed = IndependentFloatWindow.KeepReachable(new Rectangle(-5000, -5000, 2500, 1500), new[] { Primary });
            Assert.Equal(Primary, placed);
        }

        /// <summary>
        /// The adversary's case: a title bar one pixel onto the screen is not a title bar anyone can
        /// grab. The window's top edge must be on the screen.
        /// </summary>
        [Fact]
        public void A_window_whose_top_edge_is_above_the_screen_is_moved_down_onto_it()
        {
            var placed = IndependentFloatWindow.KeepReachable(new Rectangle(100, -31, 900, 600), new[] { Primary });
            Assert.Equal(new Rectangle(100, 0, 900, 600), placed);
        }

        [Fact]
        public void A_window_with_only_a_sliver_of_its_title_bar_on_the_screen_is_moved_onto_it()
        {
            var placed = IndependentFloatWindow.KeepReachable(new Rectangle(1900, 100, 900, 600), new[] { Primary });
            Assert.Equal(new Rectangle(1020, 100, 900, 600), placed);
        }

        [Fact]
        public void A_window_whose_top_edge_is_too_close_to_the_bottom_of_the_screen_is_moved_up()
        {
            var placed = IndependentFloatWindow.KeepReachable(new Rectangle(100, 1030, 900, 600), new[] { Primary });
            Assert.Equal(new Rectangle(100, 440, 900, 600), placed);
        }

        [Fact]
        public void With_no_screens_reported_the_bounds_are_unchanged()
        {
            var bounds = new Rectangle(-5000, -5000, 400, 300);
            Assert.Equal(bounds, IndependentFloatWindow.KeepReachable(bounds, Array.Empty<Rectangle>()));
        }
    }
}
