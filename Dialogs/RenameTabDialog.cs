using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Simple dialog for renaming terminal tabs.
    /// </summary>
    public class RenameTabDialog : Form
    {
        private TextBox _textBox;
        private Button _okButton;
        private Button _cancelButton;

        /// <summary>
        /// Gets the new name entered by the user.
        /// </summary>
        public string NewName => _textBox.Text.Trim();

        public RenameTabDialog(string currentName)
        {
            Text = "Rename Tab";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(350, 130);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var label = new Label
            {
                Text = "Enter new tab name:",
                Location = new Point(12, 15),
                AutoSize = true
            };
            Controls.Add(label);

            _textBox = new TextBox
            {
                Text = currentName,
                Location = new Point(12, 35),
                Size = new Size(310, 23),
                SelectionStart = 0,
                SelectionLength = currentName?.Length ?? 0
            };
            Controls.Add(_textBox);

            _okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(166, 65),
                Size = new Size(75, 23)
            };
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(247, 65),
                Size = new Size(75, 23)
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _textBox.Focus();
            _textBox.SelectAll();
        }
    }
}
