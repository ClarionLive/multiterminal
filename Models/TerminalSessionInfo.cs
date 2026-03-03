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
    }
}
