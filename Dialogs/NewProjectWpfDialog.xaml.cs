using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// WPF replacement for NewProjectDialog. Dark-themed window with three fields:
    /// project name, project folder (with browse), and team lead dropdown.
    /// Uses WindowInteropHelper so it can be owned by the WinForms MainForm handle.
    /// </summary>
    public partial class NewProjectWpfDialog : Window
    {
        private readonly List<(string Id, string DisplayName, string AvatarUrl)> _teamLeadProfiles;

        /// <summary>Project name entered by the user.</summary>
        public string ProjectName { get; private set; }

        /// <summary>Absolute folder path entered by the user (may not exist yet).</summary>
        public string ProjectFolder { get; private set; }

        /// <summary>Display name of the selected team lead, or null if none chosen.</summary>
        public string SelectedTeamLead { get; private set; }

        public NewProjectWpfDialog(
            bool isDark,
            List<(string Id, string DisplayName, string AvatarUrl)> teamLeadProfiles)
        {
            _teamLeadProfiles = teamLeadProfiles ?? new List<(string, string, string)>();

            InitializeComponent();

            // Apply light theme override if needed (default XAML is dark)
            if (!isDark)
                ApplyLightTheme();

            PopulateTeamLeadDropdown();
            SetupPlaceholder();
        }

        private void PopulateTeamLeadDropdown()
        {
            TeamLeadCombo.Items.Add("(none)");
            foreach (var (_, displayName, _) in _teamLeadProfiles)
            {
                TeamLeadCombo.Items.Add(displayName ?? "(unnamed)");
            }
            TeamLeadCombo.SelectedIndex = 0;
        }

        private void SetupPlaceholder()
        {
            // Simple placeholder behaviour using GotFocus/LostFocus
            NameBox.GotFocus += (s, e) =>
            {
                if (NameBox.Text == "My Project")
                    NameBox.Text = string.Empty;
            };
            NameBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                    NameBox.Text = "My Project";
            };
            NameBox.Text = "My Project";
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // Use WinForms FolderBrowserDialog via interop (WPF has no native folder picker)
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Project Folder",
                ShowNewFolderButton = true
            };

            string current = FolderBox.Text?.Trim();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dlg.SelectedPath = current;

            var hwnd = new WindowInteropHelper(this).Handle;
            System.Windows.Forms.NativeWindow owner = null;
            if (hwnd != IntPtr.Zero)
            {
                owner = new System.Windows.Forms.NativeWindow();
                owner.AssignHandle(hwnd);
            }

            System.Windows.Forms.DialogResult result;
            try
            {
                result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            }
            finally
            {
                owner?.ReleaseHandle();
            }

            if (result == System.Windows.Forms.DialogResult.OK)
            {
                FolderBox.Text = dlg.SelectedPath;

                // Auto-fill project name from folder name when name is still placeholder/empty
                string currentName = NameBox.Text?.Trim();
                if (string.IsNullOrEmpty(currentName) || currentName == "My Project")
                {
                    string folderName = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(folderName))
                        NameBox.Text = folderName;
                }
            }
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            TryCreate();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void TryCreate()
        {
            ErrorLabel.Visibility = Visibility.Collapsed;

            string name = NameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name) || name == "My Project")
            {
                ShowError("Project name is required.");
                NameBox.Focus();
                return;
            }

            string folder = FolderBox.Text?.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                ShowError("Please enter a project folder path.");
                FolderBox.Focus();
                return;
            }

            // Validate path syntax only — do not require the folder to already exist
            try
            {
                folder = Path.GetFullPath(folder);
            }
            catch
            {
                ShowError("The project folder path is not valid.");
                FolderBox.Focus();
                return;
            }

            ProjectName = name;
            ProjectFolder = folder;

            int selectedIndex = TeamLeadCombo.SelectedIndex;
            // Index 0 is "(none)", indices 1+ map to _teamLeadProfiles
            if (selectedIndex > 0 && selectedIndex - 1 < _teamLeadProfiles.Count)
                SelectedTeamLead = _teamLeadProfiles[selectedIndex - 1].DisplayName;
            else
                SelectedTeamLead = null;

            DialogResult = true;
        }

        private void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.Visibility = Visibility.Visible;
        }

        private void ApplyLightTheme()
        {
            // Override background colors for light mode
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(240, 240, 240));
        }
    }
}
