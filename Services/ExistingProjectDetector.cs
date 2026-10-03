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
    /// The project already living at a folder, as reported by <see cref="ExistingProjectDetector.Detect"/>.
    /// </summary>
    public sealed class ExistingProjectMatch
    {
        /// <summary>The "nothing here" result.</summary>
        public static readonly ExistingProjectMatch None = new ExistingProjectMatch(ExistingProjectSource.None, null, null, null, Array.Empty<string>());

        public ExistingProjectMatch(ExistingProjectSource source, string projectId, string projectName, string projectPath, IReadOnlyList<string> databaseIds)
        {
            Source = source;
            ProjectId = projectId;
            ProjectName = projectName;
            ProjectPath = projectPath;
            DatabaseIds = databaseIds ?? Array.Empty<string>();
        }

        public ExistingProjectSource Source { get; }

        public bool Exists => Source != ExistingProjectSource.None;

        /// <summary>The id the folder answers to: project.json's id when there is one, else the database row's.</summary>
        public string ProjectId { get; }

        /// <summary>The database row's name when the row exists (the registry is authoritative), else project.json's.</summary>
        public string ProjectName { get; }

        public string ProjectPath { get; }

        /// <summary>
        /// Every database row whose path matches. More than one means the database already holds a
        /// same-folder pair from before this check existed.
        /// </summary>
        public IReadOnlyList<string> DatabaseIds { get; }
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
    /// <para>Before this, the only check was "does .claude/project.json exist", and the New Project
    /// dialog then reused that file's id and wrote a fresh project over it — new name, empty
    /// description, reset createdAt. A database row pointing at the folder without a project.json
    /// was not detected at all.</para>
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

        /// <summary>
        /// Looks for a project at <paramref name="folder"/>.
        /// </summary>
        /// <param name="folder">The folder the user picked.</param>
        /// <param name="projectJson">The folder's parsed .claude/project.json, or null when there is none.</param>
        /// <param name="databaseRows">Every project row in the database (id, name, path).</param>
        public static ExistingProjectMatch Detect(
            string folder,
            MultiTerminal.Models.Project projectJson,
            IEnumerable<(string Id, string Name, string Path)> databaseRows)
        {
            if (NormalizePath(folder) == null)
                return ExistingProjectMatch.None;

            var rows = (databaseRows ?? Enumerable.Empty<(string Id, string Name, string Path)>())
                .Where(r => !string.IsNullOrEmpty(r.Id) && PathsEqual(r.Path, folder))
                .ToList();

            bool hasJson = projectJson != null && !string.IsNullOrEmpty(projectJson.Id);
            if (!hasJson && rows.Count == 0)
                return ExistingProjectMatch.None;

            var source = hasJson && rows.Count > 0 ? ExistingProjectSource.Both
                : hasJson ? ExistingProjectSource.ProjectJson
                : ExistingProjectSource.DatabaseRow;

            // project.json names the folder's identity. With no file, the first matching row stands in;
            // ordinal-by-id so a same-folder pair resolves the same way every time.
            string id = hasJson ? projectJson.Id : rows.OrderBy(r => r.Id, StringComparer.Ordinal).First().Id;

            var rowForId = rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            string name = !string.IsNullOrEmpty(rowForId.Name) ? rowForId.Name : projectJson?.Name;

            return new ExistingProjectMatch(
                source,
                id,
                name,
                folder,
                rows.Select(r => r.Id).ToList());
        }

        /// <summary>
        /// The New Project dialog's decision for a folder. <paramref name="ask"/> is called only when a
        /// project already exists there; it never returns <see cref="NewProjectFolderDecision.CreateNew"/>
        /// for an occupied folder, whatever <paramref name="ask"/> answers — a second project on one
        /// folder is not offered (Owner decision, 2026-10-02).
        /// </summary>
        public static NewProjectFolderDecision Decide(ExistingProjectMatch match, Func<ExistingProjectMatch, NewProjectFolderDecision> ask)
        {
            if (match == null || !match.Exists)
                return NewProjectFolderDecision.CreateNew;

            var answer = ask?.Invoke(match) ?? NewProjectFolderDecision.Cancel;
            return answer == NewProjectFolderDecision.CreateNew ? NewProjectFolderDecision.Cancel : answer;
        }
    }
}
