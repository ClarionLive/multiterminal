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

        // GH #89: which file first delivered each transcript line (by its uuid), so a line repeated
        // in the agent's continuation file is shown once.
        private readonly ConcurrentDictionary<string, string> _lineOrigins = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

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
            if (IsActive || _completed) return Task.CompletedTask;
            IsActive = true;

            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] StartAsync called, file={_initialTranscriptPath}");

            if (File.Exists(_initialTranscriptPath))
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] File exists, reading existing content...");
                AddTranscriptFile(_initialTranscriptPath);
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] After initial read: {_messages.Count} messages buffered");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] File does NOT exist yet, starting directory watcher");
                StartDirectoryWatcher();
            }

            _quietTimer ??= new Timer(_ => CheckQuietCompletion(DateTime.UtcNow), null, QuietCheckInterval, QuietCheckInterval);

            return Task.CompletedTask;
        }

        /// <summary>
        /// The agent has finished: stop tailing and raise <see cref="Stopped"/> with exit code 0.
        /// Called for the SubagentStop hook, the hook's line in the transcript, and the quiet fallback.
        /// Raises at most once; does nothing once the tailer has stopped.
        /// </summary>
        public void Complete()
        {
            lock (_completionLock)
            {
                if (!IsActive || _completed) return;
                IsActive = false;
                _completed = true;
            }

            DisposeWatchers();
            Stopped?.Invoke(this, 0);
        }

        /// <summary>
        /// The quiet fallback (GH #89): a non-team agent whose last turn ended and whose transcript has
        /// not grown for <see cref="QuietCompletionWindow"/> is complete. Team agents are exempt: they
        /// idle between turns by design.
        /// </summary>
        internal void CheckQuietCompletion(DateTime nowUtc)
        {
            if (!IsActive || IsTeamAgent || !_turnEnded) return;
            if (nowUtc - _lastGrowthUtc < QuietCompletionWindow) return;

            System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Turn ended and transcript quiet - agent completed");
            Complete();
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
            // must not restart a tailer that nothing could then stop.
            if (string.IsNullOrEmpty(transcriptPath) || _completed) return;

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
                _trackedFiles.Add(tracked);
            }

            // Read existing content
            ReadNewLinesFromFile(tracked);

            // The catch-up read itself can reach the finish (the SubagentStop line).
            if (_completed) return;

            // Start watching for appends
            StartFileWatcherForTracked(tracked);

            // Re-activate if was marked inactive
            IsActive = true;
        }

        /// <summary>
        /// Stop tailing and clean up watchers.
        /// </summary>
        public Task StopAsync()
        {
            if (!IsActive) return Task.CompletedTask;
            IsActive = false;

            DisposeWatchers();
            Stopped?.Invoke(this, -1);

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
            lock (_messagesLock)
            {
                _messages.Add(userMsg);
            }
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
            if (!IsActive) return;

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
        /// </summary>
        private void ReadNewLinesFromFile(TrackedFile tracked)
        {
            try
            {
                var fileInfo = new FileInfo(tracked.Path);
                if (!fileInfo.Exists) return;

                if (fileInfo.Length <= tracked.LastPosition)
                {
                    if (fileInfo.Length < tracked.LastPosition)
                    {
                        tracked.LastPosition = 0;
                    }
                    else
                    {
                        return;
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
                            if (uuid != null && !string.Equals(_lineOrigins.GetOrAdd(uuid, tracked.Path), tracked.Path, StringComparison.OrdinalIgnoreCase))
                                continue;

                            NoteTurnState(lineType, stopReason);

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

                                lock (_messagesLock)
                                {
                                    _messages.Add(msg);
                                }

                                MessageReceived?.Invoke(this, msg);

                                // Detect team agent SendMessage calls for ChatPanel bridge
                                if (msg.Type == AgentMessageType.ToolUse &&
                                    msg.ToolName == "SendMessage" &&
                                    !string.IsNullOrEmpty(msg.Content))
                                {
                                    TeamMessageSent?.Invoke(this, msg);
                                }

                                // Detect agent completion: a Result message means the turn is done.
                                // For team agents, don't auto-stop — they stay alive between turns.
                                if (msg.Type == AgentMessageType.Result && IsActive && !IsTeamAgent)
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
                            if (!IsTeamAgent && IsActive && IsSubagentStopLine(line))
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

                // Fire Stopped after finishing the read pass so all messages are delivered first
                if (_pendingStop && IsActive)
                {
                    _pendingStop = false;
                    Complete();
                }
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] IO error reading {tracked.Path}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranscriptTailer:{_agentName}] Error reading {tracked.Path}: {ex.Message}");
            }
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
                    IsActive = false;
                    alreadyFinished = _completed;
                }

                DisposeWatchers();

                // After Complete() has announced the finish, a later dispose (a root or team teardown)
                // must not send a second, failing exit that would close a panel kept open (GH #89).
                if (!alreadyFinished)
                    Stopped?.Invoke(this, -1);
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
