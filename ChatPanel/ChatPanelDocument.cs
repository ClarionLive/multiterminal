using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.ChatPanel
{
    /// <summary>
    /// Dockable document for the chat panel.
    /// </summary>
    public class ChatPanelDocument : DockContent
    {
        private ChatPanelControl _chatControl;
        private MessageBroker _broker;

        /// <summary>
        /// Raised when the user requests to inject a message into a terminal.
        /// </summary>
        public event EventHandler<InjectMessageEventArgs> InjectRequested;

        /// <summary>
        /// Raised when the user requests to reply to a message.
        /// </summary>
        public event EventHandler<ReplyMessageEventArgs> ReplyRequested;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Forwards to the inner control.
        /// </summary>
        public void SetZoomFactor(double zoom) => _chatControl?.SetZoomFactor(zoom);

        public ChatPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Multi-Claude Chat";
            TabText = "Chat";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Information;

            _chatControl = new ChatPanelControl
            {
                Dock = DockStyle.Fill
            };

            _chatControl.InjectRequested += (s, e) => InjectRequested?.Invoke(this, e);
            _chatControl.ReplyRequested += (s, e) => ReplyRequested?.Invoke(this, e);
            _chatControl.ZoomChanged += (s, zoom) => ZoomChanged?.Invoke(this, zoom);

            Controls.Add(_chatControl);
        }

        /// <summary>
        /// Initialize the chat panel with a message broker.
        /// </summary>
        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            _chatControl.Initialize(broker);
        }

        /// <summary>
        /// Apply a theme to the chat panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _chatControl.ApplyTheme(isDark);
        }

        /// <summary>
        /// Update the connection status indicator.
        /// </summary>
        public void UpdateConnectionStatus(bool connected)
        {
            _chatControl.UpdateConnectionStatus(connected);
        }

        /// <summary>
        /// Clear all messages from the chat panel.
        /// </summary>
        public void ClearMessages()
        {
            _chatControl.ClearMessages();
        }

        /// <summary>
        /// Sets the font size for the chat panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            _chatControl.SetFontSize(size);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chatControl?.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override string GetPersistString()
        {
            return "ChatPanel";
        }
    }
}
