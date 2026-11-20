using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Tails one or more Claude Code transcript JSONL files in real-time using FileSystemWatcher.
    /// Implements IAgentMessageSource so AgentPanelControl can display conversations
    /// from native Claude Code agent teammates (observed via transcript files).
    ///
    /// Supports multiple files because Claude Code creates a new JSONL file each time
    /// an agent wakes up to process a message. All files for the same agent are aggregated
    /// into a single message stream.
    /// </summary>
    public class TranscriptTailer : IAgentMessageSource, IDisposable
    {
        private readonly string _inboxPath;
        private readonly string _agentName;

        private readonly List<AgentMessage> _messages = new List<AgentMessage>();
        private readonly object _messagesLock = new object();
        private readonly object _inboxLock = new object();
        private readonly List<TrackedFile> _trackedFiles = new List<TrackedFile>();
        private readonly object _trackedFilesLock = new object();

        private FileSystemWatcher _directoryWatcher;
        private bool _disposed;
        private bool _pendingStop;

        // 925c8cff: one read pass at a time. The file watcher's thread, the quiet timer and a catch-up
        // read (AddTranscriptFile) can all start a read; under this lock they run one after another,
        // so a file position, the turn state and the line ids are never updated by two at once. Events
        // are not raised under it: a read queues them (_pendingEvents) and they are raised after the
        // lock is released (DrainEvents), in the order they were queued.
        private readonly object _readLock = new object();
        private readonly ConcurrentQueue<Action> _pendingEvents = new ConcurrentQueue<Action>();
        private int _draining;

        // GH #89: which file first delivered each transcript line (by its uuid), so a line repeated
        // in the agent's continuation file is shown once. Guarded by _readLock. Capped at
        // MaxTrackedLineIds (925c8cff), oldest id evicted first: a repeat of a line older than that is
        // shown again rather than the set growing for as long as the tailer lives.
        private readonly Dictionary<string, string> _lineOrigins = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _lineOriginOrder = new Queue<string>();

        // GH #89 fallback completion: the SubagentStop hook is the normal finish signal, but it does
        // not always arrive. A non-team agent whose last turn ended (end_turn) and whose transcript
        // has then been quiet for QuietCompletionWindow has finished.
        private readonly object _completionLock = new object();
        private Timer _quietTimer;
        private volatile bool _completed;
        private volatile bool _turnEnded;
        private DateTime _lastGrowthUtc = DateTime.UtcNow;

        /// <summary>How long a transcript must be quiet after an ended turn before the agent counts as finished.</summary>
        internal TimeSpan QuietCompletionWindow { get; set; } = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan QuietCheckInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How many messages <see cref="Messages"/> keeps; past it the oldest are dropped (925c8cff). An
        /// implicit-team member's tailer lives until the team config removes the member, which may be
        /// never, so what it holds is bounded rather than tied to its lifetime.
        /// </summary>
        internal int MaxRetainedMessages { get; set; } = 2000;

        /// <summary>How many line ids the repeat check remembers (see <c>_lineOrigins</c>).</summary>
        internal int MaxTrackedLineIds { get; set; } = 10000;

        /// <summary>Test seam: how many line ids the repeat check holds now.</summary>
        internal int TrackedLineIdCount
        {
            get { lock (_readLock) { return _lineOrigins.Count; } }
        }

        // Track the last user message we sent, to deduplicate echo(es) from the transcript.
        // Uses a time window instead of one-shot because the transcript may produce multiple
        // JSONL entries for the same user turn (e.g., message + content echo).
        private string _lastSentUserContent;
        private DateTime _lastSentUserTime;

        /// <summary>
        /// The agent name this tailer was created for.
        /// </summary>
        public string AgentName => _agentName;

        /// <summary>
        /// Timestamp of the last message sent via SendMessageAsync (from Agent Panel UI).
        /// Used by TeamWatcherService to attribute wake-up transcript files to the correct agent.
        /// </summary>
        public DateTime? LastMessageSentAt { get; private set; }

        // First file path (for backward compatibility with StartAsync file-not-yet-exists logic)
        private readonly string _initialTranscriptPath;

        public event EventHandler<AgentMessage> MessageReceived;
        public event EventHandler<AgentMessage> TeamMessageSent;
        public event EventHandler<int> Stopped;

        /// <summary>
        /// When true, the tailer won't auto-stop on Result messages (team agents stay alive between turns).
        /// Only stops via explicit StopAsync() or Dispose().
        /// </summary>
        public bool IsTeamAgent { get; set; }

        /// <summary>
        /// A member of the team Claude Code gives every session on its own (<c>session-&lt;id prefix&gt;</c>,
        /// see <see cref="TeamWatcherService.IsImplicitSessionTeam"/>), not of one somebody created. Every
        /// named Agent call lands in that team, and nothing removes the member when it is done (268acc24),
        /// so it finishes the way a subagent does: the SubagentStop line, or an ended turn followed by
        /// quiet. It stays a team agent otherwise (it has an inbox and can be sent messages).
        /// <para>Completing does not shut such a tailer down: it raises <see cref="Stopped"/> with 0 (the
        /// card shows completed, or closes) and keeps watching its files. A message the lead sends
        /// within <see cref="QuietCompletionWindow"/> of the turn ending (the usual "your report was cut
        /// off, send the rest") arrives as a user line, which un-ends the turn, so that case never
        /// completes. One sent later wakes a completed member: its next user or assistant line, in the
        /// same file or a new one, makes it active again and raises <see cref="Reactivated"/>.</para>
        /// </summary>
        public bool IsImplicitTeamMember { get; set; }

        /// <summary>Waits for an explicit stop: a member of a team somebody created, which idles between turns by design.</summary>
        private bool WaitsForExplicitStop => IsTeamAgent && !IsImplicitTeamMember;

        /// <summary>Keeps watching after completing, so a later wake is seen (see <see cref="IsImplicitTeamMember"/>).</summary>
        private bool KeepsWatchingWhenComplete => IsImplicitTeamMember;

        /// <summary>Set by <see cref="StopAsync"/> and Dispose: nothing can make the tailer active again.</summary>
        private volatile bool _shutDown;

        /// <summary>Stopped for good (<see cref="StopAsync"/> or Dispose): no card should be built for it.</summary>
        public bool IsShutDown => _shutDown;

        /// <summary>The agent has finished (exit 0 was raised) and has not been woken since.</summary>
        public bool IsCompleted => _completed;

        /// <summary>
        /// A completed implicit-team member got new work and is active again. Raised after the read
        /// pass that woke it, so <see cref="Messages"/> already holds the new lines.
        /// </summary>
        public event EventHandler Reactivated;

        public IReadOnlyList<AgentMessage> Messages
        {
            get
            {
                lock (_messagesLock)
                {
                    return _messages.ToArray();
                }
            }
        }

        public bool IsActive { get; private set; }
        public string SessionId { get; private set; }

        /// <summary>
        /// Create a new TranscriptTailer for a Claude Code agent teammate.
        /// </summary>
        /// <param name="transcriptPath">Path to the initial agent-{id}.jsonl transcript file.</param>
        /// <param name="inboxPath">Path to the agent's inbox JSON file for sending messages.</param>
        /// <param name="agentName">Display name of the teammate.</param>
        public TranscriptTailer(string transcriptPath, string inboxPath, string agentName)
        {
            _initialTranscriptPath = transcriptPath ?? throw new ArgumentNullException(nameof(transcriptPath));
            _inboxPath = inboxPath; // null = read-only mode (no inbox for non-team subagents)
            _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
        }

        /// <summary>
        /// Start tailing the initial transcript file. Catches up on existing content, then watches for appends.
        /// </summary>
        public Task StartAsync()
        {
            lock (_completionLock)
            {
                // _shutDown: a member removed (and its tailer disposed) before its start ran stays stopped.
                if (IsActive || _completed || _shutDown) return Task.CompletedTask;
                IsActive = true;
            }

            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] StartAsync called, file={_initialTranscriptPath}");

            if (File.Exists(_initialTranscriptPath))
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] File exists, reading existing content...");
                AddTranscriptFile(_initialTranscriptPath);
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] After initial read: {_messages.Count} messages buffered");

                // The catch-up read is not growth: the file last grew when it was last written. A
                // transcript found already ended and quiet (an agent that finished before MT started)
                // completes now, before anyone is told about it, instead of a minute after its card opens.
                var written = File.GetLastWriteTimeUtc(_initialTranscriptPath);
                lock (_readLock)
                {
                    if (written < _lastGrowthUtc) _lastGrowthUtc = written;
                }
                CheckQuietCompletion(DateTime.UtcNow);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] File does NOT exist yet, starting directory watcher");
                StartDirectoryWatcher();
            }

            if (!_completed || KeepsWatchingWhenComplete)
                _quietTimer ??= new Timer(_ => CheckQuietCompletion(DateTime.UtcNow), null, QuietCheckInterval, QuietCheckInterval);

            return Task.CompletedTask;
        }

        /// <summary>
        /// The agent has finished: stop tailing and raise <see cref="Stopped"/> with exit code 0.
        /// Called for the SubagentStop hook, the hook's line in the transcript, and the quiet fallback.
        /// Raises at most once per piece of work; does nothing once the tailer has stopped. An
        /// implicit-team member keeps its watchers, so it can be woken (<see cref="IsImplicitTeamMember"/>).
        /// </summary>
        public void Complete()
        {
            bool completed;
            lock (_readLock)
            {
                completed = MarkCompleted();
            }
            if (completed) AfterCompleted();
        }

        /// <summary>
        /// The state half of <see cref="Complete"/>: marks the agent finished and queues Stopped(0).
        /// Caller holds <c>_readLock</c>, so the finish is ordered with the read passes: messages a
        /// pass queued are raised before the Stopped, and a later pass sees it completed.
        /// </summary>
        private bool MarkCompleted()
        {
            lock (_completionLock)
            {
                if (!IsActive || _completed) return false;
                IsActive = false;
                _completed = true;
            }

            QueueEvent(Stopped, h => h(this, 0));
            return true;
        }

        /// <summary>After <see cref="MarkCompleted"/> did it, outside <c>_readLock</c>: drop the watchers unless a wake can still come, then raise.</summary>
        private void AfterCompleted()
        {
            if (!KeepsWatchingWhenComplete)
                DisposeWatchers();
            DrainEvents();
        }

        /// <summary>
        /// Queue an event for <see cref="DrainEvents"/>. The handler list is read now, when the thing
        /// it reports happened, so a subscriber added later (TeamWatcherService subscribes
        /// TeamMessageSent only after the catch-up read) is not sent what happened before it subscribed.
        /// </summary>
        private void QueueEvent<THandler>(THandler handlers, Action<THandler> raise)
            where THandler : Delegate
        {
            if (handlers == null) return;
            _pendingEvents.Enqueue(() => raise(handlers));
        }

        /// <summary>
        /// Raise queued events, oldest first, on this thread, holding no lock of this tailer. One
        /// thread drains at a time: a thread that finds another draining leaves its events to it (the
        /// drainer re-checks the queue after it stops, so none is left behind). A handler that throws
        /// is logged and the rest still run.
        /// </summary>
        private void DrainEvents()
        {
            while (!_pendingEvents.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
                try
                {
                    while (_pendingEvents.TryDequeue(out var raise))
                    {
                        try
                        {
                            raise();
                        }
#pragma warning disable CA1031 // A subscriber's failure must not stop the events queued after it.
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Event handler failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _draining, 0);
                }
            }
        }

        private int _wakeCount;

        /// <summary>
        /// How many times this tailer has been woken (<see cref="Reactivated"/> is queued once per
        /// wake). Read with <see cref="IsCompleted"/> it says which wakes an announcement already covers.
        /// </summary>
        public int WakeCount
        {
            get { lock (_completionLock) { return _wakeCount; } }
        }

        /// <summary><see cref="IsCompleted"/> and <see cref="WakeCount"/> read together, so neither can change between the two reads.</summary>
        public (bool Completed, int Wakes) ReadCompletion()
        {
            lock (_completionLock) { return (_completed, _wakeCount); }
        }

        /// <summary>A completed implicit-team member that can still be woken.</summary>
        private bool IsDormant => _completed && KeepsWatchingWhenComplete && !_shutDown;

        /// <summary>Make a dormant member active again. True when this call did it.</summary>
        private bool TryReactivate()
        {
            lock (_completionLock)
            {
                if (!IsDormant) return false;
                _completed = false;
                IsActive = true;
                _wakeCount++;
            }

            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Completed member has new work - active again");
            return true;
        }

        /// <summary>
        /// The quiet fallback (GH #89): a non-team agent whose last turn ended and whose transcript has
        /// not grown for <see cref="QuietCompletionWindow"/> is complete. Members of a created team are
        /// exempt: they idle between turns by design. Members of a session's implicit team are not
        /// (<see cref="IsImplicitTeamMember"/>).
        /// </summary>
        internal void CheckQuietCompletion(DateTime nowUtc)
        {
            bool completed;
            lock (_readLock) // checked and acted on under it, so a read that un-ends the turn cannot land in between
            {
                if (!IsActive || WaitsForExplicitStop || !_turnEnded) return;
                if (nowUtc - _lastGrowthUtc < QuietCompletionWindow) return;

                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Turn ended and transcript quiet - agent completed");
                completed = MarkCompleted();
            }
            if (completed) AfterCompleted();
        }

        /// <summary>
        /// Tracks whether the agent's last turn has ended: an assistant line that stopped with
        /// <c>end_turn</c> ends it; any later assistant line that did not, or any user line (a tool
        /// result, a new prompt), means the agent is working again. Other line types (progress,
        /// system, hook records) change nothing.
        /// </summary>
        private void NoteTurnState(string lineType, string stopReason)
        {
            if (lineType == "assistant")
                _turnEnded = stopReason == "end_turn";
            else if (lineType == "user")
                _turnEnded = false;
        }

        /// <summary>The top-level <c>uuid</c>, <c>type</c> and <c>message.stop_reason</c> of a transcript line; nulls when absent or unparseable.</summary>
        internal static (string Uuid, string Type, string StopReason) ReadLineMeta(string jsonLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return (null, null, null);

                string uuid = root.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                string stop = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                return (string.IsNullOrEmpty(uuid) ? null : uuid, type, stop);
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
        }

        /// <summary>
        /// Check if this tailer is tracking a specific transcript file path.
        /// </summary>
        public bool HasTranscriptFile(string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath)) return false;
            lock (_trackedFilesLock)
            {
                foreach (var tf in _trackedFiles)
                {
                    if (string.Equals(tf.Path, transcriptPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Add an additional transcript file to tail. Used when the same agent
        /// creates a new JSONL file on subsequent wake-ups.
        /// Reads existing content immediately and watches for new appends.
        /// </summary>
        public void AddTranscriptFile(string transcriptPath)
        {
            // A completed agent stays completed (GH #89): a continuation file found after the finish
            // must not restart a tailer that nothing could then stop. A dormant implicit-team member is
            // the exception: a new file is how a wake can arrive, and its lines reactivate it (268acc24).
            if (string.IsNullOrEmpty(transcriptPath) || _shutDown || (_completed && !IsDormant)) return;

            lock (_trackedFilesLock)
            {
                // Don't add the same file twice
                foreach (var tf in _trackedFiles)
                {
                    if (string.Equals(tf.Path, transcriptPath, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Adding transcript file: {transcriptPath}");

            var tracked = new TrackedFile { Path = transcriptPath, LastPosition = 0 };

            lock (_trackedFilesLock)
            {
                // Checked again under the lock: two callers adding the same file at once add it once.
                foreach (var tf in _trackedFiles)
                {
                    if (string.Equals(tf.Path, transcriptPath, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                _trackedFiles.Add(tracked);
            }

            // Watch for appends FIRST, then read what is there (925c8cff). The other way round, a line
            // appended after the catch-up read reached the end of the file and before the watcher was
            // live raised no event and stayed unread until the next append. Reads are serialized
            // (_readLock) and each starts at the file's tracked position, so a watcher read that runs
            // during or after the catch-up reads only what the catch-up did not.
            StartFileWatcherForTracked(tracked);

            // Read existing content
            ReadNewLinesFromFile(tracked);

            // The catch-up read itself can reach the finish (the SubagentStop line): its Complete
            // dropped the watchers already; this drops one a concurrent DisposeWatchers may have missed.
            if (_completed && !IsDormant)
            {
                DisposeWatchers();
                return;
            }

            // Shut down while this file was being added: drop the watcher just started, and stay inactive.
            if (_shutDown)
            {
                DisposeWatchers();
                return;
            }

            // Re-activate if was marked inactive (a dormant member is reactivated only by new work)
            if (!_completed)
                IsActive = true;
        }

        /// <summary>
        /// Stop tailing and clean up watchers.
        /// </summary>
        public Task StopAsync()
        {
            bool wasActive;
            lock (_completionLock)
            {
                _shutDown = true;
                wasActive = IsActive;
                IsActive = false;
            }

            if (!wasActive)
            {
                // A dormant member (completed, still watching) has no stop to report, only watchers to drop.
                if (_completed) DisposeWatchers();
                return Task.CompletedTask;
            }

            DisposeWatchers();
            QueueEvent(Stopped, h => h(this, -1));
            DrainEvents();

            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string content)
        {
            if (string.IsNullOrEmpty(content)) return Task.CompletedTask;
            if (string.IsNullOrEmpty(_inboxPath)) return Task.CompletedTask; // read-only mode

            // Emit local user message immediately so the UI shows the sent message
            // (mirrors AgentProcess.SendMessageAsync behavior)
            var userMsg = new AgentMessage
            {
                Timestamp = DateTime.UtcNow,
                Type = AgentMessageType.User,
                Content = content,
                SessionId = SessionId
            };
            AddMessage(userMsg);
            // Track for dedup when the echo arrives in the transcript
            _lastSentUserContent = content;
            _lastSentUserTime = DateTime.UtcNow;
            LastMessageSentAt = DateTime.UtcNow;
            MessageReceived?.Invoke(this, userMsg);

            lock (_inboxLock)
            {
                try
                {
                    // Ensure parent directory exists
                    var dir = Path.GetDirectoryName(_inboxPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // Read existing inbox JSON array, preserving structure
                    List<JsonElement> existingElements;
                    if (File.Exists(_inboxPath))
                    {
                        using (var stream = new FileStream(_inboxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            using var doc = JsonDocument.Parse(stream);
                            existingElements = new List<JsonElement>();
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var elem in doc.RootElement.EnumerateArray())
                                {
                                    existingElements.Add(elem.Clone());
                                }
                            }
                        }
                    }
                    else
                    {
                        existingElements = new List<JsonElement>();
                    }

                    // Write back with the new message appended
                    using (var stream = new FileStream(_inboxPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartArray();

                        // Write existing messages
                        foreach (var elem in existingElements)
                        {
                            elem.WriteTo(writer);
                        }

                        // Write new message (from "team-lead" to match native team convention)
                        writer.WriteStartObject();
                        writer.WriteString("from", "team-lead");
                        writer.WriteString("text", content);
                        writer.WriteString("summary", content.Substring(0, Math.Min(30, content.Length)));
                        writer.WriteString("timestamp", DateTime.UtcNow.ToString("o"));
                        writer.WriteBoolean("read", false);
                        writer.WriteEndObject();

                        writer.WriteEndArray();
                    }

                    System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Wrote message to inbox: {_inboxPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error writing to inbox: {ex.Message}");
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>Keep a message, dropping the oldest past <see cref="MaxRetainedMessages"/>.</summary>
        private void AddMessage(AgentMessage msg)
        {
            lock (_messagesLock)
            {
                _messages.Add(msg);
                int excess = _messages.Count - Math.Max(1, MaxRetainedMessages);
                if (excess > 0) _messages.RemoveRange(0, excess);
            }
        }

        /// <summary>
        /// The GH #89 repeat check: true when <paramref name="uuid"/> was first delivered by another
        /// file. Records the id for <paramref name="path"/> otherwise, evicting the oldest id past
        /// <see cref="MaxTrackedLineIds"/>. Caller holds <c>_readLock</c>.
        /// </summary>
        private bool IsRepeatFromAnotherFile(string uuid, string path)
        {
            if (_lineOrigins.TryGetValue(uuid, out var origin))
                return !string.Equals(origin, path, StringComparison.OrdinalIgnoreCase);

            _lineOrigins[uuid] = path;
            _lineOriginOrder.Enqueue(uuid);
            while (_lineOriginOrder.Count > Math.Max(1, MaxTrackedLineIds))
                _lineOrigins.Remove(_lineOriginOrder.Dequeue());
            return false;
        }

        public Task InterruptAsync()
        {
            // Cannot interrupt native teammates via transcript tailing
            return Task.CompletedTask;
        }

        /// <summary>
        /// Watch a specific transcript JSONL file for new appends.
        /// </summary>
        private void StartFileWatcherForTracked(TrackedFile tracked)
        {
            try
            {
                var dir = Path.GetDirectoryName(tracked.Path);
                var fileName = Path.GetFileName(tracked.Path);

                tracked.Watcher = new FileSystemWatcher(dir)
                {
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                tracked.Watcher.Changed += (s, e) => OnTrackedFileChanged(tracked);
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Started file watcher for {tracked.Path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error starting file watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Watch the parent directory for the initial transcript file to be created.
        /// Once created, switch to tracking it.
        /// </summary>
        private void StartDirectoryWatcher()
        {
            try
            {
                var dir = Path.GetDirectoryName(_initialTranscriptPath);
                var fileName = Path.GetFileName(_initialTranscriptPath);

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Parent directory does not exist: {dir}");
                    return;
                }

                _directoryWatcher = new FileSystemWatcher(dir)
                {
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                _directoryWatcher.Created += OnInitialFileCreated;
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Waiting for transcript file creation: {_initialTranscriptPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error starting directory watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the initial transcript file is first created.
        /// </summary>
        private void OnInitialFileCreated(object sender, FileSystemEventArgs e)
        {
            if (!IsActive) return;

            try
            {
                if (_directoryWatcher != null)
                {
                    _directoryWatcher.EnableRaisingEvents = false;
                    _directoryWatcher.Created -= OnInitialFileCreated;
                    _directoryWatcher.Dispose();
                    _directoryWatcher = null;
                }

                Thread.Sleep(50);
                AddTranscriptFile(_initialTranscriptPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error handling file creation: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle changes to a tracked transcript file.
        /// </summary>
        private void OnTrackedFileChanged(TrackedFile tracked)
        {
            if (!IsActive && !IsDormant) return;

            try
            {
                Thread.Sleep(50);
                ReadNewLinesFromFile(tracked);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error reading transcript changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Read new lines from a specific transcript file starting at its tracked position.
        /// Parses each line with AgentMessage.ParseLine and fires MessageReceived.
        /// <para>One pass at a time per tailer (<c>_readLock</c>, 925c8cff); the events a pass queues are
        /// raised after the lock is released.</para>
        /// </summary>
        private void ReadNewLinesFromFile(TrackedFile tracked)
        {
            bool completed;
            lock (_readLock)
            {
                completed = ReadNewLinesLocked(tracked);
            }

            if (completed)
                AfterCompleted();
            else
                DrainEvents();
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo), null in production: called at the end of every read pass
        /// that got past its checks, after the file position was updated and before the lock is released.
        /// </summary>
        internal Action ReadPassHook { get; set; }

        /// <summary>Test seam: whether the calling thread holds this tailer's read lock.</summary>
        internal bool ReadLockHeldByCurrentThread => Monitor.IsEntered(_readLock);

        /// <summary>The body of <see cref="ReadNewLinesFromFile"/>. Caller holds <c>_readLock</c>. True when this pass completed the agent.</summary>
        private bool ReadNewLinesLocked(TrackedFile tracked)
        {
            bool woke = false;
            bool completed = false;
            try
            {
                // A tailer stopped for good reads nothing more.
                if (_shutDown) return false;

                var fileInfo = new FileInfo(tracked.Path);
                if (!fileInfo.Exists) return false;

                if (fileInfo.Length <= tracked.LastPosition)
                {
                    if (fileInfo.Length < tracked.LastPosition)
                    {
                        tracked.LastPosition = 0;
                    }
                    else
                    {
                        return false;
                    }
                }

                using (var stream = new FileStream(tracked.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(tracked.LastPosition, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var (uuid, lineType, stopReason) = ReadLineMeta(line);

                            // GH #89: when the parent session changes worktree, the agent goes on in a
                            // new file that repeats some lines of the old one. A line already shown from
                            // another file is skipped; repeats inside one file are left alone.
                            if (uuid != null && IsRepeatFromAnotherFile(uuid, tracked.Path))
                                continue;

                            // A dormant member wakes on real work only: a user or assistant line, not the
                            // bookkeeping records Claude Code may append to an idle transcript.
                            if (_completed && (lineType == "user" || lineType == "assistant") && TryReactivate())
                                woke = true;

                            NoteTurnState(lineType, stopReason);

                            // A finish seen earlier in this pass is overtaken by later work (268acc24): the
                            // SubagentStop line, then a new prompt and the agent working on it, all in one
                            // read (a catch-up read at start-up, or a burst of appends) is a working agent.
                            if (_pendingStop && (lineType == "user" || lineType == "assistant"))
                                _pendingStop = false;

                            var parsedMessages = AgentMessage.ParseLine(line);
                            foreach (var msg in parsedMessages)
                            {
                                if (string.IsNullOrEmpty(SessionId) && !string.IsNullOrEmpty(msg.SessionId))
                                {
                                    SessionId = msg.SessionId;
                                }

                                // Skip echoed user messages that we already emitted locally in SendMessageAsync.
                                // Use a 30-second time window (not one-shot) because the transcript may produce
                                // multiple JSONL entries for the same user turn.
                                if (msg.Type == AgentMessageType.User
                                    && _lastSentUserContent != null
                                    && (DateTime.UtcNow - _lastSentUserTime).TotalSeconds < 30
                                    && (msg.Content == _lastSentUserContent
                                        || msg.Content?.Trim() == _lastSentUserContent.Trim()
                                        || (msg.Content != null && msg.Content.Contains(_lastSentUserContent))))
                                {
                                    continue; // Keep suppressing within the time window
                                }

                                // Expire the dedup window after 30 seconds
                                if (_lastSentUserContent != null
                                    && (DateTime.UtcNow - _lastSentUserTime).TotalSeconds >= 30)
                                {
                                    _lastSentUserContent = null;
                                }

                                AddMessage(msg);

                                QueueEvent(MessageReceived, h => h(this, msg));

                                // Detect team agent SendMessage calls for ChatPanel bridge
                                if (msg.Type == AgentMessageType.ToolUse &&
                                    msg.ToolName == "SendMessage" &&
                                    !string.IsNullOrEmpty(msg.Content))
                                {
                                    QueueEvent(TeamMessageSent, h => h(this, msg));
                                }

                                // Detect agent completion: a Result message means the turn is done.
                                // For members of a created team, don't auto-stop — they stay alive between turns.
                                if (msg.Type == AgentMessageType.Result && IsActive && !WaitsForExplicitStop)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Result message detected - agent completed");
                                    _pendingStop = true;
                                }
                            }

                            // Detect SubagentStop hook event: Claude Code transcripts end with a
                            // type:"progress" line containing hookType:"SubagentStop" nested in the
                            // message object. This is the actual last line written — the type:"result"
                            // check above never fires because transcripts never contain type:"result".
                            // Parse this raw line directly to avoid changing AgentMessage filtering logic.
                            if (!WaitsForExplicitStop && IsActive && IsSubagentStopLine(line))
                            {
                                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] SubagentStop progress event detected - agent completed");
                                _pendingStop = true;
                            }
                        }

                        if (stream.Position != tracked.LastPosition)
                            _lastGrowthUtc = DateTime.UtcNow;
                        tracked.LastPosition = stream.Position;
                    }
                }

                // Not for a tailer shut down meanwhile (its member removed): nobody should build a card for it.
                if (woke && !_shutDown)
                    QueueEvent(Reactivated, h => h(this, EventArgs.Empty));

                // Fire Stopped after finishing the read pass so all messages are delivered first
                if (_pendingStop && IsActive)
                {
                    _pendingStop = false;
                    completed = MarkCompleted();
                }

                ReadPassHook?.Invoke();
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] IO error reading {tracked.Path}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error reading {tracked.Path}: {ex.Message}");
            }
            return completed;
        }

        /// <summary>
        /// Returns true if the raw JSONL line is a SubagentStop hook event.
        /// Claude Code transcripts end with:
        ///   {"type":"progress","message":{"type":"progress","hookType":"SubagentStop",...}}
        /// This is the actual completion signal — type:"result" is never written to transcripts.
        /// </summary>
        private static bool IsSubagentStopLine(string jsonLine)
        {
            // Fast path: skip lines that don't contain SubagentStop at all
            if (!jsonLine.Contains("SubagentStop")) return false;

            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                // Must be type:"progress"
                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.GetString() != "progress")
                    return false;

                // Must have nested message.hookType == "SubagentStop"
                if (!root.TryGetProperty("message", out var msgProp)
                    || msgProp.ValueKind != JsonValueKind.Object)
                    return false;

                if (!msgProp.TryGetProperty("hookType", out var hookTypeProp))
                    return false;

                return hookTypeProp.GetString() == "SubagentStop";
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void DisposeWatchers()
        {
            try { _quietTimer?.Dispose(); } catch (ObjectDisposedException) { }
            _quietTimer = null;

            lock (_trackedFilesLock)
            {
                foreach (var tracked in _trackedFiles)
                {
                    if (tracked.Watcher != null)
                    {
                        tracked.Watcher.EnableRaisingEvents = false;
                        tracked.Watcher.Dispose();
                        tracked.Watcher = null;
                    }
                }
            }

            if (_directoryWatcher != null)
            {
                _directoryWatcher.EnableRaisingEvents = false;
                _directoryWatcher.Created -= OnInitialFileCreated;
                _directoryWatcher.Dispose();
                _directoryWatcher = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                bool alreadyFinished;
                lock (_completionLock)
                {
                    _shutDown = true;
                    IsActive = false;
                    alreadyFinished = _completed;
                }

                DisposeWatchers();

                // After Complete() has announced the finish, a later dispose (a root or team teardown)
                // must not send a second, failing exit that would close a panel kept open (GH #89).
                if (!alreadyFinished)
                    QueueEvent(Stopped, h => h(this, -1));
                DrainEvents();
            }
            _disposed = true;
        }

        /// <summary>
        /// Tracks state for a single JSONL file being tailed.
        /// </summary>
        private class TrackedFile
        {
            public string Path;
            public long LastPosition;
            public FileSystemWatcher Watcher;
        }
    }
}
