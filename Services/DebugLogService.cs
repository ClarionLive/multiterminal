using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private bool _isDisposed;

        // File-based persistence
        private readonly string _logFilePath;
        private readonly object _fileLock = new object();
        private StreamWriter _logWriter;

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
                _logWriter = new StreamWriter(_logFilePath, append: true, encoding: Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DebugLogService] Failed to create log file: {ex.Message}");
                _logFilePath = null;
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

            var entry = new DebugLogEntry(source, level, message);
            _logQueue.Enqueue(entry);
            WriteToFile(entry);

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
        /// </summary>
        public static List<string> ReadLogFile(string filePath, int lastNLines = 200)
        {
            if (!File.Exists(filePath))
                return new List<string>();

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
            WriteToFile(entry);

            // Auto-prune if queue exceeds max size
            while (_logQueue.Count > MaxQueueSize)
            {
                _logQueue.TryDequeue(out _);
            }

            // Raise event for UI update
            LogMessageAdded?.Invoke(this, entry);
        }

        /// <summary>
        /// Write a log entry to the file. Thread-safe via lock.
        /// </summary>
        private void WriteToFile(DebugLogEntry entry)
        {
            if (_logWriter == null) return;

            try
            {
                lock (_fileLock)
                {
                    _logWriter.WriteLine(entry.ToFullString());
                }
            }
            catch
            {
                // Don't let file I/O errors break logging
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            StopSystemWideCapture();

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
    }
}
