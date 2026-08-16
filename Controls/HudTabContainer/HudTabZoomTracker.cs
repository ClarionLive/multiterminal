using System;
using System.Collections.Generic;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Decides which HUD tab a zoom belongs to, and whether a reported zoom is real or an echo
    /// (task 0d72698a).
    /// </summary>
    /// <remarks>
    /// <para>Pure state, no UI. Extracted from <see cref="HudTabContainer"/> so the two rules that are
    /// easy to get quietly wrong — the browser-tab bucket and the echo guard — can be tested without
    /// constructing a WinForms control tree and a WebView2. Follows the same shape as the codebase's
    /// other pure helpers (ChecklistGraphBuilder, GlossBackfillPlanner, StartupPortContentionClassifier).</para>
    /// <para>TWO DIFFERENT IDENTITIES, deliberately. The echo guard is keyed by TAB
    /// (<c>tabId</c>) because "has this control already been given this value" is a per-control
    /// question. Persistence is keyed by <see cref="KeyFor"/>, which collapses every browser tab onto
    /// one bucket. Conflating them was a real lost-write bug: with the guard keyed by the shared
    /// bucket, zooming browser tab B to the value tab A last reported was mistaken for A's echo and
    /// silently dropped — the screen showed the new size while settings kept the old one.</para>
    /// </remarks>
    internal sealed class HudTabZoomTracker
    {
        /// <summary>The shared persistence key used by every dynamic browser tab.</summary>
        public const string BrowserZoomKey = "__browser__";

        /// <summary>Difference below which two zoom factors are treated as the same value.</summary>
        private const double ZoomEpsilon = 0.005;

        private readonly Dictionary<string, double> _last =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maps a tab to the key its zoom is stored under.
        /// </summary>
        /// <param name="isPermanent">Whether the tab is one of the fixed HUD tabs.</param>
        /// <param name="tabId">The tab's id.</param>
        /// <returns>The tab id for permanent tabs; the shared browser bucket otherwise.</returns>
        /// <remarks>
        /// Browser tabs deliberately collapse onto one key. They are opened with arbitrary runtime ids
        /// and closed for good, so a key each would grow settings.txt without bound with entries for
        /// tabs that will never reopen.
        /// </remarks>
        public static string KeyFor(bool isPermanent, string tabId) =>
            isPermanent ? tabId : BrowserZoomKey;

        /// <summary>
        /// Records a zoom the app itself applied to ONE tab, so the resulting echo is not mistaken for
        /// user intent.
        /// </summary>
        /// <param name="tabId">The tab's own id — NOT its persistence key.</param>
        /// <param name="zoom">The zoom factor being applied.</param>
        public void Record(string tabId, double zoom)
        {
            if (string.IsNullOrEmpty(tabId)) return;
            _last[tabId] = zoom;
        }

        /// <summary>
        /// Decides whether a zoom reported by a tab is a real change worth persisting.
        /// </summary>
        /// <param name="tabId">The tab's own id — NOT its persistence key.</param>
        /// <param name="zoom">The reported zoom factor.</param>
        /// <returns>True when this is a genuine change; false when it merely repeats the last value.</returns>
        /// <remarks>
        /// <para>Comparing VALUES rather than setting a "currently applying" flag is deliberate. The
        /// renderers disagree about whether they subscribe to WebView2's ZoomFactorChanged before or
        /// after applying a pending zoom, and a zoom applied before initialisation is replayed later —
        /// so the echo can arrive long after the call that caused it, when any synchronous flag would
        /// already be clear. A value check does not care about ordering or timing.</para>
        /// <para>Keyed by TAB, never by persistence key. Several browser tabs share one persistence
        /// bucket, so a bucket-keyed guard compares one tab's new value against a DIFFERENT tab's last
        /// value and drops a genuine user zoom as though it were an echo.</para>
        /// </remarks>
        public bool ShouldReport(string tabId, double zoom)
        {
            if (string.IsNullOrEmpty(tabId)) return false;
            if (_last.TryGetValue(tabId, out var last) && Math.Abs(last - zoom) < ZoomEpsilon)
            {
                return false;
            }

            _last[tabId] = zoom;
            return true;
        }

        /// <summary>
        /// Drops a closed tab's remembered zoom.
        /// </summary>
        /// <param name="tabId">The tab's own id.</param>
        /// <remarks>
        /// Re-keying this guard from persistence key to tab id turned a bounded key space (permanent
        /// tabs + one browser bucket) into an unbounded one: every browser tab opened and closed left a
        /// dead entry for the life of the process. That is the same unbounded-growth argument used to
        /// justify the shared browser bucket in settings, so it would have been inconsistent to accept
        /// it in memory. The tab's PERSISTENCE key is deliberately not forgotten — the bucket value has
        /// to survive a moment with no browser tab open, which is what lets a later tab adopt it.
        /// </remarks>
        public void Forget(string tabId)
        {
            if (string.IsNullOrEmpty(tabId)) return;
            _last.Remove(tabId);
        }

        /// <summary>
        /// Gets the zoom last applied to or reported for a tab, if any.
        /// </summary>
        /// <param name="tabId">The tab's own id — NOT its persistence key.</param>
        /// <param name="zoom">The remembered zoom.</param>
        /// <returns>True when a zoom is known for the tab.</returns>
        /// <remarks>
        /// No production caller: the container adopts a newly-opened tab's zoom from its own
        /// <c>_lastZoomByKey</c> (a per-KEY question), not from this per-TAB guard. Retained as the
        /// read seam the tracker's tests use to assert what the guard remembers.
        /// </remarks>
        public bool TryGetKnown(string tabId, out double zoom)
        {
            zoom = 0;
            return !string.IsNullOrEmpty(tabId) && _last.TryGetValue(tabId, out zoom);
        }
    }
}
