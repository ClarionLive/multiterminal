using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>A pane open in MT, as seen by the Move Project checks (ticket 6f879683).</summary>
    /// <param name="Title">What the pane is called, for the refusal message.</param>
    /// <param name="LaunchProjectId">The project the pane was launched for, or null.</param>
    /// <param name="WorkingDirectory">The pane's current folder, or null when unknown.</param>
    public sealed record RelocationPane(string Title, string LaunchProjectId, string WorkingDirectory);

    /// <summary>
    /// Path arithmetic for Move Project (ticket 6f879683). Every comparison is separator-bounded, so
    /// <c>C:\foo</c> never matches <c>C:\foobar</c>, and accepts either separator, because the code
    /// graph stores paths with <c>/</c> while everything else uses <c>\</c>.
    /// </summary>
    public static class RelocationPaths
    {
        /// <summary>Full path with trailing separators removed. Null or blank stays null.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                full = path.Trim();
            }

            string trimmed = full.TrimEnd('\\', '/');

            // "C:\" trims to "C:", which GetFullPath would read as "current folder on C:". Keep the root whole.
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
        }

        /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or lies beneath it.</summary>
        public static bool IsSameOrUnder(string candidate, string root)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
            string c = candidate.Replace('/', '\\').TrimEnd('\\');
            string r = root.Replace('/', '\\').TrimEnd('\\');
            if (r.Length == 0) return false;
            return c.Equals(r, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Moves <paramref name="path"/> from under <paramref name="oldRoot"/> to the same place under
        /// <paramref name="newRoot"/>. Returns null when the path is not under the old root (callers leave
        /// those alone). The path's own separator style is kept, so a <c>/</c> path stays a <c>/</c> path.
        /// </summary>
        public static string Rebase(string path, string oldRoot, string newRoot)
        {
            if (!IsSameOrUnder(path, oldRoot) || string.IsNullOrWhiteSpace(newRoot)) return null;

            string r = oldRoot.Replace('/', '\\').TrimEnd('\\');
            string rest = path.Substring(r.Length); // "" or starts with a separator
            bool slashStyle = path.Contains('/') && !path.Contains('\\');
            string target = newRoot.TrimEnd('\\', '/');
            if (slashStyle) target = target.Replace('\\', '/');
            return target + rest;
        }
    }

    /// <summary>
    /// Decides whether a project may be moved, and says why not (ticket 6f879683). Pure apart from the
    /// file system probes passed in, so tests can drive every refusal.
    /// </summary>
    public static class ProjectRelocationChecks
    {
        /// <summary>
        /// Problems that stop moving the project at <paramref name="oldRoot"/> to <paramref name="newRoot"/>.
        /// Empty means the move may start. <paramref name="moveFolder"/> is false for "Folder was moved…",
        /// where the folder is already at <paramref name="newRoot"/> and must exist.
        /// </summary>
        public static IReadOnlyList<string> Check(
            string projectId,
            string oldRoot,
            string sourcePath,
            string newRoot,
            bool moveFolder,
            IEnumerable<RelocationPane> panes,
            Func<string, bool> directoryExists,
            Func<string, bool> directoryHasEntries)
        {
            var problems = new List<string>();
            string from = RelocationPaths.Normalize(oldRoot);
            string to = RelocationPaths.Normalize(newRoot);

            if (from == null)
            {
                problems.Add("The project has no folder recorded.");
                return problems;
            }

            if (to == null)
            {
                problems.Add("Choose a folder to move the project to.");
                return problems;
            }

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("The new folder is the same as the current one.");
                return problems;
            }

            if (RelocationPaths.IsSameOrUnder(to, from))
                problems.Add("The new folder is inside the project's current folder.");

            if (RelocationPaths.IsSameOrUnder(from, to))
                problems.Add("The new folder contains the project's current folder.");

            if (WorktreeLayout.LooksLikeWorktreePath(to))
                problems.Add("The new folder looks like a task worktree folder (a 'worktrees' folder followed by an 8-character id). MT would treat it as a worktree, so choose another name.");

            if (moveFolder)
            {
                if (!directoryExists(from))
                    problems.Add($"The project's folder '{from}' does not exist. If you already moved it, use \"Folder was moved…\" instead.");
                if (directoryExists(to) && directoryHasEntries(to))
                    problems.Add($"'{to}' already exists and is not empty. Choose a new or empty folder.");
            }
            else if (!directoryExists(to))
            {
                problems.Add($"'{to}' does not exist.");
            }

            // A project registered below its repo root keeps its worktrees at the repo root, outside the
            // folder being moved, so moving it alone would break them.
            string source = RelocationPaths.Normalize(sourcePath);
            if (source != null && !RelocationPaths.IsSameOrUnder(source, from))
                problems.Add($"The project's source folder '{source}' is outside its project folder. Moving only the project folder would separate them.");

            // The legacy layout keeps worktrees beside the project ("<name>-worktrees"), which would not move.
            string sibling = from + WorktreeLayout.LegacySuffix;
            if (moveFolder && directoryExists(sibling) && directoryHasEntries(sibling))
                problems.Add($"This project still has task worktrees in the old layout at '{sibling}', which would not move with it. Finish or remove those tasks first.");

            var blocking = (panes ?? Enumerable.Empty<RelocationPane>())
                .Where(p => p != null
                    && (string.Equals(p.LaunchProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                        || RelocationPaths.IsSameOrUnder(p.WorkingDirectory, from)))
                .Select(p => string.IsNullOrWhiteSpace(p.Title) ? "(untitled pane)" : p.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blocking.Count > 0)
                problems.Add("Close these terminals first, because they are using the project's folder: " + string.Join(", ", blocking) + ".");

            return problems;
        }
    }
}
