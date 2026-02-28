using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.ActivityPanel
{
    /// <summary>
    /// Dockable document for the Activity Panel - "Mission Control" for team status.
    /// </summary>
    public class ActivityPanelDocument : DockContent
    {
        private ActivityPanelRenderer _renderer;
        private ActivityService _activityService;
        private PoolCoordinator _poolCoordinator;
        private MessageBroker _messageBroker;
        private TaskDatabase _taskDatabase;
        private bool _isDarkTheme = true;
        private System.Windows.Forms.Timer _refreshTimer;
        private DateTime _lastEventUpdate = DateTime.MinValue;
        private bool _hasLoadedInitialData = false;
        private HashSet<string> _displayedFeedItemIds = new HashSet<string>();
        private HashSet<string> _knownTaskIds = new HashSet<string>();

        public ActivityPanelDocument()
        {
            InitializeComponent();

            // Hook visibility change to handle lazy-loading when panel is first shown
            this.DockStateChanged += OnDockStateChanged;
        }

        private void InitializeComponent()
        {
            Text = "Activity Monitor";
            TabText = "Activity";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _renderer = new ActivityPanelRenderer
            {
                Dock = DockStyle.Fill
            };

            // Subscribe to Ready event to load data when WebView2 is ready
            _renderer.Ready += OnRendererReady;
            _renderer.RefreshRequested += OnRefreshRequested;

            Controls.Add(_renderer);
        }

        private void OnRendererReady(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] OnRendererReady fired, _activityService={((_activityService == null) ? "NULL" : "OK")}");

            // WebView2 is now ready - load initial data
            RefreshActivity();

            // Periodic refresh timer DISABLED - causes UI flashing even with deduplication
            // Event-driven updates (OnActivityUpdated, OnLearnedMessageRecorded, OnMessageSent, OnTasksUpdated)
            // handle all activity updates. Timer was causing full RefreshActivity() calls that flash the UI.
            // If events are being dropped, re-enable with longer interval (30s) as safety net.
            // if (_refreshTimer == null)
            // {
            //     _refreshTimer = new System.Windows.Forms.Timer();
            //     _refreshTimer.Interval = 30000; // 30 seconds - safety net only
            //     _refreshTimer.Tick += OnRefreshTimerTick;
            //     _refreshTimer.Start();
            //     System.Diagnostics.Debug.WriteLine("[ActivityPanel] Started 30-second refresh timer");
            // }
        }

        private void OnRefreshTimerTick(object sender, EventArgs e)
        {
            // Skip poll if we received an event-driven update within the last 4 seconds
            // This makes the timer a fallback rather than the primary update mechanism
            if ((DateTime.UtcNow - _lastEventUpdate).TotalSeconds < 4)
                return;

            RefreshActivity();
        }

        private void OnRefreshRequested(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ActivityPanel] Manual refresh requested by user");
            RefreshActivity();
        }

        /// <summary>
        /// Handle dock state changes to lazy-load data when panel becomes visible.
        /// WebView2 only initializes when the control is visible, so we need to
        /// trigger data load when the panel is first shown.
        /// </summary>
        private void OnDockStateChanged(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] DockStateChanged: DockState={DockState}, _activityService={(_activityService == null ? "NULL" : "OK")}, _hasLoadedInitialData={_hasLoadedInitialData}");

            // When panel becomes visible and we haven't loaded data yet
            if (DockState != DockState.Hidden && DockState != DockState.Unknown && !_hasLoadedInitialData)
            {
                if (_activityService != null)
                {
                    System.Diagnostics.Debug.WriteLine("[ActivityPanel] Panel now visible with service ready - will load data when WebView2 initializes");
                    // WebView2 will initialize now that we're visible
                    // OnRendererReady will call RefreshActivity when it's ready
                    _hasLoadedInitialData = true;
                }
            }
        }

        /// <summary>
        /// Initialize the activity panel with the ActivityService for real-time updates.
        /// Data will be loaded when WebView2 is ready (via Ready event).
        /// </summary>
        public void Initialize(ActivityService activityService, PoolCoordinator poolCoordinator = null, MessageBroker messageBroker = null, TaskDatabase taskDatabase = null)
        {
            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Initialize called, activityService={(activityService == null ? "NULL" : "OK")}, poolCoordinator={(poolCoordinator == null ? "NULL" : "OK")}, messageBroker={(messageBroker == null ? "NULL" : "OK")}, renderer={((_renderer == null) ? "NULL" : (_renderer.IsInitialized ? "READY" : "NOT_READY"))}");

            _activityService = activityService;
            _poolCoordinator = poolCoordinator;
            _messageBroker = messageBroker;
            _taskDatabase = taskDatabase;

            if (_activityService != null)
            {
                _activityService.ActivityUpdated += OnActivityUpdated;
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] Subscribed to ActivityUpdated events");
            }

            // Subscribe to pool coordinator events for real-time updates
            if (_poolCoordinator != null)
            {
                _poolCoordinator.LearnedMessageRecorded += OnLearnedMessageRecorded;
                _poolCoordinator.PoolMessageRecorded += OnPoolMessageRecorded;
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] Subscribed to LearnedMessageRecorded and PoolMessageRecorded events");
            }

            // Subscribe to message broker events for real-time chat updates
            if (_messageBroker != null)
            {
                _messageBroker.MessageSent += OnMessageSent;
                _messageBroker.TasksUpdated += OnTasksUpdated;
                _messageBroker.ActivityRecorded += OnActivityRecorded;
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] Subscribed to MessageBroker events (including ActivityRecorded)");
            }

            // If renderer is already initialized, load data now
            // Otherwise, OnRendererReady will load it when WebView2 is ready
            if (_renderer?.IsInitialized == true)
            {
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] Renderer already ready, calling RefreshActivity now");
                RefreshActivity();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] Renderer not ready yet, will refresh when Ready event fires");
            }
        }

        /// <summary>
        /// Handle real-time activity updates from the service.
        /// </summary>
        private void OnActivityUpdated(object sender, MCPServer.Models.TerminalActivity activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnActivityUpdated(sender, activity)));
                return;
            }

            // Track when we last received an event-driven update
            _lastEventUpdate = DateTime.UtcNow;

            // Push the single update to the renderer
            _renderer?.PushActivityUpdate(activity);
        }

        /// <summary>
        /// Handle real-time LEARNED message events from the pool coordinator.
        /// </summary>
        private void OnLearnedMessageRecorded(object sender, LearnedMessageEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnLearnedMessageRecorded(sender, e)));
                return;
            }

            _lastEventUpdate = DateTime.UtcNow;

            // Add as a feed item if not already displayed
            if (!_displayedFeedItemIds.Contains(e.MessageId))
            {
                _displayedFeedItemIds.Add(e.MessageId);
                var feedItem = new ActivityFeedItem
                {
                    Id = e.MessageId,
                    Terminal = e.Instance,
                    Type = "learned",
                    Content = e.Topic + (string.IsNullOrEmpty(e.Summary) ? "" : $": {e.Summary}"),
                    Timestamp = e.Timestamp,
                    IsPinned = false
                };
                _renderer?.AddFeedItem(feedItem);
                UpdateMetrics();
            }
        }

        /// <summary>
        /// Handle real-time pool message events from the file watcher.
        /// This enables instant updates when hooks write to state.jsonl.
        /// </summary>
        private void OnPoolMessageRecorded(object sender, PoolMessageEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnPoolMessageRecorded(sender, e)));
                return;
            }

            _lastEventUpdate = DateTime.UtcNow;

            // Skip if already displayed (LEARNED messages are handled by OnLearnedMessageRecorded)
            if (_displayedFeedItemIds.Contains(e.MessageId))
                return;

            // Skip LEARNED - already handled by dedicated handler
            if (e.Action == PoolAction.LEARNED)
                return;

            _displayedFeedItemIds.Add(e.MessageId);

            var feedItem = new ActivityFeedItem
            {
                Id = e.MessageId,
                Terminal = e.Instance,
                Type = e.Action == PoolAction.COMPLETED ? "task" :
                       e.Action == PoolAction.WORKING_ON ? "task" :
                       e.Action == PoolAction.BLOCKED_BY ? "task" : "activity",
                Content = FormatPoolMessageContent(e),
                Timestamp = e.Timestamp,
                IsPinned = false
            };

            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] OnPoolMessageRecorded: {e.Action} from {e.Instance} - {feedItem.Content}");
            _renderer?.AddFeedItem(feedItem);
            UpdateMetrics();
        }

        /// <summary>
        /// Format pool message event content for display.
        /// </summary>
        private string FormatPoolMessageContent(PoolMessageEventArgs e)
        {
            switch (e.Action)
            {
                case PoolAction.COMPLETED:
                    return e.Topic ?? "Completed task";
                case PoolAction.WORKING_ON:
                    return $"Working on: {e.Topic}";
                case PoolAction.BLOCKED_BY:
                    return $"Blocked: {e.Topic} (by {string.Join(", ", e.BlockedBy ?? new List<string>())})";
                default:
                    return e.Topic ?? e.Summary ?? e.Action.ToString();
            }
        }

        /// <summary>
        /// Handle real-time chat message events from the message broker.
        /// </summary>
        private void OnMessageSent(object sender, MCPServer.Models.Message message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnMessageSent(sender, message)));
                return;
            }

            _lastEventUpdate = DateTime.UtcNow;

            // Add as a feed item if not already displayed
            if (!_displayedFeedItemIds.Contains(message.Id))
            {
                _displayedFeedItemIds.Add(message.Id);
                var feedItem = new ActivityFeedItem
                {
                    Id = message.Id,
                    Terminal = message.From,
                    Type = "chat",
                    Content = $"→ {message.To}: {message.Content}",
                    Timestamp = message.Timestamp,
                    IsPinned = false
                };
                _renderer?.AddFeedItem(feedItem);
                UpdateMetrics();
            }
        }

        /// <summary>
        /// Handle task updates from the message broker.
        /// Note: Task creation is now handled by OnActivityRecorded for reliability.
        /// This handler updates metrics and tracks known task IDs.
        /// </summary>
        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTasksUpdated(sender, tasks)));
                return;
            }

            _lastEventUpdate = DateTime.UtcNow;

            // Track all task IDs (for metrics and deduplication, but don't show feed items here)
            foreach (var task in tasks ?? new List<KanbanTask>())
            {
                _knownTaskIds.Add(task.Id);
            }

            UpdateMetrics();
        }

        /// <summary>
        /// Handle activity events from the message broker.
        /// This is the primary handler for all activity feed items (tasks, plans, builds, etc.).
        /// </summary>
        private void OnActivityRecorded(object sender, MCPServer.Models.ActivityEvent activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnActivityRecorded(sender, activity)));
                return;
            }

            _lastEventUpdate = DateTime.UtcNow;

            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] OnActivityRecorded: {activity.Type}/{activity.Action} - {activity.Content}");

            // Create a unique ID for deduplication
            var feedItemId = $"{activity.Type}_{activity.Id}";
            if (_displayedFeedItemIds.Contains(feedItemId))
            {
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Skipping duplicate: {feedItemId}");
                return;
            }

            _displayedFeedItemIds.Add(feedItemId);

            var feedItem = new ActivityFeedItem
            {
                Id = feedItemId,
                Terminal = activity.Terminal ?? "System",
                Type = activity.Type,
                Content = activity.Content,
                Timestamp = activity.Timestamp,
                IsPinned = false
            };

            _renderer?.AddFeedItem(feedItem);
            UpdateMetrics();
        }

        /// <summary>
        /// Update the metrics display.
        /// </summary>
        private void UpdateMetrics()
        {
            if (_renderer == null) return;

            var metrics = new ActivityMetrics();

            // Get task counts from message broker
            if (_messageBroker != null)
            {
                var tasks = _messageBroker.GetTasks();
                metrics.TotalTasks = tasks.Count;
                metrics.CompletedTasks = tasks.Count(t => t.Status == "done");
                metrics.InProgressTasks = tasks.Count(t => t.Status == "in_progress");
                metrics.PendingTasks = tasks.Count(t => t.Status == "todo" || t.Status == "suggestion");
                metrics.MessageCount = _messageBroker.GetMessageHistory(1000).Count;
            }

            // Get learned count from pool coordinator
            if (_poolCoordinator != null)
            {
                var poolMessages = _poolCoordinator.GetRecentMessages(1000);
                metrics.LearnedCount = poolMessages.Count(m => m.Action == PoolAction.LEARNED);
            }

            // Build hourly activity sparkline data (last 12 hours)
            metrics.ActivityData = BuildHourlyActivityData();

            _renderer.ShowMetrics(metrics);
        }

        /// <summary>
        /// Aggregate activity counts per hour for the last 12 hours.
        /// Combines activity feed entries, chat messages, and pool messages.
        /// </summary>
        private int[] BuildHourlyActivityData()
        {
            const int bucketCount = 12;
            var buckets = new int[bucketCount];
            var now = DateTime.UtcNow;

            try
            {
                // Source 1: Activity feed entries (builds, plans, phases) from the database
                if (_messageBroker?.ActivityFeedService != null)
                {
                    var entries = _messageBroker.ActivityFeedService.GetActivitiesSince(now.AddHours(-bucketCount), 500);
                    foreach (var entry in entries)
                    {
                        int hoursAgo = (int)(now - entry.Timestamp).TotalHours;
                        if (hoursAgo >= 0 && hoursAgo < bucketCount)
                        {
                            buckets[bucketCount - 1 - hoursAgo]++;
                        }
                    }
                }

                // Source 2: Chat messages
                if (_messageBroker != null)
                {
                    var messages = _messageBroker.GetMessageHistory(500);
                    foreach (var msg in messages)
                    {
                        int hoursAgo = (int)(now - msg.Timestamp).TotalHours;
                        if (hoursAgo >= 0 && hoursAgo < bucketCount)
                        {
                            buckets[bucketCount - 1 - hoursAgo]++;
                        }
                    }
                }

                // Source 3: Pool messages (learned, completed, working_on, etc.)
                if (_poolCoordinator != null)
                {
                    var poolMessages = _poolCoordinator.GetRecentMessages(500);
                    foreach (var pm in poolMessages)
                    {
                        // Pool timestamps can be seconds or milliseconds
                        var timestamp = pm.Timestamp > 1_000_000_000_000
                            ? DateTimeOffset.FromUnixTimeMilliseconds(pm.Timestamp).UtcDateTime
                            : DateTimeOffset.FromUnixTimeSeconds(pm.Timestamp).UtcDateTime;
                        int hoursAgo = (int)(now - timestamp).TotalHours;
                        if (hoursAgo >= 0 && hoursAgo < bucketCount)
                        {
                            buckets[bucketCount - 1 - hoursAgo]++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Error building sparkline data: {ex.Message}");
            }

            return buckets;
        }

        /// <summary>
        /// Refresh all activity data.
        /// </summary>
        public void RefreshActivity()
        {
            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] RefreshActivity called, _activityService={((_activityService == null) ? "NULL" : "OK")}, _poolCoordinator={((_poolCoordinator == null) ? "NULL" : "OK")}, _renderer={(_renderer == null ? "NULL" : (_renderer.IsInitialized ? "READY" : "NOT_READY"))}");

            if (_activityService == null)
            {
                System.Diagnostics.Debug.WriteLine("[ActivityPanel] RefreshActivity: _activityService is null, returning");
                return;
            }

            // Load and display team member profiles (for avatars)
            if (_taskDatabase != null)
            {
                try
                {
                    var profiles = _taskDatabase.LoadAllProfiles();
                    System.Diagnostics.Debug.WriteLine($"[ActivityPanel] RefreshActivity: Got {profiles?.Count ?? 0} profiles from database");
                    _renderer?.ShowProfiles(profiles);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Error loading profiles: {ex.Message}");
                }
            }

            // Load and display terminal activity (Mission Control avatars)
            var activities = _activityService.GetTeamActivity();
            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] RefreshActivity: Got {activities?.Count ?? 0} terminal activities from service");
            _renderer?.ShowTeamActivity(activities);

            // Load and display pool messages (Activity Feed - LEARNED items)
            if (_poolCoordinator != null)
            {
                var poolMessages = _poolCoordinator.GetRecentMessages(50);
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] RefreshActivity: Got {poolMessages?.Count ?? 0} pool messages");

                // Convert pool messages to feed items (newest first display, but add oldest first to maintain order)
                // Normalize timestamps to milliseconds for consistent sorting (some are seconds, some milliseconds)
                var sortedMessages = poolMessages?
                    .OrderBy(m => m.Timestamp > 1_000_000_000_000 ? m.Timestamp : m.Timestamp * 1000)
                    .ToList() ?? new List<PoolMessage>();
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] _displayedFeedItemIds has {_displayedFeedItemIds.Count} entries");
                foreach (var msg in sortedMessages)
                {
                    var alreadyDisplayed = _displayedFeedItemIds.Contains(msg.Id);
                    System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Pool msg {msg.Id?.Substring(0, 8) ?? "NULL"}... already displayed: {alreadyDisplayed}");
                    if (!alreadyDisplayed)
                    {
                        _displayedFeedItemIds.Add(msg.Id);
                        try
                        {
                            // Handle both seconds and milliseconds timestamps
                            // Timestamps > 1 trillion are milliseconds, otherwise seconds
                            var timestamp = msg.Timestamp > 1_000_000_000_000
                                ? DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).DateTime
                                : DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).DateTime;

                            var feedItem = new ActivityFeedItem
                            {
                                Id = msg.Id,
                                Terminal = msg.Instance,
                                Type = msg.Action == PoolAction.LEARNED ? "learned" :
                                       msg.Action == PoolAction.COMPLETED ? "task" :
                                       msg.Action == PoolAction.WORKING_ON ? "task" :
                                       msg.Action == PoolAction.BLOCKED_BY ? "task" : "chat",
                                Content = FormatPoolMessageContent(msg),
                                Timestamp = timestamp,
                                IsPinned = false
                            };
                            _renderer?.AddFeedItem(feedItem);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Error processing pool message {msg.Id}: {ex.Message}");
                        }
                    }
                }
            }

            // Load and display chat messages (Activity Feed - Chat items)
            if (_messageBroker != null)
            {
                var chatMessages = _messageBroker.GetMessageHistory(50);
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] RefreshActivity: Got {chatMessages?.Count ?? 0} chat messages");

                foreach (var msg in chatMessages ?? new List<MCPServer.Models.Message>())
                {
                    var alreadyDisplayed = _displayedFeedItemIds.Contains(msg.Id);
                    System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Chat msg {msg.Id?.Substring(0, 8) ?? "NULL"}... already displayed: {alreadyDisplayed}");
                    if (!alreadyDisplayed)
                    {
                        _displayedFeedItemIds.Add(msg.Id);
                        var feedItem = new ActivityFeedItem
                        {
                            Id = msg.Id,
                            Terminal = msg.From,
                            Type = "chat",
                            Content = $"→ {msg.To}: {msg.Content}",
                            Timestamp = msg.Timestamp,
                            IsPinned = false
                        };
                        _renderer?.AddFeedItem(feedItem);
                    }
                }
            }

            // Initialize known task IDs to prevent showing existing tasks as new
            if (_messageBroker != null)
            {
                var existingTasks = _messageBroker.GetTasks();
                foreach (var task in existingTasks)
                {
                    _knownTaskIds.Add(task.Id);
                }
                System.Diagnostics.Debug.WriteLine($"[ActivityPanel] Initialized _knownTaskIds with {_knownTaskIds.Count} existing tasks");
            }

            // Update metrics
            UpdateMetrics();

            System.Diagnostics.Debug.WriteLine("[ActivityPanel] RefreshActivity: Complete");
        }

        /// <summary>
        /// Format pool message content for display.
        /// </summary>
        private string FormatPoolMessageContent(PoolMessage msg)
        {
            switch (msg.Action)
            {
                case PoolAction.LEARNED:
                    return string.IsNullOrEmpty(msg.Summary) ? msg.Topic : $"{msg.Topic}: {msg.Summary}";
                case PoolAction.COMPLETED:
                    return $"Completed: {msg.Topic}";
                case PoolAction.WORKING_ON:
                    return $"Working on: {msg.Topic}";
                case PoolAction.BLOCKED_BY:
                    return $"Blocked: {msg.Topic} (by {string.Join(", ", msg.BlockedBy ?? new List<string>())})";
                default:
                    return msg.Topic ?? msg.Summary ?? "";
            }
        }

        /// <summary>
        /// Apply theme to the panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _renderer?.SetTheme(isDark);
        }

        /// <summary>
        /// Sets the font size for the activity panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            _renderer?.SetFontSize(size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_refreshTimer != null)
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Tick -= OnRefreshTimerTick;
                    _refreshTimer.Dispose();
                    _refreshTimer = null;
                }
                if (_activityService != null)
                {
                    _activityService.ActivityUpdated -= OnActivityUpdated;
                }
                if (_poolCoordinator != null)
                {
                    _poolCoordinator.LearnedMessageRecorded -= OnLearnedMessageRecorded;
                    _poolCoordinator.PoolMessageRecorded -= OnPoolMessageRecorded;
                }
                if (_messageBroker != null)
                {
                    _messageBroker.MessageSent -= OnMessageSent;
                    _messageBroker.TasksUpdated -= OnTasksUpdated;
                    _messageBroker.ActivityRecorded -= OnActivityRecorded;
                }
                if (_renderer != null)
                {
                    _renderer.Ready -= OnRendererReady;
                    _renderer.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        protected override string GetPersistString()
        {
            return "ActivityPanel";
        }
    }
}
