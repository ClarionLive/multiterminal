using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>One row of the startup restore dialog (task 3e1c09b8): a pane the previous run had open.</summary>
    public sealed class StartupRestoreEntry
    {
        internal StartupRestoreEntry(RestorablePane pane, string projectName, StartupRestoreEntry parent)
        {
            Pane = pane;
            ProjectName = projectName;
            Parent = parent;
        }

        public RestorablePane Pane { get; }

        /// <summary>The PM this helper comes back with, or null for a top-level pane.</summary>
        public StartupRestoreEntry Parent { get; }

        public bool IsHelper => Parent != null;

        /// <summary>The registered project's name (a helper shows its PM's project).</summary>
        public string ProjectName { get; }

        public List<StartupRestoreEntry> Helpers { get; } = new List<StartupRestoreEntry>();

        /// <summary>Display-safe pane name (transcript- and DB-derived text is untrusted).</summary>
        public string DisplayName => SessionCatalog.ToDisplayText(Pane.TerminalName) ?? "(unnamed)";

        /// <summary>"project folder" or "worktree &lt;id&gt;".</summary>
        public string Where => LaunchSessionChoiceModel.HelperWhere(Pane.Cwd);

        public bool Checked { get; internal set; } = true;

        /// <summary>A helper is enabled only while its PM is ticked; a top-level row always is.</summary>
        public bool Enabled => Parent == null || Parent.Checked;
    }

    /// <summary>
    /// The startup restore offer (task 3e1c09b8), pure: which of the previous run's open panes are
    /// listed, nested and ticked. Every row starts ticked (Owner decision). Unticking a PM unticks and
    /// disables its helpers, because a helper comes back only WITH its PM (its spawner link, close_helper
    /// and close-with-PM all hang on the PM pane); ticking the PM again ticks them again.
    /// </summary>
    public sealed class StartupRestoreModel
    {
        private readonly List<StartupRestoreEntry> _rows = new List<StartupRestoreEntry>();

        /// <param name="panes">From <see cref="PaneLaunchRecorder.GetPreviousRunOpenPanes"/>, oldest first.</param>
        /// <param name="projectNameOf">A registered project's name by id; null for an id with no row.</param>
        public StartupRestoreModel(IEnumerable<RestorablePane> panes, Func<string, string> projectNameOf)
        {
            var all = (panes ?? Enumerable.Empty<RestorablePane>()).Where(p => p != null).ToList();
            var byLaunch = all.Where(p => !string.IsNullOrEmpty(p.LaunchId))
                .GroupBy(p => p.LaunchId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            string NameOf(string projectId)
            {
                if (string.IsNullOrEmpty(projectId) || projectNameOf == null) return null;
                try { return projectNameOf(projectId); }
                catch (Exception) { return null; }
            }

            // Top-level: no parent among the offered panes. A resume needs a registered project (its root
            // validates the cwd and builds the command), so a top-level pane without one is not listed.
            var tops = new Dictionary<string, StartupRestoreEntry>(StringComparer.Ordinal);
            foreach (var pane in all)
            {
                bool hasParent = pane.ParentLaunchId != null && byLaunch.ContainsKey(pane.ParentLaunchId);
                if (hasParent) continue;
                string project = NameOf(pane.ProjectId);
                if (project == null)
                {
                    NotListed.Add(pane);
                    continue;
                }
                var entry = new StartupRestoreEntry(pane, project, null);
                tops[pane.LaunchId ?? Guid.NewGuid().ToString()] = entry;
                TopLevel.Add(entry);
            }

            // Helpers come back with their PM under the PM's project. One whose PM is not listed
            // (no registered project) is not listed either: alone it would have no project to resume in.
            foreach (var pane in all)
            {
                bool hasParent = pane.ParentLaunchId != null && byLaunch.ContainsKey(pane.ParentLaunchId);
                if (!hasParent) continue;
                if (!tops.TryGetValue(pane.ParentLaunchId, out var pm))
                {
                    NotListed.Add(pane);
                    continue;
                }
                pm.Helpers.Add(new StartupRestoreEntry(pane, pm.ProjectName, pm));
            }

            foreach (var top in TopLevel)
            {
                _rows.Add(top);
                _rows.AddRange(top.Helpers);
            }
        }

        /// <summary>The PMs and other top-level panes, oldest launch first.</summary>
        public List<StartupRestoreEntry> TopLevel { get; } = new List<StartupRestoreEntry>();

        /// <summary>Display order: each top-level row followed by its helpers.</summary>
        public IReadOnlyList<StartupRestoreEntry> Rows => _rows;

        /// <summary>Offered panes that cannot be listed (no registered project), for the log.</summary>
        public List<RestorablePane> NotListed { get; } = new List<RestorablePane>();

        /// <summary>Nothing to offer: no dialog is shown.</summary>
        public bool IsEmpty => _rows.Count == 0;

        /// <summary>Ticks or unticks a row. A disabled helper (its PM unticked) does not change.</summary>
        public void SetChecked(StartupRestoreEntry entry, bool isChecked)
        {
            if (entry == null || !entry.Enabled) return;
            entry.Checked = isChecked;
            foreach (var helper in entry.Helpers)
                helper.Checked = isChecked;
        }

        /// <summary>Flips a row (click or Space).</summary>
        public void Toggle(StartupRestoreEntry entry)
        {
            if (entry != null)
                SetChecked(entry, !entry.Checked);
        }

        /// <summary>Unticks everything ("Don't restore").</summary>
        public void UncheckAll()
        {
            foreach (var row in _rows)
                row.Checked = false;
        }

        /// <summary>The ticked top-level panes, oldest first, each with its ticked helpers.</summary>
        public IReadOnlyList<StartupRestoreEntry> SelectedTopLevel => TopLevel.Where(t => t.Checked).ToList();

        /// <summary>A PM's ticked helpers (none when the PM is unticked).</summary>
        public static IReadOnlyList<StartupRestoreEntry> SelectedHelpers(StartupRestoreEntry pm)
            => pm == null || !pm.Checked ? Array.Empty<StartupRestoreEntry>() : pm.Helpers.Where(h => h.Checked).ToList();

        /// <summary>How many rows are ticked (the Resume button's count).</summary>
        public int CheckedCount => _rows.Count(r => r.Checked && r.Enabled);
    }

    /// <summary>
    /// Where each restored session goes (task 3e1c09b8), pure. The layout restore already rebuilt the
    /// previous panes (on their Home screens) from layout.xml with their saved titles; a restored
    /// session reuses the pane that has its name, so it comes back in the same place.
    /// </summary>
    public static class StartupRestorePlacement
    {
        /// <summary>
        /// Matches session names to restored panes. Each session takes the first unused pane whose
        /// title equals its name, compared as the broker compares terminal names (ordinal-ignore-case,
        /// untrimmed); none = null (a new pane). Each pane is used at most once.
        /// </summary>
        /// <param name="paneTitles">The restored panes' titles, in pane order (null or blank = no title).</param>
        /// <param name="sessionNames">The names to place, in restore order.</param>
        /// <returns>Per session, the index of its pane in <paramref name="paneTitles"/>, or null.</returns>
        public static int?[] Match(IReadOnlyList<string> paneTitles, IReadOnlyList<string> sessionNames)
        {
            var titles = paneTitles ?? Array.Empty<string>();
            var names = sessionNames ?? Array.Empty<string>();
            var used = new bool[titles.Count];
            var result = new int?[names.Count];
            for (int s = 0; s < names.Count; s++)
            {
                string name = names[s];
                if (string.IsNullOrEmpty(name)) continue;
                for (int p = 0; p < titles.Count; p++)
                {
                    if (used[p] || string.IsNullOrEmpty(titles[p])) continue;
                    if (!string.Equals(titles[p], name, StringComparison.OrdinalIgnoreCase)) continue;
                    used[p] = true;
                    result[s] = p;
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// The restored panes to close once the restore has run: every pane no session went into.
        /// When no session was restored at all (Don't restore, or everything failed) the FIRST pane is
        /// kept, so MT opens on one Home pane instead of N (Owner decision) and never on none.
        /// </summary>
        /// <param name="paneCount">How many restored panes there are.</param>
        /// <param name="usedPanes">The pane indexes a restored session went into.</param>
        /// <param name="anySessionRestored">Whether any session came back (in a reused or a new pane).</param>
        public static IReadOnlyList<int> PanesToClose(int paneCount, IEnumerable<int> usedPanes, bool anySessionRestored)
        {
            var used = new HashSet<int>(usedPanes ?? Enumerable.Empty<int>());
            var close = Enumerable.Range(0, Math.Max(0, paneCount)).Where(i => !used.Contains(i)).ToList();
            if (!anySessionRestored && close.Count == paneCount && close.Count > 0)
                close.RemoveAt(0);
            return close;
        }
    }
}
