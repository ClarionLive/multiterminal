using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.Controls;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.OracleTerminal
{
    /// <summary>
    /// Dockable document hosting Oracle's ConPTY terminal.
    /// As a DockContent, Oracle can float OR dock (drag-to-dock) and her position is
    /// persisted via the DockPanel layout (SaveAsXml/LoadFromXml + GetPersistString).
    /// Closing hides instead of destroying (Oracle runs forever) via HideOnClose.
    /// Only truly closes when ForceClose() is called during app shutdown.
    /// </summary>
    public class OracleTerminalForm : DockContent
    {
        private TerminalControl _terminal;
        private DebugLogService _debugLogService;

        /// <summary>Fired when the terminal process exits.</summary>
        public event EventHandler TerminalExited;

        /// <summary>Whether the terminal process is currently running.</summary>
        public bool IsTerminalRunning { get; private set; }

        public OracleTerminalForm(DebugLogService debugLogService = null)
        {
            _debugLogService = debugLogService;

            Text = "Oracle";
            TabText = "Oracle";
            Size = new Size(1000, 650);
            MinimumSize = new Size(400, 300);
            ShowInTaskbar = false;
            Icon = SystemIcons.Application;

            // Allow Oracle to float or dock anywhere; default to floating until the
            // user docks her (or a saved layout restores her last position).
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.Float;
            CloseButtonVisible = true;

            // Hide instead of dispose when closed — Oracle is always-on. ForceClose()
            // flips this off at shutdown so the form can truly close.
            HideOnClose = true;

            _terminal = new TerminalControl { Dock = DockStyle.Fill };
            _terminal.SetDebugLogService(_debugLogService);
            _terminal.ProcessExited += OnProcessExited;

            Controls.Add(_terminal);
        }

        /// <summary>
        /// Start the Claude Code terminal with the given autorun command.
        /// Forces handle creation so WebView2 initializes before the terminal starts.
        /// </summary>
        public void StartTerminal(string workingDirectory, string docId, string autoRunCommand)
        {
            // Force handle creation so the WebView2 renderer initializes (triggers HandleCreated).
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
        /// Force close the form (app shutdown). Bypasses the hide-on-close behavior so
        /// the DockContent is truly disposed instead of just hidden.
        /// </summary>
        public void ForceClose()
        {
            HideOnClose = false;
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

        protected override string GetPersistString()
        {
            return "Oracle";
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
