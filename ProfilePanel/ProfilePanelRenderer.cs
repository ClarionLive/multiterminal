using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.ProfilePanel
{
    /// <summary>
    /// WebView2-based renderer for the Profile Panel.
    /// Displays team member profiles with avatars, skills, interests, and bio.
    /// </summary>
    public class ProfilePanelRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private readonly Queue<string> _pendingMessages = new();
        private bool _isDarkTheme = true;

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when user requests to create a profile.
        /// </summary>
        public event EventHandler<TeamMemberProfile> CreateProfileRequested;

        /// <summary>
        /// Event fired when user requests to update a profile.
        /// </summary>
        public event EventHandler<TeamMemberProfile> UpdateProfileRequested;

        /// <summary>
        /// Event fired when user requests to delete a profile.
        /// </summary>
        public event EventHandler<string> DeleteProfileRequested;

        /// <summary>
        /// Event fired when user requests to load profiles.
        /// </summary>
        public event EventHandler GetProfilesRequested;

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        public DebugLogService DebugLogService { get; set; }

        public ProfilePanelRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "ProfilePanelRenderer";
            Size = new Size(400, 500);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetPanelHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("Panel HTML file not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private string GetPanelHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "ProfilePanel", "panel.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "ProfilePanel", "panel.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                _isInitialized = true;
                _isInitializing = false;

                // Send theme immediately
                SetTheme(_isDarkTheme);

                // Send any pending messages
                while (_pendingMessages.Count > 0)
                {
                    string msg = _pendingMessages.Dequeue();
                    PostMessageToPage(msg);
                }

                Ready?.Invoke(this, EventArgs.Empty);
                DebugLogService?.Trace("ProfilePanel", "Navigation completed, panel ready");
            }
            else
            {
                _isInitializing = false;
                ShowError($"Failed to load panel: {e.WebErrorStatus}");
                DebugLogService?.Error("ProfilePanel", $"Navigation failed: {e.WebErrorStatus}");
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                {
                    DebugLogService?.Warning("ProfilePanel", "Message missing 'type' property");
                    return;
                }

                string type = typeElement.GetString();
                DebugLogService?.Trace("ProfilePanel", $"Message received: {type}");

                switch (type)
                {
                    case "ready":
                        // Page signals it's ready
                        DebugLogService?.Trace("ProfilePanel", "Page ready signal received");
                        break;

                    case "getProfiles":
                        // Request all profiles
                        GetProfilesRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "createProfile":
                        // Parse profile data
                        if (root.TryGetProperty("profile", out var createProfileElement))
                        {
                            var profile = ParseProfile(createProfileElement);
                            CreateProfileRequested?.Invoke(this, profile);
                        }
                        break;

                    case "updateProfile":
                        // Parse profile data
                        if (root.TryGetProperty("profile", out var updateProfileElement))
                        {
                            var profile = ParseProfile(updateProfileElement);
                            UpdateProfileRequested?.Invoke(this, profile);
                        }
                        break;

                    case "deleteProfile":
                        // Get profile ID
                        if (root.TryGetProperty("id", out var idElement))
                        {
                            string id = idElement.GetString();
                            DeleteProfileRequested?.Invoke(this, id);
                        }
                        break;

                    default:
                        DebugLogService?.Warning("ProfilePanel", $"Unknown message type: {type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error processing message: {ex.Message}");
            }
        }

        private TeamMemberProfile ParseProfile(JsonElement element)
        {
            var profile = new TeamMemberProfile
            {
                Id = element.TryGetProperty("id", out var id) ? id.GetString() : null,
                DisplayName = element.TryGetProperty("displayName", out var displayName) ? displayName.GetString() : null,
                Role = element.TryGetProperty("role", out var role) ? role.GetString() : null,
                AvatarUrl = element.TryGetProperty("avatarUrl", out var avatarUrl) ? avatarUrl.GetString() : null,
                Bio = element.TryGetProperty("bio", out var bio) ? bio.GetString() : null
            };

            // Parse skills array
            if (element.TryGetProperty("skills", out var skillsElement) && skillsElement.ValueKind == JsonValueKind.Array)
            {
                var skills = skillsElement.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.String)
                    .Select(s => s.GetString())
                    .ToList();
                profile.SetSkills(skills);
            }

            // Parse interests array
            if (element.TryGetProperty("interests", out var interestsElement) && interestsElement.ValueKind == JsonValueKind.Array)
            {
                var interests = interestsElement.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => i.GetString())
                    .ToList();
                profile.SetInterests(interests);
            }

            // Parse agent fields
            if (element.TryGetProperty("agentInstructions", out var instrEl) && instrEl.ValueKind == JsonValueKind.String)
                profile.AgentInstructions = instrEl.GetString();
            if (element.TryGetProperty("preferredModel", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                profile.PreferredModel = modelEl.GetString();
            if (element.TryGetProperty("isTeamLead", out var teamLeadEl) &&
                (teamLeadEl.ValueKind == JsonValueKind.True || teamLeadEl.ValueKind == JsonValueKind.False))
                profile.IsTeamLead = teamLeadEl.GetBoolean();

            return profile;
        }

        /// <summary>
        /// Send all profiles to the page.
        /// </summary>
        public void SetProfiles(List<TeamMemberProfile> profiles)
        {
            if (!_isInitialized)
            {
                DebugLogService?.Trace("ProfilePanel", "Not initialized yet, queueing profiles");
                return;
            }

            try
            {
                // Convert profiles to JSON-friendly format
                var profileData = profiles.Select(p => new
                {
                    id = p.Id,
                    displayName = p.DisplayName,
                    role = p.Role,
                    avatarUrl = p.AvatarUrl,
                    bio = p.Bio,
                    skills = p.GetSkills(),
                    interests = p.GetInterests(),
                    agentInstructions = p.AgentInstructions,
                    preferredModel = p.PreferredModel,
                    isTeamLead = p.IsTeamLead
                }).ToList();

                string json = JsonSerializer.Serialize(profileData);
                PostMessageToPage($"profiles:{json}");
                DebugLogService?.Trace("ProfilePanel", $"Sent {profiles.Count} profiles to page");
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error sending profiles: {ex.Message}");
            }
        }

        /// <summary>
        /// Notify page that a profile was created.
        /// </summary>
        public void NotifyProfileCreated(TeamMemberProfile profile)
        {
            if (!_isInitialized) return;

            try
            {
                var profileData = new
                {
                    id = profile.Id,
                    displayName = profile.DisplayName,
                    role = profile.Role,
                    avatarUrl = profile.AvatarUrl,
                    bio = profile.Bio,
                    skills = profile.GetSkills(),
                    interests = profile.GetInterests(),
                    agentInstructions = profile.AgentInstructions,
                    preferredModel = profile.PreferredModel,
                    isTeamLead = profile.IsTeamLead
                };

                string json = JsonSerializer.Serialize(profileData);
                PostMessageToPage($"profileCreated:{json}");
                DebugLogService?.Trace("ProfilePanel", $"Notified profile created: {profile.Id}");
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error notifying profile created: {ex.Message}");
            }
        }

        /// <summary>
        /// Notify page that a profile was updated.
        /// </summary>
        public void NotifyProfileUpdated(TeamMemberProfile profile)
        {
            if (!_isInitialized) return;

            try
            {
                var profileData = new
                {
                    id = profile.Id,
                    displayName = profile.DisplayName,
                    role = profile.Role,
                    avatarUrl = profile.AvatarUrl,
                    bio = profile.Bio,
                    skills = profile.GetSkills(),
                    interests = profile.GetInterests(),
                    agentInstructions = profile.AgentInstructions,
                    preferredModel = profile.PreferredModel,
                    isTeamLead = profile.IsTeamLead
                };

                string json = JsonSerializer.Serialize(profileData);
                PostMessageToPage($"profileUpdated:{json}");
                DebugLogService?.Trace("ProfilePanel", $"Notified profile updated: {profile.Id}");
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error notifying profile updated: {ex.Message}");
            }
        }

        /// <summary>
        /// Notify page that a profile was deleted.
        /// </summary>
        public void NotifyProfileDeleted(string id)
        {
            if (!_isInitialized) return;

            try
            {
                PostMessageToPage($"profileDeleted:{id}");
                DebugLogService?.Trace("ProfilePanel", $"Notified profile deleted: {id}");
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error notifying profile deleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the theme (light or dark).
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            PostMessageToPage($"theme:{(isDark ? "dark" : "light")}");
        }

        private void PostMessageToPage(string message)
        {
            if (!_isInitialized)
            {
                _pendingMessages.Enqueue(message);
                return;
            }

            try
            {
                _webView?.CoreWebView2?.PostWebMessageAsString(message);
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ProfilePanel", $"Error posting message: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    _webView.CoreWebView2?.Stop();
                    _webView.Dispose();
                    _webView = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
