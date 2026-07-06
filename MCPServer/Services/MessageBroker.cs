using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using MultiTerminal.Services.Presence;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Routes messages between terminals and maintains message queues.
    /// Supports SQLite persistence for reliable delivery with retry.
    /// </summary>
    public class MessageBroker : IDisposable, IRemoteModeSink
    {
        private bool _isDisposed;

        private readonly ConcurrentDictionary<string, TerminalInfo> _terminals = new ConcurrentDictionary<string, TerminalInfo>();
        private readonly ConcurrentDictionary<string, BlockingCollection<Message>> _messageQueues = new ConcurrentDictionary<string, BlockingCollection<Message>>();
        private readonly List<Message> _messageHistory = new List<Message>();
        private readonly object _historyLock = new object();
        private const int MaxHistorySize = 1000;

        // Kanban task storage
        private readonly ConcurrentDictionary<string, KanbanTask> _tasks = new ConcurrentDictionary<string, KanbanTask>();

        // Serializes checklist read-modify-write across all checklist mutators (append, full-replace,
        // transition, assign). _tasks being concurrent only guards the lookup, not the per-task
        // GetChecklist()->mutate->SetChecklist()->SaveTask() sequence, which is otherwise last-writer-wins.
        private readonly object _checklistMutationLock = new object();
        private readonly TaskDatabase _taskDb;
        private readonly MultiTerminal.Services.WorktreeManager _worktrees;
        private readonly MultiTerminal.Services.WorktreeAutoCommitService _autoCommit;
        private readonly MultiTerminal.Services.WorktreeMergeService _merge;

        // Per-task lock serializing the helper-branch INTEGRATION merge (run in the
        // canonical worktree at task-done) against activation-time worktree CREATION,
        // so a helper worktree can't be created/moved while the canonical branch is
        // mid-merge (per-agent isolation, task bab81a92). Prune-all is intentionally
        // NOT under this lock — it is guarded by WorktreePruneCoordinator plus the
        // activation-side status re-check (create is skipped once the task goes done).
        // Process-scoped; contention is rare (at most one create vs one teardown).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _taskWorktreeLocks
            = new System.Collections.Concurrent.ConcurrentDictionary<string, object>();

        private static object TaskWorktreeLock(string taskId)
            => _taskWorktreeLocks.GetOrAdd(taskId ?? string.Empty, _ => new object());
        private readonly MultiTerminal.Services.WorktreeJanitorService _janitor;

        // Project storage
        private readonly ConcurrentDictionary<string, Project> _projects = new ConcurrentDictionary<string, Project>();
        private readonly ProjectDatabase _projectDb;
        public ProjectDatabase ProjectDatabase => _projectDb;

        // Team member profile storage
        private readonly ConcurrentDictionary<string, TeamMemberProfile> _profiles = new ConcurrentDictionary<string, TeamMemberProfile>();

        // Office agent tracking (for Office Panel animation - not registered terminals)
        private readonly ConcurrentDictionary<string, OfficeAgentInfo> _officeAgents = new ConcurrentDictionary<string, OfficeAgentInfo>();

        // Elicitation relay: in-memory store for pending MCP elicitation requests
        private readonly ConcurrentDictionary<string, ElicitationRequest> _pendingElicitations = new ConcurrentDictionary<string, ElicitationRequest>();
        private readonly ConcurrentDictionary<string, ElicitationResponse> _elicitationResponses = new ConcurrentDictionary<string, ElicitationResponse>();

        // Remote mode: when true, hooks relay questions/elicitations to ClaudeRemote on phone.
        // Backed by SettingsService (key: remoteMode.enabled) so the flag survives MT restart.
        private const string SettingRemoteMode = "remoteMode.enabled";

        // Message queue persistence for reliable delivery
        private readonly MessageQueueDatabase _messageQueueDb;

        // HTTP client for webhook delivery
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Terminal colors for chat UI
        private readonly string[] _terminalColors = new[]
        {
            "#4A90D9", // Blue
            "#50B498", // Green
            "#E67E22", // Orange
            "#9B59B6", // Purple
            "#E74C3C", // Red
            "#1ABC9C", // Teal
            "#F39C12", // Yellow
            "#3498DB"  // Light Blue
        };
        private int _colorIndex = 0;

        /// <summary>
        /// Raised when a new message is sent.
        /// </summary>
        public event EventHandler<Message> MessageSent;

        /// <summary>
        /// Raised when a terminal registers.
        /// </summary>
        public event EventHandler<TerminalInfo> TerminalRegistered;

        /// <summary>
        /// Raised when a terminal disconnects.
        /// </summary>
        public event EventHandler<TerminalInfo> TerminalDisconnected;

        /// <summary>
        /// Raised when tasks are updated (created, claimed, or status changed).
        /// </summary>
        public event EventHandler<List<KanbanTask>> TasksUpdated;

        /// <summary>
        /// Raised when a task is claimed by a terminal (for toast notifications).
        /// </summary>
        public event EventHandler<TaskClaimedEventArgs> TaskClaimed;

        /// <summary>
        /// Raised when an activity event occurs (task created, plan updated, build completed, etc.).
        /// This is the primary event for the Activity Panel feed.
        /// </summary>
        public event EventHandler<ActivityEvent> ActivityRecorded;

        /// <summary>
        /// Raised when a plan is updated (phase change, assignment change, etc.).
        /// </summary>
        public event EventHandler<PlanUpdateEventArgs> PlanUpdated;

        /// <summary>
        /// Raised when projects are updated (created, modified, or deleted).
        /// </summary>
        public event EventHandler<List<Project>> ProjectsUpdated;

        /// <summary>
        /// Raised when team member profiles are updated (created, modified, or deleted).
        /// </summary>
        public event EventHandler<List<TeamMemberProfile>> ProfilesUpdated;

        /// <summary>
        /// Raised when a helper session is updated (spawned, status changed, completed).
        /// </summary>
        public event EventHandler<HelperSession> HelperSessionUpdated;

        /// <summary>
        /// Raised when a helper logs a message.
        /// </summary>
        public event EventHandler<HelperMessage> HelperMessageLogged;

        /// <summary>
        /// Raised when inbox messages are created or updated.
        /// Event args contain the updated message list for the affected user.
        /// </summary>
        public event EventHandler<InboxUpdatedEventArgs> InboxUpdated;

        /// <summary>
        /// Raised when an office agent is spawned (for Office Panel animation).
        /// </summary>
        public event EventHandler<OfficeAgentInfo> OfficeAgentSpawned;

        /// <summary>
        /// Raised when an office agent departs (for Office Panel animation).
        /// </summary>
        public event EventHandler<OfficeAgentInfo> OfficeAgentDeparted;

        /// <summary>
        /// Raised when a SubagentStop hook requests closing an agent panel.
        /// The string payload is the transcript file path.
        /// </summary>
        public event EventHandler<string> AgentPanelCloseRequested;

        /// <summary>
        /// Request that an agent panel be closed (called from REST API on SubagentStop hook).
        /// </summary>
        public void RequestAgentPanelClose(string transcriptPath)
        {
            RaiseSafe(AgentPanelCloseRequested, transcriptPath);
        }

        /// <summary>
        /// Raised when a pipeline agent report is saved for a task.
        /// Payload contains taskId and reportId so the UI can refresh badges.
        /// </summary>
        public event EventHandler<ReportSavedEventArgs> ReportSaved;

        /// <summary>
        /// Notify subscribers that a new agent report was saved for a task.
        /// Called from TaskReportsController after SaveTaskReport().
        /// </summary>
        public void NotifyReportSaved(string taskId, string reportId, string agentName, string verdict)
        {
            RaiseSafe(ReportSaved, new ReportSavedEventArgs
            {
                TaskId = taskId,
                ReportId = reportId,
                AgentName = agentName,
                Verdict = verdict
            });
        }

        /// <summary>
        /// Raised when a Claude Code Notification hook delivers a runtime notification.
        /// Payload is a dictionary with: id, notification_type, title, message, agent_name, session_id, cwd, created_at.
        /// </summary>
        public event EventHandler<Dictionary<string, object>> NotificationReceived;

        /// <summary>
        /// Record and broadcast a Claude Code runtime notification.
        /// Called from NotificationsController when the Notification hook POSTs.
        /// </summary>
        public string RecordNotification(string notificationType, string title, string message,
            string sessionId, string agentName, string cwd)
        {
            if (TaskDb == null)
            {
                DebugLogService?.Warning("MessageBroker", "RecordNotification called before TaskDb initialized — notification not persisted");
                return null;
            }
            string id = TaskDb.SaveNotificationEvent(notificationType, title, message, sessionId, agentName, cwd);
            var payload = new Dictionary<string, object>
            {
                ["id"] = id,
                ["notification_type"] = notificationType,
                ["title"] = title,
                ["message"] = message,
                ["agent_name"] = agentName,
                ["session_id"] = sessionId,
                ["cwd"] = cwd,
                ["created_at"] = DateTime.UtcNow.ToString("o")
            };
            RaiseSafe(NotificationReceived, payload);
            return id;
        }

        /// <summary>
        /// Callback for push delivery to terminal UI.
        /// Parameters: messageId, recipientId, senderName, messageContent
        /// Returns: Task<bool> indicating whether delivery actually completed successfully
        /// </summary>
        public Func<string, string, string, string, Task<bool>> OnMessageDelivery { get; set; }

        /// <summary>
        /// Activity service for auto-updating terminal activity on task operations.
        /// Set via DI after broker is created.
        /// </summary>
        public ActivityService ActivityService { get; set; }

        /// <summary>
        /// Task database for accessing profile and task data.
        /// Exposes database access for external services (MainForm, etc.).
        /// </summary>
        public TaskDatabase TaskDb => _taskDb;

        /// <summary>
        /// Default inbox recipient for auto-generated notifications (PM/tester).
        /// Defaults to "Owner". Set from OwnerProfile.FullName at startup if configured.
        /// </summary>
        public string DefaultInboxRecipient { get; set; } = "Owner";

        /// <summary>
        /// Debug log service for internal diagnostics.
        /// Set after broker is created for non-blocking debug logging.
        /// </summary>
        public MultiTerminal.Services.DebugLogService DebugLogService { get; set; }

        /// <summary>
        /// Activity feed service for recording high-level events to the manager dashboard.
        /// Set via DI after broker is created.
        /// </summary>
        public ActivityFeedService ActivityFeedService { get; set; }

        /// <summary>
        /// Summary service for auto-generating progress summaries on status changes.
        /// Set via DI after broker is created.
        /// </summary>
        public SummaryService SummaryService { get; set; }

        /// <summary>
        /// Complexity detector for analyzing task complexity and suggesting plans.
        /// Set via DI after broker is created.
        /// </summary>
        public ComplexityDetector ComplexityDetector { get; set; }

        /// <summary>
        /// Changelog service for automatic changelog generation when tasks are completed.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.ChangelogService ChangelogService { get; set; }

        /// <summary>
        /// Session lineage service for importing JSONL session files and querying
        /// the parent/child session chain linked to tasks.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.SessionLineageService SessionLineageService { get; set; }

        /// <summary>
        /// Raised when session lineage data is updated (session imported or linked to a task).
        /// Event arg is the session ID that was updated (null for bulk sync).
        /// </summary>
        public event EventHandler<string> SessionLineageUpdated;

        /// <summary>
        /// Fires the SessionLineageUpdated event. Called from MainForm after sync imports new sessions.
        /// </summary>
        public void FireSessionLineageUpdated(string sessionId)
        {
            RaiseSafe(SessionLineageUpdated, sessionId);
        }

        /// <summary>
        /// Raised when an agent requests a browser tab action (open, update, close).
        /// MainForm subscribes to route the request to the correct TerminalDocument.
        /// </summary>
        public event EventHandler<BrowserTabEventArgs> BrowserTabRequested;

        /// <summary>
        /// Raised after a branch's outcome is saved via BranchMetadataService.SetOutcome.
        /// HudGitRenderer subscribes to refresh the tree when its active project matches.
        /// </summary>
        public event EventHandler<BranchOutcomeUpdatedEventArgs> BranchOutcomeUpdated;

        /// <summary>
        /// Fires after <see cref="SetTaskActive"/> swaps the agent's active task.
        /// Carries the agent name, the previous task's worktree (null when there
        /// was no prior active task), and the new task's worktree (null when no
        /// worktree was materialized). Subscribed by MainForm to push a
        /// task_active_changed event over the agent's Claude Code Channel (AC2);
        /// the agent-side hook acts on it.
        /// </summary>
        public event EventHandler<TaskActiveChangedEventArgs> TaskActiveChanged;

        /// <summary>
        /// Fires shortly before <see cref="WorktreeManager.PruneForTaskAsync"/>
        /// is invoked in the task-done flow. Carries the worktree path that's
        /// about to be removed, the owning repo root, and the task assignee.
        /// Subscribed by MainForm to broadcast a <c>worktree_pruning</c> event
        /// to every live terminal's Claude Code Channel so any agent with cwd
        /// inside the worktree can <c>cd</c> out before git tries the rmdir
        /// (task db4b18c6). Broadcast is best-effort — janitor Pass 3 is the
        /// durable backstop for missed evictions.
        /// </summary>
        public event EventHandler<WorktreePruningEventArgs> WorktreePruning;

        /// <summary>
        /// Fired AFTER PruneForTaskAsync and MergeForTaskAsync both complete
        /// in the task-done flow. Subscribers (HudGitRenderer) can rebind to
        /// the post-merge state without racing the merge — by the time this
        /// fires, refs/heads/{trunk} has been written. <see cref="WorktreePruning"/>
        /// is for "the worktree is about to disappear, drop your handles";
        /// this event is for "the new repo state is ready, refresh your view".
        /// Two events, two concerns. Fires once per task-done attempt regardless
        /// of whether the merge succeeded, was skipped, or failed — the
        /// post-state is durable either way.
        /// </summary>
        public event EventHandler<WorktreeReadyEventArgs> WorktreeReady;

        /// <summary>
        /// Fired when an external trigger asks MT to type text into a live terminal
        /// (POST /api/terminals/inject). Used by the SessionStart <c>source=clear</c>
        /// hook to inject "initializing..." after /clear so the cleared session gets a
        /// turn and auto-runs <c>/multiterminal:session-start</c> (task be599e08).
        /// Subscribed by MainForm, which resolves the agent name to its TerminalDocument
        /// — the broker is HTTP/UI-free.
        /// </summary>
        public event EventHandler<TerminalInjectEventArgs> TerminalInjectRequested;

        /// <summary>
        /// Janitor-callable retry for a prune that was deferred at task-done
        /// time. Cycle-4 contract:
        /// <list type="bullet">
        ///   <item>Re-validates that the task is still <c>done</c> before
        ///   pruning — closes the security HIGH data-loss path where a task
        ///   reopened between the janitor's snapshot query and this callback
        ///   could still get its live worktree removed.</item>
        ///   <item>Only unmarks the path on a clean outcome (prune succeeded
        ///   OR the prune is no longer wanted because the task was reopened).
        ///   If the prune itself fails, the path stays marked so spawns
        ///   continue to be refused; the next janitor sweep retries.</item>
        /// </list>
        /// </summary>
        public async Task<bool> TryDeferredPruneRetryAsync(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return false;
            var markedPaths = new List<string>();
            bool clearMarkOnExit = false;
            try
            {
                var record = _taskDb.GetWorktreeForTask(taskId);
                if (record == null || !string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Cycle-4 security HIGH fix: re-check task status. The janitor
                // snapshot can be stale by the time this callback runs; if the
                // task has been reopened (moved out of done) we must NOT prune
                // the live worktree.
                var task = _taskDb.GetTask(taskId);
                if (task == null || !string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase))
                {
                    // Task no longer wants pruning. Clear any stale defer-mark on
                    // EVERY agent worktree path so spawns can resume.
                    foreach (var w in _taskDb.ListWorktreesForTask(taskId))
                    {
                        if (!string.IsNullOrEmpty(w.WorktreePath))
                            WorktreePruneCoordinator.UnmarkPruning(w.WorktreePath);
                    }
                    WorktreePruneCoordinator.UnmarkPruning(record.WorktreePath);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Deferred prune for task {taskId} cancelled — task no longer 'done'. Released defer-marks.");
                    return false;
                }

                string projectPath = TryGetProjectPathForTask(taskId);
                if (string.IsNullOrEmpty(projectPath)) return false;

                // CRITICAL (item [5], task bab81a92): a helper-integration CONFLICT
                // in the synchronous done-path halts teardown and leaves the worktree
                // rows status='active' on a DONE task — which is indistinguishable
                // HERE from a prune merely deferred for a cwd-lock. Before doing
                // anything destructive, RE-RUN helper integration. It is idempotent:
                // helpers already merged in the synchronous pass report "nothing to
                // merge". If it CONFLICTS, this is NOT a clean deferred prune — BAIL
                // and preserve every worktree and branch so the helper's committed
                // work is never force-deleted. Mirrors the synchronous path's
                // "conflict => halt teardown"; without it the janitor would prune +
                // force-delete un-integrated helper branches and trunk-merge
                // canonical-only, silently dropping committed helper commits.
                // Serialize integration under the per-task lock, exactly as the
                // synchronous done-path (CommitAndIntegrateHelpers) does, so a concurrent
                // activation can't create/move a helper worktree while the canonical
                // branch is mid-merge (bab81a92 pipeline fix #2). The janitor runs on a
                // background timer thread, so briefly blocking it here is fine; mirror the
                // sync path's lock + GetAwaiter().GetResult() to share the SAME lock
                // primitive (a separate SemaphoreSlim would not mutually exclude with the
                // monitor the sync path and activation hold).
                MultiTerminal.Services.HelperIntegrationResult integration;
                lock (TaskWorktreeLock(taskId))
                {
                    integration = _merge.IntegrateHelperBranchesAsync(taskId, projectPath).GetAwaiter().GetResult();
                }
                if (!integration.Success)
                {
                    if (integration.HadConflicts)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Deferred prune for {taskId} aborted: helper integration conflict ({string.Join(", ", integration.ConflictBranches)}). Worktrees + branches preserved for rebase recovery.");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "helper_integration_conflict",
                            Content = $"Deferred completion of '{task.Title}' blocked: helper branch(es) {string.Join(", ", integration.ConflictBranches)} conflict with the task branch. Worktrees and branches preserved — rebase the helper branch onto the task branch, resolve, then re-mark done.",
                            RelatedId = taskId
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Deferred prune for {taskId} aborted: helper integration failed ({integration.Stderr}). Worktrees preserved.");
                    }
                    return false; // nothing marked or pruned yet — everything preserved
                }

                // Integration confirmed (or a no-op for already-merged / single-agent
                // tasks): safe to prune EVERY agent worktree (canonical + helpers) and
                // then force-delete the integrated helper branches once their worktrees
                // are gone. Only branches IntegrateHelperBranchesAsync actually
                // integrated are deleted below — never an un-integrated branch.
                foreach (var w in _taskDb.ListWorktreesForTask(taskId))
                {
                    if (!string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(w.WorktreePath) && !markedPaths.Contains(w.WorktreePath))
                        markedPaths.Add(w.WorktreePath);
                }
                if (!string.IsNullOrEmpty(record.WorktreePath) && !markedPaths.Contains(record.WorktreePath))
                    markedPaths.Add(record.WorktreePath);

                foreach (var p in markedPaths)
                    WorktreePruneCoordinator.MarkPruning(p); // idempotent; broker already marked at defer time

                bool removed = await _worktrees.PruneAllForTaskAsync(taskId, projectPath).ConfigureAwait(false);
                clearMarkOnExit = removed; // only unmark on success

                // Cycle-4 dbbb8de2 fix: when the deferred prune finally
                // succeeds, run the same post-prune sequence the synchronous
                // task-done path runs — delete integrated helper branches, then
                // auto-merge the canonical branch + fire WorktreeReady. Without
                // this the task branch was never merged AND HUD panels bound to
                // the worktree stayed bound to a now-deleted directory.
                if (removed)
                {
                    foreach (var hb in integration.IntegratedBranches)
                    {
                        try
                        {
                            await _merge.DeleteBranchAsync(projectPath, hb, force: true).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Deferred-retry delete helper branch '{hb}' failed: {ex.Message}");
                        }
                    }
                    PerformPostPruneMergeAndFireReady(taskId, task, projectPath, record.WorktreePath);
                }

                return removed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] TryDeferredPruneRetryAsync({taskId}) threw: {ex.Message}");
                return false;
            }
            finally
            {
                if (clearMarkOnExit)
                {
                    foreach (var p in markedPaths)
                        WorktreePruneCoordinator.UnmarkPruning(p);
                }
            }
        }

        /// <summary>
        /// Janitor Pass-2 recovery hook (task d75d7d6e): retries the Phase-3
        /// auto-merge for a pending-merge branch whose worktree was already
        /// pruned. Re-validates that the task still exists and is <c>done</c>
        /// before merging (the janitor snapshot can be stale), then delegates
        /// to <see cref="WorktreeMergeService.MergeForTaskAsync"/>. On a clean
        /// merge it also fires <see cref="WorktreeReady"/> so HUD panels rebind
        /// to the post-merge trunk. Returns the structured result so the janitor
        /// can count recoveries vs. still-flagged branches. Never throws.
        /// </summary>
        public async Task<MultiTerminal.Services.MergeResult> TryAutoMergeForTaskAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(repoRoot))
            {
                return new MultiTerminal.Services.MergeResult { Success = false, Stderr = "taskId/repoRoot required" };
            }
            try
            {
                var task = _taskDb.GetTask(taskId);
                if (task == null || !string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase))
                {
                    // Task reopened or gone — don't merge its branch into trunk.
                    return new MultiTerminal.Services.MergeResult { Success = false, Stderr = "task no longer 'done'" };
                }

                var record = _taskDb.GetWorktreeForTask(taskId);
                var mergeResult = await _merge.MergeForTaskAsync(taskId, repoRoot, ResolveConfiguredTrunk(task)).ConfigureAwait(false);
                if (mergeResult.Merged)
                {
                    // Real merge happened — let HUD rebind to post-merge trunk.
                    FireWorktreeReady(taskId, record?.WorktreePath, repoRoot, task.Assignee);
                }
                return mergeResult;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] TryAutoMergeForTaskAsync({taskId}) threw: {ex.Message}");
                return new MultiTerminal.Services.MergeResult { Success = false, Stderr = ex.Message };
            }
        }

        /// <summary>
        /// Internal helper that invokes <see cref="WorktreePruning"/> with
        /// exception isolation. Returns the event args so callers can inspect
        /// <see cref="WorktreePruningEventArgs.AllDelivered"/> for the
        /// defer-on-timeout decision (cycle-3 adversary HIGH fix).
        /// </summary>
        private WorktreePruningEventArgs FireWorktreePruning(string taskId, string worktreePath, string repoRoot, string agentName)
        {
            var args = new WorktreePruningEventArgs
            {
                TaskId = taskId,
                WorktreePath = worktreePath,
                RepoRoot = repoRoot,
                AgentName = agentName,
            };
            try
            {
                RaiseSafe(WorktreePruning, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] WorktreePruning subscribers threw: {ex.Message}");
            }
            return args;
        }

        // Fires WorktreeReady AFTER prune + auto-merge complete in the task-done
        // flow. Used by HUD panels to rebind to the post-merge repo state without
        // racing the synchronous merge. Exception-isolated like the other broker
        // event fires — a misbehaving subscriber doesn't break the task-done path.
        private void FireWorktreeReady(string taskId, string worktreePath, string repoRoot, string agentName)
        {
            var args = new WorktreeReadyEventArgs(taskId, worktreePath, repoRoot, agentName);
            try
            {
                RaiseSafe(WorktreeReady, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] WorktreeReady subscribers threw: {ex.Message}");
            }
        }

        // Resolve the project's configured default branch (git_default_branch) for
        // a task, or null when unset / unknown. Passed to MergeForTaskAsync as the
        // authoritative expected trunk (task 90c2acc6, Suspect B): when set, the
        // merge refuses if the main checkout is parked on a different branch instead
        // of silently landing the task branch in the wrong place. Null lets the
        // merge service fall back to its own best-effort detection.
        private string ResolveConfiguredTrunk(KanbanTask task)
        {
            // The cached _projects entries (MCPServer.Models.Project) are the light
            // model and don't carry git_default_branch — read the rich project row
            // for it. Returns null ONLY when there is genuinely no configured value
            // (no project, or git_default_branch unset), in which case the merge
            // service falls back to its own best-effort detection (and fails closed
            // if that's ambiguous too).
            //
            // FAIL CLOSED on lookup failure (pipeline run 1, Codex security MEDIUM):
            // a DB exception is NOT swallowed to null. Swallowing would silently
            // convert a transient failure on the authoritative trust boundary into
            // an unprotected heuristic merge. We let it propagate — both merge call
            // sites run inside a try/catch that turns it into an auto_merge_failed
            // (branch preserved, janitor retries next sweep), which is the safe
            // outcome for a destructive-ish merge.
            if (task?.ProjectId == null || _projectDb == null) return null;
            var rich = _projectDb.GetRichProject(task.ProjectId);
            return string.IsNullOrWhiteSpace(rich?.GitDefaultBranch) ? null : rich.GitDefaultBranch.Trim();
        }

        // Post-prune flow shared by the synchronous task-done path and the
        // janitor's deferred-prune retry. Attempts the auto-merge (with full
        // activity logging for the four outcomes: skipped, merged, failed-clean,
        // threw), then fires WorktreeReady so HUD panels rebind to the post-
        // merge repo state. Cycle-4 factored this out of the inline if(prunedOK)
        // block in UpdateTaskStatus so the janitor's TryDeferredPruneRetryAsync
        // can run the same sequence after its delayed prune succeeds.
        // (Cross-model adversary HIGH from pipeline run 3.)
        //
        // Callers must only invoke this AFTER a successful prune. Failed/
        // deferred prunes don't fire WorktreeReady (the worktree may still be
        // alive on disk and rebinding to repo root would yank the user off it).
        private void PerformPostPruneMergeAndFireReady(string taskId, KanbanTask task, string projectPath, string worktreePath)
        {
            string taskIdShort = taskId.Substring(0, Math.Min(8, taskId.Length));
            try
            {
                var mergeResult = _merge.MergeForTaskAsync(taskId, projectPath, ResolveConfiguredTrunk(task)).GetAwaiter().GetResult();

                if (mergeResult.Merged)
                {
                    // A real merge landed the branch in trunk. The branch is normally
                    // deleted too — but on the partial path (merge succeeded, branch -d
                    // failed) Stderr is set and the branch is still alive. Don't claim
                    // "task branch deleted" in that case (pipeline run 1, Codex adversary
                    // MEDIUM): the next janitor sweep cleans the leftover branch.
                    bool cleanupPending = !string.IsNullOrEmpty(mergeResult.Stderr);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Auto-merge for {taskId}: merged into {mergeResult.MergedInto}" +
                        (cleanupPending ? $" (branch cleanup pending: {mergeResult.Stderr})" : ""));
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "auto_merge",
                        Content = cleanupPending
                            ? $"Merged task branch into {mergeResult.MergedInto} for '{task.Title}'; branch cleanup is pending (will be removed on the next janitor sweep)."
                            : $"Merged task branch into {mergeResult.MergedInto} for '{task.Title}'; task branch deleted.",
                        RelatedId = taskId
                    });
                }
                else if (mergeResult.Success)
                {
                    // Success WITHOUT a merge — a benign skip (no commits, branch
                    // already gone, no worktree record). Report it as a skip, NOT a
                    // merge (task 90c2acc6, Suspect A: never claim "merged into trunk"
                    // when nothing landed).
                    string skipReason = mergeResult.SkippedReason ?? "no merge performed";
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Auto-merge for {taskId}: skipped — {skipReason}");
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "auto_merge_skipped",
                        Content = $"Auto-merge skipped for '{task.Title}': {skipReason}",
                        RelatedId = taskId
                    });
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Auto-merge for {taskId} FAILED" +
                        (mergeResult.HadConflicts ? " (conflict)" : "") +
                        $": {mergeResult.Stderr}");
                    string conflictTag = mergeResult.HadConflicts ? "Merge conflict" : "Merge failed";
                    string mergeReason = TruncateReason(mergeResult.Stderr);
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "auto_merge_failed",
                        Content = $"{conflictTag} for '{task.Title}'.{(string.IsNullOrEmpty(mergeReason) ? "" : $" Reason: {mergeReason}")} Task branch task/{taskIdShort} preserved with auto-committed changes; worktree dir was removed (necessary for the merge attempt). To resolve: run `git merge task/{taskIdShort}` in the main checkout, or re-create a worktree from the branch and re-mark the task done to retry. Janitor will keep flagging this each sweep until resolved.",
                        RelatedId = taskId
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MessageBroker] Auto-merge threw for task {taskId}: {ex.Message}");
                RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "auto_merge_failed",
                    Content = $"Auto-merge threw for '{task.Title}'. Reason: {TruncateReason(ex.Message)} Task branch task/{taskIdShort} preserved with auto-committed changes; worktree dir was removed (necessary for the merge attempt). To resolve: run `git merge task/{taskIdShort}` in the main checkout, or re-create a worktree from the branch and re-mark the task done to retry. Janitor will keep flagging this each sweep until resolved.",
                    RelatedId = taskId
                });
            }

            FireWorktreeReady(taskId, worktreePath, projectPath, task.Assignee);
        }

        /// <summary>
        /// Phase 2 (helpers) + Phase 2.5 of per-agent worktree isolation
        /// (task bab81a92): commit every HELPER worktree on its own branch, then
        /// integrate each helper branch into the canonical branch inside the
        /// canonical worktree. Returns <c>true</c> to proceed with prune-all +
        /// trunk merge, or <c>false</c> to HALT teardown (a helper commit failed or
        /// a helper merge conflicted) — leaving every worktree/branch intact for
        /// manual resolution. No-op returning <c>true</c> for single-agent tasks
        /// (the canonical commit already ran in the caller). Populates
        /// <paramref name="integratedBranches"/> with the helper branches integrated
        /// (to delete after prune-all).
        /// </summary>
        private bool CommitAndIntegrateHelpers(KanbanTask task, string repoRoot, out List<string> integratedBranches)
        {
            integratedBranches = new List<string>();
            string taskId = task.Id;

            List<MultiTerminal.MCPServer.Models.TaskWorktree> all;
            try
            {
                all = _taskDb.ListWorktreesForTask(taskId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] ListWorktreesForTask({taskId}) threw: {ex.Message}");
                // Can't enumerate per-agent worktrees — fall back to the canonical-only
                // path (single-agent behavior); the canonical commit already ran.
                return true;
            }

            var helpers = new List<MultiTerminal.MCPServer.Models.TaskWorktree>();
            foreach (var w in all)
            {
                if (!w.IsCanonical && string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    helpers.Add(w);
                }
            }
            if (helpers.Count == 0) return true;  // single-agent fast path

            // Phase 2 (helpers): commit each helper worktree on its own branch.
            foreach (var helper in helpers)
            {
                try
                {
                    var hc = _autoCommit.CommitForAgentAsync(
                        taskId, helper.AgentName, repoRoot, task.Title, task.ImplementationSummary).GetAwaiter().GetResult();
                    if (!hc.Success)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Helper commit failed for {taskId}/{helper.AgentName}: {hc.Stderr}");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "helper_commit_failed",
                            Content = $"Auto-commit of helper '{helper.AgentName}' worktree failed for '{task.Title}'. Teardown halted — see debug log.",
                            RelatedId = taskId
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Helper commit threw for {taskId}/{helper.AgentName}: {ex.Message}");
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "helper_commit_failed",
                        Content = $"Auto-commit of helper '{helper.AgentName}' worktree threw for '{task.Title}'. Teardown halted — see debug log.",
                        RelatedId = taskId
                    });
                    return false;
                }
            }

            // Phase 2.5: integrate each helper branch into the canonical branch.
            // Serialized per-task so a concurrent activation can't create/move a
            // helper worktree while the canonical branch is mid-merge.
            try
            {
                MultiTerminal.Services.HelperIntegrationResult integ;
                lock (TaskWorktreeLock(taskId))
                {
                    integ = _merge.IntegrateHelperBranchesAsync(taskId, repoRoot).GetAwaiter().GetResult();
                }
                if (!integ.Success)
                {
                    string offending = integ.ConflictBranches != null && integ.ConflictBranches.Count > 0
                        ? string.Join(", ", integ.ConflictBranches)
                        : "(see debug log)";
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Helper integration halted for {taskId}: {integ.Stderr}");
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = integ.HadConflicts ? "helper_integration_conflict" : "helper_integration_failed",
                        Content = integ.HadConflicts
                            ? $"Helper branch '{offending}' conflicts with the task branch for '{task.Title}'. Teardown halted; worktrees preserved for manual resolution."
                            : $"Helper integration could not complete for '{task.Title}'. Teardown halted — see debug log.",
                        RelatedId = taskId
                    });

                    if (integ.HadConflicts)
                    {
                        NotifyHelperIntegrationConflict(task, helpers, offending);
                    }
                    return false;
                }
                integratedBranches = integ.IntegratedBranches ?? new List<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MessageBroker] Helper integration threw for {taskId}: {ex.Message}");
                RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "helper_integration_failed",
                    Content = $"Helper integration threw for '{task.Title}'. Teardown halted — see debug log.",
                    RelatedId = taskId
                });
                return false;
            }

            return true;
        }

        /// <summary>
        /// Best-effort targeted notification when helper-branch integration conflicts
        /// at task-done: messages the assignee and each helper with retry-after-rebase
        /// guidance. Delivery failures (offline / unknown terminals) are swallowed —
        /// the activity-feed entry already recorded the conflict durably.
        /// </summary>
        private void NotifyHelperIntegrationConflict(
            KanbanTask task,
            List<MultiTerminal.MCPServer.Models.TaskWorktree> helpers,
            string offendingBranches)
        {
            string from = task.Assignee ?? "System";
            string body = $"⚠ Task-done teardown for '{task.Title}' is blocked: helper branch(es) {offendingBranches} conflict with the task branch. " +
                          "Rebase your task/<id>--<slug> branch onto the updated task branch, resolve the conflict, then ask the assignee to re-mark the task done to retry integration.";

            var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(task.Assignee)) recipients.Add(task.Assignee);
            foreach (var h in helpers)
            {
                if (!string.IsNullOrEmpty(h.AgentName)
                    && !string.Equals(h.AgentName, MultiTerminal.Services.WorktreeNaming.LegacyAgent, StringComparison.Ordinal))
                {
                    recipients.Add(h.AgentName);
                }
            }

            foreach (var to in recipients)
            {
                if (string.Equals(to, from, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    SendMessage(from, to, body, "high").GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Conflict notify to '{to}' failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Collapses a git stderr / exception message to a single line and caps
        /// its length so the auto-merge-failed activity entry stays readable in
        /// the feed while still naming the actual cause (dirty trunk, conflict,
        /// etc.). Returns empty when there's nothing to show.
        /// </summary>
        private static string TruncateReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
            string oneLine = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
            const int max = 240;
            if (oneLine.Length > max)
            {
                oneLine = oneLine.Substring(0, max) + "…";
            }
            return oneLine;
        }

        /// <summary>
        /// Fires BranchOutcomeUpdated. Called by BranchMetadataService after a successful
        /// upsert so subscribers (HudGitRenderer) can refresh.
        /// </summary>
        public void FireBranchOutcomeUpdated(string projectId, string branchName)
        {
            RaiseSafe(BranchOutcomeUpdated, new BranchOutcomeUpdatedEventArgs
            {
                ProjectId = projectId,
                BranchName = branchName
            });
        }

        /// <summary>
        /// Knowledge database for institutional memory — knowledge entries and code digests.
        /// Set via DI after broker is created (shares multiterminal.db via TaskDatabase).
        /// </summary>
        public MultiTerminal.Services.KnowledgeDatabase KnowledgeDb { get; set; }

        /// <summary>
        /// Session memory database — vector-embedded session chunks for semantic search.
        /// Set via DI after broker is created (shares multiterminal.db via TaskDatabase).
        /// </summary>
        public MultiTerminal.Services.SessionMemoryDatabase SessionMemoryDb { get; set; }

        /// <summary>
        /// Code graph database — Roslyn-based C# code indexer for symbols, relationships, impact analysis.
        /// Set via DI after broker is created (shares multiterminal.db via TaskDatabase).
        /// </summary>
        public MultiTerminal.Services.CodeGraphDatabase CodeGraphDb { get; set; }

        /// <summary>
        /// Code graph query layer — structured queries over the code graph.
        /// </summary>
        public MultiTerminal.Services.CodeGraphQuery CodeGraphQuery { get; set; }

        /// <summary>
        /// Serializes all Code Graph reindexes (manual REST trigger + background CodeGraphWatcher)
        /// through a single global permit so the non-atomic indexer can't corrupt the cg_ tables by
        /// running two rebuilds concurrently on the shared SQLite connection.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.CodeGraphIndexCoordinator CodeGraphIndexCoordinator { get; set; }

        /// <summary>
        /// Wiki generator service — produces per-subsystem markdown articles from the code graph + code digests.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.WikiGeneratorService WikiGenerator { get; set; }

        /// <summary>
        /// Per-project git read-layer cache (HUD Git tab + dashboard widget share one
        /// <see cref="MultiTerminal.Services.GitRepoService"/> per project root).
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.GitRepoManager GitRepos { get; set; }

        /// <summary>
        /// Lists the parent + linked worktrees of a repository for the HUD Git
        /// tab's switcher. Stateless utility; one instance per broker is fine.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.WorktreeListService WorktreeList { get; set; }

        /// <summary>
        /// Per-task git worktree manager (Phase 1 worktree isolation).
        /// Created in the broker ctor since it only needs <see cref="TaskDb"/>.
        /// Lifecycle hooks in <see cref="SetTaskActive"/> and
        /// <see cref="UpdateTaskStatus"/> use this when
        /// <see cref="MultiTerminal.Services.WorktreeConfig.IsEnabled"/> is true.
        /// </summary>
        public MultiTerminal.Services.WorktreeManager Worktrees => _worktrees;

        /// <summary>
        /// Phase 4 Track 3 janitor — periodic reconciliation of
        /// <c>task_worktrees</c> rows against on-disk + git state. Wired up by
        /// <c>MainForm</c> via a <see cref="System.Threading.Timer"/>.
        /// </summary>
        public MultiTerminal.Services.WorktreeJanitorService WorktreeJanitor => _janitor;

        /// <summary>
        /// Shared resolver used by every named-agent spawn site. Returns the
        /// active task's materialized worktree path for <paramref name="terminalName"/>,
        /// or <c>null</c> when the agent has no active task, the task has no
        /// active worktree, the worktree path doesn't exist on disk, or any
        /// internal lookup throws. Callers must fall through to their default
        /// single-tree cwd on null.
        ///
        /// <para>The rule "always resolve before deciding workingDirectory at a
        /// spawn site" lives here so it can't be silently violated by a new
        /// caller. Lookup is in-memory + DB-cached; safe to call from spawn
        /// paths without measurable overhead.</para>
        /// </summary>
        public string ResolveTaskWorktreePath(string terminalName)
        {
            if (string.IsNullOrEmpty(terminalName)) return null;
            try
            {
                var activeTask = ResolveActiveTaskForAgent(terminalName);
                if (activeTask == null) return null;
                // Per-agent isolation: resolve THIS terminal's own worktree on the
                // task (canonical for the assignee, helper for a helper). Fall back
                // to the canonical worktree when the agent has no per-agent row.
                string candidate = _worktrees?.GetWorktreePathForTask(activeTask.Id, terminalName)
                                   ?? _worktrees?.GetWorktreePathForTask(activeTask.Id);
                if (string.IsNullOrEmpty(candidate) || !System.IO.Directory.Exists(candidate))
                    return null;
                System.Diagnostics.Trace.WriteLine($"[ResolveTaskWorktreePath] '{terminalName}' -> task '{activeTask.Id}' -> '{candidate}'");
                return candidate;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ResolveTaskWorktreePath] '{terminalName}' threw: {ex.Message} — falling through to main checkout");
                return null;
            }
        }

        /// <summary>
        /// Companion to <see cref="ResolveTaskWorktreePath"/> used by the AC7
        /// launch-root strategy. Returns the project root (main checkout) for
        /// the agent's active task, or <c>null</c> when the agent has no active
        /// task, the task has no project, or the project has no registered path.
        ///
        /// <para>Spawn sites set <c>ProcessStartInfo.WorkingDirectory = repoRoot</c>
        /// so Claude Code's launch-time permission scope AND its harness cwd-pin
        /// cover the entire repo (worktrees live inside as descendants). An
        /// in-shell <c>cd '{worktreePath}'</c> then narrows to the active
        /// worktree before <c>claude</c> starts.</para>
        /// </summary>
        public string ResolveTaskRepoRoot(string terminalName)
        {
            if (string.IsNullOrEmpty(terminalName)) return null;
            try
            {
                var activeTask = ResolveActiveTaskForAgent(terminalName);
                if (activeTask == null) return null;
                return TryGetProjectPathForTask(activeTask.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ResolveTaskRepoRoot] '{terminalName}' threw: {ex.Message} — falling through");
                return null;
            }
        }

        /// <summary>
        /// Idempotent backfill — materialize the active task's worktree for
        /// <paramref name="agentName"/> when the task is worktree-eligible but no
        /// worktree exists yet (task 4bcd1e24). This closes the "resume gap":
        /// before this, a worktree was created ONLY inside <see cref="SetTaskActive"/>,
        /// so a task already active on resume (carried from a prior session, or
        /// activated before <see cref="MultiTerminal.Services.WorktreeConfig.IsEnabled"/>
        /// became true) never re-ran activation and silently ran at repo root.
        /// Wiring this into the <c>get_active_worktree</c> read path (which
        /// session-start §2.5 calls for cwd reconciliation) makes worktree presence
        /// a function of ELIGIBILITY, not activation timing.
        ///
        /// <para>Safe to call on every <c>get_active_worktree</c>: when the worktree
        /// already exists it returns that path with no git work
        /// (<see cref="MultiTerminal.Services.WorktreeManager.CreateForTaskAsync(string, string, bool, string)"/>
        /// is idempotent per <c>(task, agent)</c>). It is a no-op (returns null)
        /// when the agent has no active task, the task is ineligible, the task is no
        /// longer in_progress, or creation fails — callers fall through to the main
        /// checkout exactly as before.</para>
        ///
        /// <para>Helper note: a helper is resolved by
        /// <see cref="ResolveActiveTaskForAgent"/> ONLY via its existing per-agent
        /// worktree row, so a helper always trips the "already exists" fast-path and
        /// is never backfilled here — helper worktrees are still created on helper
        /// activation. Only an assignee whose canonical worktree is missing reaches
        /// the create path.</para>
        /// </summary>
        /// <returns>The worktree path after ensuring it, or null when nothing was
        /// (or could be) materialized.</returns>
        public string EnsureWorktreeForActiveTask(string agentName)
        {
            if (string.IsNullOrEmpty(agentName)) return null;
            try
            {
                var activeTask = ResolveActiveTaskForAgent(agentName);
                if (activeTask == null) return null;

                // Fast-path: THIS agent already has a worktree on disk (canonical for
                // the assignee, helper for a helper). No git work, no eligibility
                // re-check needed — return it. Also the sole path a helper ever takes.
                string existing = _worktrees?.GetWorktreePathForTask(activeTask.Id, agentName)
                                  ?? _worktrees?.GetWorktreePathForTask(activeTask.Id);
                if (!string.IsNullOrEmpty(existing) && System.IO.Directory.Exists(existing))
                    return existing;

                if (!TryResolveWorktreeEligibility(activeTask, out string projectPath, out _, out _))
                    return null;

                // In the backfill path the resolving agent is the assignee (helpers
                // short-circuit on the fast-path above), but compute it from the task
                // so a fresh helper worktree is never forged under the canonical branch.
                bool isAssignee = string.IsNullOrEmpty(activeTask.Assignee)
                                  || string.Equals(activeTask.Assignee, agentName, StringComparison.OrdinalIgnoreCase);

                // Everything that decides whether to create — status, existence, and
                // the migration guard — runs UNDER the per-task lock so the check and
                // the create are atomic (Run-1 hardening: Codex security MED race; a
                // dirty check before the lock could be invalidated by edits landing
                // between the check and CreateForTaskAsync).
                lock (TaskWorktreeLock(activeTask.Id))
                {
                    // Re-check status against the LIVE cached task (not the possibly
                    // stale snapshot a helper resolves via the DB): don't materialize a
                    // worktree for a task whose teardown may have started.
                    var liveTask = _tasks.TryGetValue(activeTask.Id, out var lt) ? lt : activeTask;
                    if (!string.Equals(liveTask.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                        return null;

                    // Re-check existence under the lock: a concurrent get_active_worktree
                    // may have created it since the pre-lock fast-path. Returning here
                    // (instead of re-creating) also means backfill_created fires exactly
                    // once per real creation.
                    string underLock = _worktrees?.GetWorktreePathForTask(activeTask.Id, agentName)
                                       ?? _worktrees?.GetWorktreePathForTask(activeTask.Id);
                    if (!string.IsNullOrEmpty(underLock) && System.IO.Directory.Exists(underLock))
                        return underLock;

                    // Migration guard (task 4bcd1e24, item [2]): three-state, fail-CLOSED.
                    // null = indeterminate (couldn't verify the repo-root dirty state) →
                    // refuse rather than risk splitting work. Non-empty = this task's own
                    // LINKED files are dirty at the repo root → a fresh worktree would
                    // split the work; block and tell the agent to commit first. Unrelated
                    // dirty files (.claude/project.json, other tasks) aren't linked here
                    // and never trip it. Empty = verified safe → create.
                    var attributableDirty = GetAttributableDirtyFiles(activeTask.Id, projectPath);
                    if (attributableDirty == null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Worktree backfill indeterminate for task {activeTask.Id}: could not verify repo-root dirty state — failing closed.");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = activeTask.Assignee ?? agentName ?? "System",
                            Type = "worktree",
                            Action = "backfill_indeterminate",
                            Content = $"Worktree backfill skipped for '{activeTask.Title}': could not verify the repo root is safe to relocate from (git/DB read failed) — refusing rather than risk splitting work. Retry once the repo is reachable.",
                            RelatedId = activeTask.Id
                        });
                        return null;
                    }
                    if (attributableDirty.Count > 0)
                    {
                        string sample = string.Join(", ", attributableDirty.Take(5))
                                        + (attributableDirty.Count > 5 ? ", ..." : "");
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Worktree backfill blocked for task {activeTask.Id}: {attributableDirty.Count} linked file(s) dirty at repo root ({sample}).");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = activeTask.Assignee ?? agentName ?? "System",
                            Type = "worktree",
                            Action = "backfill_blocked",
                            Content = $"Worktree backfill skipped for '{activeTask.Title}': {attributableDirty.Count} linked file(s) have uncommitted changes at the repo root ({sample}). Commit them, then retry — a fresh worktree would split this work.",
                            RelatedId = activeTask.Id
                        });
                        return null;
                    }

                    _worktrees.CreateForTaskAsync(activeTask.Id, agentName, isAssignee, projectPath).GetAwaiter().GetResult();

                    RecordActivity(new ActivityEvent
                    {
                        Terminal = activeTask.Assignee ?? agentName ?? "System",
                        Type = "worktree",
                        Action = "backfill_created",
                        Content = $"Backfilled missing worktree for already-active task '{activeTask.Title}'.",
                        RelatedId = activeTask.Id
                    });
                }

                return _worktrees?.GetWorktreePathForTask(activeTask.Id, agentName)
                       ?? _worktrees?.GetWorktreePathForTask(activeTask.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] EnsureWorktreeForActiveTask('{agentName}') failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Migration-guard helper (task 4bcd1e24, item [2]): which of
        /// <paramref name="taskId"/>'s linked files (<c>link_task_file</c>) currently
        /// have uncommitted changes at the repo root (<paramref name="repoRoot"/>).
        ///
        /// <para><b>Three-state contract</b> (Run-1 hardening — Codex security/adversary
        /// HIGH: a guard must not fail open). <b>null</b> = INDETERMINATE: the dirty
        /// state could not be established (no git result, or a DB error reading links)
        /// — the caller MUST refuse the backfill rather than assume clean. <b>Empty
        /// list</b> = verified safe: tree clean, or no linked files are dirty. <b>Non-empty</b>
        /// = the task's own linked files are dirty at the repo root, so a fresh
        /// worktree would split in-progress work — the caller blocks and warns.</para>
        ///
        /// <para>Attribution is deliberately scoped to LINKED files so an unrelated
        /// dirty file (bookkeeping like <c>.claude/project.json</c>, or another task's
        /// edits) never trips the guard (explicit task requirement). ACCEPTED RESIDUAL
        /// (owner-adjudicated, pipeline Run-1): repo-root edits the agent never linked
        /// won't trip the guard either — but kanban rule 11 mandates link_task_file for
        /// every changed file, so unlinked task edits are a workflow violation, and
        /// broadening to "any non-bookkeeping dirty file" would breach the explicit
        /// don't-trip-on-unrelated-files requirement. Linked-only is the chosen design.
        /// Paths are matched
        /// by canonical absolute form (<see cref="System.IO.Path.GetFullPath(string)"/>
        /// round-trip) rather than raw prefix-stripping, so a link stored relative
        /// (anchored to the repo root, NOT the server process cwd) or with <c>..</c>
        /// segments still matches the porcelain output (mirrors <c>GitAttributionService</c>).
        /// Either side that cannot be canonicalized — a git-quoted dirty path OR a
        /// malformed non-empty linked path — yields null (indeterminate) rather than
        /// being dropped, keeping the guard fully fail-closed: any non-clean OR
        /// non-verifiable state refuses the backfill (Run-2/Run-3 hardening).</para>
        /// </summary>
        private List<string> GetAttributableDirtyFiles(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(repoRoot)) return null;

            List<string> dirty;
            try
            {
                dirty = _worktrees?.GetDirtyRepoRelativePathsAsync(repoRoot).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetAttributableDirtyFiles dirty-probe for {taskId} threw: {ex.Message}");
                return null; // indeterminate — fail closed
            }
            if (dirty == null) return null;          // git couldn't determine state — fail closed
            if (dirty.Count == 0) return new List<string>(); // verified clean — proceed

            List<MCPServer.Models.TaskFileLink> links;
            try
            {
                links = _taskDb?.GetFileLinksForTask(taskId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetAttributableDirtyFiles link-read for {taskId} threw: {ex.Message}");
                return null; // can't attribute → indeterminate → fail closed
            }
            if (links == null || links.Count == 0) return new List<string>(); // nothing to attribute → proceed

            // Canonicalize the dirty (repo-relative) paths to absolute form, then
            // compare against each linked file's canonical absolute path. GetFullPath
            // collapses '..'/'.'/separator and short-name differences on both sides so
            // attribution can't be defeated by an alternate spelling.
            string fullRoot;
            try { fullRoot = System.IO.Path.GetFullPath(repoRoot); }
            catch { return null; } // can't anchor comparison → fail closed

            var dirtyAbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirty)
            {
                if (string.IsNullOrEmpty(d)) continue;
                try { dirtyAbs.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, d))); }
                catch (Exception ex)
                {
                    // A reported dirty path we cannot canonicalize (e.g. a git-quoted
                    // name with special chars that ParsePorcelainPaths preserves verbatim)
                    // means we cannot fully classify the repo's dirty set. Fail CLOSED —
                    // return indeterminate rather than silently dropping it and possibly
                    // reporting "clean" when an unclassified file is actually the task's.
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetAttributableDirtyFiles: undecodable dirty path '{d}' for {taskId}: {ex.Message} — indeterminate (fail closed).");
                    return null;
                }
            }
            if (dirtyAbs.Count == 0) return new List<string>();

            var hits = new List<string>();
            foreach (var link in links)
            {
                if (string.IsNullOrEmpty(link?.FilePath)) continue; // nothing to attribute — not a parse failure
                string abs;
                try
                {
                    // Anchor a RELATIVE link path to the repo root — bare GetFullPath
                    // would resolve it against the server process's cwd, which is not the
                    // task repo and would silently miss the matching porcelain entry.
                    abs = System.IO.Path.IsPathRooted(link.FilePath)
                        ? System.IO.Path.GetFullPath(link.FilePath)
                        : System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, link.FilePath));
                }
                catch (Exception ex)
                {
                    // Symmetric with the dirty-path side: a non-empty linked path we
                    // cannot canonicalize means we cannot tell whether that linked file
                    // is among the dirty set, so the attribution result is not fully
                    // verifiable — fail CLOSED (indeterminate) rather than skip it and
                    // risk reporting "clean".
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetAttributableDirtyFiles: undecodable link path '{link.FilePath}' for {taskId}: {ex.Message} — indeterminate (fail closed).");
                    return null;
                }
                if (dirtyAbs.Contains(abs)) hits.Add(link.FilePath);
            }
            return hits;
        }

        /// <summary>
        /// Resolve a task id to its project's filesystem path, going through
        /// the cached <c>_tasks</c> + <c>_projects</c> dictionaries. Returns
        /// null when the task is unknown, has no project, or the project has
        /// no path. Used by the janitor's project-path resolver delegate.
        /// </summary>
        public string TryGetProjectPathForTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            if (!_tasks.TryGetValue(taskId, out var task)) return null;
            if (string.IsNullOrEmpty(task.ProjectId)) return null;
            if (!_projects.TryGetValue(task.ProjectId, out var project)) return null;
            return string.IsNullOrEmpty(project.Path) ? null : project.Path;
        }

        /// <summary>
        /// Phase 2 attribution overlays for the HUD Git tab — file-level agent +
        /// active-task linkage + pipeline status. Backed by <see cref="TaskDb"/>.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.GitAttributionService GitAttribution { get; set; }

        /// <summary>
        /// Per-(project, branch) outcome metadata for the HUD Git tree.
        /// Set via MainForm wire-up (mirrors <see cref="GitAttribution"/> pattern;
        /// REST controllers receive their own DI-resolved instance — both share
        /// the SQLite connection and fire events through this same broker).
        /// </summary>
        public MultiTerminal.Services.BranchMetadataService BranchMetadata { get; set; }

        /// <summary>
        /// Phase 4b auto-link service (task d42423e3 D3) — consults registered
        /// <see cref="MultiTerminal.Services.IChangelogParser"/> implementations
        /// to attribute working-tree files to kanban tasks before they land in
        /// the HUD Git "Needs a quick task" bucket. Set via MainForm wire-up;
        /// null when no parsers are registered (auto-link becomes a no-op).
        /// </summary>
        public MultiTerminal.Services.ChangelogAttributionService ChangelogAttribution { get; set; }

        /// <summary>
        /// Project service for managing .claude/project.json files.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.ProjectService ProjectService { get; set; }

        /// <summary>
        /// Initialize the message broker and load persisted tasks and projects.
        /// </summary>
        /// <summary>
        /// Shared per-session token accumulator for the terminal token meter (task f2702f69).
        /// Singleton so both the terminal poll loop (which feeds it transcript lines) and the
        /// REST stats controller (which reads snapshots) see the same totals.
        /// </summary>
        public MultiTerminal.Services.TokenMeterService TokenMeter { get; } = new MultiTerminal.Services.TokenMeterService();

        public MessageBroker()
        {
            System.Diagnostics.Trace.WriteLine("[MessageBroker] Constructor START");
            try
            {
                System.Diagnostics.Trace.WriteLine("[MessageBroker] About to create TaskDatabase");
                _taskDb = new TaskDatabase();
                System.Diagnostics.Trace.WriteLine("[MessageBroker] TaskDatabase created");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create TaskDatabase: {ex.Message}", ex);
            }

            // Route notable worktree events (e.g. a partial-prune strand,
            // task 248cc2ce) to the activity feed. The lambda captures `this`
            // but is only invoked post-construction (at prune time), so it is
            // safe to wire here. Keeps WorktreeManager free of any broker /
            // MCPServer dependency.
            _worktrees = new MultiTerminal.Services.WorktreeManager(
                _taskDb,
                (action, content, relatedId) => RecordActivity(new ActivityEvent
                {
                    Terminal = "Worktree",
                    Type = "worktree",
                    Action = action,
                    Content = content,
                    RelatedId = relatedId,
                }));
            _autoCommit = new MultiTerminal.Services.WorktreeAutoCommitService(_taskDb);
            _merge = new MultiTerminal.Services.WorktreeMergeService(_taskDb);
            _janitor = new MultiTerminal.Services.WorktreeJanitorService(_taskDb);

            try
            {
                _projectDb = new ProjectDatabase();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create ProjectDatabase: {ex.Message}", ex);
            }

            try
            {
                _messageQueueDb = new MessageQueueDatabase();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create MessageQueueDatabase: {ex.Message}", ex);
            }

            try
            {
                LoadPersistedTasks();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load persisted tasks: {ex.Message}", ex);
            }

            try
            {
                LoadPersistedProjects();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load persisted projects: {ex.Message}", ex);
            }

            try
            {
                // Set all profiles to offline before loading them (clean slate on startup)
                _taskDb.SetAllProfilesOffline();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to set profiles offline: {ex.Message}", ex);
            }

            try
            {
                LoadPersistedProfiles();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load persisted profiles: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Logs a trace message to both Debug output and DebugLogService (if available).
        /// </summary>
        private void LogTrace(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[MessageBroker] {message}");
            DebugLogService?.Trace("MessageBroker", message);
        }

        /// <summary>
        /// Logs an info message to both Debug output and DebugLogService (if available).
        /// </summary>
        private void LogInfo(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[MessageBroker] {message}");
            DebugLogService?.Info("MessageBroker", message);
        }

        /// <summary>
        /// Logs an error message to both Debug output and DebugLogService (if available).
        /// </summary>
        private void LogError(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[MessageBroker] {message}");
            DebugLogService?.Error("MessageBroker", message);
        }

        /// <summary>
        /// Resilient event dispatch (P5 / ticket 1df2a534). Snapshots the event's invocation list and
        /// invokes each subscriber inside its OWN try/catch, so a single throwing subscriber can no
        /// longer abort delivery to the remaining subscribers — nor bubble its exception back into the
        /// REST/MCP call that happened to raise the event. Every bare event-raise in this class routes
        /// through here instead of calling the delegate directly.
        ///
        /// <para><b>Snapshot semantics:</b> <see cref="System.Delegate.GetInvocationList"/> is captured
        /// once up front, so a subscriber that unsubscribes mid-dispatch still receives this raise and a
        /// subscriber added mid-dispatch does not — the standard, race-free raise contract. Passing the
        /// event field by value also means a later handler cannot see a delegate mutated by an earlier
        /// one.</para>
        /// </summary>
        /// <typeparam name="T">The event args type (all MessageBroker events are <c>EventHandler&lt;T&gt;</c>).</typeparam>
        /// <param name="handler">The event delegate — pass the event field directly; null means no subscribers.</param>
        /// <param name="args">The event args forwarded to every subscriber.</param>
        /// <param name="source">Auto-filled with the raising member's name for diagnostics; do not pass explicitly.</param>
        private void RaiseSafe<T>(EventHandler<T> handler, T args, [System.Runtime.CompilerServices.CallerMemberName] string source = "")
        {
            if (handler == null)
            {
                return;
            }

            foreach (EventHandler<T> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, args);
                }
                catch (Exception ex)
                {
                    // One bad subscriber must not starve the others (P5). Log and keep dispatching.
                    LogError($"RaiseSafe: subscriber threw dispatching {typeof(T).Name} from {source}: {ex}");
                }
            }
        }

        /// <summary>
        /// Load tasks from the database into memory.
        /// </summary>
        private void LoadPersistedTasks()
        {
            try
            {
                var tasks = _taskDb.LoadAllTasks();
                foreach (var task in tasks)
                {
                    _tasks.TryAdd(task.Id, task);
                }
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Loaded {tasks.Count} tasks from database");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to load tasks: {ex.Message}");
            }
        }

        /// <summary>
        /// Load projects from the database into memory.
        /// </summary>
        private void LoadPersistedProjects()
        {
            try
            {
                var projects = _projectDb.GetAllProjects();
                foreach (var project in projects)
                {
                    _projects.TryAdd(project.Id, project);
                }
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Loaded {projects.Count} projects from database");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to load projects: {ex.Message}");
            }
        }

        /// <summary>
        /// Load team member profiles from the database into memory.
        /// </summary>
        private void LoadPersistedProfiles()
        {
            try
            {
                var profiles = _taskDb.LoadAllProfiles();
                foreach (var profile in profiles)
                {
                    _profiles.TryAdd(profile.Id, profile);
                }
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Loaded {profiles.Count} profiles from database");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to load profiles: {ex.Message}");
            }
        }

        // Lock used to make "probe for unique name + register" an atomic pair
        // (see RegisterTerminalUnique). Only covers the narrow critical section
        // where a racing launch could choose the same candidate suffix; the
        // regular RegisterTerminal path is unaffected.
        private readonly object _uniqueRegistrationLock = new object();

        /// <summary>
        /// Returns a name safe to use for a fresh terminal registration.
        ///
        /// If <paramref name="requested"/> is not currently held by any connected
        /// terminal, returns it unchanged. Otherwise appends a numeric suffix
        /// (<c>-2</c>, <c>-3</c>, ...) until it finds one that's free.
        ///
        /// NOTE: This is a preflight check only — the returned name is NOT
        /// reserved. If callers have a concurrency concern (e.g. two Codex
        /// launches with the same default-agent name), they must use
        /// <see cref="RegisterTerminalUnique"/> instead, which probes and
        /// registers atomically under a broker-level lock.
        /// </summary>
        public string GetUniqueNameFor(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return requested;
            return FindUniqueCandidate(requested);
        }

        /// <summary>
        /// Probes for a unique name AND registers the terminal with it in a single
        /// atomic critical section. Use this (not GetUniqueNameFor + RegisterTerminal
        /// in two steps) when two concurrent callers could race on the same
        /// requested name — e.g. two Codex launches each using the per-user
        /// Codex default-agent setting.
        ///
        /// The returned <see cref="RegisterResult"/> carries the actual name used
        /// (via <see cref="TerminalInfo.Name"/> on the broker record — callers that
        /// need to surface the final name to other code paths should read it from
        /// the broker or from the result's <c>TerminalId</c> lookup).
        /// </summary>
        public RegisterResult RegisterTerminalUnique(
            string requested,
            out string resolvedName,
            string docId = null,
            bool isTeamLead = false,
            int? channelPort = null)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                resolvedName = requested;
                return RegisterTerminal(requested, docId, isTeamLead, channelPort);
            }

            // "Unassigned" is a deliberate shared sentinel — multiple anonymous
            // terminals are allowed to share it. Suffixing would break that.
            if (requested.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                resolvedName = requested;
                return RegisterTerminal(requested, docId, isTeamLead, channelPort);
            }

            lock (_uniqueRegistrationLock)
            {
                resolvedName = FindUniqueCandidate(requested);
                return RegisterTerminal(resolvedName, docId, isTeamLead, channelPort);
            }
        }

        /// <summary>
        /// Core uniqueness scan shared by <see cref="GetUniqueNameFor"/> and
        /// <see cref="RegisterTerminalUnique"/>. Returns the requested name
        /// unchanged if free; otherwise appends the first free numeric suffix.
        /// </summary>
        private string FindUniqueCandidate(string requested)
        {
            bool IsHeld(string candidate) => _terminals.Values.Any(t =>
                t.IsConnected &&
                !string.IsNullOrEmpty(t.Name) &&
                t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));

            if (!IsHeld(requested)) return requested;

            // Arbitrary cap — if we've actually got 998 connected terminals sharing
            // a base name, something else is wrong and a numeric suffix won't help.
            const int MaxUniqueNameSuffixAttempts = 1000;
            for (int i = 2; i < MaxUniqueNameSuffixAttempts; i++)
            {
                string candidate = $"{requested}-{i}";
                if (!IsHeld(candidate))
                    return candidate;
            }

            return $"{requested}-{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }

        /// <summary>
        /// Register a terminal with the broker.
        /// </summary>
        public RegisterResult RegisterTerminal(string name, string docId = null, bool isTeamLead = false, int? channelPort = null)
        {
            LogInfo($"RegisterTerminal ENTRY: name='{name}', docId='{docId ?? "null"}', channelPort={channelPort?.ToString() ?? "null"}, stack={new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name ?? "?"}");

            // Validate channel port range to prevent SSRF — only allow ports in the assigned range
            if (channelPort.HasValue && (channelPort.Value < 8800 || channelPort.Value > 8899))
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Rejected channel port {channelPort.Value} for '{name}' — outside allowed range 8800-8899");
                channelPort = null; // Silently drop invalid port, fall back to inbox delivery
            }

            var id = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Check if DocId already exists (terminal renaming, e.g., "Unassigned" → "Bob")
            TerminalInfo existingByDocId = null;
            if (!string.IsNullOrEmpty(docId))
            {
                existingByDocId = _terminals.Values.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(t.DocId) &&
                    t.DocId.Equals(docId, StringComparison.OrdinalIgnoreCase) &&
                    t.IsConnected);
            }

            if (existingByDocId != null && !existingByDocId.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                // Only allow rename if the existing terminal is "Unassigned" (placeholder → real name).
                // If a real terminal already owns this docId, reject the hijack — the new registrant
                // likely inherited the env var from a parent process (e.g., Clarion IDE addin).
                if (!existingByDocId.Name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] DocId collision rejected: '{name}' tried to claim DocId '{docId}' owned by '{existingByDocId.Name}'. Issuing fresh registration.");
                    LogInfo($"SWAPDIAG REGISTER-OUTCOME=hijack-reject incoming name='{name}' docId='{docId}' wasOwnedBy='{existingByDocId.Name}' => docId cleared, fresh registration"); // task ab32897c diag; remove after root cause
                    docId = null; // Clear the stolen docId so it falls through to fresh registration below
                    existingByDocId = null;
                }
                else
                {
                // Terminal is renaming (e.g., "Unassigned" → "Bob")
                string oldName = existingByDocId.Name;
                string newName = name;

                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Terminal renaming: {oldName} → {newName} (DocId: {docId})");
                LogInfo($"SWAPDIAG REGISTER-OUTCOME=rename '{oldName}'→'{newName}' on DocId='{docId}' (placeholder adopted incoming name). This is the prime swap suspect if DocId belongs to the OTHER doc. task ab32897c"); // remove after root cause

                // Update terminal name and channel port
                existingByDocId.Name = newName;
                existingByDocId.LastActiveAt = DateTime.UtcNow;
                if (channelPort.HasValue)
                {
                    if (existingByDocId.ChannelPort != channelPort.Value)
                        LogInfo($"CHANNEL_PORT CHANGE (docId path): '{existingByDocId.Name}' {existingByDocId.ChannelPort} → {channelPort.Value}");
                    existingByDocId.ChannelPort = channelPort.Value;
                }
                existingByDocId.IsConnected = true;

                // Handle profile transitions
                try
                {
                    // Mark OLD profile offline
                    if (_profiles.TryGetValue(oldName, out var oldProfile))
                    {
                        oldProfile.IsOnline = false;
                        oldProfile.UpdatedAt = DateTime.UtcNow;
                        _taskDb.SaveProfile(oldProfile);
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Marked old profile offline: {oldName}");
                    }

                    // Mark NEW profile online (or create if doesn't exist)
                    // Skip profile creation for temporary agents (e.g. "Agent Alice")
                    if (!IsTemporaryAgent(newName))
                    {
                        if (_profiles.TryGetValue(newName, out var newProfile))
                        {
                            newProfile.IsOnline = true;
                            newProfile.UpdatedAt = DateTime.UtcNow;
                            _taskDb.SaveProfile(newProfile);
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Marked new profile online: {newName}");
                        }
                        else
                        {
                            // Create new profile online
                            newProfile = new TeamMemberProfile
                            {
                                Id = newName,
                                DisplayName = newName,
                                IsOnline = true, // Online because registration is happening
                                IsTeamLead = isTeamLead,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            _profiles.TryAdd(newName, newProfile);
                            _taskDb.SaveProfile(newProfile);
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Created new profile online: {newName}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Skipping profile creation for temporary agent: {newName}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to handle profile transitions: {ex.Message}");
                }

                // Re-raise event so MainForm updates tab title
                RaiseSafe(TerminalRegistered, existingByDocId);

                return new RegisterResult
                {
                    Success = true,
                    TerminalId = existingByDocId.Id
                };
                } // end else (Unassigned rename)
            }

            // Check if name already exists
            var existingByName = _terminals.Values.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && t.IsConnected);

            if (existingByName != null)
            {
                // Update existing terminal
                existingByName.LastActiveAt = DateTime.UtcNow;
                existingByName.IsConnected = true;

                // Update channel port if provided
                if (channelPort.HasValue)
                {
                    if (existingByName.ChannelPort != channelPort.Value)
                        LogInfo($"CHANNEL_PORT CHANGE (name path): '{existingByName.Name}' {existingByName.ChannelPort} → {channelPort.Value}");
                    existingByName.ChannelPort = channelPort.Value;
                }

                // Update DocId if provided AND existing DocId is empty (don't overwrite valid pre-registration)
                if (!string.IsNullOrEmpty(docId) && string.IsNullOrEmpty(existingByName.DocId))
                {
                    existingByName.DocId = docId;
                }
                LogInfo($"SWAPDIAG REGISTER-OUTCOME=name-match '{name}' incomingDocId='{docId ?? "null"}' deliveredDocId='{existingByName.DocId ?? "null"}' (existing row reused; delivered docId is what MainForm binds on). task ab32897c"); // remove after root cause
                // Always re-raise event so MainForm updates its mapping
                RaiseSafe(TerminalRegistered, existingByName);

                // Auto-create profile if it doesn't exist, then set online
                // Skip creating profiles for "Unassigned" and temporary agents (e.g. "Agent Alice")
                try
                {
                    if (!name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase) && !IsTemporaryAgent(name))
                    {
                        if (!_profiles.ContainsKey(name))
                        {
                            var newProfile = new TeamMemberProfile
                            {
                                Id = name,
                                DisplayName = name,
                                IsOnline = false,  // Start offline, will be set online below
                                IsTeamLead = isTeamLead,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            _profiles.TryAdd(name, newProfile);
                            _taskDb.SaveProfile(newProfile);
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Auto-created profile for terminal: {name}");
                        }
                        else if (isTeamLead)
                        {
                            // Update existing profile's IsTeamLead flag
                            _profiles[name].IsTeamLead = true;
                            _taskDb.SaveProfile(_profiles[name]);
                        }

                        // Set profile online now that terminal is registering
                        SetProfileOnline(name);

                        // Trigger status bar refresh after profile update
                        ActivityService?.UpdateActivity(name, "idle", "Connected");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Skipping profile creation for placeholder/agent: {name}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to create/update profile: {ex.Message}");
                }

                // Ensure message queue exists (may be missing after disconnect/reconnect)
                _messageQueues.TryAdd(existingByName.Id, new BlockingCollection<Message>());

                return new RegisterResult
                {
                    Success = true,
                    TerminalId = existingByName.Id
                };
            }

            var terminal = new TerminalInfo
            {
                Id = id,
                Name = name,
                DocId = docId,
                Color = _terminalColors[_colorIndex++ % _terminalColors.Length],
                ChannelPort = channelPort
            };
            LogInfo($"NEW TERMINAL: '{name}' id={id} channelPort={channelPort?.ToString() ?? "null"} docId={docId ?? "null"}");

            if (_terminals.TryAdd(id, terminal))
            {
                _messageQueues.TryAdd(id, new BlockingCollection<Message>());
                RaiseSafe(TerminalRegistered, terminal);

                // Auto-create profile if it doesn't exist, then set online
                // Skip creating profiles for "Unassigned" and temporary agents (e.g. "Agent Alice")
                try
                {
                    if (!name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase) && !IsTemporaryAgent(name))
                    {
                        if (!_profiles.ContainsKey(name))
                        {
                            var newProfile = new TeamMemberProfile
                            {
                                Id = name,
                                DisplayName = name,
                                IsOnline = false,  // Start offline, will be set online below
                                IsTeamLead = isTeamLead,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            _profiles.TryAdd(name, newProfile);
                            _taskDb.SaveProfile(newProfile);
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Auto-created profile for terminal: {name}");
                        }
                        else if (isTeamLead)
                        {
                            // Update existing profile's IsTeamLead flag
                            _profiles[name].IsTeamLead = true;
                            _taskDb.SaveProfile(_profiles[name]);
                        }

                        // Set profile online now that terminal is registering
                        SetProfileOnline(name);

                        // Trigger status bar refresh after profile update
                        ActivityService?.UpdateActivity(name, "idle", "Connected");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Skipping profile creation for placeholder/agent: {name}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to create/update profile: {ex.Message}");
                }

                return new RegisterResult
                {
                    Success = true,
                    TerminalId = id
                };
            }

            return new RegisterResult
            {
                Success = false,
                Error = "Failed to register terminal"
            };
        }

        /// <summary>
        /// Mark an agent as ready (initialized and able to receive work).
        /// Called by HttpWebhookService when agent sends ready notification.
        /// </summary>
        /// <param name="agentName">Name of the agent</param>
        /// <param name="docId">Document ID (optional, for validation)</param>
        /// <returns>True if agent was found and marked ready, false otherwise</returns>
        public bool MarkAgentReady(string agentName, string docId = null)
        {
            var terminal = _terminals.Values.FirstOrDefault(t =>
                t.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase) &&
                t.IsConnected);

            if (terminal == null)
            {
                System.Diagnostics.Trace.WriteLine($"[MessageBroker.MarkAgentReady] Agent not found: {agentName}");
                return false;
            }

            // Validate docId if provided
            if (!string.IsNullOrEmpty(docId) &&
                !string.IsNullOrEmpty(terminal.DocId) &&
                !terminal.DocId.Equals(docId, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.WriteLine($"[MessageBroker.MarkAgentReady] DocId mismatch for {agentName}: expected {terminal.DocId}, got {docId}");
                return false;
            }

            terminal.IsReady = true;
            terminal.LastActiveAt = DateTime.UtcNow;

            System.Diagnostics.Trace.WriteLine($"[MessageBroker.MarkAgentReady] Agent {agentName} marked as ready");

            return true;
        }

        /// <summary>
        /// Check if an agent is ready (initialized and able to receive work).
        /// </summary>
        /// <param name="agentName">Name of the agent</param>
        /// <returns>True if agent is registered, connected, and ready</returns>
        public bool IsAgentReady(string agentName)
        {
            var terminal = _terminals.Values.FirstOrDefault(t =>
                t.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase) &&
                t.IsConnected);

            return terminal?.IsReady ?? false;
        }

        /// <summary>
        /// Unregister a terminal.
        /// </summary>
        public void UnregisterTerminal(string terminalId)
        {
            // Look up by dictionary key first, then fall back to DocId
            if (!_terminals.TryGetValue(terminalId, out var terminal))
            {
                terminal = _terminals.Values.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(t.DocId) &&
                    t.DocId.Equals(terminalId, StringComparison.OrdinalIgnoreCase));
            }

            if (terminal != null)
            {
                terminal.IsConnected = false;
                RaiseSafe(TerminalDisconnected, terminal);

                // Set profile offline
                try
                {
                    _taskDb.SetProfileOffline(terminal.Name);

                    // Update in-memory profile
                    if (_profiles.TryGetValue(terminal.Name, out var profile))
                    {
                        profile.IsOnline = false;
                        profile.UpdatedAt = DateTime.UtcNow;
                    }

                    BroadcastProfileUpdate();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to set profile offline: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Disconnect a terminal by name. Used by session-end hooks to properly
        /// update both in-memory state and database.
        /// </summary>
        public bool DisconnectTerminalByName(string name)
        {
            var terminal = _terminals.Values.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && t.IsConnected);

            if (terminal != null)
            {
                terminal.IsConnected = false;
                terminal.ChannelPort = null; // Clear stale port to prevent delivery to dead channel server
                RaiseSafe(TerminalDisconnected, terminal);
            }

            // Always update profile status (even if terminal not found in memory)
            SetProfileOffline(name);

            System.Diagnostics.Debug.WriteLine($"[MessageBroker] DisconnectTerminalByName: {name} (terminal found: {terminal != null})");
            return true;
        }

        /// <summary>
        /// Every <c>IsConnected</c> terminal, unfiltered by profile online
        /// status or temporary-agent rules. Used by the
        /// <see cref="WorktreePruning"/> broadcast subscriber so subagents
        /// (names like "Agent Alice") — exactly the shells most likely to
        /// hold cwd inside a worktree — also receive the eviction signal.
        /// <see cref="GetTerminals"/>'s filtering is appropriate for UI
        /// listings but wrong for broadcast audiences (task db4b18c6 cycle
        /// 2, debugger HIGH finding).
        /// </summary>
        public List<TerminalInfo> GetAllConnectedTerminals()
        {
            return _terminals.Values.Where(t => t.IsConnected).ToList();
        }

        /// <summary>
        /// Narrowed audience for the <see cref="WorktreePruning"/> eviction
        /// broadcast: only terminals that could plausibly hold cwd inside the
        /// task's worktree — the <paramref name="actingAgent"/> (the agent that
        /// drove the task to done — may be a helper, not the recorded
        /// assignee), the task's assignee + helpers, and ALL temporary
        /// <c>"Agent *"</c> subagents. Unrelated
        /// named peer terminals (another agent working a different task) are
        /// excluded so they don't render the control envelope as visible
        /// channel noise (task d32c80eb).
        ///
        /// <para>Broadcasting <see cref="WorktreePruning"/> to
        /// <see cref="GetAllConnectedTerminals"/> meant every peer terminal
        /// surfaced the raw <c>{type:"worktree_pruning",...}</c> JSON as a
        /// <c>← multiterminal-channel</c> line. The visible render is Claude
        /// Code's channel feature (MT can't suppress a delivered message per
        /// se), so the only lever is the recipient set.</para>
        ///
        /// <para>Subagents register as <c>"Agent {name}"</c> where
        /// <c>{name}</c> is the Task-tool label / subagent_type — NOT the
        /// spawning agent's name (see activity-hook.js <c>registerSubagent</c>)
        /// — and the spawner link lives only in the subagent's
        /// <c>MULTITERMINAL_SPAWNER</c> env, not in <see cref="TerminalInfo"/>.
        /// So a subagent cannot be attributed to its parent by name; we keep
        /// every connected subagent (transient, few, and the shells most
        /// likely to be cwd-inside per task db4b18c6) rather than risk
        /// stranding one. Only named non-assignee/non-helper terminals are
        /// trimmed — exactly the bystander noise being fixed.</para>
        /// </summary>
        public List<TerminalInfo> GetWorktreeEvictionAudience(string taskId, string actingAgent)
        {
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(actingAgent)) named.Add(actingAgent);

            var task = !string.IsNullOrEmpty(taskId) ? GetTask(taskId) : null;
            if (task != null)
            {
                if (!string.IsNullOrWhiteSpace(task.Assignee)) named.Add(task.Assignee);
                if (task.Helpers != null)
                {
                    foreach (var h in task.Helpers)
                    {
                        if (!string.IsNullOrWhiteSpace(h)) named.Add(h);
                    }
                }
            }

            return GetAllConnectedTerminals()
                .Where(t => t?.Name != null && (IsTemporaryAgent(t.Name) || named.Contains(t.Name)))
                .ToList();
        }

        /// <summary>
        /// Get all registered terminals that are online (both connected and have online profiles).
        /// </summary>
        public List<TerminalInfo> GetTerminals()
        {
            return _terminals.Values.Where(t =>
            {
                // Must be connected
                if (!t.IsConnected) return false;

                // Exclude temporary subagents (e.g. "Agent Alice") from terminal listings
                if (IsTemporaryAgent(t.Name)) return false;

                // Check if profile exists and is online
                if (_profiles.TryGetValue(t.Name, out var profile))
                {
                    return profile.IsOnline;
                }

                // If no profile exists, allow (backwards compatibility)
                return true;
            }).ToList();
        }

        /// <summary>
        /// Get or set remote mode. When on, hooks relay questions to ClaudeRemote and
        /// push notifications fire to the owner's phone. When off (user at desk), all
        /// phone-directed traffic short-circuits so the phone stays silent.
        /// Persisted via SettingsService so the value survives MT restart.
        ///
        /// Gate sites (keep in sync when adding new push paths):
        ///   MT:
        ///     - PermissionRelayService.Bridge / BridgeChoiceAsync / BridgePlanApprovalAsync / Notify
        ///     - NotificationsController.ForwardToGatewayAsync (forcePush bypasses this gate)
        ///     - MessageBroker.ForwardMessagePushAsync
        ///   ClaudeRemote (consults this flag via GET /api/remote-mode):
        ///     - InboxMonitorService.CheckInbox (GetRemoteModeAsync helper)
        /// </summary>
        public bool IsRemoteMode
        {
            get
            {
                var v = SettingsService.Default.Get(SettingRemoteMode);
                return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Fires only when the persisted remoteMode value actually changes (post-idempotent-check).
        /// UI (terminal status bar pill, phone toggle) subscribes to stay in sync with server-side flips
        /// from the desktop-presence hook, X-Source auto-infer, and manual /api/remote-mode toggles.
        /// </summary>
        public event EventHandler<bool> RemoteModeChanged;

        public void SetRemoteMode(bool enabled)
        {
            // fa1101db R2a — "remote off" IS the desktop-presence signal (user is at the desk,
            // per the X-Source / desktop-hook contract). Stamp desktop activity on every off
            // signal — INCLUDING the idempotent no-op below — so the idle watcher's clock stays
            // fresh while the user keeps working at the desk with remote already off.
            if (!enabled) RecordDesktopActivity();

            // Idempotent — skip the write if the value isn't changing. Avoids rewriting
            // settings.txt on every UserPromptSubmit hook fire + every phone X-Source ping.
            if (IsRemoteMode == enabled) return;
            SettingsService.Default.Set(SettingRemoteMode, enabled ? "1" : "0");
            RaiseSafe(RemoteModeChanged, enabled);
        }

        /// <summary>
        /// fa1101db R4 — fires after a permission-relay request is confirmed stored by the Cloudflare
        /// Worker. The gateway host subscribes PushNotificationService to wake the phone with a VAPID
        /// web-push (the broker is a process-wide shared singleton, so an event raised from the :5050
        /// PermissionRelayService instance still reaches the gateway-host subscriber that owns the
        /// push subscriptions — no MCPServer→API.Gateway layer dependency).
        /// </summary>
        public event EventHandler<PermissionRelayPushEventArgs> PermissionRelayPushRequested;

        /// <summary>
        /// fa1101db R4 — raise <see cref="PermissionRelayPushRequested"/>. Called by
        /// PermissionRelayService right after PostCreateAsync confirms the Worker stored the request.
        /// Fire-and-forget: never throws into the caller's relay path (a push failure must not block
        /// the round-trip). Routed through <see cref="RaiseSafe{T}"/> (P5 / 1df2a534) so subscriber
        /// exceptions are isolated PER subscriber — the R4 idiom snapshotted the delegate but a single
        /// throwing handler still aborted the rest; RaiseSafe finishes that.
        /// </summary>
        public void NotifyPermissionRelayPush(string requestType, string agentName, string title, string body)
        {
            RaiseSafe(PermissionRelayPushRequested, new PermissionRelayPushEventArgs(requestType, agentName, title, body));
        }

        // fa1101db R2 — idle auto-on for remote mode. The relay (PermissionRelayService.Bridge*/
        // Notify) and the push paths are gated on IsRemoteMode, so when the user walks away the
        // phone must be able to receive prompts. We approximate "away" as "no desktop activity for
        // N minutes" (John's model: desktop typing → off, phone message → on, 30-min idle → on).
        private DateTime _lastDesktopActivityUtc = DateTime.UtcNow; // assume present at startup
        private const string SettingIdleRemoteOnMinutes = "idleRemoteOnMinutes";
        private const int DefaultIdleRemoteOnMinutes = 30;

        /// <summary>UTC of the last observed desktop-presence signal (remote-off / X-Source desktop).</summary>
        public DateTime LastDesktopActivityUtc => _lastDesktopActivityUtc;

        /// <summary>
        /// Record a desktop-presence signal (user is at the desk). Resets the idle clock. Called
        /// from SetRemoteMode(false) and directly from the X-Source desktop branch (which can
        /// short-circuit before SetRemoteMode when remote is already off).
        /// </summary>
        public void RecordDesktopActivity() => _lastDesktopActivityUtc = DateTime.UtcNow;

        /// <summary>
        /// fa1101db R2b — if the desk has been quiet for the configured threshold (settings key
        /// "idleRemoteOnMinutes", default 30) AND remote mode is currently OFF, turn it ON so
        /// notifications/permission prompts relay to the phone. Auto-ON only — never auto-off
        /// (a desktop signal flips it back instantly). Safe to call from a background timer thread;
        /// SetRemoteMode is already invoked off the UI thread by the X-Source infer path.
        /// </summary>
        public void CheckIdleRemoteAutoOn()
        {
            if (IsRemoteMode) return; // already on — nothing to do

            int thresholdMin = DefaultIdleRemoteOnMinutes;
            var raw = SettingsService.Default.Get(SettingIdleRemoteOnMinutes);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                thresholdMin = parsed;

            var idle = DateTime.UtcNow - _lastDesktopActivityUtc;
            if (idle.TotalMinutes < thresholdMin) return;

            DebugLogService?.Info("RemoteMode",
                $"idle {idle.TotalMinutes:F0}m ≥ {thresholdMin}m with no desktop activity → auto-enabling remote mode");
            SetRemoteMode(true);
        }

        /// <summary>
        /// Get terminal by ID, DocId, or name.
        /// </summary>
        public TerminalInfo GetTerminal(string idOrNameOrDocId)
        {
            // First try direct lookup by terminalId (dictionary key)
            if (_terminals.TryGetValue(idOrNameOrDocId, out var terminal) && terminal.IsConnected)
                return terminal;

            // Then try lookup by DocId or Name (only connected terminals)
            return _terminals.Values.FirstOrDefault(t =>
                t.IsConnected && (
                    (!string.IsNullOrEmpty(t.DocId) && t.DocId.Equals(idOrNameOrDocId, StringComparison.OrdinalIgnoreCase)) ||
                    t.Name.Equals(idOrNameOrDocId, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Send a message to a specific terminal.
        /// Messages are persisted to SQLite first for reliable delivery.
        /// </summary>
        public async Task<SendResult> SendMessage(string fromTerminalId, string toTerminalIdOrName, string content, string priority = null)
        {
            LogTrace($"SendMessage ENTRY: from={fromTerminalId}, to={toTerminalIdOrName}, priority={priority ?? "normal"}, content={content.Substring(0, Math.Min(50, content.Length))}...");

            var fromTerminal = GetTerminal(fromTerminalId);
            if (fromTerminal == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Sender terminal not found: {fromTerminalId}"
                };
            }

            var toTerminal = GetTerminal(toTerminalIdOrName);
            if (toTerminal == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Recipient terminal not found: {toTerminalIdOrName}"
                };
            }

            // Normalize priority
            var effectivePriority = priority ?? MultiTerminal.Services.MessageQueueDatabase.MessagePriority.Normal;

            // Persist to SQLite first for reliable delivery
            long queuedMessageId = 0;
            try
            {
                queuedMessageId = _messageQueueDb.EnqueueMessage(fromTerminal.Name, toTerminal.Name, content, "message", null, null, null, null, effectivePriority);
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Persisted message {queuedMessageId} to SQLite queue (from={fromTerminal.Name}, to={toTerminal.Name})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Failed to persist message: {ex.Message}");
                // Continue with in-memory delivery even if persistence fails
            }

            var message = new Message
            {
                Id = queuedMessageId > 0 ? queuedMessageId.ToString() : Guid.NewGuid().ToString("N"),
                From = fromTerminal.Name,
                To = toTerminal.Name,
                Content = content,
                Priority = effectivePriority
            };

            // Log message ID for debugging (searchable format)
            LogInfo($"MESSAGE SENT → [MSG-ID: {message.Id}] from [{fromTerminal.Name}] to [{toTerminal.Name}] | Content: {content.Substring(0, Math.Min(80, content.Length))}...");

            // Add to recipient's queue (ensure queue exists — may be missing after reconnect)
            var queue = _messageQueues.GetOrAdd(toTerminal.Id, _ => new BlockingCollection<Message>());
            queue.Add(message);

            // Add to history with pruning to prevent unbounded growth
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            // Update sender's last active time and activity status
            fromTerminal.LastActiveAt = DateTime.UtcNow;
            ActivityService?.UpdateActivity(fromTerminal.Name, "working", $"Chatting with {toTerminal.Name}");

            // Notify listeners
            RaiseSafe(MessageSent, message);

            // Push notification to owner's phone for all incoming messages
            _ = ForwardMessagePushAsync(fromTerminal.Name, content);

            // Mark as delivering to prevent retry race condition
            if (queuedMessageId > 0)
            {
                try
                {
                    _messageQueueDb.MarkDelivering(queuedMessageId);
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Message {queuedMessageId} marked as delivering (in-flight)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Failed to mark as delivering: {ex.Message}");
                }
            }

            // **TIER 1: Webhook delivery DISABLED — using inbox file delivery (Tier 2) instead.
            // To re-enable, uncomment the block below.
            bool deliverySuccess = false;
            /*
            try
            {
                LogInfo($"TIER 1: Attempting webhook delivery for message {message.Id}");
                deliverySuccess = await SendViaWebhook(message.Id, toTerminal.Name, fromTerminal.Name, content);

                if (deliverySuccess)
                {
                    LogInfo($"TIER 1: SUCCESS - Webhook delivered message {message.Id}");

                    // Mark as delivered in database
                    if (queuedMessageId > 0)
                    {
                        try
                        {
                            _messageQueueDb.MarkDelivered(queuedMessageId);
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Message {queuedMessageId} marked as delivered after webhook delivery");
                        }
                        catch (Exception ex)
                        {
                            LogError($"Failed to mark message as delivered: {ex.Message}");
                        }
                    }

                    return new SendResult
                    {
                        Success = true,
                        MessageId = message.Id
                    };
                }
                else
                {
                    LogInfo($"TIER 1: FAILED - Falling back to Tier 2 (callback) for message {message.Id}");
                }
            }
            catch (Exception ex)
            {
                LogError($"TIER 1: EXCEPTION - {ex.Message}, falling back to Tier 2");
            }
            */

            // **TIER 2: Try callback delivery (existing code)**
            try
            {
                if (OnMessageDelivery != null)
                {
                    LogInfo($"TIER 2: Attempting callback delivery for message {message.Id}");
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Calling OnMessageDelivery for message {message.Id} from {fromTerminal.Name} to {toTerminal.Id}");
                    deliverySuccess = await OnMessageDelivery(message.Id, toTerminal.Id, fromTerminal.Name, content);
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] OnMessageDelivery returned {deliverySuccess} for message {message.Id}");
                    LogInfo($"TIER 2: Callback returned {deliverySuccess}");
                }
            }
            catch (Exception ex)
            {
                LogError($"TIER 2: Push delivery failed: {ex.Message}");
            }

            if (deliverySuccess)
            {
                // Mark as delivered
                if (queuedMessageId > 0)
                {
                    try
                    {
                        _messageQueueDb.MarkDelivered(queuedMessageId);
                        LogInfo($"TIER 2: SUCCESS - Message {message.Id} delivered via callback and marked delivered");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to mark message as delivered: {ex.Message}");
                    }
                }

                return new SendResult
                {
                    Success = true,
                    MessageId = message.Id
                };
            }
            else
            {
                LogInfo($"TIER 2: FAILED - Message {message.Id} will retry via Tier 3 (polling)");

                // **TIER 3: Polling timer will retry automatically (existing code, no changes needed)**

                // Mark as failed for retry
                if (queuedMessageId > 0)
                {
                    try
                    {
                        _messageQueueDb.MarkFailed(queuedMessageId, "Initial delivery failed");
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Message {queuedMessageId} marked as failed - will retry");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update message status: {ex.Message}");
                    }
                }
            }

            return new SendResult
            {
                Success = true,
                MessageId = message.Id
            };
        }

        /// <summary>
        /// Deliver a message via webhook. Called by HttpWebhookService.
        /// </summary>
        public async Task<bool> DeliverMessageViaWebhook(string messageId, string toTerminalName, string fromTerminalName, string content)
        {
            LogTrace($"DeliverMessageViaWebhook: messageId={messageId}, to={toTerminalName}, from={fromTerminalName}");

            // Find recipient terminal
            var toTerminal = GetTerminal(toTerminalName);
            if (toTerminal == null)
            {
                LogError($"DeliverMessageViaWebhook: Recipient {toTerminalName} not found");
                return false;
            }

            // Attempt delivery via OnMessageDelivery callback
            bool deliverySuccess = false;
            try
            {
                if (OnMessageDelivery != null)
                {
                    LogInfo($"DeliverMessageViaWebhook: Calling OnMessageDelivery for message {messageId} to {toTerminal.Id}");
                    deliverySuccess = await OnMessageDelivery(messageId, toTerminal.Id, fromTerminalName, content);
                    LogInfo($"DeliverMessageViaWebhook: OnMessageDelivery returned {deliverySuccess}");
                }
                else
                {
                    LogInfo("DeliverMessageViaWebhook: OnMessageDelivery callback is null");
                }
            }
            catch (Exception ex)
            {
                LogError($"DeliverMessageViaWebhook: Delivery failed - {ex.Message}");
                return false;
            }

            if (deliverySuccess)
            {
                // Mark as delivered in database
                if (long.TryParse(messageId, out long msgId))
                {
                    try
                    {
                        _messageQueueDb.MarkDelivered(msgId);
                        LogInfo($"DeliverMessageViaWebhook: Message {messageId} marked as delivered");
                    }
                    catch (Exception ex)
                    {
                        LogError($"DeliverMessageViaWebhook: Failed to mark as delivered - {ex.Message}");
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempt to deliver message via webhook POST.
        /// </summary>
        private async Task<bool> SendViaWebhook(string messageId, string toTerminalName, string fromTerminalName, string content)
        {
            try
            {
                LogTrace($"SendViaWebhook: Attempting webhook delivery for message {messageId}");

                // Build POST data (form-encoded for simplicity)
                var postData = $"messageId={Uri.EscapeDataString(messageId)}" +
                              $"&to={Uri.EscapeDataString(toTerminalName)}" +
                              $"&from={Uri.EscapeDataString(fromTerminalName)}" +
                              $"&content={Uri.EscapeDataString(content)}";

                using var httpContent = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

                var response = await _httpClient.PostAsync("http://localhost:5000/message", httpContent);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    LogInfo($"SendViaWebhook: SUCCESS - {responseBody}");
                    return true;
                }
                else
                {
                    LogInfo($"SendViaWebhook: FAILED - Status {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogError($"SendViaWebhook: EXCEPTION - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a threaded reply message to another terminal.
        /// </summary>
        /// <param name="fromTerminalId">Sender terminal ID.</param>
        /// <param name="toTerminalIdOrName">Recipient terminal ID or name.</param>
        /// <param name="content">Message content.</param>
        /// <param name="replyToMessageId">ID of the message being replied to.</param>
        /// <returns>SendResult with success status and message ID.</returns>
        public async Task<SendResult> SendReply(string fromTerminalId, string toTerminalIdOrName, string content, string replyToMessageId)
        {
            var fromTerminal = GetTerminal(fromTerminalId);
            if (fromTerminal == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Sender terminal not found: {fromTerminalId}"
                };
            }

            var toTerminal = GetTerminal(toTerminalIdOrName);
            if (toTerminal == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Recipient terminal not found: {toTerminalIdOrName}"
                };
            }

            // Find the parent message to determine thread ID
            string threadId = null;
            Message parentMessage = null;

            lock (_historyLock)
            {
                parentMessage = _messageHistory.FirstOrDefault(m => m.Id == replyToMessageId);
            }

            if (parentMessage != null)
            {
                // If parent has a thread ID, use it; otherwise parent is the thread root
                threadId = !string.IsNullOrEmpty(parentMessage.ThreadId)
                    ? parentMessage.ThreadId
                    : parentMessage.Id;
            }
            else
            {
                // Parent not found in history, this will be a new thread
                threadId = replyToMessageId; // Use parent ID as thread ID
            }

            // Persist to SQLite first for reliable delivery
            long queuedMessageId = 0;
            try
            {
                queuedMessageId = _messageQueueDb.EnqueueMessage(
                    fromTerminal.Name,
                    toTerminal.Name,
                    content,
                    "message",      // notificationType
                    null,           // taskId
                    null,           // taskTitle
                    replyToMessageId,
                    threadId);
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Persisted threaded reply {queuedMessageId} to SQLite queue (replyTo: {replyToMessageId}, thread: {threadId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to persist threaded message: {ex.Message}");
                // Continue with in-memory delivery even if persistence fails
            }

            var message = new Message
            {
                Id = queuedMessageId > 0 ? queuedMessageId.ToString() : Guid.NewGuid().ToString("N"),
                From = fromTerminal.Name,
                To = toTerminal.Name,
                Content = content,
                ReplyToId = replyToMessageId,
                ThreadId = threadId
            };

            // Add to recipient's queue (ensure queue exists — may be missing after reconnect)
            var queue = _messageQueues.GetOrAdd(toTerminal.Id, _ => new BlockingCollection<Message>());
            queue.Add(message);

            // Add to history with pruning to prevent unbounded growth
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            // Update sender's last active time
            fromTerminal.LastActiveAt = DateTime.UtcNow;

            // Notify listeners
            RaiseSafe(MessageSent, message);

            // Mark as delivering to prevent retry race condition
            if (queuedMessageId > 0)
            {
                try
                {
                    _messageQueueDb.MarkDelivering(queuedMessageId);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Reply message {queuedMessageId} marked as delivering (in-flight)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to mark reply as delivering: {ex.Message}");
                }
            }

            // Notify for push delivery to terminal UI
            var deliverySuccess = false;
            try
            {
                if (OnMessageDelivery != null)
                {
                    LogInfo($"Calling OnMessageDelivery for reply message {message.Id} from {fromTerminal.Name} to {toTerminal.Id}");
                    deliverySuccess = await OnMessageDelivery(message.Id, toTerminal.Id, fromTerminal.Name, content);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Push delivery failed: {ex.Message}");
                deliverySuccess = false;
            }

            // Update SQLite status based on delivery result
            if (queuedMessageId > 0)
            {
                try
                {
                    if (deliverySuccess)
                    {
                        _messageQueueDb.MarkDelivered(queuedMessageId);
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Reply message {queuedMessageId} marked as delivered");
                    }
                    else
                    {
                        _messageQueueDb.MarkFailed(queuedMessageId, "Initial delivery failed");
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Reply message {queuedMessageId} marked as failed - will retry");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update message status: {ex.Message}");
                }
            }

            return new SendResult
            {
                Success = true,
                MessageId = message.Id
            };
        }

        /// <summary>
        /// Process pending messages from SQLite queue.
        /// Called by polling timer for retry delivery.
        /// </summary>
        /// <returns>Number of messages successfully delivered.</returns>
        public async Task<int> ProcessPendingMessages()
        {
            int deliveredCount = 0;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] ProcessPendingMessages ENTRY");

                // Reset any stale "delivering" messages that may be stuck
                int resetCount = _messageQueueDb.ResetStaleDeliveringMessages(30);
                if (resetCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Reset {resetCount} stale 'delivering' messages back to pending");
                }

                var pendingMessages = _messageQueueDb.GetPendingMessages();
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Found {pendingMessages.Count} pending messages to process");

                foreach (var queuedMsg in pendingMessages)
                {
                    var toTerminal = GetTerminal(queuedMsg.ToTerminal);
                    if (toTerminal == null || !toTerminal.IsConnected)
                    {
                        // Terminal not available, leave pending
                        continue;
                    }

                    try
                    {
                        // Mark as delivering to prevent concurrent retry attempts
                        _messageQueueDb.MarkDelivering(queuedMsg.Id);
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] RETRY: Message {queuedMsg.Id} marked as delivering");

                        // Attempt delivery and await completion
                        bool deliverySuccess = false;
                        if (OnMessageDelivery != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] RETRY: Calling OnMessageDelivery for queued message {queuedMsg.Id} from {queuedMsg.FromTerminal} to {toTerminal.Id}");
                            deliverySuccess = await OnMessageDelivery(queuedMsg.Id.ToString(), toTerminal.Id, queuedMsg.FromTerminal, queuedMsg.Content);
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] RETRY: OnMessageDelivery returned {deliverySuccess} for message {queuedMsg.Id}");
                        }

                        if (deliverySuccess)
                        {
                            // Mark as delivered
                            _messageQueueDb.MarkDelivered(queuedMsg.Id);
                            deliveredCount++;
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Retry delivered message {queuedMsg.Id} to {queuedMsg.ToTerminal}");
                        }
                        else
                        {
                            // Delivery failed, mark as failed
                            _messageQueueDb.MarkFailed(queuedMsg.Id, "Delivery callback returned false");
                            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Retry failed for message {queuedMsg.Id}: delivery returned false");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Mark as failed (increments retry count)
                        _messageQueueDb.MarkFailed(queuedMsg.Id, ex.Message);
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] Retry failed for message {queuedMsg.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] ProcessPendingMessages error: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MessageBroker] ProcessPendingMessages EXIT: {deliveredCount} messages delivered");
            return deliveredCount;
        }

        /// <summary>
        /// Get count of pending messages per terminal.
        /// </summary>
        public Dictionary<string, int> GetPendingMessageCounts()
        {
            try
            {
                return _messageQueueDb.GetPendingCounts();
            }
            catch
            {
                return new Dictionary<string, int>();
            }
        }

        /// <summary>
        /// Clean up old delivered messages from SQLite.
        /// </summary>
        /// <param name="olderThanHours">Delete messages older than this many hours.</param>
        /// <returns>Number of messages deleted.</returns>
        public int CleanupOldMessages(int olderThanHours = 24)
        {
            try
            {
                return _messageQueueDb.CleanupOldMessages(olderThanHours);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Cleanup failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Acknowledge message delivery status from recipient.
        /// </summary>
        /// <param name="messageId">The message ID to acknowledge.</param>
        /// <param name="success">True if message was successfully delivered, false if failed.</param>
        /// <param name="error">Optional error description if delivery failed.</param>
        public void AcknowledgeMessage(long messageId, bool success, string error = null)
        {
            try
            {
                if (success)
                {
                    _messageQueueDb.MarkDelivered(messageId);
                    LogInfo($"Message {messageId} acknowledged as delivered by recipient");
                }
                else
                {
                    _messageQueueDb.MarkFailed(messageId, error ?? "Recipient reported delivery failure");
                    LogInfo($"Message {messageId} acknowledged as failed by recipient: {error}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Acknowledge failed for message {messageId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast a message to all terminals except the sender.
        /// </summary>
        public async Task<BroadcastResult> Broadcast(string fromTerminalId, string content)
        {
            var fromTerminal = GetTerminal(fromTerminalId);
            if (fromTerminal == null)
            {
                return new BroadcastResult
                {
                    Success = false,
                    Error = $"Sender terminal not found: {fromTerminalId}"
                };
            }

            var recipients = _terminals.Values
                .Where(t => t.Id != fromTerminalId && t.IsConnected)
                .ToList();

            foreach (var recipient in recipients)
            {
                var message = new Message
                {
                    From = fromTerminal.Name,
                    To = recipient.Name,
                    Content = content,
                    IsBroadcast = true
                };

                if (_messageQueues.TryGetValue(recipient.Id, out var queue))
                {
                    queue.Add(message);
                }

                // Add to history with pruning to prevent unbounded growth
                lock (_historyLock)
                {
                    _messageHistory.Add(message);
                    while (_messageHistory.Count > MaxHistorySize)
                        _messageHistory.RemoveAt(0);
                }

                RaiseSafe(MessageSent, message);

                // Notify for push delivery to terminal UI
                if (OnMessageDelivery != null)
                {
                    LogInfo($"BROADCAST: Calling OnMessageDelivery for message {message.Id} from {fromTerminal.Name} to {recipient.Id}");
                    await OnMessageDelivery(message.Id, recipient.Id, fromTerminal.Name, content);
                }
            }

            fromTerminal.LastActiveAt = DateTime.UtcNow;
            ActivityService?.UpdateActivity(fromTerminal.Name, "working", "Broadcasting to team");

            return new BroadcastResult
            {
                Success = true,
                RecipientCount = recipients.Count
            };
        }

        /// <summary>
        /// Broadcast a system message to all connected terminals.
        /// Used for system-level notifications like chat pause/resume.
        /// </summary>
        public async Task<BroadcastResult> BroadcastSystemMessage(string content)
        {
            var recipients = _terminals.Values
                .Where(t => t.IsConnected)
                .ToList();

            foreach (var recipient in recipients)
            {
                var message = new Message
                {
                    From = "SYSTEM",
                    To = recipient.Name,
                    Content = content,
                    IsBroadcast = true
                };

                if (_messageQueues.TryGetValue(recipient.Id, out var queue))
                {
                    queue.Add(message);
                }

                // Add to history with pruning
                lock (_historyLock)
                {
                    _messageHistory.Add(message);
                    while (_messageHistory.Count > MaxHistorySize)
                        _messageHistory.RemoveAt(0);
                }

                RaiseSafe(MessageSent, message);

                // Notify for push delivery to terminal UI
                if (OnMessageDelivery != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] SYSTEM_BROADCAST: Calling OnMessageDelivery for message {message.Id} from SYSTEM to {recipient.Id}");
                    await OnMessageDelivery(message.Id, recipient.Id, "SYSTEM", content);
                }
            }

            return new BroadcastResult
            {
                Success = true,
                RecipientCount = recipients.Count
            };
        }

        #region Two-Tier Notification System

        /// <summary>
        /// Tier 1 notification: Lightweight, non-interruptive FYI.
        /// Sent when a terminal is added as a helper to a task.
        /// </summary>
        /// <param name="helperName">Name of the helper terminal being notified.</param>
        /// <param name="taskId">ID of the task.</param>
        /// <param name="taskTitle">Title of the task.</param>
        /// <param name="assignee">Name of the primary task assignee.</param>
        /// <returns>SendResult indicating success or failure.</returns>
        public async Task<SendResult> NotifyHelperAdded(string helperName, string taskId, string taskTitle, string assignee)
        {
            var helper = GetTerminal(helperName);
            if (helper == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Helper terminal not found: {helperName}"
                };
            }

            var content = $"Added as helper: {taskTitle} (Assignee: {assignee})";

            // Persist to SQLite with notification type
            long queuedMessageId = 0;
            try
            {
                queuedMessageId = _messageQueueDb.EnqueueMessage(
                    "SYSTEM", helper.Name, content, "helper_added", taskId, taskTitle);
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Persisted helper_added notification {queuedMessageId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to persist helper_added notification: {ex.Message}");
            }

            var message = new Message
            {
                Id = queuedMessageId > 0 ? queuedMessageId.ToString() : Guid.NewGuid().ToString("N"),
                From = "SYSTEM",
                To = helper.Name,
                Content = content,
                NotificationType = NotificationType.HelperAdded,
                TaskId = taskId,
                TaskTitle = taskTitle
            };

            // Add to recipient's queue (ensure queue exists — may be missing after reconnect)
            var queue = _messageQueues.GetOrAdd(helper.Id, _ => new BlockingCollection<Message>());
            queue.Add(message);

            // Add to history
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            RaiseSafe(MessageSent, message);

            // Mark as delivering to prevent retry race condition
            if (queuedMessageId > 0)
            {
                try
                {
                    _messageQueueDb.MarkDelivering(queuedMessageId);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Helper notification {queuedMessageId} marked as delivering");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to mark helper notification as delivering: {ex.Message}");
                }
            }

            // Attempt push delivery
            var deliverySuccess = false;
            try
            {
                if (OnMessageDelivery != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] HELPER_ADDED: Calling OnMessageDelivery for message {message.Id} from SYSTEM to {helper.Id}");
                    deliverySuccess = await OnMessageDelivery(message.Id, helper.Id, "SYSTEM", content);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Push delivery failed for helper_added: {ex.Message}");
                deliverySuccess = false;
            }

            // Update SQLite status
            if (queuedMessageId > 0)
            {
                try
                {
                    if (deliverySuccess)
                    {
                        _messageQueueDb.MarkDelivered(queuedMessageId);
                    }
                    else
                    {
                        _messageQueueDb.MarkFailed(queuedMessageId, "Initial delivery failed");
                    }
                }
                catch { /* ignore */ }
            }

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = assignee,
                Type = "helper",
                Action = "added",
                Content = $"Added {helperName} as helper on: {taskTitle}",
                RelatedId = taskId
            });

            return new SendResult
            {
                Success = true,
                MessageId = message.Id
            };
        }

        /// <summary>
        /// Tier 2 notification: Prominent, action-required.
        /// Sent when help is actively requested from a terminal.
        /// </summary>
        /// <param name="helperName">Name of the helper terminal being requested.</param>
        /// <param name="taskId">ID of the task.</param>
        /// <param name="taskTitle">Title of the task.</param>
        /// <param name="requester">Name of the terminal requesting help.</param>
        /// <param name="details">Details about what help is needed.</param>
        /// <returns>SendResult indicating success or failure.</returns>
        public async Task<SendResult> NotifyHelpRequested(string helperName, string taskId, string taskTitle, string requester, string details = null)
        {
            var helper = GetTerminal(helperName);
            if (helper == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Helper terminal not found: {helperName}"
                };
            }

            var content = string.IsNullOrEmpty(details)
                ? $"HELP NEEDED from {requester} on task: {taskTitle}"
                : $"HELP NEEDED from {requester}: {details} [Task: {taskTitle}]";

            // Persist to SQLite with notification type
            long queuedMessageId = 0;
            try
            {
                queuedMessageId = _messageQueueDb.EnqueueMessage(
                    requester, helper.Name, content, "help_requested", taskId, taskTitle);
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Persisted help_requested notification {queuedMessageId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to persist help_requested notification: {ex.Message}");
            }

            var message = new Message
            {
                Id = queuedMessageId > 0 ? queuedMessageId.ToString() : Guid.NewGuid().ToString("N"),
                From = requester,
                To = helper.Name,
                Content = content,
                NotificationType = NotificationType.HelpRequested,
                TaskId = taskId,
                TaskTitle = taskTitle
            };

            // Add to recipient's queue (ensure queue exists — may be missing after reconnect)
            var queue = _messageQueues.GetOrAdd(helper.Id, _ => new BlockingCollection<Message>());
            queue.Add(message);

            // Add to history
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            RaiseSafe(MessageSent, message);

            // Mark as delivering to prevent retry race condition
            if (queuedMessageId > 0)
            {
                try
                {
                    _messageQueueDb.MarkDelivering(queuedMessageId);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Help request {queuedMessageId} marked as delivering");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to mark help request as delivering: {ex.Message}");
                }
            }

            // Attempt push delivery
            var deliverySuccess = false;
            try
            {
                if (OnMessageDelivery != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] HELP_REQUESTED: Calling OnMessageDelivery for message {message.Id} from {requester} to {helper.Id}");
                    deliverySuccess = await OnMessageDelivery(message.Id, helper.Id, requester, content);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Push delivery failed for help_requested: {ex.Message}");
                deliverySuccess = false;
            }

            // Update SQLite status
            if (queuedMessageId > 0)
            {
                try
                {
                    if (deliverySuccess)
                    {
                        _messageQueueDb.MarkDelivered(queuedMessageId);
                    }
                    else
                    {
                        _messageQueueDb.MarkFailed(queuedMessageId, "Initial delivery failed");
                    }
                }
                catch { /* ignore */ }
            }

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = requester,
                Type = "helper",
                Action = "help_requested",
                Content = $"Requested help from {helperName} on: {taskTitle}",
                RelatedId = taskId
            });

            return new SendResult
            {
                Success = true,
                MessageId = message.Id
            };
        }

        /// <summary>
        /// Notify multiple helpers at once (Tier 1 - lightweight).
        /// </summary>
        /// <param name="helperNames">List of helper terminal names.</param>
        /// <param name="taskId">ID of the task.</param>
        /// <param name="taskTitle">Title of the task.</param>
        /// <param name="assignee">Name of the primary task assignee.</param>
        /// <returns>Number of successful notifications.</returns>
        public async Task<int> NotifyHelpersAdded(IEnumerable<string> helperNames, string taskId, string taskTitle, string assignee)
        {
            int successCount = 0;
            foreach (var helper in helperNames)
            {
                var result = await NotifyHelperAdded(helper, taskId, taskTitle, assignee);
                if (result.Success) successCount++;
            }
            return successCount;
        }

        /// <summary>
        /// Notify an assignee that their task has become stale.
        /// Day 7: Warning notification ("Still relevant?")
        /// Day 14: Urgent notification ("Close or re-prioritize")
        /// </summary>
        /// <param name="assigneeName">Name of the terminal that owns the task.</param>
        /// <param name="taskId">ID of the stale task.</param>
        /// <param name="taskTitle">Title of the stale task.</param>
        /// <param name="staleLevel">1 = day 7 warning, 2 = day 14 urgent.</param>
        /// <param name="daysPaused">Number of days the task has been paused.</param>
        /// <returns>SendResult indicating success or failure.</returns>
        public async Task<SendResult> NotifyStaleTask(string assigneeName, string taskId, string taskTitle, int staleLevel, int daysPaused)
        {
            var assignee = GetTerminal(assigneeName);
            if (assignee == null)
            {
                return new SendResult
                {
                    Success = false,
                    Error = $"Assignee terminal not found: {assigneeName}"
                };
            }

            // Generate appropriate notification message based on stale level
            string content = staleLevel >= 2
                ? $"\u26a0\ufe0f ATTENTION: '{taskTitle}' paused for {daysPaused} days. Please close or re-prioritize."
                : $"\u23f0 Stale task check: '{taskTitle}' paused for {daysPaused} days. Still relevant? Use respond_to_stale to confirm.";

            // Persist to SQLite with notification type
            long queuedMessageId = 0;
            try
            {
                queuedMessageId = _messageQueueDb.EnqueueMessage(
                    "SYSTEM", assignee.Name, content, "stale_task", taskId, taskTitle);
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Persisted stale_task notification {queuedMessageId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to persist stale_task notification: {ex.Message}");
            }

            var message = new Message
            {
                Id = queuedMessageId > 0 ? queuedMessageId.ToString() : Guid.NewGuid().ToString("N"),
                From = "SYSTEM",
                To = assignee.Name,
                Content = content,
                NotificationType = staleLevel >= 2 ? NotificationType.HelpRequested : NotificationType.HelperAdded,
                TaskId = taskId,
                TaskTitle = taskTitle
            };

            // Add to recipient's queue (ensure queue exists — may be missing after reconnect)
            var queue = _messageQueues.GetOrAdd(assignee.Id, _ => new BlockingCollection<Message>());
            queue.Add(message);

            // Add to history
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            RaiseSafe(MessageSent, message);

            // Mark as delivering to prevent retry race condition
            if (queuedMessageId > 0)
            {
                try
                {
                    _messageQueueDb.MarkDelivering(queuedMessageId);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Stale task notification {queuedMessageId} marked as delivering");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to mark stale notification as delivering: {ex.Message}");
                }
            }

            // Attempt push delivery
            var deliverySuccess = false;
            try
            {
                if (OnMessageDelivery != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] STALE_TASK: Calling OnMessageDelivery for message {message.Id} from SYSTEM to {assignee.Id}");
                    deliverySuccess = await OnMessageDelivery(message.Id, assignee.Id, "SYSTEM", content);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Push delivery failed for stale_task: {ex.Message}");
                deliverySuccess = false;
            }

            // Update SQLite status
            if (queuedMessageId > 0)
            {
                try
                {
                    if (deliverySuccess)
                    {
                        _messageQueueDb.MarkDelivered(queuedMessageId);
                    }
                    else
                    {
                        _messageQueueDb.MarkFailed(queuedMessageId, "Initial delivery failed");
                    }
                }
                catch { /* ignore */ }
            }

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = "System",
                Type = "stale",
                Action = staleLevel >= 2 ? "day14_warning" : "day7_warning",
                Content = content,
                RelatedId = taskId
            });

            return new SendResult
            {
                Success = true,
                MessageId = message.Id
            };
        }

        #endregion

        /// <summary>
        /// Get pending messages for a terminal.
        /// </summary>
        public List<Message> GetMessages(string terminalId)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return new List<Message>();

            terminal.LastActiveAt = DateTime.UtcNow;

            var messages = new List<Message>();
            if (_messageQueues.TryGetValue(terminal.Id, out var queue))
            {
                while (queue.TryTake(out var message))
                {
                    message.Delivered = true;
                    messages.Add(message);
                }
            }

            // Sort by priority (critical first) then by timestamp within each tier
            if (messages.Count > 1)
            {
                messages.Sort((a, b) =>
                {
                    int aPri = MultiTerminal.Services.MessageQueueDatabase.MessagePriority.ToSortOrder(a.Priority);
                    int bPri = MultiTerminal.Services.MessageQueueDatabase.MessagePriority.ToSortOrder(b.Priority);
                    if (aPri != bPri) return bPri.CompareTo(aPri); // higher priority first
                    return a.Timestamp.CompareTo(b.Timestamp); // older first within same priority
                });
            }

            return messages;
        }

        /// <summary>
        /// Get all message history.
        /// </summary>
        public List<Message> GetMessageHistory(int maxCount = 100)
        {
            lock (_historyLock)
            {
                return _messageHistory
                    .OrderByDescending(m => m.Timestamp)
                    .Take(maxCount)
                    .OrderBy(m => m.Timestamp)
                    .ToList();
            }
        }

        /// <summary>
        /// Clear all messages and history.
        /// </summary>
        public void ClearAll()
        {
            foreach (var queue in _messageQueues.Values)
            {
                while (queue.TryTake(out _)) { }
            }

            lock (_historyLock)
            {
                _messageHistory.Clear();
            }
        }

        #region Kanban Task Methods

        /// <summary>
        /// Get a task by ID from the in-memory cache.
        /// </summary>
        public KanbanTask GetTask(string taskId)
        {
            return _tasks.TryGetValue(taskId, out var task) ? task : null;
        }

        /// <summary>
        /// Save a task object to database and broadcast update.
        /// Use when you've modified a task's properties directly.
        /// </summary>
        public void SaveTask(KanbanTask task)
        {
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save task: {ex.Message}"); }
            BroadcastTaskUpdate();
        }

        // Persists an agent report and fires ReportSaved so the kanban card
        // badges refresh. Used by TasksPanelControl when the human reviewer
        // hits Pass — we snapshot the cleared review_notes block to task_reports
        // before nulling so the audit trail survives (task 87ee90c3 F5).
        public string SaveTaskReport(string taskId, string agentName, string reportType, string reportContent, string verdict, int? score, string createdBy)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                _taskDb.SaveTaskReport(id, taskId, null, agentName, reportType ?? "markdown", reportContent, verdict, score, createdBy);
                NotifyReportSaved(taskId, id, agentName, verdict);
                return id;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] SaveTaskReport failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolve a caller-supplied project id to the canonical key stored in
        /// <see cref="_projects"/>. Agents routinely copy the 8-char short id
        /// from chip-formatted output (e.g. <c>list_projects</c>) instead of
        /// the full GUID; storing the truncated value would silently break
        /// every downstream lookup (worktree create-hook gate, project chips,
        /// filtering).
        ///
        /// _projects can hold a mix of 8-char keys (CreateProject default) and
        /// 36-char dashed GUIDs (project.json sync). When a short exact-match
        /// also prefixes one or more longer keys, the situation is ambiguous —
        /// we return null rather than guess and bind the task to the wrong
        /// project. Callers (CreateTask, UpdateTaskProject) treat null on
        /// non-empty input as "ambiguous, refuse to bind" and surface a
        /// failure to the user; SetTaskActive treats it as "skip worktree
        /// creation, emit a create_skipped event so the case is visible".
        ///
        /// Resolution order (each step short-circuits the next):
        /// 1. null / empty input → null silently.
        /// 1b. non-empty whitespace input → null + debug warning (probable
        ///     caller bug; behavior identical to null/empty otherwise).
        /// 2. exact key match found:
        ///    a. AND a longer key also starts with input AND input.Length &lt; 32
        ///       (so input itself is shorter than a canonical GUID) → null
        ///       + debug warning ("ambiguous, returning null").
        ///    b. otherwise → trimmed (caller intended this exact key).
        /// 3. input shorter than 4 chars → trimmed (don't prefix-match noise).
        /// 4. exactly one key starts with input (OrdinalIgnoreCase) → full key.
        /// 5. multiple keys start with input → null + debug warning
        ///    (genuinely ambiguous; symmetric with case 2a).
        /// 6. zero prefix-matches → trimmed + debug warning (unknown raw
        ///    value; let downstream lookups fail loudly rather than swallow).
        /// </summary>
        private string NormalizeProjectId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Non-null, non-empty, but all whitespace. Behavior is the same
                // as null/empty (no project bound) but log it so caller bugs
                // (e.g. trimmed-too-aggressively-on-the-frontend) are visible.
                System.Diagnostics.Debug.WriteLine(
                    $"[MessageBroker] NormalizeProjectId: input was non-empty whitespace ({raw.Length} chars); treating as no-project intent.");
                return null;
            }
            string trimmed = raw.Trim();

            // Scan once; record exact match and prefix-match population in a single pass.
            bool exactMatch = false;
            string prefixOnlyMatch = null;
            int prefixOnlyHits = 0;
            foreach (var key in _projects.Keys)
            {
                if (string.Equals(key, trimmed, StringComparison.Ordinal))
                {
                    exactMatch = true;
                    continue;
                }
                if (trimmed.Length >= 4
                    && key.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    prefixOnlyMatch = key;
                    prefixOnlyHits++;
                }
            }

            if (exactMatch)
            {
                // Aliased exact match: input is itself a key but other keys start
                // with it too. Length < 32 catches both the 8-char short-id case
                // and any other non-canonical key shorter than a full GUID
                // (32-char "N" format, 36-char dashed format).
                if (prefixOnlyHits > 0 && trimmed.Length < 32)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] NormalizeProjectId: '{trimmed}' is a short exact key that also prefixes {prefixOnlyHits} longer key(s); ambiguous, returning null. Caller should pass the full id.");
                    return null;
                }
                return trimmed;
            }

            if (trimmed.Length < 4) return trimmed;

            if (prefixOnlyHits == 1) return prefixOnlyMatch;

            if (prefixOnlyHits >= 2)
            {
                // Genuinely ambiguous: input prefixes multiple registered keys.
                // Symmetric with the aliased-exact-match case above — return
                // null so callers (CreateTask, UpdateTaskProject) fast-fail
                // instead of storing a raw value that would silently fail
                // every downstream _projects lookup.
                System.Diagnostics.Debug.WriteLine(
                    $"[MessageBroker] NormalizeProjectId: '{trimmed}' prefixes {prefixOnlyHits} registered keys; ambiguous, returning null. Caller should pass the full id.");
                return null;
            }

            // prefixOnlyHits == 0: unknown raw value, no exact and no prefix
            // match. Storing it preserves caller intent and lets the value
            // round-trip — downstream lookups fail loudly rather than this
            // call swallowing a typo silently.
            System.Diagnostics.Debug.WriteLine(
                $"[MessageBroker] NormalizeProjectId: '{trimmed}' had no prefix match in _projects; storing raw value.");
            return trimmed;
        }

        /// <summary>
        /// Public dry-run wrapper over <see cref="NormalizeProjectId"/>. Lets UI
        /// callers pre-validate a project id before submitting an edit, without
        /// mutating any state.
        ///
        /// Returns the canonical project id when the input resolves cleanly:
        /// - null/empty/whitespace input → returns null with ambiguous=false
        ///   (caller intent: "no project bound"; the empty-string and the
        ///   ambiguous-id paths must stay distinguishable so the UI can choose
        ///   to clear-the-binding vs. report-an-error).
        /// - Non-empty input that resolves to null (aliased-exact short id, or
        ///   — once the multi-prefix-match path is tightened — a short id that
        ///   prefixes multiple registered projects) → returns null with
        ///   ambiguous=true. Caller should surface the error to the user
        ///   instead of submitting the edit.
        /// - Non-empty input that resolves to a registered key → returns the
        ///   canonical full key with ambiguous=false.
        /// - Non-empty input with no exact and no unique-prefix match (unknown
        ///   raw value) → returns the trimmed input with ambiguous=false. The
        ///   downstream write path will store the raw value and emit a debug
        ///   warning, matching the existing fallback behaviour.
        /// </summary>
        public string TryNormalizeProjectId(string raw, out bool ambiguous)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                ambiguous = false;
                return null;
            }
            string canonical = NormalizeProjectId(raw);
            ambiguous = (canonical == null);
            return canonical;
        }

        /// <summary>
        /// Single source of truth for "does this task get a git worktree?" — task
        /// 4bcd1e24. Both <see cref="SetTaskActive"/> (activation) and the
        /// <see cref="UpdateTaskStatus"/> done-gate (completion) call this so the
        /// two can never disagree about a task's worktree eligibility. Before this
        /// consolidation each site inlined its own check and they had drifted:
        /// activation normalized <see cref="KanbanTask.ProjectId"/> via
        /// <see cref="NormalizeProjectId"/> before resolving the project, but the
        /// done-gate resolved <c>_projects</c> with the raw id — so a legacy
        /// truncated (8-char short) ProjectId materialized a worktree at activation
        /// yet silently no-op'd at completion (no auto-commit/prune/merge).
        ///
        /// <para>Read-only: this resolves the canonical project id and filesystem
        /// path WITHOUT mutating the task row. A caller that wants to self-heal a
        /// truncated ProjectId (only <see cref="SetTaskActive"/> does) persists
        /// <paramref name="canonicalProjectId"/> itself after calling this.</para>
        /// </summary>
        /// <param name="task">Task to evaluate (null → ineligible).</param>
        /// <param name="projectPath">Resolved project filesystem path when eligible; null otherwise.</param>
        /// <param name="canonicalProjectId">Normalized project id when one resolved; null/empty otherwise.</param>
        /// <param name="skipReason">
        /// Human-readable reason when ineligible; null when eligible. Reasons mirror
        /// the strings the activation path historically surfaced so callers can keep
        /// emitting identical user-facing notices.
        /// </param>
        /// <returns>
        /// true only when worktree mode is on AND the task's project resolves to a
        /// real filesystem path.
        /// </returns>
        private bool TryResolveWorktreeEligibility(
            KanbanTask task,
            out string projectPath,
            out string canonicalProjectId,
            out string skipReason)
        {
            projectPath = null;
            canonicalProjectId = null;
            skipReason = null;

            if (!MultiTerminal.Services.WorktreeConfig.IsEnabled)
            {
                skipReason = "worktree mode is off";
                return false;
            }

            if (task == null || string.IsNullOrEmpty(task.ProjectId))
            {
                skipReason = "task has no project association";
                return false;
            }

            canonicalProjectId = NormalizeProjectId(task.ProjectId);

            if (string.IsNullOrEmpty(canonicalProjectId))
            {
                skipReason = "id is ambiguous (matches multiple registered projects, or is a short prefix of one); pass the full project id";
                return false;
            }

            if (!_projects.TryGetValue(canonicalProjectId, out var proj))
            {
                skipReason = "project not registered or registry not yet loaded";
                return false;
            }

            if (string.IsNullOrEmpty(proj.Path))
            {
                skipReason = "project is registered but has no filesystem path";
                return false;
            }

            projectPath = proj.Path;
            return true;
        }

        /// <summary>
        /// Create a new task on the Kanban board.
        /// </summary>
        public CreateTaskResult CreateTask(string title, string description, string createdBy, string status = "todo", string priority = "normal", string projectId = null)
        {
            var validStatuses = new[] { "todo", "in_progress", "done", "suggestion" };
            if (!validStatuses.Contains(status))
                status = "todo";

            // Validate priority
            var validPriorities = new[] { "urgent", "normal", "low" };
            if (!validPriorities.Contains(priority))
                priority = "normal";

            // Distinguish "no project" (empty input — caller intent) from
            // "ambiguous project id" (NormalizeProjectId returned null because
            // a short input matched a key that ALSO prefixes longer keys).
            // Failing fast on ambiguity prevents the call from looking like a
            // success while silently dropping the requested project.
            string canonicalProjectId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                canonicalProjectId = NormalizeProjectId(projectId);
                if (canonicalProjectId == null)
                {
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
            }

            var task = new KanbanTask
            {
                Title = title,
                Description = description,
                CreatedBy = createdBy,
                Status = status,
                Priority = priority,
                ProjectId = canonicalProjectId,
                // Seed sort_order at the end of the target column so new tasks
                // land at the bottom — matches typical kanban UX (new work
                // appears at the bottom of To Do, not the top).
                SortOrder = _taskDb.GetNextSortOrderForStatus(status)
            };

            if (_tasks.TryAdd(task.Id, task))
            {
                // Persist to database. If the durable write fails, evict the
                // in-memory entry so callers don't observe a task that won't
                // survive a restart, skip the broadcast/activity announcements
                // (they would advertise a row that doesn't exist), and return
                // Success=false so the caller-side toast plumbing fires.
                try
                {
                    _taskDb.SaveTask(task);
                }
                catch (Exception ex)
                {
                    _tasks.TryRemove(task.Id, out _);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save task: {ex.Message}");
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Failed to persist new task: {ex.Message}"
                    };
                }

                BroadcastTaskUpdate();

                // Record activity for the feed
                RecordActivity(new ActivityEvent
                {
                    Terminal = createdBy ?? "System",
                    Type = "task",
                    Action = "created",
                    Content = $"Created task: {title}",
                    RelatedId = task.Id
                });

                // Update office panel activity
                if (!string.IsNullOrEmpty(createdBy))
                    ActivityService?.UpdateActivity(createdBy, "working", $"Created task: {title}");

                return new CreateTaskResult { Success = true, TaskId = task.Id };
            }

            return new CreateTaskResult { Success = false, Error = "Failed to create task" };
        }

        /// <summary>
        /// Create a quick-task — a lightweight, immutable attribution anchor for trivial
        /// working-tree changes that don't warrant a full kanban card. Always status='done',
        /// no checklist, no plan. After creation only the title can be edited (via
        /// <see cref="UpdateQuickTaskTitle"/>); all other mutation methods reject the task.
        /// Hidden by default from list_tasks (controller-level filter). See task d42423e3.
        /// </summary>
        public CreateTaskResult CreateQuickTask(string title, string createdBy, string projectId = null)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new CreateTaskResult { Success = false, Error = "Title required" };

            string canonicalProjectId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                canonicalProjectId = NormalizeProjectId(projectId);
                if (canonicalProjectId == null)
                {
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
            }

            var task = new KanbanTask
            {
                Title = title,
                Description = null,
                CreatedBy = createdBy,
                Status = "done",
                Priority = "normal",
                ProjectId = canonicalProjectId,
                IsQuickTask = true,
                ChecklistJson = "[]",
                ImplementationChecklistJson = "[]",
                SortOrder = _taskDb.GetNextSortOrderForStatus("done")
            };

            if (_tasks.TryAdd(task.Id, task))
            {
                try
                {
                    _taskDb.SaveTask(task);
                }
                catch (Exception ex)
                {
                    _tasks.TryRemove(task.Id, out _);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save quick task: {ex.Message}");
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Failed to persist quick task: {ex.Message}"
                    };
                }

                BroadcastTaskUpdate();

                RecordActivity(new ActivityEvent
                {
                    Terminal = createdBy ?? "System",
                    Type = "task",
                    Action = "quick_created",
                    Content = $"Quick task: {title}",
                    RelatedId = task.Id
                });

                return new CreateTaskResult { Success = true, TaskId = task.Id };
            }

            return new CreateTaskResult { Success = false, Error = "Failed to create quick task" };
        }

        /// <summary>
        /// Update the title of a quick-task. The only mutation allowed on quick-tasks
        /// (per the immutability contract — see <see cref="CreateQuickTask"/>).
        /// Returns Success=false if the task isn't a quick-task or doesn't exist.
        /// </summary>
        public UpdateTaskResult UpdateQuickTaskTitle(string taskId, string newTitle, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };

            if (!task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Task {taskId} is not a quick-task. Use UpdateTask for regular tasks." };

            if (string.IsNullOrWhiteSpace(newTitle))
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };

            var previousTitle = task.Title;
            task.Title = newTitle;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update quick task title: {ex.Message}"); }

            BroadcastTaskUpdate();

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = $"Renamed quick task: '{previousTitle}' → '{newTitle}'",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Claim a task by assigning it to a terminal.
        /// Priority-aware stack behavior based on task's priority field:
        /// - urgent: Pauses current active task (unless in critical section), makes this task active
        /// - normal: Queues behind current active task (default)
        /// - low: Added to bottom of paused stack
        /// </summary>
        /// <param name="taskId">The task ID to claim</param>
        /// <param name="assignee">The terminal name claiming the task</param>
        /// <param name="priorityOverride">Optional priority override. If specified, uses this instead of task's priority.</param>
        public ClaimTaskResult ClaimTask(string taskId, string assignee, string priorityOverride = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new ClaimTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Allow re-claiming by the same person, but block if claimed by someone else
            if (!string.IsNullOrEmpty(task.Assignee) && !task.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase))
            {
                return new ClaimTaskResult { Success = false, Error = $"Task already claimed by {task.Assignee}" };
            }

            // Use priority override if specified, otherwise use task's priority
            var validPriorities = new[] { "urgent", "normal", "low" };
            var priority = !string.IsNullOrWhiteSpace(priorityOverride) && validPriorities.Contains(priorityOverride)
                ? priorityOverride
                : (task.Priority ?? "normal");

            // Find current active task for this assignee
            var currentActiveTask = _tasks.Values.FirstOrDefault(t =>
                t.Assignee != null &&
                t.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase) &&
                t.SubStatus == "active" &&
                t.Id != taskId);

            // Handle based on priority
            bool wasQueued = false;
            string queuedBehind = null;

            if (currentActiveTask != null)
            {
                switch (priority)
                {
                    case "urgent":
                        // Check if terminal is in critical section
                        if (IsTerminalInCriticalSection(assignee))
                        {
                            // Queue the urgent task - it will activate when critical section ends
                            QueueTaskForTerminal(task, assignee, isLowPriority: false);
                            wasQueued = true;
                            queuedBehind = currentActiveTask.Title;

                            RecordActivity(new ActivityEvent
                            {
                                Terminal = assignee,
                                Type = "task",
                                Action = "queued",
                                Content = $"Urgent task queued (terminal in critical section): {task.Title}",
                                RelatedId = taskId
                            });
                        }
                        else
                        {
                            // Pause current active task and make urgent task active
                            PauseTaskWithSummary(currentActiveTask, assignee);
                            MakeTaskActive(task, assignee, taskId);
                        }
                        break;

                    case "low":
                        // Add to bottom of paused stack
                        QueueTaskForTerminal(task, assignee, isLowPriority: true);
                        wasQueued = true;
                        queuedBehind = currentActiveTask.Title;

                        RecordActivity(new ActivityEvent
                        {
                            Terminal = assignee,
                            Type = "task",
                            Action = "queued",
                            Content = $"Low priority task queued: {task.Title}",
                            RelatedId = taskId
                        });
                        break;

                    case "normal":
                    default:
                        // Queue behind current task (doesn't interrupt)
                        QueueTaskForTerminal(task, assignee, isLowPriority: false);
                        wasQueued = true;
                        queuedBehind = currentActiveTask.Title;

                        RecordActivity(new ActivityEvent
                        {
                            Terminal = assignee,
                            Type = "task",
                            Action = "queued",
                            Content = $"Task queued behind active work: {task.Title}",
                            RelatedId = taskId
                        });
                        break;
                }
            }
            else
            {
                // No active task - make this task active regardless of priority
                MakeTaskActive(task, assignee, taskId);
            }

            BroadcastTaskUpdate();

            // Raise TaskClaimed event for toast notification
            RaiseSafe(TaskClaimed, new TaskClaimedEventArgs
            {
                TaskId = taskId,
                TaskTitle = task.Title,
                ClaimedBy = assignee
            });

            // Analyze complexity and include suggestion if warranted
            ComplexitySuggestion complexitySuggestion = null;
            if (ComplexityDetector != null)
            {
                try
                {
                    var complexityResult = ComplexityDetector.Analyze(task.Title, task.Description);
                    if (complexityResult.SuggestPlan)
                    {
                        complexitySuggestion = new ComplexitySuggestion
                        {
                            Score = complexityResult.Score,
                            SuggestPlan = complexityResult.SuggestPlan,
                            Signals = complexityResult.Signals,
                            Recommendation = complexityResult.Recommendation
                        };
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Complexity analysis failed: {ex.Message}");
                }
            }

            return new ClaimTaskResult
            {
                Success = true,
                ComplexitySuggestion = complexitySuggestion,
                WasQueued = wasQueued,
                QueuedBehind = queuedBehind
            };
        }

        /// <summary>
        /// Check if a terminal is currently in a critical section.
        /// Critical sections have a bounded timeout (max 30 seconds).
        /// </summary>
        /// <summary>
        /// Returns true if the name represents a temporary native agent (e.g. "Agent Alice").
        /// These are short-lived Claude Code subagents that should not get persistent profiles.
        /// </summary>
        private static bool IsTemporaryAgent(string name)
        {
            return name.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsTerminalInCriticalSection(string terminalName)
        {
            var activity = _taskDb.GetTerminalActivity(terminalName);
            if (activity == null) return false;

            // Check if in critical section AND timeout hasn't expired
            if (!activity.InCriticalSection) return false;
            if (!activity.CriticalSectionTimeout.HasValue) return false;

            // Return true only if the timeout is still in the future
            return activity.CriticalSectionTimeout.Value > DateTime.UtcNow;
        }

        /// <summary>
        /// Queue a task for a terminal (add to paused stack without interrupting current work).
        /// </summary>
        private void QueueTaskForTerminal(KanbanTask task, string assignee, bool isLowPriority = false)
        {
            task.Assignee = assignee;
            task.Status = "in_progress";
            task.SubStatus = "queued";
            // Low priority tasks get a past timestamp to sort them to the bottom of the stack
            task.PausedAt = isLowPriority
                ? DateTime.UtcNow.AddDays(-1)
                : DateTime.UtcNow;

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save queued task: {ex.Message}"); }

            // Auto-generate summary when task is claimed (even if queued)
            if (SummaryService != null)
            {
                try
                {
                    SummaryService.AutoGenerateSummary(
                        task.Id,
                        task.Title,
                        "todo",
                        "in_progress",
                        assignee);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to auto-generate summary: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Pause a task with auto-generated summary.
        /// </summary>
        private void PauseTaskWithSummary(KanbanTask task, string assignee)
        {
            task.SubStatus = "paused";
            task.PausedAt = DateTime.UtcNow;

            // Persist paused task
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save paused task: {ex.Message}"); }

            // Auto-generate summary for the paused task
            if (SummaryService != null)
            {
                try
                {
                    SummaryService.AutoGenerateSummary(
                        task.Id,
                        task.Title,
                        "in_progress",
                        "paused",
                        assignee);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to auto-generate summary for paused task: {ex.Message}");
                }
            }

            // Record activity for pausing
            RecordActivity(new ActivityEvent
            {
                Terminal = assignee,
                Type = "task",
                Action = "paused",
                Content = $"Paused task: {task.Title}",
                RelatedId = task.Id
            });
        }

        /// <summary>
        /// Make a task active and update activity tracking.
        /// </summary>
        private void MakeTaskActive(KanbanTask task, string assignee, string taskId)
        {
            task.Assignee = assignee;
            task.Status = "in_progress";
            task.SubStatus = "active";

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save task: {ex.Message}"); }

            // Auto-generate summary when task is claimed
            if (SummaryService != null)
            {
                try
                {
                    SummaryService.AutoGenerateSummary(
                        taskId,
                        task.Title,
                        "todo",
                        "in_progress",
                        assignee);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to auto-generate summary: {ex.Message}");
                }
            }

            // Auto-update activity: terminal claimed a task
            ActivityService?.UpdateActivity(
                assignee,
                "working",
                $"Working on: {task.Title}",
                taskId: taskId);

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = assignee,
                Type = "task",
                Action = "claimed",
                Content = $"Claimed task: {task.Title}",
                RelatedId = taskId
            });
        }

        /// <summary>
        /// Get the most recently paused or queued task for an assignee.
        /// Tasks with SubStatus "paused" or "queued" are candidates for auto-resume.
        /// Ordered by PausedAt descending (most recent first), which means low-priority
        /// tasks (with older timestamps) are at the bottom of the stack.
        /// </summary>
        private KanbanTask GetMostRecentPausedTask(string assignee)
        {
            return _tasks.Values
                .Where(t => t.Assignee != null &&
                            t.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase) &&
                            (t.SubStatus == "paused" || t.SubStatus == "queued"))
                .OrderByDescending(t => t.PausedAt)
                .FirstOrDefault();
        }

        /// <summary>
        /// Update the status of a task.
        /// Implements stack behavior: when a task is marked "done" and was active,
        /// auto-resume the most recently paused task for that assignee.
        /// </summary>
        public UpdateTaskStatusResult UpdateTaskStatus(string taskId, string status)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot change status: task {taskId} is a quick-task (immutable; status is permanently 'done')." };

            var validStatuses = new[] { "todo", "in_progress", "done", "suggestion" };
            if (!validStatuses.Contains(status))
            {
                return new UpdateTaskStatusResult { Success = false, Error = $"Invalid status: {status}" };
            }

            var previousStatus = task.Status;
            var previousSubStatus = task.SubStatus;
            task.Status = status;
            var assignee = task.Assignee;

            // Manual override: if user explicitly sets status while AutoStatus is on,
            // disable auto-status so we respect their choice
            if (task.AutoStatus)
            {
                task.AutoStatus = false;
            }

            // Clear SubStatus when task is done
            if (status == "done")
            {
                task.SubStatus = null;
                task.PausedAt = null;
            }

            // Clear assignee and SubStatus if moving back to todo or suggestion
            if (status == "todo" || status == "suggestion")
            {
                task.Assignee = null;
                task.SubStatus = null;
                task.PausedAt = null;
            }

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save task: {ex.Message}"); }

            // Auto-generate summary on status change
            if (previousStatus != status && SummaryService != null)
            {
                try
                {
                    SummaryService.AutoGenerateSummary(
                        taskId,
                        task.Title,
                        previousStatus,
                        status,
                        assignee ?? "System");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to auto-generate summary: {ex.Message}");
                }
            }

            // Stack behavior: auto-resume most recently paused task when active task is marked done
            KanbanTask resumedTask = null;
            if (status == "done" && previousSubStatus == "active" && !string.IsNullOrEmpty(assignee))
            {
                resumedTask = GetMostRecentPausedTask(assignee);
                if (resumedTask != null)
                {
                    resumedTask.SubStatus = "active";
                    resumedTask.PausedAt = null;

                    // Persist resumed task
                    try { _taskDb.SaveTask(resumedTask); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save resumed task: {ex.Message}"); }

                    // Update activity to show the resumed task
                    ActivityService?.UpdateActivity(
                        assignee,
                        "working",
                        $"Working on: {resumedTask.Title}",
                        taskId: resumedTask.Id);

                    // Record activity for resuming
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = assignee,
                        Type = "task",
                        Action = "resumed",
                        Content = $"Resumed task: {resumedTask.Title}",
                        RelatedId = resumedTask.Id
                    });
                }
            }

            BroadcastTaskUpdate();

            // Auto-update activity when task is marked done
            if (status == "done" && !string.IsNullOrEmpty(assignee))
            {
                // Only set to idle if no task was auto-resumed
                if (resumedTask == null)
                {
                    ActivityService?.UpdateActivity(
                        assignee,
                        "idle",
                        $"Completed: {task.Title}",
                        taskId: null);  // Clear task association
                }

                // Record activity for the feed
                RecordActivity(new ActivityEvent
                {
                    Terminal = assignee,
                    Type = "task",
                    Action = "completed",
                    Content = $"Completed task: {task.Title}",
                    RelatedId = taskId
                });
            }
            else if (status != previousStatus)
            {
                // Record other status changes
                RecordActivity(new ActivityEvent
                {
                    Terminal = assignee ?? "System",
                    Type = "task",
                    Action = "updated",
                    Content = $"Task '{task.Title}' → {status}",
                    RelatedId = taskId
                });
            }

            // Phase 2 worktree commit-then-prune — gated by MULTITERMINAL_WORKTREE_MODE.
            // Auto-commit any changes in the worktree FIRST so the prune step
            // (which doesn't use --force) doesn't refuse on dirty state. If the
            // commit fails, skip prune so the dev can resolve manually; the DB
            // record stays 'active' for the next attempt.
            // Pre-declared (not inline-out) so it stays definitely-assigned for the
            // ineligible-completion else-if below: with the short-circuit && the
            // predicate isn't called when status != "done", so an inline out var
            // wouldn't be assigned on every path into the else.
            string doneSkipReason = null;
            if (status == "done"
                && TryResolveWorktreeEligibility(task, out string doneProjPath, out _, out doneSkipReason))
            {
                bool shouldPrune = false;
                MultiTerminal.Services.AutoCommitResult commitResult = null;
                try
                {
                    commitResult = _autoCommit.CommitForTaskAsync(
                        taskId,
                        doneProjPath,
                        task.Title,
                        task.ImplementationSummary,
                        task.Assignee).GetAwaiter().GetResult();

                    if (commitResult.Success)
                    {
                        shouldPrune = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Auto-commit for {taskId}: " +
                            (commitResult.SkippedReason ?? $"committed {commitResult.CommitHash}"));

                        if (!string.IsNullOrEmpty(commitResult.SkippedReason))
                        {
                            RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "auto_commit_skipped",
                                Content = $"Worktree for '{task.Title}' had no changes to commit ({commitResult.SkippedReason}).",
                                RelatedId = taskId
                            });
                        }
                        else
                        {
                            string shortHash = !string.IsNullOrEmpty(commitResult.CommitHash) && commitResult.CommitHash.Length >= 7
                                ? commitResult.CommitHash.Substring(0, 7)
                                : commitResult.CommitHash;
                            int fileCount = commitResult.ChangedFiles?.Count ?? 0;
                            string unlinkedNote = (commitResult.UnlinkedFiles != null && commitResult.UnlinkedFiles.Count > 0)
                                ? $" {commitResult.UnlinkedFiles.Count} of those weren't pre-linked via link_task_file: {string.Join(", ", commitResult.UnlinkedFiles.Take(5))}{(commitResult.UnlinkedFiles.Count > 5 ? ", ..." : "")}"
                                : "";
                            RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "auto_commit",
                                Content = $"Auto-committed {fileCount} file(s) as {shortHash} for '{task.Title}'.{unlinkedNote}",
                                RelatedId = taskId
                            });
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Auto-commit for {taskId} FAILED: {commitResult.Stderr}");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "auto_commit_failed",
                            Content = $"Auto-commit failed for '{task.Title}'. Worktree NOT pruned — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Auto-commit threw for task {taskId}: {ex.Message}");
                    // Activity feed is broadcast to HUD/board/MCP clients; raw
                    // exception text often leaks absolute paths, branch names,
                    // or git command details. Keep the full message in
                    // Debug.WriteLine above (server-side only) and surface a
                    // generic notice to clients.
                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "auto_commit_failed",
                        Content = $"Auto-commit threw for '{task.Title}'. Worktree NOT pruned — see debug log for details.",
                        RelatedId = taskId
                    });
                }

                // Phase 2 (helpers) + Phase 2.5 (integration) for per-agent isolation:
                // commit every helper worktree and merge each helper branch into the
                // canonical branch BEFORE pruning. A helper-commit failure or a merge
                // conflict halts teardown (shouldPrune=false) so nothing is lost.
                // No-op for single-agent tasks (the canonical commit already ran).
                List<string> integratedHelperBranches = new List<string>();
                if (shouldPrune)
                {
                    bool proceedTeardown;
                    try
                    {
                        proceedTeardown = CommitAndIntegrateHelpers(task, doneProjPath, out integratedHelperBranches);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] CommitAndIntegrateHelpers threw for {taskId}: {ex.Message}");
                        proceedTeardown = false;
                    }
                    if (!proceedTeardown)
                    {
                        shouldPrune = false;
                    }
                }

                bool prunedOK = false;
                // Hoisted out of the if(shouldPrune) block so the FireWorktreeReady
                // call (now after the if(prunedOK) block) can pass the same path
                // and inspect `deferred` for the fire decision.
                string worktreePathToPrune = null;
                bool deferred = false;
                if (shouldPrune)
                {
                    // Pre-prune broadcast (task db4b18c6): tell every live
                    // terminal which worktree path is about to be removed so
                    // any agent with cwd inside it can cd out before git tries
                    // the rmdir. Without this, the agent's open handle keeps
                    // the dir alive as an empty orphan on Windows.
                    //
                    // Cycle-2 fixes:
                    //   - Subscriber awaits its HTTP deliveries with a bounded
                    //     timeout (see MainForm.OnBrokerWorktreePruning), so
                    //     this call now synchronously blocks until the agents
                    //     have been *notified* (not just enqueued). The bare
                    //     Thread.Sleep(500) "gut budget" is gone.
                    //   - WorktreePruneCoordinator marks the path as pruning
                    //     so a concurrent SpawnTerminal can refuse to launch
                    //     into a soon-to-be-deleted worktree (closes the
                    //     TOCTOU window adversary flagged).
                    worktreePathToPrune = _worktrees?.GetWorktreePathForTask(taskId);

                    // Per-agent isolation: every agent worktree for the task is about
                    // to be pruned, so broadcast + mark EACH active path (not just the
                    // canonical one) so an agent cwd'd in a helper worktree can also cd
                    // out before git removes it.
                    var prunePaths = new List<string>();
                    try
                    {
                        foreach (var w in _taskDb.ListWorktreesForTask(taskId))
                        {
                            if (string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(w.WorktreePath)
                                && !prunePaths.Contains(w.WorktreePath))
                            {
                                prunePaths.Add(w.WorktreePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Enumerate prune paths for {taskId} failed: {ex.Message}");
                    }
                    // Always include the canonical path (covers single-agent + the
                    // enumerate-failed fallback).
                    if (!string.IsNullOrEmpty(worktreePathToPrune) && !prunePaths.Contains(worktreePathToPrune))
                    {
                        prunePaths.Add(worktreePathToPrune);
                    }

                    var markedPaths = new List<string>();
                    foreach (var prunePath in prunePaths)
                    {
                        WorktreePruneCoordinator.MarkPruning(prunePath);
                        markedPaths.Add(prunePath);
                        var fireArgs = FireWorktreePruning(taskId, prunePath, doneProjPath, task.Assignee);

                        // Cycle-3 adversary HIGH fix: if the broadcast didn't
                        // complete in time, DEFER prune — leave the worktree
                        // active so the janitor's deferred-prune pass retries
                        // once agents have likely moved on. Pruning while
                        // agents still hold cwd reduces to the partial-prune
                        // fallback + empty shell, which Pass 3 has to clean
                        // up anyway. Skipping it removes that window. If ANY
                        // path's broadcast didn't deliver, defer the whole prune.
                        if (!fireArgs.AllDelivered) deferred = true;
                    }
                    bool marked = markedPaths.Count > 0;

                    if (deferred)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MessageBroker] Prune deferred for task {taskId} (broadcast timeout); janitor will retry.");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "prune_deferred",
                            Content = $"Prune deferred for '{task.Title}' — broadcast did not complete in time; janitor will retry.",
                            RelatedId = taskId
                        });
                        // No prune attempt → don't auto-merge this pass either;
                        // janitor's retry will get there once the prune lands.
                    }
                    else
                    {
                        try
                        {
                            // Prune ALL agent worktrees for the task (canonical +
                            // helpers), not just the canonical one.
                            _worktrees.PruneAllForTaskAsync(taskId, doneProjPath).GetAwaiter().GetResult();
                            prunedOK = true;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MessageBroker] Worktree prune failed for task {taskId}: {ex.Message}");
                            RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "prune_failed",
                                Content = $"Worktree prune failed for '{task.Title}'. Auto-merge skipped — see debug log for details.",
                                RelatedId = taskId
                            });
                        }
                    }

                    // Cycle-4 debugger MED fix: when deferred=true the worktree
                    // is still active on disk + git — but we've decided to
                    // prune it later via the janitor. Keep the path marked so
                    // SpawnTerminal continues to refuse it during the defer
                    // window. The janitor's TryDeferredPruneRetryAsync handles
                    // unmark-on-success (or task-reopened cancellation).
                    // For the non-deferred path: a failed prune leaves the
                    // worktree in a known-valid state (no destructive partial
                    // state outside the partial-prune fallback which already
                    // marks the DB pruned), so unmarking is safe.
                    if (marked && !deferred)
                    {
                        foreach (var markedPath in markedPaths)
                        {
                            WorktreePruneCoordinator.UnmarkPruning(markedPath);
                        }
                    }
                }

                // Phase 3 auto-merge — fires only after a successful prune.
                // Prune releases the branch lock so git can merge the task
                // branch into the main checkout's current trunk. Merge failure
                // does NOT roll back commit or prune (those are durable); the
                // dev resolves the merge manually. WorktreeReady is fired
                // inside the helper after the merge attempt completes so HUD
                // panels rebind to the post-merge state without racing the
                // merge. Cycle-4 factored this into a helper so the janitor's
                // deferred-prune retry can run the same post-prune sequence.
                if (prunedOK)
                {
                    // Delete integrated helper branches now their worktrees are gone.
                    // Their commits live in the canonical branch but not yet trunk, so
                    // force-delete (-D); the canonical branch is deleted by Phase 3.
                    foreach (var helperBranch in integratedHelperBranches)
                    {
                        try
                        {
                            _merge.DeleteBranchAsync(doneProjPath, helperBranch, force: true).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Delete helper branch '{helperBranch}' failed for {taskId}: {ex.Message}");
                        }
                    }

                    PerformPostPruneMergeAndFireReady(taskId, task, doneProjPath, worktreePathToPrune);
                }
            }
            else if (status == "done"
                && MultiTerminal.Services.WorktreeConfig.IsEnabled)
            {
                // Worktree mode is ON but this task is ineligible (it resolved to no
                // worktree — no project association, ambiguous/unregistered project,
                // or a project with no filesystem path). The eligible path above runs
                // auto-commit → prune → merge; this path historically did NOTHING and
                // said nothing, so the user couldn't tell whether git automation had
                // silently failed or simply never applied. Make it explicit and
                // uniform (task 4bcd1e24, item [3]): one documented notice that the
                // task completed WITHOUT git automation and the user owns any commit.
                // (Mode-off completions are deliberately silent — there's no worktree
                // expectation to violate, so that's the global default, not a divergence.)
                RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "no_worktree_done",
                    Content = $"Task '{task.Title}' completed with no worktree ({doneSkipReason}) — no git automation ran (no auto-commit/prune/merge). Commit any changes on your current branch yourself.",
                    RelatedId = taskId
                });
            }

            // Generate changelog entry if task is marked as done and associated with a project
            if (status == "done" && !string.IsNullOrEmpty(task.ProjectId) && ChangelogService != null)
            {
                try
                {
                    // Get the project to find its path
                    if (_projects.TryGetValue(task.ProjectId, out var project) && !string.IsNullOrEmpty(project.Path))
                    {
                        ChangelogService.AddChangelogEntry(task, project.Path);
                        LogTrace($"[MessageBroker] Generated changelog entry for task {taskId} in project {project.Name}");
                    }
                    else
                    {
                        LogTrace($"[MessageBroker] Skipping changelog: Project {task.ProjectId} has no path set");
                    }
                }
                catch (Exception ex)
                {
                    LogTrace($"[MessageBroker] Failed to add changelog entry: {ex.Message}");
                }
            }

            return new UpdateTaskStatusResult { Success = true };
        }

        /// <summary>
        /// Move a task to a new position in the kanban. Handles both same-column
        /// reorder and cross-column move (status change) atomically: if newStatus
        /// differs from the current status, status updates first via the standard
        /// path (which fires resume-stack / activity / changelog side-effects),
        /// then sort_order is written. If sort_order is unchanged (newSortOrder
        /// equals current), the call is a no-op past the status update.
        ///
        /// Gap-collapse guard: when the chosen newSortOrder lands within
        /// MIN_SORT_GAP of either neighbor in the target column, the column is
        /// rebalanced into 1000-unit gaps and newSortOrder is recomputed via the
        /// post-rebalance neighbor midpoint. Prevents float collapse over many
        /// successive midpoint insertions (item 7).
        /// </summary>
        public UpdateTaskStatusResult ReorderTask(string taskId, string newStatus, double newSortOrder, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };

            if (task.IsQuickTask)
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot reorder: task {taskId} is a quick-task (immutable)." };

            // Reject NaN / ±Infinity. Either would poison the sort_order column —
            // SQL comparisons against NaN are undefined and Infinity defeats the
            // rebalance midpoint formula. Defense-in-depth: the WebView handler
            // also guards this, but a future API/MCP caller could bypass it.
            if (!double.IsFinite(newSortOrder))
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot reorder: newSortOrder must be a finite number (got {newSortOrder})." };

            // Status change first — UpdateTaskStatus handles validation, side-effects,
            // and persistence. If it fails, bail before touching sort_order.
            if (!string.IsNullOrEmpty(newStatus) && task.Status != newStatus)
            {
                var statusResult = UpdateTaskStatus(taskId, newStatus);
                if (!statusResult.Success)
                    return statusResult;
            }

            const double MIN_SORT_GAP = 1e-6;

            // Collapse guard: snapshot neighbors in the target column AFTER the
            // status change (the task may have just moved between columns).
            var siblings = _tasks.Values
                .Where(t => t.Status == task.Status && t.Id != taskId && t.SortOrder.HasValue)
                .OrderBy(t => t.SortOrder.Value)
                .ToList();

            double? prevOrder = null, nextOrder = null;
            foreach (var sib in siblings)
            {
                if (sib.SortOrder.Value < newSortOrder) prevOrder = sib.SortOrder.Value;
                else if (sib.SortOrder.Value > newSortOrder && nextOrder == null) nextOrder = sib.SortOrder.Value;
            }

            bool tooCloseBelow = prevOrder.HasValue && (newSortOrder - prevOrder.Value) < MIN_SORT_GAP;
            bool tooCloseAbove = nextOrder.HasValue && (nextOrder.Value - newSortOrder) < MIN_SORT_GAP;

            if (tooCloseBelow || tooCloseAbove)
            {
                // Rebalance the whole column. The current task is still in the
                // column at its previous sort_order; the rebalance will give it
                // a clean integer slot, then we compute a fresh midpoint from
                // the user-intended position. To preserve the visual rank the
                // user dragged to, set sort_order to the desired endpoint of
                // the gap before rebalance.
                task.SortOrder = newSortOrder;
                _taskDb.UpdateSortOrder(taskId, newSortOrder);
                _taskDb.RebalanceSortOrder(task.Status);

                // Reload affected tasks' sort_order from DB into in-memory map
                foreach (var t in _tasks.Values.Where(t => t.Status == task.Status).ToList())
                {
                    var refreshed = _taskDb.GetTask(t.Id);
                    if (refreshed != null) t.SortOrder = refreshed.SortOrder;
                }
            }
            else
            {
                task.SortOrder = newSortOrder;
                _taskDb.UpdateSortOrder(taskId, newSortOrder);
            }

            BroadcastTaskUpdate();
            return new UpdateTaskStatusResult { Success = true };
        }

        /// <summary>
        /// Update a task's title and description.
        /// </summary>
        public UpdateTaskResult UpdateTask(string taskId, string title, string description, string updatedBy = null, string plan = null, string implementationSummary = null, string testResults = null, string implementationChecklistJson = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot edit: task {taskId} is a quick-task (immutable except title). Use UpdateQuickTaskTitle for title changes." };

            if (string.IsNullOrWhiteSpace(title))
            {
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };
            }

            var previousTitle = task.Title;
            task.Title = title;
            task.Description = description;

            // Update documentation fields if provided
            if (plan != null) task.Plan = plan;
            if (implementationSummary != null) task.ImplementationSummary = implementationSummary;
            if (testResults != null) task.TestResults = testResults;
            if (implementationChecklistJson != null) task.ImplementationChecklistJson = implementationChecklistJson;

            // Persist to database (use SaveTask which includes all fields)
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task: {ex.Message}"); }

            BroadcastTaskUpdate();

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = previousTitle != title
                    ? $"Renamed task: '{previousTitle}' → '{title}'"
                    : $"Updated task: {title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Title-only rename for a task — the narrow-surface counterpart to
        /// <see cref="UpdateTask"/>'s multi-field edit. Works for both regular
        /// and quick-tasks (a quick-task's title is the one field its
        /// immutability contract allows to change). Used by the rename_task
        /// MCP tool / PATCH /api/tasks/{id}/title — neither needs the full
        /// edit_task surface, so this path skips description/plan/priority/
        /// project/status touches that <see cref="UpdateTask"/> would either
        /// require or clobber.
        /// </summary>
        public UpdateTaskResult RenameTask(string taskId, string newTitle, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (string.IsNullOrWhiteSpace(newTitle))
            {
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };
            }

            var previousTitle = task.Title;
            if (previousTitle == newTitle)
            {
                return new UpdateTaskResult { Success = true };
            }

            task.Title = newTitle;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to rename task: {ex.Message}"); }

            BroadcastTaskUpdate();

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = task.IsQuickTask
                    ? $"Renamed quick task: '{previousTitle}' → '{newTitle}'"
                    : $"Renamed task: '{previousTitle}' → '{newTitle}'",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's checklist JSON.
        /// </summary>
        public UpdateTaskResult UpdateTaskChecklist(string taskId, string checklistJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            lock (_checklistMutationLock)
            {
                task.ChecklistJson = checklistJson ?? "[]";

                // Auto-derive parent task status from checklist item positions
                RecalculateAutoStatus(task);

                // Persist to database
                try { _taskDb.SaveTask(task); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task checklist: {ex.Message}"); }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Append items to a task's existing checklist without replacing it.
        /// Unlike <see cref="UpdateTaskChecklist"/> (a full replace), the caller does not have to
        /// round-trip and faithfully rebuild the whole list (which risks dropping an existing item's
        /// status/notes when re-serializing a stale snapshot). This is server-authoritative: each
        /// appended item is rebuilt from a whitelist (a non-empty "item" description plus a validated
        /// initial status); caller-supplied Done/Notes/AssignedTo/CycleCount/SortOrder are ignored so
        /// append can't forge audit history or bypass the transition state machine. The
        /// read-modify-write is serialized under <see cref="_checklistMutationLock"/> (shared with the
        /// other checklist mutators) so it won't clobber a concurrent append/transition/assign, and it
        /// fails closed if persistence throws.
        /// </summary>
        public UpdateTaskResult AppendChecklistItems(string taskId, string itemsJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot append checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            if (string.IsNullOrWhiteSpace(itemsJson))
                return new UpdateTaskResult { Success = false, Error = "No items to append (itemsJson was empty)." };

            List<ChecklistItem> rawItems;
            try
            {
                rawItems = System.Text.Json.JsonSerializer.Deserialize<List<ChecklistItem>>(itemsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new UpdateTaskResult { Success = false, Error = $"Invalid itemsJson: {ex.Message}" };
            }

            if (rawItems == null || rawItems.Count == 0)
                return new UpdateTaskResult { Success = false, Error = "No items to append (itemsJson contained no items)." };

            // Server-side whitelist: rebuild each item from only the fields append is allowed to set.
            // A valid status is honored (defaults to "pending"); an invalid status is rejected up front
            // rather than silently corrupting the lifecycle board via RecalculateAutoStatus.
            var sanitized = new List<ChecklistItem>(rawItems.Count);
            foreach (var raw in rawItems)
            {
                if (raw == null || string.IsNullOrWhiteSpace(raw.Item))
                    return new UpdateTaskResult { Success = false, Error = "Each appended item must have a non-empty 'item' description." };

                var status = string.IsNullOrWhiteSpace(raw.Status) ? "pending" : raw.Status.Trim().ToLowerInvariant();
                if (System.Array.IndexOf(ChecklistItem.ValidStatuses, status) < 0)
                    return new UpdateTaskResult { Success = false, Error = $"Invalid status '{raw.Status}' on appended item '{raw.Item}'. Valid statuses: {string.Join(", ", ChecklistItem.ValidStatuses)}." };

                sanitized.Add(new ChecklistItem
                {
                    Item = raw.Item,
                    Status = status,
                    Done = status == "done",
                    Notes = new List<ChecklistItemNote>(),
                    AssignedTo = null,
                    CycleCount = 0
                });
            }

            // Serialize the read-modify-write-save against the other checklist mutators, and fail
            // closed: if persistence throws, revert the in-memory mutation so we don't broadcast or
            // report success for a change that never hit the database.
            lock (_checklistMutationLock)
            {
                // Snapshot every field RecalculateAutoStatus can touch (Status/SubStatus/PausedAt)
                // plus the checklist, so the fail-closed revert fully restores the in-memory task.
                var originalJson = task.ChecklistJson;
                var originalStatus = task.Status;
                var originalSubStatus = task.SubStatus;
                var originalPausedAt = task.PausedAt;

                var checklist = task.GetChecklist();
                checklist.AddRange(sanitized);
                task.SetChecklist(checklist);

                // Auto-derive parent task status from checklist item positions
                RecalculateAutoStatus(task);

                try
                {
                    _taskDb.SaveTask(task);
                }
                catch (Exception ex)
                {
                    task.ChecklistJson = originalJson;
                    task.Status = originalStatus;
                    task.SubStatus = originalSubStatus;
                    task.PausedAt = originalPausedAt;
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to append task checklist items: {ex.Message}");
                    return new UpdateTaskResult { Success = false, Error = $"Failed to persist appended checklist items: {ex.Message}" };
                }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's implementation checklist JSON.
        /// </summary>
        public UpdateTaskResult UpdateTaskImplementationChecklist(string taskId, string implementationChecklistJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            task.ImplementationChecklistJson = implementationChecklistJson ?? "[]";

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task implementation checklist: {ex.Message}"); }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Auto-derive parent task status from checklist item positions when AutoStatus is enabled.
        /// Rules:
        /// - No checklist items → do nothing (fall through to manual status)
        /// - All items in "pending" → "todo"
        /// - Any items in "coding" or "testing" → "in_progress"
        /// - All items in "done" → "done"
        /// </summary>
        private void RecalculateAutoStatus(KanbanTask task)
        {
            if (!task.AutoStatus) return;

            var checklist = task.GetChecklist();
            if (checklist.Count == 0) return; // Zero items → keep manual status

            bool allPending = checklist.All(c => c.Status == "pending");
            bool allDone = checklist.All(c => c.Status == "done");
            bool anyActive = checklist.Any(c => c.Status == "coding" || c.Status == "testing");

            string newStatus;
            if (allDone)
                newStatus = "done";
            else if (anyActive)
                newStatus = "in_progress";
            else if (allPending)
                newStatus = "todo";
            else
                newStatus = "in_progress"; // Mix of pending + done = still in progress

            if (task.Status != newStatus)
            {
                task.Status = newStatus;
                if (newStatus == "done")
                {
                    task.SubStatus = null;
                    task.PausedAt = null;
                }
                else if (newStatus == "in_progress" && task.SubStatus == null)
                {
                    task.SubStatus = "active";
                }
            }
        }

        /// <summary>
        /// Toggle auto-status for a task. When enabled, status is derived from checklist.
        /// When disabled, user controls status directly.
        /// </summary>
        public UpdateTaskResult SetAutoStatus(string taskId, bool enabled)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            task.AutoStatus = enabled;

            if (enabled)
            {
                RecalculateAutoStatus(task);
            }

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update auto-status: {ex.Message}"); }

            BroadcastTaskUpdate();
            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's priority.
        /// </summary>
        public UpdateTaskResult UpdateTaskPriority(string taskId, string priority)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            var validPriorities = new[] { "urgent", "normal", "low" };
            if (!validPriorities.Contains(priority))
            {
                return new UpdateTaskResult { Success = false, Error = $"Invalid priority: {priority}" };
            }

            task.Priority = priority;

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task priority: {ex.Message}"); }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's project assignment.
        /// </summary>
        public UpdateTaskResult UpdateTaskProject(string taskId, string projectId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Distinguish "user cleared the project" (empty input) from "we
            // refused to bind because the input was ambiguous" (non-empty input
            // that NormalizeProjectId couldn't resolve). Without this branch an
            // ambiguous short id would silently overwrite a previously valid
            // assignment with null and the call would still report Success.
            string originalProjectId = task.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                task.ProjectId = null;
            }
            else
            {
                string canonical = NormalizeProjectId(projectId);
                if (canonical == null)
                {
                    return new UpdateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
                task.ProjectId = canonical;
            }

            // Persist to database. If the durable write fails, revert the
            // in-memory mutation so the cache stays consistent with the row,
            // skip the broadcast (no one should observe a state we couldn't
            // commit), and return Success=false so the caller-side toast
            // plumbing fires — otherwise the UI would display "saved" while
            // the row stays stale.
            try
            {
                _taskDb.SaveTask(task);
            }
            catch (Exception ex)
            {
                task.ProjectId = originalProjectId;
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task project: {ex.Message}");
                return new UpdateTaskResult
                {
                    Success = false,
                    Error = $"Failed to persist project change: {ex.Message}"
                };
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's assignee (without complex stack logic - simple assignment change).
        /// </summary>
        public UpdateTaskResult UpdateTaskAssignee(string taskId, string assignee)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Normalize empty string to null
            task.Assignee = string.IsNullOrEmpty(assignee) ? null : assignee;

            // Persist to database
            try { _taskDb.UpdateTaskAssignee(taskId, task.Assignee); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task assignee: {ex.Message}"); }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        // =============================================
        // Kanban Workflow: Enhanced Checklist Operations
        // =============================================

        /// <summary>
        /// Transition a checklist item to a new status with mandatory notes.
        /// Enforces the state machine: pending→coding→testing→done (with cycling).
        /// Agent can: pending→coding, coding→testing.
        /// PM/tester can: testing→coding, testing→done.
        /// </summary>
        public UpdateChecklistItemResult TransitionChecklistItem(string taskId, int itemIndex, string newStatus, string notes, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateChecklistItemResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateChecklistItemResult { Success = false, Error = $"Cannot transition checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            List<ChecklistItem> checklist;
            ChecklistItem item;
            string previousStatus;

            // Serialize the read-modify-write against the other checklist mutators (append, full-replace,
            // assign) so a concurrent write can't clobber this transition's status/notes/cycle update.
            lock (_checklistMutationLock)
            {
                checklist = task.GetChecklist();
                if (itemIndex < 0 || itemIndex >= checklist.Count)
                {
                    return new UpdateChecklistItemResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
                }

                item = checklist[itemIndex];
                previousStatus = item.Status ?? "pending";

                // Validate the transition
                var validTransitions = new Dictionary<string, string[]>
                {
                    { "pending", new[] { "coding" } },
                    { "coding", new[] { "testing" } },
                    { "testing", new[] { "coding", "done" } }
                };

                if (!validTransitions.ContainsKey(previousStatus) || !validTransitions[previousStatus].Contains(newStatus))
                {
                    return new UpdateChecklistItemResult
                    {
                        Success = false,
                        Error = $"Invalid transition: {previousStatus} → {newStatus}. Valid transitions from '{previousStatus}': {string.Join(", ", validTransitions.ContainsKey(previousStatus) ? validTransitions[previousStatus] : new[] { "none" })}"
                    };
                }

                // Notes are mandatory for coding→testing, testing→coding, testing→done
                if ((previousStatus == "coding" || previousStatus == "testing") && string.IsNullOrWhiteSpace(notes))
                {
                    return new UpdateChecklistItemResult
                    {
                        Success = false,
                        Error = $"Notes are required for {previousStatus} → {newStatus} transition. Describe what was done or what needs fixing."
                    };
                }

                // Update the item
                item.Status = newStatus;
                item.Done = newStatus == "done";

                // Track cycles (testing→coding = another cycle)
                if (previousStatus == "testing" && newStatus == "coding")
                {
                    item.CycleCount++;
                }

                // Add the transition note
                if (item.Notes == null) item.Notes = new List<ChecklistItemNote>();
                item.Notes.Add(new ChecklistItemNote
                {
                    By = updatedBy,
                    At = DateTime.UtcNow.ToString("o"),
                    Transition = $"{previousStatus} → {newStatus}",
                    Text = notes ?? ""
                });

                // Save
                task.SetChecklist(checklist);

                // Auto-derive parent task status from checklist item positions
                RecalculateAutoStatus(task);

                try { _taskDb.SaveTask(task); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save checklist transition: {ex.Message}"); }
            }

            BroadcastTaskUpdate();

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? "System",
                Type = "task",
                Action = "checklist_transition",
                Content = $"Checklist item '{item.Item}': {previousStatus} → {newStatus}",
                RelatedId = taskId
            });

            bool escalation = item.CycleCount >= 4;

            // Auto-generate inbox notifications
            try
            {
                // coding → testing: Notify PM that item is ready for testing
                if (previousStatus == "coding" && newStatus == "testing")
                {
                    CreateInboxNotification(
                        DefaultInboxRecipient,
                        taskId,
                        task.Title,
                        itemIndex,
                        item.Item,
                        "ready_for_testing",
                        $"{updatedBy} finished '{item.Item}' - ready for your testing. {notes}",
                        updatedBy);
                }

                // Escalation: 4+ coding↔testing cycles
                if (escalation && previousStatus == "testing" && newStatus == "coding")
                {
                    CreateInboxNotification(
                        DefaultInboxRecipient,
                        taskId,
                        task.Title,
                        itemIndex,
                        item.Item,
                        "escalation",
                        $"Item '{item.Item}' has cycled {item.CycleCount} times between coding and testing - may need discussion.",
                        updatedBy);
                }

                // Task complete: Check if ALL items are now done
                if (newStatus == "done")
                {
                    var allDone = checklist.All(i => i.Status == "done");
                    if (allDone)
                    {
                        CreateInboxNotification(
                            DefaultInboxRecipient,
                            taskId,
                            task.Title,
                            null,
                            null,
                            "task_complete",
                            $"All {checklist.Count} items on '{task.Title}' are done - task ready for final sign-off.",
                            updatedBy);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to create inbox notification: {ex.Message}");
            }

            return new UpdateChecklistItemResult
            {
                Success = true,
                ItemName = item.Item,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                CycleCount = item.CycleCount,
                EscalationTriggered = escalation
            };
        }

        /// <summary>
        /// Assign a checklist item to a specific agent (or unassign by passing null).
        /// </summary>
        public UpdateTaskResult AssignChecklistItem(string taskId, int itemIndex, string assignee)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            lock (_checklistMutationLock)
            {
                var checklist = task.GetChecklist();
                if (itemIndex < 0 || itemIndex >= checklist.Count)
                {
                    return new UpdateTaskResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
                }

                checklist[itemIndex].AssignedTo = assignee;
                task.SetChecklist(checklist);

                try { _taskDb.SaveTask(task); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save checklist assignment: {ex.Message}"); }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's continuation notes for session handoff.
        /// </summary>
        public UpdateContinuationResult UpdateTaskContinuation(string taskId, string continuationNotes, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateContinuationResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            task.ContinuationNotes = continuationNotes;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update continuation notes: {ex.Message}"); }

            BroadcastTaskUpdate();

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "continuation_updated",
                Content = $"Updated continuation notes for: {task.Title}",
                RelatedId = taskId
            });

            return new UpdateContinuationResult { Success = true };
        }

        /// <summary>
        /// Update a task's plan field.
        /// </summary>
        public UpdateTaskResult UpdateTaskPlan(string taskId, string plan, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set plan: task {taskId} is a quick-task (immutable; quick-tasks have no plan)." };

            task.Plan = plan;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task plan: {ex.Message}"); }

            BroadcastTaskUpdate();

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "plan_updated",
                Content = $"Updated plan for: {task.Title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Update a task's implementation summary and/or test results.
        /// </summary>
        public UpdateTaskResult UpdateTaskSummaryFields(string taskId, string implementationSummary, string testResults, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set summary/test results: task {taskId} is a quick-task (immutable)." };

            if (implementationSummary != null) task.ImplementationSummary = implementationSummary;
            if (testResults != null) task.TestResults = testResults;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task summary: {ex.Message}"); }

            BroadcastTaskUpdate();

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "summary_updated",
                Content = $"Updated summary/results for: {task.Title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }

        /// <summary>
        /// Set a task as active, auto-pausing all other active tasks for the same assignee.
        /// Enforces the "only one active task" rule.
        /// </summary>
        public SetTaskActiveResult SetTaskActive(string taskId, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new SetTaskActiveResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.Status != "in_progress")
            {
                return new SetTaskActiveResult { Success = false, Error = $"Task must be in_progress to set active. Current status: {task.Status}" };
            }

            var assignee = task.Assignee ?? updatedBy;

            // Per-agent worktree isolation (task bab81a92): the agent performing the
            // activation owns its own worktree. The assignee holds the canonical
            // worktree (task/<id>); any other activator is a helper on a
            // task/<id>--<slug> branch. Auto-pause + activity tracking below stay
            // keyed on the task assignee (unchanged single-active-task model).
            string actingAgent = updatedBy ?? assignee;
            bool isActingAssignee = string.IsNullOrEmpty(task.Assignee)
                || string.Equals(actingAgent, task.Assignee, StringComparison.OrdinalIgnoreCase);

            var pausedIds = new List<string>();
            var pausedTitles = new List<string>();

            // Capture the previously-active task's id + worktree (for the
            // TaskActiveChanged event fired below). One assignee can have at
            // most one active task, so the first match in the loop wins; if
            // none, both stay null and AC4's "no-active-task baseline" applies.
            string oldTaskId = null;
            string oldWorktreePath = null;

            // Auto-pause other active tasks for this assignee
            foreach (var kvp in _tasks)
            {
                var other = kvp.Value;
                if (other.Id != taskId &&
                    other.Status == "in_progress" &&
                    other.SubStatus == "active" &&
                    string.Equals(other.Assignee, assignee, StringComparison.OrdinalIgnoreCase))
                {
                    if (oldTaskId == null)
                    {
                        oldTaskId = other.Id;
                        oldWorktreePath = _worktrees?.GetWorktreePathForTask(other.Id, actingAgent)
                                          ?? _worktrees?.GetWorktreePathForTask(other.Id);
                    }

                    other.SubStatus = "paused";
                    other.PausedAt = DateTime.UtcNow;
                    pausedIds.Add(other.Id);
                    pausedTitles.Add(other.Title);

                    try { _taskDb.SaveTask(other); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to pause task {other.Id}: {ex.Message}"); }
                }
            }

            // Set this task as active
            task.SubStatus = "active";
            task.PausedAt = null;

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to set task active: {ex.Message}"); }

            // Auto-helper: an agent that activates a task it doesn't own (not the
            // assignee, not already a helper) is registered as a helper so the
            // helper list stays consistent with the per-agent worktree it's about
            // to receive. Emits the standard helper-added notification.
            if (!isActingAssignee
                && !string.IsNullOrEmpty(actingAgent)
                && !task.Helpers.Contains(actingAgent, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    AddHelper(taskId, actingAgent, actingAgent).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Auto-add helper '{actingAgent}' on task {taskId} failed: {ex.Message}");
                }
            }

            // Phase 1 worktree isolation — gated by MULTITERMINAL_WORKTREE_MODE.
            // Materialize a fresh worktree on `git worktree add` so subsequent agent
            // spawns can be rooted there. Idempotent: returns the existing record
            // when one already exists. Failure is non-fatal — the task still goes
            // active so the user is not blocked by git issues.
            //
            // Eligibility (mode-on + resolvable project path) is decided by the
            // shared TryResolveWorktreeEligibility predicate so activation and the
            // done-gate can never disagree (task 4bcd1e24). The predicate also
            // hands back the canonical project id so we can opportunistically
            // self-heal a legacy truncated 8-char ProjectId on the row — future
            // lookups then succeed without repeating the prefix scan.
            if (TryResolveWorktreeEligibility(task, out string activeProjPath, out string canonicalProjectId, out string worktreeSkipReason))
            {
                bool canCreateWorktree = true;

                if (!string.Equals(canonicalProjectId, task.ProjectId, StringComparison.Ordinal))
                {
                    string originalProjectId = task.ProjectId;
                    task.ProjectId = canonicalProjectId;
                    try
                    {
                        _taskDb.SaveTask(task);
                    }
                    catch (Exception ex)
                    {
                        // Persistence failed — revert in-memory mutation so the
                        // durable record stays consistent with the in-memory cache,
                        // and skip worktree creation under a binding the task row
                        // doesn't actually claim.
                        task.ProjectId = originalProjectId;
                        canCreateWorktree = false;
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to persist normalized ProjectId for task {taskId}: {ex.Message}");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "create_skipped",
                            Content = $"Worktree creation skipped for '{task.Title}': could not persist canonical project id — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }

                if (canCreateWorktree)
                {
                    try
                    {
                        // Serialize against task-done helper integration so a helper
                        // worktree can't be created while the canonical branch is mid-merge.
                        lock (TaskWorktreeLock(taskId))
                        {
                            // Re-check under the lock: if the task went done (teardown
                            // started) since we entered SetTaskActive, don't create a
                            // worktree the teardown would never see / prune.
                            if (string.Equals(task.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                            {
                                _worktrees.CreateForTaskAsync(taskId, actingAgent, isActingAssignee, activeProjPath).GetAwaiter().GetResult();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MessageBroker] Worktree create failed for task {taskId}: {ex.Message}");
                        RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "create_failed",
                            Content = $"Worktree creation failed for '{task.Title}' — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }
            }
            else if (MultiTerminal.Services.WorktreeConfig.IsEnabled
                && !string.IsNullOrEmpty(task.ProjectId))
            {
                // Worktree mode is on AND the task claims a project, but the
                // project couldn't be resolved. The predicate already classified
                // the cause (ambiguous input, missing registration, or
                // registered-without-path) — surface it so the user can act on
                // the right one without reading debug logs.
                RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "create_skipped",
                    Content = $"Worktree creation skipped for '{task.Title}': project '{task.ProjectId}' could not be resolved ({worktreeSkipReason}).",
                    RelatedId = taskId
                });
            }

            BroadcastTaskUpdate();

            // Update ActivityService so the Task HUD can find the active task for this terminal
            ActivityService?.UpdateActivity(assignee, "working", $"Task: {task.Title}", taskId);

            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "set_active",
                Content = pausedIds.Count > 0
                    ? $"Set '{task.Title}' active. Auto-paused {pausedIds.Count} task(s): {string.Join(", ", pausedTitles)}"
                    : $"Set '{task.Title}' active.",
                RelatedId = taskId
            });

            // AC2: notify subscribers (MainForm pushes the event over the
            // agent's Claude Code Channel so the agent-side auto-cd hook can
            // react). Resolve newWorktreePath fresh from the worktree manager
            // so we reflect whatever the create attempt above produced (null
            // when worktree mode is off, project unresolvable, or git failed).
            // Resolve the ACTING agent's own worktree (canonical for the assignee,
            // helper for a helper) so the event — and the agent-side auto-cd hook —
            // target the worktree that agent will actually work in.
            string newWorktreePath = _worktrees?.GetWorktreePathForTask(taskId, actingAgent)
                                     ?? _worktrees?.GetWorktreePathForTask(taskId);
            try
            {
                RaiseSafe(TaskActiveChanged, new TaskActiveChangedEventArgs(
                    agentName: actingAgent,
                    oldTaskId: oldTaskId,
                    oldWorktreePath: oldWorktreePath,
                    newTaskId: taskId,
                    newWorktreePath: newWorktreePath));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] TaskActiveChanged subscribers threw: {ex.Message}");
            }

            return new SetTaskActiveResult
            {
                Success = true,
                PausedTaskIds = pausedIds,
                PausedTaskTitles = pausedTitles
            };
        }

        /// <summary>
        /// Delete a task from the Kanban board.
        /// </summary>
        public DeleteTaskResult DeleteTask(string taskId, string deletedBy = null)
        {
            if (!_tasks.TryRemove(taskId, out var task))
            {
                return new DeleteTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Delete from database
            try { _taskDb.DeleteTask(taskId); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to delete task: {ex.Message}"); }

            // Clean up any attachments for this task
            CleanupTaskAttachments(taskId);

            BroadcastTaskUpdate();

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = deletedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "deleted",
                Content = $"Deleted task: {task.Title}",
                RelatedId = taskId
            });

            return new DeleteTaskResult { Success = true };
        }

        // =============================================
        // Task Relationships
        // =============================================

        public AddRelationshipResult AddRelationship(string sourceTaskId, string targetTaskId, string type, string createdBy)
        {
            // Validate type
            var validTypes = new[] { "blocks", "depends_on", "related_to" };
            if (!validTypes.Contains(type))
                return new AddRelationshipResult { Success = false, Error = $"Invalid relationship type: {type}. Must be: blocks, depends_on, related_to" };

            // Validate both tasks exist
            if (!_tasks.ContainsKey(sourceTaskId))
                return new AddRelationshipResult { Success = false, Error = $"Source task not found: {sourceTaskId}" };
            if (!_tasks.ContainsKey(targetTaskId))
                return new AddRelationshipResult { Success = false, Error = $"Target task not found: {targetTaskId}" };

            // Prevent self-reference
            if (sourceTaskId == targetTaskId)
                return new AddRelationshipResult { Success = false, Error = "Cannot create relationship to self" };

            try
            {
                // Add the forward relationship
                var forwardId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddRelationship(forwardId, sourceTaskId, targetTaskId, type, createdBy);

                // Auto-create inverse
                var inverseType = type switch
                {
                    "blocks" => "depends_on",
                    "depends_on" => "blocks",
                    "related_to" => "related_to",
                    _ => type
                };
                var inverseId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddRelationship(inverseId, targetTaskId, sourceTaskId, inverseType, createdBy);

                return new AddRelationshipResult { Success = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] AddRelationship failed: {ex.Message}");
                return new AddRelationshipResult { Success = false, Error = ex.Message };
            }
        }

        public RemoveRelationshipResult RemoveRelationship(string sourceTaskId, string targetTaskId)
        {
            if (!_tasks.ContainsKey(sourceTaskId))
                return new RemoveRelationshipResult { Success = false, Error = $"Source task not found: {sourceTaskId}" };

            try
            {
                _taskDb.RemoveRelationshipsBetween(sourceTaskId, targetTaskId);
                return new RemoveRelationshipResult { Success = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] RemoveRelationship failed: {ex.Message}");
                return new RemoveRelationshipResult { Success = false, Error = ex.Message };
            }
        }

        public GetRelationshipsResult GetRelationships(string taskId)
        {
            if (!_tasks.ContainsKey(taskId))
                return new GetRelationshipsResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                // Get relationships where this task is the source (our perspective)
                var allRels = _taskDb.GetRelationshipsForTask(taskId);
                var ourRels = allRels.Where(r => r.SourceTaskId == taskId).ToList();
                return new GetRelationshipsResult { Success = true, Relationships = ourRels };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetRelationships failed: {ex.Message}");
                return new GetRelationshipsResult { Success = false, Error = ex.Message };
            }
        }

        // =============================================
        // Task File Links
        // =============================================

        public LinkFileResult LinkFile(string taskId, string filePath, string description, int? lineStart, int? lineEnd, string addedBy, int? checklistItemIndex = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new LinkFileResult { Success = false, Error = $"Task not found: {taskId}" };

            if (string.IsNullOrWhiteSpace(filePath))
                return new LinkFileResult { Success = false, Error = "File path is required" };

            // checklistItemIndex must be a real 0-based index into the live checklist.
            // Negative values or out-of-range positives produce phantom rows that survive
            // forever and silently break per-item routing (Run 2 debugger HIGH / LOW).
            // Note: this gate is best-effort — a concurrent checklist edit between this
            // read and the link insert could still produce a phantom row. ComputeItemsToBounce
            // (TasksPanelControl) filters phantom indexes at routing time as the actual
            // tolerance layer (adversary Run 3 MEDIUM acknowledged).
            if (checklistItemIndex.HasValue)
            {
                if (checklistItemIndex.Value < 0)
                {
                    return new LinkFileResult { Success = false, Error = $"checklistItemIndex must be >= 0 (got {checklistItemIndex.Value}); omit for task-scoped links" };
                }
                int itemCount = 0;
                try
                {
                    if (!string.IsNullOrEmpty(task.ChecklistJson))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(task.ChecklistJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            itemCount = doc.RootElement.GetArrayLength();
                        }
                    }
                }
                catch
                {
                    // If we can't parse the checklist, fall through and let the row be
                    // accepted — better than blocking a link on transient parse error.
                    itemCount = -1;
                }
                if (itemCount == 0)
                {
                    return new LinkFileResult { Success = false, Error = "task has no checklist items; omit checklistItemIndex for task-scoped links" };
                }
                if (itemCount > 0 && checklistItemIndex.Value >= itemCount)
                {
                    return new LinkFileResult { Success = false, Error = $"checklistItemIndex {checklistItemIndex.Value} out of range; task has {itemCount} item(s)" };
                }
            }

            try
            {
                var id = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddFileLink(id, taskId, filePath, description, lineStart, lineEnd, addedBy, checklistItemIndex);
                var files = _taskDb.GetFileLinksForTask(taskId);
                return new LinkFileResult { Success = true, FileCount = files.Count };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] LinkFile failed: {ex.Message}");
                return new LinkFileResult { Success = false, Error = ex.Message };
            }
        }

        public UnlinkFileResult UnlinkFile(string taskId, string filePath)
        {
            if (!_tasks.ContainsKey(taskId))
                return new UnlinkFileResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                _taskDb.RemoveFileLink(taskId, filePath);
                return new UnlinkFileResult { Success = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] UnlinkFile failed: {ex.Message}");
                return new UnlinkFileResult { Success = false, Error = ex.Message };
            }
        }

        public GetTaskFilesResult GetTaskFiles(string taskId)
        {
            if (!_tasks.ContainsKey(taskId))
                return new GetTaskFilesResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                var files = _taskDb.GetFileLinksForTask(taskId);
                return new GetTaskFilesResult { Success = true, Files = files };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetTaskFiles failed: {ex.Message}");
                return new GetTaskFilesResult { Success = false, Error = ex.Message };
            }
        }

        // Returns:
        //   non-null set of item indexes when item-scoped links exist for the file.
        //   null when ANY task-scoped link (checklist_item_index IS NULL) exists for
        //     the file — caller treats as "applies to all items" (backward compat).
        //   empty set when no link exists at all — caller decides fallback.
        // On exception: returns null (= same fallback path as task-scoped). Returning
        // empty would have masked a transient DB hiccup as "no link" and silently
        // dropped that comment from routing while other comments still computed a
        // routed set; null instead routes to bulk-bounce so notes never go missing
        // because of an intermittent SQLite error (Run 2 code-review/debugger LOW).
        public HashSet<int> GetItemsLinkedToFile(string taskId, string filePath)
        {
            try
            {
                return _taskDb.GetItemsLinkedToFile(taskId, filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetItemsLinkedToFile failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all tasks.
        /// </summary>
        public List<KanbanTask> GetTasks()
        {
            return _tasks.Values.OrderBy(t => t.CreatedAt).ToList();
        }

        /// <summary>
        /// Get all tasks filtered by project ID.
        /// </summary>
        /// <param name="projectId">Project ID to filter by, or null for all tasks.</param>
        public List<KanbanTask> GetTasks(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
                return GetTasks();

            return _tasks.Values
                .Where(t => t.ProjectId == projectId)
                .OrderBy(t => t.CreatedAt)
                .ToList();
        }

        /// <summary>
        /// Get the active in-progress task for a specific agent.
        /// Checks in-memory cache first, falls back to database.
        /// </summary>
        public KanbanTask GetMyActiveTask(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return null;

            // Check in-memory cache first
            var activeTask = _tasks.Values
                .FirstOrDefault(t =>
                    t.Assignee != null &&
                    t.Assignee.Equals(agentName, StringComparison.OrdinalIgnoreCase) &&
                    t.Status == "in_progress" &&
                    t.SubStatus == "active");

            return activeTask ?? _taskDb.GetActiveTaskForAgent(agentName);
        }

        /// <summary>
        /// Resolve the agent's active task for WORKTREE/CWD purposes. Distinct
        /// from <see cref="GetMyActiveTask"/>, which is strict (assignee +
        /// <c>sub_status='active'</c>) and must stay that way so kanban callers
        /// (session-start, get_my_active_task) never see a paused task.
        ///
        /// <para>A HELPER is never the task assignee, so the strict lookup always
        /// misses for them — yet a helper still owns a per-agent worktree it must
        /// be routed into (get_active_worktree, and the spawn-cwd resolvers
        /// <see cref="ResolveTaskWorktreePath"/> / <see cref="ResolveTaskRepoRoot"/>).
        /// The helper's own active <c>task_worktrees</c> row is the per-agent
        /// active pointer; resolve the task from it. Falls back to the strict
        /// result for the assignee (unchanged), so assignee resolution is
        /// byte-identical. Most-recent active worktree row wins when an agent
        /// helps on several tasks (task bab81a92, fixes acceptance scenario 2b).</para>
        /// </summary>
        public KanbanTask ResolveActiveTaskForAgent(string agentName)
        {
            if (string.IsNullOrEmpty(agentName)) return null;

            // Assignee path — strict, unchanged.
            var strict = GetMyActiveTask(agentName);
            if (strict != null) return strict;

            // Helper path — resolve via the agent's own active worktree row.
            try
            {
                var wt = _taskDb?.GetActiveWorktreeForAgent(agentName);
                if (wt != null && !string.IsNullOrEmpty(wt.TaskId))
                {
                    return _tasks.TryGetValue(wt.TaskId, out var helperTask)
                        ? helperTask
                        : _taskDb.GetTask(wt.TaskId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ResolveActiveTaskForAgent] '{agentName}' threw: {ex.Message} — no helper worktree resolved");
            }

            return null;
        }

        /// <summary>
        /// Broadcast task update to all listeners.
        /// </summary>
        private void BroadcastTaskUpdate()
        {
            var tasks = GetTasks();
            RaiseSafe(TasksUpdated, tasks);
        }

        /// <summary>
        /// Add a helper to a task and send Tier 1 notification.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="helperName">Name of the helper terminal to add.</param>
        /// <param name="addedBy">Name of the terminal adding the helper (usually the assignee).</param>
        /// <returns>Result indicating success or failure.</returns>
        public async Task<AddHelperResult> AddHelper(string taskId, string helperName, string addedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new AddHelperResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Check if helper is already added
            if (task.Helpers.Contains(helperName, StringComparer.OrdinalIgnoreCase))
            {
                return new AddHelperResult { Success = false, Error = $"{helperName} is already a helper on this task" };
            }

            // Add helper to task (in-memory)
            task.Helpers.Add(helperName);

            // Persist to database using task_helpers table
            try
            {
                var helper = _taskDb.AddTaskHelper(taskId, helperName, addedBy ?? task.Assignee ?? "System");
                if (helper == null)
                {
                    // Remove from in-memory if database failed
                    task.Helpers.Remove(helperName);
                    return new AddHelperResult { Success = false, Error = "Failed to persist helper to database" };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save helper: {ex.Message}");
                task.Helpers.Remove(helperName);
                return new AddHelperResult { Success = false, Error = $"Database error: {ex.Message}" };
            }

            BroadcastTaskUpdate();

            // Send Tier 1 notification to the helper
            await NotifyHelperAdded(helperName, taskId, task.Title, task.Assignee ?? addedBy);

            return new AddHelperResult { Success = true, HelperCount = task.Helpers.Count };
        }

        /// <summary>
        /// Remove a helper from a task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="helperName">Name of the helper terminal to remove.</param>
        /// <returns>Result indicating success or failure.</returns>
        public RemoveHelperResult RemoveHelper(string taskId, string helperName)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RemoveHelperResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Find and remove helper (case-insensitive)
            var existingHelper = task.Helpers.FirstOrDefault(h => h.Equals(helperName, StringComparison.OrdinalIgnoreCase));
            if (existingHelper == null)
            {
                return new RemoveHelperResult { Success = false, Error = $"{helperName} is not a helper on this task" };
            }

            // Remove from database
            try
            {
                var removed = _taskDb.RemoveTaskHelper(taskId, helperName);
                if (!removed)
                {
                    return new RemoveHelperResult { Success = false, Error = "Helper not found in database" };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to remove helper: {ex.Message}");
                return new RemoveHelperResult { Success = false, Error = $"Database error: {ex.Message}" };
            }

            // Remove from in-memory
            task.Helpers.Remove(existingHelper);

            BroadcastTaskUpdate();

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = task.Assignee ?? "System",
                Type = "helper",
                Action = "removed",
                Content = $"Removed {helperName} as helper from: {task.Title}",
                RelatedId = taskId
            });

            return new RemoveHelperResult { Success = true, HelperCount = task.Helpers.Count };
        }

        /// <summary>
        /// Request help from a helper on a task and send Tier 2 notification.
        /// </summary>
        /// <param name="taskId">The task ID needing help.</param>
        /// <param name="fromTerminal">Terminal name requesting help (usually the assignee).</param>
        /// <param name="helperName">Name of the helper to request help from.</param>
        /// <param name="message">Details about what help is needed.</param>
        /// <returns>Result indicating success or failure.</returns>
        public async Task<RequestHelpResult> RequestHelp(string taskId, string fromTerminal, string helperName, string message)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RequestHelpResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Verify the helper is actually on this task
            if (!task.Helpers.Contains(helperName, StringComparer.OrdinalIgnoreCase))
            {
                return new RequestHelpResult { Success = false, Error = $"{helperName} is not a helper on this task. Add them first." };
            }

            // Send Tier 2 notification (prominent help request)
            var notifyResult = await NotifyHelpRequested(helperName, taskId, task.Title, fromTerminal, message);
            if (!notifyResult.Success)
            {
                return new RequestHelpResult { Success = false, Error = notifyResult.Error };
            }

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Terminal = fromTerminal,
                Type = "help",
                Action = "requested",
                Content = $"Requested help from {helperName} on: {task.Title}",
                RelatedId = taskId
            });

            // Auto-generate inbox notification for helper_request
            try
            {
                CreateInboxNotification(
                    DefaultInboxRecipient,
                    taskId,
                    task.Title,
                    null,
                    null,
                    "helper_request",
                    $"{helperName} needs guidance on '{task.Title}': {message}",
                    fromTerminal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to create helper_request inbox notification: {ex.Message}");
            }

            return new RequestHelpResult { Success = true, Message = $"Help request sent to {helperName}" };
        }

        /// <summary>
        /// Respond to a stale task notification.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="response">Response: 'still_relevant', 'will_close', or 'reprioritize'.</param>
        /// <param name="terminalName">Terminal name responding to the notification.</param>
        /// <returns>Result indicating success or failure.</returns>
        public RespondStaleResult RespondToStale(string taskId, string response, string terminalName)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RespondStaleResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            try
            {
                // Record the response in the database
                _taskDb.RecordStaleResponse(taskId, response);

                // Handle different responses
                if (response == "still_relevant")
                {
                    // Clear stale flag and reset timer
                    _taskDb.ClearStaleTracking(taskId);
                    task.StaleLevel = 0;
                    task.FlaggedStaleAt = null;
                    task.StaleResponse = response;
                }
                else if (response == "will_close")
                {
                    // User indicated they'll close - keep flag but record response
                    task.StaleResponse = response;
                }
                else if (response == "reprioritize")
                {
                    // Move task back to todo and clear stale flag
                    task.Status = "todo";
                    task.SubStatus = null;
                    task.PausedAt = null;
                    task.StaleLevel = 0;
                    task.FlaggedStaleAt = null;
                    task.StaleResponse = response;
                    _taskDb.SaveTask(task);
                }

                BroadcastTaskUpdate();

                // Record activity
                RecordActivity(new ActivityEvent
                {
                    Terminal = terminalName,
                    Type = "stale",
                    Action = "responded",
                    Content = $"Responded to stale task '{task.Title}': {response}",
                    RelatedId = taskId
                });

                return new RespondStaleResult { Success = true, Message = $"Response '{response}' recorded" };
            }
            catch (Exception ex)
            {
                return new RespondStaleResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Get helpers for a task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <returns>List of helper names.</returns>
        public List<string> GetTaskHelpers(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                return task.Helpers.ToList();
            }
            return new List<string>();
        }

        /// <summary>
        /// Analyze task complexity to determine if a plan should be suggested.
        /// </summary>
        /// <param name="taskId">The task ID to analyze.</param>
        /// <returns>ComplexityAnalysisResult with score, signals, and recommendation.</returns>
        public ComplexityAnalysisResult AnalyzeTaskComplexity(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = $"Task not found: {taskId}"
                };
            }

            if (ComplexityDetector == null)
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = "ComplexityDetector service not available"
                };
            }

            try
            {
                var result = ComplexityDetector.Analyze(task.Title, task.Description);

                return new ComplexityAnalysisResult
                {
                    Success = true,
                    Score = result.Score,
                    SuggestPlan = result.SuggestPlan,
                    SignalsDetected = result.Signals,
                    Recommendation = result.Recommendation
                };
            }
            catch (Exception ex)
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = $"Analysis failed: {ex.Message}"
                };
            }
        }

        #endregion

        #region Project Methods

        /// <summary>
        /// Create a new project.
        /// Shared entry point for both the UI "New Project" dialog (MainForm) and the
        /// create_project MCP tool. Generates an 8-char ID, writes both the SQLite row
        /// (via the simple SaveProject + the richer SaveRichProject through
        /// ProjectService.SaveProject) AND the portable .claude/project.json, fires
        /// ProjectsUpdated, and records a "project created" activity event.
        ///
        /// Duplicate detection: if allowReuseExisting=false (the default for CREATE
        /// intent), an existing .claude/project.json at the path returns a clean error.
        /// Set allowReuseExisting=true to preserve the legacy sync-existing-by-ID behavior.
        /// </summary>
        public CreateProjectResult CreateProject(
            string name,
            string description,
            string createdBy,
            string path = null,
            string teamLead = null,
            string defaultTerminal = null,
            string projectType = null,
            string currentVersion = null,
            bool allowReuseExisting = false)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new CreateProjectResult { Success = false, Error = "Project name is required" };
            }

            // Check if a .claude/project.json already exists at the path
            string existingProjectId = null;
            if (!string.IsNullOrEmpty(path) && ProjectService != null)
            {
                try
                {
                    var existing = ProjectService.LoadProject(path);
                    if (existing != null)
                    {
                        if (!allowReuseExisting)
                        {
                            return new CreateProjectResult
                            {
                                Success = false,
                                Error = $"A project already exists at path '{path}' (id: {existing.Id}). Use update_project to modify it.",
                            };
                        }
                        existingProjectId = existing.Id;
                        LogInfo($"Found existing project at {path}, syncing to database (ID: {existingProjectId})");
                    }
                }
                catch
                {
                    // No existing project, will create new
                }
            }

            var project = new Project
            {
                Id = existingProjectId ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Description = description,
                CreatedBy = createdBy,
                Path = path
            };

            if (_projects.TryAdd(project.Id, project))
            {
                // Persist to database (5-column INSERT — rich columns get filled by the
                // SaveRichProject UPSERT inside ProjectService.SaveProject below).
                try { _projectDb.SaveProject(project); }
                catch (Exception ex) { LogError($"Failed to save project to database: {ex.Message}"); }

                // Create or update .claude/project.json file (and SaveRichProject UPSERT).
                MultiTerminal.Models.Project fileProject = null;
                if (!string.IsNullOrEmpty(path) && ProjectService != null)
                {
                    try
                    {
                        fileProject = MultiTerminal.Models.Project.Create(name, path);
                        fileProject.Id = project.Id; // Use the same 8-char ID across DB and JSON.
                        fileProject.Description = description ?? "";
                        if (!string.IsNullOrEmpty(teamLead)) fileProject.TeamLead = teamLead;
                        if (!string.IsNullOrEmpty(defaultTerminal)) fileProject.DefaultTerminal = defaultTerminal;
                        if (!string.IsNullOrEmpty(projectType)) fileProject.ProjectType = projectType;
                        if (!string.IsNullOrEmpty(currentVersion)) fileProject.CurrentVersion = currentVersion;
                        if (!string.IsNullOrEmpty(createdBy)) fileProject.CreatedBy = createdBy;
                        ProjectService.SaveProject(fileProject);
                        LogInfo($"Created/updated project file at {path}/.claude/project.json");
                    }
                    catch (Exception ex) { LogError($"Failed to save project file: {ex.Message}"); }
                }

                BroadcastProjectUpdate();

                // Record activity for the feed
                RecordActivity(new ActivityEvent
                {
                    Terminal = createdBy ?? "System",
                    Type = "project",
                    Action = "created",
                    Content = $"Created project: {name}",
                    RelatedId = project.Id
                });

                return new CreateProjectResult
                {
                    Success = true,
                    ProjectId = project.Id,
                    CreatedFileProject = fileProject,
                };
            }

            return new CreateProjectResult { Success = false, Error = "Failed to create project" };
        }

        /// <summary>
        /// Get a project by ID.
        /// Checks _projects cache first, then falls back to ProjectService.
        /// </summary>
        public GetProjectResult GetProject(string projectId)
        {
            // Count tasks for this project
            var taskCount = _tasks.Values.Count(t => t.ProjectId == projectId);

            // Try _projects cache first
            if (_projects.TryGetValue(projectId, out var project))
            {
                return new GetProjectResult
                {
                    Success = true,
                    Project = new ProjectInfo
                    {
                        Id = project.Id,
                        Name = project.Name,
                        Description = project.Description,
                        CreatedBy = project.CreatedBy,
                        CreatedAt = project.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        UpdatedAt = project.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        TaskCount = taskCount
                    }
                };
            }

            // Fallback: check ProjectService registry
            if (ProjectService != null)
            {
                try
                {
                    var registryEntry = ProjectService.GetAllRegisteredProjects()
                        .FirstOrDefault(p => p.Id == projectId);

                    if (registryEntry != null)
                    {
                        return new GetProjectResult
                        {
                            Success = true,
                            Project = new ProjectInfo
                            {
                                Id = registryEntry.Id,
                                Name = registryEntry.Name,
                                Description = "",
                                CreatedBy = "ProjectService",
                                CreatedAt = registryEntry.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                                UpdatedAt = registryEntry.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                                TaskCount = taskCount
                            }
                        };
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetProject: ProjectService fallback failed: {ex.Message}");
                }
            }

            return new GetProjectResult { Success = false, Error = $"Project not found: {projectId}" };
        }

        /// <summary>
        /// List all projects with task counts.
        /// Reads from ProjectService (single source of truth) with fallback to _projects cache.
        /// </summary>
        public ProjectListResult ListProjects()
        {
            var taskCounts = _tasks.Values
                .Where(t => !string.IsNullOrEmpty(t.ProjectId))
                .GroupBy(t => t.ProjectId)
                .ToDictionary(g => g.Key, g => g.Count());

            List<ProjectInfo> projectInfos;

            // Use ProjectService as single source of truth if available
            if (ProjectService != null)
            {
                try
                {
                    var registryEntries = ProjectService.GetAllRegisteredProjects();
                    projectInfos = registryEntries
                        .OrderByDescending(p => p.LastOpenedAt)
                        .Select(p => new ProjectInfo
                        {
                            Id = p.Id,
                            Name = p.Name,
                            Description = "",
                            CreatedBy = "ProjectService",
                            CreatedAt = p.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                            UpdatedAt = p.LastOpenedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                            TaskCount = taskCounts.TryGetValue(p.Id, out var count) ? count : 0
                        })
                        .ToList();

                    return new ProjectListResult
                    {
                        Success = true,
                        Projects = projectInfos,
                        Count = projectInfos.Count
                    };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] ListProjects: ProjectService failed, falling back to cache: {ex.Message}");
                }
            }

            // Fallback to _projects cache
            projectInfos = _projects.Values
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new ProjectInfo
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description,
                    CreatedBy = p.CreatedBy,
                    CreatedAt = p.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    UpdatedAt = p.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    TaskCount = taskCounts.TryGetValue(p.Id, out var count) ? count : 0
                })
                .ToList();

            return new ProjectListResult
            {
                Success = true,
                Projects = projectInfos,
                Count = projectInfos.Count
            };
        }

        /// <summary>
        /// Update a project's name and description.
        /// </summary>
        public UpdateProjectResult UpdateProject(string projectId, string name, string description, string updatedBy)
        {
            if (!_projects.TryGetValue(projectId, out var project))
            {
                return new UpdateProjectResult { Success = false, Error = $"Project not found: {projectId}" };
            }

            var previousName = project.Name;

            // Update only if values are provided
            if (!string.IsNullOrWhiteSpace(name))
            {
                project.Name = name;
            }
            if (description != null)
            {
                project.Description = description;
            }
            project.UpdatedAt = DateTime.UtcNow;

            // Persist to database
            try { _projectDb.SaveProject(project); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update project: {ex.Message}"); }

            BroadcastProjectUpdate();

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? "System",
                Type = "project",
                Action = "updated",
                Content = previousName != project.Name
                    ? $"Renamed project: '{previousName}' -> '{project.Name}'"
                    : $"Updated project: {project.Name}",
                RelatedId = projectId
            });

            return new UpdateProjectResult { Success = true };
        }

        /// <summary>
        /// Canonical project-delete path. Every surface (REST DELETE /api/projects/{id}, the
        /// delete_project MCP tool, the Project Manager dialog's callers, and the projects-pane
        /// delete) routes here so a project is removed consistently:
        ///  - unregistered via <see cref="Services.ProjectService.UnregisterProject"/> — the only
        ///    removal that fires ProjectRemoved/RegistryChangedExternally (so CodeGraphWatcher drops
        ///    the root's FileSystemWatcher) AND deletes .claude/project.json when asked;
        ///  - its stale code-graph rows evicted via the gated <see cref="CodeGraphIndexCoordinator"/>;
        ///  - broadcast + recorded in the activity feed.
        /// Associated tasks are intentionally NOT deleted. <paramref name="deleteLocalConfig"/> also
        /// removes the on-disk .claude/project.json (default false = unregister only).
        /// </summary>
        public DeleteProjectResult DeleteProject(string projectId, string deletedBy, bool deleteLocalConfig = false)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return new DeleteProjectResult { Success = false, Error = "Project id is required" };

            // Existence + name resolution from the database (single source of truth post-G8), with a
            // fall back to the in-memory cache name for the activity-feed line.
            var rich = _projectDb.GetRichProject(projectId);
            _projects.TryRemove(projectId, out var cached);
            if (rich == null && cached == null)
                return new DeleteProjectResult { Success = false, Error = $"Project not found: {projectId}" };

            string projectName = rich?.Name ?? cached?.Name ?? projectId;

            // Canonical unregister: DB delete + optional .claude/project.json + fires
            // ProjectRemoved/RegistryChangedExternally (CodeGraphWatcher reconciles and drops the
            // watcher). Falls back to a bare DB delete only if ProjectService isn't wired.
            // This is the AUTHORITATIVE step — if it throws, the project was not removed, so report
            // failure instead of a false success (and restore the optimistically-evicted cache entry
            // so callers don't see a phantom-deleted project that's still in the DB).
            try
            {
                if (ProjectService != null)
                    ProjectService.UnregisterProject(projectId, deleteLocalConfig);
                else
                    _projectDb.DeleteProject(projectId);
            }
            catch (Exception ex)
            {
                LogError($"Failed to delete project {projectId}: {ex.Message}");
                if (cached != null) _projects.TryAdd(projectId, cached);
                return new DeleteProjectResult { Success = false, Error = $"Failed to delete project: {ex.Message}" };
            }

            // Best-effort: evict the project's code-graph rows, serialized through the index gate so
            // it can't race a reindex. No-op if the project was never indexed.
            try { CodeGraphIndexCoordinator?.ClearProject(projectName); }
            catch (Exception ex) { LogError($"Failed to clear code graph for project {projectName}: {ex.Message}"); }

            BroadcastProjectUpdate();

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = deletedBy ?? "System",
                Type = "project",
                Action = "deleted",
                Content = $"Deleted project: {projectName}",
                RelatedId = projectId
            });

            return new DeleteProjectResult { Success = true };
        }

        /// <summary>
        /// Get all projects (internal list for compatibility).
        /// Reads from ProjectService (single source of truth) with fallback to _projects cache.
        /// </summary>
        public List<Project> GetProjectsList()
        {
            // Use ProjectService as single source of truth if available
            if (ProjectService != null)
            {
                try
                {
                    var registryEntries = ProjectService.GetAllRegisteredProjects();
                    return registryEntries
                        .OrderByDescending(p => p.LastOpenedAt)
                        .Select(p => new Project
                        {
                            Id = p.Id,
                            Name = p.Name,
                            Description = "",
                            Path = p.Path,
                            CreatedBy = "ProjectService",
                            CreatedAt = p.LastOpenedAt,
                            UpdatedAt = p.LastOpenedAt
                        })
                        .ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetProjectsList: ProjectService failed, falling back to cache: {ex.Message}");
                }
            }

            // Fallback to _projects cache
            return _projects.Values.OrderByDescending(p => p.CreatedAt).ToList();
        }

        /// <summary>
        /// Get all projects with task counts.
        /// Reads from ProjectService (single source of truth) with fallback to _projects cache.
        /// </summary>
        public List<ProjectWithCount> GetProjects()
        {
            var taskCounts = _tasks.Values
                .GroupBy(t => t.ProjectId ?? "")
                .ToDictionary(g => g.Key, g => g.Count());

            // Use ProjectService as single source of truth if available
            if (ProjectService != null)
            {
                try
                {
                    var registryEntries = ProjectService.GetAllRegisteredProjects();
                    return registryEntries.Select(p => new ProjectWithCount
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Description = "", // ProjectRegistryEntry doesn't have Description
                        TaskCount = taskCounts.TryGetValue(p.Id, out var count) ? count : 0
                    }).ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] GetProjects: ProjectService failed, falling back to cache: {ex.Message}");
                }
            }

            // Fallback to _projects cache
            return _projects.Values.Select(p => new ProjectWithCount
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                TaskCount = taskCounts.TryGetValue(p.Id, out var count) ? count : 0
            }).ToList();
        }

        /// <summary>
        /// Broadcast project update to all listeners.
        /// </summary>
        private void BroadcastProjectUpdate()
        {
            var projects = GetProjectsList();
            RaiseSafe(ProjectsUpdated, projects);
        }

        #endregion

        #region Profile Methods

        /// <summary>
        /// Create a new team member profile.
        /// </summary>
        public CreateProfileResult CreateProfile(string id, string displayName, string avatarUrl, string role, string bio, List<string> skills, List<string> interests, List<string> projectIds = null, string agentInstructions = null, string preferredModel = null, bool? isTeamLead = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new CreateProfileResult { Success = false, Error = "Profile ID is required" };
            }

            if (_profiles.ContainsKey(id))
            {
                return new CreateProfileResult { Success = false, Error = $"Profile already exists: {id}" };
            }

            var profile = new TeamMemberProfile
            {
                Id = id,
                DisplayName = displayName,
                AvatarUrl = avatarUrl,
                Role = role,
                Bio = bio,
                AgentInstructions = agentInstructions,
                PreferredModel = preferredModel ?? "sonnet",
                IsTeamLead = isTeamLead ?? false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (skills != null) profile.SetSkills(skills);
            if (interests != null) profile.SetInterests(interests);
            if (projectIds != null) profile.SetProjectIds(projectIds);

            _profiles.TryAdd(id, profile);

            // Persist to database
            try { _taskDb.SaveProfile(profile); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save profile: {ex.Message}"); }

            BroadcastProfileUpdate();

            return new CreateProfileResult { Success = true, ProfileId = id };
        }

        /// <summary>
        /// Update an existing team member profile. Only provided fields are updated.
        /// </summary>
        public UpdateProfileResult UpdateProfile(string id, string displayName, string avatarUrl, string role, string bio, List<string> skills, List<string> interests, List<string> projectIds = null, string agentInstructions = null, string preferredModel = null, bool? isTeamLead = null)
        {
            if (!_profiles.TryGetValue(id, out var profile))
            {
                return new UpdateProfileResult { Success = false, Error = $"Profile not found: {id}" };
            }

            // Update only provided fields (null means don't change)
            if (displayName != null) profile.DisplayName = displayName;
            if (avatarUrl != null) profile.AvatarUrl = avatarUrl;
            if (role != null) profile.Role = role;
            if (bio != null) profile.Bio = bio;
            if (skills != null) profile.SetSkills(skills);
            if (interests != null) profile.SetInterests(interests);
            if (projectIds != null) profile.SetProjectIds(projectIds);
            if (agentInstructions != null) profile.AgentInstructions = agentInstructions;
            if (preferredModel != null) profile.PreferredModel = preferredModel;
            if (isTeamLead.HasValue) profile.IsTeamLead = isTeamLead.Value;

            profile.UpdatedAt = DateTime.UtcNow;

            // Persist to database
            try { _taskDb.SaveProfile(profile); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update profile: {ex.Message}"); }

            BroadcastProfileUpdate();

            return new UpdateProfileResult { Success = true };
        }

        /// <summary>
        /// Get a team member profile by ID.
        /// </summary>
        public GetProfileResult GetProfile(string id)
        {
            if (_profiles.TryGetValue(id, out var profile))
            {
                return new GetProfileResult { Success = true, Profile = profile };
            }

            return new GetProfileResult { Success = false, Error = $"Profile not found: {id}" };
        }

        /// <summary>
        /// List all team member profiles.
        /// </summary>
        public ListProfilesResult ListProfiles()
        {
            var profiles = _profiles.Values
                .Where(p => !IsTemporaryAgent(p.Id))
                .OrderBy(p => p.DisplayName ?? p.Id)
                .ToList();
            return new ListProfilesResult { Success = true, Profiles = profiles };
        }

        /// <summary>
        /// Delete a team member profile.
        /// </summary>
        public DeleteProfileResult DeleteProfile(string id)
        {
            if (!_profiles.TryRemove(id, out var profile))
            {
                return new DeleteProfileResult { Success = false, Error = $"Profile not found: {id}" };
            }

            // Delete from database
            try { _taskDb.DeleteProfile(id); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to delete profile: {ex.Message}"); }

            BroadcastProfileUpdate();

            return new DeleteProfileResult { Success = true };
        }

        /// <summary>
        /// Set a profile's online status to true.
        /// Called by SessionStart hook when Claude session starts.
        /// </summary>
        public SetProfileStatusResult SetProfileOnline(string id)
        {
            try
            {
                // Auto-create profile if it doesn't exist
                if (!_profiles.ContainsKey(id))
                {
                    var newProfile = new TeamMemberProfile
                    {
                        Id = id,
                        DisplayName = id,
                        IsOnline = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _profiles.TryAdd(id, newProfile);
                    _taskDb.SaveProfile(newProfile);
                    System.Diagnostics.Debug.WriteLine($"[MessageBroker] Auto-created profile: {id}");
                }

                // Set online in database
                _taskDb.SetProfileOnline(id);

                // Update in-memory profile
                if (_profiles.TryGetValue(id, out var profile))
                {
                    profile.IsOnline = true;
                    profile.UpdatedAt = DateTime.UtcNow;
                }

                BroadcastProfileUpdate();
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Set profile online: {id}");

                return new SetProfileStatusResult { Success = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to set profile online: {ex.Message}");
                return new SetProfileStatusResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Set a profile's online status to false.
        /// Called by SessionEnd hook when Claude session ends.
        /// </summary>
        public SetProfileStatusResult SetProfileOffline(string id)
        {
            try
            {
                // Set offline in database
                _taskDb.SetProfileOffline(id);

                // Update in-memory profile
                if (_profiles.TryGetValue(id, out var profile))
                {
                    profile.IsOnline = false;
                    profile.UpdatedAt = DateTime.UtcNow;
                }

                BroadcastProfileUpdate();
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Set profile offline: {id}");

                return new SetProfileStatusResult { Success = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to set profile offline: {ex.Message}");
                return new SetProfileStatusResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Broadcast profile updates to all clients.
        /// </summary>
        private void BroadcastProfileUpdate()
        {
            var profiles = _profiles.Values.OrderBy(p => p.DisplayName ?? p.Id).ToList();
            RaiseSafe(ProfilesUpdated, profiles);
        }

        #endregion

        #region Native Helper Sessions

        /// <summary>
        /// Track a native helper spawn (called when spawning via Task tool).
        /// </summary>
        public SpawnHelperResult TrackHelperSpawn(string taskId, string prompt, string spawnedBy)
        {
            var session = new HelperSession
            {
                TaskId = taskId,
                Prompt = prompt,
                SpawnedBy = spawnedBy,
                SpawnedAt = DateTime.UtcNow,
                Status = "spawning"
            };

            // Save to database
            try
            {
                _taskDb.SaveHelperSession(session);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save helper session: {ex.Message}");
                return new SpawnHelperResult
                {
                    Success = false,
                    Error = $"Database error: {ex.Message}"
                };
            }

            // Broadcast helper spawn event (for UI updates)
            BroadcastHelperUpdate(session);

            // Record activity
            RecordActivity(new ActivityEvent
            {
                Type = "helper",
                Action = "spawned",
                Terminal = spawnedBy,
                Content = $"Spawned helper: {prompt.Substring(0, Math.Min(50, prompt.Length))}...",
                Timestamp = DateTime.UtcNow
            });

            return new SpawnHelperResult
            {
                Success = true,
                HelperId = session.Id
            };
        }

        /// <summary>
        /// Update helper session status (lifecycle transitions).
        /// </summary>
        public UpdateHelperStatusResult UpdateHelperStatus(string helperId, string status)
        {
            // Validate status
            var validStatuses = new[] { "spawning", "working", "completed", "failed" };
            if (!validStatuses.Contains(status))
            {
                return new UpdateHelperStatusResult
                {
                    Success = false,
                    Error = $"Invalid status. Must be one of: {string.Join(", ", validStatuses)}"
                };
            }

            // Get existing session
            HelperSession session;
            try
            {
                session = _taskDb.GetHelperSession(helperId);
                if (session == null)
                {
                    return new UpdateHelperStatusResult
                    {
                        Success = false,
                        Error = $"Helper not found: {helperId}"
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to get helper session: {ex.Message}");
                return new UpdateHelperStatusResult
                {
                    Success = false,
                    Error = $"Database error: {ex.Message}"
                };
            }

            // Update status
            DateTime? completedAt = null;
            if (status == "completed" || status == "failed")
            {
                completedAt = DateTime.UtcNow;
            }

            try
            {
                _taskDb.UpdateHelperStatus(helperId, status, completedAt);
                session.Status = status;
                session.CompletedAt = completedAt;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update helper status: {ex.Message}");
                return new UpdateHelperStatusResult
                {
                    Success = false,
                    Error = $"Database error: {ex.Message}"
                };
            }

            // Broadcast helper update (for UI lifecycle visualization)
            BroadcastHelperUpdate(session);

            // Record activity if completed/failed
            if (status == "completed" || status == "failed")
            {
                RecordActivity(new ActivityEvent
                {
                    Type = "helper",
                    Action = status,
                    Terminal = session.SpawnedBy,
                    Content = $"Helper {status}: {session.Prompt.Substring(0, Math.Min(50, session.Prompt.Length))}...",
                    Timestamp = DateTime.UtcNow
                });
            }

            return new UpdateHelperStatusResult
            {
                Success = true
            };
        }

        /// <summary>
        /// Log a message from a helper session.
        /// </summary>
        public LogHelperMessageResult LogHelperMessage(string helperId, string message)
        {
            var helperMessage = new HelperMessage
            {
                HelperId = helperId,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _taskDb.SaveHelperMessage(helperMessage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save helper message: {ex.Message}");
                return new LogHelperMessageResult
                {
                    Success = false,
                    Error = $"Database error: {ex.Message}"
                };
            }

            // Broadcast helper message (for chat panel display)
            RaiseSafe(HelperMessageLogged, helperMessage);

            return new LogHelperMessageResult
            {
                Success = true,
                MessageId = helperMessage.Id
            };
        }

        /// <summary>
        /// Get all active helper sessions (spawning or working).
        /// </summary>
        public GetActiveHelpersResult GetActiveHelpers()
        {
            try
            {
                var sessions = _taskDb.GetActiveHelperSessions();
                return new GetActiveHelpersResult
                {
                    Success = true,
                    Helpers = sessions
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to get active helpers: {ex.Message}");
                return new GetActiveHelpersResult
                {
                    Success = false,
                    Error = $"Database error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Broadcast helper session update (for UI updates).
        /// </summary>
        private void BroadcastHelperUpdate(HelperSession session)
        {
            RaiseSafe(HelperSessionUpdated, session);
        }

        #endregion

        #region Office Agent Animation

        /// <summary>
        /// Notify that an agent has been spawned (for Office Panel walk-in animation).
        /// Agents are not registered terminals - they are lightweight entries for visual display only.
        /// </summary>
        public OfficeAgentResult NotifyAgentSpawned(string name, string spawnedBy)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new OfficeAgentResult { Success = false, Error = "Agent name is required" };

            // Deduplicate: if name already in use, append #2, #3, etc.
            string uniqueName = name;
            int suffix = 2;
            while (_officeAgents.ContainsKey(uniqueName))
            {
                uniqueName = $"{name} #{suffix}";
                suffix++;
            }

            var agent = new OfficeAgentInfo
            {
                Name = uniqueName,
                SpawnedBy = spawnedBy,
                SpawnedAt = DateTime.UtcNow,
                Status = "working"
            };

            _officeAgents[uniqueName] = agent;
            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Office agent spawned: {uniqueName} (by {spawnedBy})");

            RaiseSafe(OfficeAgentSpawned, agent);

            return new OfficeAgentResult { Success = true, AgentName = uniqueName };
        }

        /// <summary>
        /// Notify that an agent has departed (for Office Panel exit animation).
        /// </summary>
        public OfficeAgentResult NotifyAgentDeparted(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new OfficeAgentResult { Success = false, Error = "Agent name is required" };

            // Try exact match first
            if (_officeAgents.TryRemove(name, out var agent))
            {
                agent.Status = "completed";
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Office agent departed: {name}");
                RaiseSafe(OfficeAgentDeparted, agent);
                return new OfficeAgentResult { Success = true, AgentName = name };
            }

            // Fuzzy match: find most recently spawned agent with matching base name
            var match = _officeAgents
                .Where(kv => kv.Key == name || kv.Key.StartsWith(name + " #"))
                .OrderByDescending(kv => kv.Value.SpawnedAt)
                .FirstOrDefault();

            if (match.Key != null && _officeAgents.TryRemove(match.Key, out var matchedAgent))
            {
                matchedAgent.Status = "completed";
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Office agent departed: {match.Key} (fuzzy match from {name})");
                RaiseSafe(OfficeAgentDeparted, matchedAgent);
                return new OfficeAgentResult { Success = true, AgentName = match.Key };
            }

            return new OfficeAgentResult { Success = false, Error = $"Agent not found: {name}" };
        }

        /// <summary>
        /// Get all currently active office agents.
        /// </summary>
        public List<OfficeAgentInfo> GetOfficeAgents()
        {
            return _officeAgents.Values.ToList();
        }

        /// <summary>
        /// Remove all office agents that have been active longer than the specified duration.
        /// Used to clean up ghost agents from interrupted/cancelled subagents.
        /// </summary>
        public List<OfficeAgentInfo> CleanupStaleOfficeAgents(int olderThanMinutes = 30)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-olderThanMinutes);
            var staleAgents = _officeAgents.Values
                .Where(a => a.SpawnedAt < cutoff)
                .ToList();

            foreach (var agent in staleAgents)
            {
                NotifyAgentDeparted(agent.Name);
            }

            if (staleAgents.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Cleaned up {staleAgents.Count} stale office agent(s)");
            }

            return staleAgents;
        }

        /// <summary>
        /// Remove all office agents immediately (force cleanup).
        /// </summary>
        public List<OfficeAgentInfo> ClearAllOfficeAgents()
        {
            var allAgents = _officeAgents.Values.ToList();

            foreach (var agent in allAgents)
            {
                NotifyAgentDeparted(agent.Name);
            }

            if (allAgents.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Force-cleared {allAgents.Count} office agent(s)");
            }

            return allAgents;
        }

        #endregion

        #region Activity Recording

        /// <summary>
        /// Record an activity event for the Activity Panel feed.
        /// Also persists to the activity_feed table for the dashboard.
        /// </summary>
        public void RecordActivity(ActivityEvent activity, bool alreadyPersisted = false)
        {
            System.Diagnostics.Debug.WriteLine($"[MessageBroker] Recording activity: {activity.Type}/{activity.Action} - {activity.Content}");

            // Auto-resolve project_id from task cache if not set explicitly
            if (string.IsNullOrEmpty(activity.ProjectId) && !string.IsNullOrEmpty(activity.RelatedId))
            {
                var task = GetTask(activity.RelatedId);
                if (task != null && !string.IsNullOrEmpty(task.ProjectId))
                    activity.ProjectId = task.ProjectId;
            }

            RaiseSafe(ActivityRecorded, activity);

            // Persist to activity_feed table for dashboard (skip if caller already persisted, e.g. RecordBuildActivity)
            if (!alreadyPersisted)
            {
                try
                {
                    var activityType = $"{activity.Type}_{activity.Action}";
                    ActivityFeedService?.RecordGeneralActivity(activityType, activity.Terminal, activity.Content,
                        projectId: activity.ProjectId);
                }
                catch { /* Don't let feed persistence failures break the main flow */ }
            }
        }

        /// <summary>
        /// Record a build activity (can be called by external code after builds).
        /// Records to both the new activity_feed table and the legacy event system.
        /// </summary>
        public void RecordBuildActivity(string terminal, bool success, string projectName, string details = null)
        {
            var eventType = success ? "BUILD_SUCCEEDED" : "BUILD_FAILED";
            var summary = success ? $"Build succeeded: {projectName}" : $"Build failed: {projectName}";

            // Record to new activity_feed table
            ActivityFeedService?.RecordBuildEvent(projectName, eventType, terminal, summary, details);

            // Keep legacy event for backwards compatibility (alreadyPersisted: true to avoid double-write)
            RecordActivity(new ActivityEvent
            {
                Terminal = terminal,
                Type = "build",
                Action = success ? "completed" : "failed",
                Content = summary,
                Details = details
            }, alreadyPersisted: true);
        }

        /// <summary>
        /// Record when a build starts (for activity feed tracking).
        /// </summary>
        public void RecordBuildStart(string terminal, string projectName)
        {
            var summary = $"Build started: {projectName}";

            // Record to activity_feed table only (no legacy event for start)
            ActivityFeedService?.RecordBuildEvent(projectName, "BUILD_STARTED", terminal, summary);
        }

        #endregion

        #region Team Agent Message Bridge

        /// <summary>
        /// Record a message sent between native Team Agents for display in ChatPanel.
        /// This does NOT attempt delivery — the message was already delivered natively
        /// between team agents via Claude Code's built-in SendMessage. This method
        /// only adds it to history and fires the MessageSent event for UI display.
        /// </summary>
        public void RecordTeamMessage(string sender, string recipient, string content, string teamName = null)
        {
            var message = new Message
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                From = sender,
                To = recipient,
                Content = content,
                Timestamp = DateTime.UtcNow,
                Delivered = true
            };

            // Add to message history for ChatPanel
            lock (_historyLock)
            {
                _messageHistory.Add(message);
                while (_messageHistory.Count > MaxHistorySize)
                    _messageHistory.RemoveAt(0);
            }

            LogInfo($"TEAM MSG [{teamName ?? "?"}] {sender} → {recipient}: {content.Substring(0, Math.Min(50, content.Length))}...");
            RaiseSafe(MessageSent, message);
        }

        #endregion

        #region Critical Section

        /// <summary>
        /// Set or clear a terminal's critical section status.
        /// Critical sections prevent task interruptions during sensitive operations.
        /// </summary>
        /// <param name="terminalName">The terminal name.</param>
        /// <param name="enter">True to enter critical section, false to exit.</param>
        /// <param name="expiresAt">When the critical section should automatically expire (null for immediate exit).</param>
        public void SetCriticalSection(string terminalName, bool enter, DateTime? expiresAt)
        {
            // Get or create terminal activity record
            var activity = _taskDb.GetTerminalActivity(terminalName) ?? new MCPServer.Models.TerminalActivity
            {
                Terminal = terminalName,
                Status = "working"
            };

            if (enter)
            {
                activity.InCriticalSection = true;
                activity.CriticalSectionTimeout = expiresAt;
                activity.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                activity.InCriticalSection = false;
                activity.CriticalSectionTimeout = null;
                activity.UpdatedAt = DateTime.UtcNow;
            }

            _taskDb.SaveTerminalActivity(activity);

            System.Diagnostics.Debug.WriteLine($"[MessageBroker] {terminalName} {(enter ? "entered" : "exited")} critical section" +
                (expiresAt.HasValue ? $" (expires {expiresAt.Value:HH:mm:ss})" : ""));
        }

        #endregion

        #region Plan Methods

        /// <summary>
        /// Broadcast a plan update notification to all terminals.
        /// </summary>
        public async Task BroadcastPlanUpdate(string planId, string updateType, string details = null, string triggeredBy = null)
        {
            var args = new PlanUpdateEventArgs
            {
                PlanId = planId,
                UpdateType = updateType,
                Details = details,
                TriggeredBy = triggeredBy
            };

            RaiseSafe(PlanUpdated, args);

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = triggeredBy ?? "System",
                Type = "plan",
                Action = updateType.ToLowerInvariant(),
                Content = string.IsNullOrEmpty(details) ? $"Plan: {updateType}" : $"{updateType}: {details}",
                RelatedId = planId
            });

            // Also broadcast as a system message so terminals are notified
            var message = $"[PLAN UPDATE] {updateType}";
            if (!string.IsNullOrEmpty(details))
                message += $": {details}";

            await BroadcastSystemMessage(message);
        }

        #endregion

        #region User Inbox

        /// <summary>
        /// Create an inbox notification and persist it.
        /// </summary>
        public CreateInboxMessageResult CreateInboxNotification(
            string userId,
            string taskId,
            string taskTitle,
            int? checklistItemIndex,
            string checklistItemName,
            string type,
            string summary,
            string createdBy)
        {
            var message = new InboxMessage
            {
                UserId = userId,
                TaskId = taskId,
                TaskTitle = taskTitle,
                ChecklistItemIndex = checklistItemIndex,
                ChecklistItemName = checklistItemName,
                Type = type,
                Summary = summary,
                CreatedBy = createdBy
            };

            try
            {
                _taskDb.SaveInboxMessage(message);

                // Raise event for UI updates
                RaiseSafe(InboxUpdated, new InboxUpdatedEventArgs
                {
                    UserId = userId,
                    Message = message,
                    UnreadCount = _taskDb.GetInboxUnreadCount(userId)
                });

                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Inbox notification created: {type} for {userId} on task {taskId}");

                return new CreateInboxMessageResult { Success = true, MessageId = message.Id };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to create inbox notification: {ex.Message}");
                return new CreateInboxMessageResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Get inbox messages for a user.
        /// </summary>
        public GetInboxResult GetInbox(string userId, bool unreadOnly = false, int limit = 50)
        {
            try
            {
                var messages = _taskDb.GetInboxMessages(userId, unreadOnly, limit);
                var unreadCount = _taskDb.GetInboxUnreadCount(userId);
                return new GetInboxResult
                {
                    Success = true,
                    Messages = messages,
                    UnreadCount = unreadCount,
                    TotalCount = messages.Count
                };
            }
            catch (Exception ex)
            {
                return new GetInboxResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Mark an inbox message as read.
        /// </summary>
        public MarkInboxReadResult MarkInboxRead(string messageId)
        {
            try
            {
                var message = _taskDb.GetInboxMessage(messageId);
                if (message == null)
                    return new MarkInboxReadResult { Success = false, Error = $"Message not found: {messageId}" };

                _taskDb.MarkInboxRead(messageId);

                // Raise event for UI updates
                RaiseSafe(InboxUpdated, new InboxUpdatedEventArgs
                {
                    UserId = message.UserId,
                    Message = message,
                    UnreadCount = _taskDb.GetInboxUnreadCount(message.UserId)
                });

                return new MarkInboxReadResult { Success = true };
            }
            catch (Exception ex)
            {
                return new MarkInboxReadResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Mark all inbox messages as read for a user.
        /// </summary>
        public MarkInboxReadResult MarkAllInboxRead(string userId)
        {
            try
            {
                var count = _taskDb.MarkAllInboxRead(userId);

                RaiseSafe(InboxUpdated, new InboxUpdatedEventArgs
                {
                    UserId = userId,
                    Message = null,
                    UnreadCount = 0
                });

                return new MarkInboxReadResult { Success = true };
            }
            catch (Exception ex)
            {
                return new MarkInboxReadResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Reply to an inbox message.
        /// </summary>
        public ReplyToInboxResult ReplyToInbox(string messageId, string replyText)
        {
            try
            {
                var message = _taskDb.GetInboxMessage(messageId);
                if (message == null)
                    return new ReplyToInboxResult { Success = false, Error = $"Message not found: {messageId}" };

                _taskDb.ReplyToInboxMessage(messageId, replyText);

                RaiseSafe(InboxUpdated, new InboxUpdatedEventArgs
                {
                    UserId = message.UserId,
                    Message = message,
                    UnreadCount = _taskDb.GetInboxUnreadCount(message.UserId)
                });

                return new ReplyToInboxResult { Success = true };
            }
            catch (Exception ex)
            {
                return new ReplyToInboxResult { Success = false, Error = ex.Message };
            }
        }

        #endregion

        #region Terminal Input Injection

        /// <summary>
        /// Request that MT type <paramref name="text"/> into the live terminal owned by
        /// <paramref name="agentName"/>. Raises <see cref="TerminalInjectRequested"/>, which
        /// MainForm handles by resolving the agent to its TerminalDocument and injecting on the
        /// UI thread. Used by the SessionStart(<c>source=clear</c>) hook to type "initializing..."
        /// after /clear so the cleared session gets a turn and runs /multiterminal:session-start
        /// (task be599e08). Best-effort: the event is raised synchronously, but delivery/injection
        /// is the subscriber's responsibility — a missing terminal is a no-op, not an error here.
        /// </summary>
        public (bool success, string error) RequestTerminalInject(string agentName, string sessionId, string text, string kind = "clear-trigger")
        {
            if (string.IsNullOrWhiteSpace(agentName))
                return (false, "agentName is required");
            if (string.IsNullOrEmpty(text))
                return (false, "text is required");

            RaiseSafe(TerminalInjectRequested, new TerminalInjectEventArgs
            {
                AgentName = agentName,
                SessionId = sessionId,
                Text = text,
                Kind = string.IsNullOrEmpty(kind) ? "clear-trigger" : kind
            });

            return (true, null);
        }

        #endregion

        #region Browser Tabs

        /// <summary>
        /// Open a new browser tab in a terminal's HUD area.
        /// </summary>
        public (bool success, string tabId, string error) OpenBrowserTab(string terminalId, string title, string url, string htmlContent)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tabId = Guid.NewGuid().ToString("N").Substring(0, 8);

            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "open",
                TerminalId = terminalId,
                TabId = tabId,
                Title = title,
                Url = url,
                HtmlContent = htmlContent
            });

            return (true, tabId, null);
        }

        /// <summary>
        /// Update an existing browser tab's content or URL.
        /// </summary>
        public (bool success, string error) SetBrowserContent(string terminalId, string tabId, string title, string url, string htmlContent)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, $"Terminal not found: {terminalId}");

            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "update",
                TerminalId = terminalId,
                TabId = tabId,
                Title = title,
                Url = url,
                HtmlContent = htmlContent
            });

            return (true, null);
        }

        /// <summary>
        /// Close a browser tab in a terminal's HUD area.
        /// </summary>
        public (bool success, string error) CloseBrowserTab(string terminalId, string tabId)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, $"Terminal not found: {terminalId}");

            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "close",
                TerminalId = terminalId,
                TabId = tabId
            });

            return (true, null);
        }

        /// <summary>
        /// Execute a JavaScript snippet in a browser tab and return the result.
        /// </summary>
        public async Task<(bool success, string result, string error)> ExecuteBrowserScript(string terminalId, string tabId, string script)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "execute_script",
                TerminalId = terminalId,
                TabId = tabId,
                Script = script,
                ResultTcs = tcs
            });

            try
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Get captured console logs from a browser tab.
        /// </summary>
        public async Task<(bool success, string result, string error)> GetBrowserConsoleLogs(string terminalId, string tabId, int? limit)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "get_console_logs",
                TerminalId = terminalId,
                TabId = tabId,
                Limit = limit,
                ResultTcs = tcs
            });

            try
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Get content of a DOM element in a browser tab.
        /// </summary>
        public async Task<(bool success, string result, string error)> GetBrowserElementContent(string terminalId, string tabId, string selector, string property)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "get_element_content",
                TerminalId = terminalId,
                TabId = tabId,
                Selector = selector,
                Property = property ?? "textContent",
                ResultTcs = tcs
            });

            try
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Capture a PNG screenshot of a browser tab's content.
        /// </summary>
        public async Task<(bool success, string result, string error)> CaptureBrowserScreenshot(string terminalId, string tabId)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "capture_screenshot",
                TerminalId = terminalId,
                TabId = tabId,
                ResultTcs = tcs
            });

            try
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        /// <summary>
        /// Post a JSON message to a browser tab's page.
        /// The page receives it via window.chrome.webview.addEventListener('message', ...).
        /// </summary>
        public async Task<(bool success, string error)> PostBrowserMessage(string terminalId, string tabId, string jsonData)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "post_message",
                TerminalId = terminalId,
                TabId = tabId,
                MessageData = jsonData,
                ResultTcs = tcs
            });

            try
            {
                await tcs.Task.ConfigureAwait(false);
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Get messages sent from a browser tab's page via window.chrome.webview.postMessage().
        /// </summary>
        public async Task<(bool success, string result, string error)> GetBrowserMessages(string terminalId, string tabId, int? limit)
        {
            var terminal = GetTerminal(terminalId);
            if (terminal == null)
                return (false, null, $"Terminal not found: {terminalId}");

            var tcs = new TaskCompletionSource<string>();
            RaiseSafe(BrowserTabRequested, new BrowserTabEventArgs
            {
                Action = "get_messages",
                TerminalId = terminalId,
                TabId = tabId,
                Limit = limit,
                ResultTcs = tcs
            });

            try
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        #endregion

        #region Task Attachments

        /// <summary>
        /// Maximum allowed attachment size (10 MB).
        /// </summary>
        private const long MaxAttachmentSizeBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Add an image attachment to a task or checklist item.
        /// Validates MIME type and size, writes file to disk, and persists metadata.
        /// </summary>
        /// <param name="taskId">The task to attach the image to.</param>
        /// <param name="checklistItemIndex">Checklist item index (-1 for task-level).</param>
        /// <param name="fileName">Original file name.</param>
        /// <param name="mimeType">MIME type of the image (must be image/*).</param>
        /// <param name="imageData">Raw image bytes.</param>
        /// <param name="addedBy">Name of the user or terminal adding the attachment.</param>
        /// <returns>Result indicating success or failure with attachment ID.</returns>
        public AddAttachmentResult AddAttachment(string taskId, int checklistItemIndex, string fileName, string mimeType, byte[] imageData, string addedBy)
        {
            try
            {
                // Validate task exists
                if (!_tasks.TryGetValue(taskId, out _))
                {
                    return new AddAttachmentResult { Success = false, Error = $"Task not found: {taskId}" };
                }

                // Validate MIME type is image/*
                if (string.IsNullOrEmpty(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return new AddAttachmentResult { Success = false, Error = $"Invalid MIME type: {mimeType}. Only image/* types are allowed." };
                }

                // Validate size
                if (imageData == null || imageData.Length == 0)
                {
                    return new AddAttachmentResult { Success = false, Error = "Image data is empty." };
                }

                if (imageData.Length > MaxAttachmentSizeBytes)
                {
                    return new AddAttachmentResult { Success = false, Error = $"Image size ({imageData.Length} bytes) exceeds maximum allowed size ({MaxAttachmentSizeBytes} bytes / 10 MB)." };
                }

                // Create attachment record
                var attachment = new TaskAttachment
                {
                    TaskId = taskId,
                    ChecklistItemIndex = checklistItemIndex,
                    FileName = fileName,
                    MimeType = mimeType,
                    FileSizeBytes = imageData.Length,
                    AddedBy = addedBy
                };
                var safeName = Path.GetFileName(fileName);
                if (string.IsNullOrEmpty(safeName)) safeName = "attachment";
                attachment.StoredFileName = $"{attachment.Id}_{safeName}";

                // Create attachments directory
                string attachmentsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "multiterminal", "attachments", taskId);

                // CA3003: attachmentsDir is built from %APPDATA% + app-generated taskId (GUID);
                // storedFileName is prefixed with the attachment's own generated Id and the
                // user-supplied name is stripped to GetFileName only — no traversal reachable.
#pragma warning disable CA3003
                if (!Directory.Exists(attachmentsDir))
                {
                    Directory.CreateDirectory(attachmentsDir);
                }

                // Write file to disk
                string filePath = Path.Combine(attachmentsDir, attachment.StoredFileName);
                File.WriteAllBytes(filePath, imageData);
#pragma warning restore CA3003

                // Persist metadata to database
                _taskDb.SaveAttachment(attachment);

                LogInfo($"Added attachment {attachment.Id} ({fileName}, {imageData.Length} bytes) to task {taskId}");

                // Notify UI
                BroadcastTaskUpdate();

                // Send push notification for the attachment
                var taskTitle = _tasks.TryGetValue(taskId, out var attachTask) ? attachTask.Title : taskId;
                var target = checklistItemIndex >= 0 ? $"checklist item #{checklistItemIndex}" : "ticket";
                RecordNotification("image_attached",
                    $"Image attached to {target}",
                    $"\"{fileName}\" added to: {taskTitle}",
                    sessionId: null, agentName: addedBy, cwd: null);

                return new AddAttachmentResult { Success = true, AttachmentId = attachment.Id };
            }
            catch (Exception ex)
            {
                LogError($"Failed to add attachment to task {taskId}: {ex.Message}");
                return new AddAttachmentResult { Success = false, Error = $"Failed to add attachment: {ex.Message}" };
            }
        }

        /// <summary>
        /// Delete an attachment by ID. Removes both the file from disk and the database record.
        /// </summary>
        /// <param name="attachmentId">The attachment ID to delete.</param>
        /// <returns>Result indicating success or failure.</returns>
        public DeleteAttachmentResult DeleteAttachment(string attachmentId)
        {
            try
            {
                // Get attachment metadata
                var attachment = _taskDb.GetAttachmentById(attachmentId);
                if (attachment == null)
                {
                    return new DeleteAttachmentResult { Success = false, Error = $"Attachment not found: {attachmentId}" };
                }

                // Delete file from disk
                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "multiterminal", "attachments", attachment.TaskId, attachment.StoredFileName);

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                // Delete database record
                _taskDb.DeleteAttachment(attachmentId);

                LogInfo($"Deleted attachment {attachmentId} ({attachment.FileName}) from task {attachment.TaskId}");

                // Notify UI
                BroadcastTaskUpdate();

                return new DeleteAttachmentResult { Success = true };
            }
            catch (Exception ex)
            {
                LogError($"Failed to delete attachment {attachmentId}: {ex.Message}");
                return new DeleteAttachmentResult { Success = false, Error = $"Failed to delete attachment: {ex.Message}" };
            }
        }

        /// <summary>
        /// Get attachments for a task, optionally filtered by checklist item index.
        /// </summary>
        /// <param name="taskId">The task ID to get attachments for.</param>
        /// <param name="checklistItemIndex">If provided, only return attachments for this checklist item. If null, returns all.</param>
        /// <returns>List of attachments.</returns>
        public List<TaskAttachment> GetAttachments(string taskId, int? checklistItemIndex = null)
        {
            return _taskDb.GetAttachments(taskId, checklistItemIndex);
        }

        /// <summary>
        /// Get attachment file data by attachment ID.
        /// Returns the raw bytes, MIME type, and original file name, or null if not found.
        /// </summary>
        /// <param name="attachmentId">The attachment ID.</param>
        /// <returns>Tuple of (bytes, mimeType, fileName) or null if attachment not found.</returns>
        public (byte[] Data, string MimeType, string FileName)? GetAttachmentData(string attachmentId)
        {
            try
            {
                var attachment = _taskDb.GetAttachmentById(attachmentId);
                if (attachment == null)
                {
                    return null;
                }

                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "multiterminal", "attachments", attachment.TaskId, attachment.StoredFileName);

                if (!File.Exists(filePath))
                {
                    LogError($"Attachment file not found on disk: {filePath}");
                    return null;
                }

                byte[] data = File.ReadAllBytes(filePath);
                return (data, attachment.MimeType, attachment.FileName);
            }
            catch (Exception ex)
            {
                LogError($"Failed to read attachment data for {attachmentId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clean up attachments when a checklist item is deleted.
        /// Deletes attachments for the deleted item and adjusts indexes for items after it.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="deletedItemIndex">The index of the checklist item that was deleted.</param>
        public void CleanupChecklistItemAttachments(string taskId, int deletedItemIndex)
        {
            try
            {
                // Get attachments for the deleted item so we can clean up files
                var attachmentsToDelete = _taskDb.GetAttachments(taskId, deletedItemIndex);

                // Delete files from disk
                foreach (var attachment in attachmentsToDelete)
                {
                    string filePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "multiterminal", "attachments", taskId, attachment.StoredFileName);

                    if (File.Exists(filePath))
                    {
                        try { File.Delete(filePath); }
                        catch (Exception ex)
                        {
                            LogError($"Failed to delete attachment file {filePath}: {ex.Message}");
                        }
                    }
                }

                // Delete database records for the deleted item
                _taskDb.DeleteAttachmentsForChecklistItem(taskId, deletedItemIndex);

                // Adjust indexes for items after the deleted one
                _taskDb.UpdateAttachmentIndexes(taskId, deletedItemIndex);

                LogInfo($"Cleaned up {attachmentsToDelete.Count} attachments for checklist item {deletedItemIndex} on task {taskId}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to cleanup checklist item attachments for task {taskId}, item {deletedItemIndex}: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up all attachments when a task is deleted.
        /// Deletes all attachment files and database records for the task.
        /// </summary>
        /// <param name="taskId">The task ID being deleted.</param>
        private void CleanupTaskAttachments(string taskId)
        {
            try
            {
                // Delete all database records for this task
                _taskDb.DeleteAttachmentsForTask(taskId);

                // Delete the attachments directory for this task
                string attachmentsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "multiterminal", "attachments", taskId);

                // CA3003: attachmentsDir is built from %APPDATA% + app-generated taskId (GUID) —
                // taskId is a KanbanTask primary key, never attacker-supplied free text.
#pragma warning disable CA3003
                if (Directory.Exists(attachmentsDir))
                {
                    Directory.Delete(attachmentsDir, recursive: true);
                }
#pragma warning restore CA3003

                LogInfo($"Cleaned up attachments directory for deleted task {taskId}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to cleanup attachments for task {taskId}: {ex.Message}");
            }
        }

        #endregion

        #region Message Images

        /// <summary>
        /// Save a batch of images sent via chat message. Returns the batch ID.
        /// </summary>
        public string SaveMessageImages(List<MessageImageInput> images)
        {
            return _taskDb.SaveMessageImages(images);
        }

        /// <summary>
        /// Retrieve all images in a batch by batch ID.
        /// </summary>
        public List<MessageImage> GetMessageImages(string batchId)
        {
            return _taskDb.GetMessageImages(batchId);
        }

        #endregion

        #region Elicitation Relay

        /// <summary>
        /// Store a pending elicitation request from the hook. Auto-cleans expired entries.
        /// </summary>
        public void StoreElicitation(ElicitationRequest request)
        {
            CleanExpiredElicitations();
            _pendingElicitations[request.ElicitationId] = request;
        }

        /// <summary>
        /// Submit a response to a pending elicitation (from ClaudeRemote).
        /// </summary>
        public bool SubmitElicitationResponse(string elicitationId, ElicitationResponse response)
        {
            if (!_pendingElicitations.TryGetValue(elicitationId, out var request))
                return false;
            _elicitationResponses[elicitationId] = response;

            // Notify the originating agent that the elicitation was answered
            _ = Task.Run(async () =>
            {
                try
                {
                    var summary = response.Action == "accept"
                        ? $"[ELICITATION_RESPONSE:{elicitationId}] User accepted. Values: {response.ContentJson}"
                        : $"[ELICITATION_RESPONSE:{elicitationId}] User {response.Action}d the form.";
                    await SendMessage("ClaudeRemote", request.AgentName, summary);
                }
                catch { /* best-effort notification */ }
            });

            return true;
        }

        /// <summary>
        /// Poll for an elicitation response. Returns null if not yet answered.
        /// </summary>
        public ElicitationResponse GetElicitationResponse(string elicitationId)
        {
            CleanExpiredElicitations();
            _elicitationResponses.TryGetValue(elicitationId, out var response);
            return response;
        }

        /// <summary>
        /// Get a pending elicitation by ID.
        /// </summary>
        public ElicitationRequest GetElicitation(string elicitationId)
        {
            _pendingElicitations.TryGetValue(elicitationId, out var request);
            return request;
        }

        /// <summary>
        /// Remove expired elicitations (older than 5 minutes).
        /// </summary>
        private void CleanExpiredElicitations()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (var kvp in _pendingElicitations)
            {
                if (kvp.Value.CreatedAt < cutoff)
                {
                    _pendingElicitations.TryRemove(kvp.Key, out _);
                    _elicitationResponses.TryRemove(kvp.Key, out _);
                }
            }
        }

        #endregion

        #region Push Notifications for Messages

        /// <summary>
        /// Fire-and-forget push notification to owner's phone when a message is sent.
        /// Posts to ClaudeRemote (port 5100) which forwards as a Web Push notification.
        /// </summary>
        private async Task ForwardMessagePushAsync(string fromName, string messageContent)
        {
            // remoteMode gate — no phone pushes when user is at the desk.
            if (!IsRemoteMode) return;

            // Don't notify the owner about their own messages sent from the phone.
            // The phone proxy registers as "MultiRemote" (post-rename) — older "ClaudeRemote" kept for migration safety.
            if (string.Equals(fromName, "MultiRemote", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromName, "ClaudeRemote", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // Truncate message for notification preview
                var preview = messageContent?.Length > 200
                    ? messageContent.Substring(0, 200) + "..."
                    : messageContent ?? "";

                var payload = new
                {
                    id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    notification_type = "message",
                    title = $"Message from {fromName}",
                    message = preview,
                    agent_name = fromName,
                    created_at = DateTime.UtcNow.ToString("o")
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                await _httpClient.PostAsync("http://localhost:5100/api/notifications/runtime", content);
            }
            catch (Exception ex)
            {
                LogTrace($"ClaudeRemote push failed: {ex.Message}");
            }
        }

        #endregion

        // CA1063: full Dispose(bool) template. Other IDisposable services in this codebase still
        // use the simpler one-method form pending a bulk sweep — see follow-up task f74c0ab0.
        // MainForm currently does not call this Dispose; that wiring is also part of f74c0ab0.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _taskDb?.Dispose();
                _projectDb?.Dispose();
                _messageQueueDb?.Dispose();
            }
            _isDisposed = true;
        }
    }

    /// <summary>
    /// Event args for browser tab requests (open, update, close).
    /// </summary>
    public class BrowserTabEventArgs : EventArgs
    {
        public string Action { get; set; }  // "open", "update", "close", "execute_script", "get_console_logs", "get_element_content", "post_message", "get_messages", "capture_screenshot"
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string HtmlContent { get; set; }

        // For execute_script
        public string Script { get; set; }

        // For get_element_content
        public string Selector { get; set; }
        public string Property { get; set; }  // textContent, innerHTML, outerHTML, value

        // For get_console_logs / get_messages
        public int? Limit { get; set; }

        // For post_message
        public string MessageData { get; set; }

        // For async results — the controller waits for the UI thread result
        public TaskCompletionSource<string> ResultTcs { get; set; }
    }

    /// <summary>
    /// Event args for an external terminal input-injection request
    /// (POST /api/terminals/inject → MessageBroker.RequestTerminalInject). Task be599e08.
    /// </summary>
    public class TerminalInjectEventArgs : EventArgs
    {
        public string AgentName { get; set; }
        public string SessionId { get; set; }
        public string Text { get; set; }

        /// <summary>
        /// How the subscriber should deliver <see cref="Text"/>:
        /// <c>"clear-trigger"</c> (default) routes through the deduped post-/clear
        /// session-start trigger (task be599e08); <c>"submit"</c> types the text and
        /// presses Enter as a normal prompt submission via the terminal's
        /// InjectInputAsync — used by the self-clear MCP tool to submit "/clear"
        /// (task 1d6e599d). Null/empty is treated as "clear-trigger" for back-compat.
        /// </summary>
        public string Kind { get; set; }
    }

    /// <summary>
    /// Event args for inbox updates.
    /// </summary>
    public class InboxUpdatedEventArgs : EventArgs
    {
        public string UserId { get; set; }
        public InboxMessage Message { get; set; }
        public int UnreadCount { get; set; }
    }

    public class BranchOutcomeUpdatedEventArgs : EventArgs
    {
        public string ProjectId { get; set; }
        public string BranchName { get; set; }
    }

    /// <summary>
    /// Event args for plan updates.
    /// </summary>
    public class PlanUpdateEventArgs : EventArgs
    {
        public string PlanId { get; set; }
        public string UpdateType { get; set; }
        public string Details { get; set; }
        public string TriggeredBy { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Event args for task claimed notifications.
    /// </summary>
    public class TaskClaimedEventArgs : EventArgs
    {
        public string TaskId { get; set; }
        public string TaskTitle { get; set; }
        public string ClaimedBy { get; set; }
    }

    /// <summary>
    /// Event args for report saved notifications.
    /// </summary>
    public class ReportSavedEventArgs : EventArgs
    {
        public string TaskId { get; set; }
        public string ReportId { get; set; }
        public string AgentName { get; set; }
        public string Verdict { get; set; }
    }

    /// <summary>
    /// Event args for <see cref="MessageBroker.TaskActiveChanged"/>. Fired after
    /// <see cref="MessageBroker.SetTaskActive"/> has paused the previous active
    /// task (if any) and materialized the new task's worktree. Subscribers (today:
    /// MainForm) push a corresponding system message into the agent's Claude Code
    /// Channel so the auto-cd hook can react.
    ///
    /// <para><see cref="OldWorktreePath"/> is null when the agent had no prior
    /// active task; <see cref="NewWorktreePath"/> is null when the new task
    /// didn't materialize a worktree (worktree mode off, project unregistered,
    /// or git failure — all non-fatal).</para>
    /// </summary>
    public class TaskActiveChangedEventArgs : EventArgs
    {
        // Constructor-set immutable args. Cycle-6 codex-security/adversary
        // MEDIUM fix: prior mutable-property shape let an earlier in-proc
        // subscriber rewrite AgentName/OldTaskId/etc before later subscribers
        // ran, redirecting both the HUD rebind path (TerminalDocument) and
        // the channel push (MainForm) at attacker-chosen identity/paths.
        // Mirrors the WorktreeReadyEventArgs hardening from cycle-5.
        public TaskActiveChangedEventArgs(
            string agentName,
            string oldTaskId,
            string oldWorktreePath,
            string newTaskId,
            string newWorktreePath)
        {
            AgentName = agentName;
            OldTaskId = oldTaskId;
            OldWorktreePath = oldWorktreePath;
            NewTaskId = newTaskId;
            NewWorktreePath = newWorktreePath;
        }

        public string AgentName { get; }

        public string OldTaskId { get; }

        public string OldWorktreePath { get; }

        public string NewTaskId { get; }

        public string NewWorktreePath { get; }
    }

    /// <summary>
    /// Event args for <see cref="MessageBroker.WorktreePruning"/>. Fired just
    /// before <see cref="WorktreeManager.PruneForTaskAsync"/> in the task-done
    /// flow so agents with cwd inside the worktree can evict before git tries
    /// the rmdir. <see cref="AgentName"/> is the task assignee (may be null
    /// for unowned tasks); subscribers broadcast to ALL live terminals
    /// regardless, because subagents/helpers may also hold cwd in the
    /// worktree even though they aren't the assignee.
    /// </summary>
    public class WorktreePruningEventArgs : EventArgs
    {
        public string TaskId { get; set; }

        public string WorktreePath { get; set; }

        public string RepoRoot { get; set; }

        public string AgentName { get; set; }

        /// <summary>
        /// Subscriber-writable. Default <c>true</c>. The MainForm subscriber
        /// sets this to <c>false</c> when the channel-delivery 1.5s timeout
        /// wins the race against <c>Task.WhenAll(deliveries)</c> — i.e. at
        /// least one terminal didn't acknowledge the broadcast in time.
        /// Broker reads this after firing and DEFERS the prune (leaves the
        /// worktree active) so the janitor can retry once agents have likely
        /// moved on. Cycle-3 adversary HIGH fix.
        /// </summary>
        public bool AllDelivered { get; set; } = true;
    }

    /// <summary>
    /// Event args for <see cref="MessageBroker.WorktreeReady"/>. Fired once per
    /// task-done attempt AFTER both prune and auto-merge complete. Tells HUD
    /// panels "the new post-merge repo state is durable, refresh your view."
    /// Fields mirror <see cref="WorktreePruningEventArgs"/> (same task context)
    /// so subscribers don't need a different shape to react to both events.
    ///
    /// <para>Immutable so a misbehaving earlier subscriber can't rewrite
    /// <see cref="WorktreePath"/> or <see cref="RepoRoot"/> and redirect a
    /// later subscriber's GitRepos cache eviction / SetProject call at a
    /// different repository (codex-security-auditor MEDIUM cycle-5 fix).</para>
    /// </summary>
    public class WorktreeReadyEventArgs : EventArgs
    {
        public WorktreeReadyEventArgs(string taskId, string worktreePath, string repoRoot, string agentName)
        {
            TaskId = taskId;
            WorktreePath = worktreePath;
            RepoRoot = repoRoot;
            AgentName = agentName;
        }

        public string TaskId { get; }

        public string WorktreePath { get; }

        public string RepoRoot { get; }

        public string AgentName { get; }
    }

    /// <summary>
    /// fa1101db R4 — args for a permission-relay push request. Fired by PermissionRelayService
    /// AFTER the Cloudflare Worker confirms a relay request was stored (PostCreateAsync returned a
    /// non-empty workerId), so the gateway host can wake the phone with a VAPID web-push instead of
    /// the user having to open the Permissions tab to discover the card. Immutable (constructor-set,
    /// get-only) so an earlier in-proc subscriber can't rewrite the push title/body before later
    /// subscribers run — mirrors the WorktreeReadyEventArgs / TaskActiveChangedEventArgs hardening.
    /// </summary>
    public class PermissionRelayPushEventArgs : EventArgs
    {
        public PermissionRelayPushEventArgs(string requestType, string agentName, string title, string body)
        {
            RequestType = requestType;
            AgentName = agentName;
            Title = title;
            Body = body;
        }

        /// <summary>One of: choice, elicitation, plan_approval, notification. Maps to the PWA's notification_type.</summary>
        public string RequestType { get; }

        public string AgentName { get; }

        public string Title { get; }

        public string Body { get; }
    }

    /// <summary>
    /// Project with associated task count for UI display.
    /// </summary>
    public class ProjectWithCount
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int TaskCount { get; set; }
    }

    /// <summary>
    /// Lightweight info for an office agent (for Office Panel animation only).
    /// These are NOT registered terminals - just visual representations of spawned subagents.
    /// </summary>
    public class OfficeAgentInfo
    {
        public string Name { get; set; }
        public string SpawnedBy { get; set; }
        public DateTime SpawnedAt { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// Result for office agent spawn/depart operations.
    /// </summary>
    public class OfficeAgentResult
    {
        public bool Success { get; set; }
        public string AgentName { get; set; }
        public string Error { get; set; }
    }
}
