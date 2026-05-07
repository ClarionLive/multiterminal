using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// WPF replacement for SettingsDialog. Dark-themed window with 4 sections:
    /// Font Sizes, Terminal Placement, Agent Panel, and Claude Commands.
    /// Uses WindowInteropHelper so it can be owned by the WinForms MainForm handle.
    /// </summary>
    public partial class SettingsWpfDialog : Window
    {
        private readonly SettingsService _settings;
        private readonly OwnerProfileService _ownerProfileService;
        private List<ClaudeCommand> _commands;

        /// <summary>
        /// True if the user edited their owner profile during this settings session.
        /// MainForm checks this to know if the profile was updated.
        /// </summary>
        public bool OwnerProfileEdited { get; private set; }

        // Font size options
        private static readonly float[] UiFontSizes = { 8f, 9f, 10f, 11f, 12f, 14f };
        private static readonly float[] TerminalFontSizes = { 8f, 10f, 12f, 14f, 16f, 18f, 20f, 24f };

        // Pipeline provider options (display-cased; SettingsService normalizes to lowercase)
        private static readonly string[] PipelineProviders = { "Claude", "Codex", "Off" };

        // Public properties for MainForm to read after dialog closes
        public float ToolbarFontSize => GetSelectedFontSize(ToolbarFontCombo);
        public float TerminalFontSize => GetSelectedFontSize(TerminalFontCombo);
        public float ProjectPanelFontSize => GetSelectedFontSize(ProjectPanelFontCombo);
        public float ChatPanelFontSize => GetSelectedFontSize(ChatPanelFontCombo);
        public float TasksPanelFontSize => GetSelectedFontSize(TasksPanelFontCombo);
        public float ActivityPanelFontSize => GetSelectedFontSize(ActivityPanelFontCombo);
        public int MaxGridPanes => MaxGridsCombo.SelectedIndex + 1;
        public int MaxTabsPerGrid => MaxTabsCombo.SelectedIndex + 1;

        public string AgentPanelLayout =>
            SplitBelowRadio.IsChecked == true ? "SplitBelow" :
            TabbedRightRadio.IsChecked == true ? "TabbedRight" :
            DoNotShowRadio.IsChecked == true ? "DoNotShow" : "SplitRight";

        public string AgentPanelCloseMode =>
            AutoCloseRadio.IsChecked == true ? "AutoClose" : "ManualClose";

        public SettingsWpfDialog(SettingsService settings, bool isDark, OwnerProfileService ownerProfileService = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ownerProfileService = ownerProfileService;
            _commands = new List<ClaudeCommand>();

            InitializeComponent();

            PopulateComboBoxes();
            LoadSettings();
            LoadProfileSummary();
        }

        private void PopulateComboBoxes()
        {
            // Font size combos — UI panels
            foreach (float size in UiFontSizes)
            {
                string item = $"{size}pt";
                ToolbarFontCombo.Items.Add(item);
                ProjectPanelFontCombo.Items.Add(item);
                ChatPanelFontCombo.Items.Add(item);
                TasksPanelFontCombo.Items.Add(item);
                ActivityPanelFontCombo.Items.Add(item);
            }

            // Terminal font has wider range
            foreach (float size in TerminalFontSizes)
                TerminalFontCombo.Items.Add($"{size}pt");

            // Max Grids: 1-9
            for (int i = 1; i <= 9; i++)
                MaxGridsCombo.Items.Add(i.ToString());

            // Max Tabs per Grid: 1-10
            for (int i = 1; i <= 10; i++)
                MaxTabsCombo.Items.Add(i.ToString());

            // Pipeline provider combos
            foreach (string provider in PipelineProviders)
            {
                VerifierProviderCombo.Items.Add(provider);
                CodeReviewerProviderCombo.Items.Add(provider);
                SecurityAuditorProviderCombo.Items.Add(provider);
                DebuggerProviderCombo.Items.Add(provider);
                CrossModelAdversaryProviderCombo.Items.Add(provider);
            }

            // Codex reasoning-effort dropdown
            CodexEffortCombo.Items.Add("(default)");
            CodexEffortCombo.Items.Add(SettingsService.CodexEffortHigh);
            CodexEffortCombo.Items.Add(SettingsService.CodexEffortMedium);
            CodexEffortCombo.Items.Add(SettingsService.CodexEffortLow);
            CodexEffortCombo.SelectedIndex = 0;
        }

        private void LoadSettings()
        {
            // Font sizes
            SelectFontSize(ToolbarFontCombo, _settings.GetToolbarFontSize());
            SelectFontSize(TerminalFontCombo, _settings.GetTerminalFontSize());
            SelectFontSize(ProjectPanelFontCombo, _settings.GetPromptsFontSize());
            SelectFontSize(ChatPanelFontCombo, _settings.GetChatFontSize());
            SelectFontSize(TasksPanelFontCombo, _settings.GetTasksFontSize());
            SelectFontSize(ActivityPanelFontCombo, _settings.GetActivityFontSize());

            // Terminal placement
            int maxGrids = _settings.GetMaxGridPanes();
            MaxGridsCombo.SelectedIndex = Math.Max(0, Math.Min(8, maxGrids - 1));

            int maxTabs = _settings.GetMaxTabsPerGrid();
            MaxTabsCombo.SelectedIndex = Math.Max(0, Math.Min(9, maxTabs - 1));

            // Agent panel layout radio
            string agentLayout = _settings.GetAgentPanelLayout();
            SplitRightRadio.IsChecked  = agentLayout == "SplitRight";
            SplitBelowRadio.IsChecked  = agentLayout == "SplitBelow";
            TabbedRightRadio.IsChecked = agentLayout == "TabbedRight";
            DoNotShowRadio.IsChecked   = agentLayout == "DoNotShow";

            // Default to SplitRight if nothing matched
            if (SplitRightRadio.IsChecked != true && SplitBelowRadio.IsChecked != true &&
                TabbedRightRadio.IsChecked != true && DoNotShowRadio.IsChecked != true)
            {
                SplitRightRadio.IsChecked = true;
            }

            // Agent panel close mode radio
            string closeMode = _settings.GetAgentPanelCloseMode();
            AutoCloseRadio.IsChecked = closeMode == "AutoClose";
            KeepOpenRadio.IsChecked  = closeMode != "AutoClose";

            // Default working directory
            DefaultWorkingDirText.Text = _settings.GetDefaultWorkingDirectory() ?? "";

            // Git: per-task worktree isolation
            WorktreeModeCheck.IsChecked = string.Equals(
                _settings.GetWorktreeMode(), SettingsService.WorktreeModeOn,
                StringComparison.OrdinalIgnoreCase);

            // Claude commands
            _commands = _settings.GetClaudeCommands();
            RefreshCommandsGrid();

            // Pipeline topology
            SelectProviderByValue(VerifierProviderCombo, _settings.GetPipelineVerifier());
            SelectProviderByValue(CodeReviewerProviderCombo, _settings.GetPipelineCodeReviewer());
            SelectProviderByValue(SecurityAuditorProviderCombo, _settings.GetPipelineSecurityAuditor());
            SelectProviderByValue(DebuggerProviderCombo, _settings.GetPipelineDebugger());
            SelectProviderByValue(CrossModelAdversaryProviderCombo, _settings.GetPipelineCrossModelAdversary());

            // Codex CLI
            CodexBinaryPathText.Text = _settings.GetCodexBinaryPath() ?? "";
            CodexModelText.Text = _settings.GetCodexModel() ?? "";
            CodexDefaultAgentNameText.Text = _settings.GetCodexDefaultAgentName() ?? "";
            string storedEffort = _settings.GetCodexEffort();
            if (string.Equals(storedEffort, SettingsService.CodexEffortHigh, StringComparison.OrdinalIgnoreCase))
                CodexEffortCombo.SelectedIndex = 1;
            else if (string.Equals(storedEffort, SettingsService.CodexEffortMedium, StringComparison.OrdinalIgnoreCase))
                CodexEffortCombo.SelectedIndex = 2;
            else if (string.Equals(storedEffort, SettingsService.CodexEffortLow, StringComparison.OrdinalIgnoreCase))
                CodexEffortCombo.SelectedIndex = 3;
            else
                CodexEffortCombo.SelectedIndex = 0;
        }

        private void SaveSettings()
        {
            _settings.BeginBatch();
            try
            {
                _settings.SetToolbarFontSize(GetSelectedFontSize(ToolbarFontCombo));
                _settings.SetTerminalFontSize(GetSelectedFontSize(TerminalFontCombo));
                _settings.SetPromptsFontSize(GetSelectedFontSize(ProjectPanelFontCombo));
                _settings.SetChatFontSize(GetSelectedFontSize(ChatPanelFontCombo));
                _settings.SetTasksFontSize(GetSelectedFontSize(TasksPanelFontCombo));
                _settings.SetActivityFontSize(GetSelectedFontSize(ActivityPanelFontCombo));

                _settings.SetMaxGridPanes(MaxGridsCombo.SelectedIndex + 1);
                _settings.SetMaxTabsPerGrid(MaxTabsCombo.SelectedIndex + 1);

                _settings.SetAgentPanelLayout(AgentPanelLayout);
                _settings.SetAgentPanelCloseMode(AgentPanelCloseMode);

                _settings.SetDefaultWorkingDirectory(DefaultWorkingDirText.Text?.Trim());

                _settings.SetWorktreeMode(
                    WorktreeModeCheck.IsChecked == true
                        ? SettingsService.WorktreeModeOn
                        : SettingsService.WorktreeModeOff);

                _settings.SetClaudeCommands(_commands);

                _settings.SetPipelineVerifier(GetSelectedProvider(VerifierProviderCombo));
                _settings.SetPipelineCodeReviewer(GetSelectedProvider(CodeReviewerProviderCombo));
                _settings.SetPipelineSecurityAuditor(GetSelectedProvider(SecurityAuditorProviderCombo));
                _settings.SetPipelineDebugger(GetSelectedProvider(DebuggerProviderCombo));
                _settings.SetPipelineCrossModelAdversary(GetSelectedProvider(CrossModelAdversaryProviderCombo));

                _settings.SetCodexBinaryPath(CodexBinaryPathText.Text?.Trim());
                _settings.SetCodexModel(CodexModelText.Text?.Trim());
                _settings.SetCodexDefaultAgentName(CodexDefaultAgentNameText.Text?.Trim());
                // Index 0 is "(default)" — stored as empty.
                _settings.SetCodexEffort(CodexEffortCombo.SelectedIndex switch
                {
                    1 => SettingsService.CodexEffortHigh,
                    2 => SettingsService.CodexEffortMedium,
                    3 => SettingsService.CodexEffortLow,
                    _ => ""
                });
            }
            finally
            {
                _settings.EndBatch();
            }
        }

        // -----------------------------------------------------------------
        // Browse for default working directory
        // -----------------------------------------------------------------

        private void BrowseDefaultDirButton_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select default working directory for Just Claude",
                ShowNewFolderButton = true
            };

            string current = DefaultWorkingDirText.Text?.Trim();
            if (!string.IsNullOrEmpty(current) && System.IO.Directory.Exists(current))
                dlg.SelectedPath = current;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                DefaultWorkingDirText.Text = dlg.SelectedPath;
        }

        // -----------------------------------------------------------------
        // Font size helpers
        // -----------------------------------------------------------------

        private void SelectFontSize(ComboBox combo, float size)
        {
            string target = $"{size}pt";
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i]?.ToString() == target)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            // Try integer match (e.g. 9.0 -> "9pt")
            string intTarget = $"{(int)size}pt";
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i]?.ToString() == intTarget)
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

        // -----------------------------------------------------------------
        // Pipeline provider helpers
        // -----------------------------------------------------------------

        private static void SelectProviderByValue(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string GetSelectedProvider(ComboBox combo) =>
            combo.SelectedItem?.ToString();

        // -----------------------------------------------------------------
        // Commands DataGrid
        // -----------------------------------------------------------------

        private void RefreshCommandsGrid()
        {
            CommandsGrid.ItemsSource = null;
            CommandsGrid.ItemsSource = _commands;
        }

        private void AddCommandButton_Click(object sender, RoutedEventArgs e)
        {
            string command = ShowCommandInputDialog("Add Command", "");
            if (!string.IsNullOrWhiteSpace(command))
            {
                _commands.Add(new ClaudeCommand(command, false));
                RefreshCommandsGrid();
            }
        }

        private void EditCommandButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = CommandsGrid.SelectedItem as ClaudeCommand;
            if (selected == null) return;

            string newCommand = ShowCommandInputDialog("Edit Command", selected.Command);
            if (!string.IsNullOrWhiteSpace(newCommand))
            {
                selected.Command = newCommand;
                RefreshCommandsGrid();
            }
        }

        private void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = CommandsGrid.SelectedItem as ClaudeCommand;
            if (selected == null) return;

            var result = MessageBox.Show(
                $"Delete command \"{selected.Command}\"?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _commands.Remove(selected);
                RefreshCommandsGrid();
            }
        }

        private void SetDefaultCommandButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = CommandsGrid.SelectedItem as ClaudeCommand;
            if (selected == null) return;

            // Clear all defaults, then set the selected one
            foreach (var cmd in _commands)
                cmd.IsDefault = false;

            selected.IsDefault = true;
            RefreshCommandsGrid();
        }

        private void ResetCodexBrokerButton_Click(object sender, RoutedEventArgs e)
        {
            // Force-reset across ALL workspaces. The user reaches for this when Codex
            // reports "not authenticated" even though `codex login` succeeded — the
            // companion's cached broker.json is pointing at a dead pipe and we can't
            // patch the third-party plugin's auth probe to verify pipe liveness.
            // Both calls leave live brokers untouched (pipe-alive / PID-alive checks).
            try
            {
                var brokerResult = CodexBrokerHealthService.EnsureFreshBrokerStateForAllWorkspaces();
                var pruneResult = CodexBrokerHealthService.PruneOrphanSessionDirs(0);

                int totalFiles = brokerResult.CleanedFiles.Count + pruneResult.CleanedFiles.Count;
                int totalDirs = brokerResult.CleanedDirs.Count + pruneResult.CleanedDirs.Count;
                int totalErrors = brokerResult.Errors.Count + pruneResult.Errors.Count;

                string message;
                if (totalFiles == 0 && totalDirs == 0 && totalErrors == 0)
                {
                    message = "Broker state is already clean — nothing to reset.";
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Cleaned {totalFiles} broker.json file(s) and {totalDirs} session dir(s).");

                    if (brokerResult.CleanedFiles.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("broker.json removed:");
                        foreach (string f in brokerResult.CleanedFiles)
                            sb.AppendLine("  " + f);
                    }

                    if (brokerResult.CleanedDirs.Count > 0 || pruneResult.CleanedDirs.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("Session dirs removed:");
                        foreach (string d in brokerResult.CleanedDirs)
                            sb.AppendLine("  " + d);
                        foreach (string d in pruneResult.CleanedDirs)
                            sb.AppendLine("  " + d);
                    }

                    if (totalErrors > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"Errors ({totalErrors}):");
                        foreach (string err in brokerResult.Errors)
                            sb.AppendLine("  " + err);
                        foreach (string err in pruneResult.Errors)
                            sb.AppendLine("  " + err);
                    }

                    sb.AppendLine();
                    sb.AppendLine("Next Codex launch will spawn a fresh broker.");
                    message = sb.ToString();
                }

                MessageBox.Show(
                    this,
                    message,
                    "Reset Codex broker",
                    MessageBoxButton.OK,
                    totalErrors > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Reset failed: " + ex.Message,
                    "Reset Codex broker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------------------------------------------
        // Command input sub-dialog (small WPF window)
        // -----------------------------------------------------------------

        private string ShowCommandInputDialog(string title, string currentValue)
        {
            var dlg = new CommandInputDialog(title, currentValue);
            dlg.Owner = this;
            return dlg.ShowDialog() == true ? dlg.EnteredValue : null;
        }

        // -----------------------------------------------------------------
        // User Profile
        // -----------------------------------------------------------------

        private void LoadProfileSummary()
        {
            if (_ownerProfileService == null)
            {
                ProfileSummaryText.Text = "Not available";
                EditProfileButton.IsEnabled = false;
                return;
            }

            var profile = _ownerProfileService.GetProfile();
            if (profile == null || string.IsNullOrWhiteSpace(profile.FullName))
            {
                ProfileSummaryText.Text = "Not configured";
            }
            else
            {
                var summary = $"{profile.FullName} <{profile.Email}>";
                if (!string.IsNullOrWhiteSpace(profile.GitHubUsername))
                    summary += $"  |  GitHub: {profile.GitHubUsername}";
                if (profile.HasGitHubToken)
                    summary += "  |  Token: Set";
                ProfileSummaryText.Text = summary;
            }
        }

        private void EditProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ownerProfileService == null) return;

            var dialog = new OwnerProfileDialog();
            dialog.Owner = this;

            var existing = _ownerProfileService.GetProfile();
            if (existing != null)
            {
                dialog.LoadExisting(existing.FullName, existing.Email,
                    existing.GitHubUsername, existing.HasGitHubToken);
            }

            if (dialog.ShowDialog() == true)
            {
                var profile = existing ?? new Models.OwnerProfile();
                profile.FullName = dialog.FullName;
                profile.Email = dialog.Email;
                profile.GitHubUsername = string.IsNullOrWhiteSpace(dialog.GitHubUsername)
                    ? null : dialog.GitHubUsername;

                _ownerProfileService.SaveProfile(profile);

                var token = dialog.GitHubToken;
                if (!string.IsNullOrEmpty(token) && token != "placeholder-existing")
                {
                    _ownerProfileService.SaveGitHubToken(token);
                }

                OwnerProfileEdited = true;
                LoadProfileSummary();
            }
        }

        // -----------------------------------------------------------------
        // Bottom bar buttons
        // -----------------------------------------------------------------

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }

    // =====================================================================
    // Small WPF sub-dialog for entering a command string
    // =====================================================================

    /// <summary>
    /// Minimal dark-themed input dialog: label, TextBox, OK/Cancel.
    /// </summary>
    internal class CommandInputDialog : Window
    {
        private readonly TextBox _textBox;

        /// <summary>The trimmed text the user entered, or null if cancelled.</summary>
        public string EnteredValue { get; private set; }

        public CommandInputDialog(string title, string currentValue)
        {
            Title = title;
            Width = 400;
            Height = 160;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30));

            // Root grid
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Label
            var label = new TextBlock
            {
                Text = "Command:",
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xA0)),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(16, 16, 16, 6)
            };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            // TextBox
            _textBox = new TextBox
            {
                Text = currentValue ?? "",
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13,
                Margin = new Thickness(16, 0, 16, 0),
                CaretBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
            };
            Grid.SetRow(_textBox, 1);
            root.Children.Add(_textBox);

            // Bottom bar with OK / Cancel
            var bottomBar = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x26)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42)),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            Grid.SetRow(bottomBar, 2);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 8, 12, 8)
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 70,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xCC)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            okBtn.Click += (s, e) => { EnteredValue = _textBox.Text.Trim(); DialogResult = true; };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 70,
                Height = 28,
                IsCancel = true,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; };

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            bottomBar.Child = buttonPanel;
            root.Children.Add(bottomBar);

            Content = root;
            _textBox.Focus();
            _textBox.SelectAll();
        }
    }
}
