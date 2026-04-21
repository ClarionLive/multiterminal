using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Coordinates multi-instance state via Pool Messages.
    /// Tracks AFFECTS (soft file locks) and BLOCKS (dependencies).
    /// Based on claude-cognitive's Pool Coordinator pattern.
    /// </summary>
    public class PoolCoordinator : IDisposable
    {
        private readonly string _poolDir;
        private readonly string _stateFile;
        private readonly ConcurrentDictionary<string, FileOwnership> _activeFiles;
        private readonly ConcurrentDictionary<string, HashSet<string>> _blockedInstances;
        private readonly HashSet<string> _processedMessageIds;
        private FileSystemWatcher _fileWatcher;
        private long _lastFilePosition;
        private bool _isDisposed;

        /// <summary>
        /// Event raised when a file conflict is detected.
        /// </summary>
        public event EventHandler<FileConflictEventArgs> FileConflictDetected;

        /// <summary>
        /// Event raised when a LEARNED message is recorded.
        /// Subscribe to persist to SQLite for semantic search indexing.
        /// </summary>
        public event EventHandler<LearnedMessageEventArgs> LearnedMessageRecorded;

        /// <summary>
        /// Event raised when ANY pool message is recorded (from file or API).
        /// Used for real-time Activity panel updates.
        /// </summary>
        public event EventHandler<PoolMessageEventArgs> PoolMessageRecorded;

        public PoolCoordinator(string poolDir = null)
        {
            _poolDir = poolDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "pool");
            _stateFile = Path.Combine(_poolDir, "state.jsonl");
            _activeFiles = new ConcurrentDictionary<string, FileOwnership>();
            _blockedInstances = new ConcurrentDictionary<string, HashSet<string>>();
            _processedMessageIds = new HashSet<string>();

            EnsurePoolDirectory();
            LoadState();
            StartFileWatcher();
        }

        private void EnsurePoolDirectory()
        {
            if (!Directory.Exists(_poolDir))
            {
                Directory.CreateDirectory(_poolDir);
            }
        }

        /// <summary>
        /// Start watching the state file for external changes (e.g., from hooks).
        /// </summary>
        private void StartFileWatcher()
        {
            try
            {
                // Record current file position so we only process new content
                if (File.Exists(_stateFile))
                {
                    var fileInfo = new FileInfo(_stateFile);
                    _lastFilePosition = fileInfo.Length;
                }

                _fileWatcher = new FileSystemWatcher(_poolDir)
                {
                    Filter = "state.jsonl",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnStateFileChanged;
                System.Diagnostics.Debug.WriteLine($"[PoolCoordinator] Started file watcher for {_stateFile}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PoolCoordinator] Error starting file watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle external changes to the state file.
        /// </summary>
        private void OnStateFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Small delay to ensure file write is complete
                System.Threading.Thread.Sleep(50);

                var fileInfo = new FileInfo(_stateFile);
                if (fileInfo.Length <= _lastFilePosition)
                {
                    // File was truncated or hasn't grown - reset position
                    if (fileInfo.Length < _lastFilePosition)
                    {
                        _lastFilePosition = 0;
                    }
                    return;
                }

                // Read only new content from the file
                using (var stream = new FileStream(_stateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(_lastFilePosition, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            try
                            {
                                var msg = PoolMessage.FromJson(line);

                                // Skip if we've already processed this message
                                if (_processedMessageIds.Contains(msg.Id))
                                    continue;

                                _processedMessageIds.Add(msg.Id);

                                // Process the message (updates internal state)
                                ProcessMessage(msg, persist: false);

                                // Raise event for Activity panel real-time updates
                                RaisePoolMessageEvent(msg);

                                System.Diagnostics.Debug.WriteLine($"[PoolCoordinator] File watcher processed: {msg.Action} from {msg.Instance}");
                            }
                            catch (Exception parseEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[PoolCoordinator] Error parsing line: {parseEx.Message}");
                            }
                        }

                        _lastFilePosition = stream.Position;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PoolCoordinator] Error processing file change: {ex.Message}");
            }
        }

        /// <summary>
        /// Raise the PoolMessageRecorded event for any message type.
        /// </summary>
        private void RaisePoolMessageEvent(PoolMessage message)
        {
            var timestamp = message.Timestamp > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).DateTime
                : DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).DateTime;

            PoolMessageRecorded?.Invoke(this, new PoolMessageEventArgs
            {
                MessageId = message.Id,
                Instance = message.Instance,
                Action = message.Action,
                Topic = message.Topic,
                Summary = message.Summary,
                Tags = message.Tags ?? new List<string>(),
                Affects = message.Affects ?? new List<string>(),
                Blocks = message.Blocks ?? new List<string>(),
                BlockedBy = message.BlockedBy ?? new List<string>(),
                Timestamp = timestamp
            });
        }

        /// <summary>
        /// Load existing state from JSONL file.
        /// </summary>
        private void LoadState()
        {
            if (!File.Exists(_stateFile)) return;

            try
            {
                foreach (var line in File.ReadLines(_stateFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var msg = PoolMessage.FromJson(line);
                    _processedMessageIds.Add(msg.Id);
                    ProcessMessage(msg, persist: false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PoolCoordinator] Error loading state: {ex.Message}");
            }
        }

        /// <summary>
        /// Record a pool message and update tracking state.
        /// </summary>
        public PoolMessageResult Record(PoolMessage message)
        {
            var result = ValidateMessage(message);
            if (!result.Success) return result;

            // Process and persist
            ProcessMessage(message, persist: true);

            return new PoolMessageResult
            {
                Success = true,
                MessageId = message.Id,
                Warnings = result.Warnings
            };
        }

        /// <summary>
        /// Validate a message before recording.
        /// </summary>
        private PoolMessageResult ValidateMessage(PoolMessage message)
        {
            var result = new PoolMessageResult { Success = true };
            var warnings = new List<string>();

            // Check for AFFECTS conflicts
            if (message.Affects?.Count > 0 && message.Action == PoolAction.WORKING_ON)
            {
                foreach (var file in message.Affects)
                {
                    if (_activeFiles.TryGetValue(file, out var owner) &&
                        owner.Instance != message.Instance &&
                        !owner.IsStale())
                    {
                        warnings.Add($"File '{file}' is being worked on by {owner.Instance} (since {owner.Since:HH:mm:ss})");

                        FileConflictDetected?.Invoke(this, new FileConflictEventArgs
                        {
                            File = file,
                            CurrentOwner = owner.Instance,
                            RequestingInstance = message.Instance
                        });
                    }
                }
            }

            result.Warnings = warnings;
            return result;
        }

        /// <summary>
        /// Process a message and update internal state.
        /// </summary>
        private void ProcessMessage(PoolMessage message, bool persist)
        {
            switch (message.Action)
            {
                case PoolAction.WORKING_ON:
                    // Register file ownership
                    // Handle both seconds and milliseconds timestamps (>1 trillion = milliseconds)
                    var workingTimestamp = message.Timestamp > 1_000_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).DateTime
                        : DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).DateTime;
                    foreach (var file in message.Affects ?? Enumerable.Empty<string>())
                    {
                        _activeFiles[file] = new FileOwnership
                        {
                            Instance = message.Instance,
                            Since = workingTimestamp
                        };
                    }
                    break;

                case PoolAction.COMPLETED:
                    // Release file ownership for this instance
                    foreach (var file in message.Affects ?? Enumerable.Empty<string>())
                    {
                        if (_activeFiles.TryGetValue(file, out var owner) &&
                            owner.Instance == message.Instance)
                        {
                            _activeFiles.TryRemove(file, out _);
                        }
                    }
                    // Unblock waiting instances
                    foreach (var blocked in message.Blocks ?? Enumerable.Empty<string>())
                    {
                        if (_blockedInstances.TryGetValue(blocked, out var blockers))
                        {
                            blockers.Remove(message.Instance);
                        }
                    }
                    break;

                case PoolAction.BLOCKED_BY:
                    // Track blocked state
                    var waitingOn = message.BlockedBy ?? new List<string>();
                    _blockedInstances.AddOrUpdate(
                        message.Instance,
                        new HashSet<string>(waitingOn),
                        (_, existing) => { foreach (var b in waitingOn) existing.Add(b); return existing; });
                    break;

                case PoolAction.LEARNED:
                    // LEARNED messages don't affect coordination state
                    // Raise event for semantic search indexing
                    // Handle both seconds and milliseconds timestamps (>1 trillion = milliseconds)
                    var learnedTimestamp = message.Timestamp > 1_000_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).DateTime
                        : DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).DateTime;
                    LearnedMessageRecorded?.Invoke(this, new LearnedMessageEventArgs
                    {
                        MessageId = message.Id,
                        Instance = message.Instance,
                        Topic = message.Topic,
                        Summary = message.Summary,
                        Tags = message.Tags ?? new List<string>(),
                        Timestamp = learnedTimestamp
                    });
                    break;
            }

            if (persist)
            {
                AppendToStateFile(message);
            }
        }

        /// <summary>
        /// Append message to JSONL state file.
        /// </summary>
        private void AppendToStateFile(PoolMessage message)
        {
            try
            {
                File.AppendAllText(_stateFile, message.ToJson() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PoolCoordinator] Error appending to state file: {ex.Message}");
            }
        }

        /// <summary>
        /// Get files currently being worked on by any instance.
        /// </summary>
        public Dictionary<string, string> GetActiveFiles()
        {
            return _activeFiles
                .Where(kv => !kv.Value.IsStale())
                .ToDictionary(kv => kv.Key, kv => kv.Value.Instance);
        }

        /// <summary>
        /// Check if an instance is blocked.
        /// </summary>
        public bool IsBlocked(string instance, out List<string> blockers)
        {
            if (_blockedInstances.TryGetValue(instance, out var set) && set.Count > 0)
            {
                blockers = set.ToList();
                return true;
            }
            blockers = new List<string>();
            return false;
        }

        /// <summary>
        /// Get recent pool messages for context injection.
        /// </summary>
        public List<PoolMessage> GetRecentMessages(int count = 20)
        {
            if (!File.Exists(_stateFile)) return new List<PoolMessage>();

            try
            {
                return File.ReadLines(_stateFile)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(count)
                    .Select(PoolMessage.FromJson)
                    .ToList();
            }
            catch
            {
                return new List<PoolMessage>();
            }
        }

        /// <summary>
        /// Format recent messages for context injection.
        /// </summary>
        public string FormatForInjection(int count = 10)
        {
            var messages = GetRecentMessages(count);
            if (messages.Count == 0) return string.Empty;

            var lines = messages.Select(m => m.Action switch
            {
                PoolAction.WORKING_ON => $"[{m.Instance}] working on: {m.Topic} (affects: {string.Join(", ", m.Affects)})",
                PoolAction.COMPLETED => $"[{m.Instance}] completed: {m.Topic}",
                PoolAction.BLOCKED_BY => $"[{m.Instance}] blocked by: {string.Join(", ", m.BlockedBy)} - {m.Summary}",
                PoolAction.LEARNED => $"[{m.Instance}] learned: {m.Topic} [{string.Join(", ", m.Tags)}]",
                _ => $"[{m.Instance}] {m.Action}: {m.Topic}"
            });

            return "## Recent Pool Activity\n" + string.Join("\n", lines);
        }

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
                if (_fileWatcher != null)
                {
                    _fileWatcher.EnableRaisingEvents = false;
                    _fileWatcher.Changed -= OnStateFileChanged;
                    _fileWatcher.Dispose();
                    _fileWatcher = null;
                }
            }
            _isDisposed = true;
        }
    }

    /// <summary>
    /// Tracks file ownership for AFFECTS coordination.
    /// </summary>
    public class FileOwnership
    {
        public string Instance { get; set; }
        public DateTime Since { get; set; }

        /// <summary>
        /// Ownership is stale after 30 minutes without update.
        /// </summary>
        public bool IsStale() => DateTime.UtcNow - Since > TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Result of recording a pool message.
    /// </summary>
    public class PoolMessageResult
    {
        public bool Success { get; set; }
        public string MessageId { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Event args for file conflict detection.
    /// </summary>
    public class FileConflictEventArgs : EventArgs
    {
        public string File { get; set; }
        public string CurrentOwner { get; set; }
        public string RequestingInstance { get; set; }
    }

    /// <summary>
    /// Event args for LEARNED message recording.
    /// Used to persist to SQLite for semantic search indexing.
    /// </summary>
    public class LearnedMessageEventArgs : EventArgs
    {
        public string MessageId { get; set; }
        public string Instance { get; set; }
        public string Topic { get; set; }
        public string Summary { get; set; }
        public List<string> Tags { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event args for any pool message (used for real-time Activity panel updates).
    /// </summary>
    public class PoolMessageEventArgs : EventArgs
    {
        public string MessageId { get; set; }
        public string Instance { get; set; }
        public PoolAction Action { get; set; }
        public string Topic { get; set; }
        public string Summary { get; set; }
        public List<string> Tags { get; set; }
        public List<string> Affects { get; set; }
        public List<string> Blocks { get; set; }
        public List<string> BlockedBy { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
