using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

using MultiTerminal.API.Gateway;
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
        private readonly SourceControlAccountService _sourceControlAccountService;
        private List<ClaudeCommand> _commands;

        // -------- Multi-Connect tab state (task 642c14e3, item 6) --------
        // Masked placeholder shown in a secret PasswordBox when a secret is already stored. It is
        // NEVER the real secret — the raw value is never loaded into the UI. A secret is only written
        // back when the user actually edits its box this session (the dirty flags below), so an
        // untouched placeholder can never clobber the stored DPAPI secret.
        private const string SecretPlaceholder = "••••••••";
        // Short-timeout client for the "Test connection" /health probe — reused to avoid socket churn.
        private static readonly HttpClient _mcHealthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private bool _mcLoading;                    // true while LoadMultiConnect populates controls (suppresses dirty tracking)
        private bool _mcPhonePasswordDirty;         // user edited the phone-auth password box this session
        private bool _mcNotificationSecretDirty;    // user edited the push notification secret box this session
        private bool _mcRelayApiKeyDirty;           // user edited the relay ApiKey box this session
        private bool _mcRestartNeeded;              // a restart-required field changed during this save

        // Effective values captured at load, so save only writes a field the user actually changed
        // (leaving an unchanged appsettings/default value as a fallback rather than shadowing it).
        private string _mcOrigGatewayPort = "";
        private string _mcOrigServePort = "";
        private bool _mcOrigTailscaleEnabled;
        private string _mcOrigHostname = "";
        private string _mcOrigUsername = "";
        private string _mcOrigVapidSubject = "";
        private string _mcOrigRelayBaseUrl = "";

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

        public SettingsWpfDialog(SettingsService settings, bool isDark,
            OwnerProfileService ownerProfileService = null,
            SourceControlAccountService sourceControlAccountService = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ownerProfileService = ownerProfileService;
            _sourceControlAccountService = sourceControlAccountService;
            _commands = new List<ClaudeCommand>();

            InitializeComponent();

            PopulateComboBoxes();
            LoadSettings();
            LoadProfileSummary();
            LoadMultiConnect();

            // Source control accounts manager is only available when its service is injected.
            ManageSourceControlAccountsButton.IsEnabled = _sourceControlAccountService != null;
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

                SaveMultiConnect();
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

        // The DataGrid hosts its own ScrollViewer which would otherwise swallow the mouse
        // wheel and block scrolling of the outer (General-tab) ScrollViewer. Re-raise the
        // wheel event on the grid's parent so it bubbles up to the form's ScrollViewer,
        // keeping the Source Control Accounts section below the grid reachable by wheel.
        private void CommandsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            };

            if (((FrameworkElement)sender).Parent is UIElement parent)
            {
                parent.RaiseEvent(forwarded);
            }
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
                ProfileSummaryText.Text = $"{profile.FullName} <{profile.Email}>";
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
                dialog.LoadExisting(existing.FullName, existing.Email);
            }

            if (dialog.ShowDialog() == true)
            {
                var profile = existing ?? new Models.OwnerProfile();
                profile.FullName = dialog.FullName;
                profile.Email = dialog.Email;

                _ownerProfileService.SaveProfile(profile);

                OwnerProfileEdited = true;
                LoadProfileSummary();
            }
        }

        // -----------------------------------------------------------------
        // Source Control Accounts
        // -----------------------------------------------------------------

        private void ManageSourceControlAccountsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sourceControlAccountService == null) return;

            var dialog = new SourceControlAccountsDialog(_sourceControlAccountService);
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        // -----------------------------------------------------------------
        // Bottom bar buttons
        // -----------------------------------------------------------------

        private async void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate Multi-Connect fields up front so a bad port/URL never half-writes settings.
            string mcError = ValidateMultiConnect();
            if (mcError != null)
            {
                MessageBox.Show(this, mcError, "Multi-Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveSettings();

            // If a restart-required Multi-Connect field changed, re-apply the gateway in-process so
            // the new port / VapidSubject / NotificationSecret / relay BaseUrl take effect without a
            // full app relaunch. The restart is what applies these — it is not automatic elsewhere.
            if (_mcRestartNeeded)
            {
                var restarter = MultiConnectConfig.GatewayRestarter;
                if (restarter != null)
                {
                    // Disable BOTH buttons for the duration of the awaited restart. The modal pump
                    // keeps running during the ~1-3s await; if the user could click Cancel (or the
                    // window X) mid-await, the continuation below would assign DialogResult on a
                    // closed window → InvalidOperationException from an async-void handler → crash.
                    OkButton.IsEnabled = false;
                    CancelBtn.IsEnabled = false;
                    try
                    {
                        MC_TailscaleStatusText.Text = "Restarting gateway…";
                        await restarter().ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            "Settings saved, but the gateway restart failed:\n" + ex.Message +
                            "\n\nRestart MultiTerminal to apply the restart-required changes.",
                            "Multi-Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally
                    {
                        OkButton.IsEnabled = true;
                        CancelBtn.IsEnabled = true;
                    }
                }
            }

            // Belt-and-suspenders: even with the buttons disabled above, the window could have been
            // force-closed during the await (e.g. app shutdown). Assigning DialogResult on a closed
            // window throws, so only set it while the dialog is still loaded and visible.
            if (IsLoaded && IsVisible)
                DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // =================================================================
        // Multi-Connect tab (task 642c14e3, item 6)
        // In-process: reads SettingsService + MultiConnectConfig + TailscaleService directly
        // (no HTTP to :5050 from the UI). Mirrors how MultiConnectController computes the
        // effective value + source for each field.
        // =================================================================

        private void LoadMultiConnect()
        {
            _mcLoading = true;
            try
            {
                // Gateway port: settings int → appsettings MultiRemote:Port → default 5100.
                int? settingsPort = _settings.GetMultiConnectGatewayPort();
                string cfgPort = MultiConnectConfig.Appsettings[MultiConnectConfig.CfgPort];
                int effectivePort;
                string portSource;
                if (settingsPort.HasValue) { effectivePort = settingsPort.Value; portSource = "settings"; }
                else if (int.TryParse(cfgPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cp)) { effectivePort = cp; portSource = "appsettings"; }
                else { effectivePort = SettingsService.DefaultMultiConnectGatewayPort; portSource = "default"; }
                MC_GatewayPortText.Text = effectivePort.ToString(CultureInfo.InvariantCulture);
                MC_GatewayPortSource.Text = SourceHint(portSource);
                _mcOrigGatewayPort = MC_GatewayPortText.Text;

                // Tailscale serve port: settings → default 443 (no appsettings fallback).
                int? settingsServePort = _settings.GetMultiConnectTailscaleServePort();
                int effectiveServePort = settingsServePort ?? SettingsService.DefaultMultiConnectTailscaleServePort;
                MC_ServePortText.Text = effectiveServePort.ToString(CultureInfo.InvariantCulture);
                MC_ServePortSource.Text = SourceHint(settingsServePort.HasValue ? "settings" : "default");
                _mcOrigServePort = MC_ServePortText.Text;

                // Tailscale enabled: settings → default false.
                bool? tsEnabled = _settings.GetMultiConnectTailscaleEnabled();
                bool effectiveEnabled = tsEnabled ?? false;
                MC_TailscaleEnabledCheck.IsChecked = effectiveEnabled;
                _mcOrigTailscaleEnabled = effectiveEnabled;

                var hostname = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectTailscaleHostname(), null);
                MC_TailscaleHostnameText.Text = hostname.value ?? "";
                MC_TailscaleHostnameSource.Text = SourceHint(hostname.source);
                _mcOrigHostname = MC_TailscaleHostnameText.Text;

                var username = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectPhoneAuthUsername(), MultiConnectConfig.CfgAuthUsername);
                MC_PhoneAuthUsernameText.Text = username.value ?? "";
                MC_PhoneAuthUsernameSource.Text = SourceHint(username.source);
                _mcOrigUsername = MC_PhoneAuthUsernameText.Text;

                var vapid = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectVapidSubject(), MultiConnectConfig.CfgVapidSubject);
                MC_VapidSubjectText.Text = vapid.value ?? "";
                MC_VapidSubjectSource.Text = SourceHint(vapid.source);
                _mcOrigVapidSubject = MC_VapidSubjectText.Text;

                var relayBase = MultiConnectConfig.ResolveWithSource(_settings.GetMultiConnectRelayBaseUrl(), MultiConnectConfig.CfgRelayBaseUrl);
                MC_RelayBaseUrlText.Text = relayBase.value ?? "";
                MC_RelayBaseUrlSource.Text = SourceHint(relayBase.source);
                _mcOrigRelayBaseUrl = MC_RelayBaseUrlText.Text;

                // Secrets: NEVER load the raw value — show masked dots when one is stored, else blank.
                var phonePassword = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectPhoneAuthPassword(), MultiConnectConfig.CfgAuthPassword);
                MC_PhoneAuthPasswordBox.Password = phonePassword.isSet ? SecretPlaceholder : "";
                MC_PhoneAuthPasswordSource.Text = SourceHint(phonePassword.source);
                _mcPhonePasswordDirty = false;

                var notifSecret = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectNotificationSecret(), MultiConnectConfig.CfgNotificationSecret);
                MC_NotificationSecretBox.Password = notifSecret.isSet ? SecretPlaceholder : "";
                MC_NotificationSecretSource.Text = SourceHint(notifSecret.source);
                _mcNotificationSecretDirty = false;

                var relayApiKey = MultiConnectConfig.ResolveSecretSource(_settings.HasMultiConnectRelayApiKey(), MultiConnectConfig.CfgRelayApiKey);
                MC_RelayApiKeyBox.Password = relayApiKey.isSet ? SecretPlaceholder : "";
                MC_RelayApiKeySource.Text = SourceHint(relayApiKey.source);
                _mcRelayApiKeyDirty = false;

                UpdatePhoneUrl();
            }
            finally
            {
                _mcLoading = false;
            }
        }

        private void SaveMultiConnect()
        {
            _mcRestartNeeded = false;

            // Non-secret fields: write only when the user changed the value (so an unchanged
            // appsettings/default value is left as a fallback, never shadowed by a settings copy).
            // A cleared field routes through the setter's blank → Remove() path. The restartRequired
            // flag marks the read-once gateway fields (port, VapidSubject, relay BaseUrl); the rest
            // (serve port, hostname, username) are applied by the tailscale serve proxy / per-request.
            SavePortIfChanged(MC_GatewayPortText, _mcOrigGatewayPort, _settings.SetMultiConnectGatewayPort, restartRequired: true);
            SavePortIfChanged(MC_ServePortText, _mcOrigServePort, _settings.SetMultiConnectTailscaleServePort);

            bool enabled = MC_TailscaleEnabledCheck.IsChecked == true;
            if (enabled != _mcOrigTailscaleEnabled)
                _settings.SetMultiConnectTailscaleEnabled(enabled);

            SaveStringIfChanged(MC_TailscaleHostnameText, _mcOrigHostname, _settings.SetMultiConnectTailscaleHostname);
            SaveStringIfChanged(MC_PhoneAuthUsernameText, _mcOrigUsername, _settings.SetMultiConnectPhoneAuthUsername);
            SaveStringIfChanged(MC_VapidSubjectText, _mcOrigVapidSubject, _settings.SetMultiConnectVapidSubject, restartRequired: true);
            SaveStringIfChanged(MC_RelayBaseUrlText, _mcOrigRelayBaseUrl, _settings.SetMultiConnectRelayBaseUrl, restartRequired: true);

            // Secrets: write ONLY when the box was edited this session. An untouched masked
            // placeholder is skipped entirely, so the stored DPAPI secret is never clobbered.
            // An explicit clear (dirty + blank) routes through the setter's Remove() path.
            if (_mcPhonePasswordDirty)
                _settings.SetMultiConnectPhoneAuthPassword(MC_PhoneAuthPasswordBox.Password); // auth, not restart-required

            if (_mcNotificationSecretDirty)
            {
                _settings.SetMultiConnectNotificationSecret(MC_NotificationSecretBox.Password);
                _mcRestartNeeded = true; // NotificationSecret is restart-required
            }

            if (_mcRelayApiKeyDirty)
                _settings.SetMultiConnectRelayApiKey(MC_RelayApiKeyBox.Password); // relay ApiKey read per-forward, not restart-required
        }

        /// <summary>Writes a trimmed text field only when it differs from its loaded value (blank → setter clears).</summary>
        private void SaveStringIfChanged(TextBox box, string original, Action<string> setter, bool restartRequired = false)
        {
            string value = box.Text?.Trim() ?? "";
            if (string.Equals(value, original, StringComparison.Ordinal)) return;
            setter(value);
            if (restartRequired) _mcRestartNeeded = true;
        }

        /// <summary>Writes a port field only when it changed: blank → null (clears), valid int → that port.</summary>
        private void SavePortIfChanged(TextBox box, string original, Action<int?> setter, bool restartRequired = false)
        {
            string text = box.Text?.Trim() ?? "";
            if (string.Equals(text, original, StringComparison.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(text))
                setter(null);
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                setter(port);
            if (restartRequired) _mcRestartNeeded = true;
        }

        private string ValidateMultiConnect()
        {
            string portText = MC_GatewayPortText.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(portText) && !IsValidPort(portText))
                return "Gateway port must be an integer between 1 and 65535.";

            string serveText = MC_ServePortText.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(serveText) && !IsValidPort(serveText))
                return "Tailscale serve port must be an integer between 1 and 65535.";

            string vapid = MC_VapidSubjectText.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(vapid) && !IsValidVapidSubject(vapid))
                return "VAPID subject must be a mailto: address or an http(s) URL.";

            string relay = MC_RelayBaseUrlText.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(relay) && !IsAbsoluteHttpUrl(relay))
                return "Relay BaseUrl must be an absolute http(s) URL.";

            return null;
        }

        private static bool IsValidPort(string raw) =>
            int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535;

        private static bool IsValidVapidSubject(string value)
        {
            string v = value.Trim();
            if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return v.Length > "mailto:".Length;
            return IsAbsoluteHttpUrl(v);
        }

        private static bool IsAbsoluteHttpUrl(string value)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
                return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private static string SourceHint(string source) => source switch
        {
            "settings" => "(source: settings)",
            "appsettings" => "(source: appsettings)",
            "default" => "(source: default)",
            _ => "(source: not set)",
        };

        // ---- Secret PasswordBox dirty-tracking + select-on-focus ----

        private void MC_Secret_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_mcLoading) return; // programmatic load, not a user edit
            if (sender == MC_PhoneAuthPasswordBox) _mcPhonePasswordDirty = true;
            else if (sender == MC_NotificationSecretBox) _mcNotificationSecretDirty = true;
            else if (sender == MC_RelayApiKeyBox) _mcRelayApiKeyDirty = true;
        }

        // Select the whole (placeholder) contents on focus so the first keystroke replaces it —
        // the user never edits "into" the dots and produces a garbage secret.
        private void MC_Secret_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            (sender as PasswordBox)?.SelectAll();
        }

        private void MC_Secret_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is PasswordBox pb && !pb.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                pb.Focus(); // routes to GotKeyboardFocus → SelectAll
            }
        }

        // ---- Phone URL (mirrors MultiConnectController: omit :443, else :<port>) ----

        private void MC_RecomputeUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_mcLoading) return;
            UpdatePhoneUrl();
        }

        private void UpdatePhoneUrl()
        {
            string host = MC_TailscaleHostnameText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                MC_PhoneUrlText.Text = "";
                return;
            }

            string serveText = MC_ServePortText.Text?.Trim();
            int serve = IsValidPort(serveText)
                ? int.Parse(serveText, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : SettingsService.DefaultMultiConnectTailscaleServePort;

            MC_PhoneUrlText.Text = serve == 443 ? $"https://{host}" : $"https://{host}:{serve}";
        }

        // ---- Buttons ----

        private async void MC_DetectButton_Click(object sender, RoutedEventArgs e)
        {
            MC_DetectButton.IsEnabled = false;
            MC_TailscaleStatusText.Text = "Detecting Tailscale…";
            try
            {
                var status = await TailscaleService.GetStatusAsync().ConfigureAwait(true);
                if (!status.Installed)
                {
                    MC_TailscaleStatusText.Text = "Could not detect Tailscale: " +
                        (status.Error ?? "tailscale.exe not found.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(status.Hostname))
                {
                    MC_TailscaleHostnameText.Text = status.Hostname; // TextChanged recomputes the phone URL
                    MC_TailscaleHostnameSource.Text = "(source: detected — unsaved)";
                }

                string line = $"Installed: yes   Running: {(status.Running ? "yes" : "no")}   BackendState: {status.BackendState ?? "unknown"}";
                if (!string.IsNullOrWhiteSpace(status.Error))
                    line += "\n" + status.Error;
                MC_TailscaleStatusText.Text = line;
            }
            catch (Exception ex)
            {
                MC_TailscaleStatusText.Text = "Detect failed: " + ex.Message;
            }
            finally
            {
                MC_DetectButton.IsEnabled = true;
            }
        }

        private async void MC_TestButton_Click(object sender, RoutedEventArgs e)
        {
            string portText = MC_GatewayPortText.Text?.Trim();
            int port = IsValidPort(portText)
                ? int.Parse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : SettingsService.DefaultMultiConnectGatewayPort;

            MC_TestButton.IsEnabled = false;
            MC_TailscaleStatusText.Text = $"Testing gateway /health on port {port}…";
            try
            {
                using var resp = await _mcHealthClient
                    .GetAsync($"http://localhost:{port}/health")
                    .ConfigureAwait(true);
                MC_TailscaleStatusText.Text = resp.IsSuccessStatusCode
                    ? $"Gateway OK — /health returned HTTP {(int)resp.StatusCode} on port {port}."
                    : $"Gateway reachable but /health returned HTTP {(int)resp.StatusCode} on port {port}.";
            }
            catch (Exception ex)
            {
                MC_TailscaleStatusText.Text =
                    $"Gateway not reachable on port {port}: {ex.Message}";
            }
            finally
            {
                MC_TestButton.IsEnabled = true;
            }
        }

        private void MC_CopyUrlButton_Click(object sender, RoutedEventArgs e)
        {
            string url = MC_PhoneUrlText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MC_TailscaleStatusText.Text = "No phone URL yet — set a Tailscale hostname first.";
                return;
            }

            try
            {
                Clipboard.SetText(url);
                MC_TailscaleStatusText.Text = "Phone URL copied to clipboard.";
            }
            catch (Exception ex)
            {
                MC_TailscaleStatusText.Text = "Copy failed: " + ex.Message;
            }
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
