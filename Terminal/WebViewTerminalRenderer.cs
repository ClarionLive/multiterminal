using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Services;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Event args for terminal size change.
    /// </summary>
    public class TerminalSizeEventArgs : EventArgs
    {
        public int Columns { get; }
        public int Rows { get; }
        public TerminalSizeEventArgs(int cols, int rows) { Columns = cols; Rows = rows; }
    }

    /// <summary>
    /// Event args for terminal title change.
    /// </summary>
    public class TitleChangedEventArgs : EventArgs
    {
        public string Title { get; }
        public TitleChangedEventArgs(string title) { Title = title; }
    }

    /// <summary>
    /// Event args for font size change.
    /// </summary>
    public class FontSizeChangedEventArgs : EventArgs
    {
        public float FontSize { get; }
        public FontSizeChangedEventArgs(float fontSize) { FontSize = fontSize; }
    }

    /// <summary>
    /// Event args for terminal context menu request.
    /// </summary>
    public class TerminalContextMenuEventArgs : EventArgs
    {
        public Point Location { get; }
        public string SelectedText { get; }
        public TerminalContextMenuEventArgs(Point location, string selectedText)
        {
            Location = location;
            SelectedText = selectedText;
        }
    }

    /// <summary>
    /// Terminal renderer using WebView2 and xterm.js.
    /// Provides a professional terminal experience with GPU-accelerated rendering.
    /// Uses a shared WebView2 environment for memory efficiency.
    /// </summary>
    public class WebViewTerminalRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private TerminalTheme _theme = TerminalTheme.Dark;
        private float _fontSize = 10f;
        private int _cols = 80;
        private int _rows = 24;

        // Queue for data received before WebView2 is ready
        private readonly Queue<byte[]> _pendingData = new Queue<byte[]>();

        // Write batching to reduce WebView2 message flooding
        private readonly ConcurrentQueue<byte[]> _pendingWrites = new ConcurrentQueue<byte[]>();
        private volatile bool _writeScheduled;
        private readonly object _writeLock = new object();

        // TaskCompletionSource for Enter key acknowledgment synchronization
        private System.Threading.Tasks.TaskCompletionSource<bool> _enterAckTcs;

        // Output change tracking for Enter key retry mechanism
        private DateTime _lastOutputTime = DateTime.MinValue;
        private readonly object _outputTimeLock = new object();

        // Retry configuration for Enter key (configurable for testing)
        private int _maxEnterRetries = 8; // ~15 seconds total: 500ms + 1s + 2s + 4s + 8s
        private int _initialRetryDelayMs = 500;

        /// <summary>
        /// Debug log sink, wired by the owning TerminalControl. Null until wired, so all
        /// call sites use the null-conditional.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        /// <summary>
        /// Event fired when terminal data is received from user input.
        /// </summary>
        public event Action<byte[]> DataReceived;

        /// <summary>
        /// Event fired when the terminal is resized (new column/row count).
        /// </summary>
        public event EventHandler<TerminalSizeEventArgs> TerminalResized;

        /// <summary>
        /// Event fired when font size changes.
        /// </summary>
        public event EventHandler<FontSizeChangedEventArgs> FontSizeChanged;

        /// <summary>
        /// Event fired when the terminal title changes.
        /// </summary>
        public event EventHandler<TitleChangedEventArgs> TitleChanged;

        /// <summary>
        /// Event fired when ESC key is pressed.
        /// </summary>
        public event EventHandler EscapeKeyPressed;

        /// <summary>
        /// Event fired when Shift+Tab is pressed.
        /// </summary>
        public event EventHandler ShiftTabKeyPressed;

        /// <summary>
        /// Event fired when Ctrl+Enter is pressed.
        /// </summary>
        public event EventHandler CtrlEnterKeyPressed;

        /// <summary>
        /// Event fired when Alt+V is pressed.
        /// </summary>
        public event EventHandler AltVKeyPressed;

        /// <summary>
        /// Event fired when a context menu is requested (right-click).
        /// </summary>
        public event EventHandler<TerminalContextMenuEventArgs> ContextMenuRequested;

        /// <summary>
        /// Event fired when WebView2 initialization completes.
        /// </summary>
        public event EventHandler Initialized;

        /// <summary>
        /// Event fired when the terminal is clicked.
        /// </summary>
        public event EventHandler TerminalClicked;

        /// <summary>
        /// Gets whether the WebView2 control is initialized and ready.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the current theme.
        /// </summary>
        public TerminalTheme Theme => _theme;

        /// <summary>
        /// Gets the current font size.
        /// </summary>
        public float FontSize => _fontSize;

        /// <summary>
        /// Gets the number of visible rows.
        /// </summary>
        public int VisibleRows => _rows;

        /// <summary>
        /// Gets the number of visible columns.
        /// </summary>
        public int VisibleCols => _cols;

        public WebViewTerminalRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = _theme.Background;
            Name = "WebViewTerminalRenderer";
            Size = new Size(640, 400);

            // Create WebView2 control
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView"
            };

            Controls.Add(_webView);

            ResumeLayout(false);

            // Initialize WebView2 when handle is created
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                // Use the shared cached environment for memory efficiency
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                // Configure WebView2 settings
                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true; // Enable for debugging
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                // Subscribe to message events
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                // Load terminal HTML
                string htmlPath = GetTerminalHtmlPath();
                DebugLogService?.Info("WebViewTerminalRenderer", "Terminal HTML path: " + htmlPath);
                if (File.Exists(htmlPath))
                {
                    // Append a cache-busting query (file mtime) so WebView2 can never
                    // serve a stale copy of the document on the same file:// URL.
                    string baseUri = new Uri(htmlPath).AbsoluteUri;
                    long bust = File.GetLastWriteTimeUtc(htmlPath).Ticks;
                    string navUri = baseUri + "?v=" + bust;
                    _webView.CoreWebView2.Navigate(navUri);
                }
                else
                {
                    // Fallback: try to load from embedded resource or show error
                    ShowError("Terminal HTML file not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private string GetTerminalHtmlPath()
        {
            // Try to find terminal.html relative to the assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Check in Terminal subfolder
            string path = Path.Combine(assemblyDir, "Terminal", "terminal.html");
            if (File.Exists(path)) return path;

            // Check in same folder as assembly
            path = Path.Combine(assemblyDir, "terminal.html");
            if (File.Exists(path)) return path;

            // Check in parent folder's Terminal subfolder (for development)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "Terminal", "terminal.html");
                if (File.Exists(path)) return path;
            }

            return Path.Combine(assemblyDir, "Terminal", "terminal.html");
        }

        private void ShowError(string message)
        {
            // Create a simple error display
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.Black,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            DebugLogService?.Info("WebViewTerminalRenderer", "Navigation completed, success: " + e.IsSuccess);
            if (!e.IsSuccess)
            {
                ShowError("Failed to load terminal: " + e.WebErrorStatus);
                _isInitializing = false;
            }
            // The JavaScript will send "ready" message when xterm.js is initialized
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                DebugLogService?.Trace("WebViewTerminalRenderer", "WebView message: " + json);

                // Parse the JSON message
                var message = ParseJsonMessage(json);

                switch (message.Type)
                {
                    case "ready":
                        OnTerminalReady(message);
                        break;

                    case "input":
                        OnTerminalInput(message.Data);
                        break;

                    case "resize":
                        OnTerminalResize(message.Cols, message.Rows);
                        break;

                    case "title":
                        OnTerminalTitleChange(message.Title);
                        break;

                    case "fontSizeChanged":
                        OnFontSizeChanged(message.FontSize);
                        break;

                    case "contextmenu":
                        OnContextMenuRequested(message.X, message.Y, message.SelectedText);
                        break;

                    case "terminalclick":
                        TerminalClicked?.Invoke(this, EventArgs.Empty);
                        break;

                    case "paste":
                        OnPasteRequested();
                        break;

                    case "copy":
                        OnCopyRequested(message.SelectedText);
                        break;

                    case "enterAck":
                        // Complete the TaskCompletionSource to signal Enter was processed
                        _enterAckTcs?.TrySetResult(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("WebViewTerminalRenderer", "WebView message error: " + ex.Message);
            }
        }

        private void OnTerminalReady(TerminalMessage message)
        {
            _isInitialized = true;
            _isInitializing = false;
            _cols = message.Cols;
            _rows = message.Rows;

            // Apply initial theme
            SetTheme(_theme);

            // Apply initial font size
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("fontSize:" + _fontSize.ToString());
            }

            // Send any queued data
            while (_pendingData.Count > 0)
            {
                var data = _pendingData.Dequeue();
                WriteToTerminalInternal(data);
            }

            // Notify that initialization is complete
            Initialized?.Invoke(this, EventArgs.Empty);

            // Fire initial resize event
            TerminalResized?.Invoke(this, new TerminalSizeEventArgs(_cols, _rows));
        }

        private void OnTerminalInput(string base64Data)
        {
            if (string.IsNullOrEmpty(base64Data)) return;

            try
            {
                byte[] data = Convert.FromBase64String(base64Data);

                // Check for special key sequences
                if (data.Length == 1)
                {
                    if (data[0] == 0x1B) // ESC
                    {
                        EscapeKeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (data.Length == 3 && data[0] == 0x1B && data[1] == '[' && data[2] == 'Z')
                {
                    // Shift+Tab (backtab)
                    ShiftTabKeyPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (data.Length == 2 && data[0] == 0x1B && data[1] == 'v')
                {
                    // Alt+V
                    AltVKeyPressed?.Invoke(this, EventArgs.Empty);
                    return; // Don't send to terminal
                }

                DataReceived?.Invoke(data);
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("WebViewTerminalRenderer", "Input decode error: " + ex.Message);
            }
        }

        private void OnTerminalResize(int cols, int rows)
        {
            if (cols > 0 && rows > 0)
            {
                _cols = cols;
                _rows = rows;
                TerminalResized?.Invoke(this, new TerminalSizeEventArgs(cols, rows));
            }
        }

        private void OnTerminalTitleChange(string title)
        {
            TitleChanged?.Invoke(this, new TitleChangedEventArgs(title));
        }

        private void OnFontSizeChanged(int fontSize)
        {
            if (fontSize >= 6 && fontSize <= 32)
            {
                _fontSize = fontSize;
                FontSizeChanged?.Invoke(this, new FontSizeChangedEventArgs(fontSize));
            }
        }

        private void OnContextMenuRequested(int x, int y, string selectedText)
        {
            var location = new Point(x, y);
            ContextMenuRequested?.Invoke(this, new TerminalContextMenuEventArgs(location, selectedText));
        }

        private void OnPasteRequested()
        {
            // Read clipboard on UI thread and send to terminal
            if (System.Windows.Forms.Clipboard.ContainsText())
            {
                string text = System.Windows.Forms.Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
                    DataReceived?.Invoke(data);
                }
            }
        }

        private void OnCopyRequested(string text)
        {
            // Write the selection to the Windows clipboard on the UI thread.
            // Mirrors OnPasteRequested: the WebView2 page is hosted from a file://
            // URL where navigator.clipboard.writeText silently fails, so Ctrl-C
            // (and right-click Copy) must route the copy through the host instead.
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    // Clipboard can be locked by another process; copy is best-effort.
                    DebugLogService?.Error("WebViewTerminalRenderer", "Clipboard.SetText failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Writes terminal data to xterm.js for display.
        /// Uses batching to reduce WebView2 message frequency and prevent UI overload.
        /// </summary>
        public void WriteToTerminal(byte[] data)
        {
            if (!_isInitialized)
            {
                // Queue data until WebView2 is ready
                _pendingData.Enqueue(data);
                return;
            }

            // Track output for Enter key retry mechanism
            lock (_outputTimeLock)
            {
                _lastOutputTime = DateTime.UtcNow;
            }

            // Queue for batched write
            _pendingWrites.Enqueue(data);
            ScheduleWrite();
        }

        /// <summary>
        /// Schedules a batched write to WebView2 on the next UI cycle.
        /// </summary>
        private void ScheduleWrite()
        {
            lock (_writeLock)
            {
                if (_writeScheduled) return;
                _writeScheduled = true;
            }

            // Use BeginInvoke to batch writes on next UI cycle
            if (InvokeRequired)
            {
                BeginInvoke(new Action(FlushWrites));
            }
            else
            {
                FlushWrites();
            }
        }

        /// <summary>
        /// Flushes all pending writes to WebView2 as a single message.
        /// </summary>
        private void FlushWrites()
        {
            lock (_writeLock)
            {
                _writeScheduled = false;
            }

            // Combine all pending data into a single batch
            var allData = new List<byte>();
            while (_pendingWrites.TryDequeue(out byte[] data))
            {
                allData.AddRange(data);
            }

            if (allData.Count > 0)
            {
                WriteToTerminalInternal(allData.ToArray());
            }
        }

        private void WriteToTerminalInternal(byte[] data)
        {
            if (_webView?.CoreWebView2 == null) return;

            try
            {
                string base64 = Convert.ToBase64String(data);
                _webView.CoreWebView2.PostWebMessageAsString("data:" + base64);
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("WebViewTerminalRenderer", "Write error: " + ex.Message);
            }
        }

        /// <summary>
        /// Sets the terminal color theme.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _theme = theme;
            BackColor = theme.Background;

            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                string themeName = theme == TerminalTheme.Light ? "light" : "dark";
                _webView.CoreWebView2.PostWebMessageAsString("theme:" + themeName);
            }
        }

        /// <summary>
        /// Sets the font size.
        /// </summary>
        public void SetFontSize(float size)
        {
            size = Math.Max(6f, Math.Min(32f, size));
            if (Math.Abs(_fontSize - size) < 0.1f) return;

            _fontSize = size;

            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("fontSize:" + size.ToString());
            }

            FontSizeChanged?.Invoke(this, new FontSizeChangedEventArgs(size));
        }

        /// <summary>
        /// Types text into the terminal character-by-character via xterm.js input path.
        /// Mimics real keyboard typing to avoid Claude Code's paste detection.
        /// Used for injecting text like the "initializing..." banner and prompt auto-answers.
        /// </summary>
        /// <param name="text">Text to type (without line ending - it will be appended)</param>
        /// <param name="lineEnding">Line ending to append: "cr" (\r), "lf" (\n), "crlf" (\r\n), "none"</param>
        /// <param name="charDelayMs">Delay between characters in milliseconds (default: 15ms)</param>
        public void TypeInputViaXterm(string text, string lineEnding = "cr", int charDelayMs = 15)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "TypeInputViaXterm: Not initialized");
                return;
            }

            string ending = lineEnding switch
            {
                "cr" => "\r",
                "lf" => "\n",
                "crlf" => "\r\n",
                "none" => "",
                _ => "\r"
            };

            string fullText = text + ending;
            string base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fullText));

            DebugLogService?.Trace("WebViewTerminalRenderer", $"TypeInputViaXterm: charDelay={charDelayMs}ms, text=\"{text}\", bytes={fullText.Length}");

            _webView.CoreWebView2.PostWebMessageAsString($"typeInput:{charDelayMs}:{base64}");
        }

        /// <summary>
        /// Configures Enter key retry parameters for testing.
        /// </summary>
        /// <param name="maxRetries">Maximum number of retry attempts (default: 8)</param>
        /// <param name="initialDelayMs">Initial retry delay in milliseconds (default: 500ms)</param>
        public void ConfigureEnterRetry(int maxRetries, int initialDelayMs)
        {
            _maxEnterRetries = Math.Max(1, Math.Min(20, maxRetries)); // Clamp to reasonable range
            _initialRetryDelayMs = Math.Max(100, Math.Min(5000, initialDelayMs)); // Clamp to 100ms-5s
            DebugLogService?.Info("WebViewTerminalRenderer", $"Enter retry configured: maxRetries={_maxEnterRetries}, initialDelay={_initialRetryDelayMs}ms");
        }

        /// <summary>
        /// Gets the last output time for diagnostics.
        /// </summary>
        public DateTime GetLastOutputTime()
        {
            lock (_outputTimeLock)
            {
                return _lastOutputTime;
            }
        }

        /// <summary>
        /// Scrolls to the bottom of the terminal.
        /// </summary>
        public void ScrollToBottom()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("scrollToBottom:");
            }
        }

        /// <summary>
        /// Clears the terminal screen.
        /// </summary>
        public void Clear()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("clear:");
            }
        }

        /// <summary>
        /// Resets the terminal.
        /// </summary>
        public void Reset()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("reset:");
            }
        }

        /// <summary>
        /// Focuses the terminal.
        /// </summary>
        public new void Focus()
        {
            base.Focus();
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("focus:");
            }
        }

        /// <summary>
        /// Sends Enter key via xterm.js input path and waits for acknowledgment.
        /// Includes retry mechanism with output monitoring for busy terminals.
        /// This routes through the same channel as physical keypresses.
        /// Returns a Task that completes when Enter is processed by Claude.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SendEnterViaXtermAsync()
        {
            return await SendEnterViaXtermAsync(System.Threading.CancellationToken.None);
        }

        /// <summary>
        /// Sends Enter key with cancellation token support.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SendEnterViaXtermAsync(System.Threading.CancellationToken cancellationToken)
        {
            return await SendEnterWithRetryAsync(_maxEnterRetries, _initialRetryDelayMs, cancellationToken);
        }

        /// <summary>
        /// Sends Enter key with configurable retry parameters for testing.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SendEnterViaXtermAsync(int maxRetries, int initialDelayMs, System.Threading.CancellationToken cancellationToken)
        {
            return await SendEnterWithRetryAsync(maxRetries, initialDelayMs, cancellationToken);
        }

        /// <summary>
        /// Sends Enter key with retry mechanism and output monitoring.
        /// Detects if Claude is busy by monitoring terminal output changes.
        /// Retries with exponential backoff if Enter is ignored.
        /// </summary>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="initialDelayMs">Initial retry delay in milliseconds</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if Enter was successfully processed.</returns>
        private async System.Threading.Tasks.Task<bool> SendEnterWithRetryAsync(int maxRetries, int initialDelayMs, System.Threading.CancellationToken cancellationToken)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "SendEnterWithRetryAsync: Not initialized");
                return false;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            DebugLogService?.Trace("WebViewTerminalRenderer", $"SendEnterWithRetryAsync starting (maxRetries={maxRetries})");

            // Progressive escalation: Try different methods based on attempt number
            // Attempts 1-2: Method 1 (JS without focus) - handles 87% case
            // Attempts 3-5: Method 2 (focus + JS) - fallback for focus-dependent scenarios
            // Attempts 6-8: Method 3 (SendInput API) - ultimate fallback
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"SendEnterWithRetryAsync cancelled at attempt {attempt}");
                    return false;
                }

                // Capture output time before sending Enter
                DateTime outputTimeBefore;
                lock (_outputTimeLock)
                {
                    outputTimeBefore = _lastOutputTime;
                }

                // Determine which method to use based on attempt number
                string methodName;
                bool enterSent = false;

                if (attempt < 2)
                {
                    // Method 1: JS without focus (attempts 0-1)
                    methodName = "Method 1 (JS without focus)";
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"Attempt {attempt + 1}/{maxRetries}: {methodName}");
                    enterSent = await TrySendEnterViaJsAsync();
                }
                else if (attempt < 5)
                {
                    // Method 2: Focus + JS (attempts 2-4)
                    methodName = "Method 2 (focus + JS)";
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"Attempt {attempt + 1}/{maxRetries}: {methodName}");

                    try
                    {
                        await FocusTerminalWindowAsync();
                        await System.Threading.Tasks.Task.Delay(100);
                        enterSent = await TrySendEnterViaJsAsync();
                    }
                    catch (Exception ex)
                    {
                        DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 2 exception: {ex.Message}");
                        enterSent = false;
                    }
                }
                else
                {
                    // Method 3: SendInput API (attempts 5-7)
                    methodName = "Method 3 (SendInput API)";
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"Attempt {attempt + 1}/{maxRetries}: {methodName}");

                    try
                    {
                        enterSent = await SendEnterViaSendInputAsync();
                    }
                    catch (Exception ex)
                    {
                        DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 3 exception: {ex.Message}");
                        enterSent = false;
                    }
                }

                if (!enterSent)
                {
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"Attempt {attempt + 1}: {methodName} failed to send Enter");
                    // Continue to next attempt
                    continue;
                }

                // Success! The JavaScript side (for Method 1) now verifies cursor movement
                // before sending acknowledgment, so we can trust that the Enter was processed.
                // For Method 2 and 3, the Enter key was sent via focus or SendInput API.
                DebugLogService?.Trace("WebViewTerminalRenderer", $"✅ {methodName} succeeded! Enter key processed.");
                return true;
            }

            DebugLogService?.Error("WebViewTerminalRenderer", $"❌ All {maxRetries} attempts failed with progressive escalation");
            return false;
        }

        /// <summary>
        /// Sends Enter key with multiple fallback methods for reliability (legacy method).
        /// Method 1: JS sendEnter (works ~87% of time)
        /// Method 2: Focus window + JS retry
        /// Method 3: Windows SendInput API (OS-level keyboard simulation)
        /// </summary>
        /// <returns>True if Enter was successfully sent.</returns>
        [Obsolete("Use SendEnterWithRetryAsync instead for better busy terminal handling")]
        public async System.Threading.Tasks.Task<bool> SendEnterWithFallbackAsync()
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "SendEnterWithFallbackAsync: Not initialized");
                return false;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            DebugLogService?.Trace("WebViewTerminalRenderer", $"SendEnterWithFallbackAsync starting");

            // Method 1: Try JS sendEnter (current approach)
            if (await TrySendEnterViaJsAsync())
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 1 (JS sendEnter) succeeded");
                return true;
            }
            DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 1 (JS sendEnter) failed, trying Method 2");

            // Method 2: Focus window explicitly, then JS retry
            await FocusTerminalWindowAsync();
            await System.Threading.Tasks.Task.Delay(100);

            if (await TrySendEnterViaJsAsync())
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 2 (focus + JS) succeeded");
                return true;
            }
            DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 2 (focus + JS) failed, trying Method 3");

            // Method 3: Windows SendInput API (OS-level keyboard simulation)
            var sendInputResult = await SendEnterViaSendInputAsync();
            if (sendInputResult)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", $"Method 3 (SendInput) succeeded");
                return true;
            }

            DebugLogService?.Error("WebViewTerminalRenderer", $"All Enter key methods failed!");
            return false;
        }

        /// <summary>
        /// Try sending Enter via JS with acknowledgment wait.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> TrySendEnterViaJsAsync()
        {
            if (_webView?.CoreWebView2 == null) return false;

            _enterAckTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            _webView.CoreWebView2.PostWebMessageAsString("sendEnter:");

            // Increased timeout for reliability under high load (3s instead of 1.5s)
            var timeout = System.Threading.Tasks.Task.Delay(3000);
            var completed = await System.Threading.Tasks.Task.WhenAny(_enterAckTcs.Task, timeout);

            // Log TaskCompletionSource timeout explicitly for monitoring
            if (completed == timeout)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "⚠️ TaskCompletionSource TIMEOUT after 3000ms - Enter acknowledgment not received");
                return false;
            }

            var result = _enterAckTcs.Task.Result;
            DebugLogService?.Trace("WebViewTerminalRenderer", $"TaskCompletionSource completed successfully (result={result})");
            return result;
        }

        /// <summary>
        /// Focus the terminal window to prepare for SendInput.
        /// </summary>
        private async System.Threading.Tasks.Task FocusTerminalWindowAsync()
        {
            if (_webView?.CoreWebView2 == null) return;

            DebugLogService?.Trace("WebViewTerminalRenderer", "🎯 Focus state change: Requesting terminal focus");

            // Get the parent form's handle
            var form = FindForm();
            if (form != null)
            {
                var handle = form.Handle;
                NativeMethods.FocusWindow(handle);
                DebugLogService?.Trace("WebViewTerminalRenderer", $"Focus state change: Form window focused (handle={handle})");
            }
            else
            {
                DebugLogService?.Warning("WebViewTerminalRenderer", "⚠️ Focus state change: Parent form not found");
            }

            // Also focus xterm.js via JS message
            _webView.CoreWebView2.PostWebMessageAsString("focus:");
            DebugLogService?.Trace("WebViewTerminalRenderer", "Focus state change: xterm.js focus message sent");

            // Small delay to let focus settle
            await System.Threading.Tasks.Task.Delay(50);
            DebugLogService?.Trace("WebViewTerminalRenderer", "✅ Focus state change complete (50ms settle time)");
        }

        /// <summary>
        /// Send Enter key using Windows SendInput API (OS-level keyboard simulation).
        /// This is the most reliable fallback but requires window focus.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> SendEnterViaSendInputAsync()
        {
            // Ensure terminal window is focused
            await FocusTerminalWindowAsync();
            await System.Threading.Tasks.Task.Delay(50);

            // Use Windows SendInput API
            return NativeMethods.SendEnterKey();
        }

        /// <summary>
        /// Sends Enter key via xterm.js input path (synchronous, no acknowledgment).
        /// Use SendEnterViaXtermAsync for reliable synchronization.
        /// </summary>
        public void SendEnterViaXterm()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("sendEnter:");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    }
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }

        #region JSON Message Parsing

        private class TerminalMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public int Cols { get; set; }
            public int Rows { get; set; }
            public string Title { get; set; }
            public int FontSize { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public string SelectedText { get; set; }
        }

        /// <summary>
        /// Simple JSON parser for terminal messages.
        /// Avoids dependency on Newtonsoft.Json.
        /// </summary>
        private TerminalMessage ParseJsonMessage(string json)
        {
            var msg = new TerminalMessage();

            // Remove outer braces and quotes
            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);

            // Parse key-value pairs
            int pos = 0;
            while (pos < json.Length)
            {
                // Skip whitespace and commas
                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ',' || json[pos] == '\n' || json[pos] == '\r'))
                    pos++;

                if (pos >= json.Length) break;

                // Parse key
                string key = ParseJsonString(json, ref pos);
                if (string.IsNullOrEmpty(key)) break;

                // Skip colon
                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':'))
                    pos++;

                // Parse value
                if (pos < json.Length && json[pos] == '"')
                {
                    string value = ParseJsonString(json, ref pos);
                    SetMessageProperty(msg, key, value);
                }
                else
                {
                    // Parse number
                    int start = pos;
                    while (pos < json.Length && char.IsDigit(json[pos]))
                        pos++;
                    if (pos > start)
                    {
                        string numStr = json.Substring(start, pos - start);
                        if (int.TryParse(numStr, out int num))
                        {
                            SetMessageProperty(msg, key, num);
                        }
                    }
                }
            }

            return msg;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            // Skip to opening quote
            while (pos < json.Length && json[pos] != '"')
                pos++;

            if (pos >= json.Length) return null;
            pos++; // Skip opening quote

            var sb = new StringBuilder();
            while (pos < json.Length && json[pos] != '"')
            {
                if (json[pos] == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(json[pos]); break;
                    }
                }
                else
                {
                    sb.Append(json[pos]);
                }
                pos++;
            }

            if (pos < json.Length) pos++; // Skip closing quote

            return sb.ToString();
        }

        private void SetMessageProperty(TerminalMessage msg, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "type": msg.Type = value; break;
                case "data": msg.Data = value; break;
                case "title": msg.Title = value; break;
                case "selectedtext": msg.SelectedText = value; break;
            }
        }

        private void SetMessageProperty(TerminalMessage msg, string key, int value)
        {
            switch (key.ToLowerInvariant())
            {
                case "cols": msg.Cols = value; break;
                case "rows": msg.Rows = value; break;
                case "fontsize": msg.FontSize = value; break;
                case "x": msg.X = value; break;
                case "y": msg.Y = value; break;
            }
        }

        #endregion
    }
}
