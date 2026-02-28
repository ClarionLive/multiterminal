using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Terminal;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Event arguments for identity launch requests.
    /// </summary>
    public class LaunchIdentityEventArgs : EventArgs
    {
        public string IdentityName { get; }
        public string SessionId { get; }

        public LaunchIdentityEventArgs(string identityName, string sessionId)
        {
            IdentityName = identityName;
            SessionId = sessionId;
        }
    }

    /// <summary>
    /// Identity info for display in the launcher panel.
    /// </summary>
    public class IdentityInfo
    {
        public string Name { get; set; }
        public string SessionId { get; set; }
        public bool IsRunning { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>
    /// DockContent panel for launching terminals with specific identities.
    /// Displays available identities and their status, allows resuming sessions.
    /// </summary>
    public class LauncherPanelDocument : DockContent
    {
        // Controls
        private Panel _headerPanel;
        private Button _refreshButton;
        private Button _launchButton;
        private ListView _identityListView;
        private Label _statusLabel;

        // Theme
        private TerminalTheme _currentTheme;
        private Font _smallFont;

        // State
        private HashSet<string> _runningIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        #region Events

        /// <summary>
        /// Raised when user requests to launch a terminal with a specific identity.
        /// </summary>
        public event EventHandler<LaunchIdentityEventArgs> LaunchRequested;

        /// <summary>
        /// Raised when user requests to refresh the identity list.
        /// </summary>
        public event EventHandler RefreshRequested;

        #endregion

        public LauncherPanelDocument()
        {
            Text = "Launcher";
            TabText = "Launcher";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
            ShowHint = DockState.DockRight;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _smallFont = new Font("Segoe UI", 9f);

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);

            // Header panel with buttons
            _headerPanel = CreateHeaderPanel();
            _headerPanel.Dock = DockStyle.Top;

            // Status label at bottom
            _statusLabel = new Label
            {
                Text = "Select an identity to launch",
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = _smallFont
            };

            // Identity list view
            _identityListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                Font = _smallFont,
                BorderStyle = BorderStyle.None
            };
            _identityListView.Columns.Add("Name", 100);
            _identityListView.Columns.Add("Status", 80);
            _identityListView.Columns.Add("Summary", 150);
            _identityListView.SelectedIndexChanged += OnIdentitySelectionChanged;
            _identityListView.DoubleClick += OnIdentityDoubleClick;

            // Add controls in correct order (bottom-up for docking)
            Controls.Add(_identityListView);
            Controls.Add(_statusLabel);
            Controls.Add(_headerPanel);

            ResumeLayout(false);
        }

        private Panel CreateHeaderPanel()
        {
            var panel = new Panel
            {
                Height = 40,
                Padding = new Padding(4)
            };

            _refreshButton = new Button
            {
                Text = "Refresh",
                Width = 70,
                Height = 28,
                Font = _smallFont,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(4, 6)
            };
            _refreshButton.FlatAppearance.BorderSize = 1;
            _refreshButton.Click += OnRefreshButtonClick;

            _launchButton = new Button
            {
                Text = "Launch",
                Width = 70,
                Height = 28,
                Font = _smallFont,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(80, 6),
                Enabled = false
            };
            _launchButton.FlatAppearance.BorderSize = 1;
            _launchButton.Click += OnLaunchButtonClick;

            panel.Controls.Add(_refreshButton);
            panel.Controls.Add(_launchButton);

            return panel;
        }

        #region Public Methods

        /// <summary>
        /// Sets the theme for the panel.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;
            bool isDark = theme?.IsDark ?? true;

            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _headerPanel.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 236, 242);
            _statusLabel.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 236, 242);
            _statusLabel.ForeColor = isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(80, 80, 80);

            // ListView colors
            _identityListView.BackColor = isDark ? Color.FromArgb(37, 37, 38) : Color.White;
            _identityListView.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

            // Button colors
            Color buttonBg = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);
            Color buttonFg = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            Color borderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);

            foreach (Control c in _headerPanel.Controls)
            {
                if (c is Button btn)
                {
                    btn.BackColor = buttonBg;
                    btn.ForeColor = buttonFg;
                    btn.FlatAppearance.BorderColor = borderColor;
                }
            }

            // Refresh list items with new colors
            RefreshListColors(isDark);
        }

        /// <summary>
        /// Updates the identity list with current identities.
        /// </summary>
        public void UpdateIdentities(IEnumerable<IdentityInfo> identities)
        {
            _identityListView.BeginUpdate();
            _identityListView.Items.Clear();

            bool isDark = _currentTheme?.IsDark ?? true;

            foreach (var identity in identities)
            {
                var item = new ListViewItem(identity.Name)
                {
                    Tag = identity
                };

                // Status column
                string statusText = identity.IsRunning ? "Running" : (string.IsNullOrEmpty(identity.SessionId) ? "New" : "Resumable");
                item.SubItems.Add(statusText);

                // Summary column
                item.SubItems.Add(identity.Summary ?? "");

                // Color based on status
                if (identity.IsRunning)
                {
                    item.ForeColor = isDark ? Color.FromArgb(78, 201, 176) : Color.FromArgb(0, 128, 64); // Teal/green
                }
                else if (!string.IsNullOrEmpty(identity.SessionId))
                {
                    item.ForeColor = isDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 102, 204); // Blue
                }

                _identityListView.Items.Add(item);
            }

            _identityListView.EndUpdate();

            // Update status
            int total = _identityListView.Items.Count;
            int running = 0;
            foreach (ListViewItem item in _identityListView.Items)
            {
                if (item.Tag is IdentityInfo info && info.IsRunning)
                    running++;
            }
            _statusLabel.Text = $"{total} identities, {running} running";
        }

        /// <summary>
        /// Marks an identity as running (terminal is open).
        /// </summary>
        public void SetIdentityRunning(string identityName, bool isRunning)
        {
            if (isRunning)
                _runningIdentities.Add(identityName);
            else
                _runningIdentities.Remove(identityName);

            // Update the list item if it exists
            foreach (ListViewItem item in _identityListView.Items)
            {
                if (item.Tag is IdentityInfo info &&
                    string.Equals(info.Name, identityName, StringComparison.OrdinalIgnoreCase))
                {
                    info.IsRunning = isRunning;
                    item.SubItems[1].Text = isRunning ? "Running" :
                        (string.IsNullOrEmpty(info.SessionId) ? "New" : "Resumable");

                    bool isDark = _currentTheme?.IsDark ?? true;
                    if (isRunning)
                    {
                        item.ForeColor = isDark ? Color.FromArgb(78, 201, 176) : Color.FromArgb(0, 128, 64);
                    }
                    else if (!string.IsNullOrEmpty(info.SessionId))
                    {
                        item.ForeColor = isDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 102, 204);
                    }
                    else
                    {
                        item.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
                    }
                    break;
                }
            }

            // Update status count
            UpdateStatusCount();
        }

        #endregion

        #region Event Handlers

        private void OnRefreshButtonClick(object sender, EventArgs e)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnLaunchButtonClick(object sender, EventArgs e)
        {
            LaunchSelectedIdentity();
        }

        private void OnIdentitySelectionChanged(object sender, EventArgs e)
        {
            var selected = _identityListView.SelectedItems.Count > 0
                ? _identityListView.SelectedItems[0].Tag as IdentityInfo
                : null;

            // Enable launch button if an identity is selected and not already running
            _launchButton.Enabled = selected != null && !selected.IsRunning;

            // Update status with selection info
            if (selected != null)
            {
                if (selected.IsRunning)
                {
                    _statusLabel.Text = $"{selected.Name} is already running";
                }
                else if (!string.IsNullOrEmpty(selected.SessionId))
                {
                    _statusLabel.Text = $"Resume {selected.Name}'s session";
                }
                else
                {
                    _statusLabel.Text = $"Start new session as {selected.Name}";
                }
            }
            else
            {
                UpdateStatusCount();
            }
        }

        private void OnIdentityDoubleClick(object sender, EventArgs e)
        {
            LaunchSelectedIdentity();
        }

        #endregion

        #region Private Methods

        private void LaunchSelectedIdentity()
        {
            if (_identityListView.SelectedItems.Count == 0)
                return;

            var selected = _identityListView.SelectedItems[0].Tag as IdentityInfo;
            if (selected == null || selected.IsRunning)
                return;

            LaunchRequested?.Invoke(this, new LaunchIdentityEventArgs(selected.Name, selected.SessionId));
        }

        private void RefreshListColors(bool isDark)
        {
            foreach (ListViewItem item in _identityListView.Items)
            {
                if (item.Tag is IdentityInfo info)
                {
                    if (info.IsRunning)
                    {
                        item.ForeColor = isDark ? Color.FromArgb(78, 201, 176) : Color.FromArgb(0, 128, 64);
                    }
                    else if (!string.IsNullOrEmpty(info.SessionId))
                    {
                        item.ForeColor = isDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 102, 204);
                    }
                    else
                    {
                        item.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
                    }
                }
            }
        }

        private void UpdateStatusCount()
        {
            int total = _identityListView.Items.Count;
            int running = 0;
            foreach (ListViewItem item in _identityListView.Items)
            {
                if (item.Tag is IdentityInfo info && info.IsRunning)
                    running++;
            }
            _statusLabel.Text = $"{total} identities, {running} running";
        }

        #endregion

        protected override string GetPersistString()
        {
            return typeof(LauncherPanelDocument).FullName;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _smallFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
