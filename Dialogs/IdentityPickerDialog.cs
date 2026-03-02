using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog shown when a user tries to open a project whose team lead
    /// identity is already active in another terminal. Lets them pick
    /// an alternative identity or cancel.
    /// </summary>
    public class IdentityPickerDialog : Form
    {
        private ListBox _listBox;
        private Button _okButton;
        private Button _cancelButton;
        private readonly string _conflictingName;
        private readonly List<string> _identities;

        /// <summary>
        /// Gets the identity selected by the user.
        /// </summary>
        public string SelectedIdentity { get; private set; }

        public IdentityPickerDialog(string conflictingName, List<string> availableIdentities, TerminalTheme theme)
        {
            _conflictingName = conflictingName;
            _identities = availableIdentities ?? new List<string>();

            Text = "Identity Conflict";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(360, 300);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var label = new Label
            {
                Text = $"\"{conflictingName}\" is already active in another terminal.\nChoose an identity for this terminal:",
                Location = new Point(12, 12),
                Size = new Size(320, 36),
                AutoSize = false
            };
            Controls.Add(label);

            _listBox = new ListBox
            {
                Location = new Point(12, 52),
                Size = new Size(320, 160),
                IntegralHeight = false
            };

            // Add identities — mark the conflicting one
            foreach (var name in _identities)
            {
                if (name.Equals(conflictingName, StringComparison.OrdinalIgnoreCase))
                    _listBox.Items.Add($"{name}  (active)");
                else
                    _listBox.Items.Add(name);
            }

            // Pre-select first non-conflicting identity
            for (int i = 0; i < _listBox.Items.Count; i++)
            {
                string item = _listBox.Items[i].ToString();
                if (!item.Contains("(active)"))
                {
                    _listBox.SelectedIndex = i;
                    break;
                }
            }

            _listBox.DoubleClick += (s, e) =>
            {
                if (TryAccept()) DialogResult = DialogResult.OK;
            };
            Controls.Add(_listBox);

            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(176, 222),
                Size = new Size(75, 28)
            };
            _okButton.Click += (s, e) =>
            {
                if (TryAccept()) DialogResult = DialogResult.OK;
            };
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(257, 222),
                Size = new Size(75, 28)
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            ApplyTheme(theme);
        }

        private bool TryAccept()
        {
            if (_listBox.SelectedItem == null) return false;

            string selected = _listBox.SelectedItem.ToString();

            // Don't allow selecting the conflicting name
            if (selected.Contains("(active)"))
            {
                MessageBox.Show(this,
                    $"\"{_conflictingName}\" is already in use. Please choose a different identity.",
                    "Identity In Use",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            SelectedIdentity = selected.Trim();
            return true;
        }

        private void ApplyTheme(TerminalTheme theme)
        {
            if (theme == null) return;

            BackColor = theme.ToolbarBackground;
            ForeColor = theme.ToolbarForeground;

            foreach (Control ctrl in Controls)
            {
                if (ctrl is Label lbl)
                {
                    lbl.ForeColor = theme.ToolbarForeground;
                }
                else if (ctrl is ListBox lb)
                {
                    lb.BackColor = theme.Background;
                    lb.ForeColor = theme.Foreground;
                }
                else if (ctrl is Button btn)
                {
                    btn.BackColor = theme.TabInactiveBackground;
                    btn.ForeColor = theme.ToolbarForeground;
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = theme.StatusForeground;
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _listBox.Focus();
        }
    }
}
