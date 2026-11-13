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
        /// <summary>
        /// Reads the panes open right now. Called by every check, including the one the move itself runs
        /// after the Owner confirmed, so a terminal opened while the confirmation was up still refuses the move.
        /// </summary>
        public Func<IReadOnlyList<RelocationPane>> GetPanes { get; init; } = () => Array.Empty<RelocationPane>();

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

            // Both pipes are drained at once, so a chatty stderr cannot block git before the timeout applies.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "git did not finish within 60 seconds");
            }

            return (process.ExitCode, (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim());
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
                project.Id, project.Path, project.SourcePath, newRoot, moveFolder, _host.GetPanes?.Invoke(),
                Directory.Exists, HasEntries).ToList();

            string from = RelocationPaths.Normalize(project.Path);
            string to = RelocationPaths.Normalize(newRoot);
            if (!moveFolder && problems.Count == 0)
                problems.AddRange(CheckSameProject(project, to));

            if (problems.Count == 0)
            {
                using var connection = _host.OpenDatabase();
                problems.AddRange(ProjectRelocationStore.FindConflicts(connection, to));

                // Another registered project inside this folder would have its row moved but not its Claude
                // history, previous folders or cached copy (pipeline Run 1, Debugger).
                foreach (var name in ProjectRelocationStore.FindProjectsUnder(connection, from, project.Id))
                    problems.Add($"The project '{name}' is registered inside this folder. Move or remove it first, then move this one.");
            }

            if (problems.Count == 0)
                problems.AddRange(FindHistoryCollisions(from, to));

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
                        // Registered BEFORE the copy starts, so a cancel or a failure half way removes what was
                        // copied (pipeline Run 1). Check guaranteed the destination was new or empty.
                        undo.Push(() => TryDeleteCopy(to));
                        CopyVerified(from, to, progress, cancel);
                        result.Done.Add($"Copied the project to '{to}' and checked every file's contents against the original.");
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
                    db = ProjectRelocationStore.Apply(connection, project.Id, project.Name, from, to, claudeMoves);
                }

                if (!db.Succeeded)
                    throw new RelocationAbortedException("MT could not update its records: " + db.Error);

                // Committed: from here on nothing is undone; problems become warnings.
                undo.Clear();
                result.Succeeded = true;
                result.Done.Add($"Updated {db.Total} record(s): " +
                    string.Join(", ", db.RowsChanged.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
            }
#pragma warning disable CA1031 // Every failure before the commit must run the undo: "fully moved or untouched" (pipeline Run 1).
            catch (Exception ex)
            {
                string why = ex is OperationCanceledException ? "The move was cancelled." : ex.Message;
                result.Refusals.Add(why + " Nothing was changed.");
                while (undo.Count > 0)
                {
                    try { undo.Pop()(); }
                    catch (Exception undoEx)
                    {
                        result.Refusals.Add("Undoing part of the move failed: " + undoEx.Message + " Check both folders.");
                    }
                }
            }
#pragma warning restore CA1031
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

        /// <summary>
        /// Copies <paramref name="from"/> to <paramref name="to"/> and proves the copy (pipeline Run 1, Codex):
        /// <list type="bullet">
        ///   <item>refuses a source holding a junction or symbolic link, which a recursive copy would follow
        ///   out of the project (or into a loop);</item>
        ///   <item>hashes each file while reading it and re-hashes the written copy, so equal sizes with different
        ///   contents fail;</item>
        ///   <item>re-reads the source listing afterwards and fails if any file appeared, vanished, or changed
        ///   size or write time while the copy ran.</item>
        /// </list>
        /// A failure throws; the caller's undo removes the partial copy.
        /// </summary>
        private static void CopyVerified(string from, string to, IProgress<RelocationProgress> progress, CancellationToken cancel)
        {
            var links = FindReparsePoints(from).Take(3).ToList();
            if (links.Count > 0)
                throw new RelocationAbortedException(
                    "The project contains a junction or symbolic link (" + string.Join(", ", links.Select(l => Path.GetRelativePath(from, l))) +
                    "). Copying it to another drive would follow the link, so MT will not move it. Remove the link or move the folder yourself.");

            var before = Snapshot(from);
            long totalBytes = before.Values.Sum(v => v.Length);

            var drive = new DriveInfo(Path.GetPathRoot(to));
            if (drive.AvailableFreeSpace < totalBytes)
                throw new RelocationAbortedException($"'{drive.Name}' has {FormatBytes(drive.AvailableFreeSpace)} free; the project needs {FormatBytes(totalBytes)}.");

            Directory.CreateDirectory(to);
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

            long copied = 0;
            int lastPercent = -1;
            foreach (var (relative, info) in before)
            {
                cancel.ThrowIfCancellationRequested();
                string source = Path.Combine(from, relative);
                string target = Path.Combine(to, relative);
                byte[] sourceHash = CopyHashing(source, target);
                if (!sourceHash.AsSpan().SequenceEqual(HashFile(target)))
                    throw new RelocationAbortedException($"The copy of '{relative}' does not match the original.");
                File.SetAttributes(target, File.GetAttributes(source));

                copied += info.Length;
                int percent = totalBytes == 0 ? 100 : (int)(copied * 100 / totalBytes);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(new RelocationProgress($"Copying files ({FormatBytes(copied)} of {FormatBytes(totalBytes)})", percent));
                }
            }

            var after = Snapshot(from);
            var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
                .Take(3).ToList();
            if (changed.Count > 0)
                throw new RelocationAbortedException(
                    "Files in the project changed while it was being copied (" + string.Join(", ", changed) +
                    "). Close whatever is writing to it and try again.");

            int copiedFiles = Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).Count();
            if (copiedFiles != before.Count)
                throw new RelocationAbortedException($"The copy holds {copiedFiles} files; the original has {before.Count}.");
        }

        private static Dictionary<string, (long Length, DateTime WrittenUtc)> Snapshot(string root)
        {
            var map = new Dictionary<string, (long Length, DateTime WrittenUtc)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(root, file.FullName)] = (file.Length, file.LastWriteTimeUtc);
            return map;
        }

        /// <summary>Folders and files under <paramref name="root"/> that are junctions or symbolic links, without following them.</summary>
        internal static IEnumerable<string> FindReparsePoints(string root)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        yield return entry.FullName;
                    else if (entry is DirectoryInfo dir)
                        pending.Push(dir);
                }
            }
        }

        private static byte[] CopyHashing(string source, string target)
        {
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }

            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            return sha.GetHashAndReset();
        }

        private static byte[] HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return System.Security.Cryptography.SHA256.HashData(stream);
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
        /// A history folder for the new location that already exists while the old location has one to move
        /// (pipeline Run 1, Codex). Moving would leave the old sessions unreachable, so the move is refused.
        /// </summary>
        private IEnumerable<string> FindHistoryCollisions(string from, string to)
        {
            if (from == null || to == null) yield break;
            string newName = SessionCatalog.EncodeProjectPath(to);
            foreach (var configRoot in _host.ClaudeConfigRoots ?? Array.Empty<string>())
            {
                string projectsDir = Path.Combine(configRoot, "projects");
                if (!Directory.Exists(projectsDir)) continue;
                foreach (var (folder, suffix) in FindHistoryFolders(projectsDir, from))
                {
                    string target = Path.Combine(projectsDir, newName + suffix);
                    if (!string.Equals(folder, target, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                        yield return $"Claude Code already has history for the new folder ('{target}'). Moving would hide this project's past sessions. Choose another folder, or remove that history first.";
                }
            }
        }

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
