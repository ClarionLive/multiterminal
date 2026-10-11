using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;
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
        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private ListBox _identityList;
        private TextBox _folderTextBox;
        private Button _browseButton;
        private ListBox _recentList;
        private Button _okButton;
        private Button _cancelButton;
        private ComboBox _profileCombo;
        private Label _profileNote;
#pragma warning restore CA2213

        // Claude profile choice (ticket c8ee0eec item 8). Shown only in selection mode and only when
        // more than one profile exists. _describeProfileChoice says, for the folder in the box, whether
        // a profile may be picked at all (a folder a project owns always uses that project's profile).
        private readonly IReadOnlyList<string> _profileNames;
        private readonly Func<string, ClaudeProfileLaunchChoice> _describeProfileChoice;
        private readonly Func<string, string> _profileHint;
        private Timer _profileRefreshTimer;
        private string _pickedProfileName = ClaudeProfile.DefaultName;
        private bool _profileChoiceOpen = true;
        private bool _settingProfileSelection;
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
        /// The Claude profile the user picked for this launch ("Default" included), or null when there
        /// was nothing to pick: only one profile exists, or the selected folder belongs to a project
        /// (which always uses that project's profile). Selection mode only.
        /// </summary>
        public string SelectedClaudeProfileName { get; private set; }

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
            : this(conflictingName, availableIdentities, theme, isSelectionMode, defaultFolder, recentFolders, null, null, null)
        {
        }

        /// <summary>
        /// Selection mode with a "Claude profile" choice (ticket c8ee0eec item 8).
        /// </summary>
        /// <param name="claudeProfileNames">Every profile name, Default first. The choice appears only
        /// when there is more than one.</param>
        /// <param name="describeProfileChoice">Folder → whether a profile may be picked for it
        /// (<see cref="ClaudeProfileLaunchPolicy.DescribeChoice"/>).</param>
        /// <param name="profileHint">Profile name → a one-line note to show under the choice, or null.</param>
        internal IdentityPickerDialog(
            string conflictingName,
            List<string> availableIdentities,
            TerminalTheme theme,
            bool isSelectionMode,
            string defaultFolder,
            List<string> recentFolders,
            IReadOnlyList<string> claudeProfileNames,
            Func<string, ClaudeProfileLaunchChoice> describeProfileChoice,
            Func<string, string> profileHint)
        {
            _profileNames = claudeProfileNames ?? Array.Empty<string>();
            _describeProfileChoice = describeProfileChoice;
            _profileHint = profileHint;
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

            // --- Claude Profile Section (only when there is something to choose between) ---
            if (_profileNames.Count > 1)
            {
                var profileLabel = new Label
                {
                    Text = "Claude profile:",
                    Location = new Point(16, y),
                    Size = new Size(512, 20),
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                Controls.Add(profileLabel);
                y += 24;

                _profileCombo = new ComboBox
                {
                    Location = new Point(16, y),
                    Size = new Size(260, 26),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = new Font("Segoe UI", 9.5f)
                };
                foreach (var profileName in _profileNames)
                    _profileCombo.Items.Add(profileName);
                _profileCombo.SelectedIndex = 0;
                _profileCombo.SelectedIndexChanged += (s, e) =>
                {
                    if (_settingProfileSelection || !_profileChoiceOpen || _profileCombo.SelectedItem == null)
                        return;
                    _pickedProfileName = _profileCombo.SelectedItem.ToString();
                    ShowProfileNote(_profileHint?.Invoke(_pickedProfileName));
                };
                Controls.Add(_profileCombo);
                y += 30;

                _profileNote = new Label
                {
                    Location = new Point(16, y),
                    Size = new Size(512, 34),
                    AutoSize = false,
                    Font = new Font("Segoe UI", 8.5f)
                };
                Controls.Add(_profileNote);
                y += 38;

                // The folder decides whether there is a choice at all. Typing is debounced: the answer
                // reads the project database and the folder's project.json.
                _profileRefreshTimer = new Timer { Interval = 300 };
                _profileRefreshTimer.Tick += (s, e) =>
                {
                    _profileRefreshTimer.Stop();
                    RefreshProfileChoice();
                };
                _folderTextBox.TextChanged += (s, e) =>
                {
                    _profileRefreshTimer.Stop();
                    _profileRefreshTimer.Start();
                };
                RefreshProfileChoice();
            }

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

        /// <summary>
        /// Brings the profile choice in line with the folder in the box: open when no project owns the
        /// folder (the user's last pick is restored), else closed on the owning project's profile with
        /// the reason shown. No-op when the choice is not shown.
        /// </summary>
        private void RefreshProfileChoice()
        {
            if (_profileCombo == null)
                return;

            var choice = _describeProfileChoice?.Invoke(_folderTextBox.Text.Trim())
                ?? new ClaudeProfileLaunchChoice(true, null, null);
            _profileChoiceOpen = choice.CanChoose;

            string show = choice.CanChoose ? _pickedProfileName : choice.FixedProfileName;
            _settingProfileSelection = true;
            try
            {
                _profileCombo.SelectedIndex = show == null ? -1 : _profileCombo.FindStringExact(show);
                _profileCombo.Enabled = choice.CanChoose;
            }
            finally
            {
                _settingProfileSelection = false;
            }

            ShowProfileNote(choice.CanChoose ? _profileHint?.Invoke(_pickedProfileName) : choice.Reason);
        }

        private void ShowProfileNote(string text)
        {
            if (_profileNote != null)
                _profileNote.Text = text ?? "";
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

                // Decided again for the folder being accepted (the debounce may not have run yet), so a
                // pick is never returned for a folder that a project owns.
                if (_profileCombo != null)
                {
                    _profileRefreshTimer?.Stop();
                    RefreshProfileChoice();
                    SelectedClaudeProfileName = _profileChoiceOpen ? _pickedProfileName : null;
                }
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
                else if (ctrl is ComboBox cb)
                {
                    cb.BackColor = theme.Background;
                    cb.ForeColor = theme.Foreground;
                    cb.FlatStyle = FlatStyle.Flat;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _profileRefreshTimer?.Dispose();
                _profileRefreshTimer = null;
            }

            base.Dispose(disposing);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _identityList.Focus();
        }
    }
}
