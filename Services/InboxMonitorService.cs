using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Watches the inbox directory for new/modified message files and fires events
    /// so MainForm can nudge idle terminals via ConPTY input injection.
    ///
    /// When Claude Code is idle (waiting for user input), no hooks fire and inbox
    /// messages go unread. This service detects inbox file creation and signals
    /// MainForm to inject a minimal "nudge" prompt into the target terminal,
    /// triggering the UserPromptSubmit hook which reads the inbox via inbox-check-hook.js.
    /// </summary>
    public class InboxMonitorService : IDisposable
    {
        private static readonly string InboxDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "multiterminal", "inbox");

        private FileSystemWatcher _watcher;
        private bool _disposed;
        private readonly object _lock = new object();
        private DebugLogService _log;

        // Debouncing: InboxFileWriter does temp-write + atomic File.Move(rename),
        // which can generate multiple Created/Changed events for one logical write.
        private readonly ConcurrentDictionary<string, DateTime> _lastEventTime = new();
        private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);

        // Short delay after file event to let the write complete and check if
        // a hook already consumed the file (Claude was active, not idle).
        private readonly TimeSpan _nudgeDelay = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Fired when an inbox file is created or modified for a terminal.
        /// The string parameter is the terminal name (filename without .json extension).
        /// This event fires on a ThreadPool thread - subscribers must marshal to UI thread.
        /// </summary>
        public event EventHandler<string> InboxFileDetected;

        /// <summary>
        /// Set the debug log service for internal debug panel logging.
        /// </summary>
        public DebugLogService DebugLogService { set => _log = value; }

        /// <summary>
        /// Start watching the inbox directory for new message files.
        /// </summary>
        public void StartWatching()
        {
            if (_disposed) return;

            // Ensure directory exists (InboxFileWriter also creates it, but be safe)
            Directory.CreateDirectory(InboxDir);

            _watcher = new FileSystemWatcher(InboxDir)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnInboxFileChanged;
            _watcher.Changed += OnInboxFileChanged;
            // File.Move (atomic rename from .tmp → .json) fires Renamed, not Created/Changed
            _watcher.Renamed += (s, e) => OnInboxFileChanged(s, e);

            _log?.Info("InboxMonitor", $"Started watching: {InboxDir}");
        }

        /// <summary>
        /// Stop watching the inbox directory.
        /// </summary>
        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnInboxFileChanged;
                _watcher.Changed -= OnInboxFileChanged;
                _watcher.Dispose();
                _watcher = null;
                _log?.Info("InboxMonitor", "Stopped watching");
            }
        }

        private void OnInboxFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_disposed) return;

            // Extract terminal name from filename (e.g., "Alice.json" -> "Alice")
            string fileName = Path.GetFileNameWithoutExtension(e.Name);
            if (string.IsNullOrEmpty(fileName)) return;

            // Skip .tmp files that somehow pass the filter
            if (e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;

            // Debounce: multiple filesystem events for one logical write
            var now = DateTime.UtcNow;
            if (_lastEventTime.TryGetValue(fileName, out var lastTime))
            {
                if (now - lastTime < _debounceInterval)
                {
                    _log?.Trace("InboxMonitor", $"Debounced event for {fileName}");
                    return;
                }
            }
            _lastEventTime[fileName] = now;

            // Fire event after a short delay to let the write complete
            Task.Run(async () =>
            {
                await Task.Delay(_nudgeDelay);

                if (_disposed) return;

                // Verify file still exists (may have been consumed by a hook already)
                string filePath = Path.Combine(InboxDir, fileName + ".json");
                if (!File.Exists(filePath))
                {
                    _log?.Info("InboxMonitor", $"File already consumed before nudge: {fileName}");
                    return;
                }

                _log?.Info("InboxMonitor", $"Inbox file detected for terminal: {fileName}");
                InboxFileDetected?.Invoke(this, fileName);
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            StopWatching();
            _lastEventTime.Clear();
        }
    }
}
