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

        /// <summary>The launch folder's own .claude/project.json, used only because no database row is
        /// registered at that folder. When the file's project id has a row registered at another
        /// folder, the file is used only if that row names the same profile; otherwise the launch is
        /// refused.</summary>
        ProjectFile,

        /// <summary>No project owns the folder; the name was handed down (a helper's spawner, or the
        /// profile a pane was already running under).</summary>
        Inherited,

        /// <summary>No project owns the folder; the human starting the launch picked the profile
        /// (Just Claude, Launch as).</summary>
        Requested,
    }

    /// <summary>The columns of a project's database row that the profile lookup needs.</summary>
    internal sealed record ClaudeProfileProjectRow(string Id, string Name, string Path, string SourcePath, string ClaudeProfile);

    /// <summary>
    /// Everything the lookup reads from the outside world, injected so the decision is testable without
    /// a database, a disk or a form.
    /// </summary>
    /// <param name="RowById">Project id → its database row, or null when there is none.</param>
    /// <param name="AllRows">Every project row (for the folder match).</param>
    /// <param name="LoadProjectFile">Folder → the project in its .claude/project.json, or null. Asked
    /// only for the launch folder itself and its main-checkout twin, never for a folder above them
    /// (see <see cref="ClaudeProfileLaunchPolicy.LaunchLevels"/>).</param>
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
        /// Set when two sources claim the launch and do not name the same profile: the nearest owning
        /// folder is registered to more than one project, or the launch folder's project.json names a
        /// project whose database row is registered at another folder under a different profile. The
        /// text says what was found and how to fix it. There is then no profile name to use, and
        /// <see cref="ClaudeProfileLaunchPolicy.Decide"/> refuses the launch with this text after
        /// "Not started: (what)."
        /// <para>Also set when a profile was asked for by name for a launch whose folder a project owns,
        /// and it is not that project's profile.</para>
        /// </summary>
        public string Conflict { get; init; }
    }

    /// <summary>
    /// What a "Claude profile" picker may offer for a launch folder
    /// (<see cref="ClaudeProfileLaunchPolicy.DescribeChoice"/>), so that it never offers what
    /// <see cref="ClaudeProfileLaunchPolicy.Decide"/> would refuse.
    /// </summary>
    /// <param name="CanChoose">True when no project owns the folder, so the launch takes whichever
    /// profile is picked.</param>
    /// <param name="FixedProfileName">When a project owns the folder: its profile's display name
    /// ("Default" when it has none set). Null when <paramref name="CanChoose"/>, or when the owner
    /// could not be worked out.</param>
    /// <param name="Reason">One line saying why there is no choice; null when <paramref name="CanChoose"/>.</param>
    internal sealed record ClaudeProfileLaunchChoice(bool CanChoose, string FixedProfileName, string Reason);

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
    ///   checkout. A nearer project always beats a farther one.</item>
    ///   <item>ABOVE the launch folder only a database row registered at that exact level (path or
    ///   source path) owns the launch. A project.json there is never read: a stale or copied file in a
    ///   broad parent folder must not choose the profile for everything beneath it.</item>
    ///   <item>AT the launch folder (and the same folder in the main checkout, for a worktree) a row
    ///   registered there decides. With no such row, that folder's project.json counts, by the profile
    ///   name written in it. The file's project id is followed to a database row, which owns the
    ///   launch only when it is registered at the launch folder or its twin. A row registered anywhere
    ///   else does not own the folder, but it is the same project, so the file may not contradict it:
    ///   if the file and that row name the same profile the launch runs under it, and if they differ
    ///   the launch is REFUSED (the folder was moved or copied, and the file may be stale). A file
    ///   whose id has no row at all counts as written.</item>
    ///   <item>The owning project's profile is final, including "none set" (Default): the search stops
    ///   there and nothing is inherited.</item>
    ///   <item>Two projects registered at the same owning folder with different profiles REFUSE the
    ///   launch.</item>
    ///   <item>Only a folder with no owning project at any level takes a profile from elsewhere: the
    ///   one the human picked for this launch (Just Claude, Launch as), else the inherited name (a
    ///   helper's spawner, the pane's current profile), else Default.</item>
    ///   <item>A picked profile never overrides an owner. Picking the owner's own profile changes
    ///   nothing; picking any other REFUSES the launch.</item>
    ///   <item>A name this machine cannot honour REFUSES the launch. Nothing falls back to Default.</item>
    ///   <item>A launch nobody is watching (a spawned helper, a headless agent, the gloss writer) is
    ///   REFUSED under a profile that has never run Claude: its sign-in and first-run prompts would
    ///   wait for an answer nobody can give.</item>
    /// </list>
    /// </summary>
    internal static class ClaudeProfileLaunchPolicy
    {
        /// <summary>
        /// Which profile name the launch asks for. Reads the outside world only through
        /// <paramref name="lookup"/> and the <c>.git</c> check in <see cref="LaunchLevels"/>; an
        /// exception from the lookup (an unreadable database) propagates, and <see cref="Decide"/> turns
        /// it into a refusal rather than a guess.
        /// </summary>
        /// <param name="projectId">The project the launch names, or null/blank when it names none.</param>
        /// <param name="folder">The folder the launch starts in.</param>
        /// <param name="inheritedProfileName">The name to use when no project owns the folder and
        /// nothing was requested.</param>
        /// <param name="requestedProfileName">The profile a human picked for this launch, or null/blank
        /// when none was picked ("Default" is a pick: it selects Default over the inherited name). Used
        /// only when no project owns the folder. When a project does, the same profile as the owner's
        /// is ignored and any other is a <see cref="ClaudeProfileNameDecision.Conflict"/>.</param>
        internal static ClaudeProfileNameDecision FindProfileName(
            string projectId, string folder, string inheritedProfileName, ClaudeProfileLookup lookup,
            string requestedProfileName = null)
        {
            bool requested = !string.IsNullOrWhiteSpace(requestedProfileName);
            string requestedName = ClaudeProfile.NormalizeName(requestedProfileName);

            var owner = FindOwner(projectId, folder, lookup);
            if (owner != null)
            {
                // An owner that is itself a conflict refuses for its own reason, whatever was picked.
                if (!requested || owner.Conflict != null
                    || string.Equals(requestedName, owner.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    return owner;
                }

                return new ClaudeProfileNameDecision(null, owner.Source, owner.ProjectId, owner.ProjectName)
                {
                    Conflict =
                        $"The Claude profile '{requestedName ?? ClaudeProfile.DefaultName}' was chosen for it, but its folder belongs to project '{owner.ProjectName}', "
                        + $"which uses the Claude profile '{owner.ProfileName ?? ClaudeProfile.DefaultName}'. "
                        + "A terminal in a project's folder always runs under that project's profile. "
                        + "To change it, choose another profile for the project in Project Settings (the gear on its Home card).",
                };
            }

            if (requested)
                return new ClaudeProfileNameDecision(requestedName, ClaudeProfileNameSource.Requested, null, null);

            string inherited = ClaudeProfile.NormalizeName(inheritedProfileName);
            return inherited != null
                ? new ClaudeProfileNameDecision(inherited, ClaudeProfileNameSource.Inherited, null, null)
                : new ClaudeProfileNameDecision(null, ClaudeProfileNameSource.None, null, null);
        }

        /// <summary>
        /// What a profile picker may offer for <paramref name="folder"/>: a free choice when no project
        /// owns it, else the owner's profile as fixed text with the reason. Uses the same ownership
        /// search as <see cref="FindProfileName"/>. Never throws: a lookup that fails offers no choice,
        /// and the launch itself then reports why.
        /// </summary>
        internal static ClaudeProfileLaunchChoice DescribeChoice(string folder, ClaudeProfileLookup lookup)
        {
            ClaudeProfileNameDecision owner;
            try
            {
                owner = FindOwner(null, folder, lookup);
            }
            catch (Exception)
            {
                return new ClaudeProfileLaunchChoice(false, null, "MultiTerminal could not work out which project this folder belongs to.");
            }

            if (owner == null)
                return new ClaudeProfileLaunchChoice(true, null, null);

            if (owner.Conflict != null)
                return new ClaudeProfileLaunchChoice(false, null, "This folder's project gives no clear Claude profile; Launch will say why.");

            string fixedName = owner.ProfileName ?? ClaudeProfile.DefaultName;
            return new ClaudeProfileLaunchChoice(
                false, fixedName,
                $"This folder belongs to project '{owner.ProjectName}', which uses '{fixedName}'. Change it in Project Settings.");
        }

        /// <summary>
        /// The project that owns the launch and its profile name (or its conflict), or null when no
        /// project owns it. The ownership rules are items 1 to 6 of the class summary.
        /// </summary>
        private static ClaudeProfileNameDecision FindOwner(string projectId, string folder, ClaudeProfileLookup lookup)
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
            var launchLevels = LaunchLevels(folder);
            IReadOnlyList<ClaudeProfileProjectRow> rows = null;
            ClaudeProfileNameDecision worktreeFile = null;
            foreach (string level in levels)
            {
                rows ??= lookup.AllRows?.Invoke() ?? Array.Empty<ClaudeProfileProjectRow>();

                // Every database row registered at this level.
                var owners = new List<ClaudeProfileProjectRow>();
                foreach (var row in rows)
                {
                    if (row != null
                        && (SameFolder(row.Path, level) || SameFolder(row.SourcePath, level))
                        && !owners.Exists(o => string.Equals(o.Id, row.Id, StringComparison.Ordinal)))
                    {
                        owners.Add(row);
                    }
                }

                // A project.json is read only at the launch folder and its main-checkout twin. Above
                // them a file is not evidence of anything: only a registered row owns from above.
                bool isLaunchLevel = launchLevels.Exists(l => SameFolder(l, level));
                var file = isLaunchLevel ? lookup.LoadProjectFile?.Invoke(level) : null;

                // The file's id is followed to its row. That row OWNS the launch only when it is
                // registered at the launch folder or its twin (a worktree's copy of the repo's
                // project.json). A row registered anywhere else does not own this folder, but it is
                // kept: the file may not contradict it (see FromFileChecked).
                ClaudeProfileProjectRow rowElsewhere = null;
                if (!string.IsNullOrWhiteSpace(file?.Id))
                {
                    var byFileId = lookup.RowById?.Invoke(file.Id);
                    if (byFileId != null)
                    {
                        if (!launchLevels.Exists(l => SameFolder(byFileId.Path, l) || SameFolder(byFileId.SourcePath, l)))
                            rowElsewhere = byFileId;
                        else if (!owners.Exists(o => string.Equals(o.Id, byFileId.Id, StringComparison.Ordinal)))
                            owners.Add(byFileId);
                    }
                }

                if (owners.Count > 0)
                    return FromOwners(level, owners);

                if (file == null)
                    continue;

                // No row owns this launch level, so its project.json decides, unless it contradicts
                // the row its id belongs to. One exception: a project.json INSIDE a worktree checkout
                // is a copy of the repo's, so a row farther up (the repo's project) still beats it. Its
                // decision is kept and used if no row turns up.
                var fromFile = FromFileChecked(level, file, rowElsewhere);
                if (IsInsideWorktreeCheckout(ClaudeProfileResolver.Canonicalize(level)))
                {
                    worktreeFile ??= fromFile;
                    continue;
                }

                return worktreeFile ?? fromFile;
            }

            return worktreeFile;
        }

        /// <summary>
        /// The whole decision for one launch: <see cref="FindProfileName"/>, then
        /// <paramref name="resolve"/> (normally <see cref="ClaudeProfileResolver.Resolve(string, string)"/>)
        /// to check the name against this machine. Never throws: a lookup that fails is a refusal,
        /// because "could not find out" must not become "Default".
        /// </summary>
        /// <param name="launchLabel">What is being started, for the message ("project 'X'", "helper 'Bob'").</param>
        /// <param name="requestedProfileName">The profile a human picked for this launch, or null
        /// (see <see cref="FindProfileName"/>).</param>
        /// <param name="neverUsedReason">Passed only for a launch nobody is watching. Given the resolved
        /// profile it returns null when that profile has run Claude before, else the sentence saying it
        /// has not (normally <see cref="ClaudeProfileResolver.DescribeNeverUsed(ClaudeProfile)"/>), and
        /// the launch is refused with it. Null for a launch with a human at the pane, who sees the
        /// sign-in and first-run prompts; that is how a profile gets set up.</param>
        internal static ClaudeProfileLaunchDecision Decide(
            string projectId,
            string folder,
            string inheritedProfileName,
            string launchLabel,
            ClaudeProfileLookup lookup,
            Func<string, ClaudeProfileResolution> resolve,
            string requestedProfileName = null,
            Func<ClaudeProfile, string> neverUsedReason = null)
        {
            ClaudeProfileNameDecision name;
            ClaudeProfileResolution resolution = null;
            string neverUsed = null;
            try
            {
                name = FindProfileName(projectId, folder, inheritedProfileName, lookup, requestedProfileName);
                if (name.Conflict == null)
                {
                    resolution = resolve(name.ProfileName);
                    if (resolution.CanLaunch && neverUsedReason != null)
                        neverUsed = neverUsedReason(resolution.Profile);
                }
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
                    $"Not started: {launchLabel}. {name.Conflict}");
            }

            if (neverUsed != null)
            {
                return new ClaudeProfileLaunchDecision(
                    null, name.ProfileName, name.Source, name.ProjectName,
                    $"Not started: {launchLabel}. {DescribeOrigin(name)}{neverUsed} Nobody is watching this launch to sign in or answer Claude's first-run prompts, so it would wait on them forever. "
                    + $"Open a terminal under the Claude profile '{resolution.Profile.Name}' once and sign in, then try again.");
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
            string fix = name.Source switch
            {
                ClaudeProfileNameSource.Inherited => "Fix the profile in Settings > Claude Profiles.",
                ClaudeProfileNameSource.Requested => "Fix the profile in Settings > Claude Profiles, or choose another one.",
                _ => "Fix the profile in Settings > Claude Profiles, or choose another profile for the project in Project Settings.",
            };

            return $"Not started: {launchLabel}. {DescribeOrigin(name)}{blockedReason} {fix} MultiTerminal does not fall back to another profile.";
        }

        /// <summary>One sentence (with a trailing space) saying where the launch's profile name came
        /// from, or "" when nothing chose one (no owner, nothing picked, nothing inherited).</summary>
        private static string DescribeOrigin(ClaudeProfileNameDecision name) => name.Source switch
        {
            ClaudeProfileNameSource.Database or ClaudeProfileNameSource.ProjectFile when name.ProfileName != null =>
                $"Project '{name.ProjectName}' is set to the Claude profile '{name.ProfileName}'. ",
            ClaudeProfileNameSource.Database or ClaudeProfileNameSource.ProjectFile =>
                $"Project '{name.ProjectName}' uses the Default Claude profile. ",
            ClaudeProfileNameSource.Inherited =>
                $"Its folder belongs to no project, so it takes the Claude profile '{name.ProfileName}' from the terminal it came from. ",
            ClaudeProfileNameSource.Requested =>
                $"The Claude profile '{name.ProfileName ?? ClaudeProfile.DefaultName}' was chosen for it. ",
            _ => "",
        };

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
        /// <para>Each level at or under the worktree directory is followed at once by the same folder in
        /// the main checkout: <c>&lt;worktree&gt;\Sub</c>, <c>&lt;repo&gt;\Sub</c>,
        /// <c>&lt;worktree&gt;</c>, <c>&lt;repo&gt;</c>. So a project registered at
        /// <c>&lt;repo&gt;\Sub</c> owns <c>&lt;worktree&gt;\Sub</c> ahead of anything at the worktree
        /// directory or the repo root. Reads the disk only for those <c>.git</c> checks.</para>
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

            // Each worktree-side level is paired with the same folder in the main checkout, down to
            // the worktree directory itself (whose twin is the repo root).
            string twin = TryFindMainCheckout(start, out string worktreeDir, out string mapped) ? mapped : null;
            foreach (string level in SelfAndAncestors(start, stopAt: null))
            {
                Add(level);
                if (twin == null)
                    continue;

                Add(twin);
                twin = string.Equals(level, worktreeDir, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ClaudeProfileResolver.Canonicalize(Path.GetDirectoryName(twin));
            }

            return result;
        }

        /// <summary>
        /// The only folders whose project.json may count for a launch in <paramref name="folder"/>: the
        /// folder itself and, when it is at or under a worktree, the same folder in the main checkout.
        /// Canonical. Never a folder above either of them. Reads the disk only for the <c>.git</c> check
        /// that finds the main checkout.
        /// </summary>
        internal static List<string> LaunchLevels(string folder)
        {
            var result = new List<string>();
            string start = ClaudeProfileResolver.Canonicalize(folder);
            if (start == null)
                return result;

            result.Add(start);
            if (TryFindMainCheckout(start, out _, out string mapped)
                && !string.Equals(mapped, start, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(mapped);
            }

            return result;
        }

        /// <summary>
        /// For a canonical folder at or under a worktree: the worktree directory and the same folder in
        /// the main checkout (both canonical). False for any other folder.
        /// </summary>
        private static bool TryFindMainCheckout(string start, out string worktreeDir, out string mapped)
        {
            worktreeDir = null;
            if (!TryMapClaudeWorktree(start, out string repoRoot, out mapped)
                && !WorktreeLayout.TryMapWorktreePath(start, out repoRoot, out mapped))
            {
                mapped = null;
                return false;
            }

            repoRoot = ClaudeProfileResolver.Canonicalize(repoRoot);
            mapped = ClaudeProfileResolver.Canonicalize(mapped);
            int suffixLength = repoRoot == null || mapped == null ? -1 : mapped.Length - repoRoot.Length;
            if (suffixLength < 0 || suffixLength >= start.Length)
            {
                mapped = null;
                return false;
            }

            worktreeDir = start.Substring(0, start.Length - suffixLength);
            return true;
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
                Conflict = $"The folder '{level}' is registered to more than one project, and they are set to different Claude profiles: {list}. MultiTerminal will not pick one. Give those projects the same Claude profile in Project Settings, or remove the duplicate project.",
            };
        }

        private static ClaudeProfileNameDecision FromRow(ClaudeProfileProjectRow row) =>
            new ClaudeProfileNameDecision(ClaudeProfile.NormalizeName(row.ClaudeProfile), ClaudeProfileNameSource.Database, row.Id, row.Name);

        private static ClaudeProfileNameDecision FromFile(Project file) =>
            new ClaudeProfileNameDecision(ClaudeProfile.NormalizeName(file.ClaudeProfile), ClaudeProfileNameSource.ProjectFile, file.Id, file.Name);

        /// <summary>
        /// The decision for a launch-level project.json that no registered row overrides.
        /// <paramref name="rowElsewhere"/> is the database row the file's project id belongs to when
        /// that row is registered at some OTHER folder (null when the id has no row). That is what a
        /// moved or copied project folder looks like, and the file's profile may be out of date. So the
        /// file is used only when it names the same profile as the row (one rule:
        /// <see cref="ClaudeProfile.NormalizeName"/>, then ordinal-ignore-case; blank and Default are
        /// the same). When they differ, in either direction, the result is a conflict and the launch is
        /// refused. Agreement keeps <see cref="ClaudeProfileNameSource.ProjectFile"/> as the source:
        /// the row does not own this folder, it only failed to contradict the file.
        /// </summary>
        private static ClaudeProfileNameDecision FromFileChecked(string level, Project file, ClaudeProfileProjectRow rowElsewhere)
        {
            var fromFile = FromFile(file);
            if (rowElsewhere == null)
                return fromFile;

            string rowProfile = ClaudeProfile.NormalizeName(rowElsewhere.ClaudeProfile);
            if (string.Equals(fromFile.ProfileName, rowProfile, StringComparison.OrdinalIgnoreCase))
                return fromFile;

            string registeredPath = !string.IsNullOrWhiteSpace(rowElsewhere.Path) ? rowElsewhere.Path : rowElsewhere.SourcePath;
            string registeredAt = string.IsNullOrWhiteSpace(registeredPath) ? "with no folder" : $"at '{registeredPath}'";
            string fileName = string.IsNullOrWhiteSpace(file.Name) ? rowElsewhere.Name : file.Name;
            return new ClaudeProfileNameDecision(null, ClaudeProfileNameSource.ProjectFile, null, null)
            {
                Conflict =
                    $"The folder '{level}' has a .claude\\project.json that says it is project '{fileName}' with the Claude profile '{fromFile.ProfileName ?? ClaudeProfile.DefaultName}', "
                    + $"but MultiTerminal has that project registered as '{rowElsewhere.Name}' {registeredAt} with the Claude profile '{rowProfile ?? ClaudeProfile.DefaultName}'. "
                    + "The folder looks moved or copied, and MultiTerminal will not guess which profile is right. "
                    + "If the folder was moved, set the project's Source path to this folder in Project Settings (Paths). "
                    + "If it is a copy, remove or correct .claude\\project.json in this folder.",
            };
        }

        private static bool SameFolder(string a, string b)
        {
            string ca = ClaudeProfileResolver.Canonicalize(a);
            string cb = ClaudeProfileResolver.Canonicalize(b);
            return ca != null && cb != null && string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
        }
    }
}
