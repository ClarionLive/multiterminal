using System;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Carries which HUD tab was zoomed, and to what (task 0d72698a).
    /// </summary>
    /// <remarks>
    /// <para>The tab identity is the whole point. The container previously reported zoom as a bare
    /// <c>double</c>, which is precisely why the value could only ever be stored in one global setting:
    /// the listener had no way to tell which tab the user had zoomed.</para>
    /// <para>Immutable by construction, following the convention set by the broker's event args — an
    /// earlier subscriber must not be able to rewrite the key or the value and redirect a later one at
    /// a different tab's setting.</para>
    /// </remarks>
    public sealed class HudTabZoomChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HudTabZoomChangedEventArgs"/> class.
        /// </summary>
        /// <param name="zoomKey">The persistence key for the tab (see <see cref="ZoomKey"/>).</param>
        /// <param name="zoom">The new zoom factor.</param>
        public HudTabZoomChangedEventArgs(string zoomKey, double zoom)
        {
            ZoomKey = zoomKey;
            Zoom = zoom;
        }

        /// <summary>
        /// Gets the persistence key for the tab that changed.
        /// </summary>
        /// <remarks>
        /// For permanent tabs this is the tab id itself (<c>__graph__</c>, <c>__git__</c>, ...). Every
        /// dynamic browser tab reports the single shared <see cref="HudTabContainer.BrowserZoomKey"/>
        /// instead of its own runtime id: browser tabs are opened with arbitrary ids and closed for
        /// good, so a key each would grow the settings file without bound for tabs that never reopen.
        /// </remarks>
        public string ZoomKey { get; }

        /// <summary>
        /// Gets the new zoom factor.
        /// </summary>
        public double Zoom { get; }
    }
}
