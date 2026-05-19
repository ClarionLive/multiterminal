using System;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Shared helpers for the two on-disk worktree layouts used by this repo:
    ///
    /// <list type="bullet">
    ///   <item><b>New (child) layout:</b>
    ///   <c>{repoRoot}/worktrees/{taskIdShort}/</c> — parent dir is named
    ///   <c>worktrees</c>; repo root is the grandparent.</item>
    ///   <item><b>Legacy (sibling) layout:</b>
    ///   <c>{repoParent}/{repoName}-worktrees/{taskIdShort}/</c> — parent dir
    ///   ends in <c>-worktrees</c>; repo root is the sibling without the
    ///   suffix.</item>
    /// </list>
    ///
    /// <para>Consumers: <see cref="WorktreeJanitorService"/> Pass 3 and
    /// <see cref="TerminalSpawner"/> stale-path guard. Centralised here so
    /// that fixes (e.g. requiring a <c>.git</c> presence check at the derived
    /// repo root) propagate to both sites — task db4b18c6 cycle 2 found a
    /// name-only match risked deleting source dirs in a repo legitimately
    /// named <c>worktrees</c>.</para>
    /// </summary>
    internal static class WorktreeLayout
    {
        /// <summary>Filename for the legacy sibling layout suffix.</summary>
        public const string LegacySuffix = "-worktrees";

        /// <summary>Filename for the new child layout subfolder.</summary>
        public const string NewSubfolder = "worktrees";

        /// <summary>
        /// Derive the repo root for a worktree-parent directory. Returns null
        /// when the parent doesn't match either layout, OR when the derived
        /// candidate isn't itself a git repo (no <c>.git</c> file/dir).
        /// The git-presence check is what stops Pass 3 from wandering into
        /// an unrelated source tree that happens to live under a directory
        /// literally named <c>worktrees</c>.
        /// </summary>
        public static string DeriveRepoRootFromParent(string parentDir)
        {
            if (string.IsNullOrWhiteSpace(parentDir)) return null;

            string parentName = Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string candidate = null;

            if (string.Equals(parentName, NewSubfolder, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetDirectoryName(parentDir);
            }
            else if (!string.IsNullOrEmpty(parentName) && parentName.EndsWith(LegacySuffix, StringComparison.OrdinalIgnoreCase))
            {
                string repoName = parentName.Substring(0, parentName.Length - LegacySuffix.Length);
                string grand = Path.GetDirectoryName(parentDir);
                if (!string.IsNullOrEmpty(grand) && !string.IsNullOrEmpty(repoName))
                {
                    candidate = Path.Combine(grand, repoName);
                }
            }

            return IsLikelyGitRepoRoot(candidate) ? candidate : null;
        }

        /// <summary>
        /// Derive the worktree-parent directory for a repo root, given a layout
        /// choice. Used by Pass 3's opportunistic sibling-scan to surface
        /// legacy parent dirs that no longer have any DB rows (post-migration
        /// orphan-blindspot fix).
        /// </summary>
        public static string DeriveLegacyParentForRepo(string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return null;
            try
            {
                string trimmed = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string repoName = Path.GetFileName(trimmed);
                string parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(parent)) return null;
                return Path.Combine(parent, repoName + LegacySuffix);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="path"/> looks like a git
        /// repo root — either contains a <c>.git</c> directory (main checkout)
        /// or a <c>.git</c> file (secondary worktree). Cheap filesystem check;
        /// no subprocess. Used as a sanity gate before any destructive
        /// enumeration in Pass 3 and as a fast-path validation in
        /// <see cref="TerminalSpawner.IsValidWorktree"/>.
        /// </summary>
        public static bool IsLikelyGitRepoRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!Directory.Exists(path)) return false;
                string dotGit = Path.Combine(path, ".git");
                return Directory.Exists(dotGit) || File.Exists(dotGit);
            }
            catch
            {
                return false;
            }
        }
    }
}
