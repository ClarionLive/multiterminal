using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTerminal.Terminal;
using MultiTerminal.Services;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Event args for terminal title change events.
    /// </summary>
    public class TerminalTitleChangedEventArgs : EventArgs
    {
        public string Title { get; private set; }
        public TerminalTitleChangedEventArgs(string title) { Title = title; }
    }

    /// <summary>
    /// A complete terminal emulator control using Windows ConPTY and WebView2/xterm.js.
    /// xterm.js handles all terminal emulation; ConPTY provides the shell backend.
    /// </summary>
    public class TerminalControl : UserControl
    {
        private ConPtyTerminal _terminal;
        private WebViewTerminalRenderer _renderer;

        private int _cols = 80;
        private int _rows = 24;
        private bool _isDisposed;
        private bool _pendingStart;
        private string _pendingWorkingDir;
        private string _pendingDocId;
        private string _pendingTerminalName;
        private string _pendingAutoRunCommand;
        private string _pendingSpawnerName;
        private string _pendingProjectId;
        private bool _pendingIsTeamLead;
        private string _pendingGatewayProfile;
        private string _pendingTaskWorktreePath;
        private TerminalTheme _pendingTheme = TerminalTheme.Dark;
        private string _terminalTitle = "PowerShell";
        private DebugLogService _debugLogService;

        /// <summary>
        /// Sets the debug log service for this terminal control.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
        }

        /// <summary>
        /// Logs a trace message to DebugLogService if available, otherwise to Debug.WriteLine.
        /// </summary>
        private void LogTrace(string message)
        {
            if (_debugLogService != null)
            {
                _debugLogService.Trace("TerminalControl", message);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalControl] {message}");
            }
        }

        /// <summary>
        /// Event fired when the terminal process exits.
        /// </summary>
        public event EventHandler ProcessExited;

        /// <summary>
        /// Event fired when the terminal title changes.
        /// </summary>
        public event EventHandler<TerminalTitleChangedEventArgs> TitleChanged;

        /// <summary>
        /// Event fired when font size changes.
        /// </summary>
        public event EventHandler<FontSizeChangedEventArgs> FontSizeChanged;

        /// <summary>
        /// Event fired when a context menu is requested (right-click).
        /// </summary>
        public event EventHandler<TerminalContextMenuEventArgs> ContextMenuRequested;

        /// <summary>
        /// Event fired when the terminal is clicked.
        /// </summary>
        public event EventHandler TerminalClicked;

        /// <summary>
        /// Event fired when the terminal is fully initialized and ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when the WebView2/xterm.js renderer is initialized (before shell starts).
        /// </summary>
        public event EventHandler RendererReady;

        /// <summary>
        /// Gets whether the terminal process is currently running.
        /// </summary>
        public bool IsRunning => _terminal != null && _terminal.IsRunning;

        /// <summary>
        /// Gets the underlying ConPTY terminal instance for direct I/O access.
        /// Used by TerminalStreamService for WebSocket streaming.
        /// </summary>
        public ConPtyTerminal ConPty => _terminal;

        /// <summary>
        /// Gets whether the WebView2/xterm.js renderer is initialized and ready for input.
        /// </summary>
        public bool IsRendererReady => _renderer?.IsInitialized ?? false;

        /// <summary>
        /// Gets the current terminal title.
        /// </summary>
        public string TerminalTitle => _terminalTitle;

        public TerminalControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            // Create WebView2-based renderer
            _renderer = new WebViewTerminalRenderer
            {
                Dock = DockStyle.Fill,
                Name = "terminalRenderer"
            };

            // Subscribe to renderer events
            _renderer.Initialized += OnRendererInitialized;
            _renderer.TerminalResized += OnRendererResized;
            _renderer.DataReceived += OnRendererDataReceived;
            _renderer.TitleChanged += OnRendererTitleChanged;
            _renderer.FontSizeChanged += OnRendererFontSizeChanged;
            _renderer.EscapeKeyPressed += OnRendererEscapeKeyPressed;
            _renderer.ShiftTabKeyPressed += OnRendererShiftTabKeyPressed;
            _renderer.CtrlEnterKeyPressed += OnRendererCtrlEnterKeyPressed;
            _renderer.AltVKeyPressed += OnRendererAltVKeyPressed;
            _renderer.ContextMenuRequested += OnRendererContextMenuRequested;
            _renderer.TerminalClicked += OnRendererTerminalClicked;

            Controls.Add(_renderer);

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "TerminalControl";
            Size = new Size(640, 400);

            ResumeLayout(false);
        }

        /// <summary>
        /// Called when WebView2/xterm.js is fully initialized and ready.
        /// </summary>
        private void OnRendererInitialized(object sender, EventArgs e)
        {
            // Always fire RendererReady when WebView2 is initialized
            RendererReady?.Invoke(this, EventArgs.Empty);

            if (_pendingStart)
            {
                _pendingStart = false;
                DoStart(_pendingWorkingDir, _pendingDocId, _pendingTerminalName, _pendingAutoRunCommand, _pendingSpawnerName, _pendingProjectId, _pendingIsTeamLead, _pendingGatewayProfile, _pendingTaskWorktreePath);
            }
        }

        /// <summary>
        /// Starts the terminal with the specified working directory.
        /// </summary>
        /// <param name="workingDirectory">Initial working directory</param>
        /// <param name="docId">Document ID for MCP push notifications</param>
        /// <param name="terminalName">Pre-registered terminal name for MCP (null if not pre-registered)</param>
        /// <param name="autoRunCommand">Command to run automatically after shell starts (e.g., "claude -r session_id")</param>
        /// <param name="projectId">Project ID for context injection (sets MULTITERMINAL_PROJECT_ID env var)</param>
        /// <param name="isTeamLead">Whether this terminal is a team lead (sets MULTITERMINAL_TEAM_LEAD env var)</param>
        public void Start(string workingDirectory = null, string docId = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, string taskWorktreePath = null)
        {
            if (_terminal != null && _terminal.IsRunning)
            {
                Stop();
            }

            // If renderer is initialized, start immediately
            if (_renderer.IsInitialized)
            {
                DoStart(workingDirectory, docId, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile, taskWorktreePath);
            }
            else
            {
                // Defer until WebView2 is initialized
                _pendingStart = true;
                _pendingWorkingDir = workingDirectory;
                _pendingDocId = docId;
                _pendingTerminalName = terminalName;
                _pendingAutoRunCommand = autoRunCommand;
                _pendingSpawnerName = spawnerName;
                _pendingProjectId = projectId;
                _pendingIsTeamLead = isTeamLead;
                _pendingGatewayProfile = gatewayProfile;
                _pendingTaskWorktreePath = taskWorktreePath;
            }
        }

        private void OnRendererFontSizeChanged(object sender, FontSizeChangedEventArgs e)
        {
            FontSizeChanged?.Invoke(this, e);
        }

        private void DoStart(string workingDirectory, string docId = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, string taskWorktreePath = null)
        {
            // Reset Claude Code detection so the event fires again for this new session
            _claudeCodeDetectedThisSession = false;
            _devChannelWarningHandled = false;
            _outputBuffer.Clear();

            // Use size from renderer. Trust xterm.js as the source of truth — the post-fit
            // `ready` message has already populated VisibleCols/VisibleRows by the time
            // OnRendererInitialized fires. The Math.Max(1, ...) guard is only against 0/negative;
            // an earlier Math.Max(80, ...) / Math.Max(24, ...) floor here lied to ConPTY when
            // xterm.js had fitted to <80 cols, producing a permanent col-mismatch that surfaced as
            // stray characters in the rendered output until a layout reflow forced a real resize.
            _cols = Math.Max(1, _renderer.VisibleCols);
            _rows = Math.Max(1, _renderer.VisibleRows);

            System.Diagnostics.Debug.WriteLine($"[TerminalControl] DoStart: launching ConPTY at {_cols}x{_rows} (renderer reported {_renderer.VisibleCols}x{_renderer.VisibleRows})");

            // Apply theme
            _renderer.SetTheme(_pendingTheme);

            // Create and start terminal
            _terminal = new ConPtyTerminal();
            _terminal.DataReceived += OnTerminalDataReceived;
            _terminal.ProcessExited += OnTerminalProcessExited;

            try
            {
                _terminal.Start(_cols, _rows, null, workingDirectory, docId, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile, taskWorktreePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to start terminal: " + ex.Message + "\r\n\r\n" +
                    "Make sure you're running Windows 10 version 1809 or later.",
                    "Terminal Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            _renderer.Focus();
            Ready?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Stops the terminal process.
        /// </summary>
        public void Stop()
        {
            if (_terminal != null)
            {
                _terminal.DataReceived -= OnTerminalDataReceived;
                _terminal.ProcessExited -= OnTerminalProcessExited;
                _terminal.Dispose();
                _terminal = null;
            }
        }

        /// <summary>
        /// Sends text to the terminal.
        /// </summary>
        public void Write(string text)
        {
            _terminal?.Write(text);
        }

        /// <summary>
        /// Sends raw bytes to the terminal.
        /// </summary>
        public void Write(byte[] data)
        {
            _terminal?.Write(data);
        }

        /// <summary>
        /// Maximum chunk size in bytes for message chunking.
        /// Set to 500 bytes to prevent PowerShell's bracketed paste mode from triggering.
        /// PowerShell enters paste mode at ~500-600 characters, causing messages to wait for user confirmation.
        /// </summary>
        private const int MaxChunkSize = 500;

        /// <summary>
        /// Injects text input into the terminal and submits it.
        /// Uses xterm.js JavaScript injection for the Enter key to work with apps
        /// like Claude Code that detect programmatic input differently.
        /// Returns a Task that completes when the Enter key has been acknowledged by JS.
        /// Large messages (>500 bytes) are automatically chunked with markers to prevent PowerShell paste mode.
        /// </summary>
        /// <param name="text">The text to inject.</param>
        /// <returns>A Task that completes when injection is finished. Returns true if successful.</returns>
        public async Task<bool> InjectInputAsync(string text)
        {
            LogTrace($"InjectInputAsync called, text length={text?.Length ?? 0}");

            if (string.IsNullOrEmpty(text) || _terminal == null)
            {
                LogTrace("InjectInputAsync: early return (text empty or terminal null)");
                return false;
            }

            // Check if chunking is needed
            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (textBytes > MaxChunkSize)
            {
                LogTrace($"InjectInputAsync: using chunked mode ({textBytes} bytes)");
                return await InjectChunkedInputAsync(text);
            }

            // Single message - inject directly
            LogTrace("InjectInputAsync: using single mode");
            return await InjectSingleInputAsync(text);
        }

        /// <summary>
        /// Injects a single chunk of text (no chunking).
        /// </summary>
        private async Task<bool> InjectSingleInputAsync(string text)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TerminalControl] InjectSingleInputAsync ENTRY: textLength={text?.Length ?? 0}");
            LogTrace("InjectSingleInputAsync called");

            if (string.IsNullOrEmpty(text) || _terminal == null)
            {
                LogTrace("InjectSingleInputAsync: early return (text empty or terminal null)");
                return false;
            }

            // Write text WITHOUT newline (text appears in prompt field)
            LogTrace("Writing text to ConPTY pipe (no newline)...");
            _terminal.Write(text);
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TerminalControl] ConPTY write complete");
            LogTrace("Text written, now triggering Enter via xterm.js...");

            // Small delay to let text appear in prompt
            try
            {
                await System.Threading.Tasks.Task.Delay(50);
                LogTrace("Delay completed, checking terminal state...");
            }
            catch (Exception ex)
            {
                LogTrace($"Task.Delay failed: {ex.Message}");
                return false;
            }

            if (IsDisposed)
            {
                LogTrace("InjectSingleInputAsync: early return (disposed)");
                return false;
            }

            if (_renderer == null)
            {
                LogTrace("InjectSingleInputAsync: early return (renderer is null)");
                return false;
            }

            // Trigger Enter key through xterm.js to submit the input to Claude Code
            try
            {
                LogTrace("Calling SendEnterViaXtermAsync...");
                bool enterSuccess = await _renderer.SendEnterViaXtermAsync();
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TerminalControl] Enter key triggered, success={enterSuccess}");
                LogTrace($"SendEnterViaXtermAsync completed with result: {enterSuccess}");

                // Check if Enter key was successfully sent
                if (!enterSuccess)
                {
                    LogTrace("InjectSingleInputAsync: Enter key failed (terminal not initialized)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogTrace($"SendEnterViaXtermAsync threw exception: {ex.Message}");
                return false;
            }

            // Small delay to let Enter key be processed
            await System.Threading.Tasks.Task.Delay(100);

            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TerminalControl] InjectSingleInputAsync EXIT: success=true");
            LogTrace("InjectSingleInputAsync completed successfully");
            return true;
        }

        /// <summary>
        /// Sends only the Enter key via xterm.js without writing any text.
        /// Used to retry Enter submission when text was already written to the terminal.
        /// </summary>
        public async Task<bool> SendEnterAsync()
        {
            LogTrace("SendEnterAsync called");

            if (IsDisposed || _renderer == null)
            {
                LogTrace("SendEnterAsync: early return (disposed or renderer null)");
                return false;
            }

            try
            {
                bool enterSuccess = await _renderer.SendEnterViaXtermAsync();
                LogTrace($"SendEnterAsync completed with result: {enterSuccess}");

                if (enterSuccess)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }

                return enterSuccess;
            }
            catch (Exception ex)
            {
                LogTrace($"SendEnterAsync threw exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Types text into the terminal character-by-character via xterm.js.
        /// Mimics real keyboard typing to avoid paste detection.
        /// </summary>
        public void TypeInput(string text, string lineEnding = "cr", int charDelayMs = 15)
        {
            LogTrace($"TypeInput: charDelay={charDelayMs}ms, lineEnding={lineEnding}, text=\"{text}\"");
            _renderer?.TypeInputViaXterm(text, lineEnding, charDelayMs);
        }

        /// <summary>
        /// Injects large text by splitting into chunks with markers.
        /// Each chunk is sent as a separate message with [n/total] prefix.
        /// </summary>
        private async Task<bool> InjectChunkedInputAsync(string text)
        {
            var chunks = SplitIntoChunks(text, MaxChunkSize);
            var totalChunks = chunks.Count;

            LogTrace($"Chunking message: {Encoding.UTF8.GetByteCount(text)} bytes into {totalChunks} chunks");

            for (int i = 0; i < totalChunks; i++)
            {
                var chunkMarker = $"[{i + 1}/{totalChunks}] ";
                var chunkText = chunkMarker + chunks[i];

                var success = await InjectSingleInputAsync(chunkText);
                if (!success)
                {
                    LogTrace($"Chunk {i + 1}/{totalChunks} failed to inject");
                    return false;
                }

                // Delay between chunks to allow processing
                if (i < totalChunks - 1)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }

            return true;
        }

        /// <summary>
        /// Splits text into chunks at word boundaries where possible.
        /// </summary>
        /// <param name="text">Text to split.</param>
        /// <param name="maxChunkBytes">Maximum bytes per chunk (excluding chunk marker).</param>
        /// <returns>List of text chunks.</returns>
        private static List<string> SplitIntoChunks(string text, int maxChunkBytes)
        {
            var chunks = new List<string>();

            // Reserve space for chunk marker like "[99/99] " (9 bytes max)
            var effectiveMaxBytes = maxChunkBytes - 10;

            var remaining = text;
            while (!string.IsNullOrEmpty(remaining))
            {
                var chunk = GetChunkAtWordBoundary(remaining, effectiveMaxBytes);
                chunks.Add(chunk);
                remaining = remaining.Substring(chunk.Length).TrimStart();
            }

            return chunks;
        }

        /// <summary>
        /// Gets a chunk from the start of text, breaking at word boundary if possible.
        /// </summary>
        private static string GetChunkAtWordBoundary(string text, int maxBytes)
        {
            // If text fits, return it all
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            // Find the longest substring that fits
            int low = 0;
            int high = text.Length;
            int bestFit = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (Encoding.UTF8.GetByteCount(text.Substring(0, mid)) <= maxBytes)
                {
                    bestFit = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (bestFit == 0) bestFit = 1; // At minimum take one character

            // Try to break at word boundary (look back for space/newline)
            var candidate = text.Substring(0, bestFit);
            var lastSpace = candidate.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' });

            // Only use word boundary if it's reasonably close (within 20% of max)
            if (lastSpace > bestFit * 0.8)
            {
                return text.Substring(0, lastSpace);
            }

            return candidate;
        }

        /// <summary>
        /// Injects text input into the terminal and submits it (fire-and-forget version).
        /// For backward compatibility - prefer InjectInputAsync for new code.
        /// </summary>
        /// <param name="text">The text to inject.</param>
        /// <returns>True for backward compatibility (actual result is discarded).</returns>
        public bool InjectInput(string text)
        {
            _ = InjectInputAsync(text);
            return true;
        }

        /// <summary>
        /// Sends Ctrl+C to interrupt the current process.
        /// </summary>
        public void SendCtrlC()
        {
            _terminal?.SendCtrlC();
        }

        /// <summary>
        /// Clears the terminal screen.
        /// </summary>
        public void Clear()
        {
            _renderer?.Clear();
        }

        /// <summary>
        /// Called when xterm.js receives user input (keyboard data).
        /// Forwards the input to the ConPTY terminal.
        /// </summary>
        private void OnRendererDataReceived(byte[] data)
        {
            if (_terminal != null && _terminal.IsRunning)
            {
                _terminal.Write(data);
            }

            // Buffer input characters to detect slash commands
            BufferInputForSlashDetection(data);
        }

        /// <summary>
        /// Event fired when Claude Code is detected running in this terminal.
        /// </summary>
        public event EventHandler ClaudeCodeDetected;

        private bool _claudeCodeDetectedThisSession = false;
        private bool _devChannelWarningHandled = false;
        private StringBuilder _outputBuffer = new StringBuilder();

        // Slash command interception: buffer user input to detect commands like /clear
        private readonly StringBuilder _inputLineBuffer = new StringBuilder();
        private bool _inEscapeSequence; // true immediately after ESC, before we know the sequence type
        private bool _inCsiSequence;    // true inside a CSI sequence (ESC[ ... final_byte)

        /// <summary>
        /// Buffers user input characters and checks for slash commands on Enter.
        /// Skips CSI escape sequences (e.g. mouse tracking: ESC[&lt;35;27;15M) so they
        /// don't pollute the buffer and break slash command matching.
        /// </summary>
        private void BufferInputForSlashDetection(byte[] data)
        {
            try
            {
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];

                    // While inside a CSI sequence (ESC[...), consume until final byte (0x40-0x7E)
                    if (_inCsiSequence)
                    {
                        if (b >= 0x40 && b <= 0x7E) // Final byte terminates CSI
                            _inCsiSequence = false;
                        continue;
                    }

                    // After ESC, determine sequence type
                    if (_inEscapeSequence)
                    {
                        _inEscapeSequence = false;
                        if (b == 0x5B) // '[' — CSI introducer, enter CSI mode
                        {
                            _inCsiSequence = true;
                            continue;
                        }
                        continue; // Two-byte escape (e.g., ESC O), just skip
                    }

                    if (b == 0x0D) // Enter (CR)
                    {
                        string line = _inputLineBuffer.ToString().Trim();
                        _inputLineBuffer.Clear();

                        if (line.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                        {
                            OnSlashClearDetected();
                        }
                    }
                    else if (b == 0x7F || b == 0x08) // Backspace / DEL
                    {
                        if (_inputLineBuffer.Length > 0)
                            _inputLineBuffer.Length--;
                    }
                    else if (b == 0x1B) // ESC — start of escape sequence
                    {
                        _inEscapeSequence = true;
                        _inputLineBuffer.Clear();
                    }
                    else if (b == 0x03) // Ctrl+C — reset buffer
                    {
                        _inputLineBuffer.Clear();
                    }
                    else if (b >= 0x20 && b < 0x7F) // Printable ASCII
                    {
                        _inputLineBuffer.Append((char)b);
                        // Safety: don't let buffer grow unbounded
                        if (_inputLineBuffer.Length > 200)
                            _inputLineBuffer.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalControl] Input buffer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when user types /clear. Flushes session memory, then re-injects session-start.
        /// </summary>
        private async void OnSlashClearDetected()
        {
            LogTrace("Detected /clear — flushing session memory and re-injecting startup");

            // Flush session memory for this project (old session file is now frozen)
            FlushSessionMemoryAsync();

            // Also sync the old session into session_lineage immediately
            // (don't wait 30 seconds for the periodic timer)
            SyncSessionLineageAsync();

            // Wait for Claude Code to process /clear (it's instant, but give the UI a moment)
            await Task.Delay(1500);

            if (_isDisposed || _terminal == null || !_terminal.IsRunning)
            {
                LogTrace($"/clear injection aborted: terminal not running (disposed={_isDisposed}, terminal={(_terminal == null ? "null" : "ok")}, running={_terminal?.IsRunning ?? false})");
                return;
            }

            // Wait for renderer to be ready (may be briefly unavailable during /clear transition)
            int waitedMs = 0;
            while (!IsRendererReady && waitedMs < 3000)
            {
                await Task.Delay(100);
                waitedMs += 100;
            }

            if (!IsRendererReady)
            {
                LogTrace($"/clear injection failed: renderer not ready after {waitedMs}ms (renderer={(_renderer == null ? "null" : "not initialized")})");
                return;
            }

            LogTrace($"Injecting 'initializing...' after /clear to trigger session-start (renderer ready after {waitedMs}ms)");
            TypeInput("initializing...", "cr", 20);
        }

        /// <summary>
        /// Calls the REST API to index session memory for this terminal's project.
        /// Fire-and-forget — does not block the /clear flow.
        /// </summary>
        private async void FlushSessionMemoryAsync()
        {
            if (string.IsNullOrEmpty(_pendingWorkingDir)) return;

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    projectPath = _pendingWorkingDir,
                    terminalName = _pendingTerminalName ?? "Unknown"
                });
                using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:5050/api/session-memory/index-project", content);
                LogTrace($"Session memory flush after /clear: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                LogTrace($"Session memory flush failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Calls the REST API to sync session lineage for this terminal's project.
        /// Fire-and-forget — ensures the previous session's lineage record is imported
        /// before the new session's session-start skill queries for it.
        /// </summary>
        private async void SyncSessionLineageAsync()
        {
            if (string.IsNullOrEmpty(_pendingWorkingDir)) return;

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    projectPath = _pendingWorkingDir,
                    terminalName = _pendingTerminalName ?? "Unknown"
                });
                using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:5050/api/session-lineage/sync-project", content);
                LogTrace($"Session lineage sync after /clear: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                LogTrace($"Session lineage sync failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Called when ConPTY terminal outputs data.
        /// Forwards the data to xterm.js for display.
        /// </summary>
        private void OnTerminalDataReceived(byte[] data)
        {
            if (_isDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnTerminalDataReceived(data)));
                }
                catch { }
                return;
            }

            _renderer?.WriteToTerminal(data);

            // Detect Claude Code startup (only once per session to avoid spam)
            if (!_claudeCodeDetectedThisSession)
            {
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(data);
                    _outputBuffer.Append(text);

                    // Keep buffer from growing too large
                    if (_outputBuffer.Length > 2000)
                    {
                        _outputBuffer.Remove(0, _outputBuffer.Length - 1000);
                    }

                    // Check for Claude Code patterns
                    string buffer = _outputBuffer.ToString();
                    if (buffer.Contains("Claude Code") ||
                        buffer.Contains("claude-code") ||
                        buffer.Contains("╭─") && buffer.Contains("Tips"))
                    {
                        _claudeCodeDetectedThisSession = true;
                        _outputBuffer.Clear();
                        ClaudeCodeDetected?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch { }
            }

            // Auto-accept the dev channel warning dialog by sending "1".
            // The dialog shows numbered options; "1" selects
            // "I am using this for local development" without needing Enter.
            if (!_devChannelWarningHandled)
            {
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(data);
                    if (text.Contains("Loading development channels") || text.Contains("Enter to confirm"))
                    {
                        _devChannelWarningHandled = true;
                        // Small delay to ensure the dialog is fully rendered before sending input.
                        // Uses TypeInput via xterm.js — raw pipe Write("\r") doesn't work
                        // with Claude Code's interactive prompts.
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            if (!_isDisposed)
                                BeginInvoke(new Action(() => TypeInput("1", "none")));
                        });
                    }
                }
                catch { }
            }
        }

        private void OnTerminalProcessExited(object sender, EventArgs e)
        {
            if (_isDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnTerminalProcessExited(sender, e)));
                }
                catch { }
                return;
            }

            ProcessExited?.Invoke(this, EventArgs.Empty);
        }

        private void OnRendererTitleChanged(object sender, TitleChangedEventArgs e)
        {
            if (_isDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnRendererTitleChanged(sender, e)));
                }
                catch { }
                return;
            }

            _terminalTitle = e.Title;
            TitleChanged?.Invoke(this, new TerminalTitleChangedEventArgs(e.Title));
        }

        private void OnRendererResized(object sender, TerminalSizeEventArgs e)
        {
            if (e.Columns != _cols || e.Rows != _rows)
            {
                _cols = e.Columns;
                _rows = e.Rows;
                _terminal?.Resize(_cols, _rows);
            }
        }

        /// <summary>
        /// Handles ESC key from the renderer.
        /// </summary>
        private void OnRendererEscapeKeyPressed(object sender, EventArgs e)
        {
            // ESC is sent to terminal via DataReceived
        }

        /// <summary>
        /// Handles Shift+Tab from the renderer.
        /// </summary>
        private void OnRendererShiftTabKeyPressed(object sender, EventArgs e)
        {
            // Shift+Tab is sent to terminal via DataReceived
        }

        /// <summary>
        /// Handles Ctrl+Enter from the renderer.
        /// </summary>
        private void OnRendererCtrlEnterKeyPressed(object sender, EventArgs e)
        {
            // Send newline to terminal for Ctrl+Enter
            if (_terminal != null && _terminal.IsRunning)
            {
                _terminal.Write(new byte[] { 0x0A }); // LF
            }
        }

        /// <summary>
        /// Handles Alt+V from the renderer.
        /// Used for pasting images.
        /// </summary>
        private void OnRendererAltVKeyPressed(object sender, EventArgs e)
        {
            if (_terminal != null && _terminal.IsRunning)
            {
                // Alt+key in terminal is typically ESC followed by the key
                _terminal.Write(Encoding.ASCII.GetBytes("\x1bv"));
            }
        }

        /// <summary>
        /// Handles context menu request from the renderer.
        /// </summary>
        private void OnRendererContextMenuRequested(object sender, TerminalContextMenuEventArgs e)
        {
            ContextMenuRequested?.Invoke(this, e);
        }

        /// <summary>
        /// Handles terminal click from the renderer.
        /// </summary>
        private void OnRendererTerminalClicked(object sender, EventArgs e)
        {
            TerminalClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isDisposed = true;

                // Unsubscribe from renderer events
                if (_renderer != null)
                {
                    _renderer.Initialized -= OnRendererInitialized;
                    _renderer.TerminalResized -= OnRendererResized;
                    _renderer.DataReceived -= OnRendererDataReceived;
                    _renderer.TitleChanged -= OnRendererTitleChanged;
                    _renderer.FontSizeChanged -= OnRendererFontSizeChanged;
                    _renderer.EscapeKeyPressed -= OnRendererEscapeKeyPressed;
                    _renderer.ShiftTabKeyPressed -= OnRendererShiftTabKeyPressed;
                    _renderer.CtrlEnterKeyPressed -= OnRendererCtrlEnterKeyPressed;
                    _renderer.AltVKeyPressed -= OnRendererAltVKeyPressed;
                    _renderer.ContextMenuRequested -= OnRendererContextMenuRequested;
                    _renderer.TerminalClicked -= OnRendererTerminalClicked;
                }

                Stop();
                _renderer?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Sets focus to the terminal renderer.
        /// </summary>
        public void FocusTerminal()
        {
            _renderer?.Focus();
        }

        /// <summary>
        /// Sets the font size.
        /// </summary>
        public void SetFontSize(float size)
        {
            _renderer?.SetFontSize(size);
        }

        /// <summary>
        /// Gets the current font size.
        /// </summary>
        public float GetFontSize()
        {
            return _renderer != null ? _renderer.FontSize : 10f;
        }

        /// <summary>
        /// Sets the terminal color theme.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _pendingTheme = theme;

            if (_renderer != null)
            {
                _renderer.SetTheme(theme);
            }
            BackColor = theme.Background;
        }

        /// <summary>
        /// Gets the current theme.
        /// </summary>
        public TerminalTheme CurrentTheme
        {
            get { return _renderer?.Theme ?? TerminalTheme.Dark; }
        }
    }
}
