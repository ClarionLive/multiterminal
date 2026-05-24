using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Tracks live <see cref="CodeReviewPopupForm"/> instances keyed by
    /// taskId (task mode) or a synthesized "wt:" key (working-tree mode,
    /// Phase 3b). Reopening for the same key activates the existing window
    /// and (optionally) preselects a file. Concurrent reviews of different
    /// targets are supported. Task and working-tree keys live in the same
    /// dict but cannot collide: working-tree keys carry a fixed "wt:" prefix
    /// that task IDs (random 8-char hex) cannot.
    ///
    /// Plus a process-wide theme-broadcast helper so MainForm.ApplyTheme can
    /// keep live popups in sync without holding direct references.
    /// </summary>
    public static class CodeReviewPopupManager
    {
        private const string WorkingTreeKeyPrefix = "wt:";

        private static readonly object _sync = new object();
        private static readonly Dictionary<string, CodeReviewPopupForm> _instances =
            new Dictionary<string, CodeReviewPopupForm>(StringComparer.Ordinal);

        /// <summary>
        /// Build the registry key for a working-tree popup. Stable for a given
        /// (canonicalized repoRoot, filePath) pair so a repeated right-click
        /// reuses the open window instead of stacking duplicates. filePath
        /// participates in the key because each ad-hoc popup is presently
        /// constructed against a single file; multi-file working-tree popups
        /// will need a list-hashed key.
        /// </summary>
        public static string BuildWorkingTreeKey(string repoRoot, string filePath)
        {
            string canonicalRoot;
            try
            {
                canonicalRoot = string.IsNullOrEmpty(repoRoot)
                    ? string.Empty
                    : Path.GetFullPath(repoRoot);
            }
            catch (Exception)
            {
                canonicalRoot = repoRoot ?? string.Empty;
            }
            string canonicalFile = filePath ?? string.Empty;
            // Lower-case both halves: NTFS is case-insensitive, but a key
            // mismatch from differing case would silently spawn a duplicate
            // popup. OrdinalIgnoreCase-style normalization here matches the
            // path validation in CodeReviewService.GetCodeReviewDataWorkingTreeCore.
            return WorkingTreeKeyPrefix
                + canonicalRoot.ToLowerInvariant()
                + "|"
                + canonicalFile.ToLowerInvariant();
        }

        /// <summary>
        /// Open a popup for <paramref name="taskId"/>, or focus the existing
        /// one. <paramref name="filePath"/> (optional) tells the popup which
        /// file's diff to preselect — useful when opened from HUD Git or a
        /// task-card icon that targets a specific file.
        /// </summary>
        public static void OpenOrFocus(
            string taskId,
            string taskTitle,
            string filePath,
            bool isDarkTheme,
            MessageBroker broker,
            CodeReviewService crService,
            Form owner)
        {
            if (string.IsNullOrEmpty(taskId)) return;

            // Single critical section for the entire check-or-create so two
            // concurrent OpenOrFocus calls for the same taskId can't both
            // construct a form and stomp each other in the registry. A pair of
            // racing callers will each see one of two outcomes inside the lock:
            // (a) the existing form (focus path) or (b) a freshly-created form
            // already in the dict (winner). The losing thread takes the focus
            // path on what the winner just registered.
            CodeReviewPopupForm form;
            bool isNew;
            lock (_sync)
            {
                if (_instances.TryGetValue(taskId, out var existing) &&
                    existing != null && !existing.IsDisposed)
                {
                    form = existing;
                    isNew = false;
                }
                else
                {
                    form = new CodeReviewPopupForm(taskId, taskTitle ?? string.Empty, filePath);
                    _instances[taskId] = form;
                    isNew = true;
                }
            }

            if (!isNew)
            {
                try { form.PreselectFile(filePath); } catch { }
                return;
            }

            // Register the disposal-cleanup hook outside the lock — we just
            // need it wired before the form is Shown. ReferenceEquals guards
            // against a later OpenOrFocus replacing the entry (e.g. after
            // disposal) so we don't yank a successor out from under itself.
            form.FormClosed += (s, e) =>
            {
                lock (_sync)
                {
                    if (_instances.TryGetValue(taskId, out var current) &&
                        ReferenceEquals(current, form))
                    {
                        _instances.Remove(taskId);
                    }
                }
            };

            // Fire-and-forget Initialize — the WebView2 bootstrap is async but
            // the form is non-modal and must be Shown immediately so the user
            // sees the chrome while the webview loads.
            _ = InitializeAndShowAsync(form, broker, crService, isDarkTheme, owner);
        }

        /// <summary>
        /// Open a working-tree popup against <paramref name="filePaths"/> in
        /// <paramref name="repoRoot"/>, or focus the existing one when a popup
        /// is already open for the same target. Phase 3b path for files in
        /// the "Needs a quick task" group (and any other taskless review).
        ///
        /// Currently shaped for single-file callers — the registry key is
        /// derived from <paramref name="preselectFilePath"/> (or
        /// <c>filePaths[0]</c> as a fallback). Multi-file working-tree popups
        /// will need a list-hashed key before this signature broadens.
        /// </summary>
        public static void OpenOrFocusWorkingTree(
            string repoRoot,
            IList<string> filePaths,
            string preselectFilePath,
            bool isDarkTheme,
            MessageBroker broker,
            CodeReviewService crService,
            Form owner,
            Action<string> onQuickTaskCreated = null)
        {
            if (string.IsNullOrEmpty(repoRoot)) return;
            if (filePaths == null || filePaths.Count == 0) return;

            string keyFile = !string.IsNullOrEmpty(preselectFilePath)
                ? preselectFilePath
                : filePaths[0];
            string key = BuildWorkingTreeKey(repoRoot, keyFile);

            CodeReviewPopupForm form;
            bool isNew;
            lock (_sync)
            {
                if (_instances.TryGetValue(key, out var existing) &&
                    existing != null && !existing.IsDisposed)
                {
                    form = existing;
                    isNew = false;
                }
                else
                {
                    form = new CodeReviewPopupForm(key, repoRoot, filePaths, preselectFilePath);
                    _instances[key] = form;
                    isNew = true;
                }
            }

            if (!isNew)
            {
                try { form.PreselectFile(preselectFilePath); } catch { }
                return;
            }

            // Subscribe the caller's wrap-success callback ONLY on the new-form
            // path. Focus-existing reuses the original subscription wired when
            // the form was first created; re-subscribing here would accumulate
            // handlers (N opens => N RefreshAsync calls per wrap, each now
            // writing to SQLite via the auto-link pass).
            if (onQuickTaskCreated != null)
            {
                form.QuickTaskCreated += (s, taskId) =>
                {
                    try { onQuickTaskCreated(taskId); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[CodeReviewPopupManager] onQuickTaskCreated threw: {ex.Message}");
                    }
                };
            }

            form.FormClosed += (s, e) =>
            {
                lock (_sync)
                {
                    if (_instances.TryGetValue(key, out var current) &&
                        ReferenceEquals(current, form))
                    {
                        _instances.Remove(key);
                    }
                }
            };

            _ = InitializeAndShowAsync(form, broker, crService, isDarkTheme, owner);
        }

        private static async Task InitializeAndShowAsync(
            CodeReviewPopupForm form,
            MessageBroker broker,
            CodeReviewService crService,
            bool isDarkTheme,
            Form owner)
        {
            try
            {
                if (owner != null)
                    form.Show(owner);
                else
                    form.Show();

                await form.Initialize(broker, crService, isDarkTheme);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeReviewPopupManager] Initialize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast a theme switch to every live popup. Called by MainForm
        /// when the user toggles dark/light.
        /// </summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            CodeReviewPopupForm[] snapshot;
            lock (_sync)
            {
                snapshot = new CodeReviewPopupForm[_instances.Count];
                _instances.Values.CopyTo(snapshot, 0);
            }
            foreach (var f in snapshot)
            {
                if (f == null || f.IsDisposed) continue;
                try
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke((Action)(() => f.ApplyTheme(isDark)));
                    else
                        f.ApplyTheme(isDark);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CodeReviewPopupManager] ApplyThemeToAll failed: {ex.Message}");
                }
            }
        }

        /// <summary>True if a popup is currently open for the given task.</summary>
        public static bool IsOpen(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return false;
            lock (_sync)
            {
                return _instances.TryGetValue(taskId, out var f) && f != null && !f.IsDisposed;
            }
        }

        /// <summary>
        /// Close all live popups — used during app shutdown so window-bounds
        /// settings get flushed.
        /// </summary>
        public static void CloseAll()
        {
            CodeReviewPopupForm[] snapshot;
            lock (_sync)
            {
                snapshot = new CodeReviewPopupForm[_instances.Count];
                _instances.Values.CopyTo(snapshot, 0);
                _instances.Clear();
            }
            foreach (var f in snapshot)
            {
                if (f == null || f.IsDisposed) continue;
                try
                {
                    if (f.InvokeRequired)
                        f.BeginInvoke((Action)f.Close);
                    else
                        f.Close();
                }
                catch { }
            }
        }
    }
}
