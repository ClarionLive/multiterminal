using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for viewing and searching chat message history.
    /// </summary>
    public partial class ChatHistoryDialog : Form
    {
        private readonly TaskDatabase _database;
        private readonly TerminalTheme _theme;
        private List<ChatMessageRecord> _allMessages;
        private List<ChatMessageRecord> _filteredMessages;

        // Controls
        private TextBox _searchBox;
        private ComboBox _terminalFilter;
        private ComboBox _dateRangeFilter;
        private Button _refreshButton;
        private DataGridView _messagesGrid;
        private TextBox _previewBox;
        private Button _copyButton;
        private Button _closeButton;

        /// <summary>
        /// Creates a new ChatHistoryDialog instance.
        /// </summary>
        /// <param name="database">The session database for chat message queries.</param>
        /// <param name="theme">The terminal theme to apply.</param>
        public ChatHistoryDialog(TaskDatabase database, TerminalTheme theme)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _theme = theme ?? TerminalTheme.Dark;

            InitializeComponent();
            ApplyTheme();
            LoadMessages();
        }

        /// <summary>
        /// Loads all messages from the database.
        /// </summary>
        private void LoadMessages()
        {
            try
            {
                _allMessages = _database.GetChatMessages(1000);
                PopulateTerminalFilter();
                ApplyFilters();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load messages: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                _allMessages = new List<ChatMessageRecord>();
                _filteredMessages = new List<ChatMessageRecord>();
            }
        }

        /// <summary>
        /// Populates the terminal filter dropdown with unique terminal names.
        /// </summary>
        private void PopulateTerminalFilter()
        {
            var terminals = new HashSet<string>();
            foreach (var msg in _allMessages)
            {
                if (!string.IsNullOrEmpty(msg.FromTerminal))
                    terminals.Add(msg.FromTerminal);
                if (!string.IsNullOrEmpty(msg.ToTerminal) && msg.ToTerminal != "all")
                    terminals.Add(msg.ToTerminal);
            }

            _terminalFilter.Items.Clear();
            _terminalFilter.Items.Add("All Terminals");
            foreach (var terminal in terminals.OrderBy(t => t))
            {
                _terminalFilter.Items.Add(terminal);
            }
            _terminalFilter.SelectedIndex = 0;
        }

        /// <summary>
        /// Applies search, terminal, and date filters to the message list.
        /// </summary>
        private void ApplyFilters()
        {
            string searchText = _searchBox?.Text?.Trim() ?? "";
            string selectedTerminal = _terminalFilter?.SelectedItem?.ToString() ?? "All Terminals";
            string selectedDateRange = _dateRangeFilter?.SelectedItem?.ToString() ?? "All Time";

            // Start with all messages
            _filteredMessages = _allMessages.ToList();

            // Apply terminal filter
            if (selectedTerminal != "All Terminals")
            {
                _filteredMessages = _filteredMessages
                    .Where(m => m.FromTerminal == selectedTerminal || m.ToTerminal == selectedTerminal)
                    .ToList();
            }

            // Apply date range filter
            DateTime? minDate = GetMinDateForRange(selectedDateRange);
            if (minDate.HasValue)
            {
                _filteredMessages = _filteredMessages
                    .Where(m => m.Timestamp >= minDate.Value)
                    .ToList();
            }

            // Apply search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                _filteredMessages = _filteredMessages
                    .Where(m => m.Content != null &&
                           m.Content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            // Sort by timestamp descending (newest first)
            _filteredMessages = _filteredMessages
                .OrderByDescending(m => m.Timestamp)
                .ToList();

            PopulateGrid();
        }

        /// <summary>
        /// Gets the minimum date for the selected date range.
        /// </summary>
        private DateTime? GetMinDateForRange(string range)
        {
            var now = DateTime.Now;
            switch (range)
            {
                case "Today":
                    return now.Date;
                case "Last 7 Days":
                    return now.Date.AddDays(-7);
                case "Last 30 Days":
                    return now.Date.AddDays(-30);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Populates the DataGridView with filtered messages.
        /// </summary>
        private void PopulateGrid()
        {
            _messagesGrid.Rows.Clear();

            foreach (var msg in _filteredMessages)
            {
                int rowIndex = _messagesGrid.Rows.Add();
                var row = _messagesGrid.Rows[rowIndex];
                row.Tag = msg;

                // Time column
                row.Cells["TimeColumn"].Value = FormatTimestamp(msg.Timestamp);

                // From column
                row.Cells["FromColumn"].Value = msg.FromTerminal ?? "";

                // To column
                string toValue = msg.IsBroadcast ? "[all]" : (msg.ToTerminal ?? "");
                row.Cells["ToColumn"].Value = toValue;

                // Message column (truncated)
                row.Cells["MessageColumn"].Value = TruncateMessage(msg.Content, 80);
                row.Cells["MessageColumn"].ToolTipText = msg.Content;
            }

            UpdateButtonStates();
            UpdateStatusLabel();
        }

        /// <summary>
        /// Formats a timestamp for display.
        /// </summary>
        private string FormatTimestamp(DateTime timestamp)
        {
            var now = DateTime.Now;

            if (timestamp.Date == now.Date)
                return timestamp.ToString("h:mm:ss tt");

            if (timestamp.Date == now.Date.AddDays(-1))
                return "Yesterday " + timestamp.ToString("h:mm tt");

            if ((now - timestamp).TotalDays < 7)
                return timestamp.ToString("ddd h:mm tt");

            return timestamp.ToString("MMM d, h:mm tt");
        }

        /// <summary>
        /// Truncates a message for display in the grid.
        /// </summary>
        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message))
                return "";

            // Replace newlines with spaces for grid display
            message = message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            if (message.Length <= maxLength)
                return message;

            return message.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Updates the preview box with the selected message content.
        /// </summary>
        private void UpdatePreview()
        {
            if (_messagesGrid.SelectedRows.Count > 0)
            {
                var msg = _messagesGrid.SelectedRows[0].Tag as ChatMessageRecord;
                if (msg != null)
                {
                    _previewBox.Text = msg.Content ?? "";
                    return;
                }
            }
            _previewBox.Text = "";
        }

        /// <summary>
        /// Updates button enabled states based on selection.
        /// </summary>
        private void UpdateButtonStates()
        {
            bool hasSelection = _messagesGrid.SelectedRows.Count > 0;
            _copyButton.Enabled = hasSelection;
        }

        /// <summary>
        /// Updates the status label with message count.
        /// </summary>
        private void UpdateStatusLabel()
        {
            int total = _allMessages?.Count ?? 0;
            int filtered = _filteredMessages?.Count ?? 0;

            if (total == filtered)
            {
                this.Text = $"Chat History ({total} messages)";
            }
            else
            {
                this.Text = $"Chat History ({filtered} of {total} messages)";
            }
        }

        #region Event Handlers

        private void SearchBox_TextChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        private void TerminalFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        private void DateRangeFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        private void RefreshButton_Click(object sender, EventArgs e)
        {
            LoadMessages();
        }

        private void MessagesGrid_SelectionChanged(object sender, EventArgs e)
        {
            UpdateButtonStates();
            UpdatePreview();
        }

        private void CopyButton_Click(object sender, EventArgs e)
        {
            if (_messagesGrid.SelectedRows.Count > 0)
            {
                var msg = _messagesGrid.SelectedRows[0].Tag as ChatMessageRecord;
                if (msg != null && !string.IsNullOrEmpty(msg.Content))
                {
                    try
                    {
                        Clipboard.SetText(msg.Content);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to copy to clipboard: {ex.Message}",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
            }
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        #endregion

        #region Theme Application

        private void ApplyTheme()
        {
            // Form colors
            BackColor = _theme.ToolbarBackground;
            ForeColor = _theme.ToolbarForeground;

            // Search TextBox
            ApplyThemeToTextBox(_searchBox);

            // Preview TextBox
            ApplyThemeToTextBox(_previewBox);

            // ComboBoxes
            ApplyThemeToComboBox(_terminalFilter);
            ApplyThemeToComboBox(_dateRangeFilter);

            // Buttons
            ApplyThemeToButton(_refreshButton);
            ApplyThemeToButton(_copyButton);
            ApplyThemeToButton(_closeButton);

            // DataGridView
            ApplyThemeToDataGridView(_messagesGrid);
        }

        private void ApplyThemeToTextBox(TextBox textBox)
        {
            textBox.BackColor = _theme.Background;
            textBox.ForeColor = _theme.Foreground;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }

        private void ApplyThemeToComboBox(ComboBox comboBox)
        {
            comboBox.BackColor = _theme.Background;
            comboBox.ForeColor = _theme.Foreground;
            comboBox.FlatStyle = FlatStyle.Flat;
        }

        private void ApplyThemeToButton(Button button)
        {
            if (_theme.IsDark)
            {
                button.BackColor = Color.FromArgb(62, 62, 66);
                button.ForeColor = Color.White;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(86, 86, 86);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 84);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 94);
            }
            else
            {
                button.BackColor = Color.FromArgb(225, 225, 225);
                button.ForeColor = Color.FromArgb(30, 30, 30);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(173, 173, 173);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 220, 240);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 200, 220);
            }
        }

        private void ApplyThemeToDataGridView(DataGridView dgv)
        {
            dgv.BackgroundColor = _theme.Background;
            dgv.ForeColor = _theme.Foreground;
            dgv.GridColor = _theme.IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(200, 200, 200);
            dgv.BorderStyle = BorderStyle.FixedSingle;

            // Default cell style
            dgv.DefaultCellStyle.BackColor = _theme.Background;
            dgv.DefaultCellStyle.ForeColor = _theme.Foreground;
            dgv.DefaultCellStyle.SelectionBackColor = _theme.SelectionBackground;
            dgv.DefaultCellStyle.SelectionForeColor = _theme.SelectionForeground;

            // Header style
            dgv.ColumnHeadersDefaultCellStyle.BackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = _theme.ToolbarForeground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = _theme.ToolbarForeground;
            dgv.EnableHeadersVisualStyles = false;

            // Row headers
            dgv.RowHeadersDefaultCellStyle.BackColor = _theme.ToolbarBackground;
            dgv.RowHeadersDefaultCellStyle.ForeColor = _theme.ToolbarForeground;
            dgv.RowHeadersDefaultCellStyle.SelectionBackColor = _theme.SelectionBackground;
            dgv.RowHeadersDefaultCellStyle.SelectionForeColor = _theme.SelectionForeground;

            // Alternating row style
            dgv.AlternatingRowsDefaultCellStyle.BackColor = _theme.IsDark
                ? Color.FromArgb(20, 20, 20)
                : Color.FromArgb(245, 245, 245);
            dgv.AlternatingRowsDefaultCellStyle.ForeColor = _theme.Foreground;
        }

        #endregion

        #region Designer Code

        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this._searchBox = new TextBox();
            this._terminalFilter = new ComboBox();
            this._dateRangeFilter = new ComboBox();
            this._refreshButton = new Button();
            this._messagesGrid = new DataGridView();
            this._previewBox = new TextBox();
            this._copyButton = new Button();
            this._closeButton = new Button();

            ((System.ComponentModel.ISupportInitialize)(this._messagesGrid)).BeginInit();
            this.SuspendLayout();

            // Search label
            var searchLabel = new Label();
            searchLabel.AutoSize = true;
            searchLabel.Location = new Point(12, 18);
            searchLabel.Text = "Search:";
            searchLabel.ForeColor = _theme.ToolbarForeground;

            // searchBox
            this._searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this._searchBox.Location = new Point(70, 15);
            this._searchBox.Name = "_searchBox";
            this._searchBox.Size = new Size(250, 23);
            this._searchBox.TabIndex = 0;
            this._searchBox.TextChanged += new EventHandler(this.SearchBox_TextChanged);

            // Terminal label
            var terminalLabel = new Label();
            terminalLabel.AutoSize = true;
            terminalLabel.Location = new Point(330, 18);
            terminalLabel.Text = "Terminal:";
            terminalLabel.ForeColor = _theme.ToolbarForeground;

            // terminalFilter
            this._terminalFilter.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this._terminalFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            this._terminalFilter.Location = new Point(395, 14);
            this._terminalFilter.Name = "_terminalFilter";
            this._terminalFilter.Size = new Size(140, 23);
            this._terminalFilter.TabIndex = 1;
            this._terminalFilter.SelectedIndexChanged += new EventHandler(this.TerminalFilter_SelectedIndexChanged);

            // Date Range label
            var dateLabel = new Label();
            dateLabel.AutoSize = true;
            dateLabel.Location = new Point(12, 50);
            dateLabel.Text = "Date Range:";
            dateLabel.ForeColor = _theme.ToolbarForeground;

            // dateRangeFilter
            this._dateRangeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            this._dateRangeFilter.Location = new Point(90, 47);
            this._dateRangeFilter.Name = "_dateRangeFilter";
            this._dateRangeFilter.Size = new Size(120, 23);
            this._dateRangeFilter.TabIndex = 2;
            this._dateRangeFilter.Items.AddRange(new object[] { "All Time", "Today", "Last 7 Days", "Last 30 Days" });
            this._dateRangeFilter.SelectedIndex = 0;
            this._dateRangeFilter.SelectedIndexChanged += new EventHandler(this.DateRangeFilter_SelectedIndexChanged);

            // refreshButton
            this._refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this._refreshButton.Location = new Point(545, 14);
            this._refreshButton.Name = "_refreshButton";
            this._refreshButton.Size = new Size(75, 27);
            this._refreshButton.TabIndex = 3;
            this._refreshButton.Text = "Refresh";
            this._refreshButton.Click += new EventHandler(this.RefreshButton_Click);

            // messagesGrid
            this._messagesGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this._messagesGrid.AllowUserToAddRows = false;
            this._messagesGrid.AllowUserToDeleteRows = false;
            this._messagesGrid.AllowUserToResizeRows = false;
            this._messagesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            this._messagesGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this._messagesGrid.Location = new Point(12, 80);
            this._messagesGrid.MultiSelect = false;
            this._messagesGrid.Name = "_messagesGrid";
            this._messagesGrid.ReadOnly = true;
            this._messagesGrid.RowHeadersVisible = false;
            this._messagesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this._messagesGrid.Size = new Size(608, 300);
            this._messagesGrid.TabIndex = 4;
            this._messagesGrid.SelectionChanged += new EventHandler(this.MessagesGrid_SelectionChanged);

            // Add columns to DataGridView
            var timeColumn = new DataGridViewTextBoxColumn();
            timeColumn.Name = "TimeColumn";
            timeColumn.HeaderText = "Time";
            timeColumn.FillWeight = 15;
            timeColumn.MinimumWidth = 80;
            this._messagesGrid.Columns.Add(timeColumn);

            var fromColumn = new DataGridViewTextBoxColumn();
            fromColumn.Name = "FromColumn";
            fromColumn.HeaderText = "From";
            fromColumn.FillWeight = 15;
            fromColumn.MinimumWidth = 80;
            this._messagesGrid.Columns.Add(fromColumn);

            var toColumn = new DataGridViewTextBoxColumn();
            toColumn.Name = "ToColumn";
            toColumn.HeaderText = "To";
            toColumn.FillWeight = 15;
            toColumn.MinimumWidth = 80;
            this._messagesGrid.Columns.Add(toColumn);

            var messageColumn = new DataGridViewTextBoxColumn();
            messageColumn.Name = "MessageColumn";
            messageColumn.HeaderText = "Message";
            messageColumn.FillWeight = 55;
            this._messagesGrid.Columns.Add(messageColumn);

            // Preview label
            var previewLabel = new Label();
            previewLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            previewLabel.AutoSize = true;
            previewLabel.Location = new Point(12, 390);
            previewLabel.Text = "Message Preview:";
            previewLabel.ForeColor = _theme.ToolbarForeground;

            // previewBox
            this._previewBox.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this._previewBox.Location = new Point(12, 410);
            this._previewBox.Multiline = true;
            this._previewBox.Name = "_previewBox";
            this._previewBox.ReadOnly = true;
            this._previewBox.ScrollBars = ScrollBars.Vertical;
            this._previewBox.Size = new Size(608, 80);
            this._previewBox.TabIndex = 5;

            // copyButton
            this._copyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this._copyButton.Location = new Point(455, 500);
            this._copyButton.Name = "_copyButton";
            this._copyButton.Size = new Size(80, 30);
            this._copyButton.TabIndex = 6;
            this._copyButton.Text = "Copy";
            this._copyButton.Click += new EventHandler(this.CopyButton_Click);

            // closeButton
            this._closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this._closeButton.DialogResult = DialogResult.Cancel;
            this._closeButton.Location = new Point(540, 500);
            this._closeButton.Name = "_closeButton";
            this._closeButton.Size = new Size(80, 30);
            this._closeButton.TabIndex = 7;
            this._closeButton.Text = "Close";
            this._closeButton.Click += new EventHandler(this.CloseButton_Click);

            // ChatHistoryDialog
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this._closeButton;
            this.ClientSize = new Size(632, 543);
            this.Controls.Add(searchLabel);
            this.Controls.Add(this._searchBox);
            this.Controls.Add(terminalLabel);
            this.Controls.Add(this._terminalFilter);
            this.Controls.Add(dateLabel);
            this.Controls.Add(this._dateRangeFilter);
            this.Controls.Add(this._refreshButton);
            this.Controls.Add(this._messagesGrid);
            this.Controls.Add(previewLabel);
            this.Controls.Add(this._previewBox);
            this.Controls.Add(this._copyButton);
            this.Controls.Add(this._closeButton);
            this.MinimizeBox = false;
            this.MinimumSize = new Size(650, 500);
            this.Name = "ChatHistoryDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Chat History";

            ((System.ComponentModel.ISupportInitialize)(this._messagesGrid)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion
    }
}
