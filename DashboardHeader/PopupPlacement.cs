using System;
using System.Drawing;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>Which side of its anchor a header popup opens on.</summary>
    public enum PopupSide
    {
        /// <summary>Below the anchor, left edges aligned (the M menu, the project picker).</summary>
        Below,

        /// <summary>To the right of the anchor, top edges aligned (the Grid Layout flyout).</summary>
        Right,
    }

    /// <summary>
    /// Where a header popup goes on screen (task 4cac608c). Pure, so the placement rules are testable
    /// without a window: open on the preferred side, flip to the opposite side when that runs off the
    /// working area, then clamp so the whole popup stays visible.
    /// </summary>
    public static class PopupPlacement
    {
        /// <summary>Gap between the anchor and the popup, in physical pixels.</summary>
        public const int Gap = 4;

        public static Rectangle Place(Rectangle anchor, Size size, Rectangle workingArea, PopupSide side)
        {
            int x, y;
            if (side == PopupSide.Below)
            {
                x = anchor.Left;
                y = anchor.Bottom + Gap;
                if (y + size.Height > workingArea.Bottom)
                    y = anchor.Top - Gap - size.Height;
            }
            else
            {
                x = anchor.Right + Gap;
                y = anchor.Top;
                if (x + size.Width > workingArea.Right)
                    x = anchor.Left - Gap - size.Width;
            }

            x = Clamp(x, workingArea.Left, workingArea.Right - size.Width);
            y = Clamp(y, workingArea.Top, workingArea.Bottom - size.Height);
            return new Rectangle(x, y, size.Width, size.Height);
        }

        // Math.Clamp throws when max < min, which happens when the popup is larger than the working
        // area; pin it to the top-left edge instead so at least its start is visible.
        private static int Clamp(int value, int min, int max) => max < min ? min : Math.Min(Math.Max(value, min), max);
    }
}
