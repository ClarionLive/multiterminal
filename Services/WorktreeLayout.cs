using System;
using System.Collections.Generic;
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

        /// <summary>
        /// True when a path segment is an 8-hex MT worktree id (optionally
        /// <c>&lt;id&gt;--&lt;slug&gt;</c> for a helper worktree). The id is the
        /// first 8 chars of a task id (see <see cref="WorktreeNaming.ShortId"/>).
        /// </summary>
        public static bool IsWorktreeIdSegment(string seg)
        {
            if (string.IsNullOrEmpty(seg)) return false;
            int dash = seg.IndexOf("--", StringComparison.Ordinal);
            string id = dash >= 0 ? seg.Substring(0, dash) : seg;
            if (id.Length != 8) return false;
            foreach (char c in id)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>
        /// True when <paramref name="path"/> matches one of the MT git-worktree
        /// layouts — a separator-bounded <c>worktrees</c> (or <c>*-worktrees</c>)
        /// directory immediately followed by an 8-hex id segment (see
        /// <see cref="IsWorktreeIdSegment"/>). The three historical layouts are
        /// <c>&lt;repo&gt;\.claude\worktrees\&lt;id&gt;</c> (current),
        /// <c>&lt;repo&gt;\worktrees\&lt;id&gt;</c>, and
        /// <c>&lt;repo&gt;-worktrees\&lt;id&gt;</c> (legacy).
        ///
        /// <para>Requiring BOTH the worktrees directory AND a following id segment
        /// stops a legitimate ad-hoc folder that merely contains a "worktrees"
        /// directory (e.g. <c>D:\clients\worktrees\demo</c>) from being mistaken
        /// for a worktree — a plain substring match would misclassify it.</para>
        /// </summary>
        public static bool LooksLikeWorktreePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var segs = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length - 1; i++)
            {
                bool isWorktreesDir =
                    segs[i].Equals("worktrees", StringComparison.OrdinalIgnoreCase) ||
                    segs[i].EndsWith("-worktrees", StringComparison.OrdinalIgnoreCase);
                if (isWorktreesDir && IsWorktreeIdSegment(segs[i + 1]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Map a path that is AT or UNDER a git-worktree directory to its STABLE on-disk
        /// equivalent. Walks up to the worktree-id directory
        /// (<c>.../&lt;worktreesDir&gt;/&lt;id&gt;</c>), derives the <c>.git</c>-validated repo root,
        /// and re-roots any suffix that was under the id directory beneath that repo root:
        ///
        /// <list type="bullet">
        ///   <item><c>{repo}/.claude/worktrees/&lt;id&gt;</c> → <paramref name="repoRoot"/>=<c>{repo}</c>,
        ///   <paramref name="mappedPath"/>=<c>{repo}</c>.</item>
        ///   <item><c>{repo}/.claude/worktrees/&lt;id&gt;/Sub/Proj</c> → <paramref name="repoRoot"/>=<c>{repo}</c>,
        ///   <paramref name="mappedPath"/>=<c>{repo}/Sub/Proj</c>.</item>
        /// </list>
        ///
        /// <para>Handles the current <c>.claude/worktrees</c> layout plus both legacy layouts
        /// (<c>{repo}/worktrees/&lt;id&gt;</c>, <c>{repoParent}/{repo}-worktrees/&lt;id&gt;</c>), and a
        /// PRUNED worktree: only the ephemeral <c>worktrees/&lt;id&gt;</c> leaf is removed on prune; the
        /// repo root (and its <c>.git</c>) persists, so the derivation + <see cref="IsLikelyGitRepoRoot"/>
        /// gate still resolve. Returns <c>false</c> when no worktree-id directory is found on the
        /// path or the derived repo root can't be validated as a git repo. Task 19d0d867.</para>
        /// </summary>
        public static bool TryMapWorktreePath(string path, out string repoRoot, out string mappedPath)
        {
            repoRoot = null;
            mappedPath = null;
            if (string.IsNullOrWhiteSpace(path)) return false;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }

            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Walk up until we hit the worktree-id directory: its name is an 8-hex id AND its parent
            // is a worktrees/*-worktrees dir from which a real repo root is derivable. Segments popped
            // along the way (a project registered at a worktree SUBDIR) become the suffix re-rooted
            // under the repo root, so a csproj-in-subfolder project still lands on a watchable path.
            var suffix = new List<string>();
            string cur = trimmed;
            while (!string.IsNullOrEmpty(cur))
            {
                string name = Path.GetFileName(cur);
                string parent = Path.GetDirectoryName(cur);

                if (!string.IsNullOrEmpty(parent)
                    && IsWorktreeIdSegment(name)
                    && TryDeriveRepoRootFromWorktreesDir(parent, out var root))
                {
                    // Re-root the popped suffix (reversed — we collected leaf-first) under the repo root.
                    string result = root;
                    for (int k = suffix.Count - 1; k >= 0; k--)
                        result = Path.Combine(result, suffix[k]);

                    repoRoot = root;
                    mappedPath = result;
                    return true;
                }

                suffix.Add(name);
                cur = parent;
            }

            return false;
        }

        /// <summary>
        /// Derive the <c>.git</c>-validated repo root from a worktree-parent directory, covering the
        /// current <c>.claude/worktrees</c> layout (repo root = parent of the <c>.claude</c> dir) and
        /// both legacy layouts. Returns <c>false</c> when the directory isn't a worktrees parent or
        /// the candidate isn't a git repo root.
        /// </summary>
        private static bool TryDeriveRepoRootFromWorktreesDir(string worktreesDir, out string repoRoot)
        {
            repoRoot = null;
            string worktreesName = Path.GetFileName(worktreesDir);
            string candidate = null;

            if (string.Equals(worktreesName, NewSubfolder, StringComparison.OrdinalIgnoreCase))
            {
                string grand = Path.GetDirectoryName(worktreesDir);
                if (string.IsNullOrEmpty(grand)) return false;

                // Current layout: {repoRoot}/.claude/worktrees/<id> — repo root is the parent of the
                // .claude dir. Legacy child layout: {repoRoot}/worktrees/<id>.
                candidate = string.Equals(Path.GetFileName(grand), ".claude", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(grand)
                    : grand;
            }
            else if (worktreesName != null && worktreesName.EndsWith(LegacySuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Legacy sibling layout: {repoParent}/{repo}-worktrees/<id>.
                string repoName = worktreesName.Substring(0, worktreesName.Length - LegacySuffix.Length);
                string grand = Path.GetDirectoryName(worktreesDir);
                if (!string.IsNullOrEmpty(grand) && !string.IsNullOrEmpty(repoName))
                    candidate = Path.Combine(grand, repoName);
            }

            if (IsLikelyGitRepoRoot(candidate))
            {
                repoRoot = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Derive the STABLE repo root for a path at/under a git-worktree directory. Thin wrapper over
        /// <see cref="TryMapWorktreePath"/> that discards the re-rooted suffix — use this when you only
        /// need the repo root, not the project's exact subfolder.
        /// </summary>
        public static bool TryGetRepoRootForWorktreePath(string path, out string repoRoot)
            => TryMapWorktreePath(path, out repoRoot, out _);

        /// <summary>
        /// Resolve a STABLE canonical project path when <paramref name="path"/> is a git-worktree
        /// path (which must never be a project's registered <c>path</c> — task 19d0d867).
        ///
        /// <para>Priority: prefer <paramref name="sourcePathFallback"/> when it is a real, non-worktree
        /// folder that belongs to the SAME repo as the worktree — i.e. it IS the derived repo root or a
        /// descendant of it. This preserves a csproj-in-subfolder layout (repo root <c>X</c> but project
        /// at <c>X\Sub</c>) that bare repo-root derivation would lose (re-orphaning the project from
        /// <see cref="CodeGraphWatcher"/>'s top-level-csproj gate), while the same-repo containment
        /// check stops an unrelated or hostile <c>source_path</c> from redirecting the canonical path and
        /// the <c>.claude/project.json</c> write to an arbitrary directory (OWASP A01/A04). Otherwise
        /// fall back to the suffix-preserving mapped path — the repo root for an exact worktree dir, or
        /// <c>repoRoot/&lt;suffix&gt;</c> for a project registered at a worktree SUBDIR.</para>
        ///
        /// <para>Returns <c>false</c> (and a null <paramref name="stablePath"/>) when
        /// <paramref name="path"/> is NOT a worktree path, or when no stable target can be resolved
        /// (e.g. the repo root itself is gone) — callers then leave the path unchanged.</para>
        /// </summary>
        public static bool TryResolveStableProjectPath(string path, string sourcePathFallback, out string stablePath)
        {
            stablePath = null;
            if (!LooksLikeWorktreePath(path)) return false;

            bool haveMap = TryMapWorktreePath(path, out string repoRoot, out string mappedPath);

            // Prefer source_path ONLY when it's a real, non-worktree dir within the SAME repo as the
            // worktree (== the derived repo root or a descendant of it). The containment check is what
            // makes source_path safe to honor here: without it a poisoned project (worktree-looking
            // path + arbitrary existing source_path) could redirect the canonical path / project.json
            // write to an unrelated directory. The legitimate csproj-in-subfolder case still passes
            // because its source_path is a descendant of the repo root.
            // CA3003: sourcePathFallback / the derived paths are app-managed project-registry values
            // (a registered project root or user-selected folder), not attacker-controlled web input;
            // we only READ with Directory.Exists here (never open/write a file with caller content).
            // Same justification as the existing ProjectService/ProjectDatabase CA3003 suppressions.
#pragma warning disable CA3003
            if (haveMap
                && !string.IsNullOrWhiteSpace(sourcePathFallback)
                && !LooksLikeWorktreePath(sourcePathFallback))
            {
                try
                {
                    if (Directory.Exists(sourcePathFallback))
                    {
                        string srcFull = Path.GetFullPath(sourcePathFallback);
                        if (IsSameOrDescendant(repoRoot, srcFull))
                        {
                            stablePath = srcFull;
                            return true;
                        }
                    }
                }
                catch { /* fall through to the derived mapping */ }
            }

            if (haveMap)
            {
                // Return the suffix-preserving mapped path. For an exact worktree dir this IS the repo
                // root; for a worktree SUBDIR it's repoRoot/<suffix> — the project's durable identity,
                // returned even if that subfolder doesn't exist on disk YET (e.g. it hasn't been merged
                // into the main checkout). Collapsing to the bare repo root instead would permanently
                // hide a csproj-in-subfolder project from CodeGraphWatcher's top-level-csproj gate once
                // the subfolder later appears (Codex adversary round 2). The suffix derives from the
                // GetFullPath-normalized worktree path, so it cannot contain ".." and always stays
                // within repoRoot; repoRoot itself is .git-validated, so the no-suffix case exists.
                stablePath = mappedPath;
                return true;
            }
#pragma warning restore CA3003

            return false;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or a directory
        /// beneath it (case-insensitive, separator-bounded so a sibling like <c>X-other</c> isn't
        /// treated as under <c>X</c>). Both arguments are expected to be absolute, normalized paths.
        /// </summary>
        private static bool IsSameOrDescendant(string root, string candidate)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
