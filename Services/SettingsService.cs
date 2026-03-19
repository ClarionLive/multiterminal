using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Persists user settings in AppData folder.
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        private Dictionary<string, string> _settings;
        private bool _batchMode;

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiTerminal");
            try
            {
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to create settings folder: {ex.Message}");
            }
            _settingsPath = Path.Combine(folder, "settings.txt");
            _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        public string Get(string key) => _settings.TryGetValue(key ?? "", out var v) ? v : null;

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _settings[key] = value ?? "";
            if (!_batchMode) Save();
        }

        public void Remove(string key)
        {
            if (_settings.Remove(key ?? ""))
            {
                if (!_batchMode) Save();
            }
        }

        /// <summary>
        /// Begins batch mode - saves are deferred until EndBatch is called.
        /// Use this when making multiple settings changes to avoid repeated disk writes.
        /// </summary>
        public void BeginBatch() => _batchMode = true;

        /// <summary>
        /// Ends batch mode and saves all pending changes.
        /// </summary>
        public void EndBatch()
        {
            _batchMode = false;
            Save();
        }

        private const string LastDirectoryKey = "LastDirectory";
        private const string DefaultWorkingDirectoryKey = "DefaultWorkingDirectory";
        private const string RecentDirectoriesKey = "RecentDirectories";
        private const int MaxRecentDirectories = 10;

        // UI Font Size settings
        private const string ToolbarFontSizeKey = "ToolbarFontSize";
        private const string TabFontSizeKey = "TabFontSize";
        private const float DefaultUIFontSize = 9f;
        private const float MinUIFontSize = 8f;
        private const float MaxUIFontSize = 14f;

        // Terminal font size settings
        private const string TerminalFontSizeKey = "TerminalFontSize";
        private const float DefaultTerminalFontSize = 10f;
        private const float MinTerminalFontSize = 6f;
        private const float MaxTerminalFontSize = 32f;

        // Prompts panel font size settings
        private const string PromptsFontSizeKey = "PromptsFontSize";
        private const float DefaultPromptsFontSize = 9f;

        // Chat panel font size settings
        private const string ChatFontSizeKey = "ChatFontSize";
        private const float DefaultChatFontSize = 13f;

        // Tasks panel font size settings
        private const string TasksFontSizeKey = "TasksFontSize";
        private const float DefaultTasksFontSize = 13f;

        // Activity panel font size settings
        private const string ActivityFontSizeKey = "ActivityFontSize";
        private const float DefaultActivityFontSize = 13f;

        // WebView2 panel zoom settings
        private const string TasksPanelZoomKey = "TasksPanelZoom";
        private const string ChatPanelZoomKey = "ChatPanelZoom";
        private const string OfficePanelZoomKey = "OfficePanelZoom";
        private const double DefaultPanelZoom = 1.0;
        private const double MinPanelZoom = 0.25;
        private const double MaxPanelZoom = 5.0;

        // Terminal placement settings
        private const string MaxGridPanesKey = "MaxGridPanes";
        private const int DefaultMaxGridPanes = 4;
        private const string MaxTabsPerGridKey = "MaxTabsPerGrid";
        private const int DefaultMaxTabsPerGrid = 3;

        // Agent panel layout settings
        private const string AgentPanelLayoutKey = "AgentPanelLayout";
        private const string DefaultAgentPanelLayout = "SplitRight";

        // Agent panel close mode settings
        private const string AgentPanelCloseModeKey = "AgentPanelCloseMode";
        private const string DefaultAgentPanelCloseMode = "ManualClose";

        // Status bar height setting
        private const string StatusBarHeightKey = "StatusBarHeight";
        private const int DefaultStatusBarHeight = 140;
        private const int MinStatusBarHeight = 60;
        private const int MaxStatusBarHeight = 400;

        // Global agent/HUD layout settings (shared across all terminals)
        private const string AgentPanelZoomKey = "AgentPanelZoom";
        private const string TaskHudZoomKey = "TaskHudZoom";
        private const string AgentPanelSplitRatioKey = "AgentPanelSplitRatio";
        private const string HudSplitRatioKey = "HudSplitRatio";
        private const double DefaultAgentPanelZoom = 1.0;
        private const double DefaultTaskHudZoom = 1.0;
        private const double DefaultAgentPanelSplitRatio = 0.75;
        private const double DefaultHudSplitRatio = 0.60;
        private const double MinSplitRatio = 0.2;
        private const double MaxSplitRatio = 0.95;
        private const double MaxHudSplitRatio = 0.90; // Ensures HUD always gets at least 10% of container height

        // Claude commands settings
        private const string ClaudeCommandsKey = "ClaudeCommands";

        // Window bounds settings
        private const string WindowLeftKey = "WindowLeft";
        private const string WindowTopKey = "WindowTop";
        private const string WindowWidthKey = "WindowWidth";
        private const string WindowHeightKey = "WindowHeight";
        private const string WindowStateKey = "WindowState";

        // Lifecycle board window bounds settings
        private const string LifecycleBoardLeftKey = "LifecycleBoardLeft";
        private const string LifecycleBoardTopKey = "LifecycleBoardTop";
        private const string LifecycleBoardWidthKey = "LifecycleBoardWidth";
        private const string LifecycleBoardHeightKey = "LifecycleBoardHeight";

        /// <summary>
        /// Gets the toolbar font size (8-14pt range).
        /// </summary>
        public float GetToolbarFontSize()
        {
            string value = Get(ToolbarFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultUIFontSize;
        }

        /// <summary>
        /// Sets the toolbar font size.
        /// </summary>
        public void SetToolbarFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(ToolbarFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the tab control font size (8-14pt range).
        /// </summary>
        public float GetTabFontSize()
        {
            string value = Get(TabFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultUIFontSize;
        }

        /// <summary>
        /// Sets the tab control font size.
        /// </summary>
        public void SetTabFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(TabFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the terminal font size (6-32pt range).
        /// </summary>
        public float GetTerminalFontSize()
        {
            string value = Get(TerminalFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinTerminalFontSize, Math.Min(MaxTerminalFontSize, size));
            }
            return DefaultTerminalFontSize;
        }

        /// <summary>
        /// Sets the terminal font size.
        /// </summary>
        public void SetTerminalFontSize(float size)
        {
            size = Math.Max(MinTerminalFontSize, Math.Min(MaxTerminalFontSize, size));
            Set(TerminalFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the prompts panel font size (8-14pt range).
        /// </summary>
        public float GetPromptsFontSize()
        {
            string value = Get(PromptsFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultPromptsFontSize;
        }

        /// <summary>
        /// Sets the prompts panel font size.
        /// </summary>
        public void SetPromptsFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(PromptsFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the chat panel font size (8-14pt range).
        /// </summary>
        public float GetChatFontSize()
        {
            string value = Get(ChatFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultChatFontSize;
        }

        /// <summary>
        /// Sets the chat panel font size.
        /// </summary>
        public void SetChatFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(ChatFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the tasks panel font size (8-14pt range).
        /// </summary>
        public float GetTasksFontSize()
        {
            string value = Get(TasksFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultTasksFontSize;
        }

        /// <summary>
        /// Sets the tasks panel font size.
        /// </summary>
        public void SetTasksFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(TasksFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the activity panel font size (8-14pt range).
        /// </summary>
        public float GetActivityFontSize()
        {
            string value = Get(ActivityFontSizeKey);
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, out float size))
            {
                return Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            }
            return DefaultActivityFontSize;
        }

        /// <summary>
        /// Sets the activity panel font size.
        /// </summary>
        public void SetActivityFontSize(float size)
        {
            size = Math.Max(MinUIFontSize, Math.Min(MaxUIFontSize, size));
            Set(ActivityFontSizeKey, size.ToString("F1"));
        }

        /// <summary>
        /// Gets the tasks panel WebView2 zoom level (0.25-5.0 range).
        /// </summary>
        public double GetTasksPanelZoom()
        {
            string value = Get(TasksPanelZoomKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                return Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            }
            return DefaultPanelZoom;
        }

        /// <summary>
        /// Sets the tasks panel WebView2 zoom level.
        /// </summary>
        public void SetTasksPanelZoom(double zoom)
        {
            zoom = Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            Set(TasksPanelZoomKey, zoom.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the chat panel WebView2 zoom level (0.25-5.0 range).
        /// </summary>
        public double GetChatPanelZoom()
        {
            string value = Get(ChatPanelZoomKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                return Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            }
            return DefaultPanelZoom;
        }

        /// <summary>
        /// Sets the chat panel WebView2 zoom level.
        /// </summary>
        public void SetChatPanelZoom(double zoom)
        {
            zoom = Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            Set(ChatPanelZoomKey, zoom.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the office panel WebView2 zoom level (0.25-5.0 range).
        /// </summary>
        public double GetOfficePanelZoom()
        {
            string value = Get(OfficePanelZoomKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                return Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            }
            return DefaultPanelZoom;
        }

        /// <summary>
        /// Sets the office panel WebView2 zoom level.
        /// </summary>
        public void SetOfficePanelZoom(double zoom)
        {
            zoom = Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            Set(OfficePanelZoomKey, zoom.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the maximum number of grid panes (1-9).
        /// </summary>
        public int GetMaxGridPanes()
        {
            string value = Get(MaxGridPanesKey);
            if (int.TryParse(value, out int result))
                return Math.Max(1, Math.Min(9, result));
            return DefaultMaxGridPanes;
        }

        /// <summary>
        /// Sets the maximum number of grid panes (1-9).
        /// </summary>
        public void SetMaxGridPanes(int value)
        {
            Set(MaxGridPanesKey, Math.Max(1, Math.Min(9, value)).ToString());
        }

        /// <summary>
        /// Gets the maximum number of tabs per grid pane (1-10).
        /// </summary>
        public int GetMaxTabsPerGrid()
        {
            string value = Get(MaxTabsPerGridKey);
            if (int.TryParse(value, out int result))
                return Math.Max(1, Math.Min(10, result));
            return DefaultMaxTabsPerGrid;
        }

        /// <summary>
        /// Sets the maximum number of tabs per grid pane (1-10).
        /// </summary>
        public void SetMaxTabsPerGrid(int value)
        {
            Set(MaxTabsPerGridKey, Math.Max(1, Math.Min(10, value)).ToString());
        }

        /// <summary>
        /// Gets the agent panel layout mode.
        /// Valid values: "SplitRight", "SplitBelow", "TabbedRight", "DoNotShow"
        /// </summary>
        public string GetAgentPanelLayout()
        {
            string value = Get(AgentPanelLayoutKey);
            if (value == "SplitBelow" || value == "TabbedRight" || value == "DoNotShow")
                return value;
            return DefaultAgentPanelLayout;
        }

        /// <summary>
        /// Sets the agent panel layout mode.
        /// </summary>
        public void SetAgentPanelLayout(string mode)
        {
            Set(AgentPanelLayoutKey, mode ?? DefaultAgentPanelLayout);
        }

        /// <summary>
        /// Gets the agent panel close mode.
        /// Valid values: "AutoClose", "ManualClose"
        /// </summary>
        public string GetAgentPanelCloseMode()
        {
            string value = Get(AgentPanelCloseModeKey);
            if (value == "AutoClose")
                return "AutoClose";
            return DefaultAgentPanelCloseMode;
        }

        /// <summary>
        /// Sets the agent panel close mode.
        /// </summary>
        public void SetAgentPanelCloseMode(string mode)
        {
            Set(AgentPanelCloseModeKey, mode == "AutoClose" ? "AutoClose" : "ManualClose");
        }

        /// <summary>
        /// Gets the global agent panel WebView2 zoom level (shared across all terminals).
        /// </summary>
        public double GetAgentPanelZoom()
        {
            string value = Get(AgentPanelZoomKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                return Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            }
            return DefaultAgentPanelZoom;
        }

        /// <summary>
        /// Sets the global agent panel WebView2 zoom level.
        /// </summary>
        public void SetAgentPanelZoom(double zoom)
        {
            zoom = Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            Set(AgentPanelZoomKey, zoom.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the global task HUD WebView2 zoom level (shared across all terminals).
        /// </summary>
        public double GetTaskHudZoom()
        {
            string value = Get(TaskHudZoomKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                return Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            }
            return DefaultTaskHudZoom;
        }

        /// <summary>
        /// Sets the global task HUD WebView2 zoom level.
        /// </summary>
        public void SetTaskHudZoom(double zoom)
        {
            zoom = Math.Max(MinPanelZoom, Math.Min(MaxPanelZoom, zoom));
            Set(TaskHudZoomKey, zoom.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the global agent panel split ratio (terminal+HUD width vs agent panel width).
        /// Default 0.75 means terminal gets 75% of the width.
        /// </summary>
        public double GetAgentPanelSplitRatio()
        {
            string value = Get(AgentPanelSplitRatioKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
            {
                return Math.Max(MinSplitRatio, Math.Min(MaxSplitRatio, ratio));
            }
            return DefaultAgentPanelSplitRatio;
        }

        /// <summary>
        /// Sets the global agent panel split ratio.
        /// </summary>
        public void SetAgentPanelSplitRatio(double ratio)
        {
            ratio = Math.Max(MinSplitRatio, Math.Min(MaxSplitRatio, ratio));
            Set(AgentPanelSplitRatioKey, ratio.ToString("F3", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the global HUD split ratio (terminal height vs HUD height).
        /// Default 0.75 means terminal gets 75% of the height.
        /// </summary>
        public double GetHudSplitRatio()
        {
            string value = Get(HudSplitRatioKey);
            if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio))
            {
                return Math.Max(MinSplitRatio, Math.Min(MaxHudSplitRatio, ratio));
            }
            return DefaultHudSplitRatio;
        }

        /// <summary>
        /// Sets the global HUD split ratio.
        /// </summary>
        public void SetHudSplitRatio(double ratio)
        {
            ratio = Math.Max(MinSplitRatio, Math.Min(MaxHudSplitRatio, ratio));
            Set(HudSplitRatioKey, ratio.ToString("F3", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Gets the saved status bar height in pixels.
        /// </summary>
        public int GetStatusBarHeight()
        {
            string value = Get(StatusBarHeightKey);
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int height))
            {
                return Math.Max(MinStatusBarHeight, Math.Min(MaxStatusBarHeight, height));
            }
            return DefaultStatusBarHeight;
        }

        /// <summary>
        /// Sets the status bar height in pixels.
        /// </summary>
        public void SetStatusBarHeight(int height)
        {
            height = Math.Max(MinStatusBarHeight, Math.Min(MaxStatusBarHeight, height));
            Set(StatusBarHeightKey, height.ToString());
        }

        /// <summary>
        /// Gets the list of configured Claude commands.
        /// </summary>
        public List<ClaudeCommand> GetClaudeCommands()
        {
            var result = new List<ClaudeCommand>();
            string data = Get(ClaudeCommandsKey);

            if (string.IsNullOrEmpty(data))
            {
                // Return default commands if none configured
                result.Add(new ClaudeCommand("claude", true));
                result.Add(new ClaudeCommand("claude -c", false));
                result.Add(new ClaudeCommand("claude --dangerously-skip-permissions", false));
                return result;
            }

            try
            {
                foreach (string entry in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split('|');
                    if (parts.Length >= 2)
                    {
                        result.Add(new ClaudeCommand(parts[0], parts[1] == "1"));
                    }
                }
            }
            catch
            {
                // Return defaults on parse error
                result.Add(new ClaudeCommand("claude", true));
            }

            return result;
        }

        /// <summary>
        /// Sets the list of configured Claude commands.
        /// </summary>
        public void SetClaudeCommands(List<ClaudeCommand> commands)
        {
            if (commands == null || commands.Count == 0)
            {
                Remove(ClaudeCommandsKey);
                return;
            }

            var entries = new List<string>();
            foreach (var cmd in commands)
            {
                entries.Add($"{cmd.Command}|{(cmd.IsDefault ? "1" : "0")}");
            }
            Set(ClaudeCommandsKey, string.Join(";", entries));
        }

        /// <summary>
        /// Gets the default Claude command, or null if none set.
        /// </summary>
        public ClaudeCommand GetDefaultClaudeCommand()
        {
            var commands = GetClaudeCommands();
            return commands.Find(c => c.IsDefault);
        }

        /// <summary>
        /// Gets the saved window bounds, or null if not saved.
        /// </summary>
        public Rectangle? GetWindowBounds()
        {
            string left = Get(WindowLeftKey);
            string top = Get(WindowTopKey);
            string width = Get(WindowWidthKey);
            string height = Get(WindowHeightKey);

            if (int.TryParse(left, out int l) && int.TryParse(top, out int t) &&
                int.TryParse(width, out int w) && int.TryParse(height, out int h))
            {
                return new Rectangle(l, t, w, h);
            }
            return null;
        }

        /// <summary>
        /// Saves the window bounds.
        /// </summary>
        public void SetWindowBounds(Rectangle bounds)
        {
            Set(WindowLeftKey, bounds.Left.ToString());
            Set(WindowTopKey, bounds.Top.ToString());
            Set(WindowWidthKey, bounds.Width.ToString());
            Set(WindowHeightKey, bounds.Height.ToString());
        }

        /// <summary>
        /// Gets the saved window state.
        /// </summary>
        public FormWindowState GetWindowState()
        {
            string state = Get(WindowStateKey);
            if (state == "Maximized") return FormWindowState.Maximized;
            if (state == "Minimized") return FormWindowState.Normal; // Don't restore minimized
            return FormWindowState.Normal;
        }

        /// <summary>
        /// Saves the window state.
        /// </summary>
        public void SetWindowState(FormWindowState state)
        {
            Set(WindowStateKey, state.ToString());
        }

        /// <summary>
        /// Gets the saved lifecycle board window bounds, or null if not saved.
        /// </summary>
        public Rectangle? GetLifecycleBoardBounds()
        {
            string left = Get(LifecycleBoardLeftKey);
            string top = Get(LifecycleBoardTopKey);
            string width = Get(LifecycleBoardWidthKey);
            string height = Get(LifecycleBoardHeightKey);

            if (int.TryParse(left, out int l) && int.TryParse(top, out int t) &&
                int.TryParse(width, out int w) && int.TryParse(height, out int h))
            {
                return new Rectangle(l, t, w, h);
            }
            return null;
        }

        /// <summary>
        /// Saves the lifecycle board window bounds.
        /// </summary>
        public void SetLifecycleBoardBounds(Rectangle bounds)
        {
            BeginBatch();
            Set(LifecycleBoardLeftKey, bounds.Left.ToString());
            Set(LifecycleBoardTopKey, bounds.Top.ToString());
            Set(LifecycleBoardWidthKey, bounds.Width.ToString());
            Set(LifecycleBoardHeightKey, bounds.Height.ToString());
            EndBatch();
        }

        /// <summary>
        /// Gets the path to the dock panel layout file.
        /// </summary>
        public string GetLayoutFilePath()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiTerminal");
            return Path.Combine(folder, "layout.xml");
        }

        // Session persistence settings
        private const string SessionTerminalsKey = "SessionTerminals";
        private const string SessionLayoutKey = "SessionLayout";

        /// <summary>
        /// Gets the saved terminal sessions from the last run.
        /// Format: "dir1|size1|title1;dir2|size2|title2;..."
        /// </summary>
        public List<TerminalSessionInfo> GetSessionTerminals()
        {
            string data = Get(SessionTerminalsKey);
            if (string.IsNullOrEmpty(data))
                return null;

            var result = new List<TerminalSessionInfo>();
            try
            {
                foreach (string entry in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split('|');
                    if (parts.Length >= 2 &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float size))
                    {
                        var info = new TerminalSessionInfo
                        {
                            WorkingDirectory = parts[0],
                            FontSize = size
                        };
                        // Custom title is optional (third part)
                        if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
                        {
                            info.CustomTitle = parts[2];
                        }
                        result.Add(info);
                    }
                }
            }
            catch
            {
                return null;
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Saves the terminal sessions for the next run.
        /// Format: "dir1|size1|title1;dir2|size2|title2;..."
        /// </summary>
        public void SetSessionTerminals(List<TerminalSessionInfo> terminals)
        {
            if (terminals == null || terminals.Count == 0)
            {
                Remove(SessionTerminalsKey);
                return;
            }

            var entries = new List<string>();
            foreach (var t in terminals)
            {
                string dir = t.WorkingDirectory ?? "";
                string size = t.FontSize.ToString("F1", CultureInfo.InvariantCulture);
                string title = t.CustomTitle ?? "";
                entries.Add($"{dir}|{size}|{title}");
            }
            Set(SessionTerminalsKey, string.Join(";", entries));
        }

        /// <summary>
        /// Gets the saved session layout preset name.
        /// </summary>
        public string GetSessionLayout()
        {
            return Get(SessionLayoutKey);
        }

        /// <summary>
        /// Saves the session layout preset name.
        /// </summary>
        public void SetSessionLayout(string layoutPreset)
        {
            if (string.IsNullOrEmpty(layoutPreset))
                Remove(SessionLayoutKey);
            else
                Set(SessionLayoutKey, layoutPreset);
        }

        /// <summary>
        /// Gets the default working directory for "Just Claude" sessions.
        /// Falls back to user profile directory if not set or path doesn't exist.
        /// </summary>
        public string GetDefaultWorkingDirectory()
        {
            string path = Get(DefaultWorkingDirectoryKey);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
            return null;
        }

        /// <summary>
        /// Sets the default working directory for "Just Claude" sessions.
        /// </summary>
        public void SetDefaultWorkingDirectory(string path)
        {
            Set(DefaultWorkingDirectoryKey, path ?? "");
        }

        /// <summary>
        /// Gets the last working directory.
        /// </summary>
        public string GetLastDirectory()
        {
            string path = Get(LastDirectoryKey);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
            return null;
        }

        /// <summary>
        /// Sets the last working directory.
        /// </summary>
        public void SetLastDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Set(LastDirectoryKey, path);
                AddRecentDirectory(path);
            }
        }

        /// <summary>
        /// Gets the list of recent directories.
        /// </summary>
        public List<string> GetRecentDirectories()
        {
            var result = new List<string>();
            string stored = Get(RecentDirectoriesKey);
            if (string.IsNullOrEmpty(stored))
                return result;

            foreach (string path in stored.Split('|'))
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    result.Add(path);
                    if (result.Count >= MaxRecentDirectories)
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// Adds a directory to the recent list (deduplicates and limits to max).
        /// </summary>
        public void AddRecentDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            var recent = GetRecentDirectories();

            // Remove if already exists (will be re-added at front)
            recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

            // Add to front
            recent.Insert(0, path);

            // Limit to max
            if (recent.Count > MaxRecentDirectories)
                recent.RemoveRange(MaxRecentDirectories, recent.Count - MaxRecentDirectories);

            // Save
            Set(RecentDirectoriesKey, string.Join("|", recent));
        }

        private void Load()
        {
            _settings.Clear();
            if (!File.Exists(_settingsPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(_settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        _settings[key] = value;
                    }
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                // Ensure folder exists (defensive - may have been deleted)
                string folder = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                var sb = new StringBuilder();
                sb.AppendLine("# MultiTerminal Settings");
                sb.AppendLine($"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                foreach (var kv in _settings)
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                File.WriteAllText(_settingsPath, sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to save settings: {ex.Message}");
            }
        }
    }
}
