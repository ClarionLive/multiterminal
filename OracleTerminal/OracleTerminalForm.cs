using System;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Controls;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.OracleTerminal
{
    /// <summary>
    /// Standalone popup Form hosting Oracle's ConPTY terminal.
    /// Starts hidden. Closing hides instead of destroying (Oracle runs forever).
    /// Only truly closes when ForceClose() is called during app shutdown.
    /// </summary>
    public class OracleTerminalForm : Form
    {
        private TerminalControl _terminal;
        private bool _allowClose;
        private bool _allowVisible;
        private bool _creatingHandle;
        private DebugLogService _debugLogService;

        /// <summary>Fired when the terminal process exits.</summary>
        public event EventHandler TerminalExited;

        /// <summary>Whether the terminal process is currently running.</summary>
        public bool IsTerminalRunning { get; private set; }

        public OracleTerminalForm(DebugLogService debugLogService = null)
        {
            _debugLogService = debugLogService;

            Text = "Oracle";
            Size = new Size(1000, 650);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            Icon = null;
            MinimumSize = new Size(400, 300);

            _terminal = new TerminalControl { Dock = DockStyle.Fill };
            _terminal.SetDebugLogService(_debugLogService);
            _terminal.ProcessExited += OnProcessExited;

            Controls.Add(_terminal);
        }

        /// <summary>
        /// Suppress visibility until explicitly requested via Show()/ShowPopup().
        /// Prevents WinForms internals (handle creation, owner relationship) from
        /// making the form appear before the user asks for it.
        /// </summary>
        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible)
            {
                // Still create the handle so WebView2 can initialize in the background.
                // Guard against recursion: CreateHandle() may re-enter SetVisibleCore.
                if (!Created && !_creatingHandle)
                {
                    _creatingHandle = true;
                    CreateControl();
                    _creatingHandle = false;
                }
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        /// <summary>
        /// Start the Claude Code terminal with the given autorun command.
        /// Forces handle creation so WebView2 initializes while the form stays hidden.
        /// </summary>
        public void StartTerminal(string workingDirectory, string docId, string autoRunCommand)
        {
            // Force handle creation so WebView2 renderer initializes (triggers HandleCreated).
            // SetVisibleCore override keeps the form hidden during this.
            if (!Created)
                CreateControl();

            _terminal.Start(
                workingDirectory: workingDirectory,
                docId: docId,
                terminalName: OracleService.OracleName,
                autoRunCommand: autoRunCommand);

            IsTerminalRunning = true;
        }

        /// <summary>
        /// Show the form (sets _allowVisible so SetVisibleCore lets it through).
        /// </summary>
        public new void Show()
        {
            _allowVisible = true;
            base.Show();
            BringToFront();
        }

        /// <summary>
        /// Apply theme to the terminal.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            var theme = isDark ? TerminalTheme.Dark : TerminalTheme.Light;
            _terminal?.SetTheme(theme);
            BackColor = isDark ? Color.FromArgb(30, 30, 37) : Color.FromArgb(239, 241, 245);
        }

        /// <summary>
        /// Sets the terminal font size.
        /// </summary>
        public void SetFontSize(float size)
        {
            _terminal?.SetFontSize(size);
        }

        /// <summary>
        /// Force close the form (app shutdown). Bypasses the hide-on-close behavior.
        /// </summary>
        public void ForceClose()
        {
            _allowClose = true;
            _allowVisible = true; // Allow visibility changes during shutdown
            IsTerminalRunning = false;

            if (!IsDisposed)
            {
                try { Close(); }
                catch { /* already disposed */ }
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            IsTerminalRunning = false;

            if (InvokeRequired)
                BeginInvoke(new Action(() => TerminalExited?.Invoke(this, EventArgs.Empty)));
            else
                TerminalExited?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                // Hide instead of close — Oracle keeps running
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_terminal != null)
                {
                    _terminal.ProcessExited -= OnProcessExited;
                    _terminal.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
