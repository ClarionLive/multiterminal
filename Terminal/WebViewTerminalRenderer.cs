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

        // Enter-key acknowledgment correlation (task f420feeb, census F2). The decision itself lives
        // in EnterAckRegistry, which is pure and therefore testable — this control cannot be
        // instantiated in a test. See that class for what the single shared field used to get wrong.
        private readonly EnterAckRegistry _enterAcks = new();

        // ⚠️ A SEPARATE REGISTRY FROM _enterAcks, NOT A SHARED ONE (task 8b270b37). Both mint
        // ids from their own counter, so one registry serving both channels would let an
        // enterAck release a typing waiter that happened to hold the same number.
        private readonly TypeAckRegistry _typeAcks = new();

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
                        // Complete ONLY the waiter that asked for this Enter (task f420feeb).
                        OnEnterAcknowledged(message.EnterJobId);
                        break;

                    case "typeAck":
                        // This job's last character, including its trailing line ending, was sent.
                        // ⚠️ NOT "the text was submitted" — see TypeInputViaXtermAsync (task 8b270b37).
                        OnTypeAcknowledged(message.TypeJobId, typed: true);
                        break;

                    case "typeAbort":
                        // The page dropped the job part-way through. Reported rather than left to time
                        // out, so the caller learns the truth in milliseconds instead of minutes.
                        OnTypeAcknowledged(message.TypeJobId, typed: false);
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
                string themeName = theme.IsDark ? "dark" : "light";
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
        /// Types text into the terminal character-by-character via the xterm.js input path, and waits
        /// for the page to acknowledge that job by id.
        /// Mimics real keyboard typing to avoid Claude Code's paste detection.
        /// Used for injecting text like the "initializing..." banner and prompt auto-answers.
        ///
        /// <para><b>⚠️ READ THE RETURN VALUE'S MEANING BEFORE USING IT (task 8b270b37, item 2).</b>
        /// True means the characters were typed and the page said so. It does NOT mean the text was
        /// submitted, accepted, or even seen by whatever is running in the terminal — and mistaking
        /// the one for the other is literally the defect this ticket exists to fix: a trailing CR
        /// that Claude's composer takes as a newline leaves the prompt typed but unsent, and every
        /// signal on this path still says "delivered". A genuine submission oracle is checklist item
        /// 3 of the same ticket. Until it exists, no caller may treat a true here as evidence that
        /// anything was submitted.</para>
        ///
        /// <para>This method was <c>void</c> until item 2. It returned silently when the renderer was
        /// not initialized, which is why an undelivered prompt could be logged as delivered: there
        /// was no channel through which any caller COULD have noticed.</para>
        /// </summary>
        /// <param name="text">Text to type (without line ending - it will be appended)</param>
        /// <param name="lineEnding">Line ending to append: "cr" (\r), "lf" (\n), "crlf" (\r\n), "none"</param>
        /// <param name="charDelayMs">Delay between characters in milliseconds (default: 15ms)</param>
        /// <returns>
        /// True when the page acknowledged that this job's last character — including its trailing
        /// line ending — had been sent. False when the renderer was not initialized, when the page
        /// reported that it dropped the job, or when no acknowledgment arrived within
        /// <see cref="TypeAckRegistry.AckBudgetMs"/>. NEVER a statement about submission.
        /// </returns>
        public async System.Threading.Tasks.Task<bool> TypeInputViaXtermAsync(string text, string lineEnding = "cr", int charDelayMs = 15)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "TypeInputViaXtermAsync: Not initialized");
                return false;
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
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(fullText);
            string base64 = Convert.ToBase64String(payload);

            // Identity per request, mirroring the Enter path (task f420feeb). Two typing jobs into one
            // pane overlap routinely — the "initializing..." banner and a spawned helper's prompt land
            // within a second of each other — and the page serialises them through one queue, so a
            // bare "typing finished" signal could not say WHOSE typing finished.
            var (jobId, ack) = _typeAcks.Register();

            try
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", $"TypeInputViaXtermAsync: job={jobId}, charDelay={charDelayMs}ms, text=\"{text}\", bytes={payload.Length}");

                // Wire format: "typeInput:<jobId>:<delayMs>:<base64>". The id is OPAQUE to the page —
                // it is minted here, matched here, and the page only ever copies it back.
                _webView.CoreWebView2.PostWebMessageAsString($"typeInput:{jobId}:{charDelayMs}:{base64}");

                int budgetMs = TypeAckRegistry.AckBudgetMs(payload.Length, charDelayMs);
                var timeout = System.Threading.Tasks.Task.Delay(budgetMs);
                var completed = await System.Threading.Tasks.Task.WhenAny(ack, timeout);

                if (completed == timeout)
                {
                    DebugLogService?.Warning("WebViewTerminalRenderer", $"typeInput job {jobId} was never acknowledged within {budgetMs}ms ({payload.Length} bytes at {charDelayMs}ms/char) — reporting NOT typed.");
                    return false;
                }

                // Await the task THIS call created, never a re-read of shared state: `ack` is a local
                // and cannot be swapped out from under it (the bug f420feeb removed from the Enter
                // path, not reintroduced here).
                bool typed = await ack;
                DebugLogService?.Trace("WebViewTerminalRenderer", $"typeInput job {jobId} acknowledged (typed={typed}).");
                return typed;
            }
            finally
            {
                // Timed-out jobs must not accumulate: the renderer outlives every one of them.
                _typeAcks.Release(jobId);
            }
        }

        /// <summary>
        /// How long to let the terminal render before reading its screen back.
        ///
        /// <para>The submit path is not instantaneous on either side: xterm writes the CR out to
        /// ConPTY, Claude Code's input handler acts on it, and the composer is repainted. Sampling
        /// before that has finished reads the PRE-Enter screen — in which the payload is still in
        /// the box in every case, success included. The whole oracle would invert.</para>
        ///
        /// <para>750ms is chosen against the measurement this ticket was opened on, where the whole
        /// contested window at the start of a turn was of order 100ms. It is slack, not a tuned
        /// value, and it is slack in the safe direction: waiting too long costs a delayed report,
        /// waiting too little costs a wrong one.</para>
        /// </summary>
        internal const int DefaultComposerSettleMs = 750;

        /// <summary>
        /// Asks the page for the rows around the cursor and hands them to <see cref="ComposerOracle"/>.
        ///
        /// <para>⚠️ EVERY failure of the CHECK returns <see cref="SubmissionVerdict.Unknown"/> — no
        /// page, no answer, an answer that will not parse, an answer the page marked not-ok. None of
        /// them is evidence about the prompt, and turning "I could not look" into either "it was
        /// submitted" or "it was not" is how a detector starts lying (see
        /// <c>.claude/rules/verification-discipline.md</c>, which this ticket exists because of).</para>
        /// </summary>
        /// <param name="fingerprint">Output of <see cref="ComposerOracle.Fingerprint"/>.</param>
        internal async System.Threading.Tasks.Task<SubmissionCheck> ProbeComposerAsync(string fingerprint)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                return SubmissionCheck.Unknown("the terminal renderer is not initialized, so its screen could not be read");
            }

            string json;
            try
            {
                json = await _webView.CoreWebView2.ExecuteScriptAsync(
                    $"window.mtSampleComposerRegion({ComposerOracle.SampleRowsAbove},{ComposerOracle.SampleRowsBelow})");
            }
            catch (Exception ex)
            {
                return SubmissionCheck.Unknown($"reading the terminal's screen failed: {ex.Message}");
            }

            // ExecuteScriptAsync answers the literal "null" when the script threw — which is what a
            // terminal.html predating this probe does, since the function simply is not there.
            if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal))
            {
                return SubmissionCheck.Unknown("the page did not answer the composer probe — this terminal.html has no mtSampleComposerRegion, so no prompt can ever be confirmed here");
            }

            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(json);
                var root = parsed.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return SubmissionCheck.Unknown("the composer probe returned something that is not an object");
                }

                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != System.Text.Json.JsonValueKind.True)
                {
                    string reason = root.TryGetProperty("reason", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String
                        ? r.GetString()
                        : "no reason given";
                    return SubmissionCheck.Unknown($"the page could not sample its own screen: {reason}");
                }

                if (!root.TryGetProperty("cursorRow", out var cursorEl) || !cursorEl.TryGetInt32(out int cursorRow))
                {
                    return SubmissionCheck.Unknown("the composer probe answered without a cursor row");
                }

                if (!root.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return SubmissionCheck.Unknown("the composer probe answered without any rows");
                }

                var rows = new List<string>(rowsEl.GetArrayLength());
                foreach (var row in rowsEl.EnumerateArray())
                {
                    rows.Add(row.ValueKind == System.Text.Json.JsonValueKind.String ? row.GetString() : string.Empty);
                }

                return ComposerOracle.Evaluate(rows, cursorRow, fingerprint);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return SubmissionCheck.Unknown($"the composer probe's answer would not parse: {ex.Message}");
            }
        }

        /// <summary>
        /// Types text, then answers the question the rest of this path cannot: <b>did it actually
        /// go?</b> (task 8b270b37, checklist items 3 and 4.)
        ///
        /// <para>On <see cref="SubmissionVerdict.NotConfirmed"/> it retries <b>the submit only</b>,
        /// once, and re-checks. ⚠️ IT NEVER RETYPES THE PAYLOAD. Retyping is how a prompt gets
        /// delivered twice — the failure mode already argued at length at the task-drop fallback in
        /// <c>MainForm</c> — and it is not needed, because the verdict being acted on is precisely
        /// "the text is already in the box". <see cref="SendEnterViaXtermAsync()"/> adds no text, so
        /// it cannot duplicate; if the box is somehow empty it submits nothing.</para>
        ///
        /// <para><b>⚠️ ONE retry, not a loop.</b> The failure this addresses is a ~100ms race at the
        /// start of a turn. By the time the first probe has run, that window is long gone, so a
        /// second Enter either works or the cause is something else entirely — and something else
        /// entirely is a thing to REPORT, not to keep pressing Enter at.</para>
        ///
        /// <para>A false return from the typing step short-circuits to NotConfirmed WITHOUT an Enter
        /// retry, and that asymmetry is load-bearing: if the characters never arrived, the box holds
        /// whatever was there before — quite possibly the human's half-written line — and an Enter
        /// would submit THAT.</para>
        /// </summary>
        /// <param name="text">Text to type, without a line ending.</param>
        /// <param name="lineEnding">As <see cref="TypeInputViaXtermAsync"/>. <c>"none"</c> asks for no submission and therefore yields Unknown.</param>
        /// <param name="charDelayMs">As <see cref="TypeInputViaXtermAsync"/>.</param>
        /// <param name="settleMs">Render settle time before the screen is read; see <see cref="DefaultComposerSettleMs"/>.</param>
        internal async System.Threading.Tasks.Task<SubmissionCheck> TypeAndConfirmSubmissionAsync(
            string text,
            string lineEnding = "cr",
            int charDelayMs = 15,
            int settleMs = DefaultComposerSettleMs)
        {
            string fingerprint = ComposerOracle.Fingerprint(text);

            bool typed = await TypeInputViaXtermAsync(text, lineEnding, charDelayMs);
            if (!typed)
            {
                // ⚠️ THE ADVICE HERE IS DELIBERATELY WEAKER THAN "nothing was typed", which is the
                // sentence everything ELSE on this path uses. An unacknowledged typing job is not the
                // same as an untyped one: the page acks on its LAST character, so a job that was
                // aborted part-way, or timed out while still typing, leaves a PARTIAL line in that
                // composer. Telling the recipient "nothing was typed" would be a confident claim
                // about the one thing this branch specifically does not know.
                return SubmissionCheck.NotConfirmed(
                    "the characters were never acknowledged as typed, so nothing was submitted; the Enter was NOT retried, because pressing Enter on a box this text may never have reached would submit whatever else is in it",
                    "⚠️ MT does not know how much of the text reached that pane — possibly none of it, possibly a partial line. LOOK at the composer before sending anything: retyping on top of a partial line garbles both.");
            }

            if (string.Equals(lineEnding, "none", StringComparison.OrdinalIgnoreCase))
            {
                return SubmissionCheck.Unknown("no line ending was appended, so no submission was asked for and there is nothing to confirm");
            }

            await System.Threading.Tasks.Task.Delay(settleMs);
            var first = await ProbeComposerAsync(fingerprint);
            if (first.Verdict != SubmissionVerdict.NotConfirmed)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", $"Submission check: {first}");
                return first;
            }

            DebugLogService?.Warning("WebViewTerminalRenderer", $"Submission check FAILED: {first.Reason}. Retrying the Enter only — the text is already in the box and must not be typed again.");

            bool enterSent = await SendEnterViaXtermAsync();
            if (!enterSent)
            {
                // The FIRST check's advice is carried forward, not re-invented: it is the one that
                // established the payload is in the box, and that fact did not change because an
                // Enter failed to send.
                return SubmissionCheck.NotConfirmed($"{first.Reason}; the retry Enter could not be sent either", first.Advice);
            }

            await System.Threading.Tasks.Task.Delay(settleMs);
            var second = await ProbeComposerAsync(fingerprint);

            switch (second.Verdict)
            {
                case SubmissionVerdict.Confirmed:
                    DebugLogService?.Info("WebViewTerminalRenderer", "Submission check passed on the retry Enter — the first Enter had been taken as a newline.");
                    return SubmissionCheck.Confirmed($"{second.Reason} (it took a second Enter; the first was not acted on)");

                case SubmissionVerdict.NotConfirmed:
                    return SubmissionCheck.NotConfirmed(
                        $"{second.Reason} — and this is AFTER a retry Enter, so the text is not going to submit on its own",
                        second.Advice);

                default:
                    return SubmissionCheck.Unknown($"{second.Reason} (asked after a retry Enter, because the first check said the text was still in the box)");
            }
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
        /// Sends Enter by escalating through three send methods, retrying for as long as the SEND
        /// itself reports failure.
        ///
        /// <para>⚠️ WHAT IT DOES NOT DO (task 8b270b37). The previous version of this comment said
        /// it "detects if Claude is busy by monitoring terminal output changes". It never did: it
        /// snapshotted <c>_lastOutputTime</c> into a local and then never compared that local
        /// against anything. No terminal output is observed here, and nothing here establishes that
        /// the Enter submitted anything. A retry is triggered ONLY by a failed send — an attempt
        /// whose acknowledgment did not arrive within 3s, or which threw. An acknowledged attempt is
        /// reported as success whether or not a prompt was submitted, because the acknowledgment
        /// cannot tell the two apart (see the <c>sendEnter</c> case in
        /// <c>Terminal/terminal.html</c>: it acks on any cursor movement, and acks again anyway when
        /// its own 500ms poll expires).</para>
        ///
        /// <para>A real submission oracle is checklist item 3 of task 8b270b37. The output-change
        /// idea moved there; it was not dropped. It is deliberately not started here, because half
        /// an oracle wired into this retry loop would have to be unpicked to build the whole one.</para>
        ///
        /// <para>There is also no backoff of any kind between attempts, despite
        /// <paramref name="initialDelayMs"/>. That parameter is accepted here, threaded in from
        /// <c>ConfigureEnterRetry</c> and the public overloads, and read by nothing. It is left in
        /// place rather than removed, because removing it is an API change and this is a comment
        /// correction.</para>
        /// </summary>
        /// <param name="maxRetries">Maximum number of attempts, across all three methods.</param>
        /// <param name="initialDelayMs">Currently UNUSED — see the remark above.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// True when one attempt's send was acknowledged (Method 1) or completed without throwing
        /// (Methods 2 and 3). That means the keypress was delivered — NOT that a prompt was
        /// submitted.
        /// </returns>
        private async System.Threading.Tasks.Task<bool> SendEnterWithRetryAsync(int maxRetries, int initialDelayMs, System.Threading.CancellationToken cancellationToken)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                DebugLogService?.Trace("WebViewTerminalRenderer", "SendEnterWithRetryAsync: Not initialized");
                return false;
            }

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

                // ⚠️ NO OUTPUT SNAPSHOT IS TAKEN HERE, deliberately (task 8b270b37). This used to
                // read _lastOutputTime into a local that nothing ever compared against — dead code
                // whose only effect was to make the doc comment's "monitors terminal output changes"
                // read as implemented. Deciding whether an Enter actually submitted needs an oracle
                // this loop does not have; that is checklist item 3 of the same ticket, which is
                // where the output-change idea went.

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

                // The send was acknowledged (Method 1) or completed without throwing (Methods 2
                // and 3).
                //
                // ⚠️ THAT IS NOT "the Enter was processed", which this comment used to claim on the
                // strength of terminal.html's cursor check (task 8b270b37). That check acks on ANY
                // cursor movement — including the movement a CR makes when the composer takes it as
                // a NEWLINE instead of a submission — and acks again unconditionally when its own
                // 500ms poll expires. Telling those two apart is checklist item 3 of that ticket.
                DebugLogService?.Trace("WebViewTerminalRenderer", $"{methodName} send acknowledged (not a submission guarantee).");
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
        /// Completes the waiter for one acknowledged Enter, and REPORTS WHICH PATH IT TOOK.
        /// <para>⚠️ The compatibility path logs at Warning on success, which reads like an
        /// over-reaction and is not. If the page's id echo were broken — a mistyped field, or an
        /// echo added to only one of terminal.html's two ack sites — every ack would arrive id-less
        /// and be silently rescued here. Enters would complete promptly, the suite would stay green,
        /// and the correlation this exists to provide would never run once. So routine firing of
        /// that path is not compatibility working; it is the fix not working, and this log is the
        /// only thing that can tell the two apart at runtime.</para>
        /// </summary>
        /// <param name="jobId">Job id carried by the ack; null/empty only from a page predating it.</param>
        private void OnEnterAcknowledged(string jobId)
        {
            switch (_enterAcks.Complete(jobId))
            {
                case EnterAckOutcome.Correlated:
                    break;

                case EnterAckOutcome.NoWaiter:
                    // Worded for BOTH shapes. It used to name a job id unconditionally, which read as
                    // nonsense for an id-less ack arriving when nothing was outstanding — the case
                    // that used to be misreported as "multiple waiters" (see EnterAckOutcome).
                    DebugLogService?.Trace(
                        "WebViewTerminalRenderer",
                        string.IsNullOrEmpty(jobId)
                            ? "enterAck carried no job id and nothing was waiting — dropped"
                            : $"enterAck for job '{jobId}' has no waiter (already timed out) — dropped");
                    break;

                case EnterAckOutcome.CompatibilitySingleWaiter:
                    DebugLogService?.Warning("WebViewTerminalRenderer", "enterAck carried NO job id — released the single outstanding waiter via the compatibility path. If this is routine, terminal.html is not echoing the id and per-job correlation is NOT in effect.");
                    break;

                case EnterAckOutcome.AmbiguousDropped:
                    DebugLogService?.Warning("WebViewTerminalRenderer", "enterAck carried NO job id while multiple waiters were outstanding — dropped rather than guess which caller it belonged to.");
                    break;
            }
        }

        /// <summary>
        /// Completes the waiter for one typing job (task 8b270b37, item 2).
        /// <para>⚠️ An ack carrying NO id is DROPPED and logged at Warning, where the Enter path
        /// instead rescues the single outstanding waiter. That asymmetry is deliberate and is argued
        /// in <see cref="TypeAckRegistry"/>: no <c>terminal.html</c> has ever sent <c>typeAck</c>
        /// without an id, so an id-less one has no benign origin — it can only mean the echo is
        /// broken. Rescuing it would make every typing job complete promptly, keep the suite green,
        /// and leave the correlation this exists to provide having never run once.</para>
        /// </summary>
        private void OnTypeAcknowledged(string jobId, bool typed)
        {
            switch (_typeAcks.Complete(jobId, typed))
            {
                case TypeAckOutcome.Correlated:
                    break;

                case TypeAckOutcome.NoWaiter:
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"typeAck for job '{jobId}' has no waiter (already timed out, or acked twice) — dropped.");
                    break;

                case TypeAckOutcome.Uncorrelated:
                    DebugLogService?.Warning("WebViewTerminalRenderer", "typeAck carried NO job id — dropped. terminal.html is not echoing the id, so no typing job can ever be confirmed and every one of them will time out.");
                    break;
            }
        }

        /// <summary>
        /// Try sending Enter via JS with acknowledgment wait.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> TrySendEnterViaJsAsync()
        {
            if (_webView?.CoreWebView2 == null) return false;

            // Identity per request (task f420feeb).
            var (jobId, ack) = _enterAcks.Register();

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString("sendEnter:" + jobId);

                // Increased timeout for reliability under high load (3s instead of 1.5s)
                var timeout = System.Threading.Tasks.Task.Delay(3000);
                var completed = await System.Threading.Tasks.Task.WhenAny(ack, timeout);

                // Log TaskCompletionSource timeout explicitly for monitoring
                if (completed == timeout)
                {
                    DebugLogService?.Trace("WebViewTerminalRenderer", $"⚠️ TaskCompletionSource TIMEOUT after 3000ms - Enter acknowledgment not received (job {jobId})");
                    return false;
                }

                // ⚠️ AWAIT THE TASK THIS CALL CREATED. The previous version re-read the shared field
                // here and called .Result on it, so a concurrent injection that had replaced the field
                // turned this line into a blocking wait on an incomplete TaskCompletionSource — on the
                // UI thread. `ack` is a local and cannot be swapped out from under it.
                bool result = await ack;
                DebugLogService?.Trace("WebViewTerminalRenderer", $"TaskCompletionSource completed successfully (job {jobId}, result={result})");
                return result;
            }
            finally
            {
                // Timed-out and faulted attempts must not accumulate: this runs up to 5 times per
                // failed injection and the renderer outlives every one of them.
                _enterAcks.Release(jobId);
            }
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
        /// <para>Sends the sentinel job id <c>fire-and-forget</c> rather than an empty one (task
        /// f420feeb). It has no waiter by definition, and the sentinel makes the resulting ack match
        /// nothing instead of arriving id-less and tripping OnEnterAcknowledged's single-waiter
        /// shim — which would complete a REAL concurrent injection's wait with this method's ack.</para>
        /// </summary>
        public void SendEnterViaXterm()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("sendEnter:fire-and-forget");
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

            /// <summary>
            /// Job id echoed back on an <c>enterAck</c> (task f420feeb). Carried as a STRING, not a
            /// number: ParseJsonMessage's number branch reads digits only, so a wrapped (negative)
            /// counter would parse as garbage. A string round-trips whatever the counter produces.
            /// </summary>
            public string EnterJobId { get; set; }

            /// <summary>
            /// Job id echoed back on a <c>typeAck</c> / <c>typeAbort</c> (task 8b270b37). A STRING for
            /// the same reason as <see cref="EnterJobId"/>: ParseJsonMessage's number branch reads
            /// digits only.
            /// </summary>
            public string TypeJobId { get; set; }
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
                case "enterjobid": msg.EnterJobId = value; break;
                case "typejobid": msg.TypeJobId = value; break;
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
