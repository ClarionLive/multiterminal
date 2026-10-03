using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using MultiTerminal.Models;
using MultiTerminal.Services;


namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// WPF replacement for NewProjectDialog. Dark-themed window with five fields:
    /// project name, project folder (with browse), team lead dropdown,
    /// default-terminal dropdown (Claude Code / Codex), and the Quiet start checkbox.
    /// Uses WindowInteropHelper so it can be owned by the WinForms MainForm handle.
    /// </summary>
    public partial class NewProjectWpfDialog : Window
    {
        private readonly List<(string Id, string DisplayName, string AvatarUrl)> _teamLeadProfiles;
        private readonly Func<string, ExistingProjectMatch> _findExistingProject;

        /// <summary>Project name entered by the user.</summary>
        public string ProjectName { get; private set; }

        /// <summary>Absolute folder path entered by the user (may not exist yet).</summary>
        public string ProjectFolder { get; private set; }

        /// <summary>Display name of the selected team lead, or null if none chosen.</summary>
        public string SelectedTeamLead { get; private set; }

        /// <summary>
        /// Canonical storage value for the default terminal the user picked
        /// (<see cref="TerminalKindHelper.ClaudeCodeValue"/> or
        /// <see cref="TerminalKindHelper.CodexValue"/>). Always set — defaults
        /// to ClaudeCode if the user didn't touch the dropdown.
        /// </summary>
        public string SelectedDefaultTerminal { get; private set; } = TerminalKindHelper.ClaudeCodeValue;

        /// <summary>The Quiet start choice for the new project (GitHub #34).</summary>
        public bool SelectedQuietStart { get; private set; }

        /// <summary>
        /// What to do once the dialog closes with true (task 9f95ab0c): create a new project, or —
        /// when the folder already held one — open it or rename it. Never CreateNew for an occupied folder.
        /// </summary>
        public NewProjectFolderDecision Decision { get; private set; } = NewProjectFolderDecision.CreateNew;

        /// <summary>The project already at <see cref="ProjectFolder"/>, or null when the folder was free.</summary>
        public ExistingProjectMatch ExistingProject { get; private set; }

        /// <param name="initialQuietStart">Pre-fills Quiet start. The caller passes the value chosen
        /// for the last project created here (Owner decision, task e0fa9d90).</param>
        /// <param name="findExistingProject">Reports the project already at a folder (task 9f95ab0c).
        /// When it finds one, Create asks what to do instead of creating.</param>
        public NewProjectWpfDialog(
            bool isDark,
            List<(string Id, string DisplayName, string AvatarUrl)> teamLeadProfiles,
            bool initialQuietStart = false,
            Func<string, ExistingProjectMatch> findExistingProject = null)
        {
            _teamLeadProfiles = teamLeadProfiles ?? new List<(string, string, string)>();
            _findExistingProject = findExistingProject;

            InitializeComponent();

            // Apply light theme override if needed (default XAML is dark)
            if (!isDark)
                ApplyLightTheme();

            PopulateTeamLeadDropdown();
            PopulateDefaultTerminalDropdown();
            QuietStartCheck.IsChecked = initialQuietStart;
            SetupPlaceholder();
        }

        private void PopulateTeamLeadDropdown()
        {
            TeamLeadCombo.Items.Add("Unassigned");
            foreach (var (_, displayName, _) in _teamLeadProfiles)
            {
                TeamLeadCombo.Items.Add(displayName ?? "(unnamed)");
            }
            TeamLeadCombo.SelectedIndex = 0;
        }

        private void PopulateDefaultTerminalDropdown()
        {
            // Index 0 → Claude Code (default), Index 1 → Codex.
            DefaultTerminalCombo.Items.Add("Claude Code");
            DefaultTerminalCombo.Items.Add("Codex");
            DefaultTerminalCombo.SelectedIndex = 0;
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

            // A folder that already holds a project is never created over (task 9f95ab0c, Owner
            // decision 2026-10-02): ask, offering exactly open / rename / different folder / cancel.
            var existing = _findExistingProject?.Invoke(folder) ?? ExistingProjectMatch.None;
            var decision = ExistingProjectDetector.Decide(existing, m => AskAboutExistingProject(m, name));
            if (decision == NewProjectFolderDecision.ChooseDifferentFolder)
            {
                ShowError(existing.CanOpenOrRename
                    ? $"That folder already has the project '{existing.ProjectName}'. Choose a different folder."
                    : ExistingProjectDetector.DescribeProblem(existing));
                FolderBox.Focus();
                FolderBox.SelectAll();
                return;
            }
            if (decision == NewProjectFolderDecision.Cancel)
            {
                DialogResult = false;
                return;
            }

            Decision = decision;
            ExistingProject = existing.Exists ? existing : null;
            ProjectName = name;
            ProjectFolder = folder;

            int selectedIndex = TeamLeadCombo.SelectedIndex;
            // Index 0 is "Unassigned", indices 1+ map to _teamLeadProfiles
            if (selectedIndex > 0 && selectedIndex - 1 < _teamLeadProfiles.Count)
                SelectedTeamLead = _teamLeadProfiles[selectedIndex - 1].DisplayName;
            else
                SelectedTeamLead = null;

            SelectedDefaultTerminal = DefaultTerminalCombo.SelectedIndex == 1
                ? TerminalKindHelper.CodexValue
                : TerminalKindHelper.ClaudeCodeValue;

            SelectedQuietStart = QuietStartCheck.IsChecked == true;

            DialogResult = true;
        }

        private NewProjectFolderDecision AskAboutExistingProject(ExistingProjectMatch existing, string newName)
        {
            // Native TaskDialog, the same confirm style as TaskHudRenderer's re-assign prompt.
            var open = new System.Windows.Forms.TaskDialogCommandLinkButton(
                "Open existing project",
                $"Launch '{existing.ProjectName}' as it is.");
            var rename = new System.Windows.Forms.TaskDialogCommandLinkButton(
                "Rename existing",
                $"Rename '{existing.ProjectName}' to '{newName}', then launch it. Its id, tasks, team lead, description and creation date are kept.");
            var differentFolder = new System.Windows.Forms.TaskDialogCommandLinkButton(
                "Choose a different folder",
                "Go back to New Project and pick another folder.");
            var cancel = System.Windows.Forms.TaskDialogButton.Cancel;

            // Open/Rename would act on another folder's project or on an unreadable file: offer only a
            // different folder and Cancel, and say why (pipeline Run 1 on 9f95ab0c).
            if (!existing.CanOpenOrRename)
            {
                var blocked = new System.Windows.Forms.TaskDialogPage
                {
                    Caption = "Project already exists",
                    Heading = "This folder already has a project that cannot be opened from here",
                    Text = ExistingProjectDetector.DescribeProblem(existing),
                    Icon = System.Windows.Forms.TaskDialogIcon.Warning,
                    AllowCancel = true,
                    DefaultButton = cancel,
                };
                blocked.Buttons.Add(differentFolder);
                blocked.Buttons.Add(cancel);
                var choice = System.Windows.Forms.TaskDialog.ShowDialog(new WindowInteropHelper(this).Handle, blocked);
                return choice == differentFolder ? NewProjectFolderDecision.ChooseDifferentFolder : NewProjectFolderDecision.Cancel;
            }

            string text = $"{existing.ProjectPath}\nProject id: {existing.ProjectId}";
            if (existing.DatabaseIds.Count > 1)
                text += $"\n\n{existing.DatabaseIds.Count} projects in MultiTerminal point at this folder ({string.Join(", ", existing.DatabaseIds)}). Open and Rename act on {existing.ProjectId}.";
            text += "\n\nA second project on the same folder is not supported.";

            var page = new System.Windows.Forms.TaskDialogPage
            {
                Caption = "Project already exists",
                Heading = $"This folder already has a project: {existing.ProjectName}",
                Text = text,
                Icon = System.Windows.Forms.TaskDialogIcon.Warning,
                AllowCancel = true,
                DefaultButton = cancel,
            };
            page.Buttons.Add(open);
            page.Buttons.Add(rename);
            page.Buttons.Add(differentFolder);
            page.Buttons.Add(cancel);

            var clicked = System.Windows.Forms.TaskDialog.ShowDialog(new WindowInteropHelper(this).Handle, page);
            if (clicked == open) return NewProjectFolderDecision.OpenExisting;
            if (clicked == rename) return NewProjectFolderDecision.RenameExisting;
            if (clicked == differentFolder) return NewProjectFolderDecision.ChooseDifferentFolder;
            return NewProjectFolderDecision.Cancel;
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
