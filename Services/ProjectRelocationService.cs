using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>One progress report from a Move Project.</summary>
    /// <param name="Step">What is happening, in plain words.</param>
    /// <param name="Percent">0-100 while copying; null when the step has no measurable size.</param>
    public sealed record RelocationProgress(string Step, int? Percent);

    /// <summary>What a Move Project did, for the summary the Owner sees (ticket 6f879683).</summary>
    public sealed class RelocationResult
    {
        /// <summary>True when the project now lives in the new folder.</summary>
        public bool Succeeded { get; set; }

        /// <summary>Why nothing changed. Set only when <see cref="Succeeded"/> is false.</summary>
        public List<string> Refusals { get; } = new List<string>();

        /// <summary>What was done, one line each.</summary>
        public List<string> Done { get; } = new List<string>();

        /// <summary>Things the move could not do; the project works, but these need a look.</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Where the original folder now is (cross-volume move only), or null.</summary>
        public string OldFolderKeptAt { get; set; }
    }

    /// <summary>
    /// The side effects a move needs from the running app, so the service itself stays testable
    /// (ticket 6f879683). MainForm supplies the live versions.
    /// </summary>
    public sealed class RelocationHost
    {
        /// <summary>The panes open right now (read on the UI thread before the move starts).</summary>
        public IReadOnlyList<RelocationPane> Panes { get; init; } = Array.Empty<RelocationPane>();

        /// <summary>Every Claude config folder (<c>ClaudeProfileResolver.GetAllRoots</c>).</summary>
        public IReadOnlyList<string> ClaudeConfigRoots { get; init; } = Array.Empty<string>();

        /// <summary>Stops watchers that hold the project folder open (the code graph watcher).</summary>
        public Action PauseWatchers { get; init; }

        /// <summary>Starts them again.</summary>
        public Action ResumeWatchers { get; init; }

        /// <summary>Refreshes caches and tells the UI the project changed.</summary>
        public Action<string> ProjectChanged { get; init; }

        /// <summary>Opens a connection to multiterminal.db. Defaults to <see cref="MultiterminalDb.Open"/>.</summary>
        public Func<SQLiteConnection> OpenDatabase { get; init; } = MultiterminalDb.Open;

        /// <summary>Runs <c>git</c> with the arguments in a folder; returns (exit code, output). Null skips git.</summary>
        public Func<string, string[], (int ExitCode, string Output)> RunGit { get; init; } = RunGitProcess;

        internal static (int ExitCode, string Output) RunGitProcess(string workingDirectory, string[] args)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var process = Process.Start(info);
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "git did not finish within 60 seconds");
            }

            return (process.ExitCode, output.Trim());
        }
    }

    /// <summary>
    /// Move Project (ticket 6f879683). Two modes:
    /// <list type="bullet">
    ///   <item><see cref="Move"/>: MT moves the folder. Same drive: one rename. Another drive: copy,
    ///   verify, and keep the original renamed to <c>&lt;name&gt;_to_be_deleted</c>. Never deletes it.</item>
    ///   <item><see cref="Repoint"/>: the folder was already moved outside MT; MT only catches up.</item>
    /// </list>
    /// Order: checks, pause watchers, move the folder, rename Claude history folders, ONE database
    /// transaction, git worktree repair, code graph reset, refresh, resume watchers. Anything that fails
    /// before the database commit is undone, so the project is either fully moved or untouched.
    /// Runs off the UI thread; it never shows UI itself.
    /// </summary>
    public sealed class ProjectRelocationService
    {
        /// <summary>The suffix the original folder gets after a cross-drive move.</summary>
        public const string KeptSuffix = "_to_be_deleted";

        private readonly RelocationHost _host;

        public ProjectRelocationService(RelocationHost host) =>
            _host = host ?? throw new ArgumentNullException(nameof(host));

        /// <summary>Moves the project's folder to <paramref name="newRoot"/> and repoints everything.</summary>
        public RelocationResult Move(Models.Project project, string newRoot, IProgress<RelocationProgress> progress = null, CancellationToken cancel = default) =>
            Run(project, newRoot, moveFolder: true, progress, cancel);

        /// <summary>Repoints the project at a folder that was already moved to <paramref name="newRoot"/>.</summary>
        public RelocationResult Repoint(Models.Project project, string newRoot, IProgress<RelocationProgress> progress = null) =>
            Run(project, newRoot, moveFolder: false, progress, CancellationToken.None);

        /// <summary>
        /// Problems that would stop a move or repoint, without changing anything. The dialog shows these
        /// before the Owner confirms.
        /// </summary>
        public IReadOnlyList<string> Check(Models.Project project, string newRoot, bool moveFolder)
        {
            if (project == null) return new[] { "No project." };
            var problems = ProjectRelocationChecks.Check(
                project.Id, project.Path, project.SourcePath, newRoot, moveFolder, _host.Panes,
                Directory.Exists, HasEntries).ToList();

            if (!moveFolder && problems.Count == 0)
                problems.AddRange(CheckSameProject(project, RelocationPaths.Normalize(newRoot)));

            if (problems.Count == 0)
            {
                using var connection = _host.OpenDatabase();
                problems.AddRange(ProjectRelocationStore.FindConflicts(connection, RelocationPaths.Normalize(newRoot)));
            }

            return problems;
        }

        private RelocationResult Run(Models.Project project, string newRoot, bool moveFolder, IProgress<RelocationProgress> progress, CancellationToken cancel)
        {
            var result = new RelocationResult();
            var problems = Check(project, newRoot, moveFolder);
            if (problems.Count > 0)
            {
                result.Refusals.AddRange(problems);
                return result;
            }

            string from = RelocationPaths.Normalize(project.Path);
            string to = RelocationPaths.Normalize(newRoot);
            bool crossVolume = moveFolder && !SameVolume(from, to);
            var undo = new Stack<Action>();
            bool watchersPaused = false;

            try
            {
                progress?.Report(new RelocationProgress("Pausing MT's folder watchers", null));
                _host.PauseWatchers?.Invoke();
                watchersPaused = true;

                if (moveFolder)
                {
                    if (crossVolume)
                    {
                        CopyVerified(from, to, progress, cancel);
                        undo.Push(() => TryDeleteCopy(to));
                        result.Done.Add($"Copied the project to '{to}' and checked the copy.");
                    }
                    else
                    {
                        progress?.Report(new RelocationProgress("Moving the folder", null));
                        string parent = Path.GetDirectoryName(to);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        if (Directory.Exists(to)) Directory.Delete(to); // empty, checked above
                        Directory.Move(from, to);
                        undo.Push(() => Directory.Move(to, from));
                        result.Done.Add($"Moved the folder to '{to}'.");
                    }
                }

                progress?.Report(new RelocationProgress("Moving Claude Code's history", null));
                var claudeMoves = MoveClaudeHistory(from, to, undo, result);

                progress?.Report(new RelocationProgress("Updating MT's records", null));
                RelocationDbResult db;
                using (var connection = _host.OpenDatabase())
                {
                    db = ProjectRelocationStore.Apply(connection, project.Id, from, to, claudeMoves);
                    if (db.Succeeded) ResetCodeGraphFreshness(connection, project.Name);
                }

                if (!db.Succeeded)
                    throw new RelocationAbortedException("MT could not update its records: " + db.Error);

                // Committed: from here on nothing is undone; problems become warnings.
                undo.Clear();
                result.Succeeded = true;
                result.Done.Add($"Updated {db.Total} record(s): " +
                    string.Join(", ", db.RowsChanged.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is RelocationAbortedException
                                       || ex is OperationCanceledException || ex is SQLiteException)
            {
                string why = ex is OperationCanceledException ? "The move was cancelled." : ex.Message;
                result.Refusals.Add(why + " Nothing was changed.");
                while (undo.Count > 0)
                {
                    try { undo.Pop()(); }
                    catch (Exception undoEx) when (undoEx is IOException || undoEx is UnauthorizedAccessException)
                    {
                        result.Refusals.Add("Undoing part of the move failed: " + undoEx.Message + " Check both folders.");
                    }
                }
            }
            finally
            {
                if (watchersPaused) TryRun(_host.ResumeWatchers, "Restarting MT's folder watchers", result);
            }

            if (!result.Succeeded) return result;

            progress?.Report(new RelocationProgress("Repairing git worktrees", null));
            RepairWorktrees(to, result);

            if (crossVolume)
            {
                progress?.Report(new RelocationProgress("Renaming the original folder", null));
                KeepOriginal(from, result);
            }

            TryRun(() => _host.ProjectChanged?.Invoke(project.Id), "Refreshing MT's screens", result);
            FlagCommandsMentioning(project, from, result);
            return result;
        }

        // ── Folder copy (cross-volume) ──────────────────────────────────────────────

        private static void CopyVerified(string from, string to, IProgress<RelocationProgress> progress, CancellationToken cancel)
        {
            var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList();
            long totalBytes = files.Sum(f => new FileInfo(f).Length);

            var drive = new DriveInfo(Path.GetPathRoot(to));
            if (drive.AvailableFreeSpace < totalBytes)
                throw new RelocationAbortedException($"'{drive.Name}' has {FormatBytes(drive.AvailableFreeSpace)} free; the project needs {FormatBytes(totalBytes)}.");

            Directory.CreateDirectory(to);
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

            long copied = 0;
            int lastPercent = -1;
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                File.Copy(file, target, overwrite: false);
                File.SetAttributes(target, File.GetAttributes(file));
                copied += new FileInfo(file).Length;
                int percent = totalBytes == 0 ? 100 : (int)(copied * 100 / totalBytes);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(new RelocationProgress($"Copying files ({FormatBytes(copied)} of {FormatBytes(totalBytes)})", percent));
                }
            }

            var copiedFiles = Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).ToList();
            long copiedBytes = copiedFiles.Sum(f => new FileInfo(f).Length);
            if (copiedFiles.Count != files.Count || copiedBytes != totalBytes)
                throw new RelocationAbortedException(
                    $"The copy does not match the original ({copiedFiles.Count} files, {FormatBytes(copiedBytes)} against {files.Count} files, {FormatBytes(totalBytes)}).");
        }

        private static void TryDeleteCopy(string to)
        {
            // Only ever the copy this move created in a folder that was new or empty before it.
            if (!Directory.Exists(to)) return;
            foreach (var file in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(to, recursive: true);
        }

        private static void KeepOriginal(string from, RelocationResult result)
        {
            string target = from + KeptSuffix;
            for (int n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = from + KeptSuffix + n;

            try
            {
                Directory.Move(from, target);
                result.OldFolderKeptAt = target;
                result.Done.Add($"Renamed the original folder to '{target}'. Delete it yourself once you're happy with the move.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.OldFolderKeptAt = from;
                result.Warnings.Add($"The original folder '{from}' could not be renamed ({ex.Message}). The project already works from the new folder; rename or delete the old one yourself.");
            }
        }

        // ── Claude Code history ─────────────────────────────────────────────────────

        /// <summary>
        /// Renames each Claude history folder of the old root (and its worktree folders) to the new root's
        /// encoding, under every config root. Returns the (old, new) folder pairs for the database rewrite.
        /// </summary>
        private List<(string OldFolder, string NewFolder)> MoveClaudeHistory(string from, string to, Stack<Action> undo, RelocationResult result)
        {
            var moves = new List<(string, string)>();
            string newName = SessionCatalog.EncodeProjectPath(to);
            foreach (var configRoot in _host.ClaudeConfigRoots ?? Array.Empty<string>())
            {
                string projectsDir = Path.Combine(configRoot, "projects");
                if (!Directory.Exists(projectsDir)) continue;

                foreach (var (folder, suffix) in FindHistoryFolders(projectsDir, from))
                {
                    string target = Path.Combine(projectsDir, newName + suffix);
                    if (string.Equals(folder, target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(target))
                    {
                        result.Warnings.Add($"Claude Code history '{Path.GetFileName(folder)}' was not moved because '{Path.GetFileName(target)}' already exists in '{projectsDir}'.");
                        continue;
                    }

                    Directory.Move(folder, target);
                    undo.Push(() => Directory.Move(target, folder));
                    moves.Add((folder, target));
                }
            }

            if (moves.Count > 0)
                result.Done.Add($"Moved {moves.Count} Claude Code history folder(s), so past sessions can still be resumed.");
            return moves;
        }

        /// <summary>
        /// The history folders on disk for <paramref name="root"/>: the root's own folder and its
        /// <c>--claude-worktrees-*</c> folders, in any of the encodings Claude Code and MT have used
        /// (every non-alphanumeric to '-', or only ':' '\' '/'). Each with the suffix after the root's part.
        /// </summary>
        internal static IEnumerable<(string Folder, string Suffix)> FindHistoryFolders(string projectsDir, string root)
        {
            var prefixes = new[]
            {
                SessionCatalog.EncodeProjectPath(root),
                SessionDiscovery.GetProjectFolderName(root),
            }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var dir in Directory.EnumerateDirectories(projectsDir))
            {
                string name = Path.GetFileName(dir);
                foreach (var prefix in prefixes)
                {
                    if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return (dir, "");
                        break;
                    }

                    const string worktrees = "--claude-worktrees-";
                    if (name.StartsWith(prefix + worktrees, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length + worktrees.Length)
                    {
                        yield return (dir, name.Substring(prefix.Length));
                        break;
                    }
                }
            }
        }

        // ── git and code graph ──────────────────────────────────────────────────────

        private void RepairWorktrees(string root, RelocationResult result)
        {
            if (_host.RunGit == null || !WorktreeLayout.IsLikelyGitRepoRoot(root)) return;

            var worktrees = new List<string>();
            foreach (var parent in new[] { Path.Combine(root, ".claude", "worktrees"), Path.Combine(root, WorktreeLayout.NewSubfolder) })
            {
                if (!Directory.Exists(parent)) continue;
                worktrees.AddRange(Directory.EnumerateDirectories(parent).Where(d => File.Exists(Path.Combine(d, ".git"))));
            }

            try
            {
                var (exit, output) = _host.RunGit(root, new[] { "worktree", "repair" }.Concat(worktrees).ToArray());
                if (exit == 0)
                    result.Done.Add(worktrees.Count == 0 ? "Checked git's worktree records." : $"Repaired git's records for {worktrees.Count} task worktree(s).");
                else
                    result.Warnings.Add($"'git worktree repair' failed ({output}). Run it yourself in '{root}'.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                result.Warnings.Add($"Could not run git ({ex.Message}). Run 'git worktree repair' yourself in '{root}'.");
            }
        }

        private static void ResetCodeGraphFreshness(SQLiteConnection connection, string projectName)
        {
            // The watcher skips a project indexed recently; the old index points at the old folder.
            using var command = new SQLiteCommand(
                "DELETE FROM cg_index_metadata WHERE key = @k AND EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='cg_index_metadata')",
                connection);
            command.Parameters.AddWithValue("@k", $"project:{projectName}:last_indexed");
            try { command.ExecuteNonQuery(); }
            catch (SQLiteException) { /* no code graph table yet: nothing to reset */ }
        }

        // ── Repoint mode ────────────────────────────────────────────────────────────

        private static IEnumerable<string> CheckSameProject(Models.Project project, string newRoot)
        {
            // Same project: its project.json names the same id, or it is a git repository.
            string found = ReadProjectJsonId(Path.Combine(newRoot, ".claude", "project.json"));
            if (string.Equals(found, project.Id, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();
            if (!string.IsNullOrEmpty(found))
                return new[] { $"'{newRoot}' holds a different MT project (id {found})." };
            if (WorktreeLayout.IsLikelyGitRepoRoot(newRoot))
                return Array.Empty<string>();
            return new[] { $"'{newRoot}' does not look like this project: it has no .claude\\project.json for it and is not a git repository." };
        }

        /// <summary>The "id" in a project.json, or null when the file is missing or unreadable.</summary>
        internal static string ReadProjectJsonId(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        return property.Value.GetString();
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException)
            {
                return null;
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static void FlagCommandsMentioning(Models.Project project, string from, RelocationResult result)
        {
            foreach (var (label, text) in new[]
            {
                ("Launch command", project.LaunchCommand), ("Build command", project.BuildCommand), ("Deploy command", project.DeployCommand),
            })
            {
                if (!string.IsNullOrEmpty(text) && text.IndexOf(from, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Warnings.Add($"The {label} still mentions the old folder; edit it in Project Settings.");
            }
        }

        private static void TryRun(Action action, string what, RelocationResult result)
        {
            try { action?.Invoke(); }
#pragma warning disable CA1031 // A failed refresh must not hide a completed move; it is reported instead.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                result.Warnings.Add($"{what} failed: {ex.Message}");
            }
        }

        private static bool HasEntries(string dir)
        {
            try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return true; }
        }

        internal static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        private static string FormatBytes(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
            : $"{bytes / 1024.0:0} KB";

        private sealed class RelocationAbortedException : Exception
        {
            public RelocationAbortedException(string message) : base(message) { }
        }
    }
}
