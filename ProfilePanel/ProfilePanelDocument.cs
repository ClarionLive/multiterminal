using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.ProfilePanel
{
    /// <summary>
    /// Dockable document for the Profile Panel - Team member profiles.
    /// </summary>
    public class ProfilePanelDocument : DockContent
    {
        private ProfilePanelRenderer _renderer;
        private MessageBroker _messageBroker;
        private bool _isDarkTheme = true;
        private bool _hasLoadedInitialData = false;

        public ProfilePanelDocument()
        {
            InitializeComponent();

            // Hook visibility change to handle lazy-loading when panel is first shown
            this.DockStateChanged += OnDockStateChanged;
        }

        private void InitializeComponent()
        {
            Text = "Team Profiles";
            TabText = "Profiles";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening

            _renderer = new ProfilePanelRenderer
            {
                Dock = DockStyle.Fill
            };

            // Subscribe to renderer events
            _renderer.Ready += OnRendererReady;
            _renderer.CreateProfileRequested += OnCreateProfileRequested;
            _renderer.UpdateProfileRequested += OnUpdateProfileRequested;
            _renderer.DeleteProfileRequested += OnDeleteProfileRequested;
            _renderer.GetProfilesRequested += OnGetProfilesRequested;

            Controls.Add(_renderer);
        }

        private void OnRendererReady(object sender, EventArgs e)
        {
            System.Diagnostics.Trace.WriteLine("[ProfilePanel] OnRendererReady fired");

            // WebView2 is now ready - load initial data
            LoadProfiles();
        }

        private void OnDockStateChanged(object sender, EventArgs e)
        {
            // When panel becomes visible for the first time, ensure data is loaded
            if (!_hasLoadedInitialData && this.Visible && _renderer?.IsInitialized == true)
            {
                LoadProfiles();
            }
        }

        /// <summary>
        /// Set the message broker for profile operations.
        /// </summary>
        public void SetMessageBroker(MessageBroker messageBroker)
        {
            if (_messageBroker != null)
            {
                // Unsubscribe from old broker
                _messageBroker.ProfilesUpdated -= OnProfilesUpdated;
            }

            _messageBroker = messageBroker;

            if (_messageBroker != null)
            {
                // Subscribe to profile updates
                _messageBroker.ProfilesUpdated += OnProfilesUpdated;
                System.Diagnostics.Trace.WriteLine("[ProfilePanel] Subscribed to MessageBroker.ProfilesUpdated");
            }
        }

        /// <summary>
        /// Set the theme (light or dark).
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            _renderer?.SetTheme(isDark);
        }

        /// <summary>
        /// Load all profiles from the message broker.
        /// </summary>
        private void LoadProfiles()
        {
            if (_messageBroker == null)
            {
                System.Diagnostics.Trace.WriteLine("[ProfilePanel] MessageBroker not set, cannot load profiles");
                return;
            }

            if (!_renderer.IsInitialized)
            {
                System.Diagnostics.Trace.WriteLine("[ProfilePanel] Renderer not initialized yet");
                return;
            }

            try
            {
                var result = _messageBroker.ListProfiles();
                if (result.Success)
                {
                    _renderer.SetProfiles(result.Profiles);
                    _hasLoadedInitialData = true;
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Loaded {result.Profiles.Count} profiles");
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Failed to load profiles: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Error loading profiles: {ex.Message}");
            }
        }

        // ============================================
        // Event Handlers - Renderer Requests
        // ============================================

        private void OnCreateProfileRequested(object sender, TeamMemberProfile profile)
        {
            if (_messageBroker == null) return;

            try
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Create profile requested: {profile.Id}");
                var result = _messageBroker.CreateProfile(
                    profile.Id,
                    profile.DisplayName,
                    profile.AvatarUrl,
                    profile.Role,
                    profile.Bio,
                    profile.GetSkills(),
                    profile.GetInterests(),
                    profile.GetProjectIds(),
                    profile.AgentInstructions,
                    profile.PreferredModel,
                    profile.IsTeamLead
                );

                if (result.Success)
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Profile created: {profile.Id}");
                    // ProfilesUpdated event will auto-refresh the UI
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Failed to create profile: {result.Error}");
                    MessageBox.Show($"Failed to create profile: {result.Error}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Error creating profile: {ex.Message}");
                MessageBox.Show($"Error creating profile: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnUpdateProfileRequested(object sender, TeamMemberProfile profile)
        {
            if (_messageBroker == null) return;

            try
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Update profile requested: {profile.Id}");
                var result = _messageBroker.UpdateProfile(
                    profile.Id,
                    profile.DisplayName,
                    profile.AvatarUrl,
                    profile.Role,
                    profile.Bio,
                    profile.GetSkills(),
                    profile.GetInterests(),
                    profile.GetProjectIds(),
                    profile.AgentInstructions,
                    profile.PreferredModel,
                    profile.IsTeamLead
                );

                if (result.Success)
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Profile updated: {profile.Id}");
                    // ProfilesUpdated event will auto-refresh the UI
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Failed to update profile: {result.Error}");
                    MessageBox.Show($"Failed to update profile: {result.Error}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Error updating profile: {ex.Message}");
                MessageBox.Show($"Error updating profile: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDeleteProfileRequested(object sender, string id)
        {
            if (_messageBroker == null) return;

            try
            {
                var confirmResult = MessageBox.Show(
                    $"Are you sure you want to delete the profile for {id}?",
                    "Confirm Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirmResult != DialogResult.Yes)
                    return;

                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Delete profile requested: {id}");
                var result = _messageBroker.DeleteProfile(id);

                if (result.Success)
                {
                    _renderer.NotifyProfileDeleted(id);
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Profile deleted: {id}");
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Failed to delete profile: {result.Error}");
                    MessageBox.Show($"Failed to delete profile: {result.Error}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ProfilePanel] Error deleting profile: {ex.Message}");
                MessageBox.Show($"Error deleting profile: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnGetProfilesRequested(object sender, EventArgs e)
        {
            LoadProfiles();
        }

        // ============================================
        // Event Handlers - MessageBroker Updates
        // ============================================

        private void OnProfilesUpdated(object sender, List<TeamMemberProfile> profiles)
        {
            // Profiles were updated externally - reload the list
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => OnProfilesUpdated(sender, profiles)));
                return;
            }

            System.Diagnostics.Trace.WriteLine("[ProfilePanel] ProfilesUpdated event received");
            LoadProfiles();
        }

        // ============================================
        // Cleanup
        // ============================================

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_messageBroker != null)
                {
                    _messageBroker.ProfilesUpdated -= OnProfilesUpdated;
                }

                _renderer?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
