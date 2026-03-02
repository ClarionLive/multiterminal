using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Settings dialog for configuring MultiTerminal options.
    /// </summary>
    public class SettingsDialog : Form
    {
        private readonly SettingsService _settings;
        private readonly TerminalTheme _theme;

        // Font size controls
        private ComboBox _toolbarFontCombo;
        private ComboBox _terminalFontCombo;
        private ComboBox _projectPanelFontCombo;
        private ComboBox _chatPanelFontCombo;
        private ComboBox _tasksPanelFontCombo;
        private ComboBox _activityPanelFontCombo;

        // Terminal placement controls
        private ComboBox _maxGridPanesCombo;
        private ComboBox _maxTabsPerGridCombo;

        // Agent panel controls
        private RadioButton _splitRightRadio;
        private RadioButton _splitBelowRadio;
        private RadioButton _tabbedRightRadio;
        private RadioButton _doNotShowRadio;
        private RadioButton _autoCloseRadio;
        private RadioButton _manualCloseRadio;

        // Claude commands controls
        private DataGridView _commandsGrid;
        private Button _addButton;
        private Button _editButton;
        private Button _deleteButton;
        private Button _setDefaultButton;

        // Dialog buttons
        private Button _okButton;
        private Button _cancelButton;

        // Data
        private List<ClaudeCommand> _commands;

        public SettingsDialog(SettingsService settings, TerminalTheme theme)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _theme = theme ?? TerminalTheme.Dark;
            _commands = new List<ClaudeCommand>();

            InitializeComponent();
            ApplyTheme();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            Text = "Settings";
            Size = new Size(500, 750);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 10f);

            int yPos = 15;
            int leftMargin = 20;
            int rightMargin = 20;
            int groupWidth = ClientSize.Width - leftMargin - rightMargin;

            // Font Sizes Group
            var fontGroup = new GroupBox
            {
                Text = "Font Sizes",
                Location = new Point(leftMargin, yPos),
                Size = new Size(groupWidth, 210)
            };

            var toolbarLabel = new Label { Text = "Toolbar:", Location = new Point(15, 28), AutoSize = true };
            _toolbarFontCombo = CreateFontComboBox(new Point(140, 25), GetUIFontSizes());

            var terminalLabel = new Label { Text = "Terminal:", Location = new Point(15, 58), AutoSize = true };
            _terminalFontCombo = CreateFontComboBox(new Point(140, 55), GetTerminalFontSizes());

            var projectLabel = new Label { Text = "Project Panel:", Location = new Point(15, 88), AutoSize = true };
            _projectPanelFontCombo = CreateFontComboBox(new Point(140, 85), GetUIFontSizes());

            var chatLabel = new Label { Text = "Chat Panel:", Location = new Point(15, 118), AutoSize = true };
            _chatPanelFontCombo = CreateFontComboBox(new Point(140, 115), GetUIFontSizes());

            var tasksLabel = new Label { Text = "Tasks Panel:", Location = new Point(15, 148), AutoSize = true };
            _tasksPanelFontCombo = CreateFontComboBox(new Point(140, 145), GetUIFontSizes());

            var activityLabel = new Label { Text = "Activity Panel:", Location = new Point(15, 178), AutoSize = true };
            _activityPanelFontCombo = CreateFontComboBox(new Point(140, 175), GetUIFontSizes());

            fontGroup.Controls.AddRange(new Control[] {
                toolbarLabel, _toolbarFontCombo,
                terminalLabel, _terminalFontCombo,
                projectLabel, _projectPanelFontCombo,
                chatLabel, _chatPanelFontCombo,
                tasksLabel, _tasksPanelFontCombo,
                activityLabel, _activityPanelFontCombo
            });

            yPos += fontGroup.Height + 15;

            // Terminal Behavior Group
            var terminalGroup = new GroupBox
            {
                Text = "Terminal Placement",
                Location = new Point(leftMargin, yPos),
                Size = new Size(groupWidth, 90)
            };

            var maxGridsLabel = new Label { Text = "Max Grids:", Location = new Point(15, 28), AutoSize = true };
            _maxGridPanesCombo = new ComboBox
            {
                Location = new Point(160, 25),
                Size = new Size(60, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            for (int i = 1; i <= 9; i++) _maxGridPanesCombo.Items.Add(i.ToString());

            var maxTabsLabel = new Label { Text = "Max Tabs per Grid:", Location = new Point(15, 58), AutoSize = true };
            _maxTabsPerGridCombo = new ComboBox
            {
                Location = new Point(160, 55),
                Size = new Size(60, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            for (int i = 1; i <= 10; i++) _maxTabsPerGridCombo.Items.Add(i.ToString());

            terminalGroup.Controls.AddRange(new Control[] { maxGridsLabel, _maxGridPanesCombo, maxTabsLabel, _maxTabsPerGridCombo });

            yPos += terminalGroup.Height + 15;

            // Agent Panel Group
            var agentGroup = new GroupBox
            {
                Text = "Agent Panel",
                Location = new Point(leftMargin, yPos),
                Size = new Size(groupWidth, 100)
            };

            // Layout radio group (Panel isolates these from On Complete radios)
            var layoutPanel = new Panel
            {
                Location = new Point(0, 18),
                Size = new Size(groupWidth, 55),
                BackColor = Color.Transparent
            };
            var layoutLabel = new Label { Text = "Layout:", Location = new Point(15, 7), AutoSize = true };
            _splitRightRadio = new RadioButton { Text = "Split Right", Location = new Point(100, 5), AutoSize = true };
            _splitBelowRadio = new RadioButton { Text = "Split Below", Location = new Point(210, 5), AutoSize = true };
            _tabbedRightRadio = new RadioButton { Text = "Tabbed Right", Location = new Point(320, 5), AutoSize = true };
            _doNotShowRadio = new RadioButton { Text = "Do Not Show", Location = new Point(100, 30), AutoSize = true };
            layoutPanel.Controls.AddRange(new Control[] { layoutLabel, _splitRightRadio, _splitBelowRadio, _tabbedRightRadio, _doNotShowRadio });

            // On Complete radio group (separate Panel = separate radio group)
            var closePanel = new Panel
            {
                Location = new Point(0, 73),
                Size = new Size(groupWidth, 25),
                BackColor = Color.Transparent
            };
            var closeLabel = new Label { Text = "On Complete:", Location = new Point(15, 2), AutoSize = true };
            _autoCloseRadio = new RadioButton { Text = "Auto-close", Location = new Point(130, 0), AutoSize = true };
            _manualCloseRadio = new RadioButton { Text = "Keep open", Location = new Point(250, 0), AutoSize = true };
            closePanel.Controls.AddRange(new Control[] { closeLabel, _autoCloseRadio, _manualCloseRadio });

            agentGroup.Controls.AddRange(new Control[] { layoutPanel, closePanel });

            yPos += agentGroup.Height + 15;

            // Claude Commands Group
            var claudeGroup = new GroupBox
            {
                Text = "Claude Commands",
                Location = new Point(leftMargin, yPos),
                Size = new Size(groupWidth, 200)
            };

            _commandsGrid = new DataGridView
            {
                Location = new Point(15, 25),
                Size = new Size(groupWidth - 30, 120),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            _commandsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Command",
                HeaderText = "Command",
                FillWeight = 80
            });
            _commandsGrid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "IsDefault",
                HeaderText = "Default",
                FillWeight = 20,
                ReadOnly = true
            });

            int buttonY = 155;
            int buttonWidth = 80;
            int buttonSpacing = 10;

            _addButton = new Button { Text = "Add", Location = new Point(15, buttonY), Size = new Size(buttonWidth, 28) };
            _editButton = new Button { Text = "Edit", Location = new Point(15 + buttonWidth + buttonSpacing, buttonY), Size = new Size(buttonWidth, 28) };
            _deleteButton = new Button { Text = "Delete", Location = new Point(15 + (buttonWidth + buttonSpacing) * 2, buttonY), Size = new Size(buttonWidth, 28) };
            _setDefaultButton = new Button { Text = "Set Default", Location = new Point(15 + (buttonWidth + buttonSpacing) * 3, buttonY), Size = new Size(100, 28) };

            _addButton.Click += OnAddCommand;
            _editButton.Click += OnEditCommand;
            _deleteButton.Click += OnDeleteCommand;
            _setDefaultButton.Click += OnSetDefault;

            claudeGroup.Controls.AddRange(new Control[] {
                _commandsGrid,
                _addButton, _editButton, _deleteButton, _setDefaultButton
            });

            yPos += claudeGroup.Height + 20;

            // Dialog buttons
            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(ClientSize.Width - 180, yPos),
                Size = new Size(75, 30),
                DialogResult = DialogResult.OK
            };
            _okButton.Click += OnOkClick;

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(ClientSize.Width - 95, yPos),
                Size = new Size(75, 30),
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[] { fontGroup, terminalGroup, agentGroup, claudeGroup, _okButton, _cancelButton });
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private ComboBox CreateFontComboBox(Point location, float[] sizes)
        {
            var combo = new ComboBox
            {
                Location = location,
                Size = new Size(80, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            foreach (var size in sizes)
            {
                combo.Items.Add($"{size}pt");
            }

            return combo;
        }

        private float[] GetUIFontSizes()
        {
            return new float[] { 8, 9, 10, 11, 12, 14 };
        }

        private float[] GetTerminalFontSizes()
        {
            return new float[] { 8, 10, 12, 14, 16, 18, 20, 24 };
        }

        private void ApplyTheme()
        {
            bool isDark = _theme.IsDark;

            BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
            ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

            foreach (Control control in Controls)
            {
                ApplyThemeToControl(control, isDark);
            }

            // Style the grid
            if (_commandsGrid != null)
            {
                _commandsGrid.BackgroundColor = isDark ? Color.FromArgb(30, 30, 30) : Color.White;
                _commandsGrid.DefaultCellStyle.BackColor = isDark ? Color.FromArgb(37, 37, 38) : Color.White;
                _commandsGrid.DefaultCellStyle.ForeColor = isDark ? Color.White : Color.Black;
                _commandsGrid.DefaultCellStyle.SelectionBackColor = isDark ? Color.FromArgb(0, 122, 204) : Color.FromArgb(51, 153, 255);
                _commandsGrid.DefaultCellStyle.SelectionForeColor = Color.White;
                _commandsGrid.ColumnHeadersDefaultCellStyle.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 230, 230);
                _commandsGrid.ColumnHeadersDefaultCellStyle.ForeColor = isDark ? Color.White : Color.Black;
                _commandsGrid.EnableHeadersVisualStyles = false;
                _commandsGrid.GridColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 200, 200);
                _commandsGrid.BorderStyle = BorderStyle.FixedSingle;
            }
        }

        private void ApplyThemeToControl(Control control, bool isDark)
        {
            if (control is GroupBox groupBox)
            {
                groupBox.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            }
            else if (control is Button button)
            {
                button.BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225);
                button.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);
            }
            else if (control is ComboBox combo)
            {
                combo.BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.White;
                combo.ForeColor = isDark ? Color.White : Color.Black;
            }
            else if (control is RadioButton radio)
            {
                radio.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            }
            else if (control is Label label)
            {
                label.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            }

            foreach (Control child in control.Controls)
            {
                ApplyThemeToControl(child, isDark);
            }
        }

        private void LoadSettings()
        {
            // Load font sizes
            SelectFontSize(_toolbarFontCombo, _settings.GetToolbarFontSize());
            SelectFontSize(_terminalFontCombo, _settings.GetTerminalFontSize());
            SelectFontSize(_projectPanelFontCombo, _settings.GetPromptsFontSize());
            SelectFontSize(_chatPanelFontCombo, _settings.GetChatFontSize());
            SelectFontSize(_tasksPanelFontCombo, _settings.GetTasksFontSize());
            SelectFontSize(_activityPanelFontCombo, _settings.GetActivityFontSize());

            // Load terminal placement settings
            _maxGridPanesCombo.SelectedIndex = _settings.GetMaxGridPanes() - 1;
            _maxTabsPerGridCombo.SelectedIndex = _settings.GetMaxTabsPerGrid() - 1;

            // Load agent panel settings
            string agentLayout = _settings.GetAgentPanelLayout();
            _splitRightRadio.Checked = agentLayout == "SplitRight";
            _splitBelowRadio.Checked = agentLayout == "SplitBelow";
            _tabbedRightRadio.Checked = agentLayout == "TabbedRight";
            _doNotShowRadio.Checked = agentLayout == "DoNotShow";

            string closeMode = _settings.GetAgentPanelCloseMode();
            _autoCloseRadio.Checked = closeMode == "AutoClose";
            _manualCloseRadio.Checked = closeMode == "ManualClose";

            // Load Claude commands
            _commands = _settings.GetClaudeCommands();
            RefreshCommandsGrid();
        }

        private void SelectFontSize(ComboBox combo, float size)
        {
            string target = $"{size}pt";
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i].ToString() == target)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            // If exact match not found, find closest
            string closestMatch = $"{(int)size}pt";
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i].ToString() == closestMatch)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            // Default to first item
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private float GetSelectedFontSize(ComboBox combo)
        {
            string selected = combo.SelectedItem?.ToString() ?? "10pt";
            selected = selected.Replace("pt", "");
            if (float.TryParse(selected, out float size))
                return size;
            return 10f;
        }

        private void RefreshCommandsGrid()
        {
            _commandsGrid.Rows.Clear();
            foreach (var cmd in _commands)
            {
                int idx = _commandsGrid.Rows.Add(cmd.Command, cmd.IsDefault);
                _commandsGrid.Rows[idx].Tag = cmd;
            }
        }

        private void OnAddCommand(object sender, EventArgs e)
        {
            string command = ShowCommandInputDialog("Add Command", "");
            if (!string.IsNullOrWhiteSpace(command))
            {
                _commands.Add(new ClaudeCommand(command, false));
                RefreshCommandsGrid();
            }
        }

        private void OnEditCommand(object sender, EventArgs e)
        {
            if (_commandsGrid.SelectedRows.Count == 0) return;

            var row = _commandsGrid.SelectedRows[0];
            var cmd = row.Tag as ClaudeCommand;
            if (cmd == null) return;

            string newCommand = ShowCommandInputDialog("Edit Command", cmd.Command);
            if (!string.IsNullOrWhiteSpace(newCommand))
            {
                cmd.Command = newCommand;
                RefreshCommandsGrid();
            }
        }

        private void OnDeleteCommand(object sender, EventArgs e)
        {
            if (_commandsGrid.SelectedRows.Count == 0) return;

            var row = _commandsGrid.SelectedRows[0];
            var cmd = row.Tag as ClaudeCommand;
            if (cmd == null) return;

            if (MessageBox.Show($"Delete command \"{cmd.Command}\"?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _commands.Remove(cmd);
                RefreshCommandsGrid();
            }
        }

        private void OnSetDefault(object sender, EventArgs e)
        {
            if (_commandsGrid.SelectedRows.Count == 0) return;

            var row = _commandsGrid.SelectedRows[0];
            var cmd = row.Tag as ClaudeCommand;
            if (cmd == null) return;

            // Clear all defaults
            foreach (var c in _commands)
            {
                c.IsDefault = false;
            }

            // Set selected as default
            cmd.IsDefault = true;
            RefreshCommandsGrid();
        }

        private string ShowCommandInputDialog(string title, string currentValue)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.Size = new Size(400, 150);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = Font;

                bool isDark = _theme.IsDark;
                form.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
                form.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

                var label = new Label
                {
                    Text = "Command:",
                    Location = new Point(15, 20),
                    AutoSize = true
                };

                var textBox = new TextBox
                {
                    Text = currentValue,
                    Location = new Point(15, 45),
                    Size = new Size(355, 25),
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.White,
                    ForeColor = isDark ? Color.White : Color.Black
                };

                var okBtn = new Button
                {
                    Text = "OK",
                    Location = new Point(210, 80),
                    Size = new Size(75, 28),
                    DialogResult = DialogResult.OK,
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225),
                    ForeColor = isDark ? Color.White : Color.Black,
                    FlatStyle = FlatStyle.Flat
                };

                var cancelBtn = new Button
                {
                    Text = "Cancel",
                    Location = new Point(295, 80),
                    Size = new Size(75, 28),
                    DialogResult = DialogResult.Cancel,
                    BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225),
                    ForeColor = isDark ? Color.White : Color.Black,
                    FlatStyle = FlatStyle.Flat
                };

                form.Controls.AddRange(new Control[] { label, textBox, okBtn, cancelBtn });
                form.AcceptButton = okBtn;
                form.CancelButton = cancelBtn;

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    return textBox.Text.Trim();
                }
                return null;
            }
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            SaveSettings();
        }

        private void SaveSettings()
        {
            _settings.BeginBatch();
            try
            {
                // Save font sizes
                _settings.SetToolbarFontSize(GetSelectedFontSize(_toolbarFontCombo));
                _settings.SetTerminalFontSize(GetSelectedFontSize(_terminalFontCombo));
                _settings.SetPromptsFontSize(GetSelectedFontSize(_projectPanelFontCombo));
                _settings.SetChatFontSize(GetSelectedFontSize(_chatPanelFontCombo));
                _settings.SetTasksFontSize(GetSelectedFontSize(_tasksPanelFontCombo));
                _settings.SetActivityFontSize(GetSelectedFontSize(_activityPanelFontCombo));

                // Save terminal placement settings
                _settings.SetMaxGridPanes(_maxGridPanesCombo.SelectedIndex + 1);
                _settings.SetMaxTabsPerGrid(_maxTabsPerGridCombo.SelectedIndex + 1);

                // Save agent panel settings
                string agentLayout = _splitBelowRadio.Checked ? "SplitBelow" :
                                     _tabbedRightRadio.Checked ? "TabbedRight" :
                                     _doNotShowRadio.Checked ? "DoNotShow" : "SplitRight";
                _settings.SetAgentPanelLayout(agentLayout);
                _settings.SetAgentPanelCloseMode(_autoCloseRadio.Checked ? "AutoClose" : "ManualClose");

                // Save Claude commands
                _settings.SetClaudeCommands(_commands);
            }
            finally
            {
                _settings.EndBatch();
            }
        }

        // Public properties to get the new settings values (for applying to UI)
        public float ToolbarFontSize => GetSelectedFontSize(_toolbarFontCombo);
        public float TerminalFontSize => GetSelectedFontSize(_terminalFontCombo);
        public float ProjectPanelFontSize => GetSelectedFontSize(_projectPanelFontCombo);
        public float ChatPanelFontSize => GetSelectedFontSize(_chatPanelFontCombo);
        public float TasksPanelFontSize => GetSelectedFontSize(_tasksPanelFontCombo);
        public float ActivityPanelFontSize => GetSelectedFontSize(_activityPanelFontCombo);
        public int MaxGridPanes => _maxGridPanesCombo.SelectedIndex + 1;
        public int MaxTabsPerGrid => _maxTabsPerGridCombo.SelectedIndex + 1;
        public string AgentPanelLayout =>
            _splitBelowRadio.Checked ? "SplitBelow" :
            _tabbedRightRadio.Checked ? "TabbedRight" :
            _doNotShowRadio.Checked ? "DoNotShow" : "SplitRight";
        public string AgentPanelCloseMode => _autoCloseRadio.Checked ? "AutoClose" : "ManualClose";
    }
}
