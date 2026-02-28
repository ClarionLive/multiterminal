using System;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Services;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.AgentPanel
{
    public class AgentPanelDocument : DockContent
    {
        private AgentPanelControl _control;

        public AgentPanelDocument()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Fired when the user clicks the close (X) button inside the agent panel WebView.
        /// </summary>
        public event EventHandler CloseRequested;

        private void InitializeComponent()
        {
            Text = "Agent";
            TabText = "Agent";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight |
                        DockAreas.DockBottom | DockAreas.DockTop |
                        DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true;

            _control = new AgentPanelControl { Dock = DockStyle.Fill };
            _control.CloseRequested += (s, e) => CloseRequested?.Invoke(this, e);
            Controls.Add(_control);
        }

        protected override string GetPersistString() => "AgentPanel";

        /// <summary>
        /// Attach this panel to an agent message source to display its conversation.
        /// Works with both AgentProcess (piped I/O) and TranscriptTailer (file watching).
        /// </summary>
        public void AttachAgent(IAgentMessageSource agent, string agentName = null, string taskDescription = null, string subagentType = null, bool isTeamAgent = false)
        {
            _control?.AttachAgent(agent, agentName, taskDescription, subagentType, isTeamAgent);
            if (!string.IsNullOrEmpty(agentName))
            {
                Text = $"Agent: {agentName}";
                TabText = agentName;
            }
        }

        /// <summary>
        /// Detach from the current agent (panel stays open, agent keeps running).
        /// </summary>
        public void DetachAgent()
        {
            _control?.DetachAgent();
            Text = "Agent";
            TabText = "Agent";
        }

        /// <summary>
        /// Get the currently attached agent message source, if any.
        /// </summary>
        public IAgentMessageSource AttachedAgent => _control?.AttachedAgent;

        /// <summary>
        /// Whether an agent is currently attached and active.
        /// </summary>
        public bool HasActiveAgent => _control?.AttachedAgent?.IsActive == true;

        /// <summary>
        /// Stop the attached agent process.
        /// </summary>
        public async System.Threading.Tasks.Task StopAgentAsync()
        {
            if (_control != null)
                await _control.StopAgentAsync();
        }

        public void ApplyTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _control?.ApplyTheme(isDark);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _control?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
