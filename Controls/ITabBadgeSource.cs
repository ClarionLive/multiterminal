using System;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// A HUD tab that can show a count on its tab header (task c133b27d: the Notes tab's unread agent
    /// notes, visible from whichever HUD tab is showing).
    /// </summary>
    /// <remarks>
    /// <para>Optional, unlike <see cref="IZoomableTab"/>: most tabs have nothing to count, so
    /// <see cref="HudTabContainer"/> hooks it with an <c>is</c> check in one place, next to its zoom hook,
    /// rather than requiring it. The container names no tab type, so a second tab that wants a badge only
    /// implements this; there is no chain to extend.</para>
    /// <para>The count must be valid before the tab is first shown: a WebView2 tab that initializes lazily
    /// has to compute it host-side, or the badge stays at zero until the Owner opens the very tab the
    /// badge exists to point them at.</para>
    /// </remarks>
    public interface ITabBadgeSource
    {
        /// <summary>The number to show; 0 shows no badge.</summary>
        int BadgeCount { get; }

        /// <summary>Raised on the UI thread after <see cref="BadgeCount"/> changes.</summary>
        event EventHandler BadgeCountChanged;
    }
}
