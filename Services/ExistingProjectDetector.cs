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
        /// Comparable form of a folder path: full path, trimmed, no trailing separators (a drive root
        /// keeps its own). Returns null for a blank path. Compare results with
        /// <see cref="StringComparison.OrdinalIgnoreCase"/> (see <see cref="PathsEqual"/>).
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                full = path.Trim();
            }

            string trimmed = full.TrimEnd(Separators);

            // "C:\" trims to "C:", which means "the current directory on C:", not the root.
            if (trimmed.Length == 0 || trimmed.EndsWith(':'))
                return full;

            return trimmed;
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

        /// <summary>
        /// Looks for a project at <paramref name="folder"/>.
        /// </summary>
        /// <param name="folder">The folder the user picked.</param>
        /// <param name="projectFileExists">Whether .claude/project.json EXISTS, whether or not it parsed.
        /// Occupancy is the file's existence: a damaged file is still somebody's project (fail closed).</param>
        /// <param name="projectJson">The folder's parsed .claude/project.json, or null when absent or unreadable.</param>
        /// <param name="databaseRows">Every project row in the database (id, name, path), not just this
        /// folder's: a copied project.json is recognised by its id being registered somewhere else.</param>
        public static ExistingProjectMatch Detect(
            string folder,
            bool projectFileExists,
            MultiTerminal.Models.Project projectJson,
            IEnumerable<(string Id, string Name, string Path)> databaseRows)
        {
            if (NormalizePath(folder) == null)
                return ExistingProjectMatch.None;

            var allRows = (databaseRows ?? Enumerable.Empty<(string Id, string Name, string Path)>())
                .Where(r => !string.IsNullOrEmpty(r.Id))
                .ToList();

            // Ordinal-by-id so a same-folder pair resolves the same way every time.
            var rows = allRows
                .Where(r => PathsEqual(r.Path, folder))
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
            var rowIds = rows.Select(r => r.Id).ToList();

            bool fileExists = projectFileExists || projectJson != null;
            bool hasJsonId = projectJson != null && !string.IsNullOrWhiteSpace(projectJson.Id);
            if (!fileExists && rows.Count == 0)
                return ExistingProjectMatch.None;

            var source = fileExists && rows.Count > 0 ? ExistingProjectSource.Both
                : fileExists ? ExistingProjectSource.ProjectJson
                : ExistingProjectSource.DatabaseRow;

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
            if (byId.Count > 0 && !byId.Any(r => PathsEqual(r.Path, folder)))
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
                    return $"This folder's .claude/project.json says project {match.ProjectId}, but MultiTerminal has {string.Join(", ", match.DatabaseIds)} registered at this folder. Remove or replace the file to settle which is right, or choose a different folder.";
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
