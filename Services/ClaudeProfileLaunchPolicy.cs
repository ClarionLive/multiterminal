using System;
using System.Collections.Generic;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>Where a launch's Claude profile name came from.</summary>
    public enum ClaudeProfileNameSource
    {
        /// <summary>No project and nothing inherited: the Default profile.</summary>
        None,

        /// <summary>The project's database row (the authority).</summary>
        Database,

        /// <summary>The folder's .claude/project.json, used only because the project has no database row.</summary>
        ProjectFile,

        /// <summary>No project owns the folder; the name was handed down (a helper's spawner, or the
        /// profile a pane was already running under).</summary>
        Inherited,
    }

    /// <summary>The columns of a project's database row that the profile lookup needs.</summary>
    internal sealed record ClaudeProfileProjectRow(string Id, string Name, string Path, string SourcePath, string ClaudeProfile);

    /// <summary>
    /// Everything the lookup reads from the outside world, injected so the decision is testable without
    /// a database, a disk or a form.
    /// </summary>
    /// <param name="RowById">Project id → its database row, or null when there is none.</param>
    /// <param name="AllRows">Every project row (for the folder match).</param>
    /// <param name="LoadProjectFile">Folder → the project in its .claude/project.json, or null.</param>
    /// <param name="CandidateFolders">Folder → the folders to look the project up at, nearest first
    /// (see <see cref="ClaudeProfileLaunchPolicy.CandidateFolders"/>).</param>
    internal sealed record ClaudeProfileLookup(
        Func<string, ClaudeProfileProjectRow> RowById,
        Func<IReadOnlyList<ClaudeProfileProjectRow>> AllRows,
        Func<string, Project> LoadProjectFile,
        Func<string, IReadOnlyList<string>> CandidateFolders);

    /// <summary>Which profile NAME a launch asks for, and why. The name is not yet checked against this machine.</summary>
    internal sealed record ClaudeProfileNameDecision(string ProfileName, ClaudeProfileNameSource Source, string ProjectId, string ProjectName);

    /// <summary>
    /// The outcome for one launch: the profile to start under, or the refusal to show or report.
    /// </summary>
    /// <param name="Profile">The profile to launch under; null exactly when the launch is refused.</param>
    /// <param name="ProfileName">The name that was asked for (null = Default).</param>
    /// <param name="Source">Where that name came from.</param>
    /// <param name="ProjectName">The project the name belongs to, when there is one.</param>
    /// <param name="Refusal">The complete user-facing refusal, or null when the launch may proceed.</param>
    public sealed record ClaudeProfileLaunchDecision(
        ClaudeProfile Profile,
        string ProfileName,
        ClaudeProfileNameSource Source,
        string ProjectName,
        string Refusal)
    {
        /// <summary>True when the launch may start, under <see cref="Profile"/>.</summary>
        public bool CanLaunch => Refusal == null;
    }

    /// <summary>
    /// Ticket c8ee0eec (GitHub #21): the ONE answer to "which Claude profile does this launch run under,
    /// and may it start at all". Every launch site in MainForm asks here; none reads
    /// <c>Project.ClaudeProfile</c> off an object it happens to be holding.
    /// <para>Order (Owner decisions 2026-10-10):</para>
    /// <list type="number">
    ///   <item>The project's DATABASE row. The row is the authority; project.json is a portable copy
    ///   that can be stale, so it is used only when the project has no row at all.</item>
    ///   <item>A launch that names no project takes the project that owns its folder (so Just Claude,
    ///   Open PowerShell, Launch as and a spawned helper all land on the project's profile).</item>
    ///   <item>A folder no project owns takes the inherited name (a helper's spawner), else Default.</item>
    ///   <item>A name this machine cannot honour REFUSES the launch. Nothing falls back to Default.</item>
    /// </list>
    /// </summary>
    internal static class ClaudeProfileLaunchPolicy
    {
        /// <summary>
        /// Which profile name the launch asks for. Pure apart from <paramref name="lookup"/>; an
        /// exception from the lookup (an unreadable database) propagates, and <see cref="Decide"/> turns
        /// it into a refusal rather than a guess.
        /// </summary>
        /// <param name="projectId">The project the launch names, or null/blank when it names none.</param>
        /// <param name="folder">The folder the launch starts in.</param>
        /// <param name="inheritedProfileName">The name to use when no project owns the folder.</param>
        internal static ClaudeProfileNameDecision FindProfileName(
            string projectId, string folder, string inheritedProfileName, ClaudeProfileLookup lookup)
        {
            var folders = lookup.CandidateFolders?.Invoke(folder) ?? Array.Empty<string>();

            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var named = lookup.RowById?.Invoke(projectId);
                if (named != null)
                    return FromRow(named);

                // No row for the named id: the launch is treated like one that names no project, so
                // the folder decides below (a row for the folder first, a project.json only after that).
            }

            // Pass 1: any database row for the folder. The file only says WHICH project the folder is;
            // the row says its profile. A row at a farther candidate (the repo root of a worktree) still
            // beats a file at a nearer one.
            IReadOnlyList<ClaudeProfileProjectRow> rows = null;
            foreach (string candidate in folders)
            {
                var file = lookup.LoadProjectFile?.Invoke(candidate);
                if (!string.IsNullOrWhiteSpace(file?.Id))
                {
                    var byFileId = lookup.RowById?.Invoke(file.Id);
                    if (byFileId != null)
                        return FromRow(byFileId);
                }

                rows ??= lookup.AllRows?.Invoke() ?? Array.Empty<ClaudeProfileProjectRow>();
                foreach (var row in rows)
                {
                    if (SameFolder(row.Path, candidate) || SameFolder(row.SourcePath, candidate))
                        return FromRow(row);
                }
            }

            // Pass 2: no row anywhere, so a project.json is all there is.
            foreach (string candidate in folders)
            {
                var file = lookup.LoadProjectFile?.Invoke(candidate);
                if (file != null)
                    return FromFile(file);
            }

            string inherited = ClaudeProfile.NormalizeName(inheritedProfileName);
            return inherited != null
                ? new ClaudeProfileNameDecision(inherited, ClaudeProfileNameSource.Inherited, null, null)
                : new ClaudeProfileNameDecision(null, ClaudeProfileNameSource.None, null, null);
        }

        /// <summary>
        /// The whole decision for one launch: <see cref="FindProfileName"/>, then
        /// <paramref name="resolve"/> (normally <see cref="ClaudeProfileResolver.Resolve(string, string)"/>)
        /// to check the name against this machine. Never throws: a lookup that fails is a refusal,
        /// because "could not find out" must not become "Default".
        /// </summary>
        /// <param name="launchLabel">What is being started, for the message ("project 'X'", "helper 'Bob'").</param>
        internal static ClaudeProfileLaunchDecision Decide(
            string projectId,
            string folder,
            string inheritedProfileName,
            string launchLabel,
            ClaudeProfileLookup lookup,
            Func<string, ClaudeProfileResolution> resolve)
        {
            ClaudeProfileNameDecision name;
            ClaudeProfileResolution resolution;
            try
            {
                name = FindProfileName(projectId, folder, inheritedProfileName, lookup);
                resolution = resolve(name.ProfileName);
            }
            catch (Exception ex)
            {
                return new ClaudeProfileLaunchDecision(
                    null, null, ClaudeProfileNameSource.None, null,
                    $"Not started: {launchLabel}. MultiTerminal could not work out which Claude profile it belongs under ({ex.Message}), and will not guess. Try again; if it keeps happening, check the debug log.");
            }

            if (resolution.CanLaunch)
                return new ClaudeProfileLaunchDecision(resolution.Profile, name.ProfileName, name.Source, name.ProjectName, null);

            return new ClaudeProfileLaunchDecision(
                null, name.ProfileName, name.Source, name.ProjectName,
                DescribeRefusal(launchLabel, name, resolution.BlockedReason));
        }

        /// <summary>The refusal text: what was not started, which project and profile, why, and where to fix it.</summary>
        internal static string DescribeRefusal(string launchLabel, ClaudeProfileNameDecision name, string blockedReason)
        {
            string origin = name.Source switch
            {
                ClaudeProfileNameSource.Database or ClaudeProfileNameSource.ProjectFile =>
                    $"Project '{name.ProjectName}' is set to the Claude profile '{name.ProfileName}'. ",
                ClaudeProfileNameSource.Inherited =>
                    $"Its folder belongs to no project, so it takes the Claude profile '{name.ProfileName}' from the terminal it came from. ",
                _ => "",
            };

            string fix = name.Source == ClaudeProfileNameSource.Inherited
                ? "Fix the profile in Settings > Claude Profiles."
                : "Fix the profile in Settings > Claude Profiles, or choose another profile for the project in Edit Project.";

            return $"Not started: {launchLabel}. {origin}{blockedReason} {fix} MultiTerminal does not fall back to another profile.";
        }

        /// <summary>
        /// The folders a project is looked up at, nearest first: the folder itself, then, for a folder at
        /// or under an MT task worktree (<c>&lt;repo&gt;\.claude\worktrees\&lt;id&gt;</c> and the two legacy
        /// layouts), the matching folder in the main checkout and the repo root. A worktree has no
        /// project of its own; it belongs to the project at its repo.
        /// <para>Ancestors of an ordinary folder are NOT searched: a pane opened in a project's subfolder
        /// matches only when that subfolder is itself the project's path or source path.</para>
        /// </summary>
        internal static IReadOnlyList<string> CandidateFolders(string folder)
        {
            var result = new List<string>();
            void Add(string f)
            {
                string canonical = ClaudeProfileResolver.Canonicalize(f);
                if (canonical != null && !result.Exists(x => string.Equals(x, canonical, StringComparison.OrdinalIgnoreCase)))
                    result.Add(canonical);
            }

            Add(folder);
            if (WorktreeLayout.TryMapWorktreePath(folder, out string repoRoot, out string mapped))
            {
                Add(mapped);
                Add(repoRoot);
            }

            return result;
        }

        private static ClaudeProfileNameDecision FromRow(ClaudeProfileProjectRow row) =>
            new ClaudeProfileNameDecision(ClaudeProfile.NormalizeName(row.ClaudeProfile), ClaudeProfileNameSource.Database, row.Id, row.Name);

        private static ClaudeProfileNameDecision FromFile(Project file) =>
            new ClaudeProfileNameDecision(ClaudeProfile.NormalizeName(file.ClaudeProfile), ClaudeProfileNameSource.ProjectFile, file.Id, file.Name);

        private static bool SameFolder(string a, string b)
        {
            string ca = ClaudeProfileResolver.Canonicalize(a);
            string cb = ClaudeProfileResolver.Canonicalize(b);
            return ca != null && cb != null && string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
        }
    }
}
