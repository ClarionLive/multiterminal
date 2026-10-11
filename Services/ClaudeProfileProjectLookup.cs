using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ticket c8ee0eec: the ONE place that builds the live <see cref="ClaudeProfileLookup"/>, the
    /// project registry as <see cref="ClaudeProfileLaunchPolicy"/> reads it (database rows, a folder's
    /// <c>.claude/project.json</c>, and the levels to search). The launch decision in MainForm, the
    /// profile pickers and <see cref="ClaudeProfileDataRoots"/> all ask here, so "which project owns
    /// this folder" is answered from the same sources whether a pane is being started or its session
    /// data is being read or written.
    /// <para>MainForm calls <see cref="Configure"/> once, when the project database exists. Until
    /// then <see cref="Current"/> is null: a launch decision given a null lookup refuses
    /// (<see cref="ClaudeProfileLaunchPolicy.Decide"/> never throws and never guesses), and
    /// <see cref="ClaudeProfileDataRoots"/> answers "unknown". Neither is Default.</para>
    /// </summary>
    internal static class ClaudeProfileProjectLookup
    {
        private static readonly object Sync = new object();
        private static Func<ClaudeProfileLookup> _factory;

        /// <summary>Called once by MainForm with the shared project database and project service.</summary>
        internal static void Configure(ProjectDatabase db, ProjectService projects)
        {
            Func<ClaudeProfileLookup> factory = () => Build(db, projects);
            lock (Sync)
                _factory = factory;
        }

        /// <summary>The live lookup, or null before <see cref="Configure"/> has run. A new instance per
        /// call; every read goes to the database or the disk when the policy asks, so nothing is cached.</summary>
        internal static ClaudeProfileLookup Current()
        {
            Func<ClaudeProfileLookup> factory;
            lock (Sync)
                factory = _factory;

            return factory?.Invoke();
        }

        /// <summary>
        /// The lookup over <paramref name="db"/> and <paramref name="projects"/>. A null database reads
        /// as "no rows" and a null project service as "no project.json", as both copies of this wiring
        /// did before they were merged here.
        /// </summary>
        internal static ClaudeProfileLookup Build(ProjectDatabase db, ProjectService projects) =>
            new ClaudeProfileLookup(
                RowById: id => ToRow(db?.GetRichProject(id)),
                AllRows: () => (db?.GetAllRichProjects() ?? new List<Project>()).Select(ToRow).ToList(),
                LoadProjectFile: f => projects?.DiscoverProject(f),
                CandidateFolders: ClaudeProfileLaunchPolicy.CandidateFolders);

        private static ClaudeProfileProjectRow ToRow(Project p) =>
            p == null ? null : new ClaudeProfileProjectRow(p.Id, p.Name, p.Path, p.SourcePath, p.ClaudeProfile);
    }
}
