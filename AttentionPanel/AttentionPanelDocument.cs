using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.MCPServer.Services;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// Dockable panel showing every open agent session, and which of them are blocked on the owner
    /// (task 2289bb8a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A standalone dockable panel, deliberately NOT a HUD tab.</b> The rail's whole job is to
    /// span every session at once; a HUD tab belongs to a single terminal (or to the board) and
    /// could only ever show one. That choice also keeps it clear of the
    /// <c>HudTabContainer.ApplyTheme</c> type-name chain — this panel themes through its own
    /// <see cref="ApplyTheme"/>, like every other dockable panel.
    /// </para>
    /// <para>
    /// Named "Attention", not "Sessions": <c>HudSessionsPanel</c> is a historical session TIMELINE,
    /// a different feature that would otherwise collide on the word.
    /// </para>
    /// </remarks>
    public class AttentionPanelDocument : DockContent
    {
        private AttentionPanelControl _control;

        /// <summary>Raised when the user clicks a card. Argument is the session id.</summary>
        public event EventHandler<string> FocusSessionRequested;

        /// <summary>Raised when the user clicks a ticket chip. Argument is the task id.</summary>
        public event EventHandler<string> OpenTicketRequested;

        /// <summary>Raised when the user changes the ordering preference ("attention" | "fixed").</summary>
        public event EventHandler<string> OrderChanged;

        /// <summary>Creates the dockable attention panel.</summary>
        public AttentionPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Attention";
            TabText = "Attention";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;

            // Prevent disposal when closed so the toolbar toggle can reopen it, matching every
            // other panel in the app.
            HideOnClose = true;

            _control = new AttentionPanelControl { Dock = DockStyle.Fill };
            _control.FocusSessionRequested += (s, id) => FocusSessionRequested?.Invoke(this, id);
            _control.OpenTicketRequested += (s, taskId) => OpenTicketRequested?.Invoke(this, taskId);
            _control.OrderChanged += (s, order) => OrderChanged?.Invoke(this, order);

            Controls.Add(_control);
        }

        /// <summary>Pushes the current card list to the view.</summary>
        public void SetSessions(IEnumerable<object> sessions) => _control?.SetSessions(sessions);

        /// <summary>Applies the saved ordering preference.</summary>
        public void SetOrder(string order) => _control?.SetOrder(order);

        /// <summary>Applies the app theme to the panel.</summary>
        public void ApplyTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _control?.ApplyTheme(isDark);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _control?.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc/>
        protected override string GetPersistString() => "AttentionPanel";
    }
}
