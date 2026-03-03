using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Embedded agent panel that lives inside each terminal's layout (right side).
    /// Shows "No Agents" when empty, splits vertically for multiple agents.
    /// </summary>
    public class EmbeddedAgentPanel : Panel
    {
        private readonly Label _noAgentsLabel;
        private readonly List<Panel> _agentSlots = new List<Panel>();
        private readonly TableLayoutPanel _agentLayout;
        private bool _isDarkTheme = true;

        // Theme colors (matched to app-wide palette)
        private static readonly Color DarkBackground = Color.FromArgb(30, 30, 30);
        private static readonly Color LightBackground = Color.FromArgb(240, 240, 240);
        private static readonly Color DarkMutedText = Color.FromArgb(100, 100, 100);
        private static readonly Color LightMutedText = Color.FromArgb(150, 150, 150);
        private static readonly Color DarkBorderColor = Color.FromArgb(50, 50, 50);
        private static readonly Color LightBorderColor = Color.FromArgb(200, 200, 200);

        /// <summary>
        /// Fired when agent slots change between empty and non-empty.
        /// True = has agents (show panel), False = no agents (hide panel).
        /// </summary>
        public event EventHandler<bool> VisibilityRequested;

        public EmbeddedAgentPanel()
        {
            SuspendLayout();

            Dock = DockStyle.Fill;
            Padding = new Padding(0);

            // "No Agents" placeholder label
            _noAgentsLabel = new Label
            {
                Text = "No Agents",
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                AutoSize = false
            };

            // Layout container for multiple agent slots
            _agentLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Visible = false
            };

            Controls.Add(_noAgentsLabel);
            Controls.Add(_agentLayout);

            // Apply initial theme colors
            ApplyTheme(_isDarkTheme);

            ResumeLayout(false);
        }

        /// <summary>
        /// Adds an agent slot panel. Returns the panel so the caller can host content in it.
        /// When agents are added, the layout splits evenly vertically.
        /// </summary>
        public Panel AddAgentSlot(string agentName = null)
        {
            var slot = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _isDarkTheme ? DarkBackground : LightBackground,
                Margin = new Padding(0, _agentSlots.Count > 0 ? 1 : 0, 0, 0),
                Padding = new Padding(4)
            };

            if (!string.IsNullOrEmpty(agentName))
            {
                slot.Tag = agentName;
            }

            _agentSlots.Add(slot);
            RebuildLayout();
            return slot;
        }

        /// <summary>
        /// Removes an agent slot by reference.
        /// </summary>
        public void RemoveAgentSlot(Panel slot)
        {
            if (_agentSlots.Remove(slot))
            {
                slot.Dispose();
                RebuildLayout();
            }
        }

        /// <summary>
        /// Removes all agent slots and returns to "No Agents" state.
        /// </summary>
        public void ClearAgentSlots()
        {
            foreach (var slot in _agentSlots)
            {
                slot.Dispose();
            }
            _agentSlots.Clear();
            RebuildLayout();
        }

        /// <summary>
        /// Gets the current number of agent slots.
        /// </summary>
        public int AgentSlotCount => _agentSlots.Count;

        /// <summary>
        /// Applies the current theme.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? DarkBackground : LightBackground;
            _noAgentsLabel.ForeColor = isDark ? DarkMutedText : LightMutedText;

            foreach (var slot in _agentSlots)
            {
                slot.BackColor = isDark ? DarkBackground : LightBackground;
            }
        }

        /// <summary>
        /// Rebuilds the TableLayoutPanel rows to evenly divide among agent slots.
        /// Shows "No Agents" label when empty, or hides it and shows layout when agents exist.
        /// </summary>
        private void RebuildLayout()
        {
            _agentLayout.SuspendLayout();
            _agentLayout.Controls.Clear();
            _agentLayout.RowStyles.Clear();

            if (_agentSlots.Count == 0)
            {
                _agentLayout.Visible = false;
                _noAgentsLabel.Visible = true;
                _agentLayout.ResumeLayout(false);
                VisibilityRequested?.Invoke(this, false);
                return;
            }

            _noAgentsLabel.Visible = false;
            _agentLayout.Visible = true;
            VisibilityRequested?.Invoke(this, true);

            _agentLayout.RowCount = _agentSlots.Count;
            float rowPercent = 100f / _agentSlots.Count;

            for (int i = 0; i < _agentSlots.Count; i++)
            {
                _agentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, rowPercent));
                _agentLayout.Controls.Add(_agentSlots[i], 0, i);
            }

            _agentLayout.ResumeLayout(true);
        }

        /// <summary>
        /// Custom paint to draw a subtle left border separating agent panel from terminal.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Draw left border line
            var borderColor = _isDarkTheme ? DarkBorderColor : LightBorderColor;
            using (var pen = new Pen(borderColor, 1f))
            {
                e.Graphics.DrawLine(pen, 0, 0, 0, Height);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearAgentSlots();
                _noAgentsLabel?.Dispose();
                _agentLayout?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
