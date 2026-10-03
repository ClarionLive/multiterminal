using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>Where an existing project at a folder was found (task 9f95ab0c).</summary>
    public enum ExistingProjectSource
    {
        /// <summary>The folder holds no project.</summary>
        None,

        /// <summary>The folder has a .claude/project.json, but no database row points at it.</summary>
        ProjectJson,

        /// <summary>A database row points at the folder, but it has no .claude/project.json.</summary>
        DatabaseRow,

        /// <summary>Both a .claude/project.json and a database row.</summary>
        Both,
    }

    /// <summary>
    /// Why an occupied folder's project cannot be opened or renamed (pipeline Run 1 on 9f95ab0c). Any
    /// value but None leaves only "choose a different folder" and "cancel"; nothing is created either.
    /// </summary>
    public enum ExistingProjectProblem
    {
        /// <summary>The folder's project can be opened or renamed.</summary>
        None,

        /// <summary>A .claude/project.json exists but cannot be parsed or has no id. Occupied all the same.</summary>
        UnreadableProjectFile,

        /// <summary>
        /// project.json carries the id of a project registered at a DIFFERENT folder: a copied, cloned or
        /// moved folder, or a hostile file. Opening or renaming would act on that other project.
        /// </summary>
        IdRegisteredElsewhere,

        /// <summary>project.json names one id while the database has other id(s) registered at this folder.</summary>
        IdConflict,

        /// <summary>
        /// Whether the folder has a .claude/project.json could not be determined (access denied, I/O error,
        /// unusable path). Treated as occupied: not knowing is not "empty" (Run 2, fail closed).
        /// </summary>
        IndeterminateProjectFile,
    }

    /// <summary>What a look for .claude/project.json found (see <see cref="ExistingProjectDetector.ProbeProjectFile"/>).</summary>
    public enum ProjectFileState
    {
        /// <summary>Confirmed: there is no .claude/project.json.</summary>
        Absent,

        /// <summary>The file exists (parsed or not).</summary>
        Present,

        /// <summary>The look failed, so the answer is unknown.</summary>
        Indeterminate,
    }

    /// <summary>
    /// The project already living at a folder, as reported by <see cref="ExistingProjectDetector"/>.
    /// </summary>
    public sealed class ExistingProjectMatch
    {
        /// <summary>The "nothing here" result.</summary>
        public static readonly ExistingProjectMatch None = new ExistingProjectMatch(ExistingProjectSource.None, null, null, null, Array.Empty<string>());

        public ExistingProjectMatch(
            ExistingProjectSource source,
            string projectId,
            string projectName,
            string projectPath,
            IReadOnlyList<string> databaseIds,
            ExistingProjectProblem problem = ExistingProjectProblem.None,
            string otherPath = null,
            MultiTerminal.Models.Project projectJson = null)
        {
            Source = source;
            ProjectId = projectId;
            ProjectName = projectName;
            ProjectPath = projectPath;
            DatabaseIds = databaseIds ?? Array.Empty<string>();
            Problem = problem;
            OtherPath = otherPath;
            ProjectJson = projectJson;
        }

        public ExistingProjectSource Source { get; }

        public bool Exists => Source != ExistingProjectSource.None;

        /// <summary>Why Open/Rename are not offered; None when they are.</summary>
        public ExistingProjectProblem Problem { get; }

        /// <summary>Open existing / Rename existing are safe to offer.</summary>
        public bool CanOpenOrRename => Exists && Problem == ExistingProjectProblem.None;

        /// <summary>
        /// The id the folder answers to: project.json's id when it has one, else the first database row's
        /// (null for an unreadable project.json with no row).
        /// </summary>
        public string ProjectId { get; }

        /// <summary>The database row's name when the row exists (the registry is authoritative), else project.json's.</summary>
        public string ProjectName { get; }

        public string ProjectPath { get; }

        /// <summary>
        /// Every database row whose path matches. More than one means the database already holds a
        /// same-folder pair from before this check existed.
        /// </summary>
        public IReadOnlyList<string> DatabaseIds { get; }

        /// <summary>For <see cref="ExistingProjectProblem.IdRegisteredElsewhere"/>: the folder the id is registered at.</summary>
        public string OtherPath { get; }

        /// <summary>The folder's parsed project.json (null when absent or unreadable), so callers need not parse it again.</summary>
        public MultiTerminal.Models.Project ProjectJson { get; }
    }

    /// <summary>
    /// What the New Project flow should do once the folder has been checked.
    /// </summary>
    public enum NewProjectFolderDecision
    {
        /// <summary>The folder is free: create a new project.</summary>
        CreateNew,

        /// <summary>Launch the project that is already there.</summary>
        OpenExisting,

        /// <summary>Rename the project that is already there to the new name, then launch it.</summary>
        RenameExisting,

        /// <summary>Stay in the dialog with the folder field focused.</summary>
        ChooseDifferentFolder,

        /// <summary>Close the dialog and do nothing.</summary>
        Cancel,
    }

    /// <summary>
    /// Pure "is there already a project at this folder?" check, plus the decision the New Project
    /// dialog takes on the answer (task 9f95ab0c).
    ///
    /// <para>Before this, the only check was "does .claude/project.json parse", and the New Project
    /// dialog then reused that file's id and wrote a fresh project over it: new name, empty
    /// description, reset createdAt. A database row pointing at the folder without a project.json
    /// was not detected at all, and a damaged project.json counted as an empty folder.</para>
    /// </summary>
    public static class ExistingProjectDetector
    {
        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>
        /// Resolves an EXISTING folder to its canonical path (8.3 names expanded, junctions followed), or
        /// returns null to keep the path as given. Test seam (InternalsVisibleTo); production uses
        /// <see cref="FinalPathNativeMethods.TryResolveFinalPath"/>.
        /// </summary>
        internal static Func<string, string> FinalPathResolver { get; set; } = FinalPathNativeMethods.TryResolveFinalPath;

        /// <summary>
        /// Comparable form of a folder path: full path, trimmed, no trailing separators (a drive root
        /// keeps its own); an existing folder is resolved to its final path, so a junction or symlink
        /// compares equal to its target (Run 2; 8.3 short names are already expanded by
        /// Path.GetFullPath on .NET). Returns null for a blank path. Compare
        /// results with <see cref="StringComparison.OrdinalIgnoreCase"/> (see <see cref="PathsEqual"/>).
        /// </summary>
        public static string NormalizePath(string path)
        {
            string full = FullPath(path);
            if (full == null)
                return null;

            try
            {
                // CA3003: resolving (not opening for read/write) a local folder path.
#pragma warning disable CA3003
                if (DirectoryExistsProbe(full))
#pragma warning restore CA3003
                {
                    string resolved = FinalPathResolver?.Invoke(full);
                    if (!string.IsNullOrEmpty(resolved))
                        full = resolved;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Keep the unresolved path.
            }

            return TrimSeparators(full);
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo): the existence check that gates <see cref="FinalPathResolver"/>.
        /// It is the first disk touch for a path, so counting its calls counts disk work per path.
        /// </summary>
        internal static Func<string, bool> DirectoryExistsProbe { get; set; } = Directory.Exists;

        // Full path without touching the disk; null for a blank path.
        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return path.Trim();
            }
        }

        private static string TrimSeparators(string full)
        {
            string trimmed = full.TrimEnd(Separators);

            // "C:\" trims to "C:", which means "the current directory on C:", not the root.
            if (trimmed.Length == 0 || trimmed.EndsWith(':'))
                return full;

            return trimmed;
        }

        // The lexical (no disk) comparable form: full path, trailing separators trimmed.
        private static string LexicalPath(string path)
        {
            string full = FullPath(path);
            return full == null ? null : TrimSeparators(full);
        }

        /// <summary>
        /// Matches database rows against ONE target folder (Run 3). The target is normalized once (disk
        /// work: existence check + final-path resolution). A row is compared lexically first; it costs disk
        /// work only when it sits on the same LOCAL FIXED drive as the target. Without that limit every
        /// registered row was resolved on each check, under the create lock and on the UI thread, and a row
        /// on an offline network share stalled the create for the SMB timeout. Cost of the limit: a row
        /// reaching the target through a junction on ANOTHER drive, or through a network path, is not
        /// recognised as the same folder.
        /// </summary>
        private sealed class FolderMatcher
        {
            private readonly string _lexical;
            private readonly string _resolved;
            private readonly HashSet<string> _localFixedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public FolderMatcher(string folder)
            {
                _lexical = LexicalPath(folder);
                _resolved = NormalizePath(folder);
                foreach (var candidate in new[] { _lexical, _resolved })
                {
                    string root = RootOf(candidate);
                    if (root != null && IsLocalFixedRoot(root))
                        _localFixedRoots.Add(root);
                }
            }

            public bool Matches(string rowPath)
            {
                string rowLexical = LexicalPath(rowPath);
                if (rowLexical == null)
                    return false;
                if (string.Equals(rowLexical, _lexical, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rowLexical, _resolved, StringComparison.OrdinalIgnoreCase))
                    return true;

                string rowRoot = RootOf(rowLexical);
                if (rowRoot == null || !_localFixedRoots.Contains(rowRoot))
                    return false;
                return string.Equals(NormalizePath(rowPath), _resolved, StringComparison.OrdinalIgnoreCase);
            }

            private static string RootOf(string path)
            {
                try
                {
                    return string.IsNullOrEmpty(path) ? null : Path.GetPathRoot(path);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            private static bool IsLocalFixedRoot(string root)
            {
                if (root.StartsWith(@"\\", StringComparison.Ordinal))
                    return false; // UNC / device paths: network, never resolved per row
                try
                {
                    return new DriveInfo(root).DriveType == DriveType.Fixed;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <summary>Whether two folder paths name the same folder (Windows: case-insensitive).</summary>
        public static bool PathsEqual(string a, string b)
        {
            string na = NormalizePath(a);
            string nb = NormalizePath(b);
            return na != null && nb != null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Looks for a project at <paramref name="folder"/>, treating a non-null project.json as present.</summary>
        public static ExistingProjectMatch Detect(
            string folder,
            MultiTerminal.Models.Project projectJson,
            IEnumerable<(string Id, string Name, string Path)> databaseRows)
            => Detect(folder, projectJson != null, projectJson, databaseRows);

        /// <summary>Looks for a project at <paramref name="folder"/>, with the file's existence as a bool.</summary>
        public static ExistingProjectMatch Detect(
            string folder,
            bool projectFileExists,
            MultiTerminal.Models.Project projectJson,
            IEnumerable<(string Id, string Name, string Path)> databaseRows)
            => Detect(folder, projectFileExists ? ProjectFileState.Present : ProjectFileState.Absent, projectJson, databaseRows);

        /// <summary>
        /// Looks for .claude/project.json in <paramref name="folder"/>, distinguishing "confirmed absent"
        /// from "could not look" (Run 2). <see cref="File.Exists"/> alone answers false for both: it
        /// swallows access-denied and I/O errors. So a false is confirmed by listing: the folder for
        /// ".claude", then ".claude" for "project.json"; a listing that throws is
        /// <see cref="ProjectFileState.Indeterminate"/>. A folder that does not exist yet is Absent
        /// (New Project creates it).
        /// </summary>
        public static ProjectFileState ProbeProjectFile(string folder)
        {
            try
            {
                string full = Path.GetFullPath(folder);
                string claude = Path.Combine(full, ".claude");
                // CA3003: existence probes and listings on a local caller's project folder; nothing is
                // opened for read or write here.
#pragma warning disable CA3003
                if (File.Exists(Path.Combine(claude, "project.json")))
                    return ProjectFileState.Present;
                if (!Directory.Exists(full))
                    return ProjectFileState.Absent;
                if (!Directory.EnumerateDirectories(full, ".claude").Any())
                    return ProjectFileState.Absent;
                return Directory.EnumerateFiles(claude, "project.json").Any()
                    ? ProjectFileState.Present
                    : ProjectFileState.Absent;
#pragma warning restore CA3003
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return ProjectFileState.Indeterminate;
            }
        }

        /// <summary>The refusal text for a project.json id that differs from the folder's database row(s).</summary>
        public static string DescribeIdConflict(string projectJsonId, IEnumerable<string> databaseIds)
            => $"This folder's .claude/project.json says project {projectJsonId}, but MultiTerminal has {string.Join(", ", databaseIds ?? Enumerable.Empty<string>())} registered at this folder. Remove or replace the file to settle which is right, or choose a different folder.";

        /// <summary>
        /// Looks for a project at <paramref name="folder"/>.
        /// </summary>
        /// <param name="folder">The folder the user picked.</param>
        /// <param name="projectFile">Whether .claude/project.json EXISTS, whether or not it parsed; Indeterminate
        /// when that could not be determined. Occupancy is the file's existence, and not knowing counts as
        /// occupied: a damaged or unreadable file is still somebody's project (fail closed).</param>
        /// <param name="projectJson">The folder's parsed .claude/project.json, or null when absent or unreadable.</param>
        /// <param name="databaseRows">Every project row in the database (id, name, path), not just this
        /// folder's: a copied project.json is recognised by its id being registered somewhere else.</param>
        public static ExistingProjectMatch Detect(
            string folder,
            ProjectFileState projectFile,
            MultiTerminal.Models.Project projectJson,
            IEnumerable<(string Id, string Name, string Path)> databaseRows)
        {
            if (NormalizePath(folder) == null)
                return ExistingProjectMatch.None;

            var allRows = (databaseRows ?? Enumerable.Empty<(string Id, string Name, string Path)>())
                .Where(r => !string.IsNullOrEmpty(r.Id))
                .ToList();
            var matcher = new FolderMatcher(folder);

            // Ordinal-by-id so a same-folder pair resolves the same way every time.
            var rows = allRows
                .Where(r => matcher.Matches(r.Path))
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
            var rowIds = rows.Select(r => r.Id).ToList();

            bool indeterminate = projectFile == ProjectFileState.Indeterminate && projectJson == null;
            bool fileExists = projectFile == ProjectFileState.Present || projectJson != null || indeterminate;
            bool hasJsonId = projectJson != null && !string.IsNullOrWhiteSpace(projectJson.Id);
            if (!fileExists && rows.Count == 0)
                return ExistingProjectMatch.None;

            var source = fileExists && rows.Count > 0 ? ExistingProjectSource.Both
                : fileExists ? ExistingProjectSource.ProjectJson
                : ExistingProjectSource.DatabaseRow;

            if (indeterminate)
            {
                var first = rows.FirstOrDefault();
                return new ExistingProjectMatch(source, first.Id, first.Name, folder, rowIds,
                    ExistingProjectProblem.IndeterminateProjectFile);
            }

            if (fileExists && !hasJsonId)
            {
                var first = rows.FirstOrDefault();
                return new ExistingProjectMatch(source, first.Id, first.Name, folder, rowIds,
                    ExistingProjectProblem.UnreadableProjectFile);
            }

            if (!fileExists)
                return new ExistingProjectMatch(source, rows[0].Id, rows[0].Name, folder, rowIds);

            // project.json names the folder's identity, but only if that id is registered HERE (or nowhere).
            string id = projectJson.Id;
            var byId = allRows.Where(r => string.Equals(r.Id, id, StringComparison.Ordinal)).ToList();
            if (byId.Count > 0 && !byId.Any(r => matcher.Matches(r.Path)))
            {
                var other = byId[0];
                return new ExistingProjectMatch(source, id,
                    !string.IsNullOrEmpty(other.Name) ? other.Name : projectJson.Name,
                    folder, rowIds, ExistingProjectProblem.IdRegisteredElsewhere,
                    string.IsNullOrEmpty(other.Path) ? "(no folder)" : other.Path, projectJson);
            }

            if (rows.Count > 0 && !rowIds.Contains(id, StringComparer.Ordinal))
            {
                return new ExistingProjectMatch(source, id, projectJson.Name, folder, rowIds,
                    ExistingProjectProblem.IdConflict, null, projectJson);
            }

            var rowForId = rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            string name = !string.IsNullOrEmpty(rowForId.Name) ? rowForId.Name : projectJson.Name;
            return new ExistingProjectMatch(source, id, name, folder, rowIds, ExistingProjectProblem.None, null, projectJson);
        }

        /// <summary>
        /// The user-facing reason Open/Rename are not offered for <paramref name="match"/>, or null when they
        /// are. Shared by the New Project prompt and the broker's refusal, so both say the same thing.
        /// </summary>
        public static string DescribeProblem(ExistingProjectMatch match)
        {
            switch (match?.Problem ?? ExistingProjectProblem.None)
            {
                case ExistingProjectProblem.UnreadableProjectFile:
                    return $"The folder '{match.ProjectPath}' has a .claude/project.json that MultiTerminal cannot read (damaged, empty, or missing its id). Repair or remove that file, or choose a different folder.";
                case ExistingProjectProblem.IdRegisteredElsewhere:
                    return $"This folder's .claude/project.json is a copy of project '{match.ProjectName}' ({match.ProjectId}) at '{match.OtherPath}'. Choose a different folder, or remove/replace that file.";
                case ExistingProjectProblem.IdConflict:
                    return DescribeIdConflict(match.ProjectId, match.DatabaseIds);
                case ExistingProjectProblem.IndeterminateProjectFile:
                    return $"MultiTerminal can't read the .claude folder in '{match.ProjectPath}' (access denied, or the path can't be examined), so it can't tell whether a project is already there. Fix the folder's permissions, or choose a different folder.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// The New Project dialog's decision for a folder. <paramref name="ask"/> is called only when a
        /// project already exists there; it never returns <see cref="NewProjectFolderDecision.CreateNew"/>
        /// for an occupied folder, whatever <paramref name="ask"/> answers: a second project on one
        /// folder is not offered (Owner decision, 2026-10-02). Nor does it return Open/Rename when the
        /// match carries a <see cref="ExistingProjectMatch.Problem"/>: those would act on the wrong
        /// project, or on a file nobody can read.
        /// </summary>
        public static NewProjectFolderDecision Decide(ExistingProjectMatch match, Func<ExistingProjectMatch, NewProjectFolderDecision> ask)
        {
            if (match == null || !match.Exists)
                return NewProjectFolderDecision.CreateNew;

            var answer = ask?.Invoke(match) ?? NewProjectFolderDecision.Cancel;
            if (answer == NewProjectFolderDecision.CreateNew)
                return NewProjectFolderDecision.Cancel;
            if (!match.CanOpenOrRename
                && (answer == NewProjectFolderDecision.OpenExisting || answer == NewProjectFolderDecision.RenameExisting))
                return NewProjectFolderDecision.Cancel;
            return answer;
        }
    }
}
