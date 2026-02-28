using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.OfficePanel
{
    /// <summary>
    /// Dockable document for the Office Panel - animated office view of the team.
    /// Shows characters representing team members with real-time status updates,
    /// speech bubbles for messages, and a whiteboard with task counts.
    /// </summary>
    public class OfficePanelDocument : DockContent
    {
        private OfficePanelRenderer _renderer;
        private MessageBroker _messageBroker;
        private ActivityService _activityService;
        private bool _isDarkTheme = true;
        private Timer _refreshTimer;
        private readonly HashSet<string> _knownTerminalIds = new();
        private readonly HashSet<string> _knownAgentNames = new();
        private int _ghostCleanupCounter;
        private static readonly string TrackingFilePath = Path.Combine(Path.GetTempPath(), "mt-office-agents.json");

        public OfficePanelDocument()
        {
            InitializeComponent();

            // Hook visibility change to handle lazy-loading when panel is first shown
            this.DockStateChanged += OnDockStateChanged;
        }

        private void InitializeComponent()
        {
            Text = "Office View";
            TabText = "Office";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _renderer = new OfficePanelRenderer
            {
                Dock = DockStyle.Fill
            };

            // Subscribe to Ready event to load data when WebView2 is ready
            _renderer.Ready += OnRendererReady;
            _renderer.ZoomChanged += (s, zoom) => ZoomChanged?.Invoke(this, zoom);

            Controls.Add(_renderer);
        }

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Forwards to the renderer.
        /// </summary>
        public void SetZoomFactor(double zoom) => _renderer?.SetZoomFactor(zoom);

        private void OnRendererReady(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnRendererReady fired, _messageBroker={(_messageBroker == null ? "NULL" : "OK")}");

            // WebView2 is now ready - load initial data and start refresh timer
            RefreshData();
            StartRefreshTimer();
        }

        /// <summary>
        /// Handle dock state changes to refresh data when panel becomes visible.
        /// </summary>
        private void OnDockStateChanged(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[OfficePanel] DockStateChanged: DockState={DockState}, _messageBroker={(_messageBroker == null ? "NULL" : "OK")}");

            // When panel becomes visible, refresh data if we can
            if (DockState != DockState.Hidden && DockState != DockState.Unknown)
            {
                if (_messageBroker != null && _renderer?.IsInitialized == true)
                {
                    System.Diagnostics.Debug.WriteLine("[OfficePanel] Panel now visible - refreshing data");
                    RefreshData();
                    StartRefreshTimer();
                }
            }
            else
            {
                // Panel hidden - stop timer to save resources
                StopRefreshTimer();
            }
        }

        /// <summary>
        /// Initialize the office panel with the MessageBroker and ActivityService for real-time updates.
        /// </summary>
        public void Initialize(MessageBroker messageBroker, ActivityService activityService)
        {
            System.Diagnostics.Debug.WriteLine($"[OfficePanel] Initialize called, messageBroker={(messageBroker == null ? "NULL" : "OK")}, activityService={(activityService == null ? "NULL" : "OK")}, renderer={(_renderer == null ? "NULL" : (_renderer.IsInitialized ? "READY" : "NOT_READY"))}");

            _messageBroker = messageBroker;
            _activityService = activityService;

            if (_messageBroker != null)
            {
                _messageBroker.TerminalRegistered += OnTerminalRegistered;
                _messageBroker.TerminalDisconnected += OnTerminalDisconnected;
                _messageBroker.MessageSent += OnMessageSent;
                _messageBroker.TasksUpdated += OnTasksUpdated;
                _messageBroker.OfficeAgentSpawned += OnOfficeAgentSpawned;
                _messageBroker.OfficeAgentDeparted += OnOfficeAgentDeparted;
                System.Diagnostics.Debug.WriteLine("[OfficePanel] Subscribed to MessageBroker events");
            }

            if (_activityService != null)
            {
                _activityService.ActivityUpdated += OnActivityUpdated;
                System.Diagnostics.Debug.WriteLine("[OfficePanel] Subscribed to ActivityUpdated events");
            }

            // If renderer is already initialized, load data and start timer now
            if (_renderer?.IsInitialized == true)
            {
                System.Diagnostics.Debug.WriteLine("[OfficePanel] Renderer already ready, refreshing data now");
                RefreshData();
                StartRefreshTimer();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[OfficePanel] Renderer not ready yet, will load when Ready event fires");
            }
        }

        /// <summary>
        /// Start the periodic refresh timer (5 second interval).
        /// Syncs displayed characters with actual terminal state.
        /// </summary>
        private void StartRefreshTimer()
        {
            if (_refreshTimer != null) return; // Already running

            _refreshTimer = new Timer { Interval = 5000 };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
            System.Diagnostics.Debug.WriteLine("[OfficePanel] Refresh timer started (5s interval)");
        }

        /// <summary>
        /// Stop the periodic refresh timer.
        /// </summary>
        private void StopRefreshTimer()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= OnRefreshTimerTick;
                _refreshTimer.Dispose();
                _refreshTimer = null;
                System.Diagnostics.Debug.WriteLine("[OfficePanel] Refresh timer stopped");
            }
        }

        private void OnRefreshTimerTick(object sender, EventArgs e)
        {
            RefreshData();

            // Run ghost agent cleanup every 2nd tick (~10 seconds)
            _ghostCleanupCounter++;
            if (_ghostCleanupCounter >= 2)
            {
                _ghostCleanupCounter = 0;
                CleanupGhostAgents();
            }
        }

        /// <summary>
        /// Sync the office view with current terminal and task state.
        /// Adds new characters, removes disconnected ones, updates whiteboard.
        /// </summary>
        private void RefreshData()
        {
            if (_messageBroker == null || _renderer?.IsInitialized != true)
                return;

            // Sync terminals - add new, remove disconnected
            var currentTerminals = _messageBroker.GetTerminals();
            var currentIds = new HashSet<string>();

            foreach (var terminal in currentTerminals)
            {
                currentIds.Add(terminal.Id);

                // Skip "Unassigned" placeholder terminals and temporary subagents
                if (terminal.Name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (terminal.Name.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!_knownTerminalIds.Contains(terminal.Id))
                {
                    // New terminal - add character
                    var activity = _activityService?.GetActivity(terminal.Name);
                    string role = "director";
                    _renderer.AddCharacter(
                        terminal.Id,
                        terminal.Name,
                        terminal.Color ?? "",
                        activity?.Status ?? "idle",
                        activity?.Activity ?? "Connected",
                        role);
                    _knownTerminalIds.Add(terminal.Id);
                    System.Diagnostics.Debug.WriteLine($"[OfficePanel] RefreshData: Added {terminal.Name} ({terminal.Id}) as {role}");
                }
            }

            // Remove characters for terminals that are no longer connected
            var removedIds = _knownTerminalIds.Where(id => !currentIds.Contains(id)).ToList();
            foreach (var id in removedIds)
            {
                _renderer.RemoveCharacter(id);
                _knownTerminalIds.Remove(id);
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] RefreshData: Removed {id}");
            }

            // Sync office agents (subagents that aren't registered terminals)
            var currentAgents = _messageBroker.GetOfficeAgents();
            var currentAgentNames = new HashSet<string>(currentAgents.Select(a => a.Name));

            foreach (var agent in currentAgents)
            {
                if (!_knownAgentNames.Contains(agent.Name))
                {
                    var agentId = "agent-" + agent.Name;
                    _renderer.AddCharacter(agentId, agent.Name, "", agent.Status ?? "working", "Working", "agent");
                    _knownAgentNames.Add(agent.Name);
                    System.Diagnostics.Debug.WriteLine($"[OfficePanel] RefreshData: Added agent {agent.Name}");
                }
            }

            // Remove agents that are no longer active
            var removedAgents = _knownAgentNames.Where(n => !currentAgentNames.Contains(n)).ToList();
            foreach (var name in removedAgents)
            {
                var agentId = "agent-" + name;
                _renderer.RemoveCharacter(agentId);
                _knownAgentNames.Remove(name);
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] RefreshData: Removed agent {name}");
            }

            // Update whiteboard task counts
            var tasks = _messageBroker.GetTasks();
            int todo = tasks.Count(t => t.Status == "todo" || t.Status == "suggestion");
            int inProgress = tasks.Count(t => t.Status == "in_progress");
            int done = tasks.Count(t => t.Status == "done");
            string recentTask = tasks.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.Title ?? "";
            _renderer.UpdateWhiteboard(todo, inProgress, done, recentTask);
        }

        /// <summary>
        /// Check the hook tracking file for ghost agents whose transcript files
        /// are no longer being written to (cancelled/dead subagents).
        /// Departs them from the office and removes them from the tracking file.
        /// </summary>
        private void CleanupGhostAgents()
        {
            try
            {
                if (!File.Exists(TrackingFilePath))
                    return;

                var json = File.ReadAllText(TrackingFilePath);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
                    return;

                var tracking = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (tracking == null || tracking.Count == 0)
                    return;

                var agentsToDepartNames = new List<string>();
                var agentIdsToRemove = new List<string>();

                foreach (var kvp in tracking)
                {
                    var agentId = kvp.Key;
                    var entry = kvp.Value;

                    // Get the transcript path from the tracking entry
                    string transcriptPath = null;
                    string agentName = null;
                    if (entry.ValueKind == JsonValueKind.Object)
                    {
                        if (entry.TryGetProperty("transcriptPath", out var tp))
                            transcriptPath = tp.GetString();
                        if (entry.TryGetProperty("name", out var n))
                            agentName = n.GetString();
                    }

                    if (string.IsNullOrEmpty(transcriptPath) || string.IsNullOrEmpty(agentName))
                        continue;

                    // Check if the transcript file exists and when it was last modified
                    if (!File.Exists(transcriptPath))
                    {
                        // Transcript file doesn't exist yet - agent may still be starting up.
                        // Check startedAt to see if it's been too long.
                        if (entry.TryGetProperty("startedAt", out var startedAtProp))
                        {
                            if (DateTime.TryParse(startedAtProp.GetString(), out var startedAt))
                            {
                                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[OfficePanel] Ghost cleanup: {agentName} - no transcript after 30s, departing");
                                    agentsToDepartNames.Add(agentName);
                                    agentIdsToRemove.Add(agentId);
                                }
                            }
                        }
                        continue;
                    }

                    var lastWrite = File.GetLastWriteTimeUtc(transcriptPath);
                    var staleSeconds = (DateTime.UtcNow - lastWrite).TotalSeconds;

                    if (staleSeconds > 15)
                    {
                        System.Diagnostics.Debug.WriteLine($"[OfficePanel] Ghost cleanup: {agentName} transcript stale for {staleSeconds:F0}s, departing");
                        agentsToDepartNames.Add(agentName);
                        agentIdsToRemove.Add(agentId);
                    }
                }

                // Depart ghost agents via MessageBroker
                foreach (var name in agentsToDepartNames)
                {
                    _messageBroker?.NotifyAgentDeparted(name);
                }

                // Remove departed agents from the tracking file
                if (agentIdsToRemove.Count > 0)
                {
                    // Re-read to avoid race conditions with the hook script
                    var freshJson = File.ReadAllText(TrackingFilePath);
                    var freshTracking = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(freshJson)
                                       ?? new Dictionary<string, JsonElement>();

                    foreach (var id in agentIdsToRemove)
                    {
                        freshTracking.Remove(id);
                    }

                    File.WriteAllText(TrackingFilePath, JsonSerializer.Serialize(freshTracking, JsonOptions.UnicodeIndented));
                    System.Diagnostics.Debug.WriteLine($"[OfficePanel] Ghost cleanup: removed {agentIdsToRemove.Count} ghost(s) from tracking file");
                }
            }
            catch (Exception ex)
            {
                // Don't let ghost cleanup errors affect the main refresh cycle
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] Ghost cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle a new terminal registering - add a character to the office.
        /// </summary>
        private void OnTerminalRegistered(object sender, TerminalInfo terminal)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTerminalRegistered(sender, terminal)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnTerminalRegistered: {terminal.Name}");

            // Skip "Unassigned" placeholder terminals - they'll re-register with a real name
            // and we want the walk-in animation to play for the real name, not the placeholder
            if (terminal.Name.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] Skipping 'Unassigned' terminal");
                return;
            }

            // Skip temporary subagents - they are internal Claude Code subagents
            // that should not appear as characters in the office
            if (terminal.Name.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] Skipping temporary agent: {terminal.Name}");
                return;
            }

            string role = "director";
            string initialStatus = role == "agent" ? "working" : "idle";
            string initialActivity = role == "agent" ? "Investigating" : "Just connected";

            if (!_knownTerminalIds.Contains(terminal.Id))
            {
                _knownTerminalIds.Add(terminal.Id);
            }
            // Always call AddCharacter - if RefreshData already added it with "idle" status,
            // this will update it to "working" (JS addCharacter handles duplicates via updateCharacterState)
            _renderer?.AddCharacter(terminal.Id, terminal.Name, terminal.Color ?? "", initialStatus, initialActivity, role);
        }

        /// <summary>
        /// Handle a terminal disconnecting - remove its character from the office.
        /// </summary>
        private void OnTerminalDisconnected(object sender, TerminalInfo terminal)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTerminalDisconnected(sender, terminal)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnTerminalDisconnected: {terminal.Name}");
            _renderer?.RemoveCharacter(terminal.Id);
            _knownTerminalIds.Remove(terminal.Id);
        }

        /// <summary>
        /// Handle activity updates - update character state in the office.
        /// </summary>
        private void OnActivityUpdated(object sender, TerminalActivity activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnActivityUpdated(sender, activity)));
                return;
            }

            // Find the terminal by name to get its ID
            var terminal = _messageBroker?.GetTerminals()?.FirstOrDefault(t => t.Name == activity.Terminal);
            if (terminal != null)
            {
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnActivityUpdated: {activity.Terminal} -> {activity.Status}");
                _renderer?.UpdateCharacterState(terminal.Id, activity.Status ?? "idle", activity.Activity ?? "");
            }
        }

        /// <summary>
        /// Handle messages - show speech bubble on the sender's character.
        /// </summary>
        private void OnMessageSent(object sender, MCPServer.Models.Message message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnMessageSent(sender, message)));
                return;
            }

            // Find sender terminal to show speech bubble and move to whiteboard
            var terminal = _messageBroker?.GetTerminals()?.FirstOrDefault(t => t.Name == message.From);
            if (terminal != null)
            {
                System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnMessageSent: {message.From} says something");
                _renderer?.UpdateCharacterState(terminal.Id, "working", "Reporting results");
                _renderer?.ShowSpeechBubble(terminal.Id, message.Content, 4000);
            }
        }

        /// <summary>
        /// Handle task updates - refresh whiteboard counts.
        /// </summary>
        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTasksUpdated(sender, tasks)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnTasksUpdated: {tasks?.Count ?? 0} tasks");

            int todo = tasks?.Count(t => t.Status == "todo" || t.Status == "suggestion") ?? 0;
            int inProgress = tasks?.Count(t => t.Status == "in_progress") ?? 0;
            int done = tasks?.Count(t => t.Status == "done") ?? 0;
            string recentTask = tasks?.OrderByDescending(t => t.CreatedAt).FirstOrDefault()?.Title ?? "";
            _renderer?.UpdateWhiteboard(todo, inProgress, done, recentTask);
        }

        /// <summary>
        /// Handle an office agent being spawned - add an agent character to the office.
        /// </summary>
        private void OnOfficeAgentSpawned(object sender, OfficeAgentInfo agent)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnOfficeAgentSpawned(sender, agent)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnOfficeAgentSpawned: {agent.Name} (by {agent.SpawnedBy})");

            var agentId = "agent-" + agent.Name;
            if (!_knownAgentNames.Contains(agent.Name))
            {
                _knownAgentNames.Add(agent.Name);
            }
            _renderer?.AddCharacter(agentId, agent.Name, "", "working", "Investigating", "agent", agent.SpawnedBy);
        }

        /// <summary>
        /// Handle an office agent departing - remove its character from the office.
        /// </summary>
        private void OnOfficeAgentDeparted(object sender, OfficeAgentInfo agent)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnOfficeAgentDeparted(sender, agent)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OfficePanel] OnOfficeAgentDeparted: {agent.Name}");

            var agentId = "agent-" + agent.Name;
            _renderer?.RemoveCharacter(agentId);
            _knownAgentNames.Remove(agent.Name);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopRefreshTimer();
                if (_activityService != null)
                {
                    _activityService.ActivityUpdated -= OnActivityUpdated;
                }
                if (_messageBroker != null)
                {
                    _messageBroker.TerminalRegistered -= OnTerminalRegistered;
                    _messageBroker.TerminalDisconnected -= OnTerminalDisconnected;
                    _messageBroker.MessageSent -= OnMessageSent;
                    _messageBroker.TasksUpdated -= OnTasksUpdated;
                    _messageBroker.OfficeAgentSpawned -= OnOfficeAgentSpawned;
                    _messageBroker.OfficeAgentDeparted -= OnOfficeAgentDeparted;
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
            return "OfficePanel";
        }
    }
}
