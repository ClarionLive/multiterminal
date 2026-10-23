using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Docking;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for choosing a team lead identity and (in selection mode) a working folder.
    /// Used in two scenarios:
    /// 1. Conflict: a project's team lead is already active — pick an alternative.
    /// 2. Selection: no team lead was assigned (e.g. "Just Claude") — pick identity + folder. The list
    /// opens on "Unassigned" (no team identity); a named agent is an explicit choice (ae3d0193).
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

        /// <summary>Default size of the Launch Claude window, in 96-DPI logical pixels.</summary>
        internal static readonly Size SelectionDefaultLogicalSize = new Size(640, 720);

        /// <summary>Smallest size the Launch Claude window can be dragged to, in 96-DPI logical pixels.</summary>
        internal static readonly Size SelectionMinimumLogicalSize = new Size(480, 440);

        /// <summary>
        /// The agent-name entries the Launch Claude picker shows (ticket ae3d0193): the "Unassigned"
        /// placeholder FIRST, then <paramref name="identities"/> in their given order. Any other spelling of
        /// the placeholder in <paramref name="identities"/> (identity discovery reads past sessions, so it can
        /// surface one) is dropped, so it appears exactly once. Picking it launches with no team identity:
        /// see <see cref="Services.JustClaudeLaunchIdentity"/>. Selection mode only: the conflict picker swaps
        /// a team lead for an alternative NAME, and the placeholder is not one.
        /// </summary>
        internal static List<string> BuildSelectionEntries(IEnumerable<string> identities)
        {
            var entries = new List<string> { TerminalRegistrationBinder.UnassignedPlaceholder };
            foreach (var name in identities ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (TerminalRegistrationBinder.IsUnassigned(name.Trim())) continue;
                entries.Add(name);
            }

            return entries;
        }

        /// <summary>Test seam: the entries the identity list holds, in display order.</summary>
        internal IReadOnlyList<string> IdentityEntries =>
            _identityList.Items.Cast<object>().Select(o => o.ToString()).ToList();

        /// <summary>Test seam: the identity list's selected index (-1 for none).</summary>
        internal int SelectedIdentityIndex => _identityList.SelectedIndex;

        /// <summary>Test seam: the identity list's selected entry as TryAccept reads it (null for none).</summary>
        internal string SelectedIdentityEntry => _identityList.SelectedItem?.ToString().Trim();

        /// <summary>Test seam: the identity list's background, to prove the theme reached it inside the layout.</summary>
        internal System.Drawing.Color IdentityListBackColor => _identityList.BackColor;

        private void BuildSelectionLayout(string defaultFolder, List<string> recentFolders)
        {
            // Resizable (ticket ae3d0193): a docked TableLayoutPanel whose two list rows are percent-sized,
            // so both lists grow with the window. Labels, the folder row and the buttons are AutoSize rows
            // and the fonts are in points, so the layout follows the DPI instead of fixed pixel offsets.
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Show;
            MinimumSize = LogicalToDeviceUnits(SelectionMinimumLogicalSize);
            Size = FitToWorkingArea(LogicalToDeviceUnits(SelectionDefaultLogicalSize));

            var recents = recentFolders?.Where(f => Directory.Exists(f)).ToList() ?? new List<string>();
            bool hasRecents = recents.Count > 0;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(LogicalToDeviceUnits(12)),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            int row = 0;
            void AddRow(RowStyle style)
            {
                layout.RowStyles.Add(style);
                row++;
            }

            // --- Agent Identity Section ---
            var identityLabel = new Label
            {
                Text = "Agent name:",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            layout.Controls.Add(identityLabel, 0, row);
            layout.SetColumnSpan(identityLabel, 2);
            AddRow(new RowStyle(SizeType.AutoSize));

            _identityList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12)),
                Font = new Font("Segoe UI", 10f)
            };

            foreach (var name in BuildSelectionEntries(_identities))
                _identityList.Items.Add(name);

            SelectFirstAvailable();
            layout.Controls.Add(_identityList, 0, row);
            layout.SetColumnSpan(_identityList, 2);
            AddRow(new RowStyle(SizeType.Percent, hasRecents ? 55f : 100f));

            // --- Working Folder Section ---
            var folderLabel = new Label
            {
                Text = "Working folder:",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            layout.Controls.Add(folderLabel, 0, row);
            layout.SetColumnSpan(folderLabel, 2);
            AddRow(new RowStyle(SizeType.AutoSize));

            _folderTextBox = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 0, LogicalToDeviceUnits(6), LogicalToDeviceUnits(10)),
                Font = new Font("Segoe UI", 9.5f),
                Text = defaultFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            layout.Controls.Add(_folderTextBox, 0, row);

            _browseButton = new Button
            {
                Text = "Browse...",
                AutoSize = true,
                MinimumSize = LogicalToDeviceUnits(new Size(82, 27)),
                Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(10))
            };
            _browseButton.Click += OnBrowseFolder;
            layout.Controls.Add(_browseButton, 1, row);
            AddRow(new RowStyle(SizeType.AutoSize));

            // --- Claude Profile Section (only when there is something to choose between) ---
            if (_profileNames.Count > 1)
            {
                // Rows of the same table as the sections around it (the layout became a resizable
                // TableLayoutPanel in ae3d0193, after this section was written with fixed positions).
                var profileLabel = new Label
                {
                    Text = "Claude profile:",
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                layout.Controls.Add(profileLabel, 0, row);
                layout.SetColumnSpan(profileLabel, 2);
                AddRow(new RowStyle(SizeType.AutoSize));

                _profileCombo = new ComboBox
                {
                    Anchor = AnchorStyles.Left,
                    Width = LogicalToDeviceUnits(260),
                    Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
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
                layout.Controls.Add(_profileCombo, 0, row);
                layout.SetColumnSpan(_profileCombo, 2);
                AddRow(new RowStyle(SizeType.AutoSize));

                _profileNote = new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    Height = LogicalToDeviceUnits(34),
                    Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8)),
                    Font = new Font("Segoe UI", 8.5f)
                };
                layout.Controls.Add(_profileNote, 0, row);
                layout.SetColumnSpan(_profileNote, 2);
                AddRow(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(42)));

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
            if (hasRecents)
            {
                var recentLabel = new Label
                {
                    Text = "Recent folders:",
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
                    Font = new Font("Segoe UI", 9f)
                };
                layout.Controls.Add(recentLabel, 0, row);
                layout.SetColumnSpan(recentLabel, 2);
                AddRow(new RowStyle(SizeType.AutoSize));

                _recentList = new ListBox
                {
                    Dock = DockStyle.Fill,
                    IntegralHeight = false,
                    Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12)),
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

                layout.Controls.Add(_recentList, 0, row);
                layout.SetColumnSpan(_recentList, 2);
                AddRow(new RowStyle(SizeType.Percent, 45f));
            }

            // --- Buttons ---
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right,
                WrapContents = false,
                Margin = Padding.Empty
            };

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = LogicalToDeviceUnits(new Size(80, 30)),
                Margin = new Padding(LogicalToDeviceUnits(6), 0, 0, 0),
                Font = new Font("Segoe UI", 9.5f)
            };

            _okButton = new Button
            {
                Text = "Launch",
                AutoSize = true,
                MinimumSize = LogicalToDeviceUnits(new Size(80, 30)),
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 9.5f)
            };
            _okButton.Click += (s, e) =>
            {
                if (TryAccept()) DialogResult = DialogResult.OK;
            };

            // RightToLeft flow: the first control added sits rightmost, so Cancel ends up right of Launch.
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(_okButton);
            layout.Controls.Add(buttons, 0, row);
            layout.SetColumnSpan(buttons, 2);
            AddRow(new RowStyle(SizeType.AutoSize));

            layout.RowCount = row;
            Controls.Add(layout);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        /// <summary>
        /// Caps <paramref name="size"/> at 90% of the working area of the screen the dialog opens on, so the
        /// default fits a 1080p display at 150% scaling with the taskbar showing.
        /// </summary>
        private static Size FitToWorkingArea(Size size)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            return new Size(
                Math.Min(size.Width, (int)(area.Width * 0.9)),
                Math.Min(size.Height, (int)(area.Height * 0.9)));
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

            ApplyThemeTo(Controls, theme);
        }

        // Recursive since ae3d0193: the Launch Claude layout nests its controls in layout panels, which
        // take the form's colours as ambient properties but would otherwise hide their children here.
        private static void ApplyThemeTo(Control.ControlCollection controls, TerminalTheme theme)
        {
            foreach (Control ctrl in controls)
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
                else if (ctrl.HasChildren)
                {
                    ApplyThemeTo(ctrl.Controls, theme);
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
