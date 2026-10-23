#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Closes a <see cref="ToolStripDropDown"/> when a mouse button goes down outside it, including over
    /// a WebView2 (task df58b35d, Owner live test 2026-10-23).
    ///
    /// <para><b>Why this exists:</b> WinForms closes an open menu when its message filter sees a click in
    /// another window of this thread. A WebView2's input windows belong to the browser process, so a click
    /// on a terminal, the status bar or any other web panel never reaches that filter and the menu stays
    /// up with no way to dismiss it except picking an item. The terminal's own context menu gets round
    /// this with a page message (<c>terminalclick</c>), which only covers that one pane; a menu shown from
    /// the status bar needs every surface, so this watches the button state system-wide instead.</para>
    ///
    /// <para>It polls only while the menu is open (a 50ms WinForms timer, started on Opened and disposed
    /// on Closed), so it costs nothing the rest of the time.</para>
    /// </summary>
    internal static class OutsideClickMenuCloser
    {
        private const int PollIntervalMs = 50;
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;
        private const int VK_MBUTTON = 0x04;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// One poll's decision. <paramref name="armed"/> starts false so the press that opened the menu,
        /// which may still be held when it appears, is not taken as a click away: the closer arms only
        /// once every button is up. A press over the menu itself is the menu's own business.
        /// </summary>
        internal static bool Step(ref bool armed, bool anyButtonDown, bool cursorOverMenu)
        {
            if (!armed)
            {
                armed = !anyButtonDown;
                return false;
            }

            return anyButtonDown && !cursorOverMenu;
        }

        /// <summary>
        /// Watches <paramref name="menu"/> from when it opens until it closes, and closes it on a press
        /// outside it. <paramref name="closedByOutsideClick"/> runs just before that close.
        /// </summary>
        internal static void Attach(ToolStripDropDown menu, Action? closedByOutsideClick = null)
        {
            // CA2000: the timer is owned by the menu's Closed handler below, which disposes it.
#pragma warning disable CA2000
            var timer = new Timer { Interval = PollIntervalMs };
#pragma warning restore CA2000
            bool armed = false;

            timer.Tick += (s, e) =>
            {
                if (menu.IsDisposed || !menu.Visible) return;
                bool overMenu = menu.Bounds.Contains(Cursor.Position);
                if (Step(ref armed, AnyButtonDown(), overMenu))
                {
                    timer.Stop();
                    closedByOutsideClick?.Invoke();
                    menu.Close(ToolStripDropDownCloseReason.AppClicked);
                }
            };
            menu.Opened += (s, e) => timer.Start();
            menu.Closed += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
            };
        }

        // The high bit is "down now"; the physical buttons are checked so swapped buttons do not matter.
        private static bool AnyButtonDown() =>
            (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
            || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
            || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
    }
}
