using System;
using System.Diagnostics;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One-shot startup migration that relocates Phase-1 worktrees from the
    /// sibling layout (<c>{repoParent}/{repoName}-worktrees/</c>) to the child
    /// layout (<c>{repoRoot}/worktrees/</c>) introduced by task c6ed236c.
    ///
    /// <para>Reads every <c>status='active'</c> row from <c>task_worktrees</c>,
    /// detects rows still on the old layout, runs <c>git worktree move</c>, and
    /// updates the DB row on success. Failures are logged and left in place —
    /// the operation is idempotent and retries on every startup until every
    /// row has moved (then the settings flag is set and the service no-ops).</para>
    ///
    /// <para>The plan's "skip rows whose terminal is currently spawned" safety
    /// is delegated to the OS: Windows refuses to rename a directory whose
    /// handle is held by any process (terminal cwd, open file, etc.), so
    /// <c>git worktree move</c> exits non-zero and the row stays put. No
    /// explicit liveness check is needed at startup since MT itself hasn't
    /// spawned any terminals yet, but external shells (e.g. Claude Code agents
    /// whose cwd is inside an old-layout worktree) are still naturally
    /// guarded.</para>
    /// </summary>
    public static class WorktreeLayoutMigrationService
    {
        /// <summary>
        /// Settings key — set to "true" once every active worktree row has
        /// successfully moved to the new layout. The service exits early on
        /// subsequent startups when this is set.
        /// </summary>
        private const string CompleteFlagKey = "WorktreeLayoutMigrationV1Complete";

        /// <summary>
        /// Old layout's suffix on the parent dir (e.g. "MultiTerminal-worktrees").
        /// </summary>
        private const string OldLayoutSuffix = "-worktrees";

        /// <summary>
        /// New child-of-repo subfolder name.
        /// </summary>
        private const string NewSubfolder = "worktrees";

        /// <summary>
        /// Run the migration if the completion flag is not set. Safe to call
        /// multiple times. Swallows all exceptions — a migration failure must
        /// not block MT startup.
        /// </summary>
        public static void RunIfNeeded()
        {
            try
            {
                var settings = SettingsService.Default;
                if (string.Equals(settings.Get(CompleteFlagKey), "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                using var db = new TaskDatabase();
                var rows = db.ListActiveWorktrees();
                if (rows == null || rows.Count == 0)
                {
                    settings.Set(CompleteFlagKey, "true");
                    return;
                }

                bool allMigrated = true;
                int moved = 0;
                int skipped = 0;
                int failed = 0;

                foreach (var row in rows)
                {
                    if (row == null || string.IsNullOrEmpty(row.WorktreePath))
                    {
                        skipped++;
                        continue;
                    }

                    var planned = TryPlanMigration(row.WorktreePath);
                    if (planned == null)
                    {
                        // Already at new layout or doesn't match the old convention.
                        skipped++;
                        continue;
                    }

                    if (!Directory.Exists(planned.RepoRoot))
                    {
                        Debug.WriteLine($"[WorktreeLayoutMigration] Skipping {row.TaskId}: repoRoot {planned.RepoRoot} does not exist.");
                        skipped++;
                        continue;
                    }

                    if (!Directory.Exists(row.WorktreePath))
                    {
                        Debug.WriteLine($"[WorktreeLayoutMigration] Skipping {row.TaskId}: old worktree path {row.WorktreePath} does not exist (already removed?).");
                        skipped++;
                        continue;
                    }

                    if (TryMove(planned.RepoRoot, row.WorktreePath, planned.NewPath, out string error))
                    {
                        db.UpdateWorktreePath(row.TaskId, planned.NewPath);
                        Debug.WriteLine($"[WorktreeLayoutMigration] Moved {row.TaskId}: {row.WorktreePath} -> {planned.NewPath}");
                        moved++;
                    }
                    else
                    {
                        Debug.WriteLine($"[WorktreeLayoutMigration] Failed to move {row.TaskId} ({row.WorktreePath}): {error}");
                        failed++;
                        allMigrated = false;
                    }
                }

                if (allMigrated)
                {
                    settings.Set(CompleteFlagKey, "true");
                    Debug.WriteLine($"[WorktreeLayoutMigration] Complete. moved={moved} skipped={skipped} failed={failed}");
                }
                else
                {
                    Debug.WriteLine($"[WorktreeLayoutMigration] Partial. moved={moved} skipped={skipped} failed={failed}. Will retry on next startup.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WorktreeLayoutMigration] Aborted with exception: {ex.Message}");
            }
        }

        private sealed class MigrationPlan
        {
            public string RepoRoot { get; init; }

            public string NewPath { get; init; }
        }

        /// <summary>
        /// Returns a migration plan if <paramref name="oldWorktreePath"/> matches
        /// the old sibling layout; returns null when the path is already at the
        /// new layout, has no parent, or doesn't follow the convention.
        /// </summary>
        private static MigrationPlan TryPlanMigration(string oldWorktreePath)
        {
            string oldParent = Path.GetDirectoryName(oldWorktreePath);
            if (string.IsNullOrEmpty(oldParent)) return null;

            string oldParentName = Path.GetFileName(oldParent);
            if (string.IsNullOrEmpty(oldParentName)) return null;
            if (!oldParentName.EndsWith(OldLayoutSuffix, StringComparison.OrdinalIgnoreCase)) return null;

            string repoName = oldParentName.Substring(0, oldParentName.Length - OldLayoutSuffix.Length);
            if (string.IsNullOrEmpty(repoName)) return null;

            string repoParent = Path.GetDirectoryName(oldParent);
            if (string.IsNullOrEmpty(repoParent)) return null;

            string repoRoot = Path.Combine(repoParent, repoName);
            string taskFolder = Path.GetFileName(oldWorktreePath);
            if (string.IsNullOrEmpty(taskFolder)) return null;

            string newPath = Path.Combine(repoRoot, NewSubfolder, taskFolder);
            return new MigrationPlan { RepoRoot = repoRoot, NewPath = newPath };
        }

        /// <summary>
        /// Runs <c>git worktree move</c> synchronously. Ensures the new path's
        /// parent directory exists first (git requires it). Returns true only
        /// on exit code 0.
        /// </summary>
        // CA3003: oldPath comes from task_worktrees (written only by WorktreeManager,
        // which itself takes app-managed inputs), and repoRoot/newPath are derived
        // from oldPath via string surgery + hardcoded literals. No user-supplied path
        // text reaches Process.Start. Same trust model as WorktreeManager.
#pragma warning disable CA3003
        private static bool TryMove(string repoRoot, string oldPath, string newPath, out string error)
        {
            error = null;
            try
            {
                string newParent = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(newParent) && !Directory.Exists(newParent))
                {
                    Directory.CreateDirectory(newParent);
                }

                if (Directory.Exists(newPath))
                {
                    error = $"target already exists: {newPath}";
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("worktree");
                psi.ArgumentList.Add("move");
                psi.ArgumentList.Add(oldPath);
                psi.ArgumentList.Add(newPath);

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode == 0) return true;
                error = $"git exit {proc.ExitCode}. stderr: {stderr.Trim()}; stdout: {stdout.Trim()}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
#pragma warning restore CA3003
    }
}
