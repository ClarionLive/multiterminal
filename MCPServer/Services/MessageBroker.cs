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

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Routes messages between terminals and maintains message queues.
    /// Supports SQLite persistence for reliable delivery with retry.
    /// </summary>
    public class MessageBroker : IDisposable
    {
        private bool _isDisposed;

        private readonly ConcurrentDictionary<string, TerminalInfo> _terminals = new ConcurrentDictionary<string, TerminalInfo>();
        private readonly ConcurrentDictionary<string, BlockingCollection<Message>> _messageQueues = new ConcurrentDictionary<string, BlockingCollection<Message>>();
        private readonly List<Message> _messageHistory = new List<Message>();
        private readonly object _historyLock = new object();
        private const int MaxHistorySize = 1000;

        // Kanban task storage
        private readonly ConcurrentDictionary<string, KanbanTask> _tasks = new ConcurrentDictionary<string, KanbanTask>();
        private readonly TaskDatabase _taskDb;
        private readonly MultiTerminal.Services.WorktreeManager _worktrees;
        private readonly MultiTerminal.Services.WorktreeAutoCommitService _autoCommit;
        private readonly MultiTerminal.Services.WorktreeMergeService _merge;
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
            AgentPanelCloseRequested?.Invoke(this, transcriptPath);
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
            ReportSaved?.Invoke(this, new ReportSavedEventArgs
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
            NotificationReceived?.Invoke(this, payload);
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
            SessionLineageUpdated?.Invoke(this, sessionId);
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
            string markedPath = null;
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
                    // Task no longer wants pruning. Clear any stale defer-mark
                    // so spawns can resume in the worktree.
                    WorktreePruneCoordinator.UnmarkPruning(record.WorktreePath);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MessageBroker] Deferred prune for task {taskId} cancelled — task no longer 'done'. Released defer-mark.");
                    return false;
                }

                string projectPath = TryGetProjectPathForTask(taskId);
                if (string.IsNullOrEmpty(projectPath)) return false;

                markedPath = record.WorktreePath;
                WorktreePruneCoordinator.MarkPruning(markedPath); // idempotent; broker already marked at defer time
                bool removed = await _worktrees.PruneForTaskAsync(taskId, projectPath).ConfigureAwait(false);
                clearMarkOnExit = removed; // only unmark on success
                return removed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] TryDeferredPruneRetryAsync({taskId}) threw: {ex.Message}");
                return false;
            }
            finally
            {
                if (clearMarkOnExit && !string.IsNullOrEmpty(markedPath))
                {
                    WorktreePruneCoordinator.UnmarkPruning(markedPath);
                }
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
                WorktreePruning?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MessageBroker] WorktreePruning subscribers threw: {ex.Message}");
            }
            return args;
        }

        /// <summary>
        /// Fires BranchOutcomeUpdated. Called by BranchMetadataService after a successful
        /// upsert so subscribers (HudGitRenderer) can refresh.
        /// </summary>
        public void FireBranchOutcomeUpdated(string projectId, string branchName)
        {
            BranchOutcomeUpdated?.Invoke(this, new BranchOutcomeUpdatedEventArgs
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
                var activeTask = GetMyActiveTask(terminalName);
                if (activeTask == null) return null;
                string candidate = _worktrees?.GetWorktreePathForTask(activeTask.Id);
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
                var activeTask = GetMyActiveTask(terminalName);
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
        /// Project service for managing .claude/project.json files.
        /// Set via DI after broker is created.
        /// </summary>
        public MultiTerminal.Services.ProjectService ProjectService { get; set; }

        /// <summary>
        /// Initialize the message broker and load persisted tasks and projects.
        /// </summary>
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

            _worktrees = new MultiTerminal.Services.WorktreeManager(_taskDb);
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
                    docId = null; // Clear the stolen docId so it falls through to fresh registration below
                    existingByDocId = null;
                }
                else
                {
                // Terminal is renaming (e.g., "Unassigned" → "Bob")
                string oldName = existingByDocId.Name;
                string newName = name;

                System.Diagnostics.Debug.WriteLine($"[MessageBroker] Terminal renaming: {oldName} → {newName} (DocId: {docId})");

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
                TerminalRegistered?.Invoke(this, existingByDocId);

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
                // Always re-raise event so MainForm updates its mapping
                TerminalRegistered?.Invoke(this, existingByName);

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
                TerminalRegistered?.Invoke(this, terminal);

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
                TerminalDisconnected?.Invoke(this, terminal);

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
                TerminalDisconnected?.Invoke(this, terminal);
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
        ///     - NotificationsController.ForwardToClaudeRemoteAsync
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
            // Idempotent — skip the write if the value isn't changing. Avoids rewriting
            // settings.txt on every UserPromptSubmit hook fire + every phone X-Source ping.
            if (IsRemoteMode == enabled) return;
            SettingsService.Default.Set(SettingRemoteMode, enabled ? "1" : "0");
            RemoteModeChanged?.Invoke(this, enabled);
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
            MessageSent?.Invoke(this, message);

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
            MessageSent?.Invoke(this, message);

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

                MessageSent?.Invoke(this, message);

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

                MessageSent?.Invoke(this, message);

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

            MessageSent?.Invoke(this, message);

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

            MessageSent?.Invoke(this, message);

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

            MessageSent?.Invoke(this, message);

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
        /// 1. null / whitespace input → null.
        /// 2. exact key match found:
        ///    a. AND a longer key also starts with input AND input.Length &lt; 32
        ///       (so input itself is shorter than a canonical GUID) → null
        ///       + debug warning ("ambiguous, returning null").
        ///    b. otherwise → trimmed (caller intended this exact key).
        /// 3. input shorter than 4 chars → trimmed (don't prefix-match noise).
        /// 4. exactly one key starts with input (OrdinalIgnoreCase) → full key.
        /// 5. zero or multiple prefix-matches → trimmed + debug warning.
        /// </summary>
        private string NormalizeProjectId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
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

            System.Diagnostics.Debug.WriteLine(
                $"[MessageBroker] NormalizeProjectId: '{trimmed}' had {prefixOnlyHits} prefix matches in _projects; storing raw value.");
            return trimmed;
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
                        Error = $"Project id '{projectId}' is ambiguous (matches a short key that also prefixes longer keys). Pass the full id."
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
                ProjectId = canonicalProjectId
            };

            if (_tasks.TryAdd(task.Id, task))
            {
                // Persist to database
                try { _taskDb.SaveTask(task); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save task: {ex.Message}"); }

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
            TaskClaimed?.Invoke(this, new TaskClaimedEventArgs
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
            if (status == "done"
                && MultiTerminal.Services.WorktreeConfig.IsEnabled
                && !string.IsNullOrEmpty(task.ProjectId)
                && _projects.TryGetValue(task.ProjectId, out var doneProj)
                && !string.IsNullOrEmpty(doneProj.Path))
            {
                bool shouldPrune = false;
                MultiTerminal.Services.AutoCommitResult commitResult = null;
                try
                {
                    commitResult = _autoCommit.CommitForTaskAsync(
                        taskId,
                        doneProj.Path,
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

                bool prunedOK = false;
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
                    string worktreePathToPrune = _worktrees?.GetWorktreePathForTask(taskId);
                    bool marked = false;
                    bool deferred = false;
                    if (!string.IsNullOrEmpty(worktreePathToPrune))
                    {
                        WorktreePruneCoordinator.MarkPruning(worktreePathToPrune);
                        marked = true;
                        var fireArgs = FireWorktreePruning(taskId, worktreePathToPrune, doneProj.Path, task.Assignee);

                        // Cycle-3 adversary HIGH fix: if the broadcast didn't
                        // complete in time, DEFER prune — leave the worktree
                        // active so the janitor's deferred-prune pass retries
                        // once agents have likely moved on. Pruning while
                        // agents still hold cwd reduces to the partial-prune
                        // fallback + empty shell, which Pass 3 has to clean
                        // up anyway. Skipping it removes that window.
                        deferred = !fireArgs.AllDelivered;
                    }

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
                            _worktrees.PruneForTaskAsync(taskId, doneProj.Path).GetAwaiter().GetResult();
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
                        WorktreePruneCoordinator.UnmarkPruning(worktreePathToPrune);
                    }
                }

                // Phase 3 auto-merge — fires only after a successful prune.
                // Prune releases the branch lock so git can merge the task
                // branch into the main checkout's current trunk. Merge failure
                // does NOT roll back commit or prune (those are durable); the
                // dev resolves the merge manually.
                if (prunedOK)
                {
                    string taskIdShort = taskId.Substring(0, Math.Min(8, taskId.Length));
                    try
                    {
                        var mergeResult = _merge.MergeForTaskAsync(taskId, doneProj.Path).GetAwaiter().GetResult();

                        if (mergeResult.Success)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MessageBroker] Auto-merge for {taskId}: " +
                                (mergeResult.SkippedReason ?? $"merged into {mergeResult.MergedInto}"));

                            if (!string.IsNullOrEmpty(mergeResult.SkippedReason))
                            {
                                RecordActivity(new ActivityEvent
                                {
                                    Terminal = task.Assignee ?? "System",
                                    Type = "worktree",
                                    Action = "auto_merge_skipped",
                                    Content = $"Auto-merge skipped for '{task.Title}': {mergeResult.SkippedReason}",
                                    RelatedId = taskId
                                });
                            }
                            else
                            {
                                RecordActivity(new ActivityEvent
                                {
                                    Terminal = task.Assignee ?? "System",
                                    Type = "worktree",
                                    Action = "auto_merge",
                                    Content = $"Merged task branch into {mergeResult.MergedInto} for '{task.Title}'; task branch deleted.",
                                    RelatedId = taskId
                                });
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[MessageBroker] Auto-merge for {taskId} FAILED" +
                                (mergeResult.HadConflicts ? " (conflict)" : "") +
                                $": {mergeResult.Stderr}");
                            string conflictTag = mergeResult.HadConflicts ? "Merge conflict" : "Merge failed";
                            RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "auto_merge_failed",
                                Content = $"{conflictTag} for '{task.Title}'. Task branch task/{taskIdShort} preserved with auto-committed changes; worktree dir was removed (necessary for the merge attempt). To resolve: run `git merge task/{taskIdShort}` in the main checkout, or re-create a worktree from the branch and re-mark the task done to retry. Janitor will keep flagging this each sweep until resolved.",
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
                            Content = $"Auto-merge threw for '{task.Title}'. Task branch task/{taskIdShort} preserved with auto-committed changes; worktree dir was removed (necessary for the merge attempt). To resolve: run `git merge task/{taskIdShort}` in the main checkout, or re-create a worktree from the branch and re-mark the task done to retry. Janitor will keep flagging this each sweep until resolved.",
                            RelatedId = taskId
                        });
                    }
                }
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
        /// Update a task's title and description.
        /// </summary>
        public UpdateTaskResult UpdateTask(string taskId, string title, string description, string updatedBy = null, string plan = null, string implementationSummary = null, string testResults = null, string implementationChecklistJson = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

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
        /// Update a task's checklist JSON.
        /// </summary>
        public UpdateTaskResult UpdateTaskChecklist(string taskId, string checklistJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            task.ChecklistJson = checklistJson ?? "[]";

            // Auto-derive parent task status from checklist item positions
            RecalculateAutoStatus(task);

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task checklist: {ex.Message}"); }

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
                        Error = $"Project id '{projectId}' is ambiguous (matches a short key that also prefixes longer keys). Pass the full id."
                    };
                }
                task.ProjectId = canonical;
            }

            // Persist to database
            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to update task project: {ex.Message}"); }

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

            var checklist = task.GetChecklist();
            if (itemIndex < 0 || itemIndex >= checklist.Count)
            {
                return new UpdateChecklistItemResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
            }

            var item = checklist[itemIndex];
            var previousStatus = item.Status ?? "pending";

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

            var checklist = task.GetChecklist();
            if (itemIndex < 0 || itemIndex >= checklist.Count)
            {
                return new UpdateTaskResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
            }

            checklist[itemIndex].AssignedTo = assignee;
            task.SetChecklist(checklist);

            try { _taskDb.SaveTask(task); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to save checklist assignment: {ex.Message}"); }

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
                        oldWorktreePath = _worktrees?.GetWorktreePathForTask(other.Id);
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

            // Phase 1 worktree isolation — gated by MULTITERMINAL_WORKTREE_MODE.
            // Materialize a fresh worktree on `git worktree add` so subsequent agent
            // spawns can be rooted there. Idempotent: returns the existing record
            // when one already exists. Failure is non-fatal — the task still goes
            // active so the user is not blocked by git issues.
            //
            // Normalize ProjectId first: legacy tasks may have a truncated 8-char
            // short id stored (silent footgun before the CreateTask fix). If we
            // can resolve it to a full key, opportunistically self-heal the row
            // so future lookups succeed without repeating the prefix scan.
            if (MultiTerminal.Services.WorktreeConfig.IsEnabled
                && !string.IsNullOrEmpty(task.ProjectId))
            {
                string canonicalProjectId = NormalizeProjectId(task.ProjectId);
                bool canCreateWorktree = true;

                if (!string.IsNullOrEmpty(canonicalProjectId)
                    && !string.Equals(canonicalProjectId, task.ProjectId, StringComparison.Ordinal))
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

                if (canCreateWorktree
                    && !string.IsNullOrEmpty(canonicalProjectId)
                    && _projects.TryGetValue(canonicalProjectId, out var activeProj)
                    && !string.IsNullOrEmpty(activeProj.Path))
                {
                    try
                    {
                        _worktrees.CreateForTaskAsync(taskId, activeProj.Path).GetAwaiter().GetResult();
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
                else if (canCreateWorktree)
                {
                    // Worktree mode is on AND the task claims a project, but the
                    // project couldn't be resolved. Distinguish the three causes
                    // so the user can act on the right one without reading debug
                    // logs: ambiguous input (caller fix), cold cache or missing
                    // registration (config fix), or registered-without-path
                    // (project record fix).
                    string skipReason;
                    if (string.IsNullOrEmpty(canonicalProjectId))
                    {
                        skipReason = "id is ambiguous; pass the full project id";
                    }
                    else if (!_projects.ContainsKey(canonicalProjectId))
                    {
                        skipReason = "project not registered or registry not yet loaded";
                    }
                    else
                    {
                        skipReason = "project is registered but has no filesystem path";
                    }

                    RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "create_skipped",
                        Content = $"Worktree creation skipped for '{task.Title}': project '{task.ProjectId}' could not be resolved ({skipReason}).",
                        RelatedId = taskId
                    });
                }
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
            string newWorktreePath = _worktrees?.GetWorktreePathForTask(taskId);
            try
            {
                TaskActiveChanged?.Invoke(this, new TaskActiveChangedEventArgs
                {
                    AgentName = assignee,
                    OldTaskId = oldTaskId,
                    OldWorktreePath = oldWorktreePath,
                    NewTaskId = taskId,
                    NewWorktreePath = newWorktreePath,
                });
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
        /// Broadcast task update to all listeners.
        /// </summary>
        private void BroadcastTaskUpdate()
        {
            var tasks = GetTasks();
            TasksUpdated?.Invoke(this, tasks);
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
        /// Delete a project. Associated tasks are not deleted.
        /// </summary>
        public DeleteProjectResult DeleteProject(string projectId, string deletedBy)
        {
            if (!_projects.TryRemove(projectId, out var project))
            {
                return new DeleteProjectResult { Success = false, Error = $"Project not found: {projectId}" };
            }

            // Delete from database
            try { _projectDb.DeleteProject(projectId); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MessageBroker] Failed to delete project: {ex.Message}"); }

            BroadcastProjectUpdate();

            // Record activity for the feed
            RecordActivity(new ActivityEvent
            {
                Terminal = deletedBy ?? "System",
                Type = "project",
                Action = "deleted",
                Content = $"Deleted project: {project.Name}",
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
            ProjectsUpdated?.Invoke(this, projects);
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
            ProfilesUpdated?.Invoke(this, profiles);
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
            HelperMessageLogged?.Invoke(this, helperMessage);

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
            HelperSessionUpdated?.Invoke(this, session);
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

            OfficeAgentSpawned?.Invoke(this, agent);

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
                OfficeAgentDeparted?.Invoke(this, agent);
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
                OfficeAgentDeparted?.Invoke(this, matchedAgent);
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

            ActivityRecorded?.Invoke(this, activity);

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
            MessageSent?.Invoke(this, message);
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

            PlanUpdated?.Invoke(this, args);

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
                InboxUpdated?.Invoke(this, new InboxUpdatedEventArgs
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
                InboxUpdated?.Invoke(this, new InboxUpdatedEventArgs
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

                InboxUpdated?.Invoke(this, new InboxUpdatedEventArgs
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

                InboxUpdated?.Invoke(this, new InboxUpdatedEventArgs
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

            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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

            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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

            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
            BrowserTabRequested?.Invoke(this, new BrowserTabEventArgs
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
        public string AgentName { get; set; }

        public string OldTaskId { get; set; }

        public string OldWorktreePath { get; set; }

        public string NewTaskId { get; set; }

        public string NewWorktreePath { get; set; }
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
