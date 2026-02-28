using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Thread-safe internal debug logging service for real-time collaborative debugging.
    /// Uses a non-blocking queue to avoid interfering with message delivery events.
    /// Supports both internal logging and system-wide OutputDebugString capture.
    /// </summary>
    public class DebugLogService : IDisposable
    {
        private readonly ConcurrentQueue<DebugLogEntry> _logQueue = new ConcurrentQueue<DebugLogEntry>();
        private const int MaxQueueSize = 10000;
        private OutputDebugStringListener _outputDebugListener;
        private bool _isDisposed;

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
        /// Pause logging - new entries will be silently discarded until Resume() is called.
        /// </summary>
        public void Pause() => IsPaused = true;

        /// <summary>
        /// Resume logging after a pause.
        /// </summary>
        public void Resume() => IsPaused = false;

        /// <summary>
        /// Logs a message to the debug queue.
        /// </summary>
        /// <param name="source">Source component (e.g., "MessageBroker", "MainForm").</param>
        /// <param name="level">Log level.</param>
        /// <param name="message">Log message content.</param>
        public void Log(string source, DebugLogLevel level, string message)
        {
            if (IsPaused) return;

            var entry = new DebugLogEntry(source, level, message);
            _logQueue.Enqueue(entry);

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
        /// Gets all current log entries (snapshot).
        /// </summary>
        public List<DebugLogEntry> GetMessages()
        {
            return _logQueue.ToList();
        }

        /// <summary>
        /// Clears all log entries.
        /// </summary>
        public void Clear()
        {
            while (_logQueue.TryDequeue(out _)) { }
            LogCleared?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Gets the current count of log entries.
        /// </summary>
        public int Count => _logQueue.Count;

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

                // Log that system-wide capture started
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

                // Log that system-wide capture stopped
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
            // Create a log entry with process information
            var entry = new DebugLogEntry(
                "OutputDebugString",
                DebugLogLevel.Info,
                e.Message,
                e.ProcessId,
                e.ProcessName);

            _logQueue.Enqueue(entry);

            // Auto-prune if queue exceeds max size
            while (_logQueue.Count > MaxQueueSize)
            {
                _logQueue.TryDequeue(out _);
            }

            // Raise event for UI update
            LogMessageAdded?.Invoke(this, entry);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            StopSystemWideCapture();
        }
    }
}
