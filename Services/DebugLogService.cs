using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Thread-safe internal debug logging service for real-time collaborative debugging.
    /// Uses a non-blocking queue to avoid interfering with message delivery events.
    /// Supports both internal logging and system-wide OutputDebugString capture.
    /// Persists all log entries to a timestamped file in %APPDATA%\MultiTerminal\logs\.
    /// </summary>
    public class DebugLogService : IDisposable
    {
        private readonly ConcurrentQueue<DebugLogEntry> _logQueue = new ConcurrentQueue<DebugLogEntry>();
        private const int MaxQueueSize = 10000;
        private OutputDebugStringListener _outputDebugListener;
        private volatile bool _isDisposed;

        // File-based persistence. Writes are BUFFERED: callers enqueue onto _writeQueue (lock-free,
        // never blocks the caller) and a single background writer thread drains it in batches and
        // flushes once per batch. This decouples file I/O from hot-path caller threads (broker
        // routing, watchers, timers) — before the c425e3a2 logging sweep these Debug/Trace.WriteLine
        // sites compiled out in Release; converting them to an always-on SYNCHRONOUS flush-per-call
        // (the old AutoFlush=true + locked WriteLine model) would regress exactly those hot paths.
        private readonly string _logFilePath;
        private readonly object _fileLock = new object();
        private StreamWriter _logWriter;

        // Background file-writer pipeline. _writeQueue is BOUNDED (capacity WriteQueueCapacity) so a
        // log storm or a stalled disk can't grow it without bound and exhaust process memory — the old
        // synchronous write imposed natural backpressure; the buffered writer must impose an explicit
        // one. Callers use TryAdd (never Add) so they still NEVER block: when the queue is full the
        // entry is DROPPED and counted. Only populated when file logging actually initialized
        // (_fileLoggingEnabled) so a failed log-file open doesn't allocate a queue at all.
        private readonly BlockingCollection<DebugLogEntry> _writeQueue;
        private readonly Thread _writerThread;
        private readonly bool _fileLoggingEnabled;
        private const int WriterFlushIntervalMs = 1000;
        private const int WriteQueueCapacity = 50000;

        // Overload telemetry: entries dropped because _writeQueue was full. A silent drop is a log that
        // lies (a storm becomes invisible in exactly the log you'd check), so the writer periodically
        // emits a "N log entries dropped under load" line of its own. _droppedTotal is the running total;
        // _droppedReported is what the writer has already announced.
        private long _droppedTotal;
        private long _droppedReported;

        /// <summary>
        /// Raised when a new log message is added (on UI thread-safe context).
        /// </summary>
        public event EventHandler<DebugLogEntry> LogMessageAdded;

        /// <summary>
        /// Raised when the log is cleared (e.g., via API or MCP tool).
        /// </summary>
        public event EventHandler LogCleared;

        /// <summary>
        /// Whether system-wide OutputDebugString capture is enabled.
        /// </summary>
        public bool IsSystemWideCapture { get; private set; }

        /// <summary>
        /// Whether logging is paused. When paused, new log entries are silently discarded.
        /// </summary>
        public bool IsPaused { get; private set; }

        /// <summary>
        /// Minimum level a <see cref="Log"/> call must meet to be recorded; entries below this are
        /// dropped BEFORE enqueue (no UI entry, no file write, no event) — the cheap-exit that makes
        /// the c425e3a2 sweep's "downgrade hot-path noise to Trace" actually reduce Release volume.
        /// Defaults to Trace in Debug builds (everything visible) and Info in Release (Trace skipped).
        /// Settable at runtime so diagnostics can lower the floor in a Release session on demand.
        /// Backed by a volatile field: it's read on every Log() call from many threads (broker,
        /// watchers, timers) and written from a UI/diagnostic thread; volatile makes the cross-thread
        /// visibility of a floor change explicit (a stale read is benign — enum reads are atomic).
        /// This does not regress any user-visible baseline: pre-sweep, Debug.WriteLine compiled out
        /// of Release entirely and Trace.WriteLine fired only to (empty) trace listeners, so there is
        /// no existing visible Trace output for the Info floor to hide.
        /// </summary>
#if DEBUG
        private volatile DebugLogLevel _minimumLevel = DebugLogLevel.Trace;
#else
        private volatile DebugLogLevel _minimumLevel = DebugLogLevel.Info;
#endif
        public DebugLogLevel MinimumLevel
        {
            get => _minimumLevel;
            set => _minimumLevel = value;
        }

        /// <summary>
        /// The full path to the current session's log file.
        /// </summary>
        public string LogFilePath => _logFilePath;

        /// <summary>
        /// The directory where all log files are stored.
        /// </summary>
        public static string LogDirectory
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "MultiTerminal", "logs");
            }
        }

        public DebugLogService()
        {
            // Create log file with session start timestamp
            string logDir = LogDirectory;
            try
            {
                Directory.CreateDirectory(logDir);
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                _logFilePath = Path.Combine(logDir, $"debug-{timestamp}.log");

                // AutoFlush=false: the background writer flushes once per drained batch, not once
                // per line. Per-line synchronous flush is the hot-path cost the buffering removes.
                _logWriter = new StreamWriter(_logFilePath, append: true, encoding: Encoding.UTF8)
                {
                    AutoFlush = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DebugLogService] Failed to create log file: {ex.Message}");
                _logFilePath = null;
                _logWriter = null;
            }

            _fileLoggingEnabled = _logWriter != null;
            if (_fileLoggingEnabled)
            {
                _writeQueue = new BlockingCollection<DebugLogEntry>(new ConcurrentQueue<DebugLogEntry>(), WriteQueueCapacity);
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "DebugLogService.Writer",
                };
                _writerThread.Start();
            }
        }

        /// <summary>
        /// Pause logging - new entries will be silently discarded until Resume() is called.
        /// </summary>
        public void Pause() => IsPaused = true;

        /// <summary>
        /// Resume logging after a pause.
        /// </summary>
        public void Resume() => IsPaused = false;

        /// <summary>
        /// Logs a message to the debug queue and persists to file.
        /// </summary>
        public void Log(string source, DebugLogLevel level, string message)
        {
            if (IsPaused) return;
            if (level < MinimumLevel) return;

            var entry = new DebugLogEntry(source, level, message);
            _logQueue.Enqueue(entry);
            EnqueueWrite(entry);

            // Auto-prune if queue exceeds max size
            while (_logQueue.Count > MaxQueueSize)
            {
                _logQueue.TryDequeue(out _);
            }

            // Raise event for UI update (subscribers should marshal to UI thread if needed)
            LogMessageAdded?.Invoke(this, entry);
        }

        /// <summary>
        /// Convenience method for Trace level logs.
        /// </summary>
        public void Trace(string source, string message)
        {
            Log(source, DebugLogLevel.Trace, message);
        }

        /// <summary>
        /// Convenience method for Info level logs.
        /// </summary>
        public void Info(string source, string message)
        {
            Log(source, DebugLogLevel.Info, message);
        }

        /// <summary>
        /// Convenience method for Warning level logs.
        /// </summary>
        public void Warning(string source, string message)
        {
            Log(source, DebugLogLevel.Warning, message);
        }

        /// <summary>
        /// Convenience method for Error level logs.
        /// </summary>
        public void Error(string source, string message)
        {
            Log(source, DebugLogLevel.Error, message);
        }

        /// <summary>
        /// Gets all current log entries (snapshot from in-memory queue).
        /// </summary>
        public List<DebugLogEntry> GetMessages()
        {
            return _logQueue.ToList();
        }

        /// <summary>
        /// Clears the in-memory log entries. File log is not affected.
        /// </summary>
        public void Clear()
        {
            while (_logQueue.TryDequeue(out _)) { }
            LogCleared?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Gets the current count of log entries in memory.
        /// </summary>
        public int Count => _logQueue.Count;

        /// <summary>
        /// Lists all available log files, most recent first.
        /// </summary>
        public static List<string> ListLogFiles()
        {
            string logDir = LogDirectory;
            if (!Directory.Exists(logDir))
                return new List<string>();

            return Directory.GetFiles(logDir, "debug-*.log")
                .OrderByDescending(f => f)
                .ToList();
        }

        /// <summary>
        /// Reads lines from a log file. Returns the last N lines by default.
        /// Callers are responsible for ensuring <paramref name="filePath"/> is sanitized
        /// (the sole caller — DebugController.ReadLogFile — canonicalizes and root-checks
        /// against <see cref="LogDirectory"/> before invoking).
        /// </summary>
        public static List<string> ReadLogFile(string filePath, int lastNLines = 200)
        {
            // CA3003: filePath is sanitized by the caller (DebugController validates via
            // Path.GetFullPath + StartsWith(LogDirectory) before passing here). No direct user
            // input reaches this sink.
#pragma warning disable CA3003
            if (!File.Exists(filePath))
                return new List<string>();
#pragma warning restore CA3003

            try
            {
                // Read with sharing so the active log can be read while being written
                var lines = new List<string>();
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }

                if (lastNLines > 0 && lines.Count > lastNLines)
                    return lines.Skip(lines.Count - lastNLines).ToList();

                return lines;
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Starts capturing OutputDebugString messages system-wide.
        /// </summary>
        public void StartSystemWideCapture()
        {
            if (_isDisposed || IsSystemWideCapture)
                return;

            try
            {
                _outputDebugListener = new OutputDebugStringListener();
                _outputDebugListener.MessageReceived += OnOutputDebugStringMessage;
                _outputDebugListener.Start();
                IsSystemWideCapture = true;

                Info("DebugLogService", "System-wide OutputDebugString capture started");
            }
            catch (Exception ex)
            {
                Error("DebugLogService", $"Failed to start system-wide capture: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops capturing OutputDebugString messages system-wide.
        /// </summary>
        public void StopSystemWideCapture()
        {
            if (_isDisposed || !IsSystemWideCapture)
                return;

            try
            {
                if (_outputDebugListener != null)
                {
                    _outputDebugListener.MessageReceived -= OnOutputDebugStringMessage;
                    _outputDebugListener.Stop();
                    _outputDebugListener.Dispose();
                    _outputDebugListener = null;
                }
                IsSystemWideCapture = false;

                Info("DebugLogService", "System-wide OutputDebugString capture stopped");
            }
            catch (Exception ex)
            {
                Error("DebugLogService", $"Failed to stop system-wide capture: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles OutputDebugString messages from the listener.
        /// </summary>
        private void OnOutputDebugStringMessage(object sender, OutputDebugStringEventArgs e)
        {
            var entry = new DebugLogEntry(
                "OutputDebugString",
                DebugLogLevel.Info,
                e.Message,
                e.ProcessId,
                e.ProcessName);

            _logQueue.Enqueue(entry);
            EnqueueWrite(entry);

            // Auto-prune if queue exceeds max size
            while (_logQueue.Count > MaxQueueSize)
            {
                _logQueue.TryDequeue(out _);
            }

            // Raise event for UI update
            LogMessageAdded?.Invoke(this, entry);
        }

        /// <summary>
        /// Hand a log entry to the background writer. Non-blocking by construction: TryAdd never
        /// blocks (returns false immediately when the bounded queue is full) so a hot-path caller is
        /// never stalled by a slow disk — the entry is dropped and counted instead, and the writer
        /// later announces the drop count. Also safe after shutdown: TryAdd throws
        /// InvalidOperationException after CompleteAdding and ObjectDisposedException after
        /// Dispose — and ObjectDisposedException DERIVES FROM InvalidOperationException, so the single
        /// catch below swallows BOTH shutdown races (a late log call can't escape an exception to its
        /// caller). This is why there is no separate _isDisposed guard on the log path.
        /// </summary>
        private void EnqueueWrite(DebugLogEntry entry)
        {
            if (!_fileLoggingEnabled || _writeQueue == null) return;

            try
            {
                if (!_writeQueue.TryAdd(entry))
                {
                    // Queue full (log storm / stalled writer): drop, don't block the caller. Counted;
                    // the writer emits a periodic "N dropped under load" line so the loss is visible.
                    System.Threading.Interlocked.Increment(ref _droppedTotal);
                }
            }
            catch (InvalidOperationException)
            {
                // Add-after-CompleteAdding (InvalidOperationException) or add-after-Dispose
                // (ObjectDisposedException, which derives from it) during shutdown — safe to drop.
            }
        }

        /// <summary>
        /// Background writer loop. Blocks up to <see cref="WriterFlushIntervalMs"/> for the next
        /// entry, then drains every entry currently available and flushes ONCE per batch. This is
        /// what turns N synchronous per-line flushes into one flush per batch (the buffering win).
        /// Exits when the queue is completed (Dispose) and fully drained.
        /// </summary>
        private void WriterLoop()
        {
            try
            {
                while (_writeQueue.TryTake(out var entry, WriterFlushIntervalMs))
                {
                    lock (_fileLock)
                    {
                        if (_logWriter == null) continue;
                        try
                        {
                            _logWriter.WriteLine(entry.ToFullString());
                            // Drain everything else already queued into the same flush.
                            while (_writeQueue.TryTake(out var more))
                            {
                                _logWriter.WriteLine(more.ToFullString());
                            }
                            // If entries were dropped under load since the last announcement, record
                            // the count here (written directly, not re-enqueued — a re-enqueue could
                            // itself be dropped, and we already hold the writer lock).
                            long dropped = System.Threading.Interlocked.Read(ref _droppedTotal);
                            if (dropped > _droppedReported)
                            {
                                _logWriter.WriteLine(new DebugLogEntry(
                                    "DebugLogService", DebugLogLevel.Warning,
                                    $"{dropped - _droppedReported} log entr(y/ies) dropped under load (write queue full, cap {WriteQueueCapacity}); {dropped} total dropped this session").ToFullString());
                                _droppedReported = dropped;
                            }
                            _logWriter.Flush();
                        }
                        catch
                        {
                            // Don't let file I/O errors kill the writer thread.
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // _writeQueue disposed during shutdown race — nothing left to write.
            }
            catch (OperationCanceledException)
            {
                // CompleteAdding signalled mid-take — treat as clean stop.
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (disposing)
            {
                StopSystemWideCapture();

                // Stop accepting new entries and let the writer drain what's queued, then exit.
                // Bounded join so a hung disk can't block application shutdown; the writer is a
                // background thread, so if it doesn't finish in time the OS reclaims it on exit.
                _writeQueue?.CompleteAdding();
                bool joined = _writerThread == null || _writerThread.Join(TimeSpan.FromSeconds(3));

                if (joined)
                {
                    lock (_fileLock)
                    {
                        if (_logWriter != null)
                        {
                            try
                            {
                                _logWriter.Flush();
                                _logWriter.Dispose();
                            }
                            catch { }
                            _logWriter = null;
                        }
                    }
                }

                _writeQueue?.Dispose();
            }
        }
    }
}
