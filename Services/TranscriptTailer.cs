using System;
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

        // Track the last user message we sent, to deduplicate echo(es) from the transcript.
        // Uses a time window instead of one-shot because the transcript may produce multiple
        // JSONL entries for the same user turn (e.g., message + content echo).
        private string _lastSentUserContent;
        private DateTime _lastSentUserTime;

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
            if (IsActive) return Task.CompletedTask;
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

            return Task.CompletedTask;
        }

        /// <summary>
        /// Add an additional transcript file to tail. Used when the same agent
        /// creates a new JSONL file on subsequent wake-ups.
        /// Reads existing content immediately and watches for new appends.
        /// </summary>
        public void AddTranscriptFile(string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath)) return;

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

                        tracked.LastPosition = stream.Position;
                    }
                }

                // Fire Stopped after finishing the read pass so all messages are delivered first
                if (_pendingStop && IsActive)
                {
                    _pendingStop = false;
                    IsActive = false;
                    DisposeWatchers();
                    Stopped?.Invoke(this, 0);
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
            if (_disposed) return;
            _disposed = true;

            IsActive = false;
            DisposeWatchers();
            Stopped?.Invoke(this, -1);
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
