using System;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// A HUD tab whose zoom the container can both apply and observe (task 0d72698a).
    /// </summary>
    /// <remarks>
    /// <para>This exists to close a defect rather than to abstract for its own sake. Before it,
    /// <see cref="HudTabContainer.SetZoomFactor(double)"/> was an <c>else if (tab.Control is XRenderer)</c>
    /// chain naming every tab type explicitly, and the subscription side of the same relationship was
    /// simply never written for six of the seven tabs — every one of them raised <see cref="ZoomChanged"/>
    /// into a listener that did not exist, so zoom held for a session and vanished on restart.</para>
    /// <para>A hand-maintained list of tab types is a code shape that fails silently: a new tab that
    /// nobody adds to it does not error, it just quietly behaves differently from its siblings. Both
    /// halves of the relationship now travel together on one interface, so a tab that does not satisfy
    /// it cannot be handed to the container at all — the omission becomes a compile error instead of a
    /// bug report months later.</para>
    /// <para>NOT covered: <c>HudTabContainer.ApplyTheme</c> keeps the same chain shape and the same
    /// hazard. That is deliberately a separate concern and a separate ticket; this interface reduces
    /// the trap, it does not remove it.</para>
    /// </remarks>
    public interface IZoomableTab
    {
        /// <summary>
        /// Raised when the user changes this tab's zoom, so the container can persist the factor.
        /// </summary>
        event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Applies a zoom factor to this tab.
        /// </summary>
        /// <param name="zoom">The zoom factor to apply.</param>
        void SetZoomFactor(double zoom);
    }
}
