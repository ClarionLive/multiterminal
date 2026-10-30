#nullable enable
using System;
using System.Collections.Generic;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Which window a dialog belongs to (GH #74, phase 3). With floating windows that minimise on their
    /// own, the main window can be minimised while the Owner works in another window, and a dialog owned
    /// by a minimised window is hidden along with it: the app looks hung while it waits for an answer
    /// nobody can see. Pure: the caller lists the candidates in order of preference and says which are
    /// usable (visible and not minimised).
    /// </summary>
    public static class DialogOwnerChoice
    {
        /// <summary>
        /// The first usable candidate, skipping nulls; or the first non-null candidate when none is
        /// usable (an owner the Owner can restore beats none); or null when there are no candidates.
        /// </summary>
        public static T? Pick<T>(IEnumerable<T?> candidatesInPreferenceOrder, Func<T, bool> isUsable)
            where T : class
        {
            T? fallback = null;
            foreach (var candidate in candidatesInPreferenceOrder ?? Array.Empty<T?>())
            {
                if (candidate == null) continue;
                fallback ??= candidate;
                if (isUsable(candidate)) return candidate;
            }

            return fallback;
        }
    }
}
