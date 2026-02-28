using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.ChatPanel;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.TasksPanel
{
    /// <summary>
    /// Dockable document for the Tasks Panel - Kanban board for task management.
    /// </summary>
    public class TasksPanelDocument : DockContent
    {
        private TasksPanelControl _control;
        private MessageBroker _broker;
        private bool _isDarkTheme = true;

        /// <summary>
        /// Raised when the user requests to inject a task into a terminal.
        /// </summary>
        public event EventHandler<InjectMessageEventArgs> InjectRequested;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Forwards to the inner control.
        /// </summary>
        public void SetZoomFactor(double zoom) => _control?.SetZoomFactor(zoom);

        public TasksPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Tasks";
            TabText = "Tasks";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _control = new TasksPanelControl
            {
                Dock = DockStyle.Fill
            };

            // Wire up inject event from control
            _control.InjectRequested += (s, e) => InjectRequested?.Invoke(this, e);
            _control.ZoomChanged += (s, zoom) => ZoomChanged?.Invoke(this, zoom);

            Controls.Add(_control);
        }

        /// <summary>
        /// Initialize the tasks panel with the MessageBroker for task operations.
        /// </summary>
        public void Initialize(MessageBroker broker, ActivityService activityService = null)
        {
            _broker = broker;
            _control?.Initialize(broker, activityService);
        }

        /// <summary>
        /// Set the debug log service for internal debug panel logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _control?.SetDebugLogService(debugLogService);
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
        /// Refresh the projects dropdown after ProjectService is available.
        /// </summary>
        public void RefreshProjects()
        {
            _control?.RefreshProjects();
        }

        /// <summary>
        /// Sets the font size for the tasks panel.
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
            return "TasksPanel";
        }
    }
}
