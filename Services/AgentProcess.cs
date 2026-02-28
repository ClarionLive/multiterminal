using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages a headless Claude Code child process with piped stdin/stdout.
    /// Spawns Claude with stream-json I/O, parses NDJSON output into AgentMessages,
    /// and provides methods to send messages, interrupt, and stop the process.
    /// This is the "tmux for Claude Code" foundation — 100% reliable piped I/O
    /// as an alternative to ConPTY terminal injection.
    /// </summary>
    public class AgentProcess : IAgentMessageSource, IDisposable
    {
        private Process _process;
        private StreamWriter _stdinWriter;
        private readonly List<AgentMessage> _messages = new List<AgentMessage>();
        private readonly object _messagesLock = new object();
        private Task _stdoutReadTask;
        private Task _stderrReadTask;
        private CancellationTokenSource _cts;
        private bool _isDisposed;

        // Track message content counts for deduplication with --include-partial-messages
        // (full message objects are re-emitted with accumulated content blocks)
        private string _lastMessageId;
        private int _lastContentCount;

        // Track the last user message we sent, to deduplicate the echo from --replay-user-messages
        private string _lastSentUserContent;

        /// <summary>
        /// Session ID extracted from Claude's output stream.
        /// Null until the first message carrying a session_id is received.
        /// </summary>
        public string SessionId { get; private set; }

        /// <summary>
        /// Whether the underlying process is still running.
        /// </summary>
        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>
        /// The OS process ID of the spawned Claude Code process.
        /// </summary>
        public int ProcessId => _process?.Id ?? -1;

        /// <summary>
        /// Thread-safe read-only view of all received messages.
        /// </summary>
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

        /// <summary>
        /// Fired when a new message is parsed from the output stream.
        /// </summary>
        public event EventHandler<AgentMessage> MessageReceived;

        /// <summary>
        /// Fired when the Claude Code process exits.
        /// </summary>
        public event EventHandler<int> ProcessExited;

        /// <summary>
        /// IAgentMessageSource.Stopped — mirrors ProcessExited for interface compatibility.
        /// </summary>
        public event EventHandler<int> Stopped;

        /// <summary>
        /// IAgentMessageSource.IsActive — whether the process is running.
        /// </summary>
        public bool IsActive => IsRunning;

        /// <summary>
        /// Spawn a headless Claude Code process with piped stdin/stdout.
        /// </summary>
        /// <param name="prompt">Initial prompt to send to Claude. If null, Claude starts waiting for input.</param>
        /// <param name="workingDir">Working directory for the process.</param>
        /// <param name="mcpConfigPath">Optional path to MCP config file (--mcp-config flag).</param>
        /// <param name="sessionId">Optional session ID to resume (--resume flag).</param>
        /// <param name="permissionMode">Optional permission mode (--permission-mode flag). Defaults to bypass for headless agents.</param>
        public async Task SpawnAsync(
            string prompt = null,
            string workingDir = null,
            string mcpConfigPath = null,
            string sessionId = null,
            string permissionMode = "bypassPermissions")
        {
            if (_process != null)
                throw new InvalidOperationException("AgentProcess is already running. Call StopAsync() first.");

            _cts = new CancellationTokenSource();

            // Build argument list
            var args = new List<string>
            {
                "-p",                              // Print mode (non-interactive)
                "--output-format", "stream-json",  // NDJSON output
                "--input-format", "stream-json",   // JSON input
                "--verbose",                       // Include tool details in output
                "--include-partial-messages",      // Streaming fidelity for live rendering
                "--replay-user-messages"           // Include user messages on resume
            };

            if (!string.IsNullOrEmpty(mcpConfigPath))
            {
                args.Add("--mcp-config");
                args.Add(mcpConfigPath);
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                args.Add("--resume");
                args.Add(sessionId);
            }

            if (!string.IsNullOrEmpty(permissionMode))
            {
                args.Add("--permission-mode");
                args.Add(permissionMode);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c claude " + string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory()
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.Start();

            _stdinWriter = _process.StandardInput;
            _stdinWriter.AutoFlush = false; // We flush explicitly after each write

            // Start background reading tasks
            _stdoutReadTask = Task.Run(() => ReadStdoutLoop(_process.StandardOutput, _cts.Token));
            _stderrReadTask = Task.Run(() => ReadStderrLoop(_process.StandardError, _cts.Token));

            // If an initial prompt was provided, send it as the first user message
            if (!string.IsNullOrEmpty(prompt))
            {
                await SendMessageAsync(prompt);
            }
        }

        /// <summary>
        /// Send a user message to the Claude process via stdin.
        /// Uses the stream-json input protocol: {"type":"user","message":{"role":"user","content":"..."}}\n
        /// Immediately emits a local User AgentMessage so the UI shows the prompt
        /// before the assistant's streaming response arrives.
        /// </summary>
        public async Task SendMessageAsync(string content)
        {
            if (!IsRunning)
                throw new InvalidOperationException("Cannot send message — process is not running.");

            // Emit user message immediately for correct ordering in the UI
            // (the echo from --replay-user-messages arrives after streaming starts)
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
            // Track for dedup when the echo arrives
            _lastSentUserContent = content;
            MessageReceived?.Invoke(this, userMsg);

            var message = new
            {
                type = "user",
                message = new
                {
                    role = "user",
                    content = content
                }
            };

            string json = JsonSerializer.Serialize(message);
            await _stdinWriter.WriteLineAsync(json);
            await _stdinWriter.FlushAsync();
        }

        /// <summary>
        /// Send an interrupt control request to the Claude process.
        /// This signals Claude to stop its current operation and return a result.
        /// </summary>
        public async Task InterruptAsync()
        {
            if (!IsRunning)
                return;

            var request = new
            {
                type = "control_request",
                request_id = Guid.NewGuid().ToString(),
                request = new
                {
                    subtype = "interrupt"
                }
            };

            string json = JsonSerializer.Serialize(request);
            await _stdinWriter.WriteLineAsync(json);
            await _stdinWriter.FlushAsync();
        }

        /// <summary>
        /// Gracefully stop the Claude process.
        /// Closes stdin (signals EOF), waits for exit with timeout, kills if needed.
        /// </summary>
        /// <param name="timeoutMs">Maximum time to wait for graceful exit before killing.</param>
        public async Task StopAsync(int timeoutMs = 5000)
        {
            if (_process == null)
                return;

            try
            {
                // Close stdin to signal EOF — Claude should exit gracefully
                if (_stdinWriter != null)
                {
                    try
                    {
                        _stdinWriter.Close();
                    }
                    catch (ObjectDisposedException) { }
                    _stdinWriter = null;
                }

                // Wait for process to exit gracefully
                if (!_process.HasExited)
                {
                    bool exited = await WaitForExitAsync(_process, timeoutMs);
                    if (!exited)
                    {
                        // Force kill if timeout exceeded
                        try
                        {
                            _process.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException) { } // Already exited
                    }
                }

                // Cancel background reading tasks
                _cts?.Cancel();

                // Wait for reading tasks to complete
                if (_stdoutReadTask != null)
                {
                    try { await _stdoutReadTask; } catch (OperationCanceledException) { }
                }
                if (_stderrReadTask != null)
                {
                    try { await _stderrReadTask; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }
        }

        /// <summary>
        /// Background loop reading stdout line-by-line, parsing NDJSON into AgentMessages.
        /// </summary>
        private async Task ReadStdoutLoop(StreamReader reader, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                        break; // EOF — process closed stdout

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // ParseLine returns a list (may be empty for filtered noise,
                    // or multiple for message objects with several content blocks)
                    var messages = AgentMessage.ParseLine(line);

                    // Deduplicate content from --include-partial-messages:
                    // When this flag is on, type:"message" objects are re-emitted with
                    // accumulated content blocks. We only want the NEW blocks.
                    messages = DeduplicateMessages(line, messages);

                    foreach (var msg in messages)
                    {
                        // Extract session_id from the first message that carries one
                        if (SessionId == null && !string.IsNullOrEmpty(msg.SessionId))
                            SessionId = msg.SessionId;

                        // Skip echoed user messages that we already emitted locally in SendMessageAsync
                        if (msg.Type == AgentMessageType.User
                            && _lastSentUserContent != null
                            && msg.Content == _lastSentUserContent)
                        {
                            _lastSentUserContent = null; // Only suppress once
                            continue;
                        }

                        // Buffer the message (thread-safe)
                        lock (_messagesLock)
                        {
                            _messages.Add(msg);
                        }

                        // Fire event for live subscribers
                        MessageReceived?.Invoke(this, msg);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { } // Process closed pipe
        }

        /// <summary>
        /// Deduplicate messages from --include-partial-messages mode.
        /// When enabled, type:"message" objects are re-emitted with accumulated content.
        /// We track the message ID and content count to only process NEW blocks.
        /// </summary>
        private List<AgentMessage> DeduplicateMessages(string rawLine, List<AgentMessage> messages)
        {
            if (messages.Count == 0)
                return messages;

            // Check if this line is a type:"message" object with an id field
            try
            {
                using var doc = JsonDocument.Parse(rawLine);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message"
                    && root.TryGetProperty("id", out var idProp))
                {
                    string msgId = idProp.GetString();
                    int totalBlocks = messages.Count;

                    if (msgId == _lastMessageId && totalBlocks <= _lastContentCount)
                    {
                        // Same message, no new content blocks - skip all
                        return new List<AgentMessage>();
                    }

                    if (msgId == _lastMessageId && totalBlocks > _lastContentCount)
                    {
                        // Same message, new content blocks - take only the new ones
                        var newMessages = messages.Skip(_lastContentCount).ToList();
                        _lastContentCount = totalBlocks;
                        return newMessages;
                    }

                    // New message ID - take all, reset tracking
                    _lastMessageId = msgId;
                    _lastContentCount = totalBlocks;
                }
            }
            catch { /* Not JSON or no id - pass through as-is */ }

            return messages;
        }

        /// <summary>
        /// Background loop reading stderr for error logging.
        /// Stderr output is captured as Error-type AgentMessages.
        /// </summary>
        private async Task ReadStderrLoop(StreamReader reader, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var msg = new AgentMessage
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = AgentMessageType.Error,
                        Content = line,
                        RawJson = line
                    };

                    lock (_messagesLock)
                    {
                        _messages.Add(msg);
                    }

                    MessageReceived?.Invoke(this, msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        /// <summary>
        /// Async helper to wait for process exit with a timeout.
        /// </summary>
        private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.Exited += (s, e) => tcs.TrySetResult(true);

            if (process.HasExited)
                return true;

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return completed == tcs.Task;
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int exitCode = -1;
            try { exitCode = _process.ExitCode; } catch { }
            ProcessExited?.Invoke(this, exitCode);
            Stopped?.Invoke(this, exitCode);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _cts?.Cancel();
            _cts?.Dispose();

            try { _stdinWriter?.Dispose(); } catch { }
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }

            _process?.Dispose();
            _isDisposed = true;
        }
    }
}
