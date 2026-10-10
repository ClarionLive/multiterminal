using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <param name="LoadProjectFile">Folder → the project in its .claude/project.json, or null. Called
    /// once per level of <paramref name="CandidateFolders"/>, so it is the only disk probe the ancestor
    /// walk makes for project files.</param>
    /// <param name="CandidateFolders">Folder → the levels to look for an owning project at, nearest
    /// first: the folder, its ancestors, and a worktree's main checkout
    /// (see <see cref="ClaudeProfileLaunchPolicy.CandidateFolders"/>).</param>
    internal sealed record ClaudeProfileLookup(
        Func<string, ClaudeProfileProjectRow> RowById,
        Func<IReadOnlyList<ClaudeProfileProjectRow>> AllRows,
        Func<string, Project> LoadProjectFile,
        Func<string, IReadOnlyList<string>> CandidateFolders);

    /// <summary>Which profile NAME a launch asks for, and why. The name is not yet checked against this machine.</summary>
    internal sealed record ClaudeProfileNameDecision(string ProfileName, ClaudeProfileNameSource Source, string ProjectId, string ProjectName)
    {
        /// <summary>
        /// Set when the nearest owning folder is registered to more than one project and they do not
        /// all name the same profile: the sentence naming the folder and the projects. There is then no
        /// profile name to use, and <see cref="ClaudeProfileLaunchPolicy.Decide"/> refuses the launch.
        /// </summary>
        public string Conflict { get; init; }
    }

    /// <summary>
    /// Thrown when a launch is refused because its Claude profile cannot be applied (ticket c8ee0eec).
    /// Its message is the whole user-facing explanation. A separate type so the code that starts a
    /// terminal can tell "refused over the profile" from "the pseudo console or the OS failed"; it
    /// derives from <see cref="InvalidOperationException"/>, which is what was thrown here before.
    /// </summary>
    public sealed class ClaudeProfileRefusedException : InvalidOperationException
    {
        public ClaudeProfileRefusedException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

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
    ///   <item>A launch that names a project takes that project's DATABASE row. The row is the
    ///   authority; project.json is a portable copy that can be stale.</item>
    ///   <item>Any other launch (Just Claude, Open PowerShell, Launch as, a spawned helper) belongs to
    ///   the NEAREST registered project at or above its folder: the folder itself, then each ancestor
    ///   up to (not including) the volume root, with a task worktree also looked up in its main
    ///   checkout. At each level a database row decides; a project.json counts only when no row owns
    ///   that level. A nearer project always beats a farther one.</item>
    ///   <item>The owning project's profile is final, including "none set" (Default): the search stops
    ///   there and nothing is inherited.</item>
    ///   <item>Two projects registered at the same owning folder with different profiles REFUSE the
    ///   launch.</item>
    ///   <item>Only a folder with no owning project at any level takes the inherited name (a helper's
    ///   spawner, the pane's current profile), else Default.</item>
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
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var named = lookup.RowById?.Invoke(projectId);
                if (named != null)
                    return FromRow(named);

                // No row for the named id: the launch is treated like one that names no project, so
                // the folder decides below.
            }

            // Nearest level first. The first level any project owns ends the search, whatever that
            // project's profile is: an owner with no profile set means Default, and must not fall
            // through to a farther project or to the inherited name.
            var levels = lookup.CandidateFolders?.Invoke(folder) ?? Array.Empty<string>();
            IReadOnlyList<ClaudeProfileProjectRow> rows = null;
            Project worktreeFile = null;
            foreach (string level in levels)
            {
                var file = lookup.LoadProjectFile?.Invoke(level);
                rows ??= lookup.AllRows?.Invoke() ?? Array.Empty<ClaudeProfileProjectRow>();

                // Database rows that claim this level: the row its project.json names (the file only
                // says WHICH project the folder is; the row says its profile), and every row registered
                // at this path.
                var owners = new List<ClaudeProfileProjectRow>();
                if (!string.IsNullOrWhiteSpace(file?.Id))
                {
                    var byFileId = lookup.RowById?.Invoke(file.Id);
                    if (byFileId != null)
                        owners.Add(byFileId);
                }

                foreach (var row in rows)
                {
                    if (row != null
                        && (SameFolder(row.Path, level) || SameFolder(row.SourcePath, level))
                        && !owners.Exists(o => string.Equals(o.Id, row.Id, StringComparison.Ordinal)))
                    {
                        owners.Add(row);
                    }
                }

                if (owners.Count > 0)
                    return FromOwners(level, owners);

                if (file == null)
                    continue;

                // No row owns this level, so its project.json is all there is. One exception: a
                // project.json INSIDE a worktree checkout is a copy of the repo's, so a row farther up
                // (the repo's project) still beats it. It is kept and used if no row turns up.
                if (IsInsideWorktreeCheckout(level))
                {
                    worktreeFile ??= file;
                    continue;
                }

                return FromFile(worktreeFile ?? file);
            }

            if (worktreeFile != null)
                return FromFile(worktreeFile);

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
            ClaudeProfileResolution resolution = null;
            try
            {
                name = FindProfileName(projectId, folder, inheritedProfileName, lookup);
                if (name.Conflict == null)
                    resolution = resolve(name.ProfileName);
            }
            catch (Exception ex)
            {
                return new ClaudeProfileLaunchDecision(
                    null, null, ClaudeProfileNameSource.None, null,
                    $"Not started: {launchLabel}. MultiTerminal could not work out which Claude profile it belongs under ({ex.Message}), and will not guess. Try again; if it keeps happening, check the debug log.");
            }

            if (name.Conflict != null)
            {
                return new ClaudeProfileLaunchDecision(
                    null, null, name.Source, null,
                    $"Not started: {launchLabel}. {name.Conflict} MultiTerminal will not pick one. Give those projects the same Claude profile in Edit Project, or remove the duplicate project.");
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
        /// The levels an owning project is looked for at, nearest first: the folder, then each ancestor
        /// up to but NOT including the volume root (a drive root or UNC share is searched only when it
        /// is the folder itself). Paths are canonical (<see cref="ClaudeProfileResolver.Canonicalize"/>):
        /// forward slashes, a trailing separator and <c>..</c> are normalised, and levels are whole
        /// path segments, so <c>H:\Proj</c> is never a level of <c>H:\Proj2\x</c>.
        /// <para>Worktrees. A worktree belongs to the project at its repo. Three layouts exist
        /// (<see cref="WorktreeLayout"/>):</para>
        /// <list type="bullet">
        ///   <item><c>&lt;repo&gt;\.claude\worktrees\&lt;name&gt;</c> (current). The repo is an ancestor,
        ///   so the walk reaches it for ANY worktree name. When the repo has a <c>.git</c>, a folder
        ///   under the worktree is also looked up at the same folder in the main checkout, for any
        ///   name.</item>
        ///   <item><c>&lt;repo&gt;\worktrees\&lt;id&gt;</c> (legacy child). The repo is an ancestor too; the
        ///   same-folder-in-the-main-checkout levels are added only for an 8-hex task id.</item>
        ///   <item><c>&lt;parent&gt;\&lt;repo&gt;-worktrees\&lt;id&gt;</c> (legacy sibling). The repo is NOT an
        ///   ancestor, so it is reached only through <see cref="WorktreeLayout.TryMapWorktreePath"/>,
        ///   which needs an 8-hex task id and a <c>.git</c> at the repo.</item>
        /// </list>
        /// <para>The main-checkout levels come right after the worktree directory itself, so a project
        /// registered at <c>&lt;repo&gt;\Sub</c> owns <c>&lt;worktree&gt;\Sub</c> ahead of the project
        /// at the repo root. Reads the disk only for those <c>.git</c> checks.</para>
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

            string start = ClaudeProfileResolver.Canonicalize(folder);
            if (start == null)
                return result;

            // The main checkout's equivalent of the folder, when it sits at or under a worktree.
            string worktreeDir = null;
            if (TryMapClaudeWorktree(start, out string repoRoot, out string mapped) || WorktreeLayout.TryMapWorktreePath(start, out repoRoot, out mapped))
            {
                repoRoot = ClaudeProfileResolver.Canonicalize(repoRoot);
                mapped = ClaudeProfileResolver.Canonicalize(mapped);
                int suffixLength = repoRoot == null || mapped == null ? -1 : mapped.Length - repoRoot.Length;
                if (suffixLength >= 0 && suffixLength < start.Length)
                    worktreeDir = start.Substring(0, start.Length - suffixLength);
            }

            foreach (string level in SelfAndAncestors(start, stopAt: null))
            {
                Add(level);
                if (worktreeDir != null && string.Equals(level, worktreeDir, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string checkoutLevel in SelfAndAncestors(mapped, stopAt: repoRoot))
                        Add(checkoutLevel);
                }
            }

            return result;
        }

        /// <summary>
        /// <paramref name="canonical"/> and its ancestors, nearest first. Stops after
        /// <paramref name="stopAt"/> when given; otherwise stops before the volume root, which is
        /// yielded only when it is <paramref name="canonical"/> itself.
        /// </summary>
        private static IEnumerable<string> SelfAndAncestors(string canonical, string stopAt)
        {
            string cur = canonical;
            while (!string.IsNullOrEmpty(cur))
            {
                yield return cur;
                if (stopAt != null && string.Equals(cur, stopAt, StringComparison.OrdinalIgnoreCase))
                    yield break;

                string parent = ClaudeProfileResolver.Canonicalize(Path.GetDirectoryName(cur));
                if (parent == null || ClaudeProfileResolver.IsVolumeRoot(parent))
                    yield break;
                cur = parent;
            }
        }

        /// <summary>
        /// Maps a folder at or under <c>&lt;repo&gt;\.claude\worktrees\&lt;name&gt;</c> (any name) to the
        /// same folder in the main checkout, when <c>&lt;repo&gt;</c> has a <c>.git</c>.
        /// </summary>
        private static bool TryMapClaudeWorktree(string canonical, out string repoRoot, out string mapped)
        {
            repoRoot = null;
            mapped = null;
            var below = new List<string>();
            string cur = canonical;
            while (!string.IsNullOrEmpty(cur))
            {
                string worktrees = Path.GetDirectoryName(cur);
                string dotClaude = string.IsNullOrEmpty(worktrees) ? null : Path.GetDirectoryName(worktrees);
                string repo = string.IsNullOrEmpty(dotClaude) ? null : Path.GetDirectoryName(dotClaude);
                if (!string.IsNullOrEmpty(repo)
                    && string.Equals(Path.GetFileName(worktrees), WorktreeLayout.NewSubfolder, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(dotClaude), ".claude", StringComparison.OrdinalIgnoreCase)
                    && WorktreeLayout.IsLikelyGitRepoRoot(repo))
                {
                    string result = repo;
                    for (int i = below.Count - 1; i >= 0; i--)
                        result = Path.Combine(result, below[i]);
                    repoRoot = repo;
                    mapped = result;
                    return true;
                }

                below.Add(Path.GetFileName(cur));
                cur = worktrees;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="canonical"/> is at or under a worktree checkout, judged by the path
        /// text alone: <c>\.claude\worktrees\&lt;name&gt;</c> for any name, or one of the legacy
        /// layouts with an 8-hex task id.
        /// </summary>
        internal static bool IsInsideWorktreeCheckout(string canonical)
        {
            if (string.IsNullOrEmpty(canonical))
                return false;
            if (WorktreeLayout.LooksLikeWorktreePath(canonical))
                return true;

            var segs = canonical.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < segs.Length; i++)
            {
                if (segs[i].Equals(".claude", StringComparison.OrdinalIgnoreCase)
                    && segs[i + 1].Equals(WorktreeLayout.NewSubfolder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The decision for a level one or more rows own. One row, or several that agree on the profile,
        /// is that profile. Rows that disagree are a conflict: the caller refuses rather than pick.
        /// </summary>
        private static ClaudeProfileNameDecision FromOwners(string level, List<ClaudeProfileProjectRow> owners)
        {
            var first = FromRow(owners[0]);
            bool agree = owners.All(o => string.Equals(
                ClaudeProfile.NormalizeName(o.ClaudeProfile), first.ProfileName, StringComparison.OrdinalIgnoreCase));
            if (agree)
                return first;

            string list = string.Join(", ", owners.Select(o =>
                $"'{o.Name}' ({ClaudeProfile.NormalizeName(o.ClaudeProfile) ?? ClaudeProfile.DefaultName})"));
            return new ClaudeProfileNameDecision(null, ClaudeProfileNameSource.Database, null, null)
            {
                Conflict = $"The folder '{level}' is registered to more than one project, and they are set to different Claude profiles: {list}.",
            };
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
