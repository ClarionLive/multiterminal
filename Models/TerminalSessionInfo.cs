namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents the saved state of a terminal for session persistence.
    /// </summary>
    public class TerminalSessionInfo
    {
        /// <summary>
        /// The working directory the terminal was in when saved.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The font size of the terminal.
        /// </summary>
        public float FontSize { get; set; }

        /// <summary>
        /// Custom user-defined tab title (optional).
        /// </summary>
        public string CustomTitle { get; set; }

        /// <summary>
        /// Zoom level of the agent panel WebView2 for this terminal session (default 1.0).
        /// </summary>
        public double AgentPanelZoom { get; set; } = 1.0;

        /// <summary>
        /// Zoom level of the task HUD WebView2 for this terminal session (default 1.0).
        /// </summary>
        public double TaskHudZoom { get; set; } = 1.0;
    }
}
