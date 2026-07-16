using System;
using System.Collections.Generic;
using System.IO;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Resolves which registered project owns a folder by path containment: the folder
    /// matches a project when it IS the project root or lives anywhere beneath it.
    /// Exact-path matching is not enough for HUD scoping — an active-task terminal's
    /// real working directory is a worktree under <c>.claude\worktrees\</c>, which never
    /// exact-matches a registered root (task e8c6b52f).
    /// </summary>
    public static class ProjectPathResolver
    {
        /// <summary>
        /// Returns the registered project whose root contains (or equals) <paramref name="folder"/>.
        /// When registered roots nest, the deepest (longest) containing root wins. When two
        /// entries share the same root, the first one enumerated wins — a stable pick is more
        /// useful to HUD scoping than refusing to resolve. Returns null when no root contains
        /// the folder.
        /// </summary>
        public static ProjectRegistryEntry ResolveByContainment(IEnumerable<ProjectRegistryEntry> projects, string folder)
        {
            if (projects == null || string.IsNullOrWhiteSpace(folder)) return null;

            string candidate = Normalize(folder);
            ProjectRegistryEntry best = null;
            int bestLength = -1;

            foreach (var project in projects)
            {
                if (string.IsNullOrWhiteSpace(project?.Path)) continue;

                string root = Normalize(project.Path);
                if (!IsSameOrDescendant(root, candidate)) continue;

                if (root.Length > bestLength)
                {
                    bestLength = root.Length;
                    best = project;
                }
            }

            return best;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> equals <paramref name="root"/> or is a
        /// directory beneath it. Separator-bounded so a sibling like <c>X-other</c> is not
        /// treated as under <c>X</c>. Both arguments must already be normalized.
        /// </summary>
        private static bool IsSameOrDescendant(string root, string candidate)
        {
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            return path
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
