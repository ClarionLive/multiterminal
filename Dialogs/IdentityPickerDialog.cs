using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for choosing a team lead identity and (in selection mode) a working folder.
    /// Used in two scenarios:
    /// 1. Conflict: a project's team lead is already active — pick an alternative.
    /// 2. Selection: no team lead was assigned (e.g. "Just Claude") — pick identity + folder.
    /// </summary>
    public class IdentityPickerDialog : Form
    {
        private ListBox _identityList;
        private TextBox _folderTextBox;
        private Button _browseButton;
        private ListBox _recentList;
        private Button _okButton;
        private Button _cancelButton;
        private readonly string _conflictingName;
        private readonly List<string> _identities;
        private readonly bool _isSelectionMode;
        private TerminalTheme _theme;

        /// <summary>
        /// Gets the identity selected by the user.
        /// </summary>
        public string SelectedIdentity { get; private set; }

        /// <summary>
        /// Gets the working folder selected by the user (selection mode only).
        /// </summary>
        public string SelectedFolder { get; private set; }

        /// <summary>
        /// Conflict mode: team lead identity is already in use, pick an alternative.
        /// </summary>
        public IdentityPickerDialog(string conflictingName, List<string> availableIdentities, TerminalTheme theme)
            : this(conflictingName, availableIdentities, theme, isSelectionMode: false) { }

        /// <summary>
        /// Creates the identity picker dialog.
        /// </summary>
        public IdentityPickerDialog(string conflictingName, List<string> availableIdentities, TerminalTheme theme, bool isSelectionMode)
            : this(conflictingName, availableIdentities, theme, isSelectionMode, null, null) { }

        /// <summary>
        /// Creates the identity picker dialog with folder selection support.
        /// </summary>
        public IdentityPickerDialog(
            string conflictingName,
            List<string> availableIdentities,
            TerminalTheme theme,
            bool isSelectionMode,
            string defaultFolder,
            List<string> recentFolders)
        {
            _conflictingName = conflictingName;
            _identities = availableIdentities ?? new List<string>();
            _isSelectionMode = isSelectionMode;
            _theme = theme;

            Text = isSelectionMode ? "Launch Claude" : "Identity Conflict";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            if (isSelectionMode)
                BuildSelectionLayout(defaultFolder, recentFolders);
            else
                BuildConflictLayout();

            ApplyTheme(theme);
        }

        private void BuildConflictLayout()
        {
            Size = new Size(500, 450);

            var label = new Label
            {
                Text = $"\"{_conflictingName}\" is already active in another terminal.\nChoose an identity for this terminal:",
                Location = new Point(16, 16),
                Size = new Size(452, 36),
                AutoSize = false
            };
            Controls.Add(label);

            _identityList = new ListBox
            {
                Location = new Point(16, 56),
                Size = new Size(452, 300),
                IntegralHeight = false,
                Font = new Font("Segoe UI", 10f)
            };

            foreach (var name in _identities)
            {
                if (name.Equals(_conflictingName, StringComparison.OrdinalIgnoreCase))
                    _identityList.Items.Add($"{name}  (active)");
                else
                    _identityList.Items.Add(name);
            }

            SelectFirstAvailable();
            _identityList.DoubleClick += (s, e) =>
            {
                if (TryAccept()) DialogResult = DialogResult.OK;
            };
            Controls.Add(_identityList);

            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(312, 368),
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
                Location = new Point(393, 368),
                Size = new Size(75, 28)
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void BuildSelectionLayout(string defaultFolder, List<string> recentFolders)
        {
            Size = new Size(560, 520);
            int y = 16;

            // --- Agent Identity Section ---
            var identityLabel = new Label
            {
                Text = "Agent name:",
                Location = new Point(16, y),
                Size = new Size(512, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(identityLabel);
            y += 24;

            _identityList = new ListBox
            {
                Location = new Point(16, y),
                Size = new Size(512, 120),
                IntegralHeight = false,
                Font = new Font("Segoe UI", 10f)
            };

            foreach (var name in _identities)
                _identityList.Items.Add(name);

            SelectFirstAvailable();
            Controls.Add(_identityList);
            y += 130;

            // --- Working Folder Section ---
            var folderLabel = new Label
            {
                Text = "Working folder:",
                Location = new Point(16, y),
                Size = new Size(512, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(folderLabel);
            y += 24;

            _folderTextBox = new TextBox
            {
                Location = new Point(16, y),
                Size = new Size(424, 26),
                Font = new Font("Segoe UI", 9.5f),
                Text = defaultFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            Controls.Add(_folderTextBox);

            _browseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(446, y - 1),
                Size = new Size(82, 27)
            };
            _browseButton.Click += OnBrowseFolder;
            Controls.Add(_browseButton);
            y += 34;

            // --- Recent Folders Section ---
            var recents = recentFolders?.Where(f => Directory.Exists(f)).ToList() ?? new List<string>();
            if (recents.Count > 0)
            {
                var recentLabel = new Label
                {
                    Text = "Recent folders:",
                    Location = new Point(16, y),
                    Size = new Size(512, 20),
                    Font = new Font("Segoe UI", 9f)
                };
                Controls.Add(recentLabel);
                y += 22;

                _recentList = new ListBox
                {
                    Location = new Point(16, y),
                    Size = new Size(512, 140),
                    IntegralHeight = false,
                    Font = new Font("Segoe UI", 9f)
                };

                foreach (var folder in recents)
                    _recentList.Items.Add(folder);

                _recentList.SelectedIndexChanged += (s, e) =>
                {
                    if (_recentList.SelectedItem != null)
                        _folderTextBox.Text = _recentList.SelectedItem.ToString();
                };
                _recentList.DoubleClick += (s, e) =>
                {
                    if (_recentList.SelectedItem != null)
                    {
                        _folderTextBox.Text = _recentList.SelectedItem.ToString();
                        if (TryAccept()) DialogResult = DialogResult.OK;
                    }
                };

                Controls.Add(_recentList);
                y += 148;
            }

            // --- Buttons ---
            _okButton = new Button
            {
                Text = "Launch",
                Location = new Point(370, y),
                Size = new Size(80, 30),
                Font = new Font("Segoe UI", 9.5f)
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
                Location = new Point(456, y),
                Size = new Size(72, 30)
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            // Adjust form height to fit content
            Size = new Size(560, y + 76);
        }

        private void OnBrowseFolder(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select working folder for Claude",
                ShowNewFolderButton = true
            };

            // Set initial directory from current text box value
            if (!string.IsNullOrEmpty(_folderTextBox.Text) && Directory.Exists(_folderTextBox.Text))
                dialog.SelectedPath = _folderTextBox.Text;

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _folderTextBox.Text = dialog.SelectedPath;
                // Clear recent list selection since user browsed
                if (_recentList != null)
                    _recentList.ClearSelected();
            }
        }

        private void SelectFirstAvailable()
        {
            for (int i = 0; i < _identityList.Items.Count; i++)
            {
                string item = _identityList.Items[i].ToString();
                if (!item.Contains("(active)"))
                {
                    _identityList.SelectedIndex = i;
                    break;
                }
            }
        }

        private bool TryAccept()
        {
            if (_identityList.SelectedItem == null)
            {
                MessageBox.Show(this, "Please select an agent name.", "Selection Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string selected = _identityList.SelectedItem.ToString();

            // Don't allow selecting the conflicting name (conflict mode)
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

            // Validate folder in selection mode
            if (_isSelectionMode && _folderTextBox != null)
            {
                string folder = _folderTextBox.Text.Trim();
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    MessageBox.Show(this, "Please select a valid working folder.", "Invalid Folder",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                SelectedFolder = folder;
            }

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
                else if (ctrl is TextBox tb)
                {
                    tb.BackColor = theme.Background;
                    tb.ForeColor = theme.Foreground;
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
            _identityList.Focus();
        }
    }
}
