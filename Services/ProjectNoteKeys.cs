using System;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Which project_note_tabs key a project's notes live under. Moved out of TerminalDocument
    /// (task 5d94ac91) so the Notes pane and the agent-notes REST endpoint resolve a project the SAME
    /// way: a note an agent files under a different spelling of the project than the pane reads would
    /// be written successfully and never seen.
    /// </summary>
    public static class ProjectNoteKeys
    {
        /// <summary>
        /// Resolves the canonical, registered project path for the Notes/Sessions
        /// HUD panes (which are keyed per-project, not per-folder). Prefers a lookup
        /// by projectId; falls back to matching the folder against the registered
        /// project list. Returns null when the project can't be resolved to a
        /// registered entry (caller decides the fallback). Never returns a raw
        /// worktree path — that's the whole point: worktree paths have no notes and
        /// cause an empty pane plus an orphan auto-created note-tab row.
        ///
        /// The canonical key is <c>SourcePath ?? Path</c> — the SAME precedence the
        /// launcher uses to pick a terminal's working directory (LaunchCommandBuilder
        /// "SourcePath → Path", Project.cs "Use SourcePath as the canonical field").
        /// Historical note tabs were keyed by that working directory, so resolving by
        /// Path alone would re-key Notes to a different path than the data lives under
        /// for any project whose SourcePath and Path diverge — re-introducing the
        /// empty pane this fix is meant to eliminate. We also match the folder against
        /// BOTH identity fields so either one locates the project.
        /// </summary>
        public static string TryResolve(ProjectDatabase projectDb, string projectId, string folder, Action<string> trace = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(projectId))
                {
                    // GetRichProject (not GetProject): only the rich Models.Project
                    // carries SourcePath; the lightweight MCPServer.Models.Project doesn't.
                    string canon = ForProject(projectDb?.GetRichProject(projectId));
                    if (!string.IsNullOrEmpty(canon)) return canon;
                }

                if (!string.IsNullOrEmpty(folder))
                {
                    string normFolder = NormalizeProjectPath(folder);
                    // Use the rich project list (carries SourcePath) so the folder
                    // match can consider both identity fields. Ambiguity guard: if the
                    // folder matches more than one DISTINCT project key (e.g. project
                    // A's SourcePath == project B's Path, since the schema enforces no
                    // uniqueness on those fields), DON'T remap — picking the first row
                    // would silently bind Notes/Sessions to the wrong project's notes.
                    var projects = projectDb?.GetAllRichProjects();
                    if (projects != null)
                    {
                        string firstMatch = null;
                        foreach (var p in projects)
                        {
                            if (ProjectFieldMatchesFolder(p?.SourcePath, normFolder) ||
                                ProjectFieldMatchesFolder(p?.Path, normFolder))
                            {
                                string canon = ForProject(p);
                                if (string.IsNullOrEmpty(canon)) continue;
                                if (firstMatch == null)
                                {
                                    firstMatch = canon;
                                }
                                // Compare distinctness on the NORMALIZED key so two registry
                                // rows for the SAME folder that differ only by formatting
                                // (trailing slash, slash style, case) don't read as an
                                // ambiguous multi-project match. Only a genuinely different
                                // folder trips the guard. We still RETURN the raw firstMatch.
                                else if (!string.Equals(NormalizeProjectPath(firstMatch), NormalizeProjectPath(canon),
                                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    trace?.Invoke($"Notes key: folder '{folder}' matches multiple projects — not remapping to avoid cross-project notes");
                                    return null;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(firstMatch)) return firstMatch;
                    }
                }
            }
            catch { /* non-critical — caller falls back */ }

            return null;
        }

        /// <summary>
        /// The note-tab key for a project: the RAW <c>SourcePath ?? Path</c>, matching the
        /// launcher's working-dir precedence; null when the project is null or has neither
        /// field.
        ///
        /// Kept raw here for symmetry with launch/folder resolution — the same
        /// <c>SourcePath ?? Path</c> identity is also matched against both fields during
        /// folder lookup, where the original spelling matters. Canonicalization of the
        /// stored key is owned by the persistence layer: <c>TaskDatabase.NormalizeNoteTabPath</c>
        /// (full path + trailing-separator trim) runs on every note-tab read/write and a
        /// one-time migration rewrites pre-existing rows, so trailing-slash / slash-style
        /// spelling differences in the key handed down from here are absorbed downstream —
        /// this method need not (and must not, to stay symmetric with folder matching)
        /// normalize. Case-insensitive needs are applied at each comparison site
        /// (OrdinalIgnoreCase), not baked into the stored key.
        /// </summary>
        public static string ForProject(MultiTerminal.Models.Project project)
        {
            if (project == null) return null;
            string raw = !string.IsNullOrEmpty(project.SourcePath) ? project.SourcePath : project.Path;
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        /// <summary>True when a project identity field normalizes to the given folder.</summary>
        private static bool ProjectFieldMatchesFolder(string projectField, string normalizedFolder)
        {
            if (string.IsNullOrEmpty(projectField)) return false;
            return string.Equals(NormalizeProjectPath(projectField), normalizedFolder,
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Canonicalizes a project path so the same project always maps to one
        /// note-tab key: full path with trailing separators trimmed. Guards against
        /// GetFullPath throwing on malformed input by falling back to a simple trim.
        /// </summary>
        private static string NormalizeProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }
    }
}
