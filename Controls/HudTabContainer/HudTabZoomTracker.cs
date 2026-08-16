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
        /// Records a zoom the app itself applied, so the resulting echo is not mistaken for user intent.
        /// </summary>
        /// <param name="key">The persistence key.</param>
        /// <param name="zoom">The zoom factor being applied.</param>
        public void Record(string key, double zoom)
        {
            if (string.IsNullOrEmpty(key)) return;
            _last[key] = zoom;
        }

        /// <summary>
        /// Decides whether a zoom reported by a tab is a real change worth persisting.
        /// </summary>
        /// <param name="key">The persistence key.</param>
        /// <param name="zoom">The reported zoom factor.</param>
        /// <returns>True when this is a genuine change; false when it merely repeats the last value.</returns>
        /// <remarks>
        /// Comparing VALUES rather than setting a "currently applying" flag is deliberate. The renderers
        /// disagree about whether they subscribe to WebView2's ZoomFactorChanged before or after applying
        /// a pending zoom, and a zoom applied before initialisation is replayed later — so the echo can
        /// arrive long after the call that caused it, when any synchronous flag would already be clear.
        /// A value check does not care about ordering or timing.
        /// </remarks>
        public bool ShouldReport(string key, double zoom)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_last.TryGetValue(key, out var last) && Math.Abs(last - zoom) < ZoomEpsilon)
            {
                return false;
            }

            _last[key] = zoom;
            return true;
        }

        /// <summary>
        /// Gets the zoom last applied to or reported for a key, if any.
        /// </summary>
        /// <param name="key">The persistence key.</param>
        /// <param name="zoom">The remembered zoom.</param>
        /// <returns>True when a zoom is known for the key.</returns>
        public bool TryGetKnown(string key, out double zoom) => _last.TryGetValue(key ?? "", out zoom);
    }
}
