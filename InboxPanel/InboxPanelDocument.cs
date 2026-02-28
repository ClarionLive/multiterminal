using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.InboxPanel
{
    /// <summary>
    /// Dockable document for the Inbox Panel - displays notification messages for the user.
    /// </summary>
    public class InboxPanelDocument : DockContent
    {
        private InboxPanelControl _control;
        private MessageBroker _broker;
        private bool _isDarkTheme = true;

        /// <summary>
        /// Raised when the user clicks a task link to navigate to that task on the kanban board.
        /// The string argument is the task ID.
        /// </summary>
        public event EventHandler<string> NavigateToTask;

        public InboxPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Inbox";
            TabText = "Inbox";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _control = new InboxPanelControl
            {
                Dock = DockStyle.Fill
            };

            // Wire up navigate-to-task event from control
            _control.NavigateToTask += (s, taskId) => NavigateToTask?.Invoke(this, taskId);

            Controls.Add(_control);
        }

        /// <summary>
        /// Initialize the inbox panel with the MessageBroker for inbox operations.
        /// </summary>
        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            _control?.Initialize(broker);
        }

        /// <summary>
        /// Apply theme to the panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _control?.ApplyTheme(isDark);
        }

        /// <summary>
        /// Sets the font size for the inbox panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            _control?.SetFontSize(size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _control?.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override string GetPersistString()
        {
            return "InboxPanel";
        }
    }
}
